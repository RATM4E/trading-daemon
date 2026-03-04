"""
Momentum Continuation V1 — Strategy for Trading Daemon
=======================================================
Multi-timeframe impulse → pullback entry.

Signal logic:
  HTF (H1 or M30): Detect impulse candles
    - body/range > 70% AND range > 1.5×ATR(14)
    - Impulse decays over HTF bars (freshness halves per HTF bar)
  LTF (M15 or M5): Pullback entry via RSI
    - Bullish impulse active + RSI < 50 → LONG
    - Bearish impulse active + RSI > 50 → SHORT

Risk:
  SL = entry ± sl_atr × ATR(14) on LTF
  TP = entry ± tp_r × SL_distance
  Protector: at trigger_R, lock SL at lock_R (negative = below entry)

HTF bars are aggregated from LTF bars internally.
Daemon subscribes to LTF only.

Interface: get_requirements / on_bars / save_state / restore_state
"""

import json
import math
import time
from datetime import datetime


def log(msg: str):
    ts = datetime.now().strftime("%H:%M:%S.%f")[:-3]
    print(f"[momentum_cont {ts}] {msg}", flush=True)


# ═══════════════════════════════════════════════════════════════
#  Indicators
# ═══════════════════════════════════════════════════════════════

def compute_atr(highs, lows, closes, period=14):
    """Wilder's ATR from lists."""
    n = len(closes)
    if n < 2:
        return [float('nan')] * n
    trs = [highs[0] - lows[0]]
    for i in range(1, n):
        trs.append(max(
            highs[i] - lows[i],
            abs(highs[i] - closes[i - 1]),
            abs(lows[i] - closes[i - 1])
        ))
    out = [float('nan')] * n
    if n < period:
        return out
    s = sum(trs[:period])
    out[period - 1] = s / period
    for i in range(period, n):
        out[i] = (out[i - 1] * (period - 1) + trs[i]) / period
    return out


def compute_rsi(closes, period=14):
    """Wilder's RSI from list."""
    n = len(closes)
    out = [float('nan')] * n
    if n < period + 1:
        return out
    gains = [0.0] * n
    losses = [0.0] * n
    for i in range(1, n):
        d = closes[i] - closes[i - 1]
        if d > 0:
            gains[i] = d
        else:
            losses[i] = -d
    ag = sum(gains[1:period + 1]) / period
    al = sum(losses[1:period + 1]) / period
    out[period] = 100.0 if al == 0 else 100.0 - 100.0 / (1 + ag / al)
    for i in range(period + 1, n):
        ag = (ag * (period - 1) + gains[i]) / period
        al = (al * (period - 1) + losses[i]) / period
        out[i] = 100.0 if al == 0 else 100.0 - 100.0 / (1 + ag / al)
    return out


# ═══════════════════════════════════════════════════════════════
#  HTF Bar Aggregation
# ═══════════════════════════════════════════════════════════════

AGG_RATIO = {
    ("M5", "M30"): 6,
    ("M15", "H1"): 4,
}

HTF_STARTS_MIN = {
    "M30": {0, 30},
    "H1": {0},
}


def aggregate_to_htf(ltf_bars, ltf_tf, htf_tf):
    """
    Aggregate LTF OHLC bars into HTF bars.
    Returns list of completed HTF bar dicts only.
    """
    ratio = AGG_RATIO.get((ltf_tf, htf_tf))
    if ratio is None:
        log(f"WARN: unsupported aggregation {ltf_tf}→{htf_tf}")
        return []

    valid_starts = HTF_STARTS_MIN.get(htf_tf, set())
    htf_bars = []
    bucket = []

    for bar in ltf_bars:
        t = bar["time"]
        dt = datetime.utcfromtimestamp(t)
        minute = dt.minute

        if minute in valid_starts and len(bucket) > 0:
            htf_bars.append(_close_bucket(bucket))
            bucket = []

        bucket.append(bar)

    # Only close last bucket if it has exactly 'ratio' bars (complete)
    if len(bucket) == ratio:
        htf_bars.append(_close_bucket(bucket))

    return htf_bars


def _close_bucket(bars):
    return {
        "time": bars[0]["time"],
        "open": bars[0]["open"],
        "high": max(b["high"] for b in bars),
        "low": min(b["low"] for b in bars),
        "close": bars[-1]["close"],
        "volume": sum(b.get("volume", 0) for b in bars),
    }


# ═══════════════════════════════════════════════════════════════
#  Impulse detection — matches backtest nb_detect_impulses
# ═══════════════════════════════════════════════════════════════

