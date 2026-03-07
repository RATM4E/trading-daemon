"""
SQUEEZE_ZZ_ATR — strategy_core.py
Чистая логика стратегии: ZigZag, ATR, структура, OCO, позиция.
Daemon-совместимый Python (без numba). Портируется в strategy.py напрямую.

Использование в research: импортируется как reference-реализация.
Использование в daemon: strategy.py наследует/копирует SqueezeZZATRState.
"""

import math
from dataclasses import dataclass, field
from typing import Optional, List, Tuple
import numpy as np

# ─────────────────────────────────────────────────────────────────────────
# КОНСТАНТЫ И СТРУКТУРЫ
# ─────────────────────────────────────────────────────────────────────────
TF_SECONDS    = {"M30": 1800, "H1": 3600, "H4": 14400}
BARS_PER_HOUR = {"M30": 2.0,  "H1": 1.0,  "H4": 0.25}

# Коды структур
S_NONE     = 0
S_TRI_SYM  = 1
S_TRI_DESC = 2
S_TRI_ASC  = 3
S_CHANNEL  = 4
S_CHAOS    = 5
STR_NAMES  = {0:"NONE", 1:"TRI_SYM", 2:"TRI_DESC", 3:"TRI_ASC", 4:"CHANNEL", 5:"CHAOS"}

# Коды выхода из позиции
EXIT_TP      = "TP"
EXIT_SL      = "SL"
EXIT_TRAIL   = "TRAIL"
EXIT_TIMEOUT = "TIMEOUT"

# No-trade window (UTC): 21:40–23:05
NTW_START = 21 * 3600 + 40 * 60
NTW_END   = 23 * 3600 +  5 * 60


@dataclass
class Bar:
    ts:    int     # epoch UTC
    open:  float
    high:  float
    low:   float
    close: float


@dataclass
class OCOOrder:
    """Пара стоп-ордеров, ожидающих исполнения."""
    buy_stop:   float        # уровень BUY_STOP  (sq_hi)
    sell_stop:  float        # уровень SELL_STOP (sq_lo)
    sl_long:    float        # SL для long позиции
    sl_short:   float        # SL для short позиции
    sl_dist_long:  float     # расстояние SL для long (в price units)
    sl_dist_short: float
    atr_signal: float
    placed_bar: int          # индекс бара выставления
    attempt:    int


@dataclass
class Position:
    direction:  int          # +1 long, -1 short
    entry_price: float
    sl:         float
    tp:         float
    sl_dist_orig: float      # оригинальный SL dist (для R-расчёта)
    entry_bar:  int
    peak_price: float        # лучшая цена с момента входа
    trail_active: bool = False


@dataclass
class Trade:
    symbol:     str
    tf:         str
    direction:  int
    entry_bar:  int
    exit_bar:   int
    entry_ts:   int
    exit_ts:    int
    entry_price: float
    exit_price: float
    sl_dist:    float        # оригинальный
    r_pnl:      float
    exit_reason: str
    attempt:    int
    structure:  int
    atr_signal: float


# ─────────────────────────────────────────────────────────────────────────
# ZIGZAG (порт MQ5 ZigzagColor)
# ─────────────────────────────────────────────────────────────────────────
def compute_zigzag(high: np.ndarray, low: np.ndarray,
                   depth: int, deviation_pts: float, backstep: int
                   ) -> Tuple[np.ndarray, np.ndarray]:
    """
    Порт ZigzagColor.mq5. deviation_pts в ценовых единицах (deviation * point_size).
    Возвращает (zz_peak, zz_bottom) — ненулевые в подтверждённых пивотах.
    """
    n = len(high)
    high_map = np.zeros(n)
    low_map  = np.zeros(n)
    zz_peak   = np.zeros(n)
    zz_bottom = np.zeros(n)
    if n < depth:
        return zz_peak, zz_bottom

    last_hi = 0.0
    last_lo = 0.0

    # Проход 1: high_map / low_map
    for shift in range(depth - 1, n):
        beg = max(0, shift - depth + 1)

        # LOW
        lo_val = min(low[beg:shift+1])
        if lo_val == last_lo:
            lo_val = 0.0
        else:
            last_lo = lo_val
            if (low[shift] - lo_val) > deviation_pts:
                lo_val = 0.0
            else:
                for back in range(backstep, 0, -1):
                    p = shift - back
                    if p >= 0 and low_map[p] != 0.0 and low_map[p] > lo_val:
                        low_map[p] = 0.0
        if lo_val != 0.0 and low[shift] == lo_val:
            low_map[shift] = lo_val

        # HIGH
        hi_val = max(high[beg:shift+1])
        if hi_val == last_hi:
            hi_val = 0.0
        else:
            last_hi = hi_val
            if (hi_val - high[shift]) > deviation_pts:
                hi_val = 0.0
            else:
                for back in range(backstep, 0, -1):
                    p = shift - back
                    if p >= 0 and high_map[p] != 0.0 and high_map[p] < hi_val:
                        high_map[p] = 0.0
        if hi_val != 0.0 and high[shift] == hi_val:
            high_map[shift] = hi_val

    # Проход 2: state machine (Peak/Bottom)
    state = 0  # 0=init, 1=looking_for_peak, -1=looking_for_bottom
    cur_hi = 0.0; cur_hi_pos = 0
    cur_lo = 0.0; cur_lo_pos = 0

    for shift in range(depth - 1, n):
        if state == 0:
            if high_map[shift] != 0.0:
                cur_hi = high[shift]; cur_hi_pos = shift
                zz_peak[shift] = cur_hi; state = -1
            if low_map[shift] != 0.0:
                cur_lo = low[shift]; cur_lo_pos = shift
                zz_bottom[shift] = cur_lo; state = 1
        elif state == 1:   # после bottom — ищем peak
            if low_map[shift] != 0.0 and low_map[shift] < cur_lo and high_map[shift] == 0.0:
                zz_bottom[cur_lo_pos] = 0.0
                cur_lo = low_map[shift]; cur_lo_pos = shift
                zz_bottom[shift] = cur_lo
            if high_map[shift] != 0.0 and low_map[shift] == 0.0:
                cur_hi = high_map[shift]; cur_hi_pos = shift
                zz_peak[shift] = cur_hi; state = -1
        else:              # state == -1, после peak — ищем bottom
            if high_map[shift] != 0.0 and high_map[shift] > cur_hi and low_map[shift] == 0.0:
                zz_peak[cur_hi_pos] = 0.0
                cur_hi = high_map[shift]; cur_hi_pos = shift
                zz_peak[shift] = cur_hi
            if low_map[shift] != 0.0 and high_map[shift] == 0.0:
                cur_lo = low_map[shift]; cur_lo_pos = shift
                zz_bottom[shift] = cur_lo; state = 1

    return zz_peak, zz_bottom


