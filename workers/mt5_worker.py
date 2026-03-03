"""
MT5 Worker â€” TCP-ÑÐµÑ€Ð²ÐµÑ€ Ð´Ð»Ñ Ð¾Ð´Ð½Ð¾Ð³Ð¾ Ñ‚ÐµÑ€Ð¼Ð¸Ð½Ð°Ð»Ð° MT5.

Ð¡Ð»ÑƒÑˆÐ°ÐµÑ‚ TCP Ð¿Ð¾Ñ€Ñ‚, Ð¿Ñ€Ð¸Ð½Ð¸Ð¼Ð°ÐµÑ‚ JSON-ÐºÐ¾Ð¼Ð°Ð½Ð´Ñ‹ Ð¾Ñ‚ Ð´ÐµÐ¼Ð¾Ð½Ð°,
Ð²Ñ‹Ð¿Ð¾Ð»Ð½ÑÐµÑ‚ Ñ‡ÐµÑ€ÐµÐ· MetaTrader5 API, Ð²Ð¾Ð·Ð²Ñ€Ð°Ñ‰Ð°ÐµÑ‚ JSON-Ð¾Ñ‚Ð²ÐµÑ‚.

ÐŸÑ€Ð¾Ñ‚Ð¾ÐºÐ¾Ð»: newline-delimited JSON (ÐºÐ°Ð¶Ð´Ð¾Ðµ ÑÐ¾Ð¾Ð±Ñ‰ÐµÐ½Ð¸Ðµ â€” JSON ÑÑ‚Ñ€Ð¾ÐºÐ° + \n)

Ð—Ð°Ð¿ÑƒÑÐº:
    python mt5_worker.py --port 5501 --terminal-path "C:\...\terminal64.exe"
    python mt5_worker.py --port 5501 --terminal-path "C:\...\terminal64.exe" --login 12345 --password xxx --server "Demo"
"""

import argparse
import asyncio
import json
import logging
import signal
import sys
import time
from datetime import datetime, timezone

import MetaTrader5 as mt5

# ---------------------------------------------------------------------------
# Logging
# ---------------------------------------------------------------------------
logging.basicConfig(
    level=logging.INFO,
    format="%(asctime)s [%(levelname)s] %(message)s",
    datefmt="%Y-%m-%d %H:%M:%S",
)
log = logging.getLogger("mt5_worker")

# ---------------------------------------------------------------------------
# Timeframe mapping
# ---------------------------------------------------------------------------
TF_MAP = {
    "M1":  mt5.TIMEFRAME_M1,
    "M5":  mt5.TIMEFRAME_M5,
    "M15": mt5.TIMEFRAME_M15,
    "M30": mt5.TIMEFRAME_M30,
    "H1":  mt5.TIMEFRAME_H1,
    "H2":  mt5.TIMEFRAME_H2,
    "H3":  mt5.TIMEFRAME_H3,
    "H4":  mt5.TIMEFRAME_H4,
    "H6":  mt5.TIMEFRAME_H6,
    "H8":  mt5.TIMEFRAME_H8,
    "H12": mt5.TIMEFRAME_H12,
    "D1":  mt5.TIMEFRAME_D1,
    "W1":  mt5.TIMEFRAME_W1,
    "MN1": mt5.TIMEFRAME_MN1,
}

# MT5 retcodes that the daemon can retry
RETRYABLE_CODES = {
    mt5.TRADE_RETCODE_REQUOTE,      # 10004
    mt5.TRADE_RETCODE_PRICE_OFF,    # 10021
    mt5.TRADE_RETCODE_CONNECTION,   # 10031
    mt5.TRADE_RETCODE_TIMEOUT,      # 10012
}

# ---------------------------------------------------------------------------
# Symbol alias table: canonical → list of common broker variants
# Used by CHECK_SYMBOLS to resolve canonical names to broker-specific names
# ---------------------------------------------------------------------------
SYMBOL_ALIASES = {
    # Metals
    "XAUUSD": ["GOLD", "XAUUSD.fix", "XAUUSD.m", "XAUUSD.micro", "XAUUSD.pro", "XAUUSD.ecn", "XAUUSD.i", "GOLDmicro"],
    "XAGUSD": ["SILVER", "XAGUSD.fix", "XAGUSD.m", "XAGUSD.micro", "XAGUSD.pro", "XAGUSD.ecn", "SILVERmicro"],
    # Indices
    "SP500":  ["US500", "USA500", "SPX500", "SP500.cash", "US500.cash", "SPX500USD", "US500USD"],
    "NAS100": ["US100", "NAS100.cash", "NAS100USD", "USTEC", "USTECH", "NAS100m"],
    "US30":   ["DJ30", "WALLSTREET", "US30.cash", "US30USD", "US30m"],
    "DAX40":  ["GER40", "DE40", "DAX", "DAX40.cash", "GER40.cash", "DE40EUR"],
    "UK100":  ["FTSE100", "UKX", "UK100.cash", "UK100GBP"],
    "JPN225": ["JP225", "NIKKEI225", "J225", "JP225.cash", "JPN225USD"],
    # Oil
    "XTIUSD": ["USOIL", "WTI", "OIL.WTI", "OIL", "OILUSD", "WTI.cash", "USOILm"],
    "XBRUSD": ["UKOIL", "BRENT", "OIL.BRENT", "BRN", "BRNUSD", "BRENT.cash"],
    # Crypto
    "BTCUSD": ["XBTUSD", "BTCUSDm", "BTCUSD.cash", "BTCUSDT"],
    "ETHUSD": ["ETHUSDm", "ETHUSD.cash", "ETHUSDT"],
}

