"""
FX Intraday Strategy — RSI + DVWAP Mean Reversion
===================================================
12 combos across 12 symbols, M15/M30/H1.
Signal-only: daemon handles lot sizing, risk gates, execution.

Combo types:
  RSI  — RSI extreme + EMA trend filter → enter counter-trend
  DVWAP — Daily VWAP z-score extreme → fade back to VWAP

Each combo: one symbol, one timeframe, one direction at a time.
Weight (full=1.0, half=0.5) is handled by daemon per-symbol sizing.
"""
import json
import math
from datetime import datetime, timezone
from typing import Any


class Strategy:
    """FX Intraday mean-reversion strategy."""

    def __init__(self, config: dict):
        self.combos = []
        for c in config.get("combos", []):
            # New schema: directions → BOTH → strat/daemon
            if "directions" in c:
                dir_val = c["directions"].get("BOTH", {})
                flat = {
                    "symbol": c["sym"],
                    "aclass": c.get("aclass", "forex"),
                }
                strat = dir_val.get("strat", {})
                daemon = dir_val.get("daemon", {})
                flat["timeframe"] = strat.pop("tf", "M15")
                flat.update(strat)
                flat["weight"] = daemon.get("size_r", 1.0)
            else:
                flat = dict(c)
                # Legacy: rename if needed
                if "sym" in flat and "symbol" not in flat:
                    flat["symbol"] = flat.pop("sym")
                if "tf" in flat and "timeframe" not in flat:
                    flat["timeframe"] = flat.pop("tf")

            combo = ComboConfig(flat)
            self.combos.append(combo)

        params = config.get("params", {})
        self.history_bars = params.get("history_bars", config.get("history_bars", 500))
        self.r_cap = params.get("r_cap", None)

        # Track last signal bar per combo to avoid duplicate signals
        # on the same bar (keyed by combo index)
        self.last_signal_bar: dict[int, int] = {}

    def get_requirements(self) -> dict:
        """Tell daemon what symbols/timeframes/history we need."""
        symbols = []
        timeframes = {}
        for c in self.combos:
            if c.symbol not in symbols:
                symbols.append(c.symbol)
            timeframes[c.symbol] = c.timeframe

        reqs = {
            "symbols": symbols,
            "timeframes": timeframes,
            "history_bars": self.history_bars
        }
        if self.r_cap is not None:
            reqs["r_cap"] = self.r_cap

        return reqs

    def on_bars(self, bars_data: dict, positions: list) -> list:
        """
        Called on each tick. Compute indicators, generate signals.

        bars_data: {symbol: [{"time": epoch, "open", "high", "low", "close", "volume"}, ...]}
        positions: [{"ticket", "symbol", "direction", "volume", "price_open", "sl", "tp", "profit", "open_time"}]
        """
        actions = []

        # Build set of symbols with open positions
        open_symbols = set()
        for pos in positions:
            open_symbols.add(pos.get("symbol", ""))

        for idx, combo in enumerate(self.combos):
            # Skip if already have a position on this symbol
            if combo.symbol in open_symbols:
                continue

            bars = bars_data.get(combo.symbol)
            if not bars or len(bars) < combo.min_warmup:
                continue

            # Extract OHLC arrays
            times = [b["time"] for b in bars]
            opens = [b["open"] for b in bars]
            highs = [b["high"] for b in bars]
            lows = [b["low"] for b in bars]
            closes = [b["close"] for b in bars]

            # Compute ATR (needed by both types)
            atr = calc_atr(highs, lows, closes, combo.atr_period)
            if atr is None or atr <= 0:
                continue

            # Generate signal based on combo type
            signal = None
            if combo.type == "RSI":
                signal = self._check_rsi(combo, closes, highs, lows, atr)
            elif combo.type == "DVWAP":
                signal = self._check_dvwap(combo, times, highs, lows, closes, atr)

            if signal is None:
                continue

            # Dedup: don't signal twice on same bar
            bar_time = times[-1]
            if self.last_signal_bar.get(idx) == bar_time:
                continue
            self.last_signal_bar[idx] = bar_time

            direction, sl_price, tp_price = signal
            current_price = closes[-1]

            actions.append({
                "action": "ENTER",
                "symbol": combo.symbol,
                "direction": direction,
                "sl_price": sl_price,
                "tp_price": tp_price,
                "comment": f"fx_{combo.type}_{combo.timeframe}",
                "signal_data": json.dumps({
                    "type": combo.type,
                    "tf": combo.timeframe,
                    "weight": combo.weight,
                    "atr": round(atr, 5),
                }),
            })

        return actions

    # ─── RSI Signal ──────────────────────────────────────────────

    def _check_rsi(self, combo: "ComboConfig",
                   closes: list, highs: list, lows: list,
                   atr: float) -> tuple | None:
        """
        RSI mean-reversion:
          LONG  if RSI < rsi_lo AND close > EMA (uptrend)
          SHORT if RSI > rsi_hi AND close < EMA (downtrend)
        SL = sl_mult × ATR from entry
        TP = rr × SL distance from entry
        """
        n = len(closes)

        rsi = calc_rsi(closes, combo.rsi_period)
        if rsi is None:
            return None

        ema = calc_ema(closes, combo.ema_period)
        if ema is None:
            return None

        price = closes[-1]
        sl_dist = combo.sl_mult * atr

        if rsi < combo.rsi_lo and price > ema:
            # LONG: RSI oversold, but above EMA (uptrend bounce)
            sl_price = price - sl_dist
            tp_price = price + combo.rr * sl_dist
            return ("LONG", round(sl_price, 6), round(tp_price, 6))

        elif rsi > combo.rsi_hi and price < ema:
            # SHORT: RSI overbought, but below EMA (downtrend bounce)
            sl_price = price + sl_dist
            tp_price = price - combo.rr * sl_dist
            return ("SHORT", round(sl_price, 6), round(tp_price, 6))

        return None

    # ─── DVWAP Signal ────────────────────────────────────────────

    def _check_dvwap(self, combo: "ComboConfig",
                     times: list, highs: list, lows: list,
                     closes: list, atr: float) -> tuple | None:
        """
        Daily VWAP mean-reversion:
          Compute intraday VWAP + std from TP = (H+L+C)/3
          z-score = (close - VWAP) / std
          LONG  if z < -sigma (price below VWAP)
          SHORT if z > +sigma (price above VWAP)
          TP = VWAP (mean-revert to VWAP)
          SL = sl_mult × ATR
        """
        n = len(closes)
        if n < 20:
            return None

        # Find today's bars (bars sharing same date as last bar)
        last_dt = epoch_to_datetime(times[-1])
        last_date = last_dt.date()

        # Walk backwards to find start of today
        today_start = n
        for i in range(n - 1, -1, -1):
            bar_date = epoch_to_datetime(times[i]).date()
            if bar_date == last_date:
                today_start = i
            else:
                break

        today_count = n - today_start
        if today_count < 20:
            return None

        # Compute intraday VWAP and std
        tp_vals = []
        for i in range(today_start, n):
            tp_vals.append((highs[i] + lows[i] + closes[i]) / 3.0)

        cum_sum = 0.0
        cum_sq = 0.0
        vwap = 0.0
        std = 0.0
        for k, tp in enumerate(tp_vals):
            cum_sum += tp
            cum_sq += tp * tp
            count = k + 1
            vwap = cum_sum / count
            if count >= 10:
                variance = cum_sq / count - vwap * vwap
                std = math.sqrt(max(variance, 0))

        if std <= 0:
            return None

        price = closes[-1]
        z = (price - vwap) / std

        sl_dist = combo.sl_mult * atr
        vwap_dist = abs(price - vwap)
        tp_dist_r = vwap_dist / sl_dist if sl_dist > 0 else 0

        # Filter: TP must be meaningful (at least 0.1R)
        if tp_dist_r < 0.1:
            return None

        if z > combo.sigma:
            # SHORT: price far above VWAP, fade down
            sl_price = price + sl_dist
            tp_price = vwap  # Mean-revert to VWAP
            return ("SHORT", round(sl_price, 6), round(tp_price, 6))

        elif z < -combo.sigma:
            # LONG: price far below VWAP, fade up
            sl_price = price - sl_dist
            tp_price = vwap
            return ("LONG", round(sl_price, 6), round(tp_price, 6))

        return None

    # ─── State Persistence ───────────────────────────────────────

    def save_state(self) -> dict:
        return {"last_signal_bar": self.last_signal_bar}

    def restore_state(self, state: dict):
        saved = state.get("last_signal_bar", {})
        # JSON keys are strings, convert back to int
        self.last_signal_bar = {int(k): v for k, v in saved.items()}


