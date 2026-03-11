"""
Cryptonus_Trade Signal Copier — универсальный (OKX / Binance Futures)
======================================================================
Читает сигналы из Telegram канала @Cryptonus_Trade, исполняет на бирже.

Выбор биржи: EXCHANGE=okx или EXCHANGE=binance в .env

Логика входа:
  1. NEW_SIGNAL        → ENTRY1 (1/3 лота) + SL на весь лот
  2. OPEN ENTRY2 + NEW TP → ENTRY2 (2/3 лота) + отмена старых TP + новые TP
  3. TP_HIT / SL_HIT  → чистим состояние позиции

Зависимости:
    pip install telethon ccxt python-dotenv
"""

import asyncio
import json
import logging
import os
import re
import time
from abc import ABC, abstractmethod
from dataclasses import dataclass, field
from pathlib import Path
from typing import Optional

import ccxt.async_support as ccxt
import aiohttp
from dotenv import load_dotenv
from telethon import TelegramClient, events

load_dotenv()

# ---------------------------------------------------------------------------
# Конфигурация
# ---------------------------------------------------------------------------

TG_API_ID        = int(os.environ.get("TG_API_ID", "0"))
TG_API_HASH      = os.environ.get("TG_API_HASH", "")
TG_SESSION_FILE  = "cryptonus_session"
SIGNAL_CHANNEL   = "Cryptonus_Trade"

EXCHANGE         = os.environ.get("EXCHANGE", "binance").lower()

OKX_API_KEY      = os.environ.get("OKX_API_KEY", "")
OKX_SECRET       = os.environ.get("OKX_SECRET", "")
OKX_PASSPHRASE   = os.environ.get("OKX_PASSPHRASE", "")
OKX_TESTNET      = os.environ.get("OKX_TESTNET", "true").lower() == "true"

BINANCE_API_KEY  = os.environ.get("BINANCE_API_KEY", "")
BINANCE_SECRET   = os.environ.get("BINANCE_SECRET", "")
BINANCE_BASE_URL = os.environ.get("BINANCE_BASE_URL", "")
BINANCE_TESTNET  = os.environ.get("BINANCE_TESTNET", "false").lower() == "true"

RISK_USDT        = float(os.environ.get("RISK_USDT", "10"))
MAX_LEVERAGE     = 20
EXIT_STRATEGY    = os.environ.get("EXIT_STRATEGY", "tp1").lower()  # tp1 | as_is

BAR_WINDOW_SEC   = 45
BAR_TF_SEC       = 300
SYMBOL_CACHE_TTL = 7 * 24 * 3600
TP_DRIFT         = 0.0005   # 0.05% — смещение TP внутрь рынка для гарантии заполнения
POLL_INTERVAL    = 15       # секунд между проверками статуса ордеров
MAX_DAILY_STOPS  = int(os.environ.get("MAX_DAILY_STOPS", "0"))  # 0 = выключено

# ---------------------------------------------------------------------------
# Валидация конфигурации
# ---------------------------------------------------------------------------

def _require(key: str) -> str:
    val = os.environ.get(key, "").strip()
    if not val:
        raise SystemExit(f"❌ Ошибка: переменная {key} не задана в .env")
    return val

def validate_config():
    _require("TG_API_ID")
    _require("TG_API_HASH")
    if EXCHANGE not in ("okx", "binance"):
        raise SystemExit(f"❌ Ошибка: EXCHANGE='{EXCHANGE}' — допустимо только 'okx' или 'binance'")
    if EXCHANGE == "okx":
        _require("OKX_API_KEY"); _require("OKX_SECRET"); _require("OKX_PASSPHRASE")
    if EXCHANGE == "binance":
        _require("BINANCE_API_KEY"); _require("BINANCE_SECRET")
    if EXIT_STRATEGY not in ("tp1", "as_is"):
        raise SystemExit(f"❌ Ошибка: EXIT_STRATEGY='{EXIT_STRATEGY}' — допустимо только 'tp1' или 'as_is'")
    if RISK_USDT <= 0:
        raise SystemExit("❌ Ошибка: RISK_USDT должен быть больше 0")
    print(f"✅ Конфиг OK: EXCHANGE={EXCHANGE.upper()}  EXIT={EXIT_STRATEGY.upper()}  RISK={RISK_USDT} USDT")

validate_config()

# ---------------------------------------------------------------------------
# Логгер
# ---------------------------------------------------------------------------

logging.basicConfig(
    level=logging.INFO,
    format="%(asctime)s [%(levelname)s] %(message)s",
    handlers=[
        logging.StreamHandler(),
        logging.FileHandler("signal_copier.log", encoding="utf-8"),
    ]
)
log = logging.getLogger("copier")

# ---------------------------------------------------------------------------
# Модели данных
# ---------------------------------------------------------------------------

STATE_FILE = "positions_state.json"

def save_state(positions: dict):
    """Сохранить открытые позиции на диск."""
    try:
        data = {}
        for sym, pos in positions.items():
            data[sym] = {
                "signal": {
                    "symbol": pos.signal.symbol,
                    "direction": pos.signal.direction,
                    "entry1": pos.signal.entry1,
                    "entry2": pos.signal.entry2,
                    "sl": pos.signal.sl,
                    "tp1": pos.signal.tp1,
                    "tp2": pos.signal.tp2,
                    "tp3": pos.signal.tp3,
                },
                "total_lot": pos.total_lot,
                "entry_order_ids": pos.entry_order_ids,
                "sl_order_id": pos.sl_order_id,
                "tp_order_ids": pos.tp_order_ids,
                "entry2_placed": pos.entry2_placed,
            }
        with open(STATE_FILE, 'w') as f:
            json.dump(data, f, indent=2)
    except Exception as e:
        log.warning(f"save_state: {e}")

