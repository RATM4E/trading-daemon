#!/usr/bin/env python3
"""
DualThrust Strategy — Daemon Implementation v2
===============================================

Изменения v2:
  - BE протектор: при floating profit ≥ be_trigger_r × SL_dist → MODIFY_SL в breakeven
  - Тиры: T1/T2 из config['symbol_tiers'], передаётся в signal_data для демона
  - Удалён неиспользуемый numpy импорт

Логика:
  1. Из M5 истории ресемплируем D1 → DualThrust range (N дней)
  2. Upper = today_open + K × range;  Lower = today_open − K × range
  3. Пробой Upper → LONG;  пробой Lower → SHORT
  4. max_rev=0 → разворотов нет, только EOD закрытие
  5. BE: когда floating ≥ be_trigger_r × (Upper−Lower) → SL в entry price
  6. EOD: EXIT при first bar >= session_end_hour

Параметры config.json:
  N                 : 5      — дней lookback
  K                 : 0.3    — множитель
  session_start_hour: 9      — начало сессии (server time, часы)
  session_end_hour  : 17     — конец сессии (EOD)
  max_rev           : 0      — макс. разворотов (0 = только EOD)
  be_trigger_r      : 0.41   — BE триггер в R (0 = выключен)
  sl_buffer_mult    : 1.05   — буфер SL за порог (для StopsLevel)
  symbols           : [...]  — список символов
  symbol_tiers      : {...}  — {"AUDNZD": "T1", "AUDCHF": "T2", ...}
                               Демон использует для risk_per_trade

signal_data (передаётся в ENTER, возвращается демоном в каждом TICK):
  combo_key : "DT_{sym}_N{N}_K{K}"
  upper     : float  — Upper порог при входе
  lower     : float  — Lower порог при входе
  sl_dist   : float  — upper − lower (для BE расчёта)
  day_ts    : int    — начало дня
  n_rev     : int    — разворотов к моменту входа
  tier      : str    — "T1" / "T2"
  be_set    : bool   — флаг BE (обновляется стратегией через MODIFY_SL)

Важно: be_set хранится и в signal_data и в _state — они синхронизированы.
После MODIFY_SL демон не обновляет signal_data, поэтому be_set ведём
в _state[sym]['be_set'] и проверяем там.
"""

import json

D1_SEC = 86400


