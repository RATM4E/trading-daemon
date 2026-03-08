"""
CryptoNinjas Trading → OKX Signal Copier
=========================================
Читает сигналы из @cryptoninjas_trading_ann и исполняет на OKX (hedge mode, cross margin).

Логика:
  NEW_SIGNAL
    → Entry market  → market ордер (full risk или 0.5× если RISK ORDER)
    → Entry limit   → limit ордер (добор, та же сумма риска)
    → После заполнения entry market → Entire-Position SL + TP1 limit

  UPDATE: "SYMBOL move sl to entry"
    → Амендим algo SL ордер на цену entry market

  Всё остальное (close 30%, set TP4, ...) — игнорируем.

.env:
    TG_API_ID, TG_API_HASH
    TG_SESSION=cryptonus_session        # имя session файла
    OKX_API_KEY, OKX_SECRET, OKX_PASSPHRASE
    OKX_SUBACCOUNT=SubAccName           # опционально
    OKX_TESTNET=false
    RISK_USDT=25                        # риск на сделку
    POLL_INTERVAL=15
"""

import asyncio
import json
import logging
import os
import re
import time
from dataclasses import dataclass, field
from pathlib import Path
from typing import Optional

import aiohttp
import ccxt.async_support as ccxt
from dotenv import load_dotenv
from telethon import TelegramClient, events

load_dotenv()

# ---------------------------------------------------------------------------
# Конфиг
# ---------------------------------------------------------------------------

TG_API_ID       = int(os.environ["TG_API_ID"])
TG_API_HASH     = os.environ["TG_API_HASH"]
TG_SESSION      = os.environ.get("TG_SESSION", "cryptonus_session")
CHANNEL         = "cryptoninjas_trading_ann"

OKX_API_KEY     = os.environ.get("OKX_API_KEY", "")
OKX_SECRET      = os.environ.get("OKX_SECRET", "")
OKX_PASSPHRASE  = os.environ.get("OKX_PASSPHRASE", "")
OKX_SUBACCOUNT  = os.environ.get("OKX_SUBACCOUNT", "")
OKX_TESTNET     = os.environ.get("OKX_TESTNET", "false").lower() == "true"

RISK_USDT       = float(os.environ.get("RISK_USDT", "25"))
POLL_INTERVAL   = int(os.environ.get("POLL_INTERVAL", "15"))
SYMBOL_CACHE_TTL = 7 * 24 * 3600
STATE_FILE      = "ninjas_positions.json"
CACHE_FILE      = "okx_symbols_ninjas.json"

# ---------------------------------------------------------------------------
# Логгер
# ---------------------------------------------------------------------------

logging.basicConfig(
    level=logging.INFO,
    format="%(asctime)s [%(levelname)s] %(message)s",
    handlers=[
        logging.StreamHandler(),
        logging.FileHandler("ninjas_copier.log", encoding="utf-8"),
    ],
)
log = logging.getLogger("ninjas")

# ---------------------------------------------------------------------------
# Модели
# ---------------------------------------------------------------------------

@dataclass
class Signal:
    symbol:    str       # BTCUSDT-style (без $)
    direction: str       # LONG / SHORT
    entry_mkt: float     # Entry market
    entry_lim: Optional[float]  # Entry limit (добор)
    sl:        float
    tp1:       Optional[float]
    risk_note: str       # "" / "RISK ORDER"

    @property
    def okx_symbol(self) -> str:
        base = self.symbol.replace("USDT", "")
        return f"{base}/USDT:USDT"

    @property
    def sl_dist(self) -> float:
        return abs(self.entry_mkt - self.sl)

    @property
    def risk_factor(self) -> float:
        return 0.5 if self.risk_note else 1.0


@dataclass
class PositionState:
    signal:          Signal
    mkt_order_id:    Optional[str] = None   # entry market ордер
    lim_order_id:    Optional[str] = None   # entry limit ордер
    sl_algo_id:      Optional[str] = None   # SL algo ордер
    tp_order_id:     Optional[str] = None   # TP1 limit ордер
    mkt_filled:      bool          = False
    sl_moved_to_be:  bool          = False


# ---------------------------------------------------------------------------
# State persistence
# ---------------------------------------------------------------------------