def load_state() -> dict:
    """Загрузить позиции с диска при рестарте."""
    if not os.path.exists(STATE_FILE):
        return {}
    try:
        with open(STATE_FILE) as f:
            data = json.load(f)
        positions = {}
        for sym, d in data.items():
            s = d['signal']
            sig = Signal(
                symbol=s['symbol'], direction=s['direction'],
                entry1=s['entry1'], entry2=s.get('entry2'),
                sl=s['sl'], tp1=s['tp1'], tp2=s.get('tp2'), tp3=s.get('tp3'),
            )
            positions[sym] = PositionState(
                signal=sig,
                total_lot=d['total_lot'],
                entry_order_ids=d.get('entry_order_ids', []),
                sl_order_id=d.get('sl_order_id'),
                tp_order_ids=d.get('tp_order_ids', []),
                entry2_placed=d.get('entry2_placed', False),
            )
        log.info(f"Загружено {len(positions)} позиций из {STATE_FILE}")
        return positions
    except Exception as e:
        log.warning(f"load_state: {e}")
        return {}

class DailyStopGuard:
    """Считает стопы за UTC-день. При достижении лимита блокирует новые входы до следующего дня."""
    def __init__(self, max_stops: int):
        self.max_stops = max_stops
        self.count     = 0
        self._day      = self._today()

    def _today(self):
        from datetime import datetime, timezone
        return datetime.now(timezone.utc).date()

    def _check_day(self):
        today = self._today()
        if today != self._day:
            self._day  = today
            self.count = 0
            log.info("DailyStopGuard: новый день UTC — счётчик сброшен")

    def register_stop(self):
        self._check_day()
        self.count += 1
        log.warning(f"DailyStopGuard: стоп #{self.count} / {self.max_stops}")

    def is_blocked(self) -> bool:
        if self.max_stops == 0:
            return False
        self._check_day()
        if self.count >= self.max_stops:
            log.warning(f"DailyStopGuard: лимит {self.max_stops} стопов — торговля приостановлена до следующего дня UTC")
            return True
        return False

@dataclass
class Signal:
    symbol:    str
    tf:        str
    direction: str
    entry1:    float
    entry2:    float
    tp1:       float
    tp2:       float
    tp3:       float
    sl:        float
    leverage:  int
    raw:       str = field(repr=False, default="")

    @property
    def base(self) -> str:
        return self.symbol.replace("USDT", "")

    @property
    def okx_symbol(self) -> str:
        return f"{self.base}/USDT:USDT"

    @property
    def binance_symbol(self) -> str:
        return self.symbol

    @property
    def sl_dist(self) -> float:
        return abs(self.entry1 - self.sl)  # от entry1 — точка отсчёта риска


@dataclass
class PositionState:
    """Состояние открытой позиции по символу."""
    signal:         Signal
    total_lot:      float
    entry_order_ids: list = field(default_factory=list)  # [entry1_id, entry2_id]
    sl_order_id:    Optional[str] = None
    tp_order_ids:   list = field(default_factory=list)
    entry2_placed:  bool = False


# ---------------------------------------------------------------------------
# Парсеры
# ---------------------------------------------------------------------------

def parse_signal(text: str) -> Optional[Signal]:
    try:
        m = re.search(r"#(\w+USDT)\s+(\d+[mMhHdD])", text)
        if not m: return None
        symbol, tf = m.group(1), m.group(2)

        dir_m = re.search(r"STATUS\s*:\s*(LONG|SHORT)", text, re.IGNORECASE)
        if not dir_m: return None

        e1  = re.search(r"ENTRY1\s*[:\s]\s*([\d.]+)", text)
        e2  = re.search(r"ENTRY2\s*[:\s]\s*([\d.]+)", text)
        tp1 = re.search(r"TP1\s*[:\s]\s*([\d.]+)", text)
        tp2 = re.search(r"TP2\s*[:\s]\s*([\d.]+)", text)
        tp3 = re.search(r"TP3\s*[:\s]\s*([\d.]+)", text)
        sl_m = re.search(r"\bSL\s*[:\s]\s*([\d.]+)", text)
        if not all([e1, e2, tp1, tp2, tp3, sl_m]): return None

        lev_m = re.search(r"LEVERAGE\s*:\s*\w+\s*(\d+)x", text, re.IGNORECASE)
        leverage = int(lev_m.group(1)) if lev_m else 20

        return Signal(
            symbol=symbol, tf=tf,
            direction=dir_m.group(1).upper(),
            entry1=float(e1.group(1)), entry2=float(e2.group(1)),
            tp1=float(tp1.group(1)), tp2=float(tp2.group(1)), tp3=float(tp3.group(1)),
            sl=float(sl_m.group(1)),
            leverage=min(leverage, MAX_LEVERAGE),
            raw=text,
        )
    except Exception as e:
        log.warning(f"parse_signal error: {e}")
        return None


@dataclass
class Entry2Update:
    """Результат парсинга сообщения OPEN ENTRY2 + NEW TP."""
    symbol:    str
    entry2:    float
    tp1:       float
    tp2:       float
    tp3:       float


