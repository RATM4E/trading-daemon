"""
Pairs Z-Score V1 — Strategy for Trading Daemon
================================================
Trades mean-reversion of correlated forex pairs using z-score of log spread.

Pairs:
  1. NZDJPY / CADJPY  (lb=50,  z=2.5, exit=0.0)
  2. AUDJPY / CADJPY  (lb=200, z=2.0, exit=0.5)
  3. EURCAD / EURNZD  (lb=200, z=2.0, exit=0.5)

Mechanics:
  Entry:
    z > +entry_z  → SHORT spread (short A, long B)
    z < -entry_z  → LONG spread  (long A, short B)
    Vol filter: skip if spread vol < vol_pctl_min expanding percentile

  Stop-Loss (3-phase):
    Phase 0 — Initial:  SL = initial_sl_atr × ATR(14) from entry price
    Phase 1 — Breakeven: z retreats to be_z_ratio × entry_z → SL both legs to entry
    Phase 2 — Trail:     z continues toward exit → trail SL using lock_atr × ATR

  Exit (strategy-managed via EXIT actions):
    |z| < exit_z       → mean reversion complete
    |z| > stop_z       → spread diverged (z-stop)
    bars_held > max_hold → time-based exit

  Orphan protection:
    If one leg SL triggers → close the other leg immediately

Interface:
  - Standard Strategy class for runner.py
  - get_requirements() → symbols / timeframes / history_bars
  - on_bars(bars, positions) → list of ENTER / EXIT / MODIFY_SL actions
  - save_state() / restore_state() for crash recovery
"""

import json
import math
import time
from datetime import datetime


def log(msg: str):
    ts = datetime.now().strftime("%H:%M:%S.%f")[:-3]
    print(f"[pairs_zs {ts}] {msg}", flush=True)


# ╔══════════════════════════════════════════════════════════════╗
#  Indicator computations
# ╚══════════════════════════════════════════════════════════════╝

def compute_atr(bars: list, period: int = 14) -> float:
    """Compute latest ATR using Wilder's smoothing. Returns scalar."""
    n = len(bars)
    if n < period + 1:
        return 0.0

    tr = [bars[0]["high"] - bars[0]["low"]]
    for i in range(1, n):
        tr.append(max(
            bars[i]["high"] - bars[i]["low"],
            abs(bars[i]["high"] - bars[i - 1]["close"]),
            abs(bars[i]["low"] - bars[i - 1]["close"])
        ))

    atr = sum(tr[:period]) / period
    for i in range(period, n):
        atr = (atr * (period - 1) + tr[i]) / period
    return atr


def align_close_arrays(bars_a: list, bars_b: list):
    """
    Inner-join two bar arrays on timestamp.
    Returns (close_a[], close_b[], time[]) — aligned arrays.
    """
    b_map = {}
    for b in bars_b:
        b_map[b["time"]] = b["close"]

    close_a, close_b, times = [], [], []
    for bar in bars_a:
        t = bar["time"]
        if t in b_map:
            close_a.append(bar["close"])
            close_b.append(b_map[t])
            times.append(t)

    return close_a, close_b, times


def compute_zscore(close_a: list, close_b: list, lookback: int):
    """Z-score of log(A/B) spread. Returns (z_array, spread_array)."""
    n = len(close_a)
    spread = []
    for i in range(n):
        if close_a[i] > 0 and close_b[i] > 0:
            spread.append(math.log(close_a[i]) - math.log(close_b[i]))
        else:
            spread.append(0.0)

    z = [float('nan')] * n
    for i in range(lookback, n):
        window = spread[i - lookback: i]
        m = sum(window) / lookback
        var = sum((x - m) ** 2 for x in window) / lookback
        sd = math.sqrt(var) if var > 0 else 0.0
        if sd > 1e-12:
            z[i] = (spread[i] - m) / sd
    return z, spread


