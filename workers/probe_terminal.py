"""
probe_terminal.py — Quick probe of a running MT5 terminal.

Usage:
    python probe_terminal.py "C:\\path\\to\\terminal64.exe"

Returns JSON to stdout:
    {"path": "...", "status": "ok", "company": "...", "login": 12345, ...}
    {"path": "...", "status": "error", "error": "..."}

Used by the daemon's auto-discovery feature.
The terminal must already be running and logged in.
No password needed — mt5.initialize(path=...) attaches to the running process.
"""

import json
import sys

def probe(path: str) -> dict:
    result = {"path": path, "status": "error"}

    try:
        import MetaTrader5 as mt5
    except ImportError:
        result["error"] = "MetaTrader5 package not installed"
        return result

    try:
        if not mt5.initialize(path=path):
            err = mt5.last_error()
            result["error"] = f"initialize failed: {err}"
            return result

        info = mt5.terminal_info()
        acc = mt5.account_info()

        result["status"] = "ok"
        result["company"] = info.company if info else None
        result["name"] = info.name if info else None
        result["data_path"] = info.data_path if info else None
        result["connected"] = info.connected if info else False
        result["login"] = acc.login if acc else None
        result["server"] = acc.server if acc else None
        result["balance"] = acc.balance if acc else None
        result["equity"] = acc.equity if acc else None
        result["leverage"] = acc.leverage if acc else None
        result["currency"] = acc.currency if acc else None
        result["trade_mode"] = acc.trade_mode if acc else None  # 0=demo, 2=real
        result["margin_mode"] = acc.margin_mode if acc else None  # 0=netting, 2=hedge

        mt5.shutdown()
    except Exception as e:
        result["error"] = str(e)

    return result


if __name__ == "__main__":
    if len(sys.argv) < 2:
        print(json.dumps({"status": "error", "error": "Usage: probe_terminal.py <path_to_terminal64.exe>"}))
        sys.exit(1)

    path = sys.argv[1]
    result = probe(path)
    print(json.dumps(result, ensure_ascii=False))
    sys.exit(0 if result["status"] == "ok" else 1)