def extract_pivots(zz_peak: np.ndarray, zz_bottom: np.ndarray
                   ) -> Tuple[np.ndarray, np.ndarray, np.ndarray]:
    """Возвращает (p_idx, p_val, p_type) в хронологическом порядке.
    p_type: +1 = peak, -1 = trough."""
    idxs, vals, types = [], [], []
    for i in range(len(zz_peak)):
        if zz_peak[i] != 0.0:
            idxs.append(i); vals.append(zz_peak[i]); types.append(1)
        elif zz_bottom[i] != 0.0:
            idxs.append(i); vals.append(zz_bottom[i]); types.append(-1)
    return (np.array(idxs, dtype=np.int64),
            np.array(vals,  dtype=np.float64),
            np.array(types, dtype=np.int8))


# ─────────────────────────────────────────────────────────────────────────
# ATR
# ─────────────────────────────────────────────────────────────────────────
def compute_atr(high: np.ndarray, low: np.ndarray, close: np.ndarray,
                period: int) -> np.ndarray:
    n = len(close)
    atr = np.full(n, np.nan)
    if n < period + 2:
        return atr
    tr = np.empty(n)
    tr[0] = high[0] - low[0]
    for i in range(1, n):
        tr[i] = max(high[i]-low[i], abs(high[i]-close[i-1]), abs(low[i]-close[i-1]))
    s = sum(tr[1:period+1])
    atr[period] = s / period
    alpha = 2.0 / (period + 1.0)
    for i in range(period + 1, n):
        atr[i] = atr[i-1] * (1 - alpha) + tr[i] * alpha
    return atr


# ─────────────────────────────────────────────────────────────────────────
# КЛАССИФИКАЦИЯ СТРУКТУРЫ
# ─────────────────────────────────────────────────────────────────────────
def classify_structure(H1, t_H1, H2, t_H2, L1, t_L1, L2, t_L2,
                        atr_val: float, slope_flat_frac: float,
                        ratio_thresh: float, t_cur: int) -> Tuple[int, float, float, float, float]:
    """
    Классифицирует структуру ZigZag.
    Возвращает (structure_code, slope_H, slope_L, upper_cur, lower_cur).
    flat_thresh = slope_flat_frac * atr_val  (per bar, без деления на lb)
    """
    dH = t_H1 - t_H2
    dL = t_L1 - t_L2
    if dH == 0 or dL == 0:
        return S_NONE, 0.0, 0.0, 0.0, 0.0

    slope_H = (H1 - H2) / dH
    slope_L = (L1 - L2) / dL

    upper_cur = H1 + slope_H * (t_cur - t_H1)
    lower_cur = L1 + slope_L * (t_cur - t_L1)

    flat_thresh = slope_flat_frac * atr_val   # ← ПРАВИЛЬНО: без / lb_bars

    h_flat = abs(slope_H) < flat_thresh
    l_flat = abs(slope_L) < flat_thresh

    if slope_H < -flat_thresh and slope_L > flat_thresh:
        structure = S_TRI_SYM
    elif slope_H < -flat_thresh and l_flat:
        structure = S_TRI_DESC
    elif h_flat and slope_L > flat_thresh:
        structure = S_TRI_ASC
    elif h_flat and l_flat:
        structure = S_CHANNEL
    else:
        structure = S_CHAOS

    # Проверка сужения для треугольников
    if structure in (S_TRI_SYM, S_TRI_DESC, S_TRI_ASC):
        t0 = min(t_H2, t_L2)
        upper_t0 = H1 + slope_H * (t0 - t_H1)
        lower_t0 = L1 + slope_L * (t0 - t_L1)
        upper_t  = upper_cur
        lower_t  = lower_cur
        w0 = upper_t0 - lower_t0
        wt = upper_t  - lower_t
        if w0 > 0.0 and (wt / w0) > ratio_thresh:
            structure = S_CHAOS   # не сузился достаточно

    return structure, slope_H, slope_L, upper_cur, lower_cur


