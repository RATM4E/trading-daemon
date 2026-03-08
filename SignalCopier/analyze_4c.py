"""
Анализатор истории канала 4C Trading Signals
=============================================
Формат сигнала:
    💎 #FLOW/USDT #SHORT
    ✅ Entry Zone: $0.04080 - 0.04170
    ☑️ Target:
    Target 1 $0.04040
    ...
    Target 6 $0.03820
    🚫 StopLoss: 0.04298
    Leverage 20x

Формат закрытия (в том же или отдельном сообщении):
    Take-Profit target 1 ✅ 🚀
    Take-Profit target 2 ✅ 🚀
    Profit: 420.4% 📉
    Period: 2 Hours 43 Minutes

Запуск:
    python analyze_4c.py --channel 4c_trading_signals --days 90 --limit 20000
    python analyze_4c.py --channel 4c_trading_signals --no-fetch
    python analyze_4c.py --no-fetch --symbol FLOW
"""

import argparse
import asyncio
import json
import os
import re
from collections import defaultdict
from dataclasses import dataclass, field
from datetime import datetime, timedelta, timezone
from pathlib import Path
from typing import Optional

from dotenv import load_dotenv

load_dotenv()

TG_API_ID       = int(os.environ["TG_API_ID"])
TG_API_HASH     = os.environ["TG_API_HASH"]
TG_SESSION_FILE = "cryptonus_session"   # переиспользуем ту же сессию
DEFAULT_CHANNEL = "4c_trading_signals"
MAX_TARGETS     = 6

# ---------------------------------------------------------------------------
# Модели
# ---------------------------------------------------------------------------

@dataclass
class Signal4C:
    symbol:    str          # FLOWUSDT (нормализованный)
    direction: str          # LONG | SHORT
    entry_lo:  float        # нижняя граница Entry Zone
    entry_hi:  float        # верхняя граница Entry Zone
    targets:   list         # [t1, t2, ..., t6] — может быть меньше 6
    sl:        float
    leverage:  int
    msg_id:    int
    timestamp: datetime

    @property
    def entry_mid(self) -> float:
        return (self.entry_lo + self.entry_hi) / 2

    @property
    def sl_dist(self) -> float:
        return abs(self.entry_mid - self.sl)

    def target_r(self, idx: int) -> float:
        """R-значение для таргета с индексом idx (0-based)."""
        if self.sl_dist == 0 or idx >= len(self.targets):
            return 0.0
        return abs(self.targets[idx] - self.entry_mid) / self.sl_dist


@dataclass
class Trade4C:
    signal:       Signal4C
    targets_hit:  list = field(default_factory=list)  # [1, 2, 3, ...] 1-based
    sl_hit:       bool = False
    closed:       bool = False

    @property
    def outcome(self) -> str:
        if not self.closed:
            return "OPEN"
        if self.sl_hit and not self.targets_hit:
            return "SL"
        if self.sl_hit:
            return f"T{''.join(str(t) for t in self.targets_hit)}+SL"
        if self.targets_hit:
            return f"T{''.join(str(t) for t in self.targets_hit)}"
        return "UNKNOWN"

    def pnl_r(self, exit_target: int) -> float:
        """
        PnL в R при выходе на exit_target (1-based).
        Если SL сработал раньше — возвращает -1.0
        """
        sig = self.signal
        if not self.closed:
            return 0.0
        # SL без таргетов
        if self.sl_hit and not self.targets_hit:
            return -1.0
        # Нужный таргет достигнут
        if exit_target in self.targets_hit:
            return sig.target_r(exit_target - 1)
        # SL сработал (после частичных таргетов)
        if self.sl_hit:
            return -1.0
        # Не достигнут нужный таргет, нет SL — не закрылось как нужно
        return 0.0


# ---------------------------------------------------------------------------
# Парсер
# ---------------------------------------------------------------------------

