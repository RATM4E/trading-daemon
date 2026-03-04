"""
Signal Test Strategy -- Realistic Virtual Trading Test
=======================================================
RSI mean-reversion strategy for testing the full virtual trading pipeline.
Generates realistic entries, trailing SL moves, and proper exits.

Logic (fixed -- no contradictory filters):
  - LONG when RSI(14) < 35 (oversold bounce)
  - SHORT when RSI(14) > 65 (overbought fade)
  - SL = 2.0 x ATR(14) from entry
  - TP = 3.0 x ATR(14) from entry (1.5:1 R:R)
  - Trailing SL: move SL to breakeven at +1 ATR, then trail at 1.5 ATR behind price
  - Max 1 position per symbol (any direction)
  - Max 4 total positions across all symbols
  - Exit on signal reversal (RSI crosses back through neutral)
  - Cooldown: skip re-entry on same symbol for 6 bars after exit

Tests all virtual paths:
  - ENTER -> virtual position creation
  - MODIFY_SL -> trailing stop updates
  - EXIT -> signal-based close
  - SL/TP hits handled by VirtualTracker

Usage:
  Place in strategies/signal_test_strategy/ alongside config.json
"""

import json
import time
from datetime import datetime


def log(msg: str):
    ts = datetime.now().strftime("%H:%M:%S.%f")[:-3]
    print(f"[signal_test {ts}] {msg}", flush=True)


def compute_atr(highs, lows, closes, period=14):
    """Wilder's ATR."""
    n = len(closes)
    if n < 2:
        return 0.0
    trs = []
    for i in range(1, n):
        tr = max(
            highs[i] - lows[i],
            abs(highs[i] - closes[i - 1]),
            abs(lows[i] - closes[i - 1])
        )
        trs.append(tr)
    if len(trs) < period:
        return sum(trs) / len(trs) if trs else 0.0
    atr = sum(trs[:period]) / period
    for i in range(period, len(trs)):
        atr = (atr * (period - 1) + trs[i]) / period
    return atr


def compute_rsi(closes, period=14):
    """Wilder's RSI."""
    if len(closes) < period + 1:
        return 50.0  # neutral
    gains, losses = [], []
    for i in range(1, len(closes)):
        delta = closes[i] - closes[i - 1]
        gains.append(max(delta, 0))
        losses.append(max(-delta, 0))

    if len(gains) < period:
        return 50.0

    avg_gain = sum(gains[:period]) / period
    avg_loss = sum(losses[:period]) / period

    for i in range(period, len(gains)):
        avg_gain = (avg_gain * (period - 1) + gains[i]) / period
        avg_loss = (avg_loss * (period - 1) + losses[i]) / period

    if avg_loss == 0:
        return 100.0
    rs = avg_gain / avg_loss
    return 100.0 - 100.0 / (1.0 + rs)


def compute_ema(closes, period):
    """Exponential moving average."""
    if not closes:
        return 0.0
    if len(closes) < period:
        return sum(closes) / len(closes)
    k = 2.0 / (period + 1)
    ema = sum(closes[:period]) / period
    for i in range(period, len(closes)):
        ema = closes[i] * k + ema * (1 - k)
    return ema