# Build reverse lookup: alias (upper) → canonical
_ALIAS_REVERSE = {}
for _canon, _aliases in SYMBOL_ALIASES.items():
    for _a in _aliases:
        _ALIAS_REVERSE[_a.upper()] = _canon

# ---------------------------------------------------------------------------
# Command handlers
# ---------------------------------------------------------------------------

def handle_heartbeat(msg: dict) -> dict:
    """Check that MT5 terminal is alive."""
    info = mt5.terminal_info()
    if info is None:
        return _error(msg, "terminal disconnected")

    # Ping via symbol_info_tick (fast call)
    t0 = time.perf_counter_ns()
    tick = mt5.symbol_info_tick("EURUSD")
    ping_ms = (time.perf_counter_ns() - t0) / 1_000_000

    return _ok(msg, {
        "connected": info.connected,
        "ping_ms": round(ping_ms, 1),
    })


def handle_account_info(msg: dict) -> dict:
    """Balance, equity, margin, account type, server time."""
    acc = mt5.account_info()
    if acc is None:
        return _error(msg, f"account_info failed: {mt5.last_error()}")

    # Server time â€” freshest tick across forex + crypto (for weekends)
    srv_ts = 0
    for sym in ("EURUSD", "BTCUSD", "BTCUSD.i", "BTCUSD.r", "Bitcoin", "XAUUSD"):
        tick = mt5.symbol_info_tick(sym)
        if tick is not None and tick.time > srv_ts:
            srv_ts = tick.time

    return _ok(msg, {
        "login":        acc.login,
        "balance":      acc.balance,
        "equity":       acc.equity,
        "margin":       acc.margin,
        "margin_free":  acc.margin_free,
        "margin_level": acc.margin_level if acc.margin_level else 0.0,
        "profit":       acc.profit,
        "currency":     acc.currency,
        "leverage":     acc.leverage,
        "trade_mode":   acc.trade_mode,   # 0=demo, 1=contest, 2=real
        "margin_mode":  acc.margin_mode,  # 0=retail netting, 2=retail hedging
        "server_time":  srv_ts,
    })


def handle_get_positions(msg: dict) -> dict:
    """All open positions."""
    positions = mt5.positions_get()
    if positions is None:
        # None means no positions OR error; last_error() distinguishes
        err = mt5.last_error()
        if err[0] != 1:  # 1 = "no error" (just empty)
            return _error(msg, f"positions_get failed: {err}")
        return _ok(msg, [])

    result = []
    for p in positions:
        result.append({
            "ticket":        p.ticket,
            "symbol":        p.symbol,
            "type":          "BUY" if p.type == 0 else "SELL",
            "volume":        p.volume,
            "price_open":    p.price_open,
            "price_current": getattr(p, "price_current", 0.0),
            "sl":            p.sl,
            "tp":            p.tp,
            "profit":        p.profit,
            "swap":          getattr(p, "swap", 0.0),
            "commission":    getattr(p, "commission", 0.0),
            "time":          p.time,
            "time_update":   getattr(p, "time_update", 0),
            "magic":         p.magic,
            "comment":       getattr(p, "comment", ""),
            "identifier":    getattr(p, "identifier", p.ticket),
        })
    return _ok(msg, result)


def handle_get_rates(msg: dict) -> dict:
    """Get OHLCV bars."""
    symbol = msg.get("symbol")
    timeframe = msg.get("timeframe")
    count = msg.get("count", 300)

    if not symbol or not timeframe:
        return _error(msg, "missing symbol or timeframe")

    tf = TF_MAP.get(timeframe)
    if tf is None:
        return _error(msg, f"unknown timeframe: {timeframe}")

    # Ensure symbol is visible in Market Watch
    if not mt5.symbol_select(symbol, True):
        return _error(msg, f"symbol_select failed for {symbol}: {mt5.last_error()}")

    rates = mt5.copy_rates_from_pos(symbol, tf, 0, count)
    if rates is None:
        return _error(msg, f"copy_rates failed: {mt5.last_error()}")

    bars = []
    for r in rates:
        bars.append({
            "time":   int(r[0]),
            "open":   float(r[1]),
            "high":   float(r[2]),
            "low":    float(r[3]),
            "close":  float(r[4]),
            "volume": int(r[5]),
        })
    return _ok(msg, bars)



