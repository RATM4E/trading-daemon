import socket, json

c = socket.socket()
c.connect(('localhost', 5501))
f = c.makefile('r')
_id = 0

def send(cmd, **kw):
    global _id
    _id += 1
    msg = {"cmd": cmd, "id": _id, **kw}
    c.sendall((json.dumps(msg) + "\n").encode())
    resp = json.loads(f.readline())
    return resp

# 1) Открыть позицию
resp = send("SYMBOL_INFO", symbol="BTCUSD")
si = resp["data"]

resp = send("GET_RATES", symbol="BTCUSD", timeframe="M1", count=1)
price = resp["data"][-1]["close"]

resp = send("ORDER_SEND", request={
    "action": 1, "symbol": "BTCUSD", "volume": 0.01,
    "type": 0, "price": price, "sl": 0.0, "tp": 0.0,
    "deviation": 50, "magic": 999999, "comment": "debug",
    "type_filling": 1, "type_time": 0,
})
print(f"OPEN: {resp['status']} {resp.get('data', resp.get('message'))}")

import time; time.sleep(0.5)

resp = send("GET_POSITIONS")
pos = [p for p in resp["data"] if p["magic"] == 999999][0]
ticket = pos["ticket"]
print(f"Position: #{ticket} @ {pos['price_open']}")

# 2) Пробуем разные варианты MODIFY
tests = [
    # Минимальный
    {"action": 3, "symbol": "BTCUSD", "position": ticket,
     "sl": round(price - 1000, 2), "tp": round(price + 1000, 2)},
    # С volume
    {"action": 3, "symbol": "BTCUSD", "volume": 0.01, "position": ticket,
     "sl": round(price - 1000, 2), "tp": round(price + 1000, 2)},
    # С type_filling
    {"action": 3, "symbol": "BTCUSD", "position": ticket,
     "sl": round(price - 1000, 2), "tp": round(price + 1000, 2),
     "type_filling": 1},
    # С price
    {"action": 3, "symbol": "BTCUSD", "position": ticket,
     "sl": round(price - 1000, 2), "tp": round(price + 1000, 2),
     "price": price},
]

for i, req in enumerate(tests):
    resp = send("ORDER_SEND", request=req)
    print(f"MODIFY test {i+1}: {resp.get('status')} code={resp.get('code','-')} {resp.get('message', resp.get('data',''))}")
    if resp.get("status") == "ok":
        print("  ^^^ THIS ONE WORKS!")
        break

# 3) Тест HISTORY — широкое окно
resp = send("HISTORY_DEALS", from_ts=0, to_ts=2000000000)
deals = resp.get("data", [])
my_deals = [d for d in deals if d.get("magic") == 999999]
print(f"\nHISTORY: {len(deals)} total deals, {len(my_deals)} with magic=999999")
for d in my_deals[-5:]:
    print(f"  {d}")

# 4) Закрыть
resp = send("ORDER_SEND", request={
    "action": 1, "symbol": "BTCUSD", "volume": 0.01,
    "type": 1, "price": price, "deviation": 50,
    "position": ticket, "magic": 999999,
    "type_filling": 1, "type_time": 0, "comment": "debug_close",
})
print(f"\nCLOSE: {resp['status']} {resp.get('data', resp.get('message'))}")

c.close()