# ─────────────────────────────────────────────────────────────────────────
# STATEFUL СТРАТЕГИЯ (daemon-совместимая)
# ─────────────────────────────────────────────────────────────────────────
class SqueezeZZATRState:
    """
    Stateful объект стратегии. Получает бары один за одним, генерирует сигналы.
    Параметры передаются при инициализации.
    Использование в daemon: создаётся при старте, on_bar() вызывается на каждый тик.
    """

    def __init__(self,
                 symbol:          str,
                 tf:              str,
                 atr_period:      int   = 14,
                 lb_hours:        float = 20,
                 dp:              float = 0.40,
                 zz_depth:        int   = 9,
                 zz_deviation:    int   = 5,    # в MT5 points (умножается на point_size снаружи)
                 zz_backstep:     int   = 3,
                 slope_flat_frac: float = 0.20,
                 ratio_thresh:    float = 0.85,
                 noise_inside:    float = 0.10,
                 sl_buffer:       float = 0.0,  # в price units
                 sl_cap_atr:      float = 3.0,
                 tmo_order:       int   = 20,   # баров
                 tmo_position:    int   = 60,
                 max_attempts:    int   = 3,
                 trail_mult:      float = 0.5,
                 trail_act_r:     float = 1.0,
                 cost:            float = 0.0,  # flat cost в price units
                 point_size:      float = 0.00001,
                 ):
        self.symbol          = symbol
        self.tf              = tf
        self.atr_period      = atr_period
        self.lb_bars         = round(lb_hours * BARS_PER_HOUR[tf])
        self.dp              = dp
        self.zz_depth        = zz_depth
        self.zz_deviation    = zz_deviation
        self.zz_backstep     = zz_backstep
        self.slope_flat_frac = slope_flat_frac
        self.ratio_thresh    = ratio_thresh
        self.noise_inside    = noise_inside
        self.sl_buffer       = sl_buffer
        self.sl_cap_atr      = sl_cap_atr
        self.tmo_order       = tmo_order
        self.tmo_position    = tmo_position
        self.max_attempts    = max_attempts
        self.trail_mult      = trail_mult
        self.trail_act_r     = trail_act_r
        self.cost            = cost
        self.dev_pts         = zz_deviation * point_size

        # История баров (rolling buffer для индикаторов)
        self._bars_h:  List[float] = []
        self._bars_l:  List[float] = []
        self._bars_c:  List[float] = []
        self._bars_ts: List[int]   = []
        self._bar_idx: int = -1

        # Состояние
        self.active_oco:  Optional[OCOOrder] = None
        self.active_pos:  Optional[Position] = None
        self.zone_attempt: int = 0
        self.zone_atr_start: float = 0.0
        self.trades: List[Trade] = []

        # Кэш ZZ (пересчитывается на каждом баре по всему буферу)
        self._last_zz_bar = -1
        self._p_idx = np.array([], dtype=np.int64)
        self._p_val = np.array([], dtype=np.float64)
        self._p_type = np.array([], dtype=np.int8)

    def _in_notrade(self, ts: int) -> bool:
        sod = ts % 86400
        return NTW_START <= sod <= NTW_END

    def _get_atr(self) -> float:
        h = np.array(self._bars_h)
        l = np.array(self._bars_l)
        c = np.array(self._bars_c)
        atr_arr = compute_atr(h, l, c, self.atr_period)
        return float(atr_arr[-1]) if not np.isnan(atr_arr[-1]) else 0.0

    def _get_atr_at(self, idx_back: int) -> float:
        """ATR на idx_back баров назад."""
        h = np.array(self._bars_h)
        l = np.array(self._bars_l)
        c = np.array(self._bars_c)
        atr_arr = compute_atr(h, l, c, self.atr_period)
        i = len(atr_arr) - 1 - idx_back
        return float(atr_arr[i]) if i >= 0 and not np.isnan(atr_arr[i]) else 0.0

    def _update_zz(self):
        h = np.array(self._bars_h)
        l = np.array(self._bars_l)
        zp, zb = compute_zigzag(h, l, self.zz_depth, self.dev_pts, self.zz_backstep)
        self._p_idx, self._p_val, self._p_type = extract_pivots(zp, zb)

    def _get_confirmed_pivots(self):
        """Подтверждённые пивоты: все кроме последней незакрытой ноги."""
        n = len(self._p_idx)
        if n < 2:
            return None
        # Последний пивот p_idx[n-1] — открытая нога (ещё не подтверждена)
        # Подтверждённые: p_idx[0..n-2]
        conf_n = n - 1
        H1 = H2 = L1 = L2 = None
        t_H1 = t_H2 = t_L1 = t_L2 = -1
        for k in range(conf_n - 1, -1, -1):
            pt = self._p_type[k]
            if pt == 1:
                if t_H1 == -1: H1 = self._p_val[k]; t_H1 = int(self._p_idx[k])
                elif t_H2 == -1: H2 = self._p_val[k]; t_H2 = int(self._p_idx[k])
            else:
                if t_L1 == -1: L1 = self._p_val[k]; t_L1 = int(self._p_idx[k])
                elif t_L2 == -1: L2 = self._p_val[k]; t_L2 = int(self._p_idx[k])
            if t_H2 != -1 and t_L2 != -1:
                break
        if None in (H1, H2, L1, L2):
            return None
        return H1, t_H1, H2, t_H2, L1, t_L1, L2, t_L2

    def on_bar(self, bar: Bar) -> Optional[dict]:
        """
        Обработка нового закрытого бара.
        Возвращает dict с действием (или None).
        Действия: ENTER_BUY_STOP, ENTER_SELL_STOP, CANCEL_OCO, EXIT_POSITION
        """
        self._bar_idx += 1
        self._bars_h.append(bar.high)
        self._bars_l.append(bar.low)
        self._bars_c.append(bar.close)
        self._bars_ts.append(bar.ts)

        n = len(self._bars_c)
        if n < self.atr_period + self.lb_bars + 10:
            return None

        # Пересчёт ZZ каждый бар
        self._update_zz()

        atr_cur = self._get_atr()
        atr_lb  = self._get_atr_at(self.lb_bars)
        if atr_cur <= 0 or atr_lb <= 0:
            return None

        # ── 1. Обработка открытой позиции ─────────────────────────────
        if self.active_pos is not None:
            pos = self.active_pos
            action = self._manage_position(pos, bar, atr_cur)
            if action is not None:
                return action

        # ── 2. Обработка активного OCO ────────────────────────────────
        if self.active_oco is not None:
            oco = self.active_oco
            bars_waiting = self._bar_idx - oco.placed_bar
            if bars_waiting >= self.tmo_order:
                self.active_oco = None
                return {"action": "CANCEL_OCO", "reason": "timeout"}

            # Проверка триггера
            filled = self._check_oco_fill(oco, bar, atr_cur)
            if filled is not None:
                return filled
            return None

        # ── 3. Поиск нового сигнала (no position, no OCO) ─────────────
        if self.active_pos is not None or self.active_oco is not None:
            return None
        if self._in_notrade(bar.ts):
            return None

        # ATR-компрессия
        atr_drop = (atr_cur - atr_lb) / atr_lb
        if atr_drop >= -self.dp:
            return None

        # Пивоты и структура
        pivots = self._get_confirmed_pivots()
        if pivots is None:
            return None
        H1, t_H1, H2, t_H2, L1, t_L1, L2, t_L2 = pivots

        t_cur = n - 1
        structure, slope_H, slope_L, upper_cur, lower_cur = classify_structure(
            H1, t_H1, H2, t_H2, L1, t_L1, L2, t_L2,
            atr_cur, self.slope_flat_frac, self.ratio_thresh, t_cur
        )

        if structure not in (S_TRI_SYM, S_TRI_DESC, S_TRI_ASC, S_CHAOS):
            return None

        # Детекция пробоя
        breakout_up   = bar.close > upper_cur
        breakout_down = bar.close < lower_cur

        if not breakout_up and not breakout_down:
            return None

        # Проверка смены зоны (ATR вырос → новый цикл)
        if self.zone_atr_start > 0 and atr_cur > self.zone_atr_start:
            self.zone_attempt = 0

        if self.zone_attempt >= self.max_attempts:
            return None

        # Структурный SL
        sl_long  = L1 - self.sl_buffer
        sl_short = H1 + self.sl_buffer

        sl_dist_long  = (upper_cur + self.cost) - sl_long   # entry = sq_hi + spread ~ sq_hi + cost
        sl_dist_short = sl_short - (lower_cur - self.cost)

        # Cap
        sl_max = self.sl_cap_atr * atr_cur
        if sl_dist_long > sl_max:
            sl_long       = upper_cur + self.cost - sl_max
            sl_dist_long  = sl_max
        if sl_dist_short > sl_max:
            sl_short      = lower_cur - self.cost + sl_max
            sl_dist_short = sl_max

        self.zone_attempt    += 1
        self.zone_atr_start   = atr_lb  # запоминаем ATR начала компрессии

        # Формируем OCO
        self.active_oco = OCOOrder(
            buy_stop   = upper_cur,
            sell_stop  = lower_cur,
            sl_long    = sl_long,
            sl_short   = sl_short,
            sl_dist_long  = sl_dist_long,
            sl_dist_short = sl_dist_short,
            atr_signal = atr_cur,
            placed_bar = self._bar_idx,
            attempt    = self.zone_attempt,
        )

        return {
            "action":    "PLACE_OCO",
            "buy_stop":  upper_cur,
            "sell_stop": lower_cur,
            "sl_long":   sl_long,
            "sl_short":  sl_short,
            "structure": structure,
        }

    def _check_oco_fill(self, oco: OCOOrder, bar: Bar, atr_cur: float) -> Optional[dict]:
        filled_long  = bar.high >= oco.buy_stop
        filled_short = bar.low  <= oco.sell_stop

        if filled_long and filled_short:
            # Одновременный триггер — conservative: SL priority
            if bar.open < oco.buy_stop:
                filled_long = True; filled_short = False
            elif bar.open > oco.sell_stop:
                filled_short = True; filled_long = False
            else:
                filled_long = True; filled_short = False  # default

        if filled_long:
            entry = oco.buy_stop + self.cost   # flat cost на вход
            tp    = entry + oco.sl_dist_long
            self.active_pos = Position(
                direction=1, entry_price=entry,
                sl=oco.sl_long, tp=tp,
                sl_dist_orig=oco.sl_dist_long,
                entry_bar=self._bar_idx,
                peak_price=entry,
            )
            self.active_oco = None
            return {"action": "FILLED_LONG", "entry": entry, "sl": oco.sl_long, "tp": tp}

        if filled_short:
            entry = oco.sell_stop - self.cost
            tp    = entry - oco.sl_dist_short
            self.active_pos = Position(
                direction=-1, entry_price=entry,
                sl=oco.sl_short, tp=tp,
                sl_dist_orig=oco.sl_dist_short,
                entry_bar=self._bar_idx,
                peak_price=entry,
            )
            self.active_oco = None
            return {"action": "FILLED_SHORT", "entry": entry, "sl": oco.sl_short, "tp": tp}

        return None

    def _manage_position(self, pos: Position, bar: Bar, atr_cur: float) -> Optional[dict]:
        direction  = pos.direction
        bars_held  = self._bar_idx - pos.entry_bar

        # SL/TP check (conservative: SL первым)
        if direction == 1:
            sl_hit = bar.low  <= pos.sl
            tp_hit = bar.high >= pos.tp
        else:
            sl_hit = bar.high >= pos.sl
            tp_hit = bar.low  <= pos.tp

        if sl_hit and tp_hit:
            if direction == 1:
                sl_hit = bar.open <= pos.sl or True  # conservative
                tp_hit = False
            else:
                sl_hit = True; tp_hit = False

        if sl_hit:
            exit_p = pos.sl - self.cost * direction
            r = (exit_p - pos.entry_price) * direction / pos.sl_dist_orig
            self._close_position(bar, exit_p, r, EXIT_SL)
            return {"action": "EXIT", "reason": EXIT_SL, "r": r}

        if tp_hit:
            exit_p = pos.tp
            r = (exit_p - pos.entry_price) * direction / pos.sl_dist_orig
            self._close_position(bar, exit_p, r, EXIT_TP)
            return {"action": "EXIT", "reason": EXIT_TP, "r": r}

        # Trail
        if direction == 1 and bar.high > pos.peak_price:
            pos.peak_price = bar.high
        elif direction == -1 and bar.low < pos.peak_price:
            pos.peak_price = bar.low

        mfe = abs(pos.peak_price - pos.entry_price)
        if mfe >= self.trail_act_r * pos.sl_dist_orig:
            pos.trail_active = True

        if pos.trail_active:
            if direction == 1:
                new_sl = pos.peak_price - self.trail_mult * pos.sl_dist_orig
                if new_sl > pos.sl: pos.sl = new_sl
            else:
                new_sl = pos.peak_price + self.trail_mult * pos.sl_dist_orig
                if new_sl < pos.sl: pos.sl = new_sl

        # Timeout
        if bars_held >= self.tmo_position:
            exit_p = bar.close - self.cost * direction
            r = (exit_p - pos.entry_price) * direction / pos.sl_dist_orig
            self._close_position(bar, exit_p, r, EXIT_TIMEOUT)
            return {"action": "EXIT", "reason": EXIT_TIMEOUT, "r": r}

        return None

    def _close_position(self, bar: Bar, exit_price: float, r: float, reason: str):
        if self.active_pos is None:
            return
        pos = self.active_pos
        self.trades.append(Trade(
            symbol=self.symbol, tf=self.tf,
            direction=pos.direction,
            entry_bar=pos.entry_bar, exit_bar=self._bar_idx,
            entry_ts=self._bars_ts[pos.entry_bar],
            exit_ts=bar.ts,
            entry_price=pos.entry_price, exit_price=exit_price,
            sl_dist=pos.sl_dist_orig, r_pnl=r,
            exit_reason=reason,
            attempt=self.active_oco.attempt if self.active_oco else 0,
            structure=S_NONE, atr_signal=0.0,
        ))
        self.active_pos = None


