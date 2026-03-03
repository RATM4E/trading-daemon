"""
Полный тест жизненного цикла ордера через MT5 Worker:
1. Показать текущие позиции
2. Открыть позицию (с SL/TP)
3. Модифицировать SL/TP (может не поддерживаться некоторыми брокерами)
4. Закрыть позицию
5. Проверить историю сделок

Запуск:
    python test_orders.py --port 5501 --symbol BTCUSD
    python test_orders.py --port 5501 --symbol BTCUSD --close-only
"""

import argparse
import json
import socket
import sys
import time


class Client:
    def __init__(self, host="127.0.0.1", port=5501):
        self.sock = socket.socket()
        self.sock.settimeout(10)
        self.sock.connect((host, port))
        self.file = self.sock.makefile("r", encoding="utf-8")
        self._id = 0

    def send(self, cmd, **kwargs):
        self._id += 1
        msg = {"cmd": cmd, "id": self._id, **kwargs}
        line = json.dumps(msg) + "\n"
        self.sock.sendall(line.encode())
        resp_line = self.file.readline()
        if not resp_line:
            raise ConnectionError("Worker closed connection")
        return json.loads(resp_line)

    def close(self):
        self.file.close()
        self.sock.close()


def show_positions(c, label="Current positions"):
    resp = c.send("GET_POSITIONS")
    print(f"\n--- {label} ---")
    if resp.get("status") != "ok":
        print(f"  Error: {resp}")
        return []
    positions = resp["data"]
    if not positions:
        print("  (none)")
    for p in positions:
        print(f"  #{p['ticket']} {p['type']} {p['symbol']} {p['volume']} "
              f"@ {p['price_open']}  SL={p['sl']}  TP={p['tp']}  "
              f"P/L={p['profit']}  magic={p['magic']}")
    return positions


def close_test_positions(c):
    """Закрыть все позиции с magic=999999."""
    positions = show_positions(c, "Before close-all")
    test_pos = [p for p in positions if p.get("magic") == 999999]

    if not test_pos:
        print("\n  No test positions (magic=999999) to close.")
        return

    for p in test_pos:
        close_type = 1 if p["type"] == "BUY" else 0
        tick_resp = c.send("GET_RATES", symbol=p["symbol"], timeframe="M1", count=1)
        price = tick_resp["data"][-1]["close"] if tick_resp.get("status") == "ok" and tick_resp["data"] else 0

        print(f"\n  Closing #{p['ticket']} {p['type']} {p['symbol']} {p['volume']}...")
        resp = c.send("ORDER_SEND", request={
            "action": 1, "symbol": p["symbol"], "volume": p["volume"],
            "type": close_type, "price": price, "deviation": 50,
            "position": p["ticket"], "magic": 999999,
            "comment": "test_close", "type_filling": 1, "type_time": 0,
        })
        if resp.get("status") == "ok":
            print(f"  ✓ Closed @ {resp['data'].get('price')} deal={resp['data'].get('deal')}")
        else:
            print(f"  ✗ {resp.get('message', resp.get('code', resp))}")

    time.sleep(0.5)
    show_positions(c, "After close-all")