def parse_entry2_update(text: str) -> Optional[Entry2Update]:
    """
    Парсит сообщение вида:
        #CHRUSDT OPEN ENTRY2 0.0154
        NEW TP
        TP1 : 0.0162 ( 💲 2.00%)  TP2 : 0.0165 ...
    """
    try:
        sym_m = re.search(r"#(\w+USDT)", text)
        e2_m  = re.search(r"OPEN\s+ENTRY2\s+([\d.]+)", text, re.IGNORECASE)
        tp1   = re.search(r"TP1\s*[:\s]\s*([\d.]+)", text)
        tp2   = re.search(r"TP2\s*[:\s]\s*([\d.]+)", text)
        tp3   = re.search(r"TP3\s*[:\s]\s*([\d.]+)", text)
        if not all([sym_m, e2_m, tp1, tp2, tp3]): return None
        return Entry2Update(
            symbol=sym_m.group(1),
            entry2=float(e2_m.group(1)),
            tp1=float(tp1.group(1)),
            tp2=float(tp2.group(1)),
            tp3=float(tp3.group(1)),
        )
    except Exception as e:
        log.warning(f"parse_entry2_update error: {e}")
        return None

# ---------------------------------------------------------------------------
# Классификатор сообщений
# ---------------------------------------------------------------------------

def classify_message(text: str) -> str:
    if not text: return "EMPTY"
    t = text.upper()
    if "STATUS" in t and "ENTRY1" in t and "SL" in t:
        return "NEW_SIGNAL"
    if "OPEN ENTRY2" in t and "NEW TP" in t:
        return "ENTRY2_AND_NEW_TP"
    if "OPEN ENTRY2" in t:
        return "UPDATE_ENTRY"
    if "NEW TP" in t:
        return "UPDATE_TP"
    if re.search(r"TP\s*\d.*✅", text):
        return "TP_HIT"
    if re.search(r"\bSL\b.*[❌🔴⛔]", text) or re.search(r"\bSL\b.*стоп", text, re.IGNORECASE):
        return "SL_HIT"
    if re.search(r"[+\-]\d+\.?\d*%", text):
        return "RESULT"
    return "UNKNOWN"

# ---------------------------------------------------------------------------
# Timing gate
# ---------------------------------------------------------------------------

def is_bar_boundary() -> bool:
    offset = int(time.time()) % BAR_TF_SEC
    ok = offset < BAR_WINDOW_SEC
    log.info(f"TimingGate: offset={offset}s → {'PASS' if ok else 'SKIP'}")
    return ok

# ---------------------------------------------------------------------------
# Базовый исполнитель
# ---------------------------------------------------------------------------

