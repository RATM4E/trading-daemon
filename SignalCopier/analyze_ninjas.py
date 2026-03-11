"""
CryptoNinjas Trading — анализатор истории
==========================================
Скачивает историю канала, восстанавливает сделки по парам
сигнал → обновления → исход, считает статистику по R.

Запуск:
    python analyze_ninjas.py                    # последние 60 дней
    python analyze_ninjas.py --days 90
    python analyze_ninjas.py --no-fetch         # из кэша
    python analyze_ninjas.py --symbol LIT       # детально по символу
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
TG_SESSION_FILE = os.environ.get("TG_SESSION", "scan_session")
CHANNEL         = "cryptoninjas_trading_ann"
CACHE_FILE      = "history_cache_ninjas.json"


# ---------------------------------------------------------------------------
# Модель сигнала
# ---------------------------------------------------------------------------

@dataclass
class Signal:
    msg_id:    int
    date:      datetime
    symbol:    str
    direction: str       # LONG / SHORT
    entry1:    float
    entry2:    Optional[float]
    sl:        float
    tp1:       Optional[float]
    tp2:       Optional[float]
    tp3:       Optional[float]
    risk_note: str       # "" / "RISK ORDER"

    # исходы
    closed_r:  Optional[float] = None   # итоговый R (из сообщений канала)
    sl_hit:    bool             = False
    be_close:  bool             = False  # закрыт в безубыток
    updates:   list             = field(default_factory=list)

    @property
    def sl_dist(self) -> float:
        return abs(self.entry1 - self.sl)

    @property
    def r_tp1(self) -> Optional[float]:
        if self.tp1 is None: return None
        return abs(self.tp1 - self.entry1) / self.sl_dist if self.sl_dist else None

    @property
    def r_tp2(self) -> Optional[float]:
        if self.tp2 is None: return None
        return abs(self.tp2 - self.entry1) / self.sl_dist if self.sl_dist else None

    @property
    def r_tp3(self) -> Optional[float]:
        if self.tp3 is None: return None
        return abs(self.tp3 - self.entry1) / self.sl_dist if self.sl_dist else None

    @property
    def has_outcome(self) -> bool:
        return self.closed_r is not None or self.sl_hit or self.be_close


# ---------------------------------------------------------------------------
# Парсер
# ---------------------------------------------------------------------------

def parse_price(s: str) -> Optional[float]:
    """Парсит цену — удаляет запятые (1,938 → 1938), возвращает float."""
    s = s.replace(",", "").strip()
    try:
        return float(s)
    except ValueError:
        return None


def parse_signal(msg_id: int, date: datetime, text: str) -> Optional[Signal]:
    """Пытается распарсить сообщение как новый сигнал."""
    # Требуем 🟢/🔴 и LONG/SHORT в первой строке
    first = text.split("\n")[0]
    m = re.match(r'[🟢🔴]\s*(?:SWING\s+)?(LONG|SHORT)\b', first, re.I)
    if not m:
        return None

    direction = m.group(1).upper()

    # Символ: $SYMBOL в первой строке
    sym_m = re.search(r'\$([A-Z0-9]+)', first, re.I)
    if not sym_m:
        return None
    symbol = sym_m.group(1).upper()

    # RISK ORDER пометка
    risk_note = "RISK ORDER" if "RISK ORDER" in first.upper() else ""

    # Entry1 — берём первую найденную entry строку
    # Варианты: Entry market, Entry 1, Entry limit 1, Entry:, Entry (now):
    entry1 = None
    entry2 = None

    lines = text.split("\n")

    for line in lines:
        l = line.strip().lstrip("-").strip()
        # Entry market / Entry (now) / Entry:
        m2 = re.match(r'Entry(?:\s+market)?(?:\s*\(now\))?:\s*([\d.,]+)', l, re.I)
        if m2 and entry1 is None:
            entry1 = parse_price(m2.group(1))
            continue
        # Entry 1 / Entry limit 1
        m2 = re.match(r'Entry(?:\s+(?:limit\s+)?1)?:\s*([\d.,]+)', l, re.I)
        if m2 and entry1 is None:
            entry1 = parse_price(m2.group(1))
            continue
        # Entry 2 / Entry limit / Entry limit 2
        m2 = re.match(r'Entry(?:\s+(?:limit\s+)?2|limit)(?:\s*\(.*?\))?:\s*([\d.,]+)', l, re.I)
        if m2 and entry2 is None:
            entry2 = parse_price(m2.group(1))
            continue
        # Limit entry
        m2 = re.match(r'Limit\s+entry\s*[:–-]\s*([\d.,]+)', l, re.I)
        if m2 and entry2 is None:
            entry2 = parse_price(m2.group(1))
            continue

    # SL
    sl = None
    for line in lines:
        l = line.strip().lstrip("-").strip()
        m2 = re.match(r'(?:SL|Stop\s*Loss)\s*[:=]\s*([\d.,]+)', l, re.I)
        if m2:
            sl = parse_price(m2.group(1))
            break
        # ❌ SL: price
        m2 = re.match(r'❌\s*SL\s*[:=]?\s*([\d.,]+)', l, re.I)
        if m2:
            sl = parse_price(m2.group(1))
            break

    if entry1 is None or sl is None:
        return None
    if entry1 == sl:
        return None
    # Фильтр мусора: SL дальше 50% от entry
    if abs(entry1 - sl) / entry1 > 0.5:
        return None

    # TP
    tps = {}
    for line in lines:
        l = line.strip().lstrip("-").strip()
        m2 = re.match(r'(?:🎯\s*)?TP\s*(\d+)\s*[:=]\s*([\d.,]+)', l, re.I)
        if m2:
            n = int(m2.group(1))
            if n not in tps:
                tps[n] = parse_price(m2.group(2))

    # Особый формат: TP: price1 - price2 - price3
    if not tps:
        m2 = re.search(r'TP\s*:\s*([\d.,]+)\s*[-–]\s*([\d.,]+)(?:\s*[-–]\s*([\d.,]+))?', text, re.I)
        if m2:
            tps[1] = parse_price(m2.group(1))
            tps[2] = parse_price(m2.group(2))
            if m2.group(3): tps[3] = parse_price(m2.group(3))

    return Signal(
        msg_id    = msg_id,
        date      = date,
        symbol    = symbol,
        direction = direction,
        entry1    = entry1,
        entry2    = entry2,
        sl        = sl,
        tp1       = tps.get(1),
        tp2       = tps.get(2),
        tp3       = tps.get(3),
        risk_note = risk_note,
    )


# Паттерны обновлений
RE_PLUS_R   = re.compile(r'\+\s*([\d.]+)\s*R\b', re.I)
RE_SL_SWEEP = re.compile(r'sweep\s*sl|sl.*hit|hit.*sl|sl\s+hit', re.I)
RE_BE_CLOSE = re.compile(r'\bBE\b|break.?even|close.*entry.*\bBE\b|move.*sl.*entry', re.I)
RE_CANCEL   = re.compile(r'\bcancel\b|\binvalidated\b', re.I)


def apply_update(sig: Signal, text: str):
    """Применяет обновление к сигналу."""
    sig.updates.append(text[:120])

    # SL sweep
    if RE_SL_SWEEP.search(text):
        sig.sl_hit = True
        return

    # отменён
    if RE_CANCEL.search(text) and "entry" in text.lower():
        sig.be_close = True
        return

    # +R — берём максимальный
    rs = [float(m) for m in RE_PLUS_R.findall(text)]
    if rs:
        best = max(rs)
        if sig.closed_r is None or best > sig.closed_r:
            sig.closed_r = best

    # BE / move sl to entry — если нет явного R
    if not rs and RE_BE_CLOSE.search(text) and "entry" in text.lower():
        if sig.closed_r is None:
            sig.be_close = True


# ---------------------------------------------------------------------------
# Фетч истории
# ---------------------------------------------------------------------------

async def fetch_history(days: int, limit: int, session: str = None) -> list[dict]:
    from telethon import TelegramClient
    client = TelegramClient(session or TG_SESSION_FILE, TG_API_ID, TG_API_HASH)
    await client.start()

    after = datetime.now(timezone.utc) - timedelta(days=days)
    messages = []

    async for msg in client.iter_messages(CHANNEL, limit=limit):
        if msg.date < after:
            break
        if msg.raw_text:
            messages.append({
                "id":   msg.id,
                "date": msg.date.isoformat(),
                "text": msg.raw_text,
            })

    await client.disconnect()
    messages.reverse()
    Path(CACHE_FILE).write_text(json.dumps(messages, ensure_ascii=False, indent=2), encoding="utf-8")
    print(f"Скачано: {len(messages)} сообщений → {CACHE_FILE}")
    return messages


# ---------------------------------------------------------------------------
# Реконструкция сделок
# ---------------------------------------------------------------------------

def reconstruct(messages: list[dict]) -> list[Signal]:
    """
    Проходим по сообщениям хронологически.
    Открытые позиции хранятся в словаре по символу.
    Обновления матчим по имени символа в начале текста.
    """
    open_signals: dict[str, Signal] = {}
    closed: list[Signal] = []

    for msg in messages:
        mid  = msg["id"]
        date = datetime.fromisoformat(msg["date"])
        text = msg["text"]

        # Пробуем распарсить как новый сигнал
        sig = parse_signal(mid, date, text)
        if sig:
            # Если уже есть открытая позиция по этому символу — закрываем её
            if sig.symbol in open_signals:
                closed.append(open_signals[sig.symbol])
            open_signals[sig.symbol] = sig
            continue

        # Иначе — обновление. Ищем символ в начале текста
        t = text.strip()
        sym_m = re.match(r'^([A-Z0-9$]{2,20})\b', t.upper())
        if not sym_m:
            continue

        sym = sym_m.group(1).lstrip("$")
        if sym not in open_signals:
            continue

        apply_update(open_signals[sym], t)

        # Если явное финальное закрытие — убираем из open
        lower = t.lower()
        is_final = (
            bool(RE_SL_SWEEP.search(t)) or
            bool(RE_CANCEL.search(t)) or
            ("hit full tp" in lower) or
            ("full tp" in lower) or
            ("full target" in lower) or
            (bool(RE_PLUS_R.search(t)) and ("close" in lower or "take profit" in lower or "done" in lower))
        )
        if is_final:
            closed.append(open_signals.pop(sym))

    # Всё что осталось открытым
    all_signals = closed + list(open_signals.values())
    all_signals.sort(key=lambda s: s.date)
    return all_signals


# ---------------------------------------------------------------------------
# Статистика
# ---------------------------------------------------------------------------

def calc_stats(signals: list[Signal]) -> dict:
    total  = len(signals)
    closed = [s for s in signals if s.has_outcome]
    wins   = [s for s in closed if s.closed_r and s.closed_r > 0]
    losses = [s for s in closed if s.sl_hit and not s.closed_r]
    be     = [s for s in closed if s.be_close and not s.sl_hit and not s.closed_r]
    open_  = [s for s in signals if not s.has_outcome]

    r_vals = [s.closed_r for s in wins]
    total_r = sum(r_vals) - len(losses)  # каждый SL = -1R
    win_r   = sum(r_vals)
    loss_r  = float(len(losses))
    pf      = win_r / loss_r if loss_r > 0 else float("inf")
    wr      = len(wins) / len(closed) * 100 if closed else 0

    return {
        "total": total, "closed": len(closed), "open": len(open_),
        "wins": len(wins), "losses": len(losses), "be": len(be),
        "wr": wr, "pf": pf,
        "total_r": total_r, "win_r": win_r, "loss_r": loss_r,
        "avg_r": sum(r_vals)/len(r_vals) if r_vals else 0,
        "median_r": sorted(r_vals)[len(r_vals)//2] if r_vals else 0,
        "max_r": max(r_vals) if r_vals else 0,
        "r_vals": r_vals,
    }


def print_stats(stats: dict, title: str = "СТАТИСТИКА"):
    s = stats
    print(f"\n{'─'*60}")
    print(f"  {title}")
    print(f"{'─'*60}")
    print(f"  Всего сигналов : {s['total']}   закрыто: {s['closed']}   ещё открыто: {s['open']}")
    print(f"  Побед          : {s['wins']}   SL: {s['losses']}   BE: {s['be']}")
    print(f"  Win Rate       : {s['wr']:.1f}%")
    print(f"  Profit Factor  : {s['pf']:.2f}" + (" (нет убытков)" if s['loss_r'] == 0 else ""))
    print(f"  Итого R        : {s['total_r']:+.1f}R")
    print(f"  Средн R (вин)  : {s['avg_r']:.2f}R")
    print(f"  Медиана R      : {s['median_r']:.2f}R")
    print(f"  Макс R         : {s['max_r']:.1f}R")

    if s['r_vals']:
        rv = s['r_vals']
        buckets = [('<1R', lambda r: r < 1), ('1-2R', lambda r: 1 <= r < 2),
                   ('2-3R', lambda r: 2 <= r < 3), ('3-5R', lambda r: 3 <= r < 5),
                   ('>5R',  lambda r: r >= 5)]
        print(f"\n  Распределение R побед:")
        for label, fn in buckets:
            cnt = sum(1 for r in rv if fn(r))
            bar = "█" * int(cnt / max(len(rv), 1) * 30)
            print(f"    {label:6}  {cnt:3d} ({100*cnt/len(rv):4.0f}%)  {bar}")


def print_signals_table(signals: list[Signal]):
    print(f"\n{'─'*90}")
    print(f"  {'SYMBOL':12} {'DIR':6} {'DATE':12} {'ENTRY':10} {'SL':10} {'R_TP1':6} {'R_TP3':6} {'RESULT':15} {'NOTE'}")
    print(f"{'─'*90}")
    for s in sorted(signals, key=lambda x: x.date):
        r_tp1 = f"{s.r_tp1:.2f}R" if s.r_tp1 else "  —  "
        r_tp3 = f"{s.r_tp3:.2f}R" if s.r_tp3 else "  —  "
        if s.closed_r:
            result = f"+{s.closed_r:.1f}R"
        elif s.sl_hit:
            result = "-1R (SL)"
        elif s.be_close:
            result = "BE"
        else:
            result = "OPEN"
        date_s = s.date.strftime("%m-%d %H:%M")
        print(f"  {s.symbol:12} {s.direction:6} {date_s:12} {s.entry1:<10g} {s.sl:<10g} {r_tp1:6} {r_tp3:6} {result:15} {s.risk_note}")


# ---------------------------------------------------------------------------
# Точка входа
# ---------------------------------------------------------------------------

async def async_main(args):
    if args.no_fetch and Path(CACHE_FILE).exists():
        messages = json.loads(Path(CACHE_FILE).read_text(encoding="utf-8"))
        print(f"Кэш: {len(messages)} сообщений")
    else:
        messages = await fetch_history(days=args.days, limit=args.limit, session=args.session)

    signals = reconstruct(messages)

    if args.symbol:
        sym = args.symbol.upper().lstrip("$")
        signals = [s for s in signals if s.symbol == sym]
        if not signals:
            print(f"\nНет данных по {sym}")
            return

    print_signals_table(signals)

    stats = calc_stats(signals)
    print_stats(stats, f"ИТОГО ({len(signals)} сигналов)")

    # LONG vs SHORT
    longs  = [s for s in signals if s.direction == "LONG"]
    shorts = [s for s in signals if s.direction == "SHORT"]
    if longs and shorts:
        print_stats(calc_stats(longs),  f"LONG ({len(longs)})")
        print_stats(calc_stats(shorts), f"SHORT ({len(shorts)})")

    # RISK ORDER vs обычные
    risk    = [s for s in signals if s.risk_note]
    normal  = [s for s in signals if not s.risk_note]
    if risk and normal:
        print_stats(calc_stats(risk),   f"RISK ORDER ({len(risk)})")
        print_stats(calc_stats(normal), f"NORMAL ({len(normal)})")


if __name__ == "__main__":
    p = argparse.ArgumentParser()
    p.add_argument("--days",     type=int,  default=60)
    p.add_argument("--limit",    type=int,  default=1000)
    p.add_argument("--no-fetch", action="store_true")
    p.add_argument("--session",  type=str, default=None, help="TG session file")
    p.add_argument("--symbol",   type=str,  default=None)
    asyncio.run(async_main(p.parse_args()))
