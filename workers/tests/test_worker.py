"""
Test client for MT5 Worker.

Подключается к запущенному воркеру, тестирует все команды по очереди.

Запуск:
    1. Сначала запустить воркер:
       python mt5_worker.py --port 5501 --terminal-path "C:\...\terminal64.exe"

    2. В другом терминале:
       python test_worker.py --port 5501
       python test_worker.py --port 5501 --test-order   # с тестом ордера (на демо!)
"""

import argparse
import json
import socket
import sys
import time


class WorkerClient:
    """Simple synchronous TCP client for MT5 Worker."""

    def __init__(self, host: str = "127.0.0.1", port: int = 5501, timeout: float = 10.0):
        self.host = host
        self.port = port
        self.timeout = timeout
        self._sock: socket.socket | None = None
        self._file = None
        self._next_id = 1

    def connect(self):
        self._sock = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
        self._sock.settimeout(self.timeout)
        self._sock.connect((self.host, self.port))
        self._file = self._sock.makefile("r", encoding="utf-8")
        print(f"✓ Connected to {self.host}:{self.port}")

    def close(self):
        if self._file:
            self._file.close()
        if self._sock:
            self._sock.close()

    def send(self, cmd: str, **kwargs) -> dict:
        """Send a command and return the response."""
        msg = {"cmd": cmd, "id": self._next_id, **kwargs}
        self._next_id += 1

        line = json.dumps(msg) + "\n"
        self._sock.sendall(line.encode("utf-8"))

        resp_line = self._file.readline()
        if not resp_line:
            raise ConnectionError("Worker closed connection")

        return json.loads(resp_line)


def print_result(label: str, resp: dict, show_data: bool = True):
    status = resp.get("status", "?")
    icon = "✓" if status == "ok" else "✗"
    print(f"\n{icon} {label} → {status}")

    if status == "error":
        print(f"  Error: {resp.get('message', resp.get('code', '?'))}")
        if resp.get("retryable"):
            print(f"  (retryable)")
        return

    if show_data and "data" in resp:
        data = resp["data"]
        if isinstance(data, list):
            print(f"  Items: {len(data)}")
            for item in data[:3]:  # Show first 3
                print(f"    {item}")
            if len(data) > 3:
                print(f"    ... and {len(data) - 3} more")
        elif isinstance(data, dict):
            for k, v in data.items():
                print(f"  {k}: {v}")


def test_basic(client: WorkerClient):
    """Test all read-only commands."""

    # 1. HEARTBEAT
    resp = client.send("HEARTBEAT")
    print_result("HEARTBEAT", resp)

    # 2. ACCOUNT_INFO
    resp = client.send("ACCOUNT_INFO")
    print_result("ACCOUNT_INFO", resp)
    acc = resp.get("data", {})

    # 3. GET_POSITIONS
    resp = client.send("GET_POSITIONS")
    print_result("GET_POSITIONS", resp)

    # 4. SYMBOL_INFO — valid symbol
    resp = client.send("SYMBOL_INFO", symbol="EURUSD")
    print_result("SYMBOL_INFO (EURUSD)", resp)

    # 5. SYMBOL_INFO — invalid symbol
    resp = client.send("SYMBOL_INFO", symbol="FAKESYMBOL123")
    print_result("SYMBOL_INFO (invalid)", resp)

    # 6. GET_RATES — small batch
    resp = client.send("GET_RATES", symbol="EURUSD", timeframe="H1", count=5)
    print_result("GET_RATES (EURUSD H1 x5)", resp)

    # 7. GET_RATES — large batch for strategy
    t0 = time.perf_counter()
    resp = client.send("GET_RATES", symbol="EURUSD", timeframe="H1", count=300)
    elapsed = (time.perf_counter() - t0) * 1000
    status = resp.get("status")
    bar_count = len(resp.get("data", [])) if status == "ok" else 0
    print(f"\n✓ GET_RATES (EURUSD H1 x300) → {bar_count} bars in {elapsed:.0f}ms")

    # 8. GET_RATES — invalid timeframe
    resp = client.send("GET_RATES", symbol="EURUSD", timeframe="H3", count=5)
    print_result("GET_RATES (invalid TF)", resp)

    # 9. ORDERS_GET (pending orders)
    resp = client.send("ORDERS_GET")
    print_result("ORDERS_GET", resp)

    # 10. HISTORY_DEALS (last 24h)
    now_ts = int(time.time())
    resp = client.send("HISTORY_DEALS", from_ts=now_ts - 86400, to_ts=now_ts)
    print_result("HISTORY_DEALS (24h)", resp)

    # 11. Unknown command
    resp = client.send("BLABLABLA")
    print_result("UNKNOWN CMD", resp)

    return acc