def save_state(positions: dict[str, PositionState]):
    data = {}
    for sym, ps in positions.items():
        sig = ps.signal
        data[sym] = {
            "signal": {
                "symbol": sig.symbol, "direction": sig.direction,
                "entry_mkt": sig.entry_mkt, "entry_lim": sig.entry_lim,
                "sl": sig.sl, "tp1": sig.tp1, "risk_note": sig.risk_note,
            },
            "mkt_order_id":   ps.mkt_order_id,
            "lim_order_id":   ps.lim_order_id,
            "sl_algo_id":     ps.sl_algo_id,
            "tp_order_id":    ps.tp_order_id,
            "mkt_filled":     ps.mkt_filled,
            "sl_moved_to_be": ps.sl_moved_to_be,
        }
    Path(STATE_FILE).write_text(json.dumps(data, indent=2))


def load_state() -> dict[str, PositionState]:
    if not Path(STATE_FILE).exists():
        return {}
    try:
        data = json.loads(Path(STATE_FILE).read_text())
        result = {}
        for sym, d in data.items():
            s = d["signal"]
            sig = Signal(
                symbol=s["symbol"], direction=s["direction"],
                entry_mkt=s["entry_mkt"], entry_lim=s["entry_lim"],
                sl=s["sl"], tp1=s["tp1"], risk_note=s["risk_note"],
            )
            result[sym] = PositionState(
                signal=sig,
                mkt_order_id=d.get("mkt_order_id"),
                lim_order_id=d.get("lim_order_id"),
                sl_algo_id=d.get("sl_algo_id"),
                tp_order_id=d.get("tp_order_id"),
                mkt_filled=d.get("mkt_filled", False),
                sl_moved_to_be=d.get("sl_moved_to_be", False),
            )
        log.info(f"State loaded: {len(result)} positions")
        return result
    except Exception as e:
        log.error(f"load_state error: {e}")
        return {}

# ---------------------------------------------------------------------------
# Парсер сигналов
# ---------------------------------------------------------------------------

def parse_price(s: str) -> Optional[float]:
    s = s.replace(",", "").strip()
    try:
        return float(s)
    except ValueError:
        return None


def parse_signal(text: str) -> Optional[Signal]:
    first = text.split("\n")[0]

    # Требуем 🟢/🔴 LONG/SHORT
    m = re.match(r'[🟢🔴]\s*(LONG|SHORT)\b', first, re.I)
    if not m:
        return None

    direction = m.group(1).upper()

    # Символ $XXX
    sym_m = re.search(r'\$([A-Z0-9]+)', first, re.I)
    if not sym_m:
        return None
    symbol = sym_m.group(1).upper()

    risk_note = "RISK ORDER" if "RISK ORDER" in first.upper() else ""

    lines = text.split("\n")
    entry_mkt = None
    entry_lim = None
    sl        = None
    tp1       = None

    for line in lines:
        l = line.strip().lstrip("-").strip()

        # Entry market / Entry (now) / Entry: — первый найденный
        if entry_mkt is None:
            for pat in [
                r'Entry\s+market(?:\s*\(now\))?:\s*([\d.,]+)',
                r'Entry\s*\(now\)\s*:\s*([\d.,]+)',
                r'Entry\s+1\s*:\s*([\d.,]+)',
                r'Entry\s+limit\s+1\s*:\s*([\d.,]+)',  # LONG LIMIT формат
                r'Entry\s*:\s*([\d.,]+)',
            ]:
                mm = re.match(pat, l, re.I)
                if mm:
                    entry_mkt = parse_price(mm.group(1))
                    break

        # Entry limit / Entry 2 / Entry limit 2
        if entry_lim is None:
            for pat in [
                r'Entry\s+limit(?:\s+2)?\s*:\s*([\d.,]+)',
                r'Entry\s+2\s*:\s*([\d.,]+)',
                r'Limit\s+entry\s*[:–-]\s*([\d.,]+)',
                r'Entry\s+limit\s+2\s*:\s*([\d.,]+)',
            ]:
                mm = re.match(pat, l, re.I)
                if mm:
                    entry_lim = parse_price(mm.group(1))
                    break

        # SL
        if sl is None:
            for pat in [
                r'(?:SL|Stop\s*Loss)\s*:\s*([\d.,]+)',
                r'❌\s*SL\s*:?\s*([\d.,]+)',
            ]:
                mm = re.match(pat, l, re.I)
                if mm:
                    sl = parse_price(mm.group(1))
                    break

        # TP1
        if tp1 is None:
            mm = re.match(r'(?:🎯\s*)?TP\s*1\s*:\s*([\d.,]+)', l, re.I)
            if mm:
                tp1 = parse_price(mm.group(1))

    if entry_mkt is None or sl is None:
        return None
    if entry_mkt == sl:
        return None

    return Signal(
        symbol=symbol, direction=direction,
        entry_mkt=entry_mkt, entry_lim=entry_lim,
        sl=sl, tp1=tp1, risk_note=risk_note,
    )


