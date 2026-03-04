"""
MAXTRADE V2 — Strategy for Trading Daemon
==========================================
VWAP σ-band breakout with EMA3 cross trigger.

Entry:
  VWAP = SMA(typical_price, P)
  σ    = StdDev(typical_price, P, ddof=0)
  EMA3 = EMA(close, 3)
  Upper = VWAP + band_mult × σ
  Lower = VWAP - band_mult × σ

  LONG:  EMA3 crosses ABOVE upper band
  SHORT: EMA3 crosses BELOW lower band

SL:
  LONG:  entry - sl_mult × σ
  SHORT: entry + sl_mult × σ

Exit modes:
  A_fixed:   TP symmetric (1:1 RR), timeout at P/3 bars
  B_protect: TP symmetric, BE trigger at 0.2×SL → trail SL to entry
  C_trail:   No fixed TP, BE trigger, then trail SL at peak ± 0.5×σ

Timeout: P/3 bars → close at market

Config: loaded from config.json, field "combos" is a list of dicts:
  {sym, dir, tf, P, sl_mult, mode, size_r, tier, role, aclass}

Interface (standard daemon protocol):
  get_requirements() → symbols/timeframes/history_bars
  on_bars(bars, positions) → list of ENTER/EXIT/MODIFY_SL actions
  save_state() / restore_state()
"""

import json
import math
import time
from datetime import datetime


def log(msg: str):
    ts = datetime.now().strftime("%H:%M:%S.%f")[:-3]
    print(f"[maxtrade_v2 {ts}] {msg}", flush=True)


# ═══════════════════════════════════════════════════════════════
#  Indicator computations
# ═══════════════════════════════════════════════════════════════

def compute_sma(values: list, period: int) -> list:
    """Simple moving average. NaN-padded."""
    n = len(values)
    result = [float('nan')] * n
    if n < period:
        return result
    s = sum(values[:period])
    result[period - 1] = s / period
    for i in range(period, n):
        s += values[i] - values[i - period]
        result[i] = s / period
    return result


def compute_std_pop(values: list, sma: list, period: int) -> list:
    """Population std (ddof=0). Matches backtest rolling std."""
    n = len(values)
    result = [float('nan')] * n
    if n < period:
        return result
    for i in range(period - 1, n):
        if math.isnan(sma[i]):
            continue
        ss = 0.0
        for k in range(i - period + 1, i + 1):
            ss += (values[k] - sma[i]) ** 2
        result[i] = math.sqrt(ss / period)
    return result


def compute_ema(values: list, period: int) -> list:
    """Exponential moving average. NaN until first valid."""
    n = len(values)
    result = [float('nan')] * n
    if n == 0:
        return result
    alpha = 2.0 / (period + 1)
    result[0] = values[0]
    for i in range(1, n):
        result[i] = alpha * values[i] + (1 - alpha) * result[i - 1]
    return result


def compute_typical_price(bars: list) -> list:
    """(high + low + close) / 3"""
    return [(b["high"] + b["low"] + b["close"]) / 3.0 for b in bars]


# ═══════════════════════════════════════════════════════════════
#  Per-combo indicator state
# ═══════════════════════════════════════════════════════════════

class ComboState:
    """Cached indicators for one combo (sym + dir + tf + P)."""

    __slots__ = [
        'combo_id', 'cfg',
        'vwap', 'sigma', 'upper', 'lower', 'ema3',
        'close', 'high', 'low', 'open',
        'last_bar_time', 'bars_count',
    ]

    def __init__(self, combo_id: str, cfg: dict):
        self.combo_id = combo_id
        self.cfg = cfg
        self.last_bar_time = 0
        self.bars_count = 0

    def update(self, bars: list, band_mult: float, ema_period: int):
        """Recompute indicators from full bar history."""
        n = len(bars)
        self.bars_count = n
        if n == 0:
            return

        self.close = [b["close"] for b in bars]
        self.high = [b["high"] for b in bars]
        self.low = [b["low"] for b in bars]
        self.open = [b["open"] for b in bars]
        self.last_bar_time = bars[-1]["time"] if bars else 0

        P = self.cfg["P"]
        tp = compute_typical_price(bars)
        self.vwap = compute_sma(tp, P)
        self.sigma = compute_std_pop(tp, self.vwap, P)
        self.ema3 = compute_ema(self.close, ema_period)

        self.upper = [float('nan')] * n
        self.lower = [float('nan')] * n
        for i in range(n):
            if not math.isnan(self.vwap[i]) and not math.isnan(self.sigma[i]):
                self.upper[i] = self.vwap[i] + band_mult * self.sigma[i]
                self.lower[i] = self.vwap[i] - band_mult * self.sigma[i]

    def ready(self) -> bool:
        if self.bars_count < 2:
            return False
        i = self.bars_count - 1
        return (not math.isnan(self.upper[i]) and
                not math.isnan(self.ema3[i]) and
                not math.isnan(self.ema3[i - 1]))