class Strategy:
    """RSI mean-reversion with trailing SL -- tests all virtual trading paths."""

    # Defaults (overridden by config)
    SYMBOLS = ["EURUSD", "GBPUSD", "USDJPY", "AUDUSD", "XAUUSD", "BTCUSD"]
    TF = "M5"

    # Signal parameters -- relaxed for active trading
    RSI_PERIOD = 14
    RSI_OVERSOLD = 35        # was 30 -- too strict
    RSI_OVERBOUGHT = 65      # was 70 -- too strict
    RSI_EXIT_LONG = 55       # exit LONG when RSI recovers past this
    RSI_EXIT_SHORT = 45      # exit SHORT when RSI drops past this

    # Risk parameters
    SL_ATR_MULT = 2.0
    TP_ATR_MULT = 3.0        # 1.5:1 R:R
    TRAIL_ACTIVATE = 1.0     # move SL to BE after +1 ATR profit
    TRAIL_DISTANCE = 1.5     # then trail at 1.5 ATR behind price

    MAX_POSITIONS = 4
    COOLDOWN_BARS = 6        # skip re-entry for N ticks after exit

    def __init__(self, config: dict):
        self.config = config
        self.tick_count = 0

        # Position tracking: symbol -> {direction, entry, atr_at_entry, trail_active}
        self.tracked = {}

        # Cooldown tracking: symbol -> tick_count when last exited
        self.cooldowns = {}

        # --- Read config: combos format (standard) or flat fallback ---
        params = config.get("params", {})
        combos = config.get("combos", [])

        if combos:
            self.symbols = list(dict.fromkeys(c["sym"] for c in combos))
            # New schema: tf/sl_mult inside directions.*.strat
            first = combos[0]
            if "directions" in first:
                first_dir = next(iter(first["directions"].values()))
                strat = first_dir.get("strat", {})
                self.tf = strat.get("tf", self.TF)
                self.sl_atr_mult = strat.get("sl_mult", self.SL_ATR_MULT)
            else:
                self.tf = first.get("tf", self.TF)
                self.sl_atr_mult = first.get("sl_mult", self.SL_ATR_MULT)
        else:
            self.symbols = config.get("symbols", self.SYMBOLS)
            self.tf = config.get("timeframe", self.TF)
            self.sl_atr_mult = config.get("sl_atr_mult", self.SL_ATR_MULT)

        # Params (nested or flat)
        self.rsi_period = params.get("rsi_period", self.RSI_PERIOD)
        self.tp_atr_mult = params.get("tp_atr_mult", config.get("tp_atr_mult", self.TP_ATR_MULT))
        self.rsi_oversold = params.get("rsi_oversold", config.get("rsi_oversold", self.RSI_OVERSOLD))
        self.rsi_overbought = params.get("rsi_overbought", config.get("rsi_overbought", self.RSI_OVERBOUGHT))
        self.trail_activate = params.get("trail_activate_atr", self.TRAIL_ACTIVATE)
        self.trail_distance = params.get("trail_distance_atr", self.TRAIL_DISTANCE)
        self.max_positions = config.get("max_positions", self.MAX_POSITIONS)
        self.cooldown_bars = config.get("cooldown_bars", self.COOLDOWN_BARS)

        # RSI exit levels: halfway back to neutral from entry threshold
        self.rsi_exit_long = params.get("rsi_exit_long", self.RSI_EXIT_LONG)
        self.rsi_exit_short = params.get("rsi_exit_short", self.RSI_EXIT_SHORT)

        log(f"Init: {len(self.symbols)} symbols on {self.tf}, "
            f"SL={self.sl_atr_mult}xATR, TP={self.tp_atr_mult}xATR, "
            f"RSI {self.rsi_oversold}/{self.rsi_overbought}, "
            f"exit RSI {self.rsi_exit_long}/{self.rsi_exit_short}, "
            f"cooldown={self.cooldown_bars}, max_pos={self.max_positions}")

    def get_requirements(self) -> dict:
        return {
            "symbols": self.symbols,
            "timeframes": {sym: self.tf for sym in self.symbols},
            "history_bars": max(100, self.rsi_period + 20),
        }

    def on_bars(self, bars: dict, positions: list) -> list:
        self.tick_count += 1
        actions = []

        # Build position map from live data
        pos_by_symbol = {}
        for p in positions:
            pos_by_symbol[p["symbol"]] = p

        # Sync tracked state with actual positions
        self._sync_tracked(pos_by_symbol)

        open_count = len(positions)

        log(f"=== Tick #{self.tick_count} === positions={open_count}, "
            f"tracked={list(self.tracked.keys())}")

        # Process each symbol
        for sym in self.symbols:
            sym_bars = bars.get(sym, [])
            if not sym_bars or len(sym_bars) < self.rsi_period + 5:
                continue

            highs = [b["high"] for b in sym_bars]
            lows = [b["low"] for b in sym_bars]
            closes = [b["close"] for b in sym_bars]
            close = closes[-1]

            # Indicators
            atr = compute_atr(highs, lows, closes, self.rsi_period)
            rsi = compute_rsi(closes, self.rsi_period)

            if atr <= 0:
                continue

            pos = pos_by_symbol.get(sym)

            if pos:
                # ============================================================
                #  MANAGE EXISTING POSITION
                # ============================================================
                ticket = pos["ticket"]
                tracked = self.tracked.get(sym, {})
                entry = tracked.get("entry", pos.get("price_open", close))
                direction = tracked.get("direction", pos["direction"])
                atr_entry = tracked.get("atr_at_entry", atr)
                current_sl = pos.get("sl", 0)

                log(f"  {sym}: RSI={rsi:.1f} ATR={atr:.6f} pos=#{ticket} "
                    f"{direction} entry={entry:.5f} sl={current_sl:.5f}")

                # --- Signal-based exit: RSI reversal ---
                exit_signal = False
                if direction == "LONG" and rsi > self.rsi_exit_long:
                    exit_signal = True
                    log(f"  {sym}: RSI={rsi:.1f} > {self.rsi_exit_long} "
                        f"-> signal exit LONG")
                elif direction == "SHORT" and rsi < self.rsi_exit_short:
                    exit_signal = True
                    log(f"  {sym}: RSI={rsi:.1f} < {self.rsi_exit_short} "
                        f"-> signal exit SHORT")

                if exit_signal:
                    actions.append({
                        "action": "EXIT",
                        "ticket": ticket,
                        "comment": f"rsi_reversal_{rsi:.0f}",
                    })
                    self._mark_exit(sym)
                    log(f"  -> EXIT #{ticket} {sym} (RSI reversal)")
                    open_count -= 1
                    continue

                # --- Trailing SL logic ---
                new_sl = self._compute_trailing_sl(
                    direction, close, entry, current_sl, atr_entry, sym)

                if new_sl and current_sl > 0:
                    sl_valid = (
                        (direction == "LONG" and new_sl < close) or
                        (direction == "SHORT" and new_sl > close)
                    )
                    sl_improves = (
                        (direction == "LONG" and new_sl > current_sl) or
                        (direction == "SHORT" and new_sl < current_sl)
                    )

                    if sl_valid and sl_improves:
                        actions.append({
                            "action": "MODIFY_SL",
                            "ticket": ticket,
                            "new_sl": round(new_sl, 6),
                            "comment": f"trail_{close:.5f}",
                        })
                        log(f"  -> MODIFY_SL #{ticket} {sym}: "
                            f"{current_sl:.5f} -> {new_sl:.5f}")

            else:
                # ============================================================
                #  CHECK FOR NEW ENTRY
                # ============================================================
                if open_count >= self.max_positions:
                    continue

                # Cooldown check
                last_exit = self.cooldowns.get(sym, 0)
                if self.tick_count - last_exit < self.cooldown_bars:
                    continue

                # --- Pure RSI mean-reversion (no EMA filter) ---
                direction = None
                reason = ""

                if rsi < self.rsi_oversold:
                    direction = "LONG"
                    reason = f"RSI={rsi:.1f}<{self.rsi_oversold}"
                elif rsi > self.rsi_overbought:
                    direction = "SHORT"
                    reason = f"RSI={rsi:.1f}>{self.rsi_overbought}"

                if not direction:
                    continue

                # Compute SL/TP
                if direction == "LONG":
                    sl_price = close - self.sl_atr_mult * atr
                    tp_price = close + self.tp_atr_mult * atr
                else:
                    sl_price = close + self.sl_atr_mult * atr
                    tp_price = close - self.tp_atr_mult * atr

                # SL sanity
                sl_ok = (
                    (direction == "LONG" and sl_price < close) or
                    (direction == "SHORT" and sl_price > close)
                )
                if not sl_ok:
                    log(f"  {sym}: SL sanity fail, skip")
                    continue

                sl_distance = abs(close - sl_price)
                if sl_distance < close * 0.00001:
                    log(f"  {sym}: SL too tight, skip")
                    continue

                log(f"  ==> ENTER {direction} {sym} @ ~{close:.5f} "
                    f"SL={sl_price:.5f} TP={tp_price:.5f} ({reason})")

                actions.append({
                    "action": "ENTER",
                    "symbol": sym,
                    "direction": direction,
                    "sl_price": round(sl_price, 6),
                    "comment": f"rsi_{direction.lower()}_{rsi:.0f}",
                    "signal_data": json.dumps({
                        "tick": self.tick_count,
                        "close": close,
                        "rsi": round(rsi, 2),
                        "atr": round(atr, 6),
                        "tp": round(tp_price, 6),
                    }),
                })

                self.tracked[sym] = {
                    "direction": direction,
                    "entry": close,
                    "atr_at_entry": atr,
                    "trail_active": False,
                }
                open_count += 1

        if actions:
            log(f"  Sending {len(actions)} action(s): "
                f"{[a['action'] + ' ' + a.get('symbol', str(a.get('ticket',''))) for a in actions]}")
        else:
            log(f"  No actions this tick")

        return actions

    # ------------------------------------------------------------------
    #  Trailing SL
    # ------------------------------------------------------------------

    def _compute_trailing_sl(self, direction, close, entry, current_sl,
                             atr, sym):
        """
        Trailing SL:
        1. Price moves +1 ATR from entry -> move SL to breakeven
        2. Price moves further -> trail SL at 1.5 ATR behind price
        Returns new SL or None.
        """
        tracked = self.tracked.get(sym, {})

        if direction == "LONG":
            profit_distance = close - entry
            if profit_distance >= self.trail_activate * atr:
                trail_sl = close - self.trail_distance * atr
                trail_sl = max(trail_sl, entry)  # at minimum breakeven
                if not tracked.get("trail_active"):
                    log(f"  {sym}: Trail ON (profit={profit_distance:.5f}, "
                        f"threshold={self.trail_activate * atr:.5f})")
                    tracked["trail_active"] = True
                    self.tracked[sym] = tracked
                return trail_sl
        else:
            profit_distance = entry - close
            if profit_distance >= self.trail_activate * atr:
                trail_sl = close + self.trail_distance * atr
                trail_sl = min(trail_sl, entry)  # at minimum breakeven
                if not tracked.get("trail_active"):
                    log(f"  {sym}: Trail ON (profit={profit_distance:.5f}, "
                        f"threshold={self.trail_activate * atr:.5f})")
                    tracked["trail_active"] = True
                    self.tracked[sym] = tracked
                return trail_sl

        return None

    # ------------------------------------------------------------------
    #  Housekeeping
    # ------------------------------------------------------------------

    def _mark_exit(self, sym):
        """Record exit for cooldown and clean up tracking."""
        self.cooldowns[sym] = self.tick_count
        if sym in self.tracked:
            del self.tracked[sym]

    def _sync_tracked(self, pos_by_symbol):
        """Remove tracked entries for positions that no longer exist
        (SL/TP hit or external close)."""
        stale = [sym for sym in self.tracked if sym not in pos_by_symbol]
        for sym in stale:
            log(f"  Tracked {sym} closed (SL/TP hit or external)")
            self._mark_exit(sym)

    def save_state(self) -> dict:
        return {
            "tick_count": self.tick_count,
            "tracked": self.tracked,
            "cooldowns": self.cooldowns,
        }

    def restore_state(self, state: dict):
        self.tick_count = state.get("tick_count", 0)
        self.tracked = state.get("tracked", {})
        self.cooldowns = state.get("cooldowns", {})
        log(f"State restored: tick={self.tick_count}, "
            f"tracked={list(self.tracked.keys())}, "
            f"cooldowns={self.cooldowns}")