# =============================================================================
# strategy.py
# =============================================================================

"""
squeeze_zz_atr — strategy.py  v1.2
====================================
Daemon-стратегия: ATR-компрессия + ZigZag структура → OCO breakout.
Вариант B: trail_mult=0.3 при trail_act_r=0.5R, protector BE при 0.25R, TP=0.

Запрашивает M5 у демона, ресемплирует в целевой TF (M30/H1/H4) инкрементально
через bucket = epoch // tf_sec — идентично research backtester.

Зависимости:
  strategy_core.py  — должен лежать в той же папке.
"""

import json
from collections import defaultdict
from typing import Dict, List, Optional, Tuple

import numpy as np


M5_SEC = 300


# ─────────────────────────────────────────────────────────────────────────────
# Per-symbol state
# ─────────────────────────────────────────────────────────────────────────────

class SymState:

    def __init__(self, sym: str, tf: str, p: dict):
        self.sym     = sym
        self.tf      = tf
        self.tf_sec  = TF_SECONDS[tf]

        # ── Indicator params ──────────────────────────────────────────────
        self.atr_period      = int(p.get("atr_period", 14))
        self.lb_bars         = round(float(p.get("lb_hours", 20)) * BARS_PER_HOUR[tf])
        self.dp              = float(p.get("dp", 0.40))
        self.zz_depth        = int(p.get("zz_depth", 9))
        self.zz_backstep     = int(p.get("zz_backstep", 3))
        self.dev_pts         = (float(p.get("zz_deviation", 5))
                                * float(p.get("point_size", 0.00001)))
        self.slope_flat_frac = float(p.get("slope_flat_frac", 0.20))
        self.ratio_thresh    = float(p.get("ratio_thresh", 0.85))
        self.noise_inside    = float(p.get("noise_inside", 0.0))
        self.sl_buffer       = float(p.get("sl_buffer", 0.0))
        self.sl_cap_atr      = float(p.get("sl_cap_atr", 3.0))

        # ── Trail / protector ─────────────────────────────────────────────
        self.trail_act_r    = float(p.get("trail_act_r",    0.5))
        self.trail_mult     = float(p.get("trail_mult",     0.3))
        self.protect_at_r   = float(p.get("protect_at_r",  0.25))
        self.protect_lock_r = float(p.get("protect_lock_r", 0.0))   # 0.0 = BE

        # ── Timeouts ──────────────────────────────────────────────────────
        self.tmo_order    = int(p.get("tmo_order",    20))
        self.tmo_position = int(p.get("tmo_position", 60))
        self.max_attempts = int(p.get("max_attempts", 3))

        # ── Resampled TF bar buffer ───────────────────────────────────────
        self.buf_h:  List[float] = []
        self.buf_l:  List[float] = []
        self.buf_c:  List[float] = []
        self.buf_ts: List[int]   = []   # bucket timestamp (bucket * tf_sec)

        # ── M5 cursor ─────────────────────────────────────────────────────
        self.last_m5_ts:     int   = 0
        self.forming_bucket: int   = -1
        self.forming_h:      float = 0.0
        self.forming_l:      float = float("inf")
        self.forming_c:      float = 0.0
        self.forming_o:      float = 0.0

        # ── Zone tracking ─────────────────────────────────────────────────
        self.zone_attempt:   int   = 0
        self.zone_atr_start: float = 0.0

        # ── Pending OCO dedup ─────────────────────────────────────────────
        self.pending:            bool = False
        self.pending_placed_idx: int  = -1   # buf index когда выставили OCO

        # ── Trail / protector per ticket ──────────────────────────────────
        # ticket → {"peak": float, "trail_on": bool, "protect_done": bool}
        self.trail: Dict[int, dict] = {}