class BaseExecutor(ABC):

    def __init__(self):
        self.positions: dict[str, PositionState] = {}
        self.stop_guard = DailyStopGuard(MAX_DAILY_STOPS)

    @abstractmethod
    async def init(self): ...

    @abstractmethod
    async def close(self): ...

    # --- абстрактные биржевые примитивы ---

    @abstractmethod
    async def _place_limit(self, sym: str, side: str, qty: float, price: float,
                           reduce_only: bool = False, client_id: str = "",
                           direction: str = "LONG") -> str:
        """Возвращает order_id."""
        ...

    @abstractmethod
    async def _place_stop_market(self, sym: str, side: str, qty: float,
                                 stop_price: float, client_id: str = "") -> str: ...

    @abstractmethod
    async def _cancel_order(self, sym: str, order_id: str): ...

    @abstractmethod
    async def _fetch_order_status(self, sym: str, order_id: str) -> str:
        """Возвращает статус: 'open' | 'closed' | 'canceled' | 'unknown'"""

    @abstractmethod
    async def _set_leverage(self, sym: str, leverage: int, direction: str): ...

    @abstractmethod
    async def _calc_lot(self, signal: Signal) -> float: ...

    @abstractmethod
    def exchange_symbol(self, signal: Signal) -> str: ...

    @abstractmethod
    def symbol_available(self, sig_symbol: str) -> bool: ...

    # --- TP-ордера в зависимости от стратегии ---

    def _apply_drift(self, price: float, direction: str, is_tp: bool) -> float:
        """Сдвигает TP внутрь рынка на TP_DRIFT для гарантии заполнения."""
        if not is_tp:
            return price
        if direction == "LONG":
            return round(price * (1 - TP_DRIFT), 8)
        else:
            return round(price * (1 + TP_DRIFT), 8)

    def _tp_distribution(self, total_lot: float) -> list[tuple[str, float]]:

        """Возвращает [(name, qty), ...]"""
        if EXIT_STRATEGY == "tp1":
            return [("TP1", total_lot)]
        else:  # as_is
            tp_lot = round(total_lot / 3, 6)
            return [("TP1", tp_lot), ("TP2", tp_lot), ("TP3", tp_lot)]

    # --- высокоуровневые операции ---

    async def on_new_signal(self, signal: Signal):
        sym = self.exchange_symbol(signal)

        if self.stop_guard.is_blocked():
            log.warning(f"SKIP [{signal.symbol}]: дневной лимит стопов достигнут")
            return

        if not self.symbol_available(signal.symbol):
            log.warning(f"SKIP: {signal.symbol} не найден на бирже")
            return

        if signal.symbol in self.positions:
            log.warning(f"SKIP: позиция по {signal.symbol} уже открыта")
            return

        await self._set_leverage(sym, signal.leverage, signal.direction)
        total_lot = await self._calc_lot(signal)

        side       = "buy"  if signal.direction == "LONG" else "sell"
        close_side = "sell" if signal.direction == "LONG" else "buy"

        # Проверяем что цена ещё не ушла от entry1 в сторону TP
        try:
            ticker = await self.ex.fetch_ticker(sym)
            current = ticker['last']
            dist_entry_tp1 = abs(signal.tp1 - signal.entry1)
            dist_entry_cur = abs(current - signal.entry1)
            moved_toward_tp = (
                (signal.direction == "LONG" and current > signal.entry1) or
                (signal.direction == "SHORT" and current < signal.entry1)
            )
            if moved_toward_tp and dist_entry_tp1 > 0 and dist_entry_cur > dist_entry_tp1 * 0.5:
                log.warning(f"[{signal.symbol}] SKIP: цена прошла >50% к TP1 (entry={signal.entry1} cur={current:.6f} tp1={signal.tp1})")
                return
            log.info(f"[{signal.symbol}] Цена OK: cur={current:.6f} entry={signal.entry1}")
        except Exception as e:
            log.warning(f"[{signal.symbol}] Проверка цены: {e} — продолжаем")

        # ENTRY1 — 1/3 лота
        entry1_lot = round(total_lot / 3, 6)
        log.info(f"[{signal.symbol}] ENTRY1: {entry1_lot} @ {signal.entry1}  (1/3 лота)")

        # Сохраняем позицию ДО размещения ордера — если скрипт упадёт после place_limit,
        # при рестарте мы знаем что позиция существует
        self.positions[signal.symbol] = PositionState(
            signal=signal,
            total_lot=total_lot,
            entry_order_ids=[],
            sl_order_id=None,
            tp_order_ids=[],
        )
        save_state(self.positions)

        try:
            entry1_id = await self._place_limit(sym, side, entry1_lot, signal.entry1,
                                                client_id=f"1{time.time_ns() % 10**14}",
                                                direction=signal.direction)
            log.info(f"[{signal.symbol}] ENTRY1 id={entry1_id}")
        except Exception as e:
            log.error(f"ENTRY1 failed: {e}")
            del self.positions[signal.symbol]
            save_state(self.positions)
            return

        # SL и TP ставятся в _poll_position после того как ENTRY1 заполнен
        # (OKX не принимает close-ордера без открытой позиции)
        self.positions[signal.symbol].entry_order_ids = [entry1_id]
        save_state(self.positions)
        log.info(f"[{signal.symbol}] Позиция зафиксирована. Ждём заполнения ENTRY1.")
        asyncio.create_task(self._poll_position(signal.symbol))

    async def on_entry2_and_new_tp(self, upd: Entry2Update):
        pos = self.positions.get(upd.symbol)
        if not pos:
            log.warning(f"[{upd.symbol}] OPEN ENTRY2 получен, но позиция не найдена — пропускаем")
            return

        if pos.entry2_placed:
            log.warning(f"[{upd.symbol}] ENTRY2 уже выставлен — пропускаем")
            return

        signal = pos.signal
        sym    = self.exchange_symbol(signal)
        side   = "buy"  if signal.direction == "LONG" else "sell"
        close_side = "sell" if signal.direction == "LONG" else "buy"

        # ENTRY2 — 2/3 лота
        entry2_lot = round(pos.total_lot * 2 / 3, 6)
        log.info(f"[{upd.symbol}] ENTRY2: {entry2_lot} @ {upd.entry2}  (2/3 лота)")
        try:
            entry2_id = await self._place_limit(sym, side, entry2_lot, upd.entry2,
                                                client_id=f"2{time.time_ns() % 10**14}",
                                                direction=signal.direction)
            pos.entry_order_ids.append(entry2_id)
            pos.entry2_placed = True
            log.info(f"[{upd.symbol}] ENTRY2 id={entry2_id}")
        except Exception as e:
            log.error(f"ENTRY2 failed: {e}")
            return

        # Отменить старые TP
        log.info(f"[{upd.symbol}] Отменяем {len(pos.tp_order_ids)} старых TP-ордеров")
        for oid in pos.tp_order_ids:
            try:
                await self._cancel_order(sym, oid)
            except Exception as e:
                log.warning(f"  cancel TP {oid}: {e}")
        pos.tp_order_ids.clear()

        # Выставить новые TP по пересчитанным уровням
        new_tp_ids = await self._place_tp_orders(sym, signal, pos.total_lot, close_side,
                                                  upd.tp1, upd.tp2, upd.tp3)
        pos.tp_order_ids = new_tp_ids
        log.info(f"[{upd.symbol}] Новые TP выставлены: TP1={upd.tp1} TP2={upd.tp2} TP3={upd.tp3}")

    async def _cancel_all_orders(self, symbol: str, skip_ids: set = None):
        """Отменяет все ордера позиции (entry + TP + SL). skip_ids — не трогать эти ID."""
        pos = self.positions.get(symbol)
        if not pos:
            return
        sym = self.exchange_symbol(pos.signal)
        skip = skip_ids or set()
        all_orders = (
            [(f"ENTRY", oid) for oid in pos.entry_order_ids] +
            [(f"TP",    oid) for oid in pos.tp_order_ids] +
            ([("SL",   pos.sl_order_id)] if pos.sl_order_id else [])
        )
        for label, oid in all_orders:
            if oid in skip:
                continue
            try:
                await self._cancel_order(sym, oid)
                log.info(f"  [{symbol}] Отменён {label} ордер {oid}")
            except Exception as e:
                log.warning(f"  [{symbol}] Отмена {label} {oid}: {e}")

    async def _poll_position(self, symbol: str):
        """
        Фоновая задача. Логика:

        1. ENTRY1 open  → ждём, ничего не делаем
        2. ENTRY1 closed (заполнен) → позиция есть, биржа сама закроет SL/TP,
                                       нам нужно только отменить ENTRY2 если он ещё висит
        3. ENTRY1 canceled/unknown → вход не случился, отменяем всё, чистим state
        4. SL closed → отменяем незаполненные ENTRY2, TP биржа закроет сама
        """
        log.info(f"[{symbol}] Поллинг запущен (каждые {POLL_INTERVAL}s)")
        # Если после рестарта SL уже выставлен — ENTRY1 уже был заполнен
        pos0 = self.positions.get(symbol)
        entry1_filled = bool(pos0 and pos0.sl_order_id)
        if entry1_filled:
            log.info(f"[{symbol}] Восстановлено из state: entry1_filled=True")

        while symbol in self.positions:
            await asyncio.sleep(POLL_INTERVAL)
            pos = self.positions.get(symbol)
            if not pos:
                break
            sym = self.exchange_symbol(pos.signal)

            # --- Проверяем ENTRY1 пока он не заполнен ---
            if not entry1_filled and pos.entry_order_ids:
                entry1_id = pos.entry_order_ids[0]
                try:
                    status = await self._fetch_order_status(sym, entry1_id)
                except Exception as e:
                    log.warning(f"[{symbol}] Опрос ENTRY1: {e}")
                    continue

                if status == "closed":
                    log.info(f"[{symbol}] ENTRY1 заполнен — ставим SL и TP")
                    entry1_filled = True
                    sig = pos.signal
                    sym2 = self.exchange_symbol(sig)
                    cs = "sell" if sig.direction == "LONG" else "buy"
                    # SL — только если ещё не выставлен (защита от двойного выставления при рестарте)
                    if not pos.sl_order_id:
                        try:
                            sl_id = await self._place_stop_market(sym2, cs, pos.total_lot, sig.sl,
                                                                   client_id=f"3{time.time_ns() % 10**14}")
                            pos.sl_order_id = sl_id
                            log.info(f"[{symbol}] SL выставлен id={sl_id}")
                        except Exception as e:
                            log.error(f"[{symbol}] SL failed: {e}")
                    else:
                        log.info(f"[{symbol}] SL уже выставлен — пропускаем")
                    # TP — только если ещё не выставлен
                    if not pos.tp_order_ids:
                        tp_ids = await self._place_tp_orders(sym2, sig, pos.total_lot, cs,
                                                             sig.tp1, sig.tp2, sig.tp3)
                        pos.tp_order_ids = tp_ids
                        if tp_ids:
                            log.info(f"[{symbol}] TP выставлен: {tp_ids}")
                        else:
                            log.warning(f"[{symbol}] TP не выставлен — будет retry на следующем тике")
                    else:
                        log.info(f"[{symbol}] TP уже выставлен — пропускаем")
                    save_state(self.positions)
                    # Дальше следим за SL чтобы отменить ENTRY2

                elif status in ("canceled", "unknown"):
                    log.info(f"[{symbol}] ENTRY1 отменён/не заполнен — отменяем всё")
                    await self._cancel_all_orders(symbol)
                    await self.on_closed(symbol, "ENTRY_NOT_FILLED")
                    return
                # else: open — ждём следующей итерации

            # --- Retry TP если не выставлен ---
            if entry1_filled and pos.sl_order_id and not pos.tp_order_ids:
                sig = pos.signal
                sym2 = self.exchange_symbol(sig)
                cs = "sell" if sig.direction == "LONG" else "buy"
                log.info(f"[{symbol}] Retry TP...")
                tp_ids = await self._place_tp_orders(sym2, sig, pos.total_lot, cs,
                                                     sig.tp1, sig.tp2, sig.tp3)
                if tp_ids:
                    pos.tp_order_ids = tp_ids
                    save_state(self.positions)
                    log.info(f"[{symbol}] TP выставлен (retry): {tp_ids}")

            # --- После заполнения ENTRY1: следим за SL ---
            elif entry1_filled and pos.sl_order_id:
                try:
                    status = await self._fetch_order_status(sym, pos.sl_order_id)
                except Exception as e:
                    log.warning(f"[{symbol}] Опрос SL: {e}")
                    continue

                if status == "closed":
                    log.info(f"[{symbol}] SL исполнен — отменяем незаполненный ENTRY2")
                    self.stop_guard.register_stop()
                    # Отменяем только ENTRY2 (SL уже закрыт, TP — reduceOnly, биржа уберёт)
                    for oid in pos.entry_order_ids[1:]:  # entry2 и далее
                        try:
                            await self._cancel_order(sym, oid)
                            log.info(f"  [{symbol}] Отменён ENTRY2 {oid}")
                        except Exception as e:
                            log.warning(f"  [{symbol}] Отмена ENTRY2 {oid}: {e}")
                    await self.on_closed(symbol, "SL_FILLED")
                    return

                # TP сработал — биржа сама отменит SL (reduceOnly),
                # но нам надо отменить ENTRY2
                elif status in ("canceled", "unknown"):
                    log.info(f"[{symbol}] SL отменён биржей — TP сработал, отменяем ENTRY2")
                    for oid in pos.entry_order_ids[1:]:
                        try:
                            await self._cancel_order(sym, oid)
                            log.info(f"  [{symbol}] Отменён ENTRY2 {oid}")
                        except Exception as e:
                            log.warning(f"  [{symbol}] Отмена ENTRY2 {oid}: {e}")
                    await self.on_closed(symbol, "TP_FILLED")
                    return

        log.info(f"[{symbol}] Поллинг завершён")

    async def on_closed(self, symbol: str, reason: str):
        if symbol in self.positions:
            pos = self.positions[symbol]
            sym = self.exchange_symbol(pos.signal)
            # Отменяем все висящие ордера (entry1/entry2 незаполненные)
            all_oids = pos.entry_order_ids + pos.tp_order_ids
            if pos.sl_order_id:
                all_oids.append(pos.sl_order_id)
            for oid in all_oids:
                try:
                    await self._cancel_order(sym, oid)
                except Exception:
                    pass
            del self.positions[symbol]
            save_state(self.positions)
            log.info(f"[{symbol}] Позиция закрыта ({reason}), все ордера отменены")

    async def _fetch_position_size(self, sym: str, direction: str) -> float:
        """Возвращает размер открытой позиции на бирже (0 если нет)."""
        try:
            positions = await self.ex.fetch_positions([sym])
            side = "long" if direction == "LONG" else "short"
            for p in positions:
                if p.get("side") == side and (p.get("contracts") or 0) > 0:
                    return float(p["contracts"])
        except Exception as e:
            log.warning(f"fetch_position_size {sym}: {e}")
        return 0.0

    async def _place_tp_orders(self, sym: str, signal: Signal, total_lot: float,
                                close_side: str, tp1: float, tp2: float, tp3: float) -> list[str]:
        tp_prices = {"TP1": tp1, "TP2": tp2, "TP3": tp3}
        tp_ids = []
        for name, qty in self._tp_distribution(total_lot):
            price = self._apply_drift(tp_prices[name], signal.direction, is_tp=True)
            try:
                oid = await self._place_limit(sym, close_side, qty, price,
                                              reduce_only=True,
                                              client_id=f"4{abs(hash(name)) % 10}{time.time_ns() % 10**14}",
                                              direction=signal.direction)
                tp_ids.append(oid)
                log.info(f"  {name}: {qty} @ {price}  id={oid}")
            except Exception as e:
                log.error(f"  {name} failed: {e}")
        return tp_ids

