"""
Compression Breakout — Index Only
==================================
Entry TF:    M15
Filter:      H4 EMA(50) position (built from M15 bars)
Pattern:     20-bar compression (range < 1.2×ATR) → directional breakout
Day filter:  skip longs in top 20% of day range, shorts in bottom 20%
SL:          3.0 × ATR(100)
TP:          2.0R (managed by strategy via EXIT)
Timeout:     80 bars (managed by strategy via EXIT)
Protector:   move SL to BE when MFE ≥ 1.0R (via MODIFY_SL)

Research:    Step 9 OOS — PF 1.819, Calmar 14.50, 404 trades/3yr
FTMO @1%:    96.7% pass rate
"""

import json
import math
import logging

log = logging.getLogger("compression_breakout")


class Strategy:
    """Compression Breakout runner-compatible strategy."""

    # ───────────────────────────────────────────────────────────
    #  Init
    # ───────────────────────────────────────────────────────────

    def __init__(self, config: dict):
        p = config.get("params", config)   # support both flat and nested

        # Parse combos → symbols + per-symbol config
        combos = config.get("combos", [])
        self.symbols   = []
        self.combo_map = {}   # symbol → flat combo dict

        for combo in combos:
            sym = combo["sym"]
            if sym in self.combo_map:
                continue

            # New schema: directions → strat/daemon
            if "directions" in combo:
                dirs = combo["directions"]
                # For this strategy: take first direction (BOTH, or LONG/SHORT)
                dir_key = list(dirs.keys())[0]
                dir_val = dirs[dir_key]
                flat = {"sym": sym, "dir": dir_key}
                flat.update(dir_val.get("strat", {}))
                flat.update(dir_val.get("daemon", {}))
            else:
                # Legacy flat format
                flat = dict(combo)

            self.symbols.append(sym)
            self.combo_map[sym] = flat

        # If no combos, fall back to flat symbols list
        if not self.symbols:
            self.symbols = config.get("symbols", [])

        # Entry TF from first symbol's combo or default
        if self.symbols:
            first = self.combo_map[self.symbols[0]]
            self.entry_tf = first.get("tf", "M15")
        else:
            self.entry_tf = config.get("entry_tf", "M15")
        self.history_bars = config.get("history_bars", 1200)

        # Compression
        self.atr_period    = p.get("atr_period", 100)
        self.comp_bars     = p.get("comp_bars", 20)
        self.comp_thresh   = p.get("comp_thresh", 1.2)
        self.wait_bars     = p.get("wait_mult", 2) * self.comp_bars

        # Filter
        self.h4_ema_period = p.get("filter_ema_period", 50)
        self.day_pos_cut   = p.get("day_pos_cutoff", 0.20)

        # Trade management
        self.sl_atr_mult   = p.get("sl_atr_mult", 3.0)
        self.tp_r          = p.get("tp_r", 2.0)
        self.timeout_bars  = p.get("timeout_bars", 80)
        self.prot_trigger  = p.get("protector_trigger_r", 1.0)
        self.prot_level    = p.get("protector_level_r", 0.0)   # 0 = breakeven

        # Minimum M15 bars needed
        # H4 EMA(50) needs ~800 M15 bars, ATR(100), compression(20)
        self.min_bars = max(self.atr_period + self.comp_bars + 50, 900)

        # ── State ──
        self._last_bar_time  = {}   # symbol → last processed bar timestamp
        self._pending_setups = {}   # symbol → setup dict
        self._pending_entry  = {}   # symbol → entry meta (waiting for fill)
        self._trade_meta     = {}   # ticket (int) → trade tracking dict
        self._known_tickets  = set()

        log.info(f"CB init: {len(self.symbols)} symbols, "
                 f"SL={self.sl_atr_mult}×ATR, TP={self.tp_r}R, "
                 f"TO={self.timeout_bars}bars, prot@{self.prot_trigger}R")

    # ───────────────────────────────────────────────────────────
    #  Protocol interface
    # ───────────────────────────────────────────────────────────

    def get_requirements(self) -> dict:
        # Per-symbol TF from combos, or default entry_tf
        tfs = {}
        for sym in self.symbols:
            combo = self.combo_map.get(sym)
            tfs[sym] = combo["tf"] if combo else self.entry_tf
        return {
            "symbols":      self.symbols,
            "timeframes":   tfs,
            "history_bars": self.history_bars,
        }

    def on_bars(self, bars_data: dict, positions: list) -> list:
        """Called on every TICK. Returns list of action dicts."""
        actions = []

        # Build lookup: symbol → [positions]
        pos_map = {}
        for p in positions:
            pos_map.setdefault(p.get("symbol", ""), []).append(p)

        # Track which tickets are still alive (for cleanup)
        alive_tickets = {p["ticket"] for p in positions}

        for symbol in self.symbols:
            raw = bars_data.get(symbol)
            if not raw or len(raw) < self.min_bars:
                continue

            # ── Detect new bar ──
            last_t = raw[-1]["time"]
            if self._last_bar_time.get(symbol) == last_t:
                continue
            self._last_bar_time[symbol] = last_t

            # ── Parse OHLC ──
            n = len(raw)
            o = [b["open"]  for b in raw]
            h = [b["high"]  for b in raw]
            l = [b["low"]   for b in raw]
            c = [b["close"] for b in raw]
            t = [b["time"]  for b in raw]

            # ── Indicators ──
            atr_val = self._atr(h, l, c, n)
            h4_dir  = self._h4_filter(t, c)
            day_pos = self._day_position(t, h, l, n)

            # ── Match new positions to pending entries ──
            sym_pos = pos_map.get(symbol, [])
            actions += self._match_fills(symbol, sym_pos)

            # ── Manage open positions ──
            for p in sym_pos:
                actions += self._manage(p, c[n - 1])

            has_pos = len(sym_pos) > 0 or symbol in self._pending_entry

            # ── Check pending setup for breakout ──
            if not has_pos and symbol in self._pending_setups:
                setup = self._pending_setups[symbol]
                setup["wait_remaining"] -= 1

                act = self._check_breakout(symbol, setup, h[n - 1], l[n - 1],
                                           c[n - 1], atr_val)
                if act:
                    actions.append(act)
                    del self._pending_setups[symbol]
                elif setup["wait_remaining"] <= 0:
                    del self._pending_setups[symbol]

            # ── Detect new compression ──
            if not has_pos and symbol not in self._pending_setups:
                setup = self._detect_compression(
                    symbol, h, l, c, atr_val, h4_dir, day_pos, n)
                if setup:
                    self._pending_setups[symbol] = setup

        # ── Cleanup closed positions ──
        closed = [tk for tk in self._trade_meta if tk not in alive_tickets]
        for tk in closed:
            del self._trade_meta[tk]
            self._known_tickets.discard(tk)

        # Cleanup stale pending entries (no fill after 5 ticks)
        stale = []
        for sym, meta in self._pending_entry.items():
            meta["age"] += 1
            if meta["age"] > 5:
                stale.append(sym)
                log.warning(f"CB {sym}: pending entry expired (no fill after 5 ticks)")
        for sym in stale:
            del self._pending_entry[sym]

        return actions

    def save_state(self) -> dict:
        return {
            "last_bar_time":  self._last_bar_time,
            "pending_setups": self._pending_setups,
            "pending_entry":  self._pending_entry,
            "trade_meta":     {str(k): v for k, v in self._trade_meta.items()},
        }

    def restore_state(self, state: dict):
        if not state:
            return
        self._last_bar_time  = state.get("last_bar_time", {})
        self._pending_setups = state.get("pending_setups", {})
        self._pending_entry  = state.get("pending_entry", {})
        raw_meta = state.get("trade_meta", {})
        self._trade_meta = {int(k): v for k, v in raw_meta.items()}
        self._known_tickets = set(self._trade_meta.keys())
        log.info(f"CB state restored: {len(self._trade_meta)} active trades, "
                 f"{len(self._pending_setups)} pending setups")

    # ───────────────────────────────────────────────────────────
    #  Indicators
    # ───────────────────────────────────────────────────────────

    def _atr(self, h, l, c, n) -> float:
        """ATR(100) at the last bar, simple average of True Range."""
        p = self.atr_period
        if n < p + 1:
            return 0.0
        total = 0.0
        for i in range(n - p, n):
            tr1 = h[i] - l[i]
            tr2 = abs(h[i] - c[i - 1])
            tr3 = abs(l[i] - c[i - 1])
            total += max(tr1, tr2, tr3)
        return total / p

    def _h4_filter(self, times, closes) -> int:
        """
        Build H4 bars from M15, compute EMA(50), return direction.
        +1 = bullish (close > EMA), -1 = bearish, 0 = insufficient data.
        """
        H4_SEC = 4 * 3600
        groups = {}
        for i, t in enumerate(times):
            key = (t // H4_SEC) * H4_SEC
            groups[key] = closes[i]   # last close in the group

        keys = sorted(groups.keys())
        if len(keys) < self.h4_ema_period + 2:
            return 0

        # Use all completed H4 bars (exclude the last = forming)
        h4_closes = [groups[k] for k in keys[:-1]]

        # EMA
        ema = h4_closes[0]
        mult = 2.0 / (self.h4_ema_period + 1)
        for val in h4_closes[1:]:
            ema = val * mult + ema * (1 - mult)

        # Direction from last completed H4 bar vs EMA
        if h4_closes[-1] > ema:
            return 1
        elif h4_closes[-1] < ema:
            return -1
        return 0

    def _day_position(self, times, highs, lows, n) -> float:
        """
        Position of current price within today's range [0..1].
        Uses running high/low up to current bar (no lookahead).
        """
        DAY_SEC = 86400
        last_t = times[n - 1]
        today_start = (last_t // DAY_SEC) * DAY_SEC

        day_h = -1e18
        day_l =  1e18
        for i in range(n - 1, -1, -1):
            if times[i] < today_start:
                break
            if highs[i] > day_h:
                day_h = highs[i]
            if lows[i] < day_l:
                day_l = lows[i]

        day_range = day_h - day_l
        if day_range <= 0:
            return 0.5

        mid = (highs[n - 1] + lows[n - 1]) / 2.0
        return max(0.0, min(1.0, (mid - day_l) / day_range))

    # ───────────────────────────────────────────────────────────
    #  Compression detection
    # ───────────────────────────────────────────────────────────

    def _detect_compression(self, symbol, h, l, c, atr_val, h4_dir, day_pos, n) -> dict | None:
        """
        Check if the last comp_bars M15 bars form a compression zone.
        Returns setup dict or None.
        """
        if atr_val <= 0 or h4_dir == 0:
            return None

        cb = self.comp_bars
        if n < cb + 10:
            return None

        # Rolling high/low of last comp_bars bars
        comp_high = max(h[n - cb:n])
        comp_low  = min(l[n - cb:n])
        comp_range = comp_high - comp_low

        if comp_range > self.comp_thresh * atr_val:
            return None

        direction = h4_dir

        # Combo direction filter (BOTH/LONG/SHORT)
        combo = self.combo_map.get(symbol)
        if combo:
            allowed = combo.get("dir", "BOTH").upper()
            if allowed == "LONG" and direction != 1:
                return None
            if allowed == "SHORT" and direction != -1:
                return None

        # Day position filter
        if direction == 1 and day_pos > (1.0 - self.day_pos_cut):
            return None
        if direction == -1 and day_pos < self.day_pos_cut:
            return None

        return {
            "comp_high":      comp_high,
            "comp_low":       comp_low,
            "direction":      direction,
            "atr":            atr_val,
            "wait_remaining": self.wait_bars,
        }

    # ───────────────────────────────────────────────────────────
    #  Breakout detection → ENTER
    # ───────────────────────────────────────────────────────────

    def _check_breakout(self, symbol, setup, bar_h, bar_l,
                        bar_c, current_atr) -> dict | None:
        """
        Check if latest bar triggered a breakout of the pending setup.
        Returns ENTER action or None.
        """
        d = setup["direction"]
        triggered = False

        if d == 1 and bar_h > setup["comp_high"]:
            triggered = True
        elif d == -1 and bar_l < setup["comp_low"]:
            triggered = True

        if not triggered:
            return None

        # ── Calculate SL from current ATR ──
        atr = setup["atr"]
        if current_atr > 0:
            atr = current_atr     # prefer fresh ATR

        risk = self.sl_atr_mult * atr
        if risk <= 0:
            return None

        # Entry will fill at approximately current close / next open
        est_entry = bar_c
        sl_price  = est_entry - d * risk
        tp_price  = est_entry + d * self.tp_r * risk

        # Store metadata for when fill arrives
        self._pending_entry[symbol] = {
            "direction":     d,
            "risk":          risk,
            "tp_price":      tp_price,
            "bars_remaining": self.timeout_bars,
            "prot_active":   False,
            "age":           0,
            "comp_high":     setup["comp_high"],
            "comp_low":      setup["comp_low"],
        }

        # Size scaling from combo
        combo = self.combo_map.get(symbol, {})
        size_r = combo.get("size_r", 1.0)

        dir_str = "LONG" if d == 1 else "SHORT"
        log.info(f"CB {symbol}: ENTER {dir_str} SL={sl_price:.2f} "
                 f"risk={risk:.2f} TP={tp_price:.2f} size_r={size_r}")

        return {
            "action":    "ENTER",
            "symbol":    symbol,
            "direction": dir_str,
            "sl_price":  round(sl_price, 5),
            "comment":   f"CB {dir_str}",
            "signal_data": json.dumps({
                "risk":    round(risk, 5),
                "tp":      round(tp_price, 5),
                "comp_h":  round(setup["comp_high"], 5),
                "comp_l":  round(setup["comp_low"], 5),
                "atr":     round(atr, 5),
                "size_r":  size_r,
            }),
        }

    # ───────────────────────────────────────────────────────────
    #  Match fills
    # ───────────────────────────────────────────────────────────

    def _match_fills(self, symbol, sym_positions) -> list:
        """
        When a new position appears for a symbol with pending entry,
        initialize trade tracking metadata.
        """
        if symbol not in self._pending_entry:
            return []

        for p in sym_positions:
            ticket = p["ticket"]
            if ticket in self._known_tickets:
                continue

            # New position → match to pending entry
            meta = self._pending_entry.pop(symbol, None)
            if meta is None:
                break

            actual_entry = p["price_open"]
            d = meta["direction"]
            risk = meta["risk"]

            # Recalculate TP from actual fill price
            tp_price = actual_entry + d * self.tp_r * risk

            self._trade_meta[ticket] = {
                "symbol":        symbol,
                "direction":     d,
                "entry_price":   actual_entry,
                "risk":          risk,
                "tp_price":      tp_price,
                "be_price":      actual_entry + d * self.prot_level * risk,
                "bars_remaining": meta["bars_remaining"],
                "prot_active":   False,
            }
            self._known_tickets.add(ticket)

            log.info(f"CB {symbol}: FILLED #{ticket} @ {actual_entry:.2f} "
                     f"risk={risk:.2f} TP={tp_price:.2f}")
            break

        return []

    # ───────────────────────────────────────────────────────────
    #  Position management: protector, TP, timeout
    # ───────────────────────────────────────────────────────────

    def _manage(self, pos: dict, current_price: float) -> list:
        """Manage an open position: protector → TP → timeout."""
        actions = []
        ticket = pos["ticket"]

        if ticket not in self._trade_meta:
            # Position exists but we have no metadata (restart without state?)
            # Initialize from position data with conservative defaults
            self._init_meta_from_position(pos)
            if ticket not in self._trade_meta:
                return []

        meta = self._trade_meta[ticket]
        d = meta["direction"]
        entry = meta["entry_price"]
        risk = meta["risk"]

        if risk <= 0:
            return []

        # Current P/L in R-multiples
        pnl_r = (current_price - entry) / risk * d

        # ── Protector: move SL to BE when MFE ≥ trigger ──
        if not meta["prot_active"] and pnl_r >= self.prot_trigger:
            meta["prot_active"] = True
            new_sl = meta["be_price"]

            log.info(f"CB {meta['symbol']}: PROTECTOR #{ticket} "
                     f"MFE={pnl_r:.2f}R → SL→{new_sl:.2f}")

            actions.append({
                "action": "MODIFY_SL",
                "ticket": ticket,
                "new_sl": round(new_sl, 5),
                "comment": "CB protector BE",
            })

        # ── TP exit ──
        tp_hit = False
        if d == 1 and current_price >= meta["tp_price"]:
            tp_hit = True
        elif d == -1 and current_price <= meta["tp_price"]:
            tp_hit = True

        if tp_hit:
            log.info(f"CB {meta['symbol']}: TP #{ticket} "
                     f"@ {current_price:.2f} ({pnl_r:.2f}R)")
            actions.append({
                "action": "EXIT",
                "ticket": ticket,
                "comment": f"CB TP {pnl_r:.1f}R",
            })
            return actions     # exit sent, skip timeout

        # ── Timeout exit ──
        meta["bars_remaining"] -= 1
        if meta["bars_remaining"] <= 0:
            log.info(f"CB {meta['symbol']}: TIMEOUT #{ticket} "
                     f"@ {current_price:.2f} ({pnl_r:.2f}R)")
            actions.append({
                "action": "EXIT",
                "ticket": ticket,
                "comment": f"CB timeout {pnl_r:.1f}R",
            })

        return actions

    def _init_meta_from_position(self, pos: dict):
        """
        Reconstruct trade metadata from position data when state is lost.
        Uses SL distance as risk estimate.
        """
        ticket = pos["ticket"]
        entry = pos["price_open"]
        sl = pos["sl"]
        d = 1 if pos["direction"] == "LONG" else -1

        risk = abs(entry - sl) if sl > 0 else 0
        if risk <= 0:
            log.warning(f"CB: cannot reconstruct meta for #{ticket} (no SL)")
            return

        tp_price = entry + d * self.tp_r * risk

        self._trade_meta[ticket] = {
            "symbol":        pos["symbol"],
            "direction":     d,
            "entry_price":   entry,
            "risk":          risk,
            "tp_price":      tp_price,
            "be_price":      entry + d * self.prot_level * risk,
            "bars_remaining": self.timeout_bars // 2,   # conservative: half remaining
            "prot_active":   sl == entry or (d == 1 and sl > entry) or (d == -1 and sl < entry),
        }
        self._known_tickets.add(ticket)
        log.info(f"CB: reconstructed meta for #{ticket} "
                 f"risk={risk:.2f} TP={tp_price:.2f} "
                 f"prot={'ON' if self._trade_meta[ticket]['prot_active'] else 'OFF'}")