# ─────────────────────────────────────────────────────────────────────────────
# Strategy
# ─────────────────────────────────────────────────────────────────────────────

class Strategy:

    def __init__(self, config: dict):
        p      = config.get("params", {})
        combos = config.get("combos", [])

        # history_bars в M5 единицах — с запасом чтобы после ресемплинга
        # хватило на atr_period + lb_bars + zz_depth
        self.hbars = int(p.get("history_bars", 7200))
        self.rcap  = p.get("r_cap", None)

        self.syms:       List[str]           = []
        self.timeframes: Dict[str, str]      = {}
        self._states:    Dict[str, SymState] = {}

        for c in combos:
            sym  = c["sym"]
            dirs = c.get("directions", {})
            dk   = next((k for k in ("BOTH", "LONG", "SHORT") if k in dirs), None)
            if dk is None:
                continue
            strat_p = dirs[dk].get("strat", {})
            # tf в конфиге = целевой TF ресемплинга (M30/H1/H4)
            # у демона всегда запрашиваем M5, ресемплируем внутри
            resample_tf = strat_p.get("tf", "M30")
            if resample_tf not in TF_SECONDS:
                resample_tf = "M30"

            merged = {**p, **strat_p}

            self.syms.append(sym)
            self.timeframes[sym] = "M5"        # всегда M5 у демона
            self._states[sym]    = SymState(sym, resample_tf, merged)

    # ─── Protocol ────────────────────────────────────────────────────────────

    def get_requirements(self) -> dict:
        return {
            "symbols":      self.syms,
            "timeframes":   self.timeframes,
            "history_bars": self.hbars,
            "r_cap":        self.rcap,
        }

    def on_bars(self, bars: dict, positions: list) -> list:
        actions: list = []

        pos_by_sym: Dict[str, list] = defaultdict(list)
        for pos in positions:
            pos_by_sym[pos["symbol"]].append(pos)

        for sym, state in self._states.items():
            raw = bars.get(sym)
            if not raw:
                continue

            # 1. Ресемплинг M5 → целевой TF; получаем только новые закрытые бары
            new_closed = self._resample(state, raw)
            if not new_closed:
                # Новых закрытых баров нет — только trail на уже открытых позициях
                sym_pos = pos_by_sym.get(sym, [])
                if sym_pos and state.buf_c:
                    actions.extend(self._manage_positions(state, sym_pos))
                continue

            # 2. Добавляем в буфер
            for b in new_closed:
                state.buf_h.append(b["h"])
                state.buf_l.append(b["l"])
                state.buf_c.append(b["c"])
                state.buf_ts.append(b["ts"])

            warmup = state.atr_period + state.lb_bars + state.zz_depth + 5
            if len(state.buf_c) < warmup:
                continue

            cur_buf_idx = len(state.buf_c) - 1
            cur_ts      = state.buf_ts[-1]
            sym_pos     = pos_by_sym.get(sym, [])

            # 3. Позиция появилась → OCO исполнен
            if state.pending and sym_pos:
                state.pending = False

            # 4. OCO timeout
            if state.pending:
                bars_since = cur_buf_idx - state.pending_placed_idx
                if bars_since >= state.tmo_order:
                    state.pending = False

            # 5. Управление открытой позицией
            if sym_pos:
                actions.extend(self._manage_positions(state, sym_pos))
                continue

            # 6. Нет позиции, нет pending → ищем сигнал
            if state.pending:
                continue

            if self._in_notrade(cur_ts):
                continue

            sig = self._detect_signal(state, cur_buf_idx, cur_ts)
            if sig:
                actions.extend(sig)

        return actions

    # ─── Resampling ───────────────────────────────────────────────────────────

    def _resample(self, state: SymState, raw: list) -> list:
        """
        Инкрементальный ресемплинг M5 → target TF.
        bucket = epoch // tf_sec  (идентично research backtester).
        Возвращает только новые ЗАКРЫТЫЕ бары — forming-бар не включается.
        """
        tf_sec  = state.tf_sec
        closed: list = []

        for bar in raw:
            ts = int(bar["time"])
            if ts <= state.last_m5_ts:
                continue                       # уже обработан
            state.last_m5_ts = ts

            bucket = (ts // tf_sec) * tf_sec   # начало TF-бара

            if state.forming_bucket == -1:
                # Первый бар вообще
                state.forming_bucket = bucket
                state.forming_o = float(bar["open"])
                state.forming_h = float(bar["high"])
                state.forming_l = float(bar["low"])
                state.forming_c = float(bar["close"])

            elif bucket == state.forming_bucket:
                # Тот же TF-бар — обновляем OHLC
                state.forming_h = max(state.forming_h, float(bar["high"]))
                state.forming_l = min(state.forming_l, float(bar["low"]))
                state.forming_c = float(bar["close"])

            else:
                # Новый бакет → закрываем предыдущий
                closed.append({
                    "ts": state.forming_bucket,
                    "h":  state.forming_h,
                    "l":  state.forming_l,
                    "c":  state.forming_c,
                })
                # Начинаем новый forming
                state.forming_bucket = bucket
                state.forming_o = float(bar["open"])
                state.forming_h = float(bar["high"])
                state.forming_l = float(bar["low"])
                state.forming_c = float(bar["close"])

        return closed

    # ─── Signal detection ─────────────────────────────────────────────────────

    def _detect_signal(self, state: SymState,
                        cur_buf_idx: int, cur_ts: int) -> Optional[list]:
        n = len(state.buf_c)
        h = np.array(state.buf_h, dtype=np.float64)
        l = np.array(state.buf_l, dtype=np.float64)
        c = np.array(state.buf_c, dtype=np.float64)

        # ATR
        atr_arr = compute_atr(h, l, c, state.atr_period)
        atr_cur = float(atr_arr[-1])
        if np.isnan(atr_cur) or atr_cur <= 0:
            return None

        idx_lb = n - 1 - state.lb_bars
        if idx_lb < 0 or np.isnan(atr_arr[idx_lb]):
            return None
        atr_lb = float(atr_arr[idx_lb])
        if atr_lb <= 0:
            return None

        if (atr_cur - atr_lb) / atr_lb >= -state.dp:
            return None

        # ZigZag
        zp, zb = compute_zigzag(h, l, state.zz_depth, state.dev_pts, state.zz_backstep)
        p_idx, p_val, p_type = extract_pivots(zp, zb)
        pivots = self._confirmed_pivots(p_idx, p_val, p_type)
        if pivots is None:
            return None

        H1, t_H1, H2, t_H2, L1, t_L1, L2, t_L2 = pivots
        t_cur = n - 1

        structure, _, _, upper_cur, lower_cur = classify_structure(
            H1, t_H1, H2, t_H2, L1, t_L1, L2, t_L2,
            atr_cur, state.slope_flat_frac, state.ratio_thresh, t_cur
        )

        if structure not in (S_TRI_SYM, S_TRI_DESC, S_TRI_ASC, S_CHAOS):
            return None

        noise = state.noise_inside * atr_cur
        breakout_up   = c[-1] > upper_cur + noise
        breakout_down = c[-1] < lower_cur - noise
        if not breakout_up and not breakout_down:
            return None

        # Зонный счётчик
        if state.zone_atr_start > 0 and atr_cur > state.zone_atr_start:
            state.zone_attempt = 0
        if state.zone_attempt >= state.max_attempts:
            return None

        # Структурный SL — выбираем только в направлении пробоя
        if breakout_up:
            sl_price = L1 - state.sl_buffer
            sl_dist  = c[-1] - sl_price          # от текущей цены до SL
            sl_max   = state.sl_cap_atr * atr_cur
            if sl_dist > sl_max:
                sl_price = c[-1] - sl_max
                sl_dist  = sl_max
            if sl_dist <= 0:
                return None
            direction = "LONG"
        else:
            sl_price = H1 + state.sl_buffer
            sl_dist  = sl_price - c[-1]
            sl_max   = state.sl_cap_atr * atr_cur
            if sl_dist > sl_max:
                sl_price = c[-1] + sl_max
                sl_dist  = sl_max
            if sl_dist <= 0:
                return None
            direction = "SHORT"

        state.zone_attempt   += 1
        state.zone_atr_start  = atr_lb

        state.pending            = True
        state.pending_placed_idx = cur_buf_idx

        signal_data = json.dumps({
            "sl_dist":   round(float(sl_dist),  8),
            "structure": int(structure),
            "atr":       round(float(atr_cur), 8),
        })

        # Пробой по close — входим рыночным ордером, исполнение по open следующего бара
        return [{
            "action":      "ENTER",
            "symbol":      state.sym,
            "direction":   direction,
            "sl_price":    round(float(sl_price), 8),
            "tp_price":    0,
            "signal_data": signal_data,
        }]

    # ─── Position management ─────────────────────────────────────────────────

    def _manage_positions(self, state: SymState, sym_pos: list) -> list:
        actions = []
        cur_ts  = state.buf_ts[-1]
        cur_bid = state.buf_c[-1]

        for pos in sym_pos:
            ticket     = int(pos["ticket"])
            direction  = pos["direction"]
            price_open = float(pos["price_open"])
            cur_sl     = float(pos.get("sl") or 0)

            try:
                sd = json.loads(pos.get("signal_data") or "{}")
                sl_dist_orig = float(sd.get("sl_dist", 0))
            except Exception:
                sl_dist_orig = 0.0

            if sl_dist_orig <= 0:
                continue

            if ticket not in state.trail:
                state.trail[ticket] = {
                    "peak":         price_open,
                    "trail_on":     False,
                    "protect_done": False,
                }
            ts = state.trail[ticket]

            # Обновляем peak
            if direction == "LONG":
                if cur_bid > ts["peak"]:
                    ts["peak"] = cur_bid
            else:
                if cur_bid < ts["peak"]:
                    ts["peak"] = cur_bid

            mfe_r = abs(ts["peak"] - price_open) / sl_dist_orig

            # Protector BE
            if not ts["protect_done"] and mfe_r >= state.protect_at_r:
                if direction == "LONG":
                    be_sl = price_open + state.protect_lock_r * sl_dist_orig
                    if be_sl > cur_sl:
                        actions.append({"action": "MODIFY_SL", "ticket": ticket,
                                        "new_sl": round(float(be_sl), 8)})
                        cur_sl = be_sl
                else:
                    be_sl = price_open - state.protect_lock_r * sl_dist_orig
                    if be_sl < cur_sl:
                        actions.append({"action": "MODIFY_SL", "ticket": ticket,
                                        "new_sl": round(float(be_sl), 8)})
                        cur_sl = be_sl
                ts["protect_done"] = True

            # Trail
            if mfe_r >= state.trail_act_r:
                ts["trail_on"] = True

            if ts["trail_on"]:
                if direction == "LONG":
                    new_sl = ts["peak"] - state.trail_mult * sl_dist_orig
                    if new_sl > cur_sl:
                        actions.append({"action": "MODIFY_SL", "ticket": ticket,
                                        "new_sl": round(float(new_sl), 8)})
                        cur_sl = new_sl
                else:
                    new_sl = ts["peak"] + state.trail_mult * sl_dist_orig
                    if new_sl < cur_sl:
                        actions.append({"action": "MODIFY_SL", "ticket": ticket,
                                        "new_sl": round(float(new_sl), 8)})
                        cur_sl = new_sl

            # Timeout — в TF-барах
            open_ts   = int(pos.get("open_time", 0))
            bars_held = (cur_ts - open_ts) // state.tf_sec if open_ts else 0
            if bars_held >= state.tmo_position:
                actions.append({"action": "EXIT", "ticket": ticket})
                state.trail.pop(ticket, None)

        # Чистим trail закрытых тикетов
        active = {int(p["ticket"]) for p in sym_pos}
        for t in list(state.trail.keys()):
            if t not in active:
                state.trail.pop(t)

        return actions

    # ─── Helpers ─────────────────────────────────────────────────────────────

    def _confirmed_pivots(self, p_idx, p_val, p_type) -> Optional[Tuple]:
        """2 последних подтверждённых хая + 2 лоя. Последний пивот (незакрытая нога) пропускается."""
        n = len(p_idx)
        if n < 2:
            return None
        H1 = H2 = L1 = L2 = None
        t_H1 = t_H2 = t_L1 = t_L2 = -1
        for k in range(n - 2, -1, -1):
            pt = int(p_type[k])
            if pt == 1:
                if t_H1 == -1: H1 = float(p_val[k]); t_H1 = int(p_idx[k])
                elif t_H2 == -1: H2 = float(p_val[k]); t_H2 = int(p_idx[k])
            else:
                if t_L1 == -1: L1 = float(p_val[k]); t_L1 = int(p_idx[k])
                elif t_L2 == -1: L2 = float(p_val[k]); t_L2 = int(p_idx[k])
            if t_H2 != -1 and t_L2 != -1:
                break
        if None in (H1, H2, L1, L2):
            return None
        return H1, t_H1, H2, t_H2, L1, t_L1, L2, t_L2

    def _in_notrade(self, ts: int) -> bool:
        sod = ts % 86400
        return NTW_START <= sod <= NTW_END

    # ─── State persistence ───────────────────────────────────────────────────

    def save_state(self) -> dict:
        out = {}
        for sym, st in self._states.items():
            out[sym] = {
                "last_m5_ts":        int(st.last_m5_ts),
                "forming_bucket":    int(st.forming_bucket),
                "forming_o":         float(st.forming_o),
                "forming_h":         float(st.forming_h),
                "forming_l":         float(st.forming_l),
                "forming_c":         float(st.forming_c),
                "zone_attempt":      int(st.zone_attempt),
                "zone_atr_start":    float(st.zone_atr_start),
                "pending":           bool(st.pending),
                "pending_placed_idx": int(st.pending_placed_idx),
                "trail": {
                    str(k): {
                        "peak":         float(v["peak"]),
                        "trail_on":     bool(v["trail_on"]),
                        "protect_done": bool(v["protect_done"]),
                    }
                    for k, v in st.trail.items()
                },
                # Сохраняем буфер для восстановления после рестарта
                "buf_h":  [float(x) for x in st.buf_h[-500:]],
                "buf_l":  [float(x) for x in st.buf_l[-500:]],
                "buf_c":  [float(x) for x in st.buf_c[-500:]],
                "buf_ts": [int(x)   for x in st.buf_ts[-500:]],
            }
        return out

    def restore_state(self, state: dict):
        if not state:
            return
        for sym, snap in state.items():
            if sym not in self._states:
                continue
            st = self._states[sym]
            st.last_m5_ts        = int(snap.get("last_m5_ts",        0))
            st.forming_bucket    = int(snap.get("forming_bucket",    -1))
            st.forming_o         = float(snap.get("forming_o",        0.0))
            st.forming_h         = float(snap.get("forming_h",        0.0))
            st.forming_l         = float(snap.get("forming_l",        float("inf")))
            st.forming_c         = float(snap.get("forming_c",        0.0))
            st.zone_attempt      = int(snap.get("zone_attempt",       0))
            st.zone_atr_start    = float(snap.get("zone_atr_start",   0.0))
            st.pending           = bool(snap.get("pending",           False))
            st.pending_placed_idx = int(snap.get("pending_placed_idx", -1))
            st.trail             = {
                int(k): {
                    "peak":         float(v["peak"]),
                    "trail_on":     bool(v["trail_on"]),
                    "protect_done": bool(v["protect_done"]),
                }
                for k, v in snap.get("trail", {}).items()
            }
            st.buf_h  = list(snap.get("buf_h",  []))
            st.buf_l  = list(snap.get("buf_l",  []))
            st.buf_c  = list(snap.get("buf_c",  []))
            st.buf_ts = list(snap.get("buf_ts", []))