def parse_signal(text: str, msg_id: int, ts: datetime) -> Optional[Signal4C]:
    """
    Парсит сигнал формата 4C Trading.

    Поддерживаемые форматы:
      💎BUY #ADA/USDT #LONG
      💎 #FLOW/USDT #SHORT
      ✅ ENTRY ZONE: 0.4490 - 0.4400
      [цены таргетов без лейбла, одна на строку]
      ⭕️ STOP-LOSS: 0.4328
      LEVERAGE: 20x
    """
    try:
        # Направление: из BUY/SELL или из #LONG/#SHORT
        dir_m = re.search(r"\b(BUY|SELL)\b", text, re.IGNORECASE)
        if dir_m:
            direction = "LONG" if dir_m.group(1).upper() == "BUY" else "SHORT"
        else:
            dir_m2 = re.search(r"#(LONG|SHORT)", text, re.IGNORECASE)
            if not dir_m2:
                return None
            direction = dir_m2.group(1).upper()

        # Символ: #ADA/USDT или #ADAUSDT
        sym_m = re.search(r"#([A-Z0-9]+)(?:/USDT)?", text, re.IGNORECASE)
        if not sym_m:
            return None
        symbol = sym_m.group(1).upper() + "USDT"

        # Entry Zone
        ez = re.search(r"ENTRY\s*ZONE[:\s]*\$?([\d.]+)\s*[-–]\s*\$?([\d.]+)", text, re.IGNORECASE)
        if not ez:
            ez1 = re.search(r"ENTRY[:\s]*\$?([\d.]+)", text, re.IGNORECASE)
            if not ez1:
                return None
            entry_lo = entry_hi = float(ez1.group(1))
        else:
            entry_lo, entry_hi = float(ez.group(1)), float(ez.group(2))

        # StopLoss (ищем до парсинга таргетов чтобы отсечь SL-цену)
        sl_m = re.search(r"STOP.?LOSS[:\s]*\$?([\d.]+)", text, re.IGNORECASE)
        if not sl_m:
            return None
        sl = float(sl_m.group(1))

        # Таргеты — два варианта:
        # 1. С лейблом: Target 1 $0.04040
        # 2. Без лейбла: просто цифры на строках после ENTRY ZONE и до STOP-LOSS
        targets = []
        labeled = re.findall(r"Target\s+\d+[:\s.]*\$?([\d.]+)", text, re.IGNORECASE)
        if labeled:
            targets = [float(x) for x in labeled]
        else:
            # Вырезаем блок между Entry Zone и Stop-Loss
            ez_end   = ez.end() if ez else 0
            sl_start = sl_m.start()
            block    = text[ez_end:sl_start]
            # Берём все числа с десятичной точкой — это и есть таргеты
            targets = [float(x) for x in re.findall(r"\b(\d+\.\d+)\b", block)]
            # Убираем дубли и сортируем в направлении сделки
            targets = sorted(set(targets),
                             reverse=(direction == "LONG"))

        if not targets:
            return None

        # Leverage
        lev_m = re.search(r"LEVERAGE[:\s]*(\d+)x", text, re.IGNORECASE)
        leverage = int(lev_m.group(1)) if lev_m else 20

        return Signal4C(
            symbol=symbol, direction=direction,
            entry_lo=entry_lo, entry_hi=entry_hi,
            targets=targets,
            sl=sl,
            leverage=leverage,
            msg_id=msg_id,
            timestamp=ts,
        )
    except Exception:
        return None


def parse_result(text: str):
    """
    Парсит сообщение с результатом сделки.
    Возвращает (symbol, targets_hit, sl_hit) или None.

    Форматы:
      Take-Profit target 1 ✅
      Take-Profit target 2 ✅ 🚀
      #SIREN/USDT ... Take-Profit target 1 ✅ ... Profit: 122.67%
    """
    # Символ
    sym_m = re.search(r"#([A-Z0-9]+)(?:/USDT)?", text, re.IGNORECASE)
    if not sym_m:
        return None
    symbol = sym_m.group(1) + "USDT"

    # Достигнутые таргеты
    targets_hit = []
    for tm in re.finditer(r"Take-Profit target\s+(\d+)\s*✅", text, re.IGNORECASE):
        targets_hit.append(int(tm.group(1)))

    # SL
    sl_hit = bool(re.search(r"Stop.?Loss.*hit|стоп|SL\s*✅|SL\s*⛔|закрыт.*стоп", text, re.IGNORECASE))

    if not targets_hit and not sl_hit:
        return None

    return symbol, sorted(set(targets_hit)), sl_hit


def classify(text: str) -> str:
    t = text.upper()
    if re.search(r"#[A-Z0-9]+(?:/USDT)?\s+#(LONG|SHORT)", t):
        if "ENTRY ZONE" in t or "STOPLOSS" in t or "STOP LOSS" in t:
            return "NEW_SIGNAL"
    if "TAKE-PROFIT TARGET" in t and "✅" in text:
        return "RESULT"
    if re.search(r"STOP.?LOSS.*HIT|SL\s*[✅⛔]", t):
        return "SL_HIT"
    return "UNKNOWN"


# ---------------------------------------------------------------------------
# Fetch
# ---------------------------------------------------------------------------