# ---------------------------------------------------------------------------
# OKX Executor
# ---------------------------------------------------------------------------

class OKXExecutor(BaseExecutor):

    CACHE_FILE = "okx_symbols.json"

    def __init__(self):
        super().__init__()
        # aiohttp с ThreadedResolver обходит проблему aiodns на Windows
        connector = aiohttp.TCPConnector(resolver=aiohttp.ThreadedResolver(), ssl=False)
        self._session = aiohttp.ClientSession(connector=connector)
        params = {"apiKey": OKX_API_KEY, "secret": OKX_SECRET, "password": OKX_PASSPHRASE,
                  "options": {"defaultType": "swap"},
                  "session": self._session}
        if OKX_TESTNET:
            params["sandbox"] = True
            log.warning("⚠️  OKX TESTNET MODE")
        self.ex = ccxt.okx(params)
        self._symbols: set[str] = set()

    async def init(self):
        await self._load_symbols()
        asyncio.create_task(self._refresh_loop())

    async def close(self):
        await self.ex.close()
        if hasattr(self, "_session") and not self._session.closed:
            await self._session.close()

    def exchange_symbol(self, signal: Signal) -> str:
        return signal.okx_symbol

    def symbol_available(self, sig_symbol: str) -> bool:
        base = sig_symbol.replace("USDT", "")
        return f"{base}/USDT:USDT" in self._symbols

    async def _load_symbols(self):
        path = Path(self.CACHE_FILE)
        if path.exists():
            data = json.loads(path.read_text())
            if time.time() - data.get("updated_at", 0) < SYMBOL_CACHE_TTL:
                self._symbols = set(data["symbols"])
                log.info(f"OKX SymbolCache: {len(self._symbols)} (from file)")
                return
        await self._refresh_symbols()

    async def _refresh_symbols(self):
        markets = await self.ex.load_markets()
        self._symbols = {s for s, i in markets.items()
                         if i.get("type") == "swap" and i.get("quote") == "USDT" and i.get("active")}
        Path(self.CACHE_FILE).write_text(
            json.dumps({"updated_at": time.time(), "symbols": list(self._symbols)}))
        log.info(f"OKX SymbolCache: refreshed {len(self._symbols)}")

    async def _refresh_loop(self):
        while True:
            await asyncio.sleep(SYMBOL_CACHE_TTL)
            await self._refresh_symbols()

    async def _max_leverage(self, sym: str) -> int:
        markets = await self.ex.load_markets()
        info = markets.get(sym, {})
        try:
            return int(info.get("limits", {}).get("leverage", {}).get("max", MAX_LEVERAGE) or MAX_LEVERAGE)
        except Exception:
            return MAX_LEVERAGE

    async def _set_leverage(self, sym: str, leverage: int, direction: str):
        max_lev = await self._max_leverage(sym)
        leverage = min(leverage, max_lev)
        side = "long" if direction == "LONG" else "short"
        # Пробуем с запрошенным плечом, при ошибке снижаем до 1
        while leverage >= 1:
            try:
                await self.ex.set_leverage(leverage, sym, params={"mgnMode": "cross", "posSide": side})
                log.info(f"Leverage: {leverage}x (max={max_lev}x) on {sym}")
                return
            except Exception as e:
                msg = str(e)
                if "59102" in msg or "exceeds" in msg.lower():
                    leverage -= 1
                    log.warning(f"Leverage too high, trying {leverage}x...")
                else:
                    log.warning(f"set_leverage: {e}")
                    return

    async def _calc_lot(self, signal: Signal) -> float:
        markets = await self.ex.load_markets()
        info = markets.get(signal.okx_symbol)
        if not info: raise ValueError(f"Not found on OKX: {signal.okx_symbol}")
        if signal.sl_dist == 0: raise ValueError("SL distance is zero")
        contract_size = float(info.get("contractSize", 1))
        pnl_per = signal.sl_dist * contract_size
        total = RISK_USDT / pnl_per
        min_amt = float(info.get("limits", {}).get("amount", {}).get("min", 1) or 1)
        lot = max(round(total), int(min_amt))
        log.info(f"Sizing OKX: sl_dist={signal.sl_dist:.6f}  lot={lot}")
        return float(lot)

    def _bp(self, direction: str) -> dict:
        pos_side = "long" if direction == "LONG" else "short"
        return {"tdMode": "cross", "posSide": pos_side}

    async def _place_limit(self, sym: str, side: str, qty: float, price: float,
                           reduce_only: bool = False, client_id: str = "",
                           direction: str = "LONG") -> str:
        params = {**self._bp(direction), "clOrdId": client_id}
        if reduce_only: params["reduceOnly"] = True
        o = await self.ex.create_order(sym, "limit", side, qty, price, params=params)
        return o["id"]

    async def _place_stop_market(self, sym: str, side: str, qty: float,
                                  stop_price: float, client_id: str = "") -> str:
        """
        Ставит Entire Position SL через algo order (closeFraction=1).
        Биржа автоматически закрывает 100% позиции при срабатывании —
        не нужно перевыставлять при добавлении ENTRY2.
        """
        pos = self.positions.get(next((k for k, v in self.positions.items()
                                       if self.exchange_symbol(v.signal) == sym), ""))
        direction = pos.signal.direction if pos else "LONG"
        pos_side = "long" if direction == "LONG" else "short"
        params = {
            "tdMode": "cross",
            "posSide": pos_side,
            "triggerPx": str(stop_price),
            "orderPx":   "-1",       # market price при срабатывании
            "closeFraction": "1",    # закрыть 100% позиции
            "algoClOrdId": client_id,
        }
        # Entire position SL → algo endpoint
        o = await self.ex.private_post_trade_order_algo({
            "instId":    sym.replace("/", "-").replace(":USDT", "-SWAP"),
            "tdMode":    "cross",
            "side":      side,
            "posSide":   pos_side,
            "ordType":   "conditional",
            "slTriggerPx":  str(stop_price),
            "slOrdPx":      "-1",
            "closeFraction": "1",
        })
        algo_id = o["data"][0]["algoId"]
        log.info(f"OKX Entire-position SL: algoId={algo_id}  triggerPx={stop_price}")
        return algo_id

    async def _cancel_order(self, sym: str, order_id: str):
        """Отменяет обычный или algo (SL) ордер."""
        try:
            await self.ex.cancel_order(order_id, sym)
        except Exception:
            # Возможно это algo ордер — пробуем algo endpoint
            inst_id = sym.replace("/", "-").replace(":USDT", "-SWAP")
            await self.ex.private_post_trade_cancel_algos({
                "algoId": order_id, "instId": inst_id
            })

    async def _fetch_order_status(self, sym: str, order_id: str) -> str:
        try:
            o = await self.ex.fetch_order(order_id, sym)
            return o.get("status", "unknown")
        except Exception:
            # Пробуем algo endpoint
            try:
                inst_id = sym.replace("/", "-").replace(":USDT", "-SWAP")
                r = await self.ex.private_get_trade_order_algo({
                    "algoId": order_id, "instId": inst_id
                })
                state = r["data"][0].get("state", "unknown")
                # OKX algo states: live → open, effective → closed, canceled → canceled
                mapping = {"live": "open", "effective": "closed", "canceled": "canceled"}
                return mapping.get(state, "unknown")
            except Exception:
                return "unknown"

