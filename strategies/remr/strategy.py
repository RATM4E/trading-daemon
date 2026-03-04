"""
REMR v2.0 - Volatility Expansion Path Routing
===============================================
H12 PM-only, FADE + CONTINUATION branches.

Logic:
  B0 (PM bar): Range(B0) >= k * ATR(14) -> expansion detected
  B1 (AM bar): classify Cooling (FADE) or Continuation (CONT)
  Entry: at market when B1 completes (daemon fills at ~ Open B2)

Exits: trailing stop (0.15R trigger, 0.20R offset) + timeout (10 H12 bars).
No fixed TP.

Data: receives tick-TF bars (M5/M30) from daemon, resamples to H12 internally.
Actions: ENTER, EXIT, MODIFY_SL per REMR_STRATEGY_CONTRACT.md.

KNOWN ISSUES (daemon-side):
  - Scheduler.cs does NOT populate OpenTime in PositionData (line ~205).
    ProtocolSerializer.ToPositionData() includes it, but Scheduler builds
    PositionData inline without OpenTime. Result: crash-recovered positions
    get entry_bar_ts=0, timeout never fires for them. Fix: add
    OpenTime = pos.Time to Scheduler.cs PositionData construction.

TODO:
  - PENDING_ENTRY state (Entry Guard spec) for spread/gap/TTL validation.
    Currently entry fires immediately on B1 close.
"""

import json
from dataclasses import dataclass

TF_SEC = 43200
DAY_SEC = 86400
PM_OFFSET = 43200

_JPY = frozenset(["USDJPY","EURJPY","GBPJPY","AUDJPY","NZDJPY","CADJPY","CHFJPY"])

# Spreads from cost_model_v2.json (market consensus x1.32)
# Forex = pips, index/metal/energy/crypto = price units
_SPREAD = {
    "EURUSD":1.1,"GBPUSD":1.3,"AUDUSD":1.2,"NZDUSD":1.7,
    "USDCAD":1.5,"USDCHF":1.5,"USDJPY":1.2,"EURGBP":1.6,
    "EURJPY":1.8,"AUDJPY":2.0,"EURCHF":2.0,"CADJPY":2.0,
    "NZDJPY":2.1,"CHFJPY":2.4,"EURAUD":2.4,"EURCAD":2.4,
    "AUDCAD":2.2,"AUDNZD":2.4,"AUDCHF":2.0,"CADCHF":2.1,
    "NZDCAD":2.6,"NZDCHF":2.4,"GBPCHF":2.6,"GBPJPY":2.6,
    "GBPAUD":2.9,"GBPCAD":2.9,"GBPNZD":4.0,"EURNZD":3.2,
    "SP500":0.7,"NAS100":2.2,"US30":2.4,"DE40":1.7,
    "UK100":2.6,"JP225":11.0,
    "XAUUSD":0.33,"XAGUSD":0.03,
    "XTIUSD":0.07,"XBRUSD":0.07,
    "BTCUSD":13.2,"ETHUSD":4.0,
}

_AC = {
    "EURUSD":"forex","GBPUSD":"forex","AUDUSD":"forex","NZDUSD":"forex",
    "USDCAD":"forex","USDCHF":"forex","USDJPY":"forex","EURGBP":"forex",
    "EURJPY":"forex","AUDJPY":"forex","EURCHF":"forex","CADJPY":"forex",
    "NZDJPY":"forex","CHFJPY":"forex","EURAUD":"forex","EURCAD":"forex",
    "AUDCAD":"forex","AUDNZD":"forex","AUDCHF":"forex","CADCHF":"forex",
    "NZDCAD":"forex","NZDCHF":"forex","GBPCHF":"forex","GBPJPY":"forex",
    "GBPAUD":"forex","GBPCAD":"forex","GBPNZD":"forex","EURNZD":"forex",
    "SP500":"index","NAS100":"index","US30":"index","DE40":"index",
    "UK100":"index","JP225":"index",
    "XAUUSD":"metal","XAGUSD":"metal",
    "XTIUSD":"energy","XBRUSD":"energy",
    "BTCUSD":"crypto","ETHUSD":"crypto",
}


def _pip(sym):
    if _AC.get(sym) != "forex":
        return 1.0
    return 0.01 if sym in _JPY else 0.0001


