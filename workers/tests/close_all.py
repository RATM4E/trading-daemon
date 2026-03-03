import socket, json

s = socket.socket()
s.connect(('localhost', 5501))
f = s.makefile('r')

# Получить позиции
s.sendall((json.dumps({"cmd": "GET_POSITIONS", "id": 1}) + "\n").encode())
positions = json.loads(f.readline())["data"]

for p in positions:
    if p["magic"] == 999999:
        close_type = 1 if p["type"] == "BUY" else 0
        print(f"Closing #{p['ticket']} {p['type']} {p['symbol']} {p['volume']}")
        req = {
            "cmd": "ORDER_SEND", "id": 2,
            "request": {
                "action": 1, "symbol": p["symbol"],
                "volume": p["volume"], "type": close_type,
                "price": 0, "deviation": 50,
                "position": p["ticket"], "magic": 999999,
                "comment": "manual_close",
                "type_filling": 1, "type_time": 0,
            }
        }
        s.sendall((json.dumps(req) + "\n").encode())
        resp = json.loads(f.readline())
        print(f"  -> {resp.get('status')}: {resp.get('data', resp.get('message'))}")

s.close()