# ═════════════════════════════════════════════════════════════════
#  COMBO CONFIG
# ═════════════════════════════════════════════════════════════════

class ComboConfig:
    """Parsed combo configuration from config.json."""

    def __init__(self, cfg: dict):
        self.symbol = cfg["symbol"]
        self.timeframe = cfg["timeframe"]
        self.type = cfg["type"]  # "RSI" or "DVWAP"
        self.weight = cfg.get("weight", 1.0)
        self.atr_period = cfg.get("atr_period", 14)

        # RSI params
        self.rsi_period = cfg.get("rsi_period", 14)
        self.rsi_lo = cfg.get("rsi_lo", 30)
        self.rsi_hi = cfg.get("rsi_hi", 70)
        self.ema_period = cfg.get("ema_period", 200)
        self.sl_mult = cfg.get("sl_mult", 1.0)
        self.rr = cfg.get("rr", 1.5)

        # DVWAP params
        self.sigma = cfg.get("sigma", 1.5)

        # Min warmup bars needed
        if self.type == "RSI":
            self.min_warmup = max(self.rsi_period, self.ema_period, self.atr_period) + 20
        else:
            self.min_warmup = max(self.atr_period, 30) + 20


# ═════════════════════════════════════════════════════════════════
#  INDICATOR FUNCTIONS
# ═════════════════════════════════════════════════════════════════