async def fetch_history(channel: str, days: int, limit: int) -> list[dict]:
    from telethon import TelegramClient
    from telethon.tl.types import Message
    client = TelegramClient(TG_SESSION_FILE, TG_API_ID, TG_API_HASH)
    await client.start()
    print(f"Fetching @{channel} ({days}d, max {limit})...")
    cutoff = datetime.now(timezone.utc) - timedelta(days=days)
    msgs = []
    async for msg in client.iter_messages(channel, limit=limit):
        if not isinstance(msg, Message) or not msg.text:
            continue
        if msg.date < cutoff:
            break
        msgs.append({"id": msg.id, "text": msg.text,
                     "ts": msg.date.isoformat()})
    await client.disconnect()
    msgs.reverse()
    cache_file = f"history_cache_{channel.lstrip('@').lower()}.json"
    Path(cache_file).write_text(
        json.dumps(msgs, ensure_ascii=False, indent=2), encoding="utf-8")
    print(f"Saved {len(msgs)} messages → {cache_file}")
    return msgs


# ---------------------------------------------------------------------------
# Анализ
# ---------------------------------------------------------------------------

def analyze(messages: list[dict], filter_symbol: Optional[str] = None):
    open_trades: dict[str, Trade4C] = {}
    closed: list[Trade4C] = []
    total_signals = 0

    for m in messages:
        text = m["text"]
        ts   = datetime.fromisoformat(m["ts"])
        mtype = classify(text)

        if mtype == "NEW_SIGNAL":
            sig = parse_signal(text, m["id"], ts)
            if not sig:
                continue
            if filter_symbol and filter_symbol.upper() not in sig.symbol:
                continue
            total_signals += 1
            open_trades[sig.symbol] = Trade4C(signal=sig)

        elif mtype in ("RESULT", "SL_HIT"):
            result = parse_result(text)
            if not result:
                continue
            symbol, targets_hit, sl_hit = result
            if filter_symbol and filter_symbol.upper() not in symbol:
                continue

            if symbol not in open_trades:
                continue
            trade = open_trades[symbol]

            # Обновляем таргеты накопительно (результаты могут приходить поэтапно)
            for t in targets_hit:
                if t not in trade.targets_hit:
                    trade.targets_hit.append(t)
            trade.targets_hit.sort()

            if sl_hit:
                trade.sl_hit = True

            # Финальный результат: Profit: X% означает что всё закрыто
            if "Profit:" in text or sl_hit:
                trade.closed = True
                closed.append(trade)
                del open_trades[symbol]

    still_open = len(open_trades)

    print(f"\n  Всего сигналов  : {total_signals}")
    print(f"  Закрытых        : {len(closed)}")
    print(f"  Ещё открытых    : {still_open}")

    if not closed:
        print("\n  Нет закрытых сделок для анализа.")
        return

    # --- Паттерны исходов ---
    outcomes: dict[str, int] = defaultdict(int)
    for t in closed:
        outcomes[t.outcome] += 1
    outcomes_sorted = sorted(outcomes.items(), key=lambda x: -x[1])

    print(f"\n{'═'*80}")
    print(f"  ПАТТЕРНЫ ИСХОДОВ  (всего закрытых: {len(closed)})")
    print(f"{'═'*80}")
    for outcome, cnt in outcomes_sorted:
        pct = cnt / len(closed) * 100
        bar = "█" * int(pct / 2)
        print(f"  {outcome:<30} {cnt:>5}  {pct:>5.1f}%  {bar}")

    # --- Достижимость таргетов ---
    print(f"\n  ДОСТИЖИМОСТЬ ТАРГЕТОВ:")
    for ti in range(1, MAX_TARGETS + 1):
        reached = sum(1 for t in closed if ti in t.targets_hit)
        if reached == 0:
            break
        pct = reached / len(closed) * 100
        print(f"    Target {ti}: {reached:>4}  ({pct:.1f}%)")

    # --- Рейтинг стратегий ---
    print(f"\n{'═'*80}")
    print(f"  РЕЙТИНГ СТРАТЕГИЙ ВЫХОДА")
    print(f"{'═'*80}")
    header = f"  {'Стратегия':<25} {'Описание':<30} {'N':>5} {'TotalR':>9} {'WR%':>6} {'PF':>6} {'AvgW':>7} {'AvgL':>7} {'MaxDD':>8}"
    print(header)
    print(f"  {'─'*len(header)}")

    strategies = []

    # Простые: выходим целиком на Target N
    for ti in range(1, MAX_TARGETS + 1):
        applicable = [t for t in closed if t.signal.target_r(ti - 1) > 0]
        if not applicable:
            break
        wins, losses = [], []
        for t in applicable:
            r = t.pnl_r(ti)
            if r > 0:
                wins.append(r)
            elif r < 0:
                losses.append(r)
        if not wins and not losses:
            continue
        total_r  = sum(wins) + sum(losses)
        wr       = len(wins) / len(applicable) * 100
        pf       = sum(wins) / abs(sum(losses)) if losses else float("inf")
        avg_w    = sum(wins) / len(wins) if wins else 0
        avg_l    = sum(losses) / len(losses) if losses else 0
        # MaxDD
        equity, peak, max_dd = 0.0, 0.0, 0.0
        for t in applicable:
            equity += t.pnl_r(ti)
            peak = max(peak, equity)
            max_dd = min(max_dd, equity - peak)
        strategies.append((f"TARGET_{ti}", f"Всё → Target {ti}",
                           len(applicable), total_r, wr, pf, avg_w, avg_l, max_dd))

    # Равномерные: 1/N на каждый из первых N таргетов
    for n in range(2, MAX_TARGETS + 1):
        applicable = [t for t in closed if len(t.signal.targets) >= n]
        if not applicable:
            break
        wins, losses = [], []
        for t in applicable:
            r = 0.0
            for ti in range(1, n + 1):
                share = 1 / n
                r += share * t.pnl_r(ti)
            if r > 0: wins.append(r)
            elif r < 0: losses.append(r)
        if not wins and not losses:
            continue
        total_r = sum(wins) + sum(losses)
        wr      = len(wins) / len(applicable) * 100
        pf      = sum(wins) / abs(sum(losses)) if losses else float("inf")
        avg_w   = sum(wins) / len(wins) if wins else 0
        avg_l   = sum(losses) / len(losses) if losses else 0
        equity, peak, max_dd = 0.0, 0.0, 0.0
        for t in applicable:
            r = sum((1/n) * t.pnl_r(ti) for ti in range(1, n+1))
            equity += r
            peak = max(peak, equity)
            max_dd = min(max_dd, equity - peak)
        strategies.append((f"SPLIT_{n}", f"1/{n} на каждый T1..T{n}",
                           len(applicable), total_r, wr, pf, avg_w, avg_l, max_dd))

    strategies.sort(key=lambda x: -x[3])  # по Total R
    medals = ["🥇", "🥈", "🥉"]
    for i, (name, desc, n, total_r, wr, pf, avg_w, avg_l, max_dd) in enumerate(strategies):
        medal = medals[i] if i < 3 else "  "
        pf_s  = f"{pf:.2f}" if pf != float("inf") else "  ∞"
        print(f"  {medal} {name:<23} {desc:<30} {n:>5} {total_r:>+9.2f}R "
              f"{wr:>5.1f}% {pf_s:>6} {avg_w:>+7.2f}R {avg_l:>+7.2f}R {max_dd:>+8.2f}R")

    # --- Статистика по символам ---
    print(f"\n{'═'*80}")
    print(f"  ТОП СИМВОЛОВ (по Total R, стратегия TARGET_1)")
    print(f"{'═'*80}")
    by_sym: dict[str, list] = defaultdict(list)
    for t in closed:
        by_sym[t.signal.symbol].append(t)
    sym_stats = []
    for sym, trades in by_sym.items():
        r_vals = [t.pnl_r(1) for t in trades]
        total  = sum(r_vals)
        wr     = sum(1 for r in r_vals if r > 0) / len(r_vals) * 100
        sym_stats.append((sym, len(trades), total, wr))
    sym_stats.sort(key=lambda x: -x[2])
    print(f"  {'Символ':<15} {'N':>5} {'TotalR':>9} {'WR%':>6}")
    print(f"  {'─'*40}")
    for sym, n, total, wr in sym_stats[:20]:
        print(f"  {sym:<15} {n:>5} {total:>+9.2f}R {wr:>5.1f}%")
    if len(sym_stats) > 20:
        print(f"  ... и ещё {len(sym_stats)-20} символов")


# ---------------------------------------------------------------------------
# Main
# ---------------------------------------------------------------------------

async def async_main(args):
    channel    = args.channel.lstrip("@")
    cache_file = f"history_cache_{channel.lower()}.json"

    if args.no_fetch and Path(cache_file).exists():
        messages = json.loads(Path(cache_file).read_text(encoding="utf-8"))
        print(f"Cache [{channel}]: {len(messages)} messages")
    else:
        messages = await fetch_history(channel=channel, days=args.days, limit=args.limit)

    analyze(messages, filter_symbol=args.symbol)


if __name__ == "__main__":
    p = argparse.ArgumentParser(description="Анализатор 4C Trading Signals")
    p.add_argument("--channel",  type=str, default=DEFAULT_CHANNEL,
                   help="Username канала (по умолчанию: 4c_trading_signals)")
    p.add_argument("--days",     type=int, default=90)
    p.add_argument("--limit",    type=int, default=20000)
    p.add_argument("--no-fetch", action="store_true")
    p.add_argument("--symbol",   type=str, default=None)
    asyncio.run(async_main(p.parse_args()))
