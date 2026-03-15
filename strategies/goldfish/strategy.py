"""
GoldFish v1.0
=============
Balance-Breakout with VWAP direction filter and SL Protection.

Logic (per symbol, per tick):
  Signal bar [i]  = bars[-2] (last closed before current tick)
  Exec bar   [i+1]= bars[-1] (just closed, current tick)

  1. Compute ATR(14) at bar[i-1] (shift=1, no lookahead)
  2. Compute VWAP (cumulative from daily reset at vwap_reset_hour)
     at bar[i]
  3. Rolling balance: max/min of high/low over bars[i-n_balance .. i-1]
     (excludes bar[i] itself)
  4. VWAP filter: buy_ok  = close[i] > vwap[i] AND b_high > vwap[i]
                  sell_ok = close[i] < vwap[i] AND b_low  < vwap[i]
  5. Breakout:   buy_trig  = buy_ok  AND high[i+1] >= b_high
                 sell_trig = sell_ok AND low[i+1]  <= b_low
  6. Ambiguous (both) → skip
  7. Daily limit: 1 long + 1 short per day per symbol

Entry (market order):
  LONG:  entry_est = max(close[i+1], b_high)
         sl = entry_est - atr * atr_mult
         tp = entry_est + rr_tp * (entry_est - sl)
  SHORT: entry_est = min(close[i+1], b_low)
         sl = entry_est + atr * atr_mult
         tp = entry_est - rr_tp * (sl - entry_est)

SL Protection (one-time move):
  Track max_favorable excursion per position.
  When max_favorable / init_risk >= protect_trigger_r:
    LONG:  new_sl = entry_price + protect_new_sl_r * init_risk
    SHORT: new_sl = entry_price - protect_new_sl_r * init_risk
  Only if new_sl improves current SL.
"""

import json
import math


# ── Per-symbol state ────────────────────────────────────────────────────────

class _SymState:
    __slots__ = (
        'pending_entry', 'pending_direction',
        'in_pos', 'ticket', 'direction',
        'entry_price', 'init_risk', 'sl_price', 'tp_price',
        'sl_moved', 'max_favorable',
        'daily_long_done', 'daily_short_done', 'current_day',
    )

    def __init__(self):
        self.pending_entry     = False
        self.pending_direction = None
        self.in_pos            = False
        self.ticket            = None
        self.direction         = None
        self.entry_price       = 0.0
        self.init_risk         = 0.0
        self.sl_price          = 0.0
        self.tp_price          = 0.0
        self.sl_moved          = False
        self.max_favorable     = 0.0
        self.daily_long_done   = False
        self.daily_short_done  = False
        self.current_day       = -1

    def reset_position(self):
        self.pending_entry     = False
        self.pending_direction = None
        self.in_pos            = False
        self.ticket            = None
        self.direction         = None
        self.entry_price       = 0.0
        self.init_risk         = 0.0
        self.sl_price          = 0.0
        self.tp_price          = 0.0
        self.sl_moved          = False
        self.max_favorable     = 0.0

    def to_dict(self):
        return {k: getattr(self, k) for k in self.__slots__}

    def from_dict(self, d):
        for k in self.__slots__:
            if k in d:
                setattr(self, k, d[k])


# ── Per-symbol config ────────────────────────────────────────────────────────

class _SymCfg:
    __slots__ = (
        'tf', 'vwap_reset_hour', 'n_balance',
        'atr_mult', 'rr_tp',
        'protect_trigger_r', 'protect_new_sl_r',
    )

    def __init__(self, strat: dict):
        self.tf                = strat['tf']
        self.vwap_reset_hour   = int(strat['vwap_reset_hour'])
        self.n_balance         = int(strat['n_balance'])
        self.atr_mult          = float(strat['atr_mult'])
        self.rr_tp             = float(strat['rr_tp'])
        self.protect_trigger_r = float(strat['protect_trigger_r'])
        self.protect_new_sl_r  = float(strat['protect_new_sl_r'])


# ── Indicators (pure Python, no numpy) ──────────────────────────────────────

def _compute_atr(bars: list, period: int) -> list:
    """
    Wilder ATR. atr[k] = ATR computed up to and including bar k.
    Returns list of floats (nan for first `period` elements).
    """
    n = len(bars)
    atr = [math.nan] * n
    if n < period + 1:
        return atr

    s = 0.0
    for i in range(1, period + 1):
        hi = bars[i]['high']; lo = bars[i]['low']; pc = bars[i - 1]['close']
        tr = max(hi - lo, abs(hi - pc), abs(lo - pc))
        s += tr
    atr[period] = s / period

    for i in range(period + 1, n):
        hi = bars[i]['high']; lo = bars[i]['low']; pc = bars[i - 1]['close']
        tr = max(hi - lo, abs(hi - pc), abs(lo - pc))
        atr[i] = (atr[i - 1] * (period - 1) + tr) / period

    return atr