def handle_copy_rates_range(msg: dict) -> dict:
    """
    Bulk download historical bars for a date range.
    Used by BacktestEngine to populate BarsHistoryDb.

    Request:  {"cmd": "COPY_RATES_RANGE", "id": 1,
               "symbol": "EURUSD", "timeframe": "M30",
               "from_ts": 1704067200, "to_ts": 1735689600}

    Response: {"id": 1, "status": "ok",
               "data": {"symbol": "EURUSD", "timeframe": "M30",
                         "bars": [...], "count": 17520}}

    Note: from_ts/to_ts are UTC timestamps (API requirement),
    but returned bar timestamps are in BROKER SERVER TIME (e.g. EET for The5ers).
    M30 x 1 year ~ 17,500 bars, takes ~2-3 seconds from MT5.
    """
    symbol = msg.get("symbol")
    tf_str = msg.get("timeframe")
    from_ts = msg.get("from_ts")
    to_ts = msg.get("to_ts")

    if not symbol or not tf_str:
        return _error(msg, "missing symbol or timeframe")
    if from_ts is None or to_ts is None:
        return _error(msg, "missing from_ts or to_ts")

    tf = TF_MAP.get(tf_str)
    if tf is None:
        return _error(msg, f"unknown timeframe: {tf_str}")

    # Ensure symbol is visible in Market Watch
    if not mt5.symbol_select(symbol, True):
        return _error(msg, f"symbol_select failed for {symbol}: {mt5.last_error()}")

    # copy_rates_range expects datetime objects in UTC
    from_dt = datetime.fromtimestamp(from_ts, tz=timezone.utc)
    to_dt = datetime.fromtimestamp(to_ts, tz=timezone.utc)

    log.info(f"COPY_RATES_RANGE: {symbol} {tf_str} "
             f"{from_dt.strftime('%Y-%m-%d')} -> {to_dt.strftime('%Y-%m-%d')}")

    rates = mt5.copy_rates_range(symbol, tf, from_dt, to_dt)
    if rates is None or len(rates) == 0:
        err = mt5.last_error()
        return _error(msg, f"no data for {symbol} {tf_str}: {err}")

    bars = []
    for r in rates:
        bars.append({
            "time":   int(r[0]),
            "open":   float(r[1]),
            "high":   float(r[2]),
            "low":    float(r[3]),
            "close":  float(r[4]),
            "volume": int(r[5]),
        })

    log.info(f"COPY_RATES_RANGE: {symbol} {tf_str} -> {len(bars)} bars")

    return _ok(msg, {
        "symbol": symbol,
        "timeframe": tf_str,
        "bars": bars,
        "count": len(bars),
    })

def handle_symbol_info(msg: dict) -> dict:
    """Instrument card â€” everything needed for lot calculation."""
    symbol = msg.get("symbol")
    if not symbol:
        return _error(msg, "missing symbol")

    # Ensure symbol is visible
    if not mt5.symbol_select(symbol, True):
        return _error(msg, f"symbol_select failed for {symbol}: {mt5.last_error()}")

    si = mt5.symbol_info(symbol)
    if si is None:
        return _error(msg, f"symbol not found: {symbol}")

    # Calculate exact margin for 1.0 lot at current price
    margin_1lot = 0.0
    tick = mt5.symbol_info_tick(symbol)
    if tick and tick.ask > 0:
        m = mt5.order_calc_margin(mt5.ORDER_TYPE_BUY, symbol, 1.0, tick.ask)
        if m is not None:
            margin_1lot = round(m, 2)

    return _ok(msg, {
        "symbol":                si.name,
        "digits":                si.digits,
        "point":                 si.point,
        "trade_tick_size":       si.trade_tick_size,
        "trade_tick_value":      si.trade_tick_value,
        "trade_tick_value_profit": si.trade_tick_value_profit,
        "trade_tick_value_loss": si.trade_tick_value_loss,
        "trade_contract_size":   si.trade_contract_size,
        "volume_min":            si.volume_min,
        "volume_max":            si.volume_max,
        "volume_step":           si.volume_step,
        "margin_initial":        si.margin_initial,
        "margin_1lot":           margin_1lot,
        "spread":                si.spread,
        "currency_base":         si.currency_base,
        "currency_profit":       si.currency_profit,
        "currency_margin":       si.currency_margin,
        "trade_stops_level":     si.trade_stops_level,
        "trade_freeze_level":    si.trade_freeze_level,
    })


def handle_order_send(msg: dict) -> dict:
    """Send a trade order. Expects msg["request"] with MT5 order fields."""
    req_data = msg.get("request")
    if not req_data:
        return _error(msg, "missing 'request' field")

    symbol = req_data.get("symbol", "")
    # Ensure symbol is visible
    if symbol:
        mt5.symbol_select(symbol, True)

    # Build the request dict for mt5.order_send()
    # mt5.order_send() in Python accepts a plain dict
    request = {}

    # Map fields â€” integers
    for int_field in ["action", "type", "type_filling", "type_time", "magic",
                      "deviation", "position", "position_by", "expiration"]:
        if int_field in req_data:
            request[int_field] = int(req_data[int_field])

    # Map fields â€” floats
    for float_field in ["volume", "price", "sl", "tp", "stoplimit"]:
        if float_field in req_data:
            request[float_field] = float(req_data[float_field])

    # Map fields â€” strings
    for str_field in ["symbol", "comment"]:
        if str_field in req_data:
            request[str_field] = str(req_data[str_field])

    log.info(f"ORDER_SEND: {request}")

    result = mt5.order_send(request)
    if result is None:
        return _error(msg, f"order_send returned None: {mt5.last_error()}", retryable=False)

    if result.retcode != mt5.TRADE_RETCODE_DONE:
        retryable = result.retcode in RETRYABLE_CODES
        return {
            "id": msg.get("id"),
            "status": "error",
            "code": result.retcode,
            "message": result.comment,
            "retryable": retryable,
        }

    return _ok(msg, {
        "order":   result.order,
        "deal":    result.deal,
        "price":   result.price,
        "volume":  result.volume,
        "comment": result.comment,
    })


