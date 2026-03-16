"""
GoldFish v1.1
=============
Balance-Breakout with VWAP direction filter and SL Protection.

Logic (per symbol, per tick):
  Signal bar [i] = bars[-1] (last closed bar, current tick)

  1. Compute ATR(14) at bar[i-1] (shift=1, no lookahead)
  2. Compute VWAP (cumulative from daily reset at vwap_reset_hour) at bar[i]
  3. Rolling balance: max/min of high/low over bars[i-n_balance .. i-1]
     (excludes bar[i] itself)
  4. VWAP filter: buy_ok  = close[i] > vwap[i] AND b_high > vwap[i]
                  sell_ok = close[i] < vwap[i] AND b_low  < vwap[i]
  5. Ambiguous (both) -> skip
  6. Daily limit: 1 long + 1 short per day per symbol

Entry (market order on next bar open):
  sl_dist = atr * atr_mult  (fill-invariant, used for lot sizing)
  LONG:  sl_price = b_high - sl_dist  (estimate; daemon reanchors to fill)
         tp_price = b_high + rr_tp * sl_dist
  SHORT: sl_price = b_low  + sl_dist
         tp_price = b_low  - rr_tp * sl_dist

  Daemon (Scheduler/BacktestEngine) reanchors SL to actual fill:
    LONG:  sl = fill - sl_dist
    SHORT: sl = fill + sl_dist

SL Protection (one-time move):
  Track MFE per position using bar highs/lows.
  When MFE / init_risk >= protect_trigger_r:
    LONG:  new_sl = entry + protect_new_sl_r * init_risk
    SHORT: new_sl = entry - protect_new_sl_r * init_risk
  Only if new_sl improves current SL.
"""

import json
import math


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


def _compute_atr(bars: list, period: int) -> list:
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
    reset_sec   = reset_hour * 3600
    cum_pv      = 0.0
    cum_v       = 0.0
    prev_bucket = None
    result      = []
    for b in bars:
        bucket = (b['time'] - reset_sec) // 86400
        if prev_bucket is None or bucket != prev_bucket:
            cum_pv = 0.0; cum_v = 0.0; prev_bucket = bucket
        tp      = (b['high'] + b['low'] + b['close']) / 3.0
        vol     = max(b.get('volume', 1), 1)
        cum_pv += tp * vol
        cum_v  += vol
        result.append(cum_pv / cum_v)
    return result


