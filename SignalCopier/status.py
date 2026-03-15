#!/usr/bin/env python3
"""
Мониторинг позиций из positions_state.json
Запуск: python status.py          # обновление при изменении файла
        python status.py --once   # разовый вывод
"""
import json, sys, os, time
from datetime import datetime

STATE_FILE = os.environ.get("STATE_FILE", "positions_state.json")
ONCE       = "--once" in sys.argv


def render():
    if not os.path.exists(STATE_FILE):
        print(f"Нет файла {STATE_FILE} — позиций нет.")
        return

    try:
        data = json.load(open(STATE_FILE, encoding="utf-8"))
    except Exception as e:
        print(f"Ошибка чтения {STATE_FILE}: {e}")
        return

    if not data:
        print("Позиций нет.")
        return

    W = 75
    print("─" * W)
    print(f"  {'SYMBOL':<12} {'DIR':<6} {'ENTRY1':>8} {'ENTRY2':>10} {'SL':>10} {'TP1':>10}  FLAGS")
    print("─" * W)

    for sym, pos in sorted(data.items()):
        sig       = pos.get("signal", {})
        direction = sig.get("direction", "?")
        entry1    = sig.get("entry1", 0)
        entry2    = sig.get("entry2") or 0
        sl        = sig.get("sl", 0)
        tp1       = sig.get("tp1", 0)
        tp_ids    = pos.get("tp_order_ids", [])
        e2_placed = pos.get("entry2_placed", False)
        entry_ids = pos.get("entry_order_ids", [])

        flags = []
        if not pos.get("sl_order_id"):  flags.append("⚠ NO SL")
        if not tp_ids:                  flags.append("⚠ NO TP")
        if e2_placed:                   flags.append("E2✓")
        elif entry2:                    flags.append("E2 pending")
        if len(entry_ids) > 1:         flags.append(f"entries={len(entry_ids)}")

        e2_str = f"{entry2:>10g}" if entry2 else f"{'—':>10}"
        print(f"  {sym:<12} {direction:<6} {entry1:>8g} {e2_str} {sl:>10g} {tp1:>10g}  {'  '.join(flags)}")

    print("─" * W)
    print(f"  Итого: {len(data)} позиций ")
    print("─" * W)


if ONCE:
    render()
else:
    last_mtime = None
    while True:
        try:
            mtime = os.path.getmtime(STATE_FILE) if os.path.exists(STATE_FILE) else None
        except Exception:
            mtime = None

        if mtime != last_mtime:
            os.system("cls" if os.name == "nt" else "clear")
            render()
            last_mtime = mtime

        time.sleep(1)