def handle_orders_get(msg: dict) -> dict:
    """Get pending orders."""
    orders = mt5.orders_get()
    if orders is None:
        err = mt5.last_error()
        if err[0] != 1:
            return _error(msg, f"orders_get failed: {err}")
        return _ok(msg, [])

    result = []
    for o in orders:
        result.append({
            "ticket":        o.ticket,
            "symbol":        getattr(o, "symbol", ""),
            "type":          o.type,
            "volume":        getattr(o, "volume_current", 0.0),
            "price_open":    getattr(o, "price_open", 0.0),
            "sl":            getattr(o, "sl", 0.0),
            "tp":            getattr(o, "tp", 0.0),
            "price_current": getattr(o, "price_current", 0.0),
            "time_setup":    getattr(o, "time_setup", 0),
            "magic":         getattr(o, "magic", 0),
            "comment":       getattr(o, "comment", ""),
        })
    return _ok(msg, result)


def handle_history_deals(msg: dict) -> dict:
    """Get deal history. Supports two modes:
    1. By time range: {"from_ts": ..., "to_ts": ...}
    2. By position:   {"position": 12345, "from_ts": 0, "to_ts": 2000000000}
    """
    from_ts = msg.get("from_ts", 0)
    to_ts = msg.get("to_ts", int(time.time()))
    position_id = msg.get("position")

    from_dt = datetime.fromtimestamp(from_ts, tz=timezone.utc)
    to_dt = datetime.fromtimestamp(to_ts, tz=timezone.utc)

    if position_id is not None:
        # Filter by position ticket â€” much more efficient for reconciliation
        deals = mt5.history_deals_get(from_dt, to_dt, position=int(position_id))
    else:
        deals = mt5.history_deals_get(from_dt, to_dt)

    if deals is None:
        err = mt5.last_error()
        if err[0] != 1:
            return _error(msg, f"history_deals_get failed: {err}")
        return _ok(msg, [])

    result = []
    for d in deals:
        result.append({
            "ticket":      d.ticket,
            "order":       d.order,
            "symbol":      getattr(d, "symbol", ""),
            "type":        d.type,
            "volume":      d.volume,
            "price":       d.price,
            "profit":      d.profit,
            "swap":        getattr(d, "swap", 0.0),
            "commission":  getattr(d, "commission", 0.0),
            "time":        d.time,
            "magic":       d.magic,
            "comment":     getattr(d, "comment", ""),
            "position_id": d.position_id,
            "entry":       d.entry,  # 0=IN, 1=OUT, 2=INOUT, 3=OUT_BY
        })
    return _ok(msg, result)


# ---------------------------------------------------------------------------
# Helpers
# ---------------------------------------------------------------------------

def _ok(msg: dict, data) -> dict:
    return {"id": msg.get("id"), "status": "ok", "data": data}


def _error(msg: dict, message: str, retryable: bool = False) -> dict:
    return {"id": msg.get("id"), "status": "error", "message": message, "retryable": retryable}


# ---------------------------------------------------------------------------
# Leverage detection helpers
# ---------------------------------------------------------------------------

# Candidate symbols per class for auto-discovery (order = priority)
_LEVERAGE_CANDIDATES = {
    "IDX": ["NAS100", "US30", "US500", "SP500", "SPX500", "US100", "USTEC",
            "DJ30", "DJI30", "NAS100.", "US30.", "NAS100m", "US30m",
            "#NAS100", "#US30", "US30.cash", "NAS100.cash"],
    "XAU": ["XAUUSD", "GOLD", "XAUUSD.", "XAUUSDm", "#XAUUSD", "GOLD.", "GOLDm"],
    "OIL": ["XTIUSD", "USOIL", "WTI", "XBRUSD", "UKOIL",
            "XTIUSD.", "USOIL.", "XTIUSDm", "#XTIUSD", "#USOIL", "CrudeOIL"],
    "CRYP": ["BTCUSD", "BITCOIN", "BTCUSD.", "BTCUSDm", "#BTCUSD"],
}

# Fuzzy search patterns (substrings to look for in symbol names)
_FUZZY_PATTERNS = {
    "IDX": ["US30", "NAS", "SPX", "SP500", "DOW", "DJ30"],
    "XAU": ["XAU", "GOLD"],
    "OIL": ["XTI", "WTI", "OIL", "BRENT", "CRUDE"],
    "CRYP": ["BTC", "BITCOIN"],
}