class Strategy:

    def __init__(self, config: dict):
        p      = config.get('params', {})
        combos = config.get('combos', [])

        self._atr_period   = int(p.get('atr_period', 14))
        self._history_bars = int(p.get('history_bars', 100))
        self._r_cap        = p.get('r_cap')

        self._syms:  list[str]             = []
        self._cfg:   dict[str, _SymCfg]   = {}
        self._state: dict[str, _SymState] = {}
        self._tfs:   dict[str, str]        = {}

        for c in combos:
            sym = c['sym']
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

    def get_requirements(self) -> dict:
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
            day = raw[-1]['time'] // 86400
            if day != state.current_day:
                state.daily_long_done  = False
                state.daily_short_done = False
                state.current_day      = day

            # ── Resolve pending entry ────────────────────────────────────────
            if state.pending_entry:
                found = False
                for p in pos_by_sym.get(sym, []):
                    if p['direction'] == state.pending_direction and state.ticket is None:
                        state.ticket      = p['ticket']
                        state.entry_price = float(p['price_open'])
                        state.in_pos      = True
                        # init_risk from actual SL set by daemon (reanchored to fill)
                        actual_sl = float(p['sl'])
                        if state.direction == 'LONG':
                            state.init_risk = state.entry_price - actual_sl
                        else:
                            state.init_risk = actual_sl - state.entry_price
                        if state.init_risk <= 0:
                            state.reset_position()
                        else:
                            state.sl_price      = actual_sl
                            state.pending_entry = False
                        found = True
                        break
                if not found:
                    # ENTER was suppressed or rejected by daemon
                    state.reset_position()
                continue

            # ── Position management ──────────────────────────────────────────
            if state.in_pos and state.ticket is not None:
                pos = pos_by_ticket.get(state.ticket)
                if pos is None:
                    state.reset_position()
                    continue

                # MFE tracking
                if state.direction == 'LONG':
                    mf = raw[-1]['high'] - state.entry_price
                else:
                    mf = state.entry_price - raw[-1]['low']
                if mf > state.max_favorable:
                    state.max_favorable = mf

                # SL Protection (one-time)
                if not state.sl_moved and state.init_risk > 0:
                    if state.max_favorable / state.init_risk >= cfg.protect_trigger_r:
                        if state.direction == 'LONG':
                            new_sl = state.entry_price + cfg.protect_new_sl_r * state.init_risk
                            if new_sl > float(pos['sl']):
                                actions.append({'action': 'MODIFY_SL',
                                                'ticket': state.ticket,
                                                'new_sl': round(new_sl, 8)})
                                state.sl_price = new_sl
                                state.sl_moved = True
                        else:
                            new_sl = state.entry_price - cfg.protect_new_sl_r * state.init_risk
                            if new_sl < float(pos['sl']):
                                actions.append({'action': 'MODIFY_SL',
                                                'ticket': state.ticket,
                                                'new_sl': round(new_sl, 8)})
                                state.sl_price = new_sl
                                state.sl_moved = True
                continue

            # ── Entry signal ─────────────────────────────────────────────────
            # bars[-1] = bar[i], ENTER -> fill at bar[i+1].Open
            min_bars = self._atr_period + cfg.n_balance + 2
            if n < min_bars:
                continue

            signal_idx = n - 1

            atr_list = _compute_atr(raw, self._atr_period)
            atr_i    = atr_list[signal_idx - 1]
            if math.isnan(atr_i) or atr_i <= 0.0:
                continue

            vwap_list = _compute_vwap(raw, cfg.vwap_reset_hour)
            vwap_i    = vwap_list[signal_idx]

            nb = cfg.n_balance
            if signal_idx < nb:
                continue
            b_high = max(raw[j]['high'] for j in range(signal_idx - nb, signal_idx))
            b_low  = min(raw[j]['low']  for j in range(signal_idx - nb, signal_idx))

            cl_i    = raw[signal_idx]['close']
            buy_ok  = (cl_i > vwap_i) and (b_high > vwap_i) and (not state.daily_long_done)
            sell_ok = (cl_i < vwap_i) and (b_low  < vwap_i) and (not state.daily_short_done)

            if not buy_ok and not sell_ok:
                continue
            if buy_ok and sell_ok:
                continue

            # sl_dist is fill-invariant; daemon reanchors sl = fill +/- sl_dist
            sl_dist = atr_i * cfg.atr_mult

            if buy_ok:
                sl_price  = b_high - sl_dist
                tp_price  = b_high + cfg.rr_tp * sl_dist
                direction = 'LONG'
                state.daily_long_done = True
            else:
                sl_price  = b_low + sl_dist
                tp_price  = b_low - cfg.rr_tp * sl_dist
                direction = 'SHORT'
                state.daily_short_done = True

            state.pending_entry     = True
            state.pending_direction = direction
            state.direction         = direction
            state.entry_price       = b_high if buy_ok else b_low
            state.init_risk         = sl_dist
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
                    'sl_dist': round(sl_dist, 8),
                    'tp_r':    cfg.rr_tp,
                    'b_high':  round(b_high, 8),
                    'b_low':   round(b_low, 8),
                    'atr':     round(atr_i, 8),
                }),
            })

        return actions

    def save_state(self) -> dict:
        return {sym: state.to_dict() for sym, state in self._state.items()}

    def restore_state(self, state: dict):
        if not state:
            return
        for sym, s in state.items():
            if sym in self._state:
                self._state[sym].from_dict(s)