# ---------------------------------------------------------------------------
# Binance Executor
# ---------------------------------------------------------------------------

class BinanceExecutor(BaseExecutor):

    CACHE_FILE = "binance_symbols.json"

    def __init__(self):
        super().__init__()
        params = {"apiKey": BINANCE_API_KEY, "secret": BINANCE_SECRET,
                  "options": {"defaultType": "future"}}
        if BINANCE_BASE_URL:
            params["urls"] = {"api": {"fapiPublic":  BINANCE_BASE_URL + "/fapi/v1",
                                      "fapiPrivate": BINANCE_BASE_URL + "/fapi/v1"}}
            log.info(f"Binance: base URL → {BINANCE_BASE_URL}")
        if BINANCE_TESTNET:
            params["sandbox"] = True
            log.warning("⚠️  BINANCE TESTNET MODE")
        self.ex = ccxt.binance(params)
        self._symbols: set[str] = set()
        self._markets: dict = {}

    async def init(self):
        await self._load_symbols()
        asyncio.create_task(self._refresh_loop())

    async def close(self):
        await self.ex.close()

    def exchange_symbol(self, signal: Signal) -> str:
        return signal.binance_symbol

    def symbol_available(self, sig_symbol: str) -> bool:
        return sig_symbol in self._symbols

    async def _load_symbols(self):
        path = Path(self.CACHE_FILE)
        if path.exists():
            data = json.loads(path.read_text())
            if time.time() - data.get("updated_at", 0) < SYMBOL_CACHE_TTL:
                self._symbols = set(data["symbols"])
                log.info(f"Binance SymbolCache: {len(self._symbols)} (from file)")
                return
        await self._refresh_symbols()

    async def _refresh_symbols(self):
        self._markets = await self.ex.load_markets()
        self._symbols = {i["id"] for i in self._markets.values()
                         if i.get("type") == "future" and i.get("quote") == "USDT" and i.get("active")}
        Path(self.CACHE_FILE).write_text(
            json.dumps({"updated_at": time.time(), "symbols": list(self._symbols)}))
        log.info(f"Binance SymbolCache: refreshed {len(self._symbols)}")

    async def _refresh_loop(self):
        while True:
            await asyncio.sleep(SYMBOL_CACHE_TTL)
            await self._refresh_symbols()

    async def _max_leverage(self, sym: str) -> int:
        if not self._markets:
            self._markets = await self.ex.load_markets()
        info = next((v for v in self._markets.values()
                     if v.get("id") == sym and v.get("type") == "future"), {})
        try:
            return int(info.get("limits", {}).get("leverage", {}).get("max", MAX_LEVERAGE) or MAX_LEVERAGE)
        except Exception:
            return MAX_LEVERAGE

    async def _set_leverage(self, sym: str, leverage: int, direction: str):
        max_lev = await self._max_leverage(sym)
        leverage = min(leverage, max_lev)
        try:
            await self.ex.set_leverage(leverage, sym)
            log.info(f"Leverage: {leverage}x (max={max_lev}x) on {sym}")
        except Exception as e:
            log.warning(f"set_leverage: {e}")

    async def _calc_lot(self, signal: Signal) -> float:
        if not self._markets:
            self._markets = await self.ex.load_markets()
        info = next((v for v in self._markets.values()
                     if v.get("id") == signal.binance_symbol and v.get("type") == "future"), None)
        if not info: raise ValueError(f"Not found on Binance: {signal.binance_symbol}")
        if signal.sl_dist == 0: raise ValueError("SL distance is zero")
        qty = RISK_USDT / signal.sl_dist
        step = float(info.get("limits", {}).get("amount", {}).get("min", 1) or 1)
        lot = max(round(qty / step) * step, step)
        precision = info.get("precision", {}).get("amount")
        if precision: lot = round(lot, int(precision))
        log.info(f"Sizing Binance: sl_dist={signal.sl_dist:.6f}  lot={lot}")
        return lot

    def _pos_side(self, direction: str) -> str:
        return "LONG" if direction == "LONG" else "SHORT"

    async def _place_limit(self, sym: str, side: str, qty: float, price: float,
                           reduce_only: bool = False, client_id: str = "",
                           direction: str = "LONG") -> str:
        params = {"positionSide": self._pos_side(direction)}
        if reduce_only: params["reduceOnly"] = True
        o = await self.ex.create_order(sym, "LIMIT", side, qty, price, params=params)
        return str(o["id"])

    async def _place_stop_market(self, sym: str, side: str, qty: float,
                                  stop_price: float, client_id: str = "") -> str:
        pos = self.positions.get(sym)
        direction = pos.signal.direction if pos else "LONG"
        params = {"stopPrice": stop_price, "positionSide": self._pos_side(direction),
                  "reduceOnly": True}
        o = await self.ex.create_order(sym, "STOP_MARKET", side, qty, None, params=params)
        return str(o["id"])

    async def _cancel_order(self, sym: str, order_id: str):
        await self.ex.cancel_order(order_id, sym)

    async def _fetch_order_status(self, sym: str, order_id: str) -> str:
        try:
            o = await self.ex.fetch_order(order_id, sym)
            return o.get("status", "unknown")  # open | closed | canceled
        except Exception:
            return "unknown"