def test_order(client: WorkerClient, symbol: str = "EURUSD"):
    """Test order send + close on demo account."""
    print("\n" + "=" * 60)
    print("ORDER TEST (demo only!)")
    print("=" * 60)

    # Get current price
    resp = client.send("SYMBOL_INFO", symbol=symbol)
    if resp.get("status") != "ok":
        print(f"✗ Can't get symbol info: {resp}")
        return

    si = resp["data"]
    point = si["point"]
    digits = si["digits"]

    # Get current tick for price
    resp = client.send("GET_RATES", symbol=symbol, timeframe="M1", count=1)
    if resp.get("status") != "ok" or not resp["data"]:
        print(f"✗ Can't get rates: {resp}")
        return

    close_price = resp["data"][-1]["close"]

    # SL/TP distance — adaptive based on instrument
    # Forex: ~100 pips, Crypto/Indices: ~0.5% of price
    if close_price > 100:  # Crypto, indices
        sl_dist = round(close_price * 0.005, digits)  # 0.5%
    else:  # Forex
        sl_dist = round(500 * point, digits)  # 500 points = 50 pips

    sl = round(close_price - sl_dist, digits)
    tp = round(close_price + sl_dist, digits)

    print(f"\n  Opening BUY {symbol} @ ~{close_price}, SL={sl}, TP={tp} (dist={sl_dist})")
    resp = client.send("ORDER_SEND", request={
        "action": 1,          # TRADE_ACTION_DEAL
        "symbol": symbol,
        "volume": si["volume_min"],
        "type": 0,            # ORDER_TYPE_BUY
        "price": close_price,
        "sl": sl,
        "tp": tp,
        "deviation": 20,
        "magic": 999999,
        "comment": "test_worker",
        "type_filling": 1,    # ORDER_FILLING_IOC (adjust per broker)
        "type_time": 0,       # ORDER_TIME_GTC
    })
    print_result("ORDER_SEND (BUY)", resp)

    if resp.get("status") != "ok":
        print("  ⚠ Order failed — this is expected if filling mode doesn't match.")
        print("    Try adjusting type_filling (0=FOK, 1=IOC, 2=RETURN)")
        return

    order_data = resp["data"]
    print(f"  Ticket: {order_data.get('order')}, Deal: {order_data.get('deal')}")

    # Verify position exists
    time.sleep(0.5)
    my_positions = []
    resp = client.send("GET_POSITIONS")
    if resp.get("status") == "ok":
        my_positions = [p for p in resp["data"] if p.get("magic") == 999999]
        print(f"\n  Open positions with magic=999999: {len(my_positions)}")
        for p in my_positions:
            print(f"    #{p['ticket']} {p['type']} {p['symbol']} {p['volume']} @ {p['price_open']}")

    if not my_positions:
        print("  ⚠ No position found — may have been stopped out already")
        return

    # Close the position
    if my_positions:
        pos = my_positions[0]
        close_type = 1 if pos["type"] == "BUY" else 0  # Opposite type
        print(f"\n  Closing position #{pos['ticket']}...")
        resp = client.send("ORDER_SEND", request={
            "action": 1,           # TRADE_ACTION_DEAL
            "symbol": pos["symbol"],
            "volume": pos["volume"],
            "type": close_type,
            "price": close_price,
            "deviation": 20,
            "position": pos["ticket"],
            "magic": 999999,
            "comment": "test_close",
            "type_filling": 1,
            "type_time": 0,
        })
        print_result("ORDER_SEND (CLOSE)", resp)


def test_shutdown(client: WorkerClient):
    """Test SHUTDOWN command."""
    print("\n" + "=" * 60)
    print("SHUTDOWN TEST")
    print("=" * 60)
    resp = client.send("SHUTDOWN")
    print_result("SHUTDOWN", resp)


def main():
    p = argparse.ArgumentParser(description="MT5 Worker test client")
    p.add_argument("--host", default="127.0.0.1")
    p.add_argument("--port", type=int, default=5501)
    p.add_argument("--test-order", action="store_true",
                   help="Test order open/close (demo only!)")
    p.add_argument("--test-shutdown", action="store_true",
                   help="Send SHUTDOWN to worker at the end")
    p.add_argument("--symbol", default="EURUSD",
                   help="Symbol for order test (default: EURUSD)")
    args = p.parse_args()

    client = WorkerClient(host=args.host, port=args.port)

    try:
        client.connect()

        print("\n" + "=" * 60)
        print("BASIC TESTS")
        print("=" * 60)
        acc = test_basic(client)

        if args.test_order:
            test_order(client, symbol=args.symbol)

        if args.test_shutdown:
            test_shutdown(client)

        print("\n" + "=" * 60)
        print("ALL TESTS COMPLETE")
        print("=" * 60)

    except ConnectionRefusedError:
        print(f"✗ Connection refused — is the worker running on port {args.port}?")
        sys.exit(1)
    except Exception as e:
        print(f"✗ Error: {e}")
        sys.exit(1)
    finally:
        client.close()


if __name__ == "__main__":
    main()
