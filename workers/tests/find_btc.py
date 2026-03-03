import socket, json

s = socket.socket()
s.connect(('localhost', 5501))
f = s.makefile('r')

for sym in ['BTCUSD', 'BTCUSDi', 'BTCUSD.', 'BTC/USD', 'Bitcoin', 'XBTUSD']:
    msg = json.dumps({'cmd': 'SYMBOL_INFO', 'id': 1, 'symbol': sym}) + '\n'
    s.sendall(msg.encode())
    resp = json.loads(f.readline())
    if resp.get('status') == 'ok':
        d = resp['data']
        print(f"FOUND: {sym} -> {d['symbol']}  vol_min={d['volume_min']}  digits={d['digits']}  contract={d['trade_contract_size']}")
    else:
        print(f"  no: {sym}")

s.close()