# ---------------------------------------------------------------------------
# main
# ---------------------------------------------------------------------------

async def main():
    if EXCHANGE == "binance":
        executor: BaseExecutor = BinanceExecutor()
        label = f"Binance Futures{' → ' + BINANCE_BASE_URL if BINANCE_BASE_URL else ''}"
    else:
        executor = OKXExecutor()
        label = f"OKX {'TESTNET' if OKX_TESTNET else 'LIVE'}"

    log.info(f"Exchange: {label}")
    log.info(f"Risk: {RISK_USDT} USDT  |  Exit: {EXIT_STRATEGY.upper()}  |  Bar window: {BAR_WINDOW_SEC}s")

    await executor.init()

    client = TelegramClient(TG_SESSION_FILE, TG_API_ID, TG_API_HASH)
    await client.start()
    log.info(f"Listening to @{SIGNAL_CHANNEL}")

    @client.on(events.NewMessage(chats=SIGNAL_CHANNEL))
    async def handler(event):
        text  = event.message.message or ""
        mtype = classify_message(text)
        log.info(f"MSG [{mtype}]: {text[:80].strip()!r}")

        try:
            if mtype == "NEW_SIGNAL":
                if not is_bar_boundary():
                    log.info("SKIPPED: outside M5 window")
                    return
                signal = parse_signal(text)
                if signal:
                    await executor.on_new_signal(signal)
                else:
                    log.warning("SKIPPED: parse failed")

            elif mtype == "ENTRY2_AND_NEW_TP":
                upd = parse_entry2_update(text)
                if upd:
                    await executor.on_entry2_and_new_tp(upd)
                else:
                    log.warning("SKIPPED: parse_entry2_update failed")

            elif mtype == "TP_HIT":
                # Игнорируем — поллинг сам увидит закрытие позиции на бирже.
                # Это исключает ложные закрытия когда канал пишет про встречную позицию.
                log.debug(f"TP_HIT из канала — игнорируем, поллинг разберётся")

            elif mtype == "SL_HIT":
                # Аналогично — не закрываем по сообщению канала, только поллинг.
                log.debug(f"SL_HIT из канала — игнорируем, поллинг разберётся")

        except Exception as e:
            log.error(f"Handler error: {e}", exc_info=True)

    try:
        await client.run_until_disconnected()
    except (KeyboardInterrupt, asyncio.CancelledError):
        pass
    finally:
        await executor.close()
        log.info("Остановлено.")


if __name__ == "__main__":
    asyncio.run(main())