class Strategy:

    def __init__(self, config: dict):
        self.N          = int(config.get('N', 5))
        self.K          = float(config.get('K', 0.3))
        self.sh         = int(config.get('session_start_hour', 9))
        self.eh         = int(config.get('session_end_hour', 17))
        self.mr         = int(config.get('max_rev', 0))
        self.be_trig    = float(config.get('be_trigger_r', 0.41))
        self.sl_buf     = float(config.get('sl_buffer_mult', 1.05))
        self.syms       = list(config.get('symbols', []))
        self.tiers      = dict(config.get('symbol_tiers', {}))
        self.name       = config.get('strategy', 'dualthrust')

        # Per-symbol внутридневное состояние
        # {sym: {day_ts, n_rev, blocked, upper, lower, be_set}}
        self._state: dict = {}

    # ──────────────────────────────────────────────────────────────
    # PROTOCOL
    # ──────────────────────────────────────────────────────────────
    def get_requirements(self) -> dict:
        history = (self.N + 2) * 300   # N+2 дня × 288 M5 баров
        return {
            'symbols':      self.syms,
            'timeframe':    'M5',
            'history_bars': history,
        }

    def save_state(self) -> dict:
        return {'per_sym': self._state}

    def restore_state(self, state: dict):
        self._state = state.get('per_sym', {})

    # ──────────────────────────────────────────────────────────────
    # MAIN TICK
    # ──────────────────────────────────────────────────────────────
    def on_bars(self, bars_data: dict, positions: list) -> list:
        actions = []

        # Индекс открытых позиций по символу
        open_pos = {}
        for p in positions:
            sym = p.get('symbol')
            if sym in self.syms:
                open_pos[sym] = p

        for sym in self.syms:
            bars = bars_data.get(sym)
            if not bars or len(bars) < (self.N + 1) * 200:
                continue
            acts = self._process_symbol(sym, bars, open_pos.get(sym))
            actions.extend(acts)

        return actions

    # ──────────────────────────────────────────────────────────────
    # PER-SYMBOL LOGIC
    # ──────────────────────────────────────────────────────────────
    def _process_symbol(self, sym: str, bars: list, pos) -> list:
        actions = []

        last   = bars[-1]
        ts     = int(last['time'])
        hi     = float(last['high'])
        lo     = float(last['low'])
        cl     = float(last['close'])
        day_ts = (ts // D1_SEC) * D1_SEC
        hour   = (ts % D1_SEC) // 3600

        # ── Смена дня: сброс состояния ────────────────────────────
        st = self._state.get(sym, {})
        if st.get('day_ts') != day_ts:
            if pos is not None:
                # Страховочное закрытие (нормально закрывается через EOD ниже)
                actions.append(self._exit(pos, f"DT {sym} day-change EOD"))
                pos = None
            st = {
                'day_ts':  day_ts,
                'n_rev':   0,
                'blocked': False,
                'upper':   None,
                'lower':   None,
                'be_set':  False,
            }
            self._state[sym] = st

        # ── Пороги ────────────────────────────────────────────────
        upper, lower = self._compute_thresholds(bars, day_ts)
        if upper is None or upper <= lower:
            return actions
        st['upper'] = upper
        st['lower'] = lower
        sl_dist = upper - lower

        # ── EOD ───────────────────────────────────────────────────
        if hour >= self.eh:
            if pos is not None:
                actions.append(self._exit(pos, f"DT {sym} EOD"))
            st['blocked'] = True
            st['be_set']  = False
            return actions

        # ── Вне сессии ────────────────────────────────────────────
        if hour < self.sh:
            return actions

        # ── День заблокирован ────────────────────────────────────
        if st['blocked']:
            return actions

        # ── BE протектор ──────────────────────────────────────────
        if pos is not None and self.be_trig > 0 and not st.get('be_set', False):
            entry_p = float(pos.get('price_open', 0))
            dir_    = pos.get('direction', '')
            if entry_p > 0 and sl_dist > 0:
                if dir_ == 'LONG':
                    floating_r = (cl - entry_p) / sl_dist
                else:  # SHORT
                    floating_r = (entry_p - cl) / sl_dist

                if floating_r >= self.be_trig:
                    # Переставляем SL в entry price (BE)
                    # Для LONG: new_sl = entry_p (чуть ниже чтобы не выбить сразу)
                    # Для SHORT: new_sl = entry_p (чуть выше)
                    # Небольшой буфер 0.1 × sl_dist чтобы не задело на спреде
                    buf = sl_dist * 0.05
                    if dir_ == 'LONG':
                        new_sl = round(entry_p - buf, 6)
                    else:
                        new_sl = round(entry_p + buf, 6)

                    actions.append({
                        'action':  'MODIFY_SL',
                        'ticket':  pos['ticket'],
                        'new_sl':  new_sl,
                        'comment': f"DT {sym} BE @{floating_r:.2f}R",
                    })
                    st['be_set'] = True

        # ── Сигналы входа/разворота ───────────────────────────────
        if lo <= lower:   # SHORT signal
            if pos is not None:
                if pos.get('direction') == 'LONG':
                    actions.append(self._exit(pos, f"DT {sym} reversal→SHORT"))
                    st['n_rev'] += 1
                    st['be_set'] = False
                    if st['n_rev'] > self.mr:
                        st['blocked'] = True
                        return actions
                    actions.append(self._enter(sym, 'SHORT', upper, lower, day_ts, st['n_rev']))
                # уже SHORT — ничего
            elif not st['blocked']:
                actions.append(self._enter(sym, 'SHORT', upper, lower, day_ts, st['n_rev']))
                st['be_set'] = False

        elif hi >= upper:   # LONG signal
            if pos is not None:
                if pos.get('direction') == 'SHORT':
                    actions.append(self._exit(pos, f"DT {sym} reversal→LONG"))
                    st['n_rev'] += 1
                    st['be_set'] = False
                    if st['n_rev'] > self.mr:
                        st['blocked'] = True
                        return actions
                    actions.append(self._enter(sym, 'LONG', upper, lower, day_ts, st['n_rev']))
                # уже LONG — ничего
            elif not st['blocked']:
                actions.append(self._enter(sym, 'LONG', upper, lower, day_ts, st['n_rev']))
                st['be_set'] = False

        return actions

    # ──────────────────────────────────────────────────────────────
    # ПОРОГИ
    # ──────────────────────────────────────────────────────────────
    def _compute_thresholds(self, bars: list, today_day_ts: int):
        """Строит D1 из M5, возвращает (upper, lower) или (None, None)."""
        days = {}
        for b in bars:
            bkt = (int(b['time']) // D1_SEC) * D1_SEC
            if bkt not in days:
                days[bkt] = {
                    'o': float(b['open']),
                    'h': float(b['high']),
                    'l': float(b['low']),
                    'c': float(b['close']),
                }
            else:
                days[bkt]['h'] = max(days[bkt]['h'], float(b['high']))
                days[bkt]['l'] = min(days[bkt]['l'], float(b['low']))
                days[bkt]['c'] = float(b['close'])

        past = sorted((d, v) for d, v in days.items() if d < today_day_ts)
        if len(past) < self.N:
            return None, None

        last_n = past[-self.N:]
        hh  = max(v['h'] for _, v in last_n)
        ll  = min(v['l'] for _, v in last_n)
        hc  = min(v['c'] for _, v in last_n)
        lc  = max(v['c'] for _, v in last_n)
        rng = max(hh - lc, hc - ll)
        if rng <= 0.0:
            return None, None

        today = days.get(today_day_ts)
        if today is None:
            return None, None

        o = today['o']
        return o + self.K * rng, o - self.K * rng

    # ──────────────────────────────────────────────────────────────
    # ACTION BUILDERS
    # ──────────────────────────────────────────────────────────────
    def _enter(self, sym: str, direction: str,
               upper: float, lower: float,
               day_ts: int, n_rev: int) -> dict:
        sl_dist = upper - lower
        tier    = self.tiers.get(sym, 'T2')

        if direction == 'LONG':
            sl_price = lower - sl_dist * (self.sl_buf - 1.0)
            tp_price = upper + sl_dist * 10.0
        else:
            sl_price = upper + sl_dist * (self.sl_buf - 1.0)
            tp_price = lower - sl_dist * 10.0

        signal = json.dumps({
            'combo_key': f"DT_{sym}_N{self.N}_K{self.K}",
            'upper':     round(upper,   6),
            'lower':     round(lower,   6),
            'sl_dist':   round(sl_dist, 6),
            'day_ts':    day_ts,
            'n_rev':     n_rev,
            'tier':      tier,
        }, separators=(',', ':'))

        return {
            'action':      'ENTER',
            'symbol':      sym,
            'direction':   direction,
            'sl_price':    round(sl_price, 6),
            'tp_price':    round(tp_price, 6),
            'signal_data': signal,
            'comment':     f"DT {sym} {direction} {tier}",
        }

    def _exit(self, pos, comment: str) -> dict:
        return {
            'action':  'EXIT',
            'ticket':  pos['ticket'],
            'comment': comment,
        }