def _resolve_leverage_symbol(aclass, requested, all_symbols_cache):
    """Find a working symbol for leverage detection.

    Strategy:
      1. Try the requested symbol (from DashboardServer, already mapped)
      2. Try hardcoded candidates for the class
      3. Fuzzy search through all available symbols

    Returns (symbol_name, resolve_method) or (None, diagnostic_string).
    """
    # 1. Try requested symbol
    if requested:
        if mt5.symbol_select(requested, True) and mt5.symbol_info(requested):
            return requested, "requested"

    # 2. Try candidate list
    candidates = _LEVERAGE_CANDIDATES.get(aclass, [])
    for c in candidates:
        if mt5.symbol_select(c, True) and mt5.symbol_info(c):
            return c, f"candidate:{c}"

    # 3. Lazy-load all symbols for fuzzy search
    if all_symbols_cache[0] is None:
        syms = mt5.symbols_get()
        all_symbols_cache[0] = [s.name for s in syms] if syms else []
        if len(all_symbols_cache[0]) < 50:
            log.warning(f"CALC_LEVERAGE: terminal exposes only "
                        f"{len(all_symbols_cache[0])} symbols â€” "
                        f"enable Show All in Market Watch or check account")

    all_names = all_symbols_cache[0]
    if not all_names:
        return None, f"symbols_get returned 0 symbols"

    # Fuzzy: search by substring patterns, prefer USD-denominated
    patterns = _FUZZY_PATTERNS.get(aclass, [])
    for pattern in patterns:
        matches = [s for s in all_names if pattern.upper() in s.upper()]
        # Prefer symbols with USD in name
        usd_first = sorted(matches, key=lambda s: (0 if "USD" in s.upper() else 1, len(s)))
        for m in usd_first:
            if mt5.symbol_select(m, True) and mt5.symbol_info(m):
                return m, f"fuzzy:{pattern}->{m}"

    # Failed â€” build diagnostic
    sample = all_names[:20]
    return None, f"no match in {len(all_names)} symbols (sample: {', '.join(sample)})"


def _snap_leverage(raw):
    """Snap to nearest standard leverage value if within 8% tolerance."""
    standard = [1, 2, 3, 5, 10, 15, 20, 25, 30, 33, 40, 50,
                100, 125, 200, 300, 400, 500, 1000]
    for s in standard:
        if abs(raw - s) / max(s, 1) < 0.08:
            return s
    return round(raw)


def handle_calc_leverage(msg: dict) -> dict:
    """Calculate effective leverage per asset class.

    FX: uses account_leverage directly (cross-currency makes calc_margin
        unreliable for leverage inference).
    Non-FX: uses order_calc_margin with USD-denominated symbols.
        Auto-discovers symbols if requested ones don't exist.

    Returns {
        "leverage": {"FX": 100, "IDX": 25, ...},
        "margin_pct": {"FX": 1.0, "IDX": 4.0, ...},
        "details": {...},
        "account_leverage": 100
    }
    """
    symbols = msg.get("symbols")
    if not symbols or not isinstance(symbols, dict):
        return _error(msg, "missing or invalid 'symbols' dict")

    acc = mt5.account_info()
    acc_leverage = acc.leverage if acc else 0

    leverage_result = {}
    margin_pct_result = {}
    details = {}
    all_symbols_cache = [None]  # mutable ref for lazy loading

    for aclass, requested in symbols.items():

        # â”€â”€ FX: account_leverage is authoritative â”€â”€
        # MT5 forex margin = lots Ã— contract_size Ã— price / account_leverage
        # Cross-pairs (AUDCAD etc.) make notional/margin unreliable
        if aclass == "FX":
            if acc_leverage > 0:
                leverage_result["FX"] = acc_leverage
                margin_pct_result["FX"] = round(100.0 / acc_leverage, 2)
                details["FX"] = {"method": "account_leverage", "value": acc_leverage}
                log.info(f"CALC_LEVERAGE: FX = 1:{acc_leverage} (account_leverage)")
            else:
                details["FX"] = {"error": "account_leverage unavailable"}
                log.warning("CALC_LEVERAGE: FX - account_leverage unavailable")
            continue

        # â”€â”€ Non-FX: resolve symbol, calc from margin â”€â”€
        symbol, method = _resolve_leverage_symbol(aclass, requested, all_symbols_cache)

        if symbol is None:
            log.warning(f"CALC_LEVERAGE: {aclass} - no symbol found "
                        f"(requested={requested}, {method})")
            details[aclass] = {"requested": requested, "error": method}
            continue

        try:
            si = mt5.symbol_info(symbol)
            tick = mt5.symbol_info_tick(symbol)
            if si is None or tick is None or tick.ask <= 0:
                log.warning(f"CALC_LEVERAGE: {aclass} {symbol} - no market data")
                details[aclass] = {"symbol": symbol, "error": "no market data"}
                continue

            margin = mt5.order_calc_margin(mt5.ORDER_TYPE_BUY, symbol, 1.0, tick.ask)
            if margin is None or margin <= 0:
                err = mt5.last_error()
                log.warning(f"CALC_LEVERAGE: {aclass} {symbol} - "
                            f"order_calc_margin failed: {err}")
                details[aclass] = {"symbol": symbol, "error": f"calc_margin: {err}"}
                continue

            notional = si.trade_contract_size * tick.ask
            if notional <= 0:
                details[aclass] = {"symbol": symbol, "error": "notional <= 0"}
                continue

            margin_pct = margin / notional * 100
            eff_leverage = 100.0 / margin_pct if margin_pct > 0 else 0
            snapped = _snap_leverage(eff_leverage)

            detail = {
                "symbol": symbol,
                "resolved_via": method,
                "margin_1lot": round(margin, 2),
                "contract_size": si.trade_contract_size,
                "price": round(tick.ask, si.digits),
                "notional": round(notional, 2),
                "calc_mode": si.trade_calc_mode,
                "margin_initial": si.margin_initial,
                "margin_pct": round(margin_pct, 2),
                "raw_leverage": round(eff_leverage, 1),
                "snapped": snapped,
            }

            if snapped < 1 or snapped > 2000:
                log.warning(f"CALC_LEVERAGE: {aclass} {symbol} = unreasonable "
                            f"{eff_leverage:.1f} (margin={margin:.2f}, "
                            f"notional={notional:.2f})")
                detail["error"] = f"unreasonable: {eff_leverage:.0f}"
                details[aclass] = detail
                continue

            leverage_result[aclass] = snapped
            margin_pct_result[aclass] = round(margin_pct, 2)
            details[aclass] = detail

            log.info(f"CALC_LEVERAGE: {aclass} {symbol} = 1:{snapped} "
                     f"(margin%={margin_pct:.2f}%, raw={eff_leverage:.1f}, "
                     f"margin=${margin:.2f}, cs={si.trade_contract_size}, "
                     f"price={tick.ask}, notional=${notional:.0f}, "
                     f"resolved={method})")

        except Exception as e:
            log.warning(f"CALC_LEVERAGE: {aclass} error: {e}")
            details[aclass] = {"symbol": symbol, "error": str(e)}

    return _ok(msg, {
        "leverage": leverage_result,
        "margin_pct": margin_pct_result,
        "details": details,
        "account_leverage": acc_leverage,
    })