# ═══════════════════════════════════════════════════════════════
#  Position tracking (per-trade state for exit management)
# ═══════════════════════════════════════════════════════════════

class PosTrack:
    """Tracks one open position for exit management."""

    __slots__ = [
        'ticket', 'combo_id', 'symbol', 'direction',
        'entry_price', 'initial_sl', 'sl_distance',
        'mode', 'timeout_bars', 'entry_bar_time',
        'be_triggered', 'last_sent_sl',
        'peak_price', 'bars_elapsed', 'sigma_at_entry',
    ]

    def __init__(self, ticket: int, combo_id: str, symbol: str,
                 direction: str, entry_price: float, initial_sl: float,
                 mode: str, timeout_bars: int, entry_bar_time: int,
                 sigma_at_entry: float):
        self.ticket = ticket
        self.combo_id = combo_id
        self.symbol = symbol
        self.direction = direction
        self.entry_price = entry_price
        self.initial_sl = initial_sl
        self.sl_distance = abs(entry_price - initial_sl)
        self.mode = mode
        self.timeout_bars = timeout_bars
        self.entry_bar_time = entry_bar_time
        self.sigma_at_entry = sigma_at_entry
        self.be_triggered = False
        self.last_sent_sl = initial_sl
        self.peak_price = entry_price
        self.bars_elapsed = 0

    def to_dict(self) -> dict:
        return {k: getattr(self, k) for k in self.__slots__}

    @classmethod
    def from_dict(cls, d: dict) -> 'PosTrack':
        pt = cls(
            ticket=int(d['ticket']), combo_id=d['combo_id'],
            symbol=d['symbol'], direction=d['direction'],
            entry_price=d['entry_price'], initial_sl=d['initial_sl'],
            mode=d['mode'], timeout_bars=d['timeout_bars'],
            entry_bar_time=d.get('entry_bar_time', 0),
            sigma_at_entry=d.get('sigma_at_entry', 0),
        )
        pt.sl_distance = d.get('sl_distance', abs(d['entry_price'] - d['initial_sl']))
        pt.be_triggered = d.get('be_triggered', False)
        pt.last_sent_sl = d.get('last_sent_sl', d['initial_sl'])
        pt.peak_price = d.get('peak_price', d['entry_price'])
        pt.bars_elapsed = d.get('bars_elapsed', 0)
        return pt


# ═══════════════════════════════════════════════════════════════
#  Strategy Class
# ═══════════════════════════════════════════════════════════════