def detect_impulses_full(htf_o, htf_h, htf_l, htf_c, htf_atr,
                         body_ratio, range_atr_mult, decay_bars,
                         freshness_floor=0.05):
    """
    Full impulse detection with forward-propagation decay.
    Exact match of backtest nb_detect_impulses logic.
    Returns (imp_dir[], imp_fresh[]) aligned to HTF bars.
    """
    n = len(htf_c)
    imp_dir = [0.0] * n
    imp_fresh = [0.0] * n

    # Pass 1: detect raw impulses
    for i in range(1, n):
        br = htf_h[i] - htf_l[i]
        av = htf_atr[i]
        if br < 1e-10 or math.isnan(av) or av < 1e-10:
            continue
        body = htf_c[i] - htf_o[i]
        if abs(body) > body_ratio * br and br > range_atr_mult * av:
            imp_dir[i] = 1.0 if body > 0 else -1.0
            imp_fresh[i] = 1.0

    # Pass 2: forward propagation decay (matches backtest exactly)
    for _ in range(decay_bars):
        for i in range(1, n):
            if (imp_dir[i] == 0.0 and imp_dir[i - 1] != 0.0
                    and imp_fresh[i - 1] > freshness_floor):
                imp_dir[i] = imp_dir[i - 1]
                imp_fresh[i] = imp_fresh[i - 1] * 0.5

    return imp_dir, imp_fresh


# ═══════════════════════════════════════════════════════════════
#  Position Tracker (protector management)
# ═══════════════════════════════════════════════════════════════

class PosTrack:
    __slots__ = ("ticket", "sym", "direction", "entry", "sl_dist",
                 "trigger_r", "lock_r", "protector_fired")

    def __init__(self, ticket, sym, direction, entry, sl_dist,
                 trigger_r, lock_r):
        self.ticket = ticket
        self.sym = sym
        self.direction = direction
        self.entry = entry
        self.sl_dist = sl_dist
        self.trigger_r = trigger_r
        self.lock_r = lock_r
        self.protector_fired = False

    def to_dict(self):
        return {
            "ticket": self.ticket, "sym": self.sym,
            "direction": self.direction, "entry": self.entry,
            "sl_dist": self.sl_dist, "trigger_r": self.trigger_r,
            "lock_r": self.lock_r, "protector_fired": self.protector_fired,
        }

    @staticmethod
    def from_dict(d):
        pt = PosTrack(
            d["ticket"], d["sym"], d["direction"], d["entry"],
            d["sl_dist"], d.get("trigger_r"), d.get("lock_r"),
        )
        pt.protector_fired = d.get("protector_fired", False)
        return pt


# ═══════════════════════════════════════════════════════════════
#  Strategy
# ═══════════════════════════════════════════════════════════════

