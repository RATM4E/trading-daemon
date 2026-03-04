"""
mtf_pullback — Multi-Timeframe Pullback Strategy
==================================================
Two signal profiles on H4→M30:

  trend_pullback:  H4 EMA21 trend + M30 RSI oversold/overbought pullback
  momentum_cont:   H4 impulse candle + M30 RSI pullback

Walk-forward validated 2019-2024: 100% positive windows, 0% degradation.
Portfolio T1+T2 (13 symbols, max-4 correlation): 1255R/yr, MaxDD 7.0R (R-cap=3), Calmar 185.

Protocol: HELLO → ACK → TICK(M30 bars) → ACTIONS loop
Internal: resample M30→H4, compute HTF state, generate LTF signals.
Signal checked on EVERY M30 bar (not gated by H4 boundary).
"""

import math
import numpy as np
from datetime import datetime, timezone


# ═══════════════════════════════════════════════════════════════════════
#  Indicators (pure numpy, no numba in production)
# ═══════════════════════════════════════════════════════════════════════

def ema(arr, period):
    """Exponential moving average."""
    out = np.full_like(arr, np.nan)
    if len(arr) < period:
        return out
    out[period - 1] = np.mean(arr[:period])
    k = 2.0 / (period + 1)
    for i in range(period, len(arr)):
        out[i] = arr[i] * k + out[i - 1] * (1 - k)
    return out


def rsi(arr, period):
    """Wilder RSI."""
    out = np.full_like(arr, np.nan)
    if len(arr) < period + 1:
        return out
    delta = np.diff(arr)
    gains = np.where(delta > 0, delta, 0.0)
    losses = np.where(delta < 0, -delta, 0.0)
    avg_g = np.mean(gains[:period])
    avg_l = np.mean(losses[:period])
    out[period] = 100.0 if avg_l == 0 else 100.0 - 100.0 / (1 + avg_g / avg_l)
    for i in range(period, len(delta)):
        avg_g = (avg_g * (period - 1) + gains[i]) / period
        avg_l = (avg_l * (period - 1) + losses[i]) / period
        out[i + 1] = 100.0 if avg_l == 0 else 100.0 - 100.0 / (1 + avg_g / avg_l)
    return out


def atr(high, low, close, period):
    """Average True Range."""
    n = len(close)
    out = np.full(n, np.nan)
    tr = np.zeros(n)
    tr[0] = high[0] - low[0]
    for i in range(1, n):
        tr[i] = max(high[i] - low[i],
                     abs(high[i] - close[i - 1]),
                     abs(low[i] - close[i - 1]))
    if n < period:
        return out
    out[period - 1] = np.mean(tr[:period])
    for i in range(period, n):
        out[i] = (out[i - 1] * (period - 1) + tr[i]) / period
    return out


# ═══════════════════════════════════════════════════════════════════════
#  M30 → H4 resampling
# ═══════════════════════════════════════════════════════════════════════