def compute_spread_vol(spread: list, vol_lookback: int):
    """Rolling std of spread returns."""
    n = len(spread)
    ret = [0.0]
    for i in range(1, n):
        ret.append(spread[i] - spread[i - 1])

    vol = [float('nan')] * n
    for i in range(vol_lookback, n):
        window = ret[i - vol_lookback: i]
        m = sum(window) / vol_lookback
        var = sum((x - m) ** 2 for x in window) / vol_lookback
        vol[i] = math.sqrt(var) if var > 0 else 0.0
    return vol


def expanding_percentile(val: float, arr: list, idx: int) -> float:
    """What percentile is val relative to arr[:idx]?"""
    if idx < 50 or math.isnan(val):
        return 50.0
    below = sum(1 for j in range(idx) if not math.isnan(arr[j]) and arr[j] <= val)
    valid = sum(1 for j in range(idx) if not math.isnan(arr[j]))
    if valid == 0:
        return 50.0
    return below / valid * 100.0


# ╔══════════════════════════════════════════════════════════════╗
#  Pair trade state tracking
# ╚══════════════════════════════════════════════════════════════╝

class PairTrade:
    """Tracks one open pair trade (two legs) with SL state."""

    __slots__ = [
        'pair_name', 'direction',
        'ticket_a', 'ticket_b',
        'entry_price_a', 'entry_price_b',
        'entry_atr_a', 'entry_atr_b',
        'entry_time', 'entry_z',
        'bars_held',
        'sl_phase',        # 0=initial, 1=breakeven, 2=trailing
        'last_sl_a',       # last SL sent for leg A
        'last_sl_b',       # last SL sent for leg B
        'best_z',          # best z toward exit (for trailing)
    ]

    def __init__(self, pair_name: str, direction: int,
                 ticket_a: int = 0, ticket_b: int = 0,
                 entry_time: int = 0, entry_z: float = 0.0):
        self.pair_name = pair_name
        self.direction = direction
        self.ticket_a = ticket_a
        self.ticket_b = ticket_b
        self.entry_price_a = 0.0
        self.entry_price_b = 0.0
        self.entry_atr_a = 0.0
        self.entry_atr_b = 0.0
        self.entry_time = entry_time
        self.entry_z = entry_z
        self.bars_held = 0
        self.sl_phase = 0
        self.last_sl_a = 0.0
        self.last_sl_b = 0.0
        self.best_z = entry_z

    def to_dict(self) -> dict:
        return {k: getattr(self, k) for k in self.__slots__}

    @classmethod
    def from_dict(cls, d: dict) -> 'PairTrade':
        pt = cls(
            pair_name=d['pair_name'], direction=d['direction'],
            ticket_a=d.get('ticket_a', 0), ticket_b=d.get('ticket_b', 0),
            entry_time=d.get('entry_time', 0), entry_z=d.get('entry_z', 0.0),
        )
        for k in cls.__slots__:
            if k in d and hasattr(pt, k):
                setattr(pt, k, d[k])
        return pt


# ╔══════════════════════════════════════════════════════════════╗
#  Strategy Class
# ╚══════════════════════════════════════════════════════════════╝