def _compute_vwap(bars: list, reset_hour: int) -> list:
    """
    Cumulative VWAP with daily reset.
    reset_hour: hour in dataset timestamp coordinates (treated as UTC).
    vwap[k] = VWAP at close of bar k (no lookahead).
    """
    reset_sec = reset_hour * 3600
    cum_pv    = 0.0
    cum_v     = 0.0
    prev_bucket = None
    result = []

    for b in bars:
        bucket = (b['time'] - reset_sec) // 86400
        if prev_bucket is None or bucket != prev_bucket:
            cum_pv      = 0.0
            cum_v       = 0.0
            prev_bucket = bucket
        tp     = (b['high'] + b['low'] + b['close']) / 3.0
        vol    = max(b.get('volume', 1), 1)
        cum_pv += tp * vol
        cum_v  += vol
        result.append(cum_pv / cum_v)

    return result


# ── Main Strategy ─────────────────────────────────────────────────────────────

class Strategy:

    def __init__(self, config: dict):
        p      = config.get('params', {})
        combos = config.get('combos', [])

        self._atr_period  = int(p.get('atr_period', 14))
        self._history_bars = int(p.get('history_bars', 100))
        self._r_cap        = p.get('r_cap')

        self._syms: list[str]          = []
        self._cfg:  dict[str, _SymCfg] = {}
        self._state: dict[str, _SymState] = {}
        self._tfs:  dict[str, str]     = {}

        for c in combos:
            sym = c['sym']
            # Take first direction's strat block
            dirs = c.get('directions', {})
            strat_block = None
            for dk in ('BOTH', 'LONG', 'SHORT'):
                if dk in dirs:
                    strat_block = dirs[dk].get('strat', {})
                    break
            if strat_block is None:
                continue

            self._syms.append(sym)
            self._cfg[sym]   = _SymCfg(strat_block)
            self._state[sym] = _SymState()
            self._tfs[sym]   = strat_block.get('tf', 'M30')

    # ── Protocol ──────────────────────────────────────────────────────────────

    def get_requirements(self) -> dict:
        # Minimum bars needed: ATR_PERIOD + n_balance + 3 (warmup)
        max_nb = max((self._cfg[s].n_balance for s in self._syms), default=20)
        h = max(self._history_bars, self._atr_period + max_nb + 10)
        return {
            'symbols':      self._syms,
            'timeframes':   self._tfs,
            'history_bars': h,
            'r_cap':        self._r_cap,
        }

    def on_bars(self, bars: dict, positions: list) -> list:
        actions = []

        # Build ticket → symbol map for quick lookup
        pos_by_ticket = {p['ticket']: p for p in (positions or [])}
        pos_by_sym    = {}
        for p in (positions or []):
            pos_by_sym.setdefault(p['symbol'], []).append(p)

        for sym in self._syms:
            raw = bars.get(sym)
            if not raw:
                continue

            cfg   = self._cfg[sym]
            state = self._state[sym]
            n     = len(raw)

            # ── Daily reset ──────────────────────────────────────────────────
            last_ts = raw[-1]['time']
            day     = last_ts // 86400
            if day != state.current_day:
                state.daily_long_done  = False
                state.daily_short_done = False
                state.current_day      = day

            # ── Resolve pending entry (waiting for ticket) ───────────────────
            if state.pending_entry:
                sym_positions = pos_by_sym.get(sym, [])
                for p in sym_positions:
                    if p['direction'] == state.pending_direction and state.ticket is None:
                        state.ticket     = p['ticket']
                        state.entry_price = float(p['price_open'])
                        state.in_pos     = True
                        # Recompute init_risk from actual entry
                        if state.direction == 'LONG':
                            state.init_risk = state.entry_price - state.sl_price
                        else:
                            state.init_risk = state.sl_price - state.entry_price
                        if state.init_risk <= 0:
                            state.reset_position()
                        else:
                            state.pending_entry = False
                        break

            # ── Position management ──────────────────────────────────────────
            if state.in_pos and state.ticket is not None:
                pos = pos_by_ticket.get(state.ticket)

                if pos is None:
                    # Position closed by daemon (SL/TP hit)
                    state.reset_position()
                    continue

                # Update max favorable excursion
                if state.direction == 'LONG':
                    mf = raw[-1]['high'] - state.entry_price
                else:
                    mf = state.entry_price - raw[-1]['low']
                if mf > state.max_favorable:
                    state.max_favorable = mf

                # SL Protection (one-time)
                if not state.sl_moved and state.init_risk > 0:
                    mf_r = state.max_favorable / state.init_risk
                    if mf_r >= cfg.protect_trigger_r:
                        if state.direction == 'LONG':
                            new_sl = state.entry_price + cfg.protect_new_sl_r * state.init_risk
                            cur_sl = float(pos['sl'])
                            if new_sl > cur_sl:
                                actions.append({
                                    'action': 'MODIFY_SL',
                                    'ticket': state.ticket,
                                    'new_sl': round(new_sl, 8),
                                })
                                state.sl_price = new_sl
                                state.sl_moved = True
                        else:
                            new_sl = state.entry_price - cfg.protect_new_sl_r * state.init_risk
                            cur_sl = float(pos['sl'])
                            if new_sl < cur_sl:
                                actions.append({
                                    'action': 'MODIFY_SL',
                                    'ticket': state.ticket,
                                    'new_sl': round(new_sl, 8),
                                })
                                state.sl_price = new_sl
                                state.sl_moved = True
                continue

            # Skip if still waiting for ticket
            if state.pending_entry:
                continue

            # ── Entry signal ─────────────────────────────────────────────────
            # Need: n_balance bars before signal bar + ATR warmup + exec bar
            min_bars = cfg.n_balance + self._atr_period + 3
            if n < min_bars:
                continue

            signal_idx = n - 2  # bar[i]
            exec_idx   = n - 1  # bar[i+1]

            # ATR (shift=1): use ATR at bar[signal_idx - 1]
            atr_list = _compute_atr(raw, self._atr_period)
            atr_i    = atr_list[signal_idx - 1]
            if math.isnan(atr_i) or atr_i <= 0.0:
                continue

            # VWAP at signal bar
            vwap_list = _compute_vwap(raw, cfg.vwap_reset_hour)
            vwap_i    = vwap_list[signal_idx]

            # Rolling balance: bars[signal_idx - n_balance .. signal_idx - 1]
            nb = cfg.n_balance
            if signal_idx < nb:
                continue
            b_high = max(raw[j]['high'] for j in range(signal_idx - nb, signal_idx))
            b_low  = min(raw[j]['low']  for j in range(signal_idx - nb, signal_idx))

            # VWAP direction filter
            cl_i    = raw[signal_idx]['close']
            buy_ok  = (cl_i > vwap_i) and (b_high > vwap_i) and (not state.daily_long_done)
            sell_ok = (cl_i < vwap_i) and (b_low  < vwap_i) and (not state.daily_short_done)

            if not buy_ok and not sell_ok:
                continue

            # Breakout on execution bar
            exec_bar  = raw[exec_idx]
            buy_trig  = buy_ok  and (exec_bar['high'] >= b_high)
            sell_trig = sell_ok and (exec_bar['low']  <= b_low)

            # Ambiguous
            if buy_trig and sell_trig:
                continue

            if not buy_trig and not sell_trig:
                continue

            # Compute levels
            # entry_est = approximate fill (close of exec bar or breakout level)
            if buy_trig:
                entry_est = max(exec_bar['close'], b_high)
                sl_price  = entry_est - atr_i * cfg.atr_mult
                init_risk = entry_est - sl_price
                if init_risk <= 0.0:
                    continue
                tp_price  = entry_est + cfg.rr_tp * init_risk
                direction = 'LONG'
                state.daily_long_done = True
            else:
                entry_est = min(exec_bar['close'], b_low)
                sl_price  = entry_est + atr_i * cfg.atr_mult
                init_risk = sl_price - entry_est
                if init_risk <= 0.0:
                    continue
                tp_price  = entry_est - cfg.rr_tp * init_risk
                direction = 'SHORT'
                state.daily_short_done = True

            # Save state (tentative — actual entry_price updated when ticket arrives)
            state.pending_entry     = True
            state.pending_direction = direction
            state.direction         = direction
            state.entry_price       = entry_est
            state.init_risk         = init_risk
            state.sl_price          = sl_price
            state.tp_price          = tp_price
            state.sl_moved          = False
            state.max_favorable     = 0.0
            state.ticket            = None

            actions.append({
                'action':      'ENTER',
                'symbol':      sym,
                'direction':   direction,
                'sl_price':    round(sl_price, 8),
                'tp_price':    round(tp_price, 8),
                'comment':     f'gf_{direction.lower()}',
                'signal_data': json.dumps({
                    'sl_dist':  round(init_risk, 8),
                    'tp_r':     cfg.rr_tp,
                    'b_high':   round(b_high, 8),
                    'b_low':    round(b_low, 8),
                    'atr':      round(atr_i, 8),
                }),
            })

        return actions

    # ── State persistence ─────────────────────────────────────────────────────

    def save_state(self) -> dict:
        return {
            sym: state.to_dict()
            for sym, state in self._state.items()
        }

    def restore_state(self, state: dict):
        if not state:
            return
        for sym, s in state.items():
            if sym in self._state:
                self._state[sym].from_dict(s)