def handle_check_symbols(msg: dict) -> dict:
    """Check which canonical symbols are available on this terminal.

    Input:  { "symbols": ["XAUUSD", "BTCUSD", "GBPUSD", ...] }
    Output: { "resolved": { "XAUUSD": "GOLD", "GBPUSD": "GBPUSD", ... },
              "missing":  ["BTCUSD"] }

    Resolution order per symbol:
      1. Exact canonical name
      2. Known aliases from SYMBOL_ALIASES table
      3. Suffix variations (.m, .pro, .ecn, .fix, .i, .micro, .cash)
    """
    requested = msg.get("symbols", [])
    if not requested:
        return _error(msg, "missing 'symbols' list")

    # Get all symbols from terminal (cached for this call)
    all_syms = mt5.symbols_get()
    if not all_syms:
        return _error(msg, "symbols_get failed or returned 0 symbols")

    # Build lookup: upper name → actual name
    available = {s.name.upper(): s.name for s in all_syms}

    resolved = {}
    missing = []

    common_suffixes = ["", ".m", ".pro", ".ecn", ".fix", ".i", ".micro", ".cash"]

    for canonical in requested:
        found = None
        cup = canonical.upper()

        # 1. Exact match
        if cup in available:
            found = available[cup]
        else:
            # 2. Known aliases
            aliases = SYMBOL_ALIASES.get(canonical, [])
            for alias in aliases:
                if alias.upper() in available:
                    found = available[alias.upper()]
                    break

            # 3. Suffix variations on canonical name
            if not found:
                for sfx in common_suffixes:
                    candidate = cup + sfx.upper()
                    if candidate in available:
                        found = available[candidate]
                        break

        if found:
            # Ensure it's selected in Market Watch
            mt5.symbol_select(found, True)
            resolved[canonical] = found
        else:
            missing.append(canonical)

    log.info(f"CHECK_SYMBOLS: {len(resolved)} resolved, {len(missing)} missing "
             f"out of {len(requested)} requested"
             + (f" (missing: {', '.join(missing[:10])})" if missing else ""))

    return _ok(msg, {
        "resolved": resolved,
        "missing": missing,
    })


def handle_calc_margin(msg: dict) -> dict:
    """Calculate margin for a hypothetical trade using MT5 OrderCalcMargin.

    This is the authoritative way to compute margin for cross-pairs —
    MT5 handles all currency conversions internally (GBP→USD etc).

    Input:  { "symbol": "GBPCAD", "action": "buy", "volume": 1.0, "price": 1.8498 }
    Output: { "margin": 135.18 }
    """
    symbol = msg.get("symbol")
    action = msg.get("action", "buy").lower()
    volume = float(msg.get("volume", 1.0))
    price = msg.get("price")

    if not symbol or price is None:
        return _error(msg, "missing symbol or price")

    # Ensure symbol is visible
    if not mt5.symbol_select(symbol, True):
        return _error(msg, f"symbol_select failed for {symbol}: {mt5.last_error()}")

    order_type = mt5.ORDER_TYPE_BUY if action == "buy" else mt5.ORDER_TYPE_SELL

    margin = mt5.order_calc_margin(order_type, symbol, volume, float(price))
    if margin is None:
        err = mt5.last_error()
        return _error(msg, f"order_calc_margin failed: {err}")

    return _ok(msg, {"margin": round(margin, 2)})