class Strategy:
    """Momentum Continuation — MTF impulse + RSI pullback."""

    def __init__(self, config: dict):
        self.config = config
        self.tick_count = 0

        # Global params
        p = config.get("params", {})
        self.atr_period = p.get("atr_period", 14)
        self.impulse_body_ratio = p.get("impulse_body_ratio", 0.70)
        self.impulse_range_atr = p.get("impulse_range_atr", 1.5)
        self.impulse_decay_bars = p.get("impulse_decay_bars", 2)
        self.rsi_period = p.get("rsi_period", 14)
        self.rsi_thresh = p.get("rsi_pullback_thresh", 50)
        self.min_stop_pips = p.get("min_stop_pips", 20)

        # Parse combos → per-symbol config
        self.sym_cfg = {}
        combos = config.get("combos", [])
        for c in combos:
            sym = c["sym"]
            dirs = c.get("directions", {})
            for dk in ("BOTH", "LONG", "SHORT"):
                if dk in dirs:
                    strat = dirs[dk].get("strat", {})
                    self.sym_cfg[sym] = {
                        "htf": strat.get("htf", "H1"),
                        "ltf": strat.get("ltf", "M15"),
                        "sl_atr": strat.get("sl_atr", 2.0),
                        "tp_r": strat.get("tp_r", 1.0),
                        "trigger_r": strat.get("protector_trigger_r"),
                        "lock_r": strat.get("protector_lock_r"),
                        "tier": c.get("tier", "T2"),
                    }
                    break

        self.symbols = list(self.sym_cfg.keys())

        # Position tracking: ticket → PosTrack
        self.pos_tracks = {}

        # Dedup: (sym, direction) → last entry bar timestamp
        self.last_entry_ts = {}

        # First tick flag for warmup gate
        self.first_tick = True

        log(f"Init: {len(self.symbols)} symbols")
        for sym, cfg in self.sym_cfg.items():
            prot = f"pt={cfg['trigger_r']}/pl={cfg['lock_r']}" if cfg['trigger_r'] else "none"
            log(f"  {sym}: {cfg['htf']}→{cfg['ltf']} sl={cfg['sl_atr']}ATR "
                f"tp={cfg['tp_r']}R prot={prot} [{cfg['tier']}]")

    # ─────────────────────────────────────────────────────────
    #  Protocol interface
    # ─────────────────────────────────────────────────────────

    def get_requirements(self) -> dict:
        timeframes = {}
        for sym, cfg in self.sym_cfg.items():
            timeframes[sym] = cfg["ltf"]
        reqs = {
            "symbols": self.symbols,
            "timeframes": timeframes,
            "history_bars": 1500,
        }
        # R-cap from strategy config (daemon reads it from HELLO)
        r_cap = self.config.get("params", {}).get("r_cap")
        if r_cap is not None:
            reqs["r_cap"] = r_cap
        return reqs

    def on_bars(self, bars: dict, positions: list) -> list:
        self.tick_count += 1
        actions = []

        if self.first_tick:
            self.first_tick = False
            log(f"Warmup: skipping first tick")
            return []

        # Sync tracked positions (protector management)
        self._sync_tracked_positions(positions)

        # Build position map
        pos_by_sym = {}
        for p in positions:
            s = p.get("symbol", "")
            pos_by_sym.setdefault(s, []).append(p)

        for sym in self.symbols:
            sym_bars = bars.get(sym)
            if not sym_bars or len(sym_bars) < 50:
                continue
            cfg = self.sym_cfg[sym]
            sym_actions = self._process_symbol(
                sym, sym_bars, cfg, pos_by_sym.get(sym, []))
            actions.extend(sym_actions)

        if actions:
            log(f"Tick #{self.tick_count}: {len(actions)} action(s)")

        return actions

    def save_state(self) -> dict:
        return {
            "tick_count": self.tick_count,
            "pos_tracks": {str(k): v.to_dict()
                           for k, v in self.pos_tracks.items()},
            "last_entry_ts": {f"{k[0]}|{k[1]}": v
                              for k, v in self.last_entry_ts.items()},
        }

    def restore_state(self, state: dict):
        self.tick_count = state.get("tick_count", 0)
        tracks = state.get("pos_tracks", {})
        self.pos_tracks = {
            int(k): PosTrack.from_dict(v) for k, v in tracks.items()}
        lts = state.get("last_entry_ts", {})
        self.last_entry_ts = {
            (k.split("|")[0], k.split("|")[1]): v for k, v in lts.items()}
        self.first_tick = False
        log(f"State restored: tick={self.tick_count}, "
            f"tracked={len(self.pos_tracks)}")

    # ─────────────────────────────────────────────────────────
    #  Per-symbol processing
    # ─────────────────────────────────────────────────────────

    def _process_symbol(self, sym, ltf_bars, cfg, positions):
        """Full impulse detection on HTF, level-based entry on LTF."""
        actions = []
        ltf_tf = cfg["ltf"]
        htf_tf = cfg["htf"]

        # Parse LTF bars
        ltf_list = self._parse_bars(ltf_bars)
        if len(ltf_list["close"]) < 50:
            return []

        # Build LTF bar dicts for aggregation
        bar_dicts = []
        for i in range(len(ltf_list["close"])):
            bar_dicts.append({
                "time": ltf_list["time"][i],
                "open": ltf_list["open"][i],
                "high": ltf_list["high"][i],
                "low": ltf_list["low"][i],
                "close": ltf_list["close"][i],
                "volume": ltf_list.get("volume",
                                       [0] * len(ltf_list["close"]))[i],
            })

        # Aggregate to HTF
        htf_bars_agg = aggregate_to_htf(bar_dicts, ltf_tf, htf_tf)
        if len(htf_bars_agg) < self.atr_period + 5:
            return []

        # ─── HTF: Full impulse detection (matches backtest) ───
        htf_h = [b["high"] for b in htf_bars_agg]
        htf_l = [b["low"] for b in htf_bars_agg]
        htf_c = [b["close"] for b in htf_bars_agg]
        htf_o = [b["open"] for b in htf_bars_agg]
        htf_atr = compute_atr(htf_h, htf_l, htf_c, self.atr_period)

        imp_dir, imp_fresh = detect_impulses_full(
            htf_o, htf_h, htf_l, htf_c, htf_atr,
            self.impulse_body_ratio, self.impulse_range_atr,
            self.impulse_decay_bars)

        # Current impulse = latest completed HTF bar
        cur_imp_dir = imp_dir[-1]
        cur_imp_fresh = imp_fresh[-1]

        # ─── LTF indicators ───
        ltf_c = ltf_list["close"]
        ltf_h = ltf_list["high"]
        ltf_l = ltf_list["low"]
        ltf_rsi = compute_rsi(ltf_c, self.rsi_period)
        ltf_atr = compute_atr(ltf_h, ltf_l, ltf_c, self.atr_period)

        cur_rsi = ltf_rsi[-1] if not math.isnan(ltf_rsi[-1]) else float('nan')
        cur_atr = ltf_atr[-1] if not math.isnan(ltf_atr[-1]) else 0.0
        cur_close = ltf_c[-1]
        cur_time = ltf_list["time"][-1]

        # ─── Manage existing positions (protector) ───
        for pos in positions:
            ticket = pos.get("ticket", 0)
            if ticket not in self.pos_tracks:
                continue
            pt = self.pos_tracks[ticket]
            act = self._manage_protector(pt, pos, cur_close)
            if act:
                actions.append(act)

        # ─── Entry logic: LEVEL check (matches backtest) ───
        # Backtest: imp_d > 0 and rsi < thresh → LONG
        #           imp_d < 0 and rsi > (100 - thresh) → SHORT
        # Dedup via has_long/has_short + last_entry_ts
        has_long = any(p["direction"] == "LONG" for p in positions)
        has_short = any(p["direction"] == "SHORT" for p in positions)

        if cur_imp_dir != 0 and cur_imp_fresh > 0.1 and cur_atr > 0:
            if not math.isnan(cur_rsi):
                # LONG: bullish impulse + RSI below threshold (pullback)
                if (cur_imp_dir > 0 and not has_long
                        and cur_rsi < self.rsi_thresh):
                    entry_action = self._build_entry(
                        sym, "LONG", cur_close, cur_atr, cfg, cur_time,
                        cur_imp_dir, cur_imp_fresh)
                    if entry_action:
                        actions.append(entry_action)

                # SHORT: bearish impulse + RSI above (100 - threshold)
                if (cur_imp_dir < 0 and not has_short
                        and cur_rsi > (100.0 - self.rsi_thresh)):
                    entry_action = self._build_entry(
                        sym, "SHORT", cur_close, cur_atr, cfg, cur_time,
                        cur_imp_dir, cur_imp_fresh)
                    if entry_action:
                        actions.append(entry_action)

        return actions

    # ─────────────────────────────────────────────────────────
    #  Entry builder
    # ─────────────────────────────────────────────────────────

    def _build_entry(self, sym, direction, close, atr, cfg, bar_time,
                     imp_dir, imp_fresh):
        """Build an ENTER action if SL passes minimum distance check."""
        sl_dist = cfg["sl_atr"] * atr

        # Min SL floor
        min_sl = self._min_sl_price(sym)
        if sl_dist < min_sl:
            sl_dist = min_sl

        # Dedup: one entry per symbol/direction per LTF bar
        key = (sym, direction)
        if self.last_entry_ts.get(key) == bar_time:
            return None
        self.last_entry_ts[key] = bar_time

        if direction == "LONG":
            sl_price = close - sl_dist
            tp_price = close + cfg["tp_r"] * sl_dist
        else:
            sl_price = close + sl_dist
            tp_price = close - cfg["tp_r"] * sl_dist

        comment = (f"mc_{sym}_{direction[0]}_"
                   f"{cfg['htf']}{cfg['ltf']}_"
                   f"s{cfg['sl_atr']}_t{cfg['tp_r']}")

        signal_data = json.dumps({
            "htf": cfg["htf"], "ltf": cfg["ltf"],
            "sl_atr": cfg["sl_atr"], "tp_r": cfg["tp_r"],
            "protector_lock_r": cfg.get("lock_r"),
            "atr": round(atr, 6), "sl_dist": round(sl_dist, 6),
            "tier": cfg["tier"],
            "impulse_dir": imp_dir,
            "impulse_fresh": round(imp_fresh, 3),
            "close": close,
        })

        log(f"ENTRY {direction} {sym}: close={close:.5f} "
            f"SL={sl_price:.5f} TP={tp_price:.5f} "
            f"SLdist={sl_dist:.5f} ATR={atr:.5f} "
            f"imp={imp_dir}/f={imp_fresh:.2f}")

        return {
            "action": "ENTER",
            "symbol": sym,
            "direction": direction,
            "sl_price": round(sl_price, 6),
            "tp_price": round(tp_price, 6),
            "comment": comment,
            "signal_data": signal_data,
        }

    # ─────────────────────────────────────────────────────────
    #  Protector management
    # ─────────────────────────────────────────────────────────

    def _manage_protector(self, pt, pos, cur_close):
        if pt.protector_fired:
            return None
        if pt.trigger_r is None or pt.lock_r is None:
            return None

        if pt.direction == "LONG":
            profit_r = ((cur_close - pt.entry) / pt.sl_dist
                        if pt.sl_dist > 0 else 0)
        else:
            profit_r = ((pt.entry - cur_close) / pt.sl_dist
                        if pt.sl_dist > 0 else 0)

        if profit_r >= pt.trigger_r:
            if pt.direction == "LONG":
                new_sl = pt.entry + pt.lock_r * pt.sl_dist
            else:
                new_sl = pt.entry - pt.lock_r * pt.sl_dist

            current_sl = pos.get("sl", 0)
            if pt.direction == "LONG" and new_sl <= current_sl:
                return None
            if pt.direction == "SHORT" and new_sl >= current_sl:
                return None

            pt.protector_fired = True
            log(f"PROTECTOR {pt.sym} {pt.direction}: profit={profit_r:.2f}R "
                f"→ SL {current_sl:.5f} → {new_sl:.5f} "
                f"(lock={pt.lock_r}R)")

            return {
                "action": "MODIFY_SL",
                "ticket": pt.ticket,
                "new_sl": round(new_sl, 6),
            }
        return None

    # ─────────────────────────────────────────────────────────
    #  Position tracking
    # ─────────────────────────────────────────────────────────

    def _sync_tracked_positions(self, positions):
        live_tickets = set()
        for pos in positions:
            ticket = pos.get("ticket", 0)
            live_tickets.add(ticket)

            if ticket not in self.pos_tracks:
                comment = pos.get("comment", "")
                if not comment.startswith("mc_"):
                    continue

                sym = pos.get("symbol", "")
                direction = pos.get("direction", "")
                entry = pos.get("price_open", 0)
                sl = pos.get("sl", 0)

                if sym not in self.sym_cfg or entry == 0:
                    continue

                cfg = self.sym_cfg[sym]
                sl_dist = abs(entry - sl) if sl != 0 else 0

                pt = PosTrack(
                    ticket, sym, direction, entry, sl_dist,
                    cfg.get("trigger_r"), cfg.get("lock_r"),
                )
                self.pos_tracks[ticket] = pt
                log(f"Tracking: {sym} {direction} #{ticket} "
                    f"entry={entry:.5f} sl_dist={sl_dist:.5f}")

        closed = [t for t in self.pos_tracks if t not in live_tickets]
        for t in closed:
            pt = self.pos_tracks.pop(t)
            log(f"Closed: {pt.sym} {pt.direction} #{t}")

    # ─────────────────────────────────────────────────────────
    #  Helpers
    # ─────────────────────────────────────────────────────────

    def _parse_bars(self, bars_data):
        times, opens, highs, lows, closes, volumes = (
            [], [], [], [], [], [])
        for b in bars_data:
            times.append(b.get("time", 0))
            opens.append(b.get("open", 0))
            highs.append(b.get("high", 0))
            lows.append(b.get("low", 0))
            closes.append(b.get("close", 0))
            volumes.append(b.get("volume", 0))
        return {
            "time": times, "open": opens, "high": highs,
            "low": lows, "close": closes, "volume": volumes,
        }

    def _min_sl_price(self, sym):
        pip = self.min_stop_pips
        if "JPY" in sym:
            return pip * 0.01
        if sym in ("XAUUSD",):
            return pip * 1.0
        if sym in ("XAGUSD",):
            return pip * 0.01
        if sym in ("NAS100", "SP500", "US30", "DAX40", "UK100", "JPN225"):
            return pip * 1.0
        if sym in ("BTCUSD",):
            return pip * 10.0
        if sym in ("ETHUSD",):
            return pip * 1.0
        if sym in ("XTIUSD", "XBRUSD"):
            return pip * 0.01
        return pip * 0.0001