def full_lifecycle_test(c, symbol):
    """Полный цикл: open → verify → modify → verify → close → verify → history."""
    print("\n" + "=" * 60)
    print(f"FULL LIFECYCLE TEST: {symbol}")
    print("=" * 60)

    # --- Шаг 0: Инфо по символу ---
    resp = c.send("SYMBOL_INFO", symbol=symbol)
    if resp.get("status") != "ok":
        print(f"✗ Symbol not found: {resp}")
        return
    si = resp["data"]
    point = si["point"]
    digits = si["digits"]
    vol_min = si["volume_min"]
    print(f"\n  {symbol}: digits={digits}, point={point}, vol_min={vol_min}, "
          f"stops_level={si['trade_stops_level']}")

    resp = c.send("GET_RATES", symbol=symbol, timeframe="M1", count=1)
    if resp.get("status") != "ok" or not resp["data"]:
        print(f"✗ Can't get price: {resp}")
        return
    price = resp["data"][-1]["close"]
    print(f"  Current price: {price}")

    # Адаптивная дистанция SL/TP
    if price > 100:
        dist = round(price * 0.01, digits)   # 1% для крипты/индексов
    else:
        dist = round(500 * point, digits)     # 50 pips для форекса
    print(f"  SL/TP distance: {dist}")

    show_positions(c, "Before open")

    # --- Шаг 1: OPEN с SL/TP ---
    sl = round(price - dist, digits)
    tp = round(price + dist, digits)
    print(f"\n[1] OPEN BUY {symbol} {vol_min} lot, SL={sl}, TP={tp}")

    resp = c.send("ORDER_SEND", request={
        "action": 1, "symbol": symbol, "volume": vol_min,
        "type": 0, "price": price, "sl": sl, "tp": tp,
        "deviation": 50, "magic": 999999, "comment": "lifecycle_test",
        "type_filling": 1, "type_time": 0,
    })
    if resp.get("status") != "ok":
        print(f"  ✗ Open failed: {resp.get('message', resp.get('code'))}")
        return
    fill_price = resp["data"]["price"]
    print(f"  ✓ Filled @ {fill_price}, deal={resp['data']['deal']}")

    time.sleep(0.5)

    positions = show_positions(c, "After open")
    my_pos = [p for p in positions if p.get("magic") == 999999 and p["symbol"] == symbol]
    if not my_pos:
        print("  ✗ Position not found after open!")
        return
    ticket = my_pos[0]["ticket"]
    print(f"  Position ticket: {ticket}")

    # Проверить что SL/TP выставлены при открытии
    p = my_pos[0]
    sl_set = abs(p["sl"] - sl) < point * 2
    tp_set = abs(p["tp"] - tp) < point * 2
    print(f"  SL set at open: {'✓' if sl_set else '✗'} (expected {sl}, got {p['sl']})")
    print(f"  TP set at open: {'✓' if tp_set else '✗'} (expected {tp}, got {p['tp']})")

    # --- Шаг 2: MODIFY SL/TP ---
    new_sl = round(fill_price - dist * 0.5, digits)
    new_tp = round(fill_price + dist * 2, digits)
    print(f"\n[2] MODIFY #{ticket} -> SL={new_sl}, TP={new_tp}")

    modify_ok = False
    resp = c.send("ORDER_SEND", request={
        "action": 3, "symbol": symbol, "position": ticket,
        "sl": new_sl, "tp": new_tp,
    })
    if resp.get("status") != "ok":
        print(f"  ⚠ MODIFY not supported (code={resp.get('code')} {resp.get('message')})")
        print(f"    Normal for prop firms — daemon sets SL/TP at open time")
    else:
        modify_ok = True
        print(f"  ✓ Modified")
        time.sleep(0.5)
        positions = show_positions(c, "After modify")
        my_pos = [p for p in positions if p.get("ticket") == ticket]
        if my_pos:
            p = my_pos[0]
            print(f"  SL: {'✓' if abs(p['sl'] - new_sl) < point * 2 else '✗'} "
                  f"(expected {new_sl}, got {p['sl']})")
            print(f"  TP: {'✓' if abs(p['tp'] - new_tp) < point * 2 else '✗'} "
                  f"(expected {new_tp}, got {p['tp']})")

    # --- Шаг 3: CLOSE ---
    print(f"\n[3] CLOSE #{ticket}")

    resp = c.send("GET_RATES", symbol=symbol, timeframe="M1", count=1)
    close_price = resp["data"][-1]["close"] if resp.get("status") == "ok" and resp["data"] else 0

    resp = c.send("ORDER_SEND", request={
        "action": 1, "symbol": symbol, "volume": vol_min,
        "type": 1, "price": close_price, "deviation": 50,
        "position": ticket, "magic": 999999,
        "comment": "lifecycle_close", "type_filling": 1, "type_time": 0,
    })
    close_ok = resp.get("status") == "ok"
    if not close_ok:
        print(f"  ✗ Close failed: code={resp.get('code')} {resp.get('message')}")
    else:
        print(f"  ✓ Closed @ {resp['data']['price']}, deal={resp['data']['deal']}")

    time.sleep(0.5)
    remaining = show_positions(c, "After close")
    pos_gone = not any(p.get("ticket") == ticket for p in remaining)
    print(f"  Position gone: {'✓' if pos_gone else '✗'}")

    # --- Шаг 4: HISTORY ---
    print(f"\n[4] HISTORY CHECK")
    resp = c.send("HISTORY_DEALS", from_ts=0, to_ts=2000000000)
    history_ok = False
    if resp.get("status") == "ok":
        my_deals = [d for d in resp["data"] if d.get("magic") == 999999]
        recent = [d for d in my_deals if d.get("comment", "").startswith("lifecycle")]
        history_ok = len(recent) >= 2  # минимум IN + OUT
        print(f"  All test deals: {len(my_deals)}, lifecycle deals: {len(recent)}")
        for d in recent[-4:]:
            entry_str = {0: "IN", 1: "OUT", 2: "INOUT", 3: "OUT_BY"}.get(d["entry"], "?")
            print(f"    deal={d['ticket']} {entry_str} {d['symbol']} "
                  f"vol={d['volume']} @ {d['price']} P/L={d['profit']}")

    # --- Итог ---
    print(f"\n{'=' * 60}")
    print("RESULTS:")
    print(f"  OPEN with SL/TP:  {'✓' if sl_set and tp_set else '✗'}")
    modify_str = '✓' if modify_ok else '⚠ not supported (normal for prop firms)'
    print(f"  MODIFY SL/TP:     {modify_str}")
    print(f"  CLOSE:            {'✓' if close_ok else '✗'}")
    print(f"  Position removed: {'✓' if pos_gone else '✗'}")
    print(f"  HISTORY deals:    {'✓' if history_ok else '✗'}")
    print(f"{'=' * 60}")

    all_critical = sl_set and tp_set and close_ok and pos_gone and history_ok
    if all_critical:
        print("\n✓ ALL CRITICAL TESTS PASSED — Phase 1 complete!")
    else:
        print("\n✗ Some critical tests failed — review above")


def main():
    p = argparse.ArgumentParser()
    p.add_argument("--host", default="127.0.0.1")
    p.add_argument("--port", type=int, default=5501)
    p.add_argument("--symbol", default="BTCUSD")
    p.add_argument("--close-only", action="store_true",
                   help="Only close existing test positions")
    args = p.parse_args()

    c = Client(args.host, args.port)
    try:
        if args.close_only:
            close_test_positions(c)
        else:
            close_test_positions(c)
            full_lifecycle_test(c, args.symbol)
    finally:
        c.close()


if __name__ == "__main__":
    main()