class Strategy:
    """Pairs Z-Score V1 — implements standard Strategy interface."""

    def __init__(self, config: dict):
        self.config = config
        p = config.get("params", config)  # new schema: params block; fallback: root

        self.history_bars = p.get("history_bars", config.get("history_bars", 600))
        self.atr_period = p.get("atr_period", config.get("atr_period", 14))

        # SL configuration
        self.initial_sl_atr = p.get("initial_sl_atr", config.get("initial_sl_atr", 2.0))
        self.be_z_ratio = p.get("be_z_ratio", config.get("be_z_ratio", 0.5))
        self.trail_lock_atr = p.get("trail_lock_atr", config.get("trail_lock_atr", 1.0))

        # Parse pairs from combos (new schema) or pairs (legacy)
        raw_pairs = config.get("pairs", [])
        if not raw_pairs and "combos" in config:
            # New schema: combos with strat/daemon
            for combo in config["combos"]:
                flat = {
                    "name": combo["sym"],
                    "symA": combo["symA"],
                    "symB": combo["symB"],
                }
                flat.update(combo.get("strat", {}))
                raw_pairs.append(flat)

        self.pairs_cfg = raw_pairs

        # Build pair lookup
        self.pair_by_name = {pr["name"]: pr for pr in self.pairs_cfg}

        # Collect all unique symbols
        self.all_symbols = set()
        for p in self.pairs_cfg:
            self.all_symbols.add(p["symA"])
            self.all_symbols.add(p["symB"])

        # Open pair trades: pair_name → PairTrade
        self.open_pairs: dict[str, PairTrade] = {}

        # Pending entries: pair_name → direction
        self.pending_entries: dict[str, int] = {}

        # Signal dedup: (pair_name, direction) → last bar time
        self.last_signal_time: dict[tuple, int] = {}

        self.tick_count = 0
        self.last_z: dict[str, float] = {}

        log(f"Init: {len(self.pairs_cfg)} pairs, "
            f"{len(self.all_symbols)} symbols, "
            f"SL: initial={self.initial_sl_atr}xATR "
            f"BE@{self.be_z_ratio}x_entry_z "
            f"trail_lock={self.trail_lock_atr}xATR")
        for p in self.pairs_cfg:
            log(f"  {p['name']}: {p['symA']}/{p['symB']} "
                f"lb={p['lookback']} z={p['entry_z']} "
                f"exit={p['exit_z']} stop={p['stop_z']} hold={p['max_hold']}")

    # ───────────────────────────────────────────────────────────
    #  Protocol interface
    # ───────────────────────────────────────────────────────────

    def get_requirements(self) -> dict:
        symbols = sorted(self.all_symbols)
        timeframes = {sym: "H1" for sym in symbols}
        return {
            "symbols": symbols,
            "timeframes": timeframes,
            "history_bars": self.history_bars,
        }

    def on_bars(self, bars: dict, positions: list) -> list:
        """Main loop. Called on each TICK. Returns ENTER / EXIT / MODIFY_SL."""
        self.tick_count += 1
        actions = []

        pos_by_pair = self._build_pair_position_map(positions)
        live_tickets = {p["ticket"] for p in positions}

        # 1. Orphan detection
        actions.extend(self._handle_orphans(pos_by_pair, live_tickets))

        # 2. Match pending entries
        self._match_pending_entries(pos_by_pair, positions)

        # 3. Process each pair
        for pair_cfg in self.pairs_cfg:
            pair_name = pair_cfg["name"]
            symA, symB = pair_cfg["symA"], pair_cfg["symB"]

            bars_a = bars.get(symA)
            bars_b = bars.get(symB)
            if not bars_a or not bars_b:
                continue
            if len(bars_a) < pair_cfg["lookback"] + 50:
                continue
            if len(bars_b) < pair_cfg["lookback"] + 50:
                continue

            close_a, close_b, times = align_close_arrays(bars_a, bars_b)
            if len(close_a) < pair_cfg["lookback"] + 50:
                continue

            z, spread = compute_zscore(close_a, close_b, pair_cfg["lookback"])
            n = len(z)
            current_z = z[n - 1] if n > 0 and not math.isnan(z[n - 1]) else None
            current_time = times[n - 1] if n > 0 else 0
            self.last_z[pair_name] = current_z if current_z is not None else 0.0

            if current_z is None:
                continue

            atr_a = compute_atr(bars_a, self.atr_period)
            atr_b = compute_atr(bars_b, self.atr_period)

            # Vol filter
            vol_pass = True
            if pair_cfg.get("vol_pctl_min", 0) > 0:
                svol = compute_spread_vol(spread, pair_cfg["vol_lookback"])
                if n > 0 and not math.isnan(svol[n - 1]):
                    pctl = expanding_percentile(svol[n - 1], svol, n - 1)
                    if pctl < pair_cfg["vol_pctl_min"]:
                        vol_pass = False

            # ── Open pair: manage SL + check exit ──
            if pair_name in self.open_pairs:
                pt = self.open_pairs[pair_name]
                pt.bars_held += 1

                pair_pos = pos_by_pair.get(pair_name, {})
                price_a = bars_a[-1]["close"]
                price_b = bars_b[-1]["close"]

                # SL management
                sl_actions = self._manage_sl(
                    pt, pair_cfg, pair_pos, current_z,
                    price_a, price_b, atr_a, atr_b)
                actions.extend(sl_actions)

                # Exit checks
                exit_reason = None
                if abs(current_z) < pair_cfg["exit_z"]:
                    exit_reason = "z_exit"
                elif abs(current_z) > pair_cfg["stop_z"]:
                    exit_reason = "z_stop"
                elif pt.bars_held >= pair_cfg["max_hold"]:
                    exit_reason = "max_hold"

                if exit_reason:
                    actions.extend(self._exit_pair(
                        pair_name, pair_pos, exit_reason, current_z))

            # ── No position: check entry ──
            elif pair_name not in self.pending_entries:
                if not vol_pass:
                    continue

                entry_actions = self._check_entry(
                    pair_cfg, current_z, current_time,
                    bars_a, bars_b, atr_a, atr_b)
                actions.extend(entry_actions)

        return actions

    def save_state(self) -> dict:
        return {
            "tick_count": self.tick_count,
            "open_pairs": {k: v.to_dict() for k, v in self.open_pairs.items()},
            "pending_entries": self.pending_entries,
            "last_signal_time": {
                f"{k[0]}|{k[1]}": v for k, v in self.last_signal_time.items()
            },
            "last_z": self.last_z,
        }

    def restore_state(self, state: dict):
        self.tick_count = state.get("tick_count", 0)
        for k, v in state.get("open_pairs", {}).items():
            self.open_pairs[k] = PairTrade.from_dict(v)
        self.pending_entries = state.get("pending_entries", {})
        lsb = state.get("last_signal_time", {})
        self.last_signal_time = {
            (k.split("|")[0], k.split("|")[1]): v for k, v in lsb.items()
        }
        self.last_z = state.get("last_z", {})
        log(f"State restored: tick={self.tick_count}, "
            f"open_pairs={list(self.open_pairs.keys())}, "
            f"pending={list(self.pending_entries.keys())}")

    # ───────────────────────────────────────────────────────────
    #  SL Management — 3 phases
    # ───────────────────────────────────────────────────────────

    def _manage_sl(self, pt: PairTrade, pair_cfg: dict,
                   pair_pos: dict, current_z: float,
                   price_a: float, price_b: float,
                   atr_a: float, atr_b: float) -> list:
        """
        3-phase SL management for open pair.

        Phase 0 (Initial):
            SL at initial_sl_atr * ATR from entry. Set at ENTER time.
            No changes — waiting for z to retreat toward mean.

        Phase 1 (Breakeven):
            Trigger: |z| retreated to be_z_ratio * |entry_z| toward mean.
            Action: move both legs SL to entry price (zero risk).

        Phase 2 (Trail):
            Trigger: z continues past BE level toward exit.
            Action: lock profit by trailing SL at current_price +/- lock_atr * ATR.
            Ratchet: only tighten, never loosen.
        """
        actions = []

        if not pair_pos.get("A") or not pair_pos.get("B"):
            return actions

        entry_z_abs = abs(pt.entry_z)
        current_z_abs = abs(current_z)

        # Z progress toward exit: entry_z → exit_z. progress=1.0 when at exit_z.
        exit_z = pair_cfg["exit_z"]
        z_range = entry_z_abs - exit_z
        if z_range > 0.01:
            progress = (entry_z_abs - current_z_abs) / z_range
        else:
            progress = 0.0

        # Track best z (closest to exit)
        if current_z_abs < abs(pt.best_z):
            pt.best_z = current_z

        # ── Phase 0 → 1: Breakeven trigger ──
        if pt.sl_phase == 0:
            if progress >= self.be_z_ratio:
                pt.sl_phase = 1

                new_sl_a = pt.entry_price_a
                new_sl_b = pt.entry_price_b

                sl_a_act = self._make_modify_sl(pt, "A", pair_pos["A"], new_sl_a)
                sl_b_act = self._make_modify_sl(pt, "B", pair_pos["B"], new_sl_b)

                if sl_a_act:
                    actions.append(sl_a_act)
                if sl_b_act:
                    actions.append(sl_b_act)

                if sl_a_act or sl_b_act:
                    log(f"SL->BE {pt.pair_name}: z={current_z:+.3f} "
                        f"progress={progress:.0%}")

        # ── Phase 1 → 2: Trail trigger ──
        if pt.sl_phase == 1:
            trail_trigger = self.be_z_ratio + 0.10
            if progress >= trail_trigger:
                pt.sl_phase = 2

        # ── Phase 2: Trail SL ──
        if pt.sl_phase == 2:
            lock_a = self.trail_lock_atr * atr_a
            lock_b = self.trail_lock_atr * atr_b

            pos_a = pair_pos["A"]
            pos_b = pair_pos["B"]

            # Trail: current price ± lock distance
            if pos_a["direction"] == "LONG":
                new_sl_a = price_a - lock_a
            else:
                new_sl_a = price_a + lock_a

            if pos_b["direction"] == "LONG":
                new_sl_b = price_b - lock_b
            else:
                new_sl_b = price_b + lock_b

            sl_a_act = self._make_modify_sl(pt, "A", pos_a, new_sl_a)
            sl_b_act = self._make_modify_sl(pt, "B", pos_b, new_sl_b)

            if sl_a_act:
                actions.append(sl_a_act)
            if sl_b_act:
                actions.append(sl_b_act)

        return actions

    def _make_modify_sl(self, pt: PairTrade, leg: str,
                        pos: dict, new_sl: float) -> dict | None:
        """
        Create MODIFY_SL if new SL is tighter than current.
        LONG: SL must move UP. SHORT: SL must move DOWN.
        """
        direction = pos["direction"]
        current_sl = pos["sl"]

        if direction == "LONG":
            if new_sl <= current_sl + 1e-6:
                return None
        else:
            if new_sl >= current_sl - 1e-6:
                return None

        if leg == "A":
            pt.last_sl_a = new_sl
        else:
            pt.last_sl_b = new_sl

        return {
            "action": "MODIFY_SL",
            "ticket": pos["ticket"],
            "new_sl": round(new_sl, 6),
            "comment": f"pz_{pt.pair_name}_sl{pt.sl_phase}",
        }

    # ───────────────────────────────────────────────────────────
    #  Position matching helpers
    # ───────────────────────────────────────────────────────────

    def _build_pair_position_map(self, positions: list) -> dict:
        """
        Group positions by pair from comment.
        Comment: "pz_{PAIR}_{LS|SS}_{A|B}"
        Returns: {pair_name: {"A": pos_dict, "B": pos_dict}}
        """
        result = {}
        for pos in positions:
            comment = pos.get("comment", "") or ""
            if not comment.startswith("pz_"):
                continue

            for pair_cfg in self.pairs_cfg:
                pair_name = pair_cfg["name"]
                prefix_ls = f"pz_{pair_name}_LS"
                prefix_ss = f"pz_{pair_name}_SS"

                if comment.startswith(prefix_ls) or comment.startswith(prefix_ss):
                    if pair_name not in result:
                        result[pair_name] = {}
                    if comment.endswith("_A"):
                        result[pair_name]["A"] = pos
                    elif comment.endswith("_B"):
                        result[pair_name]["B"] = pos
                    break

        return result

    def _match_pending_entries(self, pos_by_pair: dict, positions: list):
        """Match pending entries to filled positions. Capture fill prices."""
        matched = []

        for pair_name, direction in list(self.pending_entries.items()):
            pair_pos = pos_by_pair.get(pair_name, {})

            if "A" in pair_pos and "B" in pair_pos:
                pos_a = pair_pos["A"]
                pos_b = pair_pos["B"]

                pt = PairTrade(
                    pair_name=pair_name,
                    direction=direction,
                    ticket_a=pos_a["ticket"],
                    ticket_b=pos_b["ticket"],
                    entry_time=int(time.time()),
                    entry_z=self.last_z.get(pair_name, 0.0),
                )
                pt.entry_price_a = pos_a["price_open"]
                pt.entry_price_b = pos_b["price_open"]
                pt.last_sl_a = pos_a["sl"]
                pt.last_sl_b = pos_b["sl"]

                # Recover entry ATR from SL distance
                sl_dist_a = abs(pos_a["price_open"] - pos_a["sl"])
                sl_dist_b = abs(pos_b["price_open"] - pos_b["sl"])
                pt.entry_atr_a = sl_dist_a / self.initial_sl_atr if self.initial_sl_atr > 0 else 0
                pt.entry_atr_b = sl_dist_b / self.initial_sl_atr if self.initial_sl_atr > 0 else 0

                pt.best_z = pt.entry_z

                self.open_pairs[pair_name] = pt
                matched.append(pair_name)

                log(f"PAIR OPENED {pair_name} "
                    f"dir={'LS' if direction == 1 else 'SS'} "
                    f"z={pt.entry_z:+.3f} "
                    f"A:{pos_a['direction']}@{pt.entry_price_a:.5f} "
                    f"B:{pos_b['direction']}@{pt.entry_price_b:.5f} "
                    f"SL_A={pos_a['sl']:.5f} SL_B={pos_b['sl']:.5f}")

        for name in matched:
            del self.pending_entries[name]

    def _handle_orphans(self, pos_by_pair: dict, live_tickets: set) -> list:
        """If one leg gone (SL hit), close the other immediately."""
        actions = []
        closed = []

        for pair_name, pt in list(self.open_pairs.items()):
            a_alive = pt.ticket_a in live_tickets
            b_alive = pt.ticket_b in live_tickets

            if not a_alive and not b_alive:
                log(f"PAIR CLOSED (both legs gone) {pair_name}")
                closed.append(pair_name)

            elif a_alive and not b_alive:
                log(f"ORPHAN {pair_name}: leg B gone, closing A "
                    f"(ticket {pt.ticket_a})")
                actions.append({
                    "action": "EXIT",
                    "ticket": pt.ticket_a,
                    "comment": f"pz_{pair_name}_orphan",
                })
                closed.append(pair_name)

            elif b_alive and not a_alive:
                log(f"ORPHAN {pair_name}: leg A gone, closing B "
                    f"(ticket {pt.ticket_b})")
                actions.append({
                    "action": "EXIT",
                    "ticket": pt.ticket_b,
                    "comment": f"pz_{pair_name}_orphan",
                })
                closed.append(pair_name)

        for name in closed:
            del self.open_pairs[name]

        return actions

    # ───────────────────────────────────────────────────────────
    #  Entry logic
    # ───────────────────────────────────────────────────────────

    def _check_entry(self, pair_cfg: dict, current_z: float,
                     current_time: int, bars_a: list, bars_b: list,
                     atr_a: float, atr_b: float) -> list:
        """Check for pair entry. Sets initial SL = initial_sl_atr * ATR."""
        pair_name = pair_cfg["name"]
        entry_z = pair_cfg["entry_z"]

        direction = 0
        if current_z > entry_z:
            direction = -1   # short spread
        elif current_z < -entry_z:
            direction = 1    # long spread

        if direction == 0:
            return []

        # Dedup
        sig_key = (pair_name, direction)
        if self.last_signal_time.get(sig_key) == current_time:
            return []
        self.last_signal_time[sig_key] = current_time

        if atr_a <= 0 or atr_b <= 0:
            log(f"SKIP {pair_name}: ATR zero")
            return []

        symA = pair_cfg["symA"]
        symB = pair_cfg["symB"]
        price_a = bars_a[-1]["close"]
        price_b = bars_b[-1]["close"]

        sl_dist_a = self.initial_sl_atr * atr_a
        sl_dist_b = self.initial_sl_atr * atr_b

        dir_label = "LS" if direction == 1 else "SS"
        actions = []

        if direction == 1:
            # Long spread: LONG A, SHORT B
            actions.append({
                "action": "ENTER",
                "symbol": symA,
                "direction": "LONG",
                "sl_price": round(price_a - sl_dist_a, 6),
                "comment": f"pz_{pair_name}_{dir_label}_A",
                "signal_data": json.dumps({
                    "pair": pair_name, "leg": "A", "spread_dir": dir_label,
                    "z": round(current_z, 4), "atr": round(atr_a, 6),
                    "sl_dist": round(sl_dist_a, 6),
                }),
            })
            actions.append({
                "action": "ENTER",
                "symbol": symB,
                "direction": "SHORT",
                "sl_price": round(price_b + sl_dist_b, 6),
                "comment": f"pz_{pair_name}_{dir_label}_B",
                "signal_data": json.dumps({
                    "pair": pair_name, "leg": "B", "spread_dir": dir_label,
                    "z": round(current_z, 4), "atr": round(atr_b, 6),
                    "sl_dist": round(sl_dist_b, 6),
                }),
            })

        else:  # direction == -1
            # Short spread: SHORT A, LONG B
            actions.append({
                "action": "ENTER",
                "symbol": symA,
                "direction": "SHORT",
                "sl_price": round(price_a + sl_dist_a, 6),
                "comment": f"pz_{pair_name}_{dir_label}_A",
                "signal_data": json.dumps({
                    "pair": pair_name, "leg": "A", "spread_dir": dir_label,
                    "z": round(current_z, 4), "atr": round(atr_a, 6),
                    "sl_dist": round(sl_dist_a, 6),
                }),
            })
            actions.append({
                "action": "ENTER",
                "symbol": symB,
                "direction": "LONG",
                "sl_price": round(price_b - sl_dist_b, 6),
                "comment": f"pz_{pair_name}_{dir_label}_B",
                "signal_data": json.dumps({
                    "pair": pair_name, "leg": "B", "spread_dir": dir_label,
                    "z": round(current_z, 4), "atr": round(atr_b, 6),
                    "sl_dist": round(sl_dist_b, 6),
                }),
            })

        self.pending_entries[pair_name] = direction

        log(f"ENTRY {pair_name} {dir_label}: z={current_z:+.3f} "
            f"SL_A={sl_dist_a:.5f}({self.initial_sl_atr}xATR) "
            f"SL_B={sl_dist_b:.5f}({self.initial_sl_atr}xATR)")

        return actions

    # ───────────────────────────────────────────────────────────
    #  Exit logic
    # ───────────────────────────────────────────────────────────

    def _exit_pair(self, pair_name: str, pair_pos: dict,
                   reason: str, current_z: float) -> list:
        """Send EXIT for both legs."""
        actions = []
        pt = self.open_pairs.get(pair_name)
        if pt is None:
            return []

        for leg_key in ["A", "B"]:
            pos = pair_pos.get(leg_key)
            if pos:
                actions.append({
                    "action": "EXIT",
                    "ticket": pos["ticket"],
                    "comment": f"pz_{pair_name}_{reason}",
                })

        if actions:
            dir_label = "LS" if pt.direction == 1 else "SS"
            log(f"EXIT {pair_name} {dir_label}: reason={reason} "
                f"z={current_z:+.3f} held={pt.bars_held}bars "
                f"sl_phase={pt.sl_phase} closing {len(actions)} leg(s)")

        if pair_name in self.open_pairs:
            del self.open_pairs[pair_name]

        return actions
