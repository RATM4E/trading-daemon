"""
Cryptonus_Trade — анализатор истории + оптимизатор стратегий выхода
====================================================================
Скачивает историю канала, восстанавливает сделки, прогоняет
каждую сделку через несколько стратегий выхода и сравнивает
результаты — общий и per-symbol.

Запуск:
    python analyze_history.py             # последние 30 дней
    python analyze_history.py --days 90
    python analyze_history.py --no-fetch  # использовать кэш без скачивания
    python analyze_history.py --symbol BTC  # детальный разбор по символу

Зависимости:
    pip install telethon python-dotenv
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
from typing import Callable, Optional

from dotenv import load_dotenv

load_dotenv()

TG_API_ID       = int(os.environ["TG_API_ID"])
TG_API_HASH     = os.environ["TG_API_HASH"]
TG_SESSION_FILE = "cryptonus_session"
SIGNAL_CHANNEL  = "Cryptonus_Trade"
CACHE_FILE      = "history_cache.json"

# ---------------------------------------------------------------------------
# Модель сделки
# ---------------------------------------------------------------------------

@dataclass
class Trade:
    symbol:        str
    direction:     str
    entry1:        float
    entry2:        float
    tp1:           float
    tp2:           float
    tp3:           float
    sl:            float
    leverage:      int
    opened_at:     datetime

    entry2_filled: bool = False
    tp1_hit:       bool = False
    tp2_hit:       bool = False
    tp3_hit:       bool = False
    sl_hit:        bool = False
    closed_at:     Optional[datetime] = None

    @property
    def entry_avg(self) -> float:
        """Взвешенное среднее: ENTRY1=1/3, ENTRY2=2/3."""
        if self.entry2_filled:
            return self.entry1 * (1/3) + self.entry2 * (2/3)
        return self.entry1

    @property
    def sl_dist(self) -> float:
        return abs(self.entry_avg - self.sl)

    @property
    def entry_fill(self) -> float:
        """1/3 если только ENTRY1, 1.0 если оба входа состоялись."""
        return 1.0 if self.entry2_filled else 1/3

    def tp_r(self, price: float) -> float:
        return abs(price - self.entry_avg) / self.sl_dist if self.sl_dist else 0.0

    @property
    def r_tp1(self) -> float: return self.tp_r(self.tp1)
    @property
    def r_tp2(self) -> float: return self.tp_r(self.tp2)
    @property
    def r_tp3(self) -> float: return self.tp_r(self.tp3)

    def status_str(self) -> str:
        parts = []
        if self.tp1_hit: parts.append("TP1")
        if self.tp2_hit: parts.append("TP2")
        if self.tp3_hit: parts.append("TP3")
        if self.sl_hit:  parts.append("SL")
        return "+".join(parts) if parts else "OPEN"

    @property
    def has_outcome(self) -> bool:
        return self.tp1_hit or self.sl_hit


# ---------------------------------------------------------------------------
# Стратегии выхода
# ---------------------------------------------------------------------------
#
# Что известно из истории:
#   tp1_hit / tp2_hit / tp3_hit — цена достигала этого уровня
#   sl_hit                      — цена вернулась к стопу
#
# Допущение для BE-стратегий:
#   tp1_hit=True, tp2_hit=False, sl_hit=True  → откат после TP1 до SL
#     → BE-стоп сработал бы, остаток = 0R (не -1R)
#   tp1_hit=True, tp2_hit=True               → цена прошла мимо BE
#     → BE-стоп не задет, держим дальше
# ---------------------------------------------------------------------------

ExitFn = Callable[["Trade"], Optional[float]]


def sim_as_is(t: Trade) -> Optional[float]:
    """Канал: TP1/TP2/TP3 по 33%, SL на остаток."""
    if not t.has_outcome: return None
    ef = t.entry_fill
    r, closed = 0.0, 0.0
    for hit, price, frac in [(t.tp1_hit, t.tp1, 1/3), (t.tp2_hit, t.tp2, 1/3), (t.tp3_hit, t.tp3, 1/3)]:
        if hit:
            r += t.tp_r(price) * frac * ef
            closed += frac
    if t.sl_hit:
        r -= 1.0 * (1.0 - closed) * ef
    return r


def sim_hold_tp1(t: Trade) -> Optional[float]:
    """Вся позиция → TP1 или SL."""
    if not t.has_outcome: return None
    ef = t.entry_fill
    return t.r_tp1 * ef if t.tp1_hit else -1.0 * ef


def sim_hold_tp2(t: Trade) -> Optional[float]:
    """Вся позиция → TP2 или SL."""
    if not t.has_outcome: return None
    ef = t.entry_fill
    return t.r_tp2 * ef if t.tp2_hit else -1.0 * ef


def sim_hold_tp3(t: Trade) -> Optional[float]:
    """Вся позиция → TP3 или SL."""
    if not t.has_outcome: return None
    ef = t.entry_fill
    return t.r_tp3 * ef if t.tp3_hit else -1.0 * ef


def sim_be_after_tp1_hold_tp3(t: Trade) -> Optional[float]:
    """
    TP1 (33%) зафиксировать, SL → BE, остаток (67%) держать до TP3.
    Если откат после TP1 — остаток закрывается в BE (0R, не убыток).
    """
    if not t.has_outcome: return None
    ef = t.entry_fill
    if not t.tp1_hit:
        return -1.0 * ef
    r = t.r_tp1 * (1/3) * ef
    if t.tp3_hit:
        r += t.r_tp3 * (2/3) * ef
    elif t.tp2_hit:
        r += t.r_tp2 * (1/3) * ef
        # последняя треть: BE = +0
    # else: откат после TP1 → BE на оставшиеся 2/3 = +0
    return r


def sim_be_after_tp1_hold_tp2(t: Trade) -> Optional[float]:
    """
    TP1 (33%) зафиксировать, SL → BE, остаток (67%) держать до TP2.
    """
    if not t.has_outcome: return None
    ef = t.entry_fill
    if not t.tp1_hit:
        return -1.0 * ef
    r = t.r_tp1 * (1/3) * ef
    if t.tp2_hit:
        r += t.r_tp2 * (2/3) * ef
    # else: откат → BE на оставшиеся 2/3 = +0
    return r


def sim_half_tp1_be_tp3(t: Trade) -> Optional[float]:
    """
    50% позиции → TP1, SL → BE, вторые 50% → TP3.
    Самый популярный классический подход.
    """
    if not t.has_outcome: return None
    ef = t.entry_fill
    if not t.tp1_hit:
        return -1.0 * ef
    r = t.r_tp1 * 0.5 * ef
    if t.tp3_hit:
        r += t.r_tp3 * 0.5 * ef
    elif t.tp2_hit:
        r += t.r_tp2 * 0.25 * ef
        # последняя четверть: BE = +0
    # else: BE на вторую половину = +0
    return r


def sim_tp1_tp2_be_tp3(t: Trade) -> Optional[float]:
    """
    TP1(33%) + TP2(33%) фиксируем, после TP2 SL → BE, остаток → TP3.
    Улучшение канальной стратегии: убираем риск отдать прибыль после TP2.
    """
    if not t.has_outcome: return None
    ef = t.entry_fill
    if not t.tp1_hit:
        return -1.0 * ef
    r = t.r_tp1 * (1/3) * ef
    if not t.tp2_hit:
        # откат после TP1 перед TP2 — BE на оставшиеся 2/3 = +0
        return r
    r += t.r_tp2 * (1/3) * ef
    if t.tp3_hit:
        r += t.r_tp3 * (1/3) * ef
    # else: BE на последнюю треть = +0
    return r


def sim_full_tp1(t: Trade) -> Optional[float]:
    """Вся позиция закрывается на TP1. Без трейла."""
    if not t.has_outcome: return None
    ef = t.entry_fill
    return t.r_tp1 * ef if t.tp1_hit else -1.0 * ef


STRATEGIES: list[tuple[str, str, ExitFn]] = [
    ("AS_IS",          "Канал: TP1+TP2+TP3 по 33%",         sim_as_is),
    ("HOLD_TP1",       "Всё → TP1",                          sim_hold_tp1),
    ("HOLD_TP2",       "Всё → TP2",                          sim_hold_tp2),
    ("HOLD_TP3",       "Всё → TP3",                          sim_hold_tp3),
    ("BE@TP1→TP3",     "TP1(33%) + BE → TP3",               sim_be_after_tp1_hold_tp3),
    ("BE@TP1→TP2",     "TP1(33%) + BE → TP2",               sim_be_after_tp1_hold_tp2),
    ("50%TP1+BE→TP3",  "50%→TP1 + BE → TP3",               sim_half_tp1_be_tp3),
    ("TP1+TP2+BE→TP3", "TP1+TP2(33% each)+BE→TP3",          sim_tp1_tp2_be_tp3),
    ("FULL_TP1",       "Всё → TP1 (без продолжения)",       sim_full_tp1),
]


# ---------------------------------------------------------------------------
# Метрики
# ---------------------------------------------------------------------------

@dataclass
class Stats:
    name:     str
    label:    str
    n:        int
    total_r:  float
    win_rate: float
    pf:       float
    avg_win:  float
    avg_loss: float
    max_dd:   float

    def pf_str(self) -> str:
        return f"{self.pf:.2f}" if self.pf < 999 else "∞"


def calc_stats(name: str, label: str, pnl: list[float]) -> Stats:
    if not pnl:
        return Stats(name, label, 0, 0, 0, 0, 0, 0, 0)
    wins   = [r for r in pnl if r > 0]
    losses = [r for r in pnl if r <= 0]
    total_r  = sum(pnl)
    win_rate = len(wins) / len(pnl) * 100
    pf       = abs(sum(wins) / sum(losses)) if losses and sum(losses) != 0 else 999.0
    avg_win  = sum(wins)   / len(wins)   if wins   else 0.0
    avg_loss = sum(losses) / len(losses) if losses else 0.0
    eq, peak, max_dd = 0.0, 0.0, 0.0
    for r in pnl:
        eq    += r
        peak   = max(peak, eq)
        max_dd = min(max_dd, eq - peak)
    return Stats(name, label, len(pnl), total_r, win_rate, pf, avg_win, avg_loss, max_dd)


def run_strategy(trades: list[Trade], fn: ExitFn) -> list[float]:
    return [r for t in trades if (r := fn(t)) is not None]


# ---------------------------------------------------------------------------
# Парсер
# ---------------------------------------------------------------------------

def parse_signal(text: str, ts: datetime) -> Optional[Trade]:
    try:
        m  = re.search(r"#(\w+USDT)\s+(\d+[mMhHdD])", text)
        d  = re.search(r"STATUS\s*:\s*(LONG|SHORT)", text, re.IGNORECASE)
        e1 = re.search(r"ENTRY1\s*[:\s]\s*([\d.]+)", text)
        e2 = re.search(r"ENTRY2\s*[:\s]\s*([\d.]+)", text)
        t1 = re.search(r"TP1\s*[:\s]\s*([\d.]+)", text)
        t2 = re.search(r"TP2\s*[:\s]\s*([\d.]+)", text)
        t3 = re.search(r"TP3\s*[:\s]\s*([\d.]+)", text)
        sl = re.search(r"\bSL\s*[:\s]\s*([\d.]+)", text)
        lv = re.search(r"LEVERAGE\s*:\s*\w+\s*(\d+)x", text, re.IGNORECASE)
        if not all([m, d, e1, e2, t1, t2, t3, sl]): return None
        return Trade(
            symbol=m.group(1), direction=d.group(1).upper(),
            entry1=float(e1.group(1)), entry2=float(e2.group(1)),
            tp1=float(t1.group(1)), tp2=float(t2.group(1)), tp3=float(t3.group(1)),
            sl=float(sl.group(1)), leverage=int(lv.group(1)) if lv else 20,
            opened_at=ts,
        )
    except Exception:
        return None


def apply_update(text: str, ts: datetime, open_trades: dict, closed: list):
    sym_m = re.search(r"#(\w+USDT)", text)
    if not sym_m: return
    sym   = sym_m.group(1)
    trade = open_trades.get(sym)
    if not trade: return
    T = text.upper()

    # OPEN ENTRY2
    if "OPEN ENTRY2" in T:
        trade.entry2_filled = True
        return

    # TP N ✅ позиция закрыта  /  TP N ✅ в бу
    tp_m = re.search(r"\bTP\s*(\d)\b", text)
    if tp_m and "✅" in text:
        n = int(tp_m.group(1))
        if n >= 1: trade.tp1_hit = True
        if n >= 2: trade.tp2_hit = True
        if n >= 3:
            trade.tp3_hit   = True
            trade.closed_at = ts
            closed.append(trade)
            del open_trades[sym]
        return

    # SL ⛔ позиция закрыта по стопу
    if re.search(r"\bSL\b", text) and ("🔴" in text or "❌" in text or "⛔" in text or "СТОП" in T):
        trade.sl_hit    = True
        trade.closed_at = ts
        closed.append(trade)
        del open_trades[sym]
        return

    if "STOP HIT" in T:
        trade.sl_hit    = True
        trade.closed_at = ts
        closed.append(trade)
        del open_trades[sym]


def reconstruct(messages: list[dict]) -> list[Trade]:
    open_t: dict[str, Trade] = {}
    closed: list[Trade]      = []
    for msg in messages:
        text = msg.get("text", "")
        if not text: continue
        ts = datetime.fromisoformat(msg["date"])
        if "STATUS" in text and "ENTRY1" in text and "SL" in text:
            t = parse_signal(text, ts)
            if t:
                if t.symbol in open_t:
                    closed.append(open_t[t.symbol])
                open_t[t.symbol] = t
        else:
            apply_update(text, ts, open_t, closed)
    closed.extend(open_t.values())
    return closed


# ---------------------------------------------------------------------------
# Загрузка из Telegram
# ---------------------------------------------------------------------------

async def fetch_history(days: int, limit: int) -> list[dict]:
    from telethon import TelegramClient
    from telethon.tl.types import Message
    client = TelegramClient(TG_SESSION_FILE, TG_API_ID, TG_API_HASH)
    await client.start()
    print(f"Fetching @{SIGNAL_CHANNEL} ({days}d, max {limit})...")
    cutoff = datetime.now(timezone.utc) - timedelta(days=days)
    msgs = []
    async for msg in client.iter_messages(SIGNAL_CHANNEL, limit=limit):
        if not isinstance(msg, Message): continue
        if msg.date < cutoff: break
        msgs.append({"id": msg.id, "date": msg.date.isoformat(), "text": msg.message or ""})
    await client.disconnect()
    msgs.reverse()
    Path(CACHE_FILE).write_text(json.dumps(msgs, ensure_ascii=False, indent=2), encoding="utf-8")
    print(f"Saved {len(msgs)} → {CACHE_FILE}")
    return msgs


# ---------------------------------------------------------------------------
# Вывод
# ---------------------------------------------------------------------------

W = 88
SEP  = "─" * W
SEP2 = "═" * W

def fmt(r: float, w: int = 7) -> str:
    return f"{r:+.2f}R".rjust(w)


def print_outcome_patterns(trades: list[Trade]):
    closed = [t for t in trades if t.has_outcome]
    if not closed: return
    total = len(closed)
    patterns: dict[str, int] = defaultdict(int)
    for t in closed:
        patterns[t.status_str()] += 1

    print(f"\n{SEP2}")
    print(f"  ПАТТЕРНЫ ИСХОДОВ  (всего закрытых: {total})")
    print(SEP2)
    for outcome, count in sorted(patterns.items(), key=lambda x: -x[1]):
        bar = "█" * int(count / total * 40)
        print(f"  {outcome:<22}  {count:>4}  {count/total*100:>5.1f}%  {bar}")

    tp1_t = [t for t in closed if t.tp1_hit]
    if tp1_t:
        n = len(tp1_t)
        n2 = sum(1 for t in tp1_t if t.tp2_hit)
        n3 = sum(1 for t in tp1_t if t.tp3_hit)
        ns = sum(1 for t in tp1_t if t.sl_hit)
        print(f"\n  После достижения TP1 ({n} сделок):")
        print(f"    → дошли до TP2  {n2:>4}  ({n2/n*100:.1f}%)")
        print(f"    → дошли до TP3  {n3:>4}  ({n3/n*100:.1f}%)")
        print(f"    → откат к SL    {ns:>4}  ({ns/n*100:.1f}%)")
        print(f"\n  BE-стоп после TP1 конвертирует {ns/n*100:.1f}% убыточных сделок в ноль")
    print()


def print_strategy_table(stats_list: list[Stats], title: str):
    ranked = sorted(stats_list, key=lambda s: s.pf, reverse=True)
    print(f"\n{SEP2}")
    print(f"  {title}")
    print(SEP2)
    print(f"  {'':2} {'Стратегия':<18} {'Описание':<30} {'N':>4} {'TotalR':>8} {'WR%':>6} {'PF':>6} {'AvgW':>7} {'AvgL':>7} {'MaxDD':>7}")
    print(f"  {SEP}")
    medals = ["🥇", "🥈", "🥉"]
    for i, s in enumerate(ranked):
        m = medals[i] if i < 3 else "  "
        print(
            f"  {m} {s.name:<18} {s.label:<30} {s.n:>4}"
            f" {fmt(s.total_r):>8} {s.win_rate:>5.1f}% {s.pf_str():>6}"
            f" {fmt(s.avg_win):>7} {fmt(s.avg_loss):>7} {fmt(s.max_dd):>7}"
        )
    print()


def print_per_symbol(trades: list[Trade]):
    by_sym: dict[str, list[Trade]] = defaultdict(list)
    for t in trades:
        if t.has_outcome:
            by_sym[t.symbol].append(t)

    print(f"\n{SEP2}")
    print(f"  PER-SYMBOL — лучшая vs худшая стратегия выхода")
    print(SEP2)
    print(f"  {'Символ':<12} {'N':>3}  {'Лучшая':<18} {'PF':>6} {'TotalR':>8} {'WR%':>6}  {'Худшая':<18} {'PF':>6}")
    print(f"  {SEP}")

    rows = []
    for sym, sym_trades in by_sym.items():
        if len(sym_trades) < 3: continue
        all_stats = []
        for name, label, fn in STRATEGIES:
            pnl = run_strategy(sym_trades, fn)
            if pnl:
                all_stats.append(calc_stats(name, label, pnl))
        if not all_stats: continue
        best  = max(all_stats, key=lambda s: s.pf)
        worst = min(all_stats, key=lambda s: s.pf)
        rows.append((sym, len(sym_trades), best, worst))

    for sym, n, best, worst in sorted(rows, key=lambda x: -x[1]):
        print(
            f"  {sym:<12} {n:>3}  {best.name:<18} {best.pf_str():>6}"
            f" {fmt(best.total_r):>8} {best.win_rate:>5.1f}%"
            f"  {worst.name:<18} {worst.pf_str():>6}"
        )
    print()


def run_analysis(messages: list[dict], detail_sym: Optional[str] = None):
    trades = reconstruct(messages)
    closed = [t for t in trades if t.has_outcome]

    print(f"\n  Всего сигналов  : {len(trades)}")
    print(f"  Закрытых        : {len(closed)}")
    print(f"  Ещё открытых    : {len(trades) - len(closed)}")

    print_outcome_patterns(trades)

    # Общий рейтинг
    all_stats = []
    for name, label, fn in STRATEGIES:
        pnl = run_strategy(closed, fn)
        if pnl:
            all_stats.append(calc_stats(name, label, pnl))
    print_strategy_table(all_stats, "ОБЩИЙ РЕЙТИНГ СТРАТЕГИЙ ВЫХОДА")

    # Per-symbol
    print_per_symbol(trades)

    # Детальный символ
    if detail_sym:
        sym = detail_sym.upper()
        if not sym.endswith("USDT"): sym += "USDT"
        sym_trades = [t for t in trades if t.symbol == sym and t.has_outcome]
        if sym_trades:
            sym_stats = []
            for name, label, fn in STRATEGIES:
                pnl = run_strategy(sym_trades, fn)
                if pnl:
                    sym_stats.append(calc_stats(name, label, pnl))
            print_strategy_table(sym_stats, f"ДЕТАЛЬНЫЙ АНАЛИЗ: {sym}  ({len(sym_trades)} сделок)")
        else:
            print(f"\n  Нет данных по {sym}")


# ---------------------------------------------------------------------------
# Точка входа
# ---------------------------------------------------------------------------

async def async_main(args):
    if args.no_fetch and Path(CACHE_FILE).exists():
        messages = json.loads(Path(CACHE_FILE).read_text(encoding="utf-8"))
        print(f"Cache: {len(messages)} messages")
    else:
        messages = await fetch_history(days=args.days, limit=args.limit)
    run_analysis(messages, detail_sym=args.symbol)


if __name__ == "__main__":
    p = argparse.ArgumentParser()
    p.add_argument("--days",     type=int,  default=30)
    p.add_argument("--limit",    type=int,  default=2000)
    p.add_argument("--no-fetch", action="store_true")
    p.add_argument("--symbol",   type=str,  default=None)
    asyncio.run(async_main(p.parse_args()))