def is_move_sl_to_entry(text: str) -> Optional[str]:
    """
    Возвращает символ если сообщение — 'SYMBOL move sl to entry'.
    """
    m = re.match(r'^([A-Z0-9]{2,20})\b.*move\s+sl\s+to\s+entry', text.strip(), re.I)
    return m.group(1).upper() if m else None


# ---------------------------------------------------------------------------
# OKX Executor
# ---------------------------------------------------------------------------

class NinjasOKX:

    def __init__(self):
        self.positions: dict[str, PositionState] = load_state()
        connector = aiohttp.TCPConnector(resolver=aiohttp.ThreadedResolver(), ssl=False)
        self._session = aiohttp.ClientSession(connector=connector)
        params = {
            "apiKey":   OKX_API_KEY,
            "secret":   OKX_SECRET,
            "password": OKX_PASSPHRASE,
            "options":  {"defaultType": "swap"},
            "session":  self._session,
        }
        if OKX_SUBACCOUNT:
            params["headers"] = {"OK-ACCESS-SUBACCOUNT": OKX_SUBACCOUNT}
        if OKX_TESTNET:
            params["sandbox"] = True
            log.warning("⚠️  OKX TESTNET MODE")
        self.ex = ccxt.okx(params)
        self._symbols: set[str] = set()
        self._markets: dict = {}

    async def init(self):
        await self._load_symbols()
        asyncio.create_task(self._refresh_loop())
        log.info("NinjasOKX ready")

    async def close(self):
        await self.ex.close()
        if not self._session.closed:
            await self._session.close()

    # --- Symbol cache ---

    async def _load_symbols(self):
        path = Path(CACHE_FILE)
        if path.exists():
            data = json.loads(path.read_text())
            if time.time() - data.get("updated_at", 0) < SYMBOL_CACHE_TTL:
                self._symbols = set(data["symbols"])
                log.info(f"OKX SymbolCache: {len(self._symbols)} (from file)")
                return
        await self._refresh_symbols()

    async def _refresh_symbols(self):
        self._markets = await self.ex.load_markets()
        self._symbols = {
            s for s, i in self._markets.items()
            if i.get("type") == "swap" and i.get("quote") == "USDT" and i.get("active")
        }
        Path(CACHE_FILE).write_text(
            json.dumps({"updated_at": time.time(), "symbols": list(self._symbols)}))
        log.info(f"OKX SymbolCache: refreshed {len(self._symbols)}")

    async def _refresh_loop(self):
        while True:
            await asyncio.sleep(SYMBOL_CACHE_TTL)
            await self._refresh_symbols()

    def symbol_available(self, signal: Signal) -> bool:
        return signal.okx_symbol in self._symbols

    # --- Helpers ---

    def _bp(self, direction: str) -> dict:
        return {"tdMode": "cross", "posSide": "long" if direction == "LONG" else "short"}

    def _inst_id(self, okx_sym: str) -> str:
        return okx_sym.replace("/", "-").replace(":USDT", "-SWAP")

    def _ts(self) -> str:
        return str(int(time.time() * 1000))[-10:]  # 10 цифр

    # --- Leverage ---

    async def _max_leverage(self, sym: str) -> int:
        if not self._markets:
            self._markets = await self.ex.load_markets()
        info = self._markets.get(sym, {})
        try:
            return int(info.get("limits", {}).get("leverage", {}).get("max", 20) or 20)
        except Exception:
            return 20

    async def _set_leverage(self, sym: str, direction: str):
        max_lev = await self._max_leverage(sym)
        side = "long" if direction == "LONG" else "short"
        lev = max_lev
        while lev >= 1:
            try:
                await self.ex.set_leverage(lev, sym, params={"mgnMode": "cross", "posSide": side})
                log.info(f"Leverage: {lev}x on {sym}")
                return
            except Exception as e:
                if "59102" in str(e) or "exceeds" in str(e).lower():
                    lev -= 1
                else:
                    log.warning(f"set_leverage: {e}")
                    return

    # --- Sizing ---

    async def _calc_lot(self, signal: Signal, risk_usdt: float) -> float:
        if not self._markets:
            self._markets = await self.ex.load_markets()
        info = self._markets.get(signal.okx_symbol)
        if not info:
            raise ValueError(f"Symbol not found: {signal.okx_symbol}")
        if signal.sl_dist == 0:
            raise ValueError("SL distance is zero")
        contract_size = float(info.get("contractSize", 1))
        pnl_per = signal.sl_dist * contract_size
        total = risk_usdt / pnl_per
        min_amt = float(info.get("limits", {}).get("amount", {}).get("min", 1) or 1)
        lot = max(round(total), int(min_amt))
        log.info(f"Sizing: sl_dist={signal.sl_dist:.6f}  risk={risk_usdt}  lot={lot}")
        return float(lot)

    # --- Orders ---

    async def _place_market(self, sym: str, side: str, qty: float,
                             direction: str, client_id: str = "") -> str:
        params = {**self._bp(direction), "clOrdId": client_id or self._ts()}
        o = await self.ex.create_order(sym, "market", side, qty, params=params)
        return o["id"]

    async def _place_limit(self, sym: str, side: str, qty: float, price: float,
                            direction: str, client_id: str = "") -> str:
        params = {**self._bp(direction), "clOrdId": client_id or self._ts()}
        o = await self.ex.create_order(sym, "limit", side, qty, price, params=params)
        return o["id"]

    async def _place_sl_algo(self, sym: str, direction: str, sl_price: float) -> str:
        """Entire-Position SL через algo endpoint."""
        side = "sell" if direction == "LONG" else "buy"
        pos_side = "long" if direction == "LONG" else "short"
        o = await self.ex.private_post_trade_order_algo({
            "instId":       self._inst_id(sym),
            "tdMode":       "cross",
            "side":         side,
            "posSide":      pos_side,
            "ordType":      "conditional",
            "slTriggerPx":  str(sl_price),
            "slOrdPx":      "-1",
            "closeFraction": "1",
        })
        algo_id = o["data"][0]["algoId"]
        log.info(f"SL algo placed: algoId={algo_id}  triggerPx={sl_price}")
        return algo_id

    async def _amend_sl_algo(self, sym: str, algo_id: str, new_sl: float) -> str:
        """
        Амендим SL algo ордер на новую цену.
        OKX поддерживает amend через /api/v5/trade/amend-algos.
        При ошибке — cancel + replace.
        """
        try:
            o = await self.ex.private_post_trade_amend_algos({
                "algoId": algo_id,
                "instId": self._inst_id(sym),
                "newSlTriggerPx": str(new_sl),
                "newSlOrdPx": "-1",
            })
            new_id = o["data"][0].get("algoId", algo_id)
            log.info(f"SL amended: algoId={new_id}  newSL={new_sl}")
            return new_id
        except Exception as e:
            log.warning(f"amend_algos failed ({e}), cancel+replace")
            # Cancel old
            try:
                await self.ex.private_post_trade_cancel_algos({
                    "algoId": algo_id,
                    "instId": self._inst_id(sym),
                })
            except Exception as ce:
                log.warning(f"cancel algo: {ce}")
            # Get direction from active positions
            ps = self.positions.get(sym.split("/")[0].replace("USDT", ""))
            direction = ps.signal.direction if ps else "LONG"
            return await self._place_sl_algo(sym, direction, new_sl)

    async def _place_tp_limit(self, sym: str, direction: str, qty: float,
                               tp_price: float, client_id: str = "") -> str:
        """TP1 как обычный reduce-only limit ордер."""
        side = "sell" if direction == "LONG" else "buy"
        params = {
            **self._bp(direction),
            "clOrdId": client_id or self._ts(),
            "reduceOnly": True,
        }
        o = await self.ex.create_order(sym, "limit", side, qty, tp_price, params=params)
        return o["id"]

    async def _cancel_order(self, sym: str, order_id: str):
        try:
            await self.ex.cancel_order(order_id, sym)
        except Exception:
            try:
                await self.ex.private_post_trade_cancel_algos({
                    "algoId": order_id, "instId": self._inst_id(sym)
                })
            except Exception as e:
                log.warning(f"cancel_order {order_id}: {e}")

    async def _cancel_all(self, sym: str, skip: set = None):
        skip = skip or set()
        ps = self.positions.get(sym)
        if not ps:
            return
        for oid in [ps.mkt_order_id, ps.lim_order_id, ps.sl_algo_id, ps.tp_order_id]:
            if oid and oid not in skip:
                await self._cancel_order(ps.signal.okx_symbol, oid)

    # --- Main flow ---

    async def on_signal(self, signal: Signal):
        sym = signal.symbol
        if sym in self.positions:
            log.warning(f"SKIP [{sym}]: уже в позиции")
            return
        if not self.symbol_available(signal):
            log.warning(f"SKIP [{sym}]: символ не найден на OKX")
            return

        log.info(f"NEW [{sym}] {signal.direction}  entry={signal.entry_mkt}  sl={signal.sl}  "
                 f"tp1={signal.tp1}  risk_note={signal.risk_note or '-'}")

        okx_sym   = signal.okx_symbol
        risk_usdt = RISK_USDT * signal.risk_factor
        side      = "buy" if signal.direction == "LONG" else "sell"

        await self._set_leverage(okx_sym, signal.direction)

        try:
            lot = await self._calc_lot(signal, risk_usdt)
        except Exception as e:
            log.error(f"[{sym}] calc_lot failed: {e}")
            return

        ps = PositionState(signal=signal)
        self.positions[sym] = ps
        save_state(self.positions)

        # Entry market
        try:
            mkt_id = await self._place_market(
                okx_sym, side, lot, signal.direction,
                client_id=f"1{self._ts()}")
            ps.mkt_order_id = mkt_id
            log.info(f"[{sym}] Entry market placed: {mkt_id}  lot={lot}")
        except Exception as e:
            log.error(f"[{sym}] market order failed: {e}")
            del self.positions[sym]
            save_state(self.positions)
            return

        # Entry limit (добор)
        if signal.entry_lim:
            try:
                lim_id = await self._place_limit(
                    okx_sym, side, lot, signal.entry_lim, signal.direction,
                    client_id=f"2{self._ts()}")
                ps.lim_order_id = lim_id
                log.info(f"[{sym}] Entry limit placed: {lim_id}  price={signal.entry_lim}")
            except Exception as e:
                log.warning(f"[{sym}] limit order failed: {e}")

        save_state(self.positions)

        # SL + TP ставим асинхронно — рыночный ордер обычно мгновенно
        await asyncio.sleep(2)
        await self._place_sl_tp(sym)

    async def _place_sl_tp(self, sym: str):
        """Ставит SL algo + TP1 limit. Вызывается после подтверждения market fill."""
        ps = self.positions.get(sym)
        if not ps or ps.sl_algo_id:
            return

        signal  = ps.signal
        okx_sym = signal.okx_symbol

        # SL
        try:
            algo_id = await self._place_sl_algo(okx_sym, signal.direction, signal.sl)
            ps.sl_algo_id = algo_id
            log.info(f"[{sym}] SL placed: {algo_id}  price={signal.sl}")
        except Exception as e:
            log.error(f"[{sym}] SL failed: {e}")

        # TP1
        if signal.tp1 and not ps.tp_order_id:
            try:
                # Для TP нужно знать реальный размер позиции
                pos_size = await self._fetch_position_size(okx_sym, signal.direction)
                if pos_size > 0:
                    tp_id = await self._place_tp_limit(
                        okx_sym, signal.direction, pos_size, signal.tp1,
                        client_id=f"3{self._ts()}")
                    ps.tp_order_id = tp_id
                    log.info(f"[{sym}] TP1 placed: {tp_id}  price={signal.tp1}")
            except Exception as e:
                log.error(f"[{sym}] TP1 failed: {e}")

        ps.mkt_filled = True
        save_state(self.positions)

    async def _fetch_position_size(self, sym: str, direction: str) -> float:
        try:
            positions = await self.ex.fetch_positions([sym])
            pos_side = "long" if direction == "LONG" else "short"
            for p in positions:
                if p.get("symbol") == sym and p.get("side", "").lower() == pos_side:
                    return float(p.get("contracts", 0))
        except Exception as e:
            log.warning(f"fetch_position_size {sym}: {e}")
        return 0.0

    async def on_move_sl_to_entry(self, sym: str):
        """Обрабатывает 'SYMBOL move sl to entry'."""
        ps = self.positions.get(sym)
        if not ps:
            log.warning(f"[{sym}] move SL: позиция не найдена")
            return
        if ps.sl_moved_to_be:
            return
        if not ps.sl_algo_id:
            log.warning(f"[{sym}] move SL: нет SL ордера")
            return

        new_sl = ps.signal.entry_mkt
        log.info(f"[{sym}] Move SL to entry: {ps.signal.sl} → {new_sl}")

        try:
            new_algo_id = await self._amend_sl_algo(ps.signal.okx_symbol, ps.sl_algo_id, new_sl)
            ps.sl_algo_id = new_algo_id
            ps.sl_moved_to_be = True
            save_state(self.positions)
        except Exception as e:
            log.error(f"[{sym}] move SL failed: {e}")

    async def on_closed(self, sym: str, reason: str):
        """Позиция закрыта — отменяем все ордера и чистим state."""
        log.info(f"[{sym}] closed: {reason}")
        if sym not in self.positions:
            return
        await self._cancel_all(sym)
        del self.positions[sym]
        save_state(self.positions)

    # --- Polling ---

    async def _poll(self):
        """Проверяет статус открытых позиций."""
        while True:
            await asyncio.sleep(POLL_INTERVAL)
            for sym in list(self.positions.keys()):
                ps = self.positions.get(sym)
                if not ps:
                    continue
                try:
                    await self._check_position(sym, ps)
                except Exception as e:
                    log.warning(f"[{sym}] poll error: {e}")

    async def _check_position(self, sym: str, ps: PositionState):
        okx_sym   = ps.signal.okx_symbol
        direction = ps.signal.direction

        # Если SL ещё не поставлен — попробуем ещё раз
        if not ps.sl_algo_id:
            await self._place_sl_tp(sym)
            return

        # Проверяем реальную позицию на бирже
        pos_size = await self._fetch_position_size(okx_sym, direction)

        if pos_size == 0:
            # Позиция закрыта — SL или TP сработал
            log.info(f"[{sym}] Позиция закрыта на бирже")
            await self.on_closed(sym, "POSITION_CLOSED")
            return

        # Проверяем статус SL algo
        try:
            r = await self.ex.private_get_trade_order_algo({
                "algoId": ps.sl_algo_id,
                "instId": self._inst_id(okx_sym),
            })
            state = r["data"][0].get("state", "live")
            if state in ("effective", "canceled"):
                log.info(f"[{sym}] SL algo state={state}")
                if state == "effective":
                    await self.on_closed(sym, "SL_HIT")
        except Exception as e:
            log.warning(f"[{sym}] check SL algo: {e}")

        # Проверяем TP1
        if ps.tp_order_id:
            try:
                o = await self.ex.fetch_order(ps.tp_order_id, okx_sym)
                if o.get("status") == "closed":
                    log.info(f"[{sym}] TP1 исполнен")
                    await self.on_closed(sym, "TP1_HIT")
            except Exception as e:
                log.warning(f"[{sym}] check TP: {e}")