class Strategy:
    """MAXTRADE V2 — VWAP σ-band breakout."""

    def __init__(self, config: dict):
        self.config = config
        self.band_mult = config.get("params", {}).get("band_mult", 0.33)
        self.ema_period = config.get("params", {}).get("ema_period", 3)
        self.be_trigger_ratio = config.get("params", {}).get("be_trigger_ratio", 0.2)

        # Parse combos — flatten new nested format into same structure
        self.combos = {}          # combo_id → cfg dict
        self.combo_states = {}    # combo_id → ComboState
        self.sym_tf_combos = {}   # (sym, tf) → [combo_id, ...]

        for c in config.get("combos", []):
            # New schema: directions → LONG/SHORT → strat/daemon
            if "directions" in c:
                sym = c["sym"]
                for dir_key, dir_val in c["directions"].items():
                    flat = {"sym": sym, "dir": dir_key, "aclass": c.get("aclass", "forex")}
                    flat.update(dir_val.get("strat", {}))
                    flat.update(dir_val.get("daemon", {}))
                    flat["tier"] = c.get("tier", "T1")

                    combo_id = f"{sym}_{dir_key}"
                    self.combos[combo_id] = flat
                    self.combo_states[combo_id] = ComboState(combo_id, flat)

                    key = (sym, flat["tf"])
                    if key not in self.sym_tf_combos:
                        self.sym_tf_combos[key] = []
                    self.sym_tf_combos[key].append(combo_id)
            else:
                # Legacy flat format
                combo_id = f"{c['sym']}_{c['dir']}"
                self.combos[combo_id] = c
                self.combo_states[combo_id] = ComboState(combo_id, c)

                key = (c["sym"], c["tf"])
                if key not in self.sym_tf_combos:
                    self.sym_tf_combos[key] = []
                self.sym_tf_combos[key].append(combo_id)

        # Max history needed
        max_P = max((c["P"] for c in self.combos.values()), default=200)
        self.history_bars = max_P + 100

        # Position tracking
        self.pos_tracks: dict[int, PosTrack] = {}

        # Signal dedup: combo_id → last_signal_bar_time
        self.last_signal_bar: dict[str, int] = {}

        self.tick_count = 0

        log(f"Init: {len(self.combos)} combos, {len(self.sym_tf_combos)} sym/tf pairs, "
            f"band_mult={self.band_mult}, ema={self.ema_period}, "
            f"history={self.history_bars}")

    # ───────────────────────────────────────────────────────────
    #  Protocol interface
    # ───────────────────────────────────────────────────────────

    def get_requirements(self) -> dict:
        """Declare symbols, timeframes, and history depth."""
        symbols = list(set(c["sym"] for c in self.combos.values()))
        timeframes = {}
        for c in self.combos.values():
            sym = c["sym"]
            if sym not in timeframes:
                timeframes[sym] = c["tf"]
            # If same symbol has different TFs for L/S, strategy needs
            # to handle this — for now we take the first TF seen.
            # TODO: multi-TF per symbol support if L/S use different TFs.

        return {
            "symbols": symbols,
            "timeframes": timeframes,
            "history_bars": self.history_bars,
        }

    def on_bars(self, bars: dict, positions: list) -> list:
        """
        Main strategy loop. Called on each TICK.

        Args:
            bars: {"SYMBOL": [list of bar dicts]}
            positions: [{"ticket", "symbol", "direction", "price_open", "sl", ...}]

        Returns:
            list of action dicts
        """
        self.tick_count += 1
        actions = []

        # Build position lookup
        pos_by_combo = {}    # combo_id → position
        live_tickets = set()
        for p in positions:
            sym = p["symbol"]
            d = p["direction"]
            combo_id = f"{sym}_{d}"
            pos_by_combo[combo_id] = p
            live_tickets.add(p["ticket"])

        # Cleanup stale position tracks
        stale = [t for t in self.pos_tracks if t not in live_tickets]
        for t in stale:
            del self.pos_tracks[t]

        # Process each combo
        for combo_id, cfg in self.combos.items():
            sym = cfg["sym"]
            sym_bars = bars.get(sym)
            if not sym_bars or len(sym_bars) < cfg["P"] + 10:
                continue

            # Update indicators
            cs = self.combo_states[combo_id]
            cs.update(sym_bars, self.band_mult, self.ema_period)
            if not cs.ready():
                continue

            existing_pos = pos_by_combo.get(combo_id)

            # --- Check entry ---
            if existing_pos is None:
                entry_action = self._check_entry(cs, cfg, combo_id)
                if entry_action:
                    actions.append(entry_action)

            # --- Manage open position ---
            else:
                ticket = existing_pos["ticket"]
                if ticket in self.pos_tracks:
                    mgmt_actions = self._manage_position(
                        cs, cfg, self.pos_tracks[ticket], existing_pos)
                    actions.extend(mgmt_actions)

        return actions

    def save_state(self) -> dict:
        return {
            "tick_count": self.tick_count,
            "pos_tracks": {str(k): v.to_dict() for k, v in self.pos_tracks.items()},
            "last_signal_bar": self.last_signal_bar,
        }

    def restore_state(self, state: dict):
        self.tick_count = state.get("tick_count", 0)
        tracks = state.get("pos_tracks", {})
        self.pos_tracks = {
            int(k): PosTrack.from_dict(v) for k, v in tracks.items()
        }
        self.last_signal_bar = state.get("last_signal_bar", {})
        log(f"State restored: tick={self.tick_count}, "
            f"tracked_positions={len(self.pos_tracks)}, "
            f"signal_dedup={len(self.last_signal_bar)}")

    # ───────────────────────────────────────────────────────────
    #  Entry logic
    # ───────────────────────────────────────────────────────────

    def _check_entry(self, cs: ComboState, cfg: dict, combo_id: str):
        """
        Check for EMA3 cross of VWAP σ-band.
        LONG:  EMA3[i-1] <= upper[i-1]  AND  EMA3[i] > upper[i]
        SHORT: EMA3[i-1] >= lower[i-1]  AND  EMA3[i] < lower[i]
        """
        i = cs.bars_count - 1
        direction = cfg["dir"]

        # Dedup: one signal per bar per combo
        if self.last_signal_bar.get(combo_id) == cs.last_bar_time:
            return None

        triggered = False
        if direction == "LONG":
            if (cs.ema3[i - 1] <= cs.upper[i - 1] and
                    cs.ema3[i] > cs.upper[i]):
                triggered = True
        else:  # SHORT
            if (cs.ema3[i - 1] >= cs.lower[i - 1] and
                    cs.ema3[i] < cs.lower[i]):
                triggered = True

        if not triggered:
            return None

        # Compute SL
        sigma = cs.sigma[i]
        if math.isnan(sigma) or sigma <= 0:
            return None

        sl_dist = cfg["sl_mult"] * sigma
        close_price = cs.close[i]

        if direction == "LONG":
            sl_price = close_price - sl_dist
        else:
            sl_price = close_price + sl_dist

        # TP for A_fixed and B_protect modes
        tp_price = None
        mode = cfg["mode"]
        if mode in ("A_fixed", "B_protect"):
            if direction == "LONG":
                tp_price = close_price + sl_dist  # 1:1 RR
            else:
                tp_price = close_price - sl_dist

        # Timeout
        timeout_bars = max(cfg["P"] // 3, 5)

        # Mark signal
        self.last_signal_bar[combo_id] = cs.last_bar_time

        # Build action
        tf = cfg["tf"]
        P = cfg["P"]
        action = {
            "action": "ENTER",
            "symbol": cfg["sym"],
            "direction": direction,
            "sl_price": round(sl_price, 6),
            "comment": f"mt2_{direction[0]}_{tf}_P{P}_{mode}",
            "signal_data": json.dumps({
                "combo_id": combo_id,
                "mode": mode,
                "size_r": cfg["size_r"],
                "tier": cfg["tier"],
                "role": cfg["role"],
                "timeout_bars": timeout_bars,
                "sigma": round(sigma, 8),
                "sl_mult": cfg["sl_mult"],
            }),
        }

        if tp_price is not None:
            action["tp_price"] = round(tp_price, 6)

        log(f"ENTRY {combo_id}: {direction} {cfg['sym']} @ ~{close_price:.5f}, "
            f"SL={sl_price:.5f}, σ={sigma:.6f}, mode={mode}, "
            f"size_r={cfg['size_r']}, timeout={timeout_bars}bars")

        return action

    # ───────────────────────────────────────────────────────────
    #  Position management (BE, trailing, timeout)
    # ───────────────────────────────────────────────────────────

    def _manage_position(self, cs: ComboState, cfg: dict,
                         pt: PosTrack, pos: dict) -> list:
        """
        Manage open position: BE trigger, trailing SL, timeout.
        Returns list of MODIFY_SL / EXIT actions.
        """
        actions = []
        i = cs.bars_count - 1

        # Count bars since entry
        if cs.last_bar_time > pt.entry_bar_time:
            pt.bars_elapsed += 1

        # Current market price (use close of last bar)
        price = cs.close[i]
        high = cs.high[i]
        low = cs.low[i]

        # Update peak
        if pt.direction == "LONG":
            if high > pt.peak_price:
                pt.peak_price = high
        else:
            if low < pt.peak_price:
                pt.peak_price = low

        # ── Timeout check ──
        if pt.bars_elapsed >= pt.timeout_bars:
            log(f"TIMEOUT {pt.combo_id}: {pt.bars_elapsed} bars, closing")
            actions.append({
                "action": "EXIT",
                "ticket": pt.ticket,
                "symbol": pt.symbol,
                "reason": "timeout",
            })
            return actions

        # ── Mode-specific management ──
        mode = pt.mode
        new_sl = pt.last_sent_sl
        sd = pt.sl_distance
        be_threshold = self.be_trigger_ratio * sd

        if mode == "A_fixed":
            # No trailing. TP/SL handled by broker. Just timeout.
            pass

        elif mode == "B_protect":
            # BE trigger: when profit >= 0.2 × SL_dist → move SL to entry
            if not pt.be_triggered:
                if pt.direction == "LONG":
                    profit = high - pt.entry_price
                else:
                    profit = pt.entry_price - low

                if profit >= be_threshold:
                    pt.be_triggered = True
                    new_sl = pt.entry_price
                    log(f"BE_TRIGGER {pt.combo_id}: SL → entry {new_sl:.5f}")

        elif mode == "C_trail":
            # Phase 1: BE trigger
            if not pt.be_triggered:
                if pt.direction == "LONG":
                    profit = high - pt.entry_price
                else:
                    profit = pt.entry_price - low

                if profit >= be_threshold:
                    pt.be_triggered = True
                    new_sl = pt.entry_price
                    log(f"BE_TRIGGER {pt.combo_id}: SL → entry {new_sl:.5f}")

            # Phase 2: Trail at peak ± 0.5 × sigma
            if pt.be_triggered:
                sigma = cs.sigma[i]
                if not math.isnan(sigma) and sigma > 0:
                    if pt.direction == "LONG":
                        trail_sl = pt.peak_price - 0.5 * sigma
                        # Only ratchet up
                        if trail_sl > new_sl:
                            new_sl = trail_sl
                    else:
                        trail_sl = pt.peak_price + 0.5 * sigma
                        # Only ratchet down
                        if trail_sl < new_sl:
                            new_sl = trail_sl

        # ── Send SL update if changed ──
        # Only send if moved meaningfully (> 1 pip equivalent)
        sl_diff = abs(new_sl - pt.last_sent_sl)
        if sl_diff > sd * 0.01:  # > 1% of SL distance
            pt.last_sent_sl = new_sl
            actions.append({
                "action": "MODIFY_SL",
                "ticket": pt.ticket,
                "symbol": pt.symbol,
                "sl_price": round(new_sl, 6),
            })

        return actions

    # ───────────────────────────────────────────────────────────
    #  Daemon callback: position opened
    # ───────────────────────────────────────────────────────────

    def on_position_opened(self, ticket: int, symbol: str, direction: str,
                           entry_price: float, sl_price: float,
                           signal_data: str):
        """
        Called by daemon after successful order execution.
        Creates PosTrack for exit management.
        """
        try:
            data = json.loads(signal_data) if signal_data else {}
        except json.JSONDecodeError:
            data = {}

        combo_id = data.get("combo_id", f"{symbol}_{direction}")
        mode = data.get("mode", "A_fixed")
        timeout_bars = data.get("timeout_bars", 30)
        sigma = data.get("sigma", 0)

        # Find current bar time from combo state
        cs = self.combo_states.get(combo_id)
        bar_time = cs.last_bar_time if cs else 0

        pt = PosTrack(
            ticket=ticket,
            combo_id=combo_id,
            symbol=symbol,
            direction=direction,
            entry_price=entry_price,
            initial_sl=sl_price,
            mode=mode,
            timeout_bars=timeout_bars,
            entry_bar_time=bar_time,
            sigma_at_entry=sigma,
        )
        self.pos_tracks[ticket] = pt

        log(f"TRACKED {combo_id}: ticket={ticket}, entry={entry_price:.5f}, "
            f"SL={sl_price:.5f}, mode={mode}, timeout={timeout_bars}")