def calc_ema(values: list, period: int) -> float | None:
    """Compute EMA of values, return last value."""
    if len(values) < period:
        return None
    k = 2.0 / (period + 1)
    ema = values[0]
    for i in range(1, len(values)):
        ema = values[i] * k + ema * (1 - k)
    return ema


def calc_rsi(closes: list, period: int) -> float | None:
    """Compute RSI using Wilder's smoothing, return last value."""
    n = len(closes)
    if n < period + 1:
        return None

    # Initial average gain/loss from first 'period' changes
    avg_gain = 0.0
    avg_loss = 0.0
    for i in range(1, period + 1):
        delta = closes[i] - closes[i - 1]
        if delta > 0:
            avg_gain += delta
        else:
            avg_loss -= delta
    avg_gain /= period
    avg_loss /= period

    # Wilder's smoothing for remaining bars
    alpha = 1.0 / period
    for i in range(period + 1, n):
        delta = closes[i] - closes[i - 1]
        gain = delta if delta > 0 else 0.0
        loss = -delta if delta < 0 else 0.0
        avg_gain = avg_gain * (1 - alpha) + gain * alpha
        avg_loss = avg_loss * (1 - alpha) + loss * alpha

    if avg_loss == 0:
        return 100.0
    rs = avg_gain / avg_loss
    return 100.0 - 100.0 / (1.0 + rs)


def calc_atr(highs: list, lows: list, closes: list, period: int) -> float | None:
    """Compute ATR using Wilder's smoothing, return last value."""
    n = len(closes)
    if n < period + 1:
        return None

    # True range series
    alpha = 1.0 / period

    # First TR
    atr = highs[0] - lows[0]

    for i in range(1, n):
        tr = max(
            highs[i] - lows[i],
            abs(highs[i] - closes[i - 1]),
            abs(lows[i] - closes[i - 1])
        )
        atr = atr * (1 - alpha) + tr * alpha

    return atr


def epoch_to_datetime(epoch: int) -> datetime:
    """Convert epoch seconds to UTC datetime."""
    return datetime.fromtimestamp(epoch, tz=timezone.utc)