def handle_calc_positions_margin(msg: dict) -> dict:
    """Calculate per-position margin and PnL, filtered by magic numbers.

    Provides accurate margin data for the "own vs foreign" margin split
    when multiple strategies share one MT5 account.

    Input:  { "magics": [100, 200] }        — filter by magic numbers
            { }                               — all positions (no filter)
    Output: {
        "positions": [
            { "ticket": 123, "symbol": "EURUSD", "magic": 100,
              "type": "BUY", "volume": 0.01, "margin": 36.00,
              "profit": -2.50, "swap": -0.12, "price_open": 1.0850 },
            ...
        ],
        "own_margin":   78.00,   # sum of margin for filtered positions
        "own_profit":   -1.20,   # sum of profit+swap for filtered positions
        "total_margin": 4578.00, # sum of margin for ALL positions
        "total_profit": -45.00   # sum of profit+swap for ALL positions
    }
    """
    magic_filter = msg.get("magics")  # list of ints or None
    if magic_filter is not None:
        magic_set = set(int(m) for m in magic_filter)
    else:
        magic_set = None

    positions = mt5.positions_get()
    if positions is None:
        err = mt5.last_error()
        if err[0] != 1:
            return _error(msg, f"positions_get failed: {err}")
        return _ok(msg, {
            "positions": [],
            "own_margin": 0.0, "own_profit": 0.0,
            "total_margin": 0.0, "total_profit": 0.0
        })

    result_positions = []
    own_margin = 0.0
    own_profit = 0.0
    total_margin = 0.0
    total_profit = 0.0

    for p in positions:
        # Calculate margin for this position using MT5 authoritative calc
        order_type = mt5.ORDER_TYPE_BUY if p.type == 0 else mt5.ORDER_TYPE_SELL
        margin = mt5.order_calc_margin(order_type, p.symbol, p.volume, p.price_current)
        if margin is None:
            # Fallback: try with price_open if price_current fails
            margin = mt5.order_calc_margin(order_type, p.symbol, p.volume, p.price_open)
        pos_margin = round(margin, 2) if margin is not None else 0.0
        pos_profit = round(p.profit + getattr(p, "swap", 0.0), 2)

        total_margin += pos_margin
        total_profit += pos_profit

        is_own = magic_set is None or p.magic in magic_set

        if is_own:
            own_margin += pos_margin
            own_profit += pos_profit
            result_positions.append({
                "ticket":     p.ticket,
                "symbol":     p.symbol,
                "magic":      p.magic,
                "type":       "BUY" if p.type == 0 else "SELL",
                "volume":     p.volume,
                "margin":     pos_margin,
                "profit":     pos_profit,
                "swap":       round(getattr(p, "swap", 0.0), 2),
                "price_open": p.price_open,
            })

    return _ok(msg, {
        "positions":    result_positions,
        "own_margin":   round(own_margin, 2),
        "own_profit":   round(own_profit, 2),
        "total_margin": round(total_margin, 2),
        "total_profit": round(total_profit, 2),
    })


def handle_calc_profit(msg: dict) -> dict:
    """Calculate profit/loss for a hypothetical trade using MT5 OrderCalcProfit.

    This is the authoritative way to compute P/L for cross-pairs, JPY, metals,
    indices — MT5 handles all currency conversions internally.

    Input:  { "symbol": "GBPJPY", "action": "buy", "volume": 1.0,
              "price_open": 195.000, "price_close": 194.500 }
    Output: { "profit": -321.45 }
    """
    symbol = msg.get("symbol")
    action = msg.get("action", "buy").lower()
    volume = float(msg.get("volume", 1.0))
    price_open = msg.get("price_open")
    price_close = msg.get("price_close")

    if not symbol or price_open is None or price_close is None:
        return _error(msg, "missing symbol, price_open, or price_close")

    # Ensure symbol is visible
    if not mt5.symbol_select(symbol, True):
        return _error(msg, f"symbol_select failed for {symbol}: {mt5.last_error()}")

    order_type = mt5.ORDER_TYPE_BUY if action == "buy" else mt5.ORDER_TYPE_SELL

    profit = mt5.order_calc_profit(order_type, symbol, volume, price_open, price_close)
    if profit is None:
        err = mt5.last_error()
        return _error(msg, f"order_calc_profit failed: {err}")

    return _ok(msg, {"profit": round(profit, 4)})


# Command dispatch table
COMMANDS = {
    "HEARTBEAT":      handle_heartbeat,
    "ACCOUNT_INFO":   handle_account_info,
    "GET_POSITIONS":  handle_get_positions,
    "GET_RATES":      handle_get_rates,
    "SYMBOL_INFO":    handle_symbol_info,
    "ORDER_SEND":     handle_order_send,
    "ORDERS_GET":     handle_orders_get,
    "HISTORY_DEALS":  handle_history_deals,
    "CALC_LEVERAGE":  handle_calc_leverage,
    "CHECK_SYMBOLS":  handle_check_symbols,
    "CALC_PROFIT":    handle_calc_profit,
    "CALC_MARGIN":    handle_calc_margin,
    "CALC_POSITIONS_MARGIN": handle_calc_positions_margin,
    "COPY_RATES_RANGE": handle_copy_rates_range,
}

# ---------------------------------------------------------------------------
# TCP Server (asyncio)
# ---------------------------------------------------------------------------