def _spread_px(sym):
    return _SPREAD.get(sym, 2.0) * _pip(sym)


@dataclass
class SymState:
    phase: str = "IDLE"         # IDLE -> WAIT_B1 -> (ENTER or IDLE)
    b0_ts: int = 0
    b0_high: float = 0.0
    b0_low: float = 0.0
    b0_range: float = 0.0
    b0_atr: float = 0.0
    b0_dir: str = ""            # "UP" or "DOWN"


@dataclass
class PosTrack:
    ticket: int = 0
    symbol: str = ""
    direction: str = ""         # "LONG" / "SHORT"
    entry_price: float = 0.0
    sl_dist: float = 0.0
    entry_bar_ts: int = 0       # H12 bar ts when entered (0 = recovered, no timeout)
    timeout_bars: int = 10
    trail_trig: float = 0.15
    trail_off: float = 0.20
    branch: str = ""            # "FADE" / "CONT" / "RECOVERED"
    best_price: float = 0.0
    trail_active: bool = False
    last_sl: float = 0.0


class Strategy:

    def __init__(self, config):
        p = config.get("params", {})
        self.tf     = p.get("tf_sec", TF_SEC)
        self.atr_n  = p.get("atr_period", 14)
        self.k      = p.get("k", 1.2)
        self.depth  = p.get("confirm_depth", 0.25)
        self.cpct   = p.get("cont_close_pct", 0.40)
        self.t_trig = p.get("trail_trigger_r", 0.15)
        self.t_off  = p.get("trail_offset_r", 0.20)
        self.tmo    = p.get("timeout_bars", 10)
        self.bufm   = p.get("sl_buffer_spread_mult", 0.5)
        self.smax   = p.get("sl_max_atr", 3.0)
        self.hbars  = p.get("history_bars", 1200)
        self.rcap   = p.get("r_cap", None)

        self.syms = [c["sym"] for c in config.get("combos", []) if "sym" in c]

        # Tick TF from combo config (daemon feeds this TF)
        self.tick_tf = "M30"
        combos = config.get("combos", [])
        if combos:
            dirs = combos[0].get("directions", {})
            for d in dirs.values():
                strat = d.get("strat", {})
                if "tf" in strat:
                    self.tick_tf = strat["tf"]
                    break
        self._tick_sec = {"M1":60,"M5":300,"M15":900,"M30":1800,
                          "H1":3600,"H4":14400}.get(self.tick_tf, 1800)
        # Trail scans last N tick-bars covering ~90 min (same window as 3×M30)
        self._trail_scan = max(3, 5400 // self._tick_sec)

        self._st      = {s: SymState() for s in self.syms}
        self._atr     = {s: 0.0 for s in self.syms}
        self._last    = {s: 0 for s in self.syms}   # last CLOSED H12 bar ts
        self._last_m5 = {s: 0 for s in self.syms}   # last processed tick-TF bar ts
        self._forming = {s: None for s in self.syms} # current forming H12 bucket
        self._bars    = {s: [] for s in self.syms}
        self._pos     = {}

    # ── interface ──────────────────────────────────────────────────

    def get_requirements(self):
        return {
            "symbols": list(self.syms),
            "timeframes": {s: self.tick_tf for s in self.syms},
            "history_bars": self.hbars,
            "r_cap": self.rcap,
        }

    def on_bars(self, bars_data, positions):
        actions = []
        self._sync(positions)
        for sym in self.syms:
            raw = bars_data.get(sym)
            if not raw:
                continue
            if self._resample(sym, raw):
                self._signal(sym, actions)
            self._trail(sym, raw, positions, actions)
        self._timeouts(actions)
        return actions

    def save_state(self):
        ss = {s: st.__dict__.copy() for s, st in self._st.items()}
        pp = {str(t): pt.__dict__.copy() for t, pt in self._pos.items()}
        return {
            "ss": ss, "pp": pp,
            "last": dict(self._last), "atr": dict(self._atr),
            "last_m5": dict(self._last_m5),
            "forming": {s: v for s, v in self._forming.items()},
        }

    def restore_state(self, state):
        if not state:
            return
        for s, d in state.get("ss", {}).items():
            if s in self._st:
                for k, v in d.items():
                    setattr(self._st[s], k, v)
        for key, d in state.get("pp", {}).items():
            self._pos[int(d.get("ticket", key))] = PosTrack(**d)
        for s, v in state.get("last", {}).items():
            if s in self._last: self._last[s] = v
        for s, v in state.get("atr", {}).items():
            if s in self._atr: self._atr[s] = v
        for s, v in state.get("last_m5", {}).items():
            if s in self._last_m5: self._last_m5[s] = v
        for s, v in state.get("forming", {}).items():
            if s in self._forming: self._forming[s] = v

    # ── tick-TF -> H12 resampling ───────────────────────────────────

    def _resample(self, sym, raw):
        """Incrementally aggregate tick-TF bars into H12 buckets.

        On each tick only processes bars newer than _last_m5[sym] cursor —
        typically 1-2 bars after delta protocol. O(new_bars) instead of O(all_bars).

        Bootstraps from full buffer on first call (or after state restore with
        empty history) so warmup works correctly.

        Returns True if at least one new H12 bar closed this tick.
        """
        if not raw:
            return False

        last_m5 = self._last_m5[sym]

        # ── Bootstrap: first call has full buffer, no cursor yet ──────────
        # Process ALL bars to build initial H12 history and set forming bar.
        if last_m5 == 0:
            bk = {}
            for b in raw:
                t, o, h, lo, c = b["time"], b["open"], b["high"], b["low"], b["close"]
                k = (t // self.tf) * self.tf
                if k not in bk:
                    bk[k] = [o, h, lo, c, k]
                else:
                    r = bk[k]
                    if h > r[1]: r[1] = h
                    if lo < r[2]: r[2] = lo
                    r[3] = c

            if not bk:
                return False

            forming_k = (raw[-1]["time"] // self.tf) * self.tf
            done = sorted(
                [v for k2, v in bk.items() if k2 != forming_k],
                key=lambda x: x[4]
            )

            mx = self.atr_n + 50
            self._bars[sym] = done[-mx:] if len(done) > mx else done
            self._forming[sym] = bk.get(forming_k)
            self._last_m5[sym] = raw[-1]["time"]

            # ATR + new-bar check
            if self._bars[sym]:
                lt = self._bars[sym][-1][4]
                if lt > self._last[sym]:
                    self._last[sym] = lt
                    self._calc_atr(sym)
                    return True
            return False

        # ── Incremental: only new bars since last processed ───────────────
        new_bars = [b for b in raw if b["time"] > last_m5]
        if not new_bars:
            return False

        self._last_m5[sym] = new_bars[-1]["time"]
        forming = self._forming[sym]
        new_closed = False

        for b in new_bars:
            t, o, h, lo, c = b["time"], b["open"], b["high"], b["low"], b["close"]
            k = (t // self.tf) * self.tf

            if forming is None:
                # No forming bar yet — start one
                forming = [o, h, lo, c, k]
            elif k == forming[4]:
                # Same H12 bucket — extend
                if h > forming[1]: forming[1] = h
                if lo < forming[2]: forming[2] = lo
                forming[3] = c
            else:
                # New bucket started → previous forming bar is now closed
                self._bars[sym].append(forming)
                mx = self.atr_n + 50
                if len(self._bars[sym]) > mx:
                    del self._bars[sym][0]
                forming = [o, h, lo, c, k]
                new_closed = True

        self._forming[sym] = forming

        if new_closed and self._bars[sym]:
            lt = self._bars[sym][-1][4]
            if lt > self._last[sym]:
                self._last[sym] = lt
                self._calc_atr(sym)
                return True
        return False

    def _calc_atr(self, sym):
        """Wilder EMA ATR on completed H12 bars."""
        bars = self._bars[sym]
        n = self.atr_n
        if len(bars) < n + 1:
            self._atr[sym] = 0.0
            return
        trs = []
        for i in range(1, len(bars)):
            pc = bars[i-1][3]     # prev close
            h, lo = bars[i][1], bars[i][2]
            trs.append(max(h - lo, abs(h - pc), abs(lo - pc)))
        if len(trs) < n:
            self._atr[sym] = 0.0
            return
        atr = sum(trs[:n]) / n
        for tr in trs[n:]:
            atr += (tr - atr) / n
        self._atr[sym] = atr

    # ── signal state machine ─────────────────────────────────────

    def _signal(self, sym, actions):
        bars = self._bars[sym]
        if len(bars) < 2:
            return
        cur = bars[-1]
        st = self._st[sym]
        atr = self._atr[sym]
        if atr <= 0:
            return

        is_pm = (cur[4] % DAY_SEC) == PM_OFFSET
        is_am = (cur[4] % DAY_SEC) == 0

        # Skip if already have a position on this symbol
        if self._has(sym):
            if st.phase != "IDLE":
                st.phase = "IDLE"
            return

        # IDLE: detect expansion on PM bar
        if st.phase == "IDLE" and is_pm:
            rng = cur[1] - cur[2]
            if rng >= self.k * atr and cur[3] != cur[0]:
                st.phase = "WAIT_B1"
                st.b0_ts = cur[4]
                st.b0_high = cur[1]
                st.b0_low = cur[2]
                st.b0_range = rng
                st.b0_atr = atr
                st.b0_dir = "UP" if cur[3] > cur[0] else "DOWN"

        # WAIT_B1: classify AM bar, generate entry
        elif st.phase == "WAIT_B1" and is_am:
            br = self._classify(st, cur)
            if br is None:
                st.phase = "IDLE"
                return
            d, sl = self._epar(sym, st, cur, br)
            if d is None:
                st.phase = "IDLE"
                return
            sd = abs(cur[3] - sl)
            if sd > self.smax * atr:
                st.phase = "IDLE"
                return

            b2 = cur[4] + self.tf
            ck = "{}_H12_{}".format(sym, self.k)
            sig = json.dumps({
                "combo_key": ck,
                "entry_bar_ts": b2,
                "sl_dist": round(sd, 6),
                "trail_trig_r": self.t_trig,
                "trail_dist_r": self.t_off,
                "branch": br,
                "timeout_bars": self.tmo,
            })
            actions.append({
                "action": "ENTER",
                "symbol": sym,
                "direction": d,
                "sl_price": round(sl, 6),
                "tp_price": 0,
                "signal_data": sig,
                "comment": "REMR {}_{}".format(ck, br),
            })
            st.phase = "IDLE"

        # WAIT_B1 but got PM instead (data gap or skip) -- reset and re-check
        elif st.phase == "WAIT_B1" and is_pm:
            st.phase = "IDLE"
            self._signal(sym, actions)

    def _classify(self, st, b1):
        """Classify B1 bar as FADE, CONT, or None (skip)."""
        o, h, lo, c, _ = b1

        if st.b0_dir == "UP":
            # FADE: B1 didn't exceed B0 high, closed >= depth below B0 high
            if h <= st.b0_high and c <= st.b0_high - self.depth * st.b0_range:
                return "FADE"
            # CONT: B1 broke above B0 high, closed in upper cpct of its range
            rng = h - lo
            if rng > 0 and h > st.b0_high:
                if c >= lo + (1.0 - self.cpct) * rng:
                    return "CONT"
        else:
            # FADE: B1 didn't go below B0 low, closed >= depth above B0 low
            if lo >= st.b0_low and c >= st.b0_low + self.depth * st.b0_range:
                return "FADE"
            # CONT: B1 broke below B0 low, closed in lower cpct of its range
            rng = h - lo
            if rng > 0 and lo < st.b0_low:
                if c <= h - (1.0 - self.cpct) * rng:
                    return "CONT"
        return None

    def _epar(self, sym, st, b1, br):
        """Compute entry direction and SL level for given branch."""
        buf = self.bufm * _spread_px(sym)
        o, h, lo, c, _ = b1

        if br == "FADE":
            if st.b0_dir == "UP":
                return "SHORT", st.b0_high + buf    # SL above B0 high
            return "LONG", st.b0_low - buf           # SL below B0 low
        if br == "CONT":
            if st.b0_dir == "UP":
                return "LONG", lo - buf              # SL below B1 low
            return "SHORT", h + buf                  # SL above B1 high
        return None, None

    # ── position management ──────────────────────────────────────

    def _sync(self, dpos):
        """Sync tracked positions with daemon's live positions."""
        live = set()
        for p in dpos:
            tk = p.get("ticket", 0)
            live.add(tk)
            if tk not in self._pos:
                # New position from daemon (crash recovery or manual)
                ep = p.get("price_open", 0.0)
                sl = p.get("sl", 0.0)
                sd = abs(ep - sl) if ep and sl else 1.0
                self._pos[tk] = PosTrack(
                    ticket=tk, symbol=p.get("symbol", ""),
                    direction=p.get("direction", ""),
                    entry_price=ep, sl_dist=sd,
                    entry_bar_ts=p.get("open_time", 0),  # NOTE: Scheduler.cs doesn't send this yet
                    timeout_bars=self.tmo,
                    trail_trig=self.t_trig, trail_off=self.t_off,
                    branch="RECOVERED", best_price=ep,
                )
            self._pos[tk].last_sl = p.get("sl", 0.0)
        # Remove positions closed by broker/daemon
        for tk in [t for t in self._pos if t not in live]:
            del self._pos[tk]

    def _has(self, sym):
        """Check if we have an open position on this symbol."""
        return any(pt.symbol == sym for pt in self._pos.values())

    def _trail(self, sym, raw, dpos, actions):
        """Update trailing stop using tick-TF bar data for high-resolution MFE tracking.

        Scans last N bars (~90 min window) to catch highs/lows that might occur when a bar
        closes between scheduler polls (every 10s). best_price is a running
        max/min that persists across ticks, so only truly new extremes matter.
        """
        if not raw:
            return

        # Scan last N bars (covers ~90 min regardless of tick TF)
        n = self._trail_scan
        scan = raw[-n:] if len(raw) >= n else raw
        scan_high = max(b["high"] for b in scan)
        scan_low  = min(b["low"]  for b in scan)

        sl_lk = {p.get("ticket", 0): p.get("sl", 0.0) for p in dpos}

        for pt in list(self._pos.values()):
            if pt.symbol != sym or pt.sl_dist <= 0:
                continue

            # Update best price from bar scan
            if pt.direction == "LONG":
                if scan_high > pt.best_price:
                    pt.best_price = scan_high
                mfe = (pt.best_price - pt.entry_price) / pt.sl_dist
            else:
                if pt.best_price <= 0 or scan_low < pt.best_price:
                    pt.best_price = scan_low
                mfe = (pt.entry_price - pt.best_price) / pt.sl_dist

            if mfe < pt.trail_trig:
                continue

            # Compute new SL from best price
            if pt.direction == "LONG":
                ns = pt.best_price - pt.trail_off * pt.sl_dist
                cs = sl_lk.get(pt.ticket, pt.last_sl)
                if cs and ns > cs + 1e-7:
                    actions.append({
                        "action": "MODIFY_SL",
                        "ticket": pt.ticket,
                        "new_sl": round(ns, 6),
                        "comment": "REMR trail {}".format(sym),
                    })
                    pt.trail_active = True
                    pt.last_sl = ns
            else:
                ns = pt.best_price + pt.trail_off * pt.sl_dist
                cs = sl_lk.get(pt.ticket, pt.last_sl)
                if cs and ns < cs - 1e-7:
                    actions.append({
                        "action": "MODIFY_SL",
                        "ticket": pt.ticket,
                        "new_sl": round(ns, 6),
                        "comment": "REMR trail {}".format(sym),
                    })
                    pt.trail_active = True
                    pt.last_sl = ns

    def _timeouts(self, actions):
        """Exit positions that exceeded timeout_bars H12 bars."""
        done = {a["ticket"] for a in actions if a.get("action") == "EXIT"}
        for pt in list(self._pos.values()):
            if pt.ticket in done:
                continue
            if pt.entry_bar_ts <= 0:
                continue  # Recovered position without open_time -- skip timeout
            ch = self._last.get(pt.symbol, 0)
            if ch <= pt.entry_bar_ts:
                continue
            bh = (ch - pt.entry_bar_ts) // self.tf
            if bh >= pt.timeout_bars:
                actions.append({
                    "action": "EXIT",
                    "ticket": pt.ticket,
                    "comment": "REMR timeout {} bars".format(bh),
                })