# ---------------------------------------------------------------------------
# Telegram listener
# ---------------------------------------------------------------------------

async def run(executor: NinjasOKX):
    client = TelegramClient(TG_SESSION, TG_API_ID, TG_API_HASH)
    await client.start()

    channel_entity = await client.get_entity(CHANNEL)
    log.info(f"Listening: {CHANNEL}")

    @client.on(events.NewMessage(chats=channel_entity))
    async def handler(event):
        text = event.raw_text or ""
        if not text:
            return

        log.debug(f"MSG: {text[:80]}")

        # Новый сигнал
        sig = parse_signal(text)
        if sig:
            await executor.on_signal(sig)
            return

        # "SYMBOL move sl to entry"
        sym = is_move_sl_to_entry(text)
        if sym:
            await executor.on_move_sl_to_entry(sym)
            return

    # Фоновый polling
    asyncio.create_task(executor._poll())

    log.info("Running... Ctrl+C to stop")
    await client.run_until_disconnected()


async def main():
    for key in ["OKX_API_KEY", "OKX_SECRET", "OKX_PASSPHRASE"]:
        if not os.environ.get(key):
            raise SystemExit(f"❌ {key} не задан в .env")

    executor = NinjasOKX()
    await executor.init()

    try:
        await run(executor)
    finally:
        await executor.close()
        log.info("Stopped.")


if __name__ == "__main__":
    asyncio.run(main())