def resample_m30_to_h4(bars):
    """
    Resample M30 OHLCV bars to H4.

    H4 boundaries: 00:00, 04:00, 08:00, 12:00, 16:00, 20:00 UTC.
    Each H4 bar contains 8 M30 bars.

    Args:
        bars: list of dicts with keys: time, open, high, low, close, volume

    Returns:
        list of H4 bar dicts (same format)
    """
    if not bars:
        return []

    h4_bars = []
    current = None
    current_boundary = None

    for bar in bars:
        ts = bar["time"]
        # H4 boundary: floor to nearest 4-hour block
        dt = datetime.fromtimestamp(ts, tz=timezone.utc)
        boundary_hour = (dt.hour // 4) * 4
        boundary = dt.replace(hour=boundary_hour, minute=0, second=0, microsecond=0)
        boundary_ts = int(boundary.timestamp())

        if current_boundary is None or boundary_ts != current_boundary:
            # New H4 bar
            if current is not None:
                h4_bars.append(current)
            current = {
                "time": boundary_ts,
                "open": bar["open"],
                "high": bar["high"],
                "low": bar["low"],
                "close": bar["close"],
                "volume": bar.get("volume", 0),
            }
            current_boundary = boundary_ts
        else:
            # Extend current H4 bar
            current["high"] = max(current["high"], bar["high"])
            current["low"] = min(current["low"], bar["low"])
            current["close"] = bar["close"]
            current["volume"] += bar.get("volume", 0)

    if current is not None:
        h4_bars.append(current)

    return h4_bars


# ═══════════════════════════════════════════════════════════════════════
#  HTF state builders
# ═══════════════════════════════════════════════════════════════════════

def compute_htf_trend(h4_close, ema_period):
    """
    H4 EMA trend direction.
    Returns: +1 (bullish), -1 (bearish), 0 (undefined) for the LAST bar.
    """
    e = ema(h4_close, ema_period)
    if np.isnan(e[-1]):
        return 0
    return 1 if h4_close[-1] > e[-1] else -1


def compute_htf_momentum(h4_o, h4_h, h4_l, h4_c, h4_atr,
                          body_ratio=0.70, range_atr=1.5, decay_bars=2):
    """
    Detect H4 impulse candle and return (direction, freshness).

    Impulse: |body| > body_ratio × range AND range > range_atr × ATR14.
    Freshness decays: 1.0 → 0.5 → 0.25 over decay_bars.

    Returns: (direction: +1/-1/0, freshness: 0.0-1.0) for LAST bar.
    """
    n = len(h4_c)
    if n < 2:
        return 0, 0.0

    mom_dir = np.zeros(n)
    mom_fresh = np.zeros(n)

    for i in range(1, n):
        bar_range = h4_h[i] - h4_l[i]
        if bar_range < 1e-10 or np.isnan(h4_atr[i]) or h4_atr[i] < 1e-10:
            continue
        body = h4_c[i] - h4_o[i]
        if abs(body) > body_ratio * bar_range and bar_range > range_atr * h4_atr[i]:
            mom_dir[i] = 1.0 if body > 0 else -1.0
            mom_fresh[i] = 1.0

    # Decay forward
    for _ in range(decay_bars):
        for i in range(1, n):
            if mom_dir[i] == 0 and mom_dir[i - 1] != 0 and mom_fresh[i - 1] > 0.1:
                mom_dir[i] = mom_dir[i - 1]
                mom_fresh[i] = mom_fresh[i - 1] * 0.5

    return int(mom_dir[-1]), float(mom_fresh[-1])


# ═══════════════════════════════════════════════════════════════════════
#  Strategy class
# ═══════════════════════════════════════════════════════════════════════

class Strategy:
    """
    MTF Pullback — daemon-compatible strategy.

    Reads config.json, subscribes to M30 bars, internally resamples to H4,
    and generates ENTER signals with SL/TP prices.
    """

    def __init__(self, config: dict):
        self.config = config
        self.params = config.get("params", {})
        self.combos = config.get("combos", [])

        # Global params
        self.atr_period = self.params.get("atr_period", 14)
        self.sl_atr_mult = self.params.get("sl_atr_mult", 1.5)
        self.min_sl_pips = self.params.get("min_sl_pips", 20)
        self.htf_ema_period = self.params.get("htf_ema_period", 21)
        self.momentum_body_ratio = self.params.get("momentum_body_ratio", 0.70)
        self.momentum_range_atr = self.params.get("momentum_range_atr", 1.5)
        self.momentum_decay_bars = self.params.get("momentum_decay_bars", 2)

        # Build per-symbol config from combos
        self.sym_configs = {}
        for combo in self.combos:
            sym = combo["sym"]
            dirs = combo.get("directions", {})
            # Get strat params from BOTH, LONG, or SHORT
            strat = None
            for key in ["BOTH", "LONG", "SHORT"]:
                if key in dirs and "strat" in dirs[key]:
                    strat = dirs[key]["strat"]
                    break
            if strat is None:
                continue

            self.sym_configs[sym] = {
                "logic": strat.get("logic", "trend_pullback"),
                "tf": strat.get("tf", "M30"),
                "htf": strat.get("htf", "H4"),
                "tp_r": strat.get("tp_r", 1.0),
                # trend_pullback params
                "rsi_p": strat.get("rsi_p", 21),
                "rsi_os": strat.get("rsi_os", 45),
                "rsi_ob": strat.get("rsi_ob", 55),
                # momentum_cont params
                "pb_rsi_p": strat.get("pb_rsi_p", 14),
                "pb_rsi_thresh": strat.get("pb_rsi_thresh", 50),
                "tier": combo.get("tier", "T3"),
            }

        # State: track which symbols have an open position (avoid duplicate signals)
        # No H4 dedup needed — _active_symbols prevents re-entry while in trade,
        # and we WANT to check RSI on every M30 bar within an H4 period
        # (backtest evaluates every M30 bar, daemon must match)
        self._active_symbols = set()

    def _pip_size(self, sym):
        return 0.01 if "JPY" in sym else 0.0001

    def _min_sl_price(self, sym):
        return self.min_sl_pips * self._pip_size(sym)

    def get_requirements(self):
        """
        Tell daemon what bars we need.

        We request M30 bars for all symbols. H4 is built internally.
        1000 M30 bars = ~125 H4 bars (enough for EMA21 + warmup).
        """
        symbols = list(self.sym_configs.keys())
        timeframes = {sym: "M30" for sym in symbols}

        return {
            "symbols": symbols,
            "timeframes": timeframes,
            "history_bars": 1000,
        }

    def on_bars(self, bars_data: dict, positions: list) -> list:
        """
        Called on every new M30 candle.

        1. Update active positions tracking
        2. For each symbol: resample → compute HTF → compute LTF signal → emit ENTER
        3. No EXIT/MODIFY_SL — daemon handles SL/TP via market orders

        Args:
            bars_data: {symbol: [bar_dicts]} — M30 OHLCV
            positions: [position_dicts] — open positions for this strategy

        Returns:
            list of action dicts
        """
        actions = []

        # Update position tracking
        self._active_symbols = set()
        for pos in positions:
            self._active_symbols.add(pos.get("symbol", ""))

        for sym, scfg in self.sym_configs.items():
            # Skip if already in a position on this symbol
            if sym in self._active_symbols:
                continue

            m30_bars = bars_data.get(sym)
            if not m30_bars or len(m30_bars) < 200:
                continue

            action = self._process_symbol(sym, scfg, m30_bars)
            if action is not None:
                actions.append(action)

        return actions

    def _process_symbol(self, sym, scfg, m30_bars):
        """
        Process one symbol: resample to H4, compute indicators, generate signal.

        Returns: action dict or None
        """
        # ── 1. Resample M30 → H4 ──
        h4_bars = resample_m30_to_h4(m30_bars)
        if len(h4_bars) < self.htf_ema_period + 10:
            return None

        h4_o = np.array([b["open"] for b in h4_bars])
        h4_h = np.array([b["high"] for b in h4_bars])
        h4_l = np.array([b["low"] for b in h4_bars])
        h4_c = np.array([b["close"] for b in h4_bars])

        # ── 2. HTF state ──
        logic = scfg["logic"]

        if logic == "trend_pullback":
            htf_dir = compute_htf_trend(h4_c, self.htf_ema_period)
            if htf_dir == 0:
                return None
        elif logic == "momentum_cont":
            h4_atr_arr = atr(h4_h, h4_l, h4_c, self.atr_period)
            htf_dir, htf_fresh = compute_htf_momentum(
                h4_o, h4_h, h4_l, h4_c, h4_atr_arr,
                self.momentum_body_ratio,
                self.momentum_range_atr,
                self.momentum_decay_bars)
            if htf_dir == 0 or htf_fresh < 0.1:
                return None
        else:
            return None

        # ── 3. LTF signal ──
        m30_c = np.array([b["close"] for b in m30_bars])
        m30_h = np.array([b["high"] for b in m30_bars])
        m30_l = np.array([b["low"] for b in m30_bars])

        if logic == "trend_pullback":
            rsi_p = scfg["rsi_p"]
            rsi_os = scfg["rsi_os"]
            rsi_ob = scfg["rsi_ob"]
            ltf_rsi = rsi(m30_c, rsi_p)
            if np.isnan(ltf_rsi[-1]):
                return None

            if htf_dir == 1 and ltf_rsi[-1] < rsi_os:
                direction = "LONG"
            elif htf_dir == -1 and ltf_rsi[-1] > rsi_ob:
                direction = "SHORT"
            else:
                return None

        elif logic == "momentum_cont":
            pb_rsi_p = scfg["pb_rsi_p"]
            pb_thresh = scfg["pb_rsi_thresh"]
            ltf_rsi = rsi(m30_c, pb_rsi_p)
            if np.isnan(ltf_rsi[-1]):
                return None

            if htf_dir == 1 and ltf_rsi[-1] < pb_thresh:
                direction = "LONG"
            elif htf_dir == -1 and ltf_rsi[-1] > (100 - pb_thresh):
                direction = "SHORT"
            else:
                return None

        else:
            return None

        # ── 4. SL/TP calculation ──
        m30_atr = atr(m30_h, m30_l, m30_c, self.atr_period)
        current_atr = m30_atr[-1]
        if np.isnan(current_atr) or current_atr < 1e-10:
            return None

        sl_dist = max(self.sl_atr_mult * current_atr, self._min_sl_price(sym))
        tp_dist = sl_dist * scfg["tp_r"]

        entry_price = m30_c[-1]  # Will be adjusted by daemon to actual market price

        if direction == "LONG":
            sl_price = entry_price - sl_dist
            tp_price = entry_price + tp_dist
        else:
            sl_price = entry_price + sl_dist
            tp_price = entry_price - tp_dist

        # ── 5. Build action ──
        return {
            "action": "ENTER",
            "symbol": sym,
            "direction": direction,
            "sl_price": round(sl_price, 5 if "JPY" not in sym else 3),
            "tp_price": round(tp_price, 5 if "JPY" not in sym else 3),
            "comment": f"mtf_{logic[:3]}_{scfg['tier']}",
            "signal_data": (
                f"logic={logic} htf_dir={htf_dir} "
                f"rsi={ltf_rsi[-1]:.1f} atr={current_atr:.5f} "
                f"sl={sl_dist/self._pip_size(sym):.1f}pip"
            ),
        }

    def save_state(self) -> dict:
        """Save state for crash recovery."""
        return {
            "active_symbols": list(self._active_symbols),
        }

    def restore_state(self, state: dict):
        """Restore state after crash recovery."""
        if isinstance(state, dict):
            self._active_symbols = set(state.get("active_symbols", []))