class MT5Worker:
    def __init__(self, port: int, terminal_path: str,
                 login: int | None, password: str | None, server: str | None):
        self.port = port
        self.terminal_path = terminal_path
        self.login = login
        self.password = password
        self.server = server
        self._shutdown = False
        self._server: asyncio.Server | None = None

    def init_mt5(self) -> bool:
        """Initialize MT5 connection."""
        log.info(f"Initializing MT5: {self.terminal_path}")
        if not mt5.initialize(path=self.terminal_path):
            log.error(f"MT5 initialize failed: {mt5.last_error()}")
            return False

        if self.login and self.password and self.server:
            log.info(f"Logging in as {self.login} on {self.server}")
            if not mt5.login(self.login, password=self.password, server=self.server):
                log.error(f"MT5 login failed: {mt5.last_error()}")
                mt5.shutdown()
                return False

        info = mt5.terminal_info()
        acc = mt5.account_info()
        log.info(f"Connected: {info.name} | Account: {acc.login} | "
                 f"Balance: {acc.balance} | Mode: {acc.margin_mode}")
        return True

    async def handle_client(self, reader: asyncio.StreamReader,
                            writer: asyncio.StreamWriter):
        """Handle a single TCP client connection."""
        addr = writer.get_extra_info("peername")
        log.info(f"Client connected: {addr}")

        try:
            while not self._shutdown:
                line = await reader.readline()
                if not line:
                    break  # Client disconnected

                line_str = line.decode("utf-8").strip()
                if not line_str:
                    continue

                # Parse request
                try:
                    msg = json.loads(line_str)
                except json.JSONDecodeError as e:
                    resp = {"id": None, "status": "error",
                            "message": f"invalid JSON: {e}"}
                    await self._send(writer, resp)
                    continue

                cmd = msg.get("cmd", "").upper()
                msg_id = msg.get("id")

                # Handle SHUTDOWN command
                if cmd == "SHUTDOWN":
                    log.info("SHUTDOWN command received")
                    await self._send(writer, {"id": msg_id, "status": "ok",
                                              "data": {"message": "shutting down"}})
                    self._shutdown = True
                    self.stop()  # Close the TCP server so process exits
                    break

                # Dispatch to handler
                handler = COMMANDS.get(cmd)
                if handler is None:
                    resp = _error(msg, f"unknown command: {cmd}")
                else:
                    try:
                        # Run MT5 calls in thread pool (they're blocking)
                        resp = await asyncio.get_event_loop().run_in_executor(
                            None, handler, msg
                        )
                    except Exception as e:
                        log.exception(f"Handler error for {cmd}")
                        resp = _error(msg, f"handler exception: {e}")

                await self._send(writer, resp)

                # Log: routine polling commands at DEBUG, actions at INFO
                if cmd in ("GET_POSITIONS", "ACCOUNT_INFO", "GET_RATES", "SERVER_TIME",
                          "CALC_POSITIONS_MARGIN"):
                    if cmd == "GET_RATES":
                        bar_count = len(resp.get("data", [])) if resp.get("status") == "ok" else 0
                        log.debug(f"CMD: {cmd} id={msg_id} â†’ {resp.get('status')} ({bar_count} bars)")
                    else:
                        log.debug(f"CMD: {cmd} id={msg_id} â†’ {resp.get('status')}")
                elif cmd == "COPY_RATES_RANGE":
                    data = resp.get("data", {})
                    bar_count = data.get("count", 0) if isinstance(data, dict) else 0
                    log.info(f"CMD: {cmd} id={msg_id} -> {resp.get('status')} ({bar_count} bars)")
                else:
                    log.info(f"CMD: {cmd} id={msg_id} â†’ {resp.get('status')}")

        except asyncio.CancelledError:
            pass
        except Exception:
            log.exception("Client handler error")
        finally:
            writer.close()
            try:
                await writer.wait_closed()
            except Exception:
                pass
            log.info(f"Client disconnected: {addr}")

    async def _send(self, writer: asyncio.StreamWriter, resp: dict):
        """Send JSON response followed by newline."""
        data = json.dumps(resp, ensure_ascii=False) + "\n"
        writer.write(data.encode("utf-8"))
        await writer.drain()

    async def run(self):
        """Start TCP server and serve until shutdown."""
        self._server = await asyncio.start_server(
            self.handle_client, "127.0.0.1", self.port
        )
        log.info(f"MT5 Worker listening on 127.0.0.1:{self.port}")

        async with self._server:
            try:
                await self._server.serve_forever()
            except asyncio.CancelledError:
                pass

        log.info("TCP server stopped")

    def stop(self):
        """Signal the server to stop."""
        self._shutdown = True
        if self._server:
            self._server.close()


# ---------------------------------------------------------------------------
# Entry point
# ---------------------------------------------------------------------------

def parse_args():
    p = argparse.ArgumentParser(description="MT5 Worker â€” TCP bridge to MetaTrader 5")
    p.add_argument("--port", type=int, required=True, help="TCP port to listen on")
    p.add_argument("--terminal-path", required=True, help="Path to terminal64.exe")
    p.add_argument("--login", type=int, default=None, help="MT5 login (optional)")
    p.add_argument("--password", type=str, default=None, help="MT5 password (optional)")
    p.add_argument("--server", type=str, default=None, help="MT5 server name (optional)")
    return p.parse_args()


def main():
    args = parse_args()

    worker = MT5Worker(
        port=args.port,
        terminal_path=args.terminal_path,
        login=args.login,
        password=args.password,
        server=args.server,
    )

    # Initialize MT5
    if not worker.init_mt5():
        sys.exit(1)

    # Handle Ctrl+C gracefully
    loop = asyncio.new_event_loop()

    def on_signal():
        log.info("Signal received, stopping...")
        worker.stop()

    if sys.platform != "win32":
        for sig in (signal.SIGINT, signal.SIGTERM):
            loop.add_signal_handler(sig, on_signal)

    try:
        loop.run_until_complete(worker.run())
    except KeyboardInterrupt:
        log.info("KeyboardInterrupt, stopping...")
        worker.stop()
    finally:
        mt5.shutdown()
        loop.close()
        log.info("MT5 Worker shut down cleanly")


if __name__ == "__main__":
    main()
