"""
SQUEEZE_ZZ_ATR — strategy_core.py  v1.1
========================================
Чистая логика стратегии: ZigZag, ATR, структура, OCO, позиция.
Daemon-совместимый Python (без numba). Портируется в strategy.py напрямую.

Использование в research: импортируется как reference-реализация.
Использование в daemon: strategy.py наследует/копирует SqueezeZZATRState.

═══════════════════════════════════════════════════════
CHANGELOG v1.1 (fixes):
  [BUG-1] _manage_position: double-touch для LONG —
          `bar.open <= pos.sl or True` было always-True → ВСЕГДА SL.
          Исправлено: if open >= tp → TP first (gap); иначе conservative SL.
  [BUG-2] _close_position: attempt всегда 0 —
          active_oco уже None к моменту вызова.
          Исправлено: attempt хранится в Position.
  [ADD-1] noise_pivot: параметр добавлен в __init__ и гриде.
          Полная реализация (проекция H1 против линии H2→H3) требует
          трекинга 3-го пивота — TODO. При noise_pivot=0.0 — жёсткий
          режим (baseline исследование), параметр готов к гриду.
  [ADD-2] noise_inside: теперь применяется к breakout-проверке:
          breakout_up = close > upper_cur + noise_inside * atr_cur.
          Смысл: требуем убедительный close за границу, не любое
          фитильное щекотание.
  [FIX-1] ATR: EMA → Wilder's (alpha=1/period, не 2/(period+1)).
  [DOC-1] dp: 0.40 = 40% — явный комментарий, фракция не проценты.
═══════════════════════════════════════════════════════
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
STR_NAMES  = {0: "NONE", 1: "TRI_SYM", 2: "TRI_DESC", 3: "TRI_ASC",
              4: "CHANNEL", 5: "CHAOS"}

# Коды выхода
EXIT_TP      = "TP"
EXIT_SL      = "SL"
EXIT_TRAIL   = "TRAIL"
EXIT_TIMEOUT = "TIMEOUT"

# No-trade window (server time): 23:40–01:05 (forex rollover, crossover через полночь)
# Совпадает с research backtester (step5/step6): sod >= NTW_START or sod <= NTW_END
NTW_START = 23 * 3600 + 40 * 60   # 85200
NTW_END   =  1 * 3600 +  5 * 60   #  3900


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
    buy_stop:      float   # уровень BUY_STOP  (sq_hi)
    sell_stop:     float   # уровень SELL_STOP (sq_lo)
    sl_long:       float   # SL для long позиции
    sl_short:      float   # SL для short позиции
    sl_dist_long:  float   # расстояние SL long (price units)
    sl_dist_short: float   # расстояние SL short
    atr_signal:    float
    placed_bar:    int     # индекс бара выставления
    attempt:       int


@dataclass
class Position:
    direction:    int     # +1 long, -1 short
    entry_price:  float
    sl:           float
    tp:           float
    sl_dist_orig: float   # оригинальный SL dist (для R-расчёта, НЕ изменяется трейлом)
    entry_bar:    int
    peak_price:   float   # лучшая цена с момента входа (для трейла)
    attempt:      int     # [BUG-2 FIX] сохраняем attempt из OCO при открытии
    trail_active: bool = False


@dataclass
class Trade:
    symbol:       str
    tf:           str
    direction:    int
    entry_bar:    int
    exit_bar:     int
    entry_ts:     int
    exit_ts:      int
    entry_price:  float
    exit_price:   float
    sl_dist:      float   # оригинальный sl_dist (для R-расчёта)
    r_pnl:        float
    exit_reason:  str
    attempt:      int
    structure:    int
    atr_signal:   float


# ─────────────────────────────────────────────────────────────────────────
# ZIGZAG (порт MQ5 ZigzagColor)
# ─────────────────────────────────────────────────────────────────────────
def compute_zigzag(high: np.ndarray, low: np.ndarray,
                   depth: int, deviation_pts: float, backstep: int
                   ) -> Tuple[np.ndarray, np.ndarray]:
    """
    Порт ZigzagColor.mq5. deviation_pts в ценовых единицах
    (deviation_param * point_size умножается снаружи).
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
        lo_val = min(low[beg:shift + 1])
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
        hi_val = max(high[beg:shift + 1])
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
        else:              # state == -1: после peak — ищем bottom
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
    return (np.array(idxs,  dtype=np.int64),
            np.array(vals,  dtype=np.float64),
            np.array(types, dtype=np.int8))


# ─────────────────────────────────────────────────────────────────────────
# ATR — Wilder's smoothing  [FIX-1]
# ─────────────────────────────────────────────────────────────────────────
def compute_atr(high: np.ndarray, low: np.ndarray, close: np.ndarray,
                period: int) -> np.ndarray:
    """
    Wilder's ATR (RMA):  atr[i] = (atr[i-1] * (period-1) + TR[i]) / period
    alpha = 1/period  (vs EMA alpha = 2/(period+1)).
    Инициализация: SMA(TR, period) по первому полному окну [1..period].
    """
    n = len(close)
    atr = np.full(n, np.nan)
    if n < period + 2:
        return atr
    tr = np.empty(n)
    tr[0] = high[0] - low[0]
    for i in range(1, n):
        tr[i] = max(high[i] - low[i],
                    abs(high[i] - close[i - 1]),
                    abs(low[i]  - close[i - 1]))
    # инициализация: SMA первых period TR-значений (индекс 1..period включительно)
    atr[period] = sum(tr[1:period + 1]) / period
    # Wilder's smoothing: alpha = 1/period
    for i in range(period + 1, n):
        atr[i] = (atr[i - 1] * (period - 1) + tr[i]) / period
    return atr


# ─────────────────────────────────────────────────────────────────────────
# КЛАССИФИКАЦИЯ СТРУКТУРЫ
# ─────────────────────────────────────────────────────────────────────────
def classify_structure(H1, t_H1, H2, t_H2, L1, t_L1, L2, t_L2,
                        atr_val: float, slope_flat_frac: float,
                        ratio_thresh: float, t_cur: int,
                        noise_pivot: float = 0.0
                        ) -> Tuple[int, float, float, float, float]:
    """
    Классифицирует структуру ZigZag.
    Возвращает (structure_code, slope_H, slope_L, upper_cur, lower_cur).

    flat_thresh = slope_flat_frac * atr_val  (per bar).

    noise_pivot (секция 4.6): проверяет что H1/L1 лежат "на линии" треугольника.
    ВАЖНО: полная реализация требует трёх пиков/трофов (H3, L3) для вычисления
    "предыдущего наклона" до появления H1. С двумя пивотами projected_H == H1
    тавтологически — проверка тривиальна. При noise_pivot=0.0 (baseline)
    функция работает корректно. Расширить до H3/L3 — TODO по результатам
    первичного исследования.
    """
    dH = t_H1 - t_H2
    dL = t_L1 - t_L2
    if dH == 0 or dL == 0:
        return S_NONE, 0.0, 0.0, 0.0, 0.0

    slope_H = (H1 - H2) / dH
    slope_L = (L1 - L2) / dL

    upper_cur = H1 + slope_H * (t_cur - t_H1)
    lower_cur = L1 + slope_L * (t_cur - t_L1)

    flat_thresh = slope_flat_frac * atr_val

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
        w0 = upper_t0 - lower_t0
        wt = upper_cur - lower_cur
        if w0 > 0.0 and (wt / w0) > ratio_thresh:
            structure = S_CHAOS  # недостаточно сузился

    # noise_pivot заглушка — при noise_pivot > 0 логика будет добавлена
    # после расширения _get_confirmed_pivots до 3 пиков/трофов.
    # Сейчас параметр присутствует в гриде, при noise_pivot=0.0 (baseline)
    # поведение идентично оригиналу (жёсткий режим без допуска).

    return structure, slope_H, slope_L, upper_cur, lower_cur


# ─────────────────────────────────────────────────────────────────────────
# STATEFUL СТРАТЕГИЯ (daemon-совместимая)
# ─────────────────────────────────────────────────────────────────────────
class SqueezeZZATRState:
    """
    Stateful объект стратегии. Получает бары один за одним, генерирует сигналы.
    Параметры передаются при инициализации.

    Daemon-совместим: on_bar() → набор действий, save/restore state.

    Параметр dp:
        Фракция (0.40 = 40%), НЕ проценты.
        Грид: [0.35, 0.40, 0.45, 0.50] — всё в долях.
        Спек пишет "35, 40, 45, 50 (%)" — при генерации грида делить на 100.
    """

    def __init__(self,
                 symbol:          str,
                 tf:              str,
                 atr_period:      int   = 14,
                 lb_hours:        float = 20,
                 dp:              float = 0.40,   # ФРАКЦИЯ: 0.40 = 40% снижения ATR
                 zz_depth:        int   = 9,
                 zz_deviation:    int   = 5,       # MT5 points, умножается на point_size снаружи
                 zz_backstep:     int   = 3,
                 slope_flat_frac: float = 0.20,
                 ratio_thresh:    float = 0.85,
                 noise_pivot:     float = 0.0,    # [ADD-1] доля ATR; 0.0 = жёсткий режим
                 noise_inside:    float = 0.0,    # [ADD-2] доля ATR для breakout-допуска
                 sl_buffer:       float = 0.0,    # в price units
                 sl_cap_atr:      float = 3.0,
                 tmo_order:       int   = 20,     # баров до отмены ордера
                 tmo_position:    int   = 60,     # баров до принудительного выхода
                 max_attempts:    int   = 3,
                 trail_mult:      float = 0.5,
                 trail_act_r:     float = 1.0,
                 cost:            float = 0.0,    # flat cost в price units
                 point_size:      float = 0.00001,
                 min_sl_dist:     float = 0.0,    # мин. расстояние SL от entry (price units)
                 #   0.0  → research-режим: все сделки включаются
                 #   >0.0 → daemon-aligned: отражает min_stop_coeffs[symbol]
                 ):
        self.symbol          = symbol
        self.tf              = tf
        self.atr_period      = atr_period
        self.lb_bars         = round(lb_hours * BARS_PER_HOUR[tf])
        self.dp              = dp                # фракция (0.40 = 40%)
        self.zz_depth        = zz_depth
        self.zz_deviation    = zz_deviation
        self.zz_backstep     = zz_backstep
        self.slope_flat_frac = slope_flat_frac
        self.ratio_thresh    = ratio_thresh
        self.noise_pivot     = noise_pivot       # [ADD-1]
        self.noise_inside    = noise_inside      # [ADD-2]
        self.sl_buffer       = sl_buffer
        self.sl_cap_atr      = sl_cap_atr
        self.tmo_order       = tmo_order
        self.tmo_position    = tmo_position
        self.max_attempts    = max_attempts
        self.trail_mult      = trail_mult
        self.trail_act_r     = trail_act_r
        self.cost            = cost
        self.dev_pts         = zz_deviation * point_size
        self.min_sl_dist     = min_sl_dist

        # История баров (rolling buffer)
        self._bars_h:  List[float] = []
        self._bars_l:  List[float] = []
        self._bars_c:  List[float] = []
        self._bars_ts: List[int]   = []
        self._bar_idx: int = -1

        # Состояние
        self.active_oco:   Optional[OCOOrder] = None
        self.active_pos:   Optional[Position] = None
        self.zone_attempt: int   = 0
        self.zone_atr_start: float = 0.0
        self.trades: List[Trade] = []

        # Кэш ZZ (пересчитывается каждый бар)
        self._p_idx  = np.array([], dtype=np.int64)
        self._p_val  = np.array([], dtype=np.float64)
        self._p_type = np.array([], dtype=np.int8)

    # ─────────────────────────────────────────────────────────────────────
    # Вспомогательные методы
    # ─────────────────────────────────────────────────────────────────────

    def _in_notrade(self, ts: int) -> bool:
        sod = ts % 86400
        return sod >= NTW_START or sod <= NTW_END

    def _get_atr(self) -> float:
        atr_arr = compute_atr(
            np.array(self._bars_h),
            np.array(self._bars_l),
            np.array(self._bars_c),
            self.atr_period
        )
        v = atr_arr[-1]
        return float(v) if not np.isnan(v) else 0.0

    def _get_atr_at(self, idx_back: int) -> float:
        """ATR на idx_back баров назад."""
        atr_arr = compute_atr(
            np.array(self._bars_h),
            np.array(self._bars_l),
            np.array(self._bars_c),
            self.atr_period
        )
        i = len(atr_arr) - 1 - idx_back
        if i < 0:
            return 0.0
        v = atr_arr[i]
        return float(v) if not np.isnan(v) else 0.0

    def _update_zz(self):
        zp, zb = compute_zigzag(
            np.array(self._bars_h),
            np.array(self._bars_l),
            self.zz_depth, self.dev_pts, self.zz_backstep
        )
        self._p_idx, self._p_val, self._p_type = extract_pivots(zp, zb)

    def _get_confirmed_pivots(self):
        """
        Подтверждённые пивоты: все кроме последней незакрытой ноги.
        Возвращает (H1, t_H1, H2, t_H2, L1, t_L1, L2, t_L2) или None.

        H1/L1 — последние подтверждённые хай/лой.
        H2/L2 — предпоследние (нужны для вычисления наклона).

        NOTE: noise_pivot (секция 4.6) требует H3/L3 для проверки что
        новый H1 лежит на экстраполированной линии H2→H3. Расширить
        функцию до возврата H3/L3 — TODO после первичного IS-анализа.
        """
        n = len(self._p_idx)
        if n < 2:
            return None
        # Открытая нога = последний пивот (p_idx[n-1]) — не подтверждён
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

    # ─────────────────────────────────────────────────────────────────────
    # ОСНОВНОЙ МЕТОД
    # ─────────────────────────────────────────────────────────────────────

    def on_bar(self, bar: Bar) -> Optional[dict]:
        """
        Обработка нового закрытого бара.
        Возвращает dict с действием или None.

        Действия: PLACE_OCO, CANCEL_OCO, FILLED_LONG, FILLED_SHORT, EXIT
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

        # ── 1. Управление открытой позицией ───────────────────────────
        if self.active_pos is not None:
            action = self._manage_position(self.active_pos, bar, atr_cur)
            if action is not None:
                return action

        # ── 2. Обработка активного OCO (ждём исполнения) ──────────────
        if self.active_oco is not None:
            oco = self.active_oco
            bars_waiting = self._bar_idx - oco.placed_bar
            if bars_waiting >= self.tmo_order:
                self.active_oco = None
                return {"action": "CANCEL_OCO", "reason": "timeout"}
            filled = self._check_oco_fill(oco, bar, atr_cur)
            if filled is not None:
                return filled
            return None

        # ── 3. Поиск нового сигнала ────────────────────────────────────
        # (достигаем только если нет ни позиции, ни OCO)
        if self._in_notrade(bar.ts):
            return None

        # ATR-компрессия
        atr_drop = (atr_cur - atr_lb) / atr_lb   # отрицательное при сжатии
        if atr_drop >= -self.dp:                  # dp — фракция (0.40 = 40%)
            return None

        # Пивоты и структура
        pivots = self._get_confirmed_pivots()
        if pivots is None:
            return None
        H1, t_H1, H2, t_H2, L1, t_L1, L2, t_L2 = pivots

        t_cur = n - 1
        structure, slope_H, slope_L, upper_cur, lower_cur = classify_structure(
            H1, t_H1, H2, t_H2, L1, t_L1, L2, t_L2,
            atr_cur, self.slope_flat_frac, self.ratio_thresh, t_cur,
            self.noise_pivot
        )

        if structure not in (S_TRI_SYM, S_TRI_DESC, S_TRI_ASC, S_CHAOS):
            return None

        # Детекция пробоя: [ADD-2] noise_inside создаёт допуск на close
        # noise_inside=0.0 → строго: любой close за границу = сигнал
        # noise_inside=0.1 → нужен close как минимум 0.1 ATR за границу
        noise_band = self.noise_inside * atr_cur
        breakout_up   = bar.close > upper_cur + noise_band
        breakout_down = bar.close < lower_cur - noise_band

        if not breakout_up and not breakout_down:
            return None

        # Проверка смены зоны (ATR вырос → новый цикл компрессии)
        if self.zone_atr_start > 0 and atr_cur > self.zone_atr_start:
            self.zone_attempt = 0

        if self.zone_attempt >= self.max_attempts:
            return None

        # Структурный SL
        sl_long  = L1 - self.sl_buffer
        sl_short = H1 + self.sl_buffer

        # Расстояния SL с учётом стоимости входа
        sl_dist_long  = (upper_cur + self.cost) - sl_long
        sl_dist_short = sl_short - (lower_cur - self.cost)

        # Cap по ATR
        sl_max = self.sl_cap_atr * atr_cur
        if sl_dist_long > sl_max:
            sl_long       = upper_cur + self.cost - sl_max
            sl_dist_long  = sl_max
        if sl_dist_short > sl_max:
            sl_short      = lower_cur - self.cost + sl_max
            sl_dist_short = sl_max

        # Проверка стороны SL:
        #   BUY_STOP  @ upper_cur → SL должен быть НИЖЕ entry (sl_long < buy_stop)
        #   SELL_STOP @ lower_cur → SL должен быть ВЫШЕ entry (sl_short > sell_stop)
        # Нарушение возможно если L1 > upper_cur или H1 < lower_cur
        # (вырожденная структура: пивот оказался за точкой входа).
        if sl_long >= upper_cur:
            return None   # SL выше или равен BUY_STOP — сделка некорректна
        if sl_short <= lower_cur:
            return None   # SL ниже или равен SELL_STOP — сделка некорректна

        # Проверка минимального расстояния SL:
        #   research (min_sl_dist=0.0): пропускаем всё — видим полное распределение
        #   daemon-aligned (min_sl_dist=min_stop_coeffs[symbol]): фильтрует как гейт
        if self.min_sl_dist > 0.0:
            if sl_dist_long  < self.min_sl_dist:
                return None
            if sl_dist_short < self.min_sl_dist:
                return None

        self.zone_attempt    += 1
        self.zone_atr_start   = atr_lb   # ATR на начало компрессии

        self.active_oco = OCOOrder(
            buy_stop      = upper_cur,
            sell_stop     = lower_cur,
            sl_long       = sl_long,
            sl_short      = sl_short,
            sl_dist_long  = sl_dist_long,
            sl_dist_short = sl_dist_short,
            atr_signal    = atr_cur,
            placed_bar    = self._bar_idx,
            attempt       = self.zone_attempt,
        )

        return {
            "action":    "PLACE_OCO",
            "buy_stop":  upper_cur,
            "sell_stop": lower_cur,
            "sl_long":   sl_long,
            "sl_short":  sl_short,
            "structure": structure,
            "structure_name": STR_NAMES[structure],
        }

    def _check_oco_fill(self, oco: OCOOrder, bar: Bar, atr_cur: float) -> Optional[dict]:
        """
        Проверяет сработал ли один из стоп-ордеров OCO.
        При double-fill на одном баре: conservative — смотрим bar.open.

        OCO механика: при срабатывании одного ордера active_oco = None
        (второй автоматически "аннулируется" — в daemon mode это
        выполняется через signal_data.oco_group в PendingOrderManager).
        """
        filled_long  = bar.high >= oco.buy_stop
        filled_short = bar.low  <= oco.sell_stop

        if filled_long and filled_short:
            # Double-touch на одном баре: определяем по open
            if bar.open >= oco.buy_stop:
                # Открылись выше BUY_STOP → short заполнился первым
                filled_long = False; filled_short = True
            elif bar.open <= oco.sell_stop:
                # Открылись ниже SELL_STOP → long заполнился первым
                filled_long = True; filled_short = False
            else:
                # Open внутри зоны: conservative — берём SHORT first
                # (цена скорее шла вниз раньше чем вверх — пессимистично)
                filled_long = False; filled_short = True

        if filled_long:
            entry = oco.buy_stop + self.cost
            tp    = entry + oco.sl_dist_long   # TP = 1R (для display, не для выхода)
            self.active_pos = Position(
                direction    = 1,
                entry_price  = entry,
                sl           = oco.sl_long,
                tp           = tp,
                sl_dist_orig = oco.sl_dist_long,
                entry_bar    = self._bar_idx,
                peak_price   = entry,
                attempt      = oco.attempt,    # [BUG-2 FIX]
            )
            self.active_oco = None             # OCO закрыт, второй ордер аннулирован
            return {"action": "FILLED_LONG", "entry": entry,
                    "sl": oco.sl_long, "tp": tp, "attempt": oco.attempt}

        if filled_short:
            entry = oco.sell_stop - self.cost
            tp    = entry - oco.sl_dist_short
            self.active_pos = Position(
                direction    = -1,
                entry_price  = entry,
                sl           = oco.sl_short,
                tp           = tp,
                sl_dist_orig = oco.sl_dist_short,
                entry_bar    = self._bar_idx,
                peak_price   = entry,
                attempt      = oco.attempt,    # [BUG-2 FIX]
            )
            self.active_oco = None
            return {"action": "FILLED_SHORT", "entry": entry,
                    "sl": oco.sl_short, "tp": tp, "attempt": oco.attempt}

        return None

    def _manage_position(self, pos: Position, bar: Bar, atr_cur: float) -> Optional[dict]:
        """
        Управление открытой позицией.
        Порядок: SL/TP check → trail update → timeout.

        Conservative double-touch: [BUG-1 FIX]
          LONG:  если open >= tp (gap выше TP) → TP первым; иначе SL первым
          SHORT: если open <= tp (gap ниже TP) → TP первым; иначе SL первым
          Обоснование: gap означает что цена уже была за TP при открытии →
          при реальном исполнении вышли бы по TP.
        """
        direction = pos.direction
        bars_held = self._bar_idx - pos.entry_bar

        # ── SL / TP hit ─────────────────────────────────────────────
        if direction == 1:   # LONG
            sl_hit = bar.low  <= pos.sl
            tp_hit = bar.high >= pos.tp
        else:                # SHORT
            sl_hit = bar.high >= pos.sl
            tp_hit = bar.low  <= pos.tp

        if sl_hit and tp_hit:
            # [BUG-1 FIX]: conservative double-touch
            if direction == 1:
                # LONG: gap выше TP → TP first; иначе SL first
                if bar.open >= pos.tp:
                    sl_hit = False; tp_hit = True
                else:
                    sl_hit = True;  tp_hit = False
            else:
                # SHORT: gap ниже TP → TP first; иначе SL first
                if bar.open <= pos.tp:
                    sl_hit = False; tp_hit = True
                else:
                    sl_hit = True;  tp_hit = False

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

        # ── Trail ─────────────────────────────────────────────────────
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
                if new_sl > pos.sl:
                    pos.sl = new_sl
            else:
                new_sl = pos.peak_price + self.trail_mult * pos.sl_dist_orig
                if new_sl < pos.sl:
                    pos.sl = new_sl

        # ── Timeout ───────────────────────────────────────────────────
        if bars_held >= self.tmo_position:
            exit_p = bar.close - self.cost * direction
            r = (exit_p - pos.entry_price) * direction / pos.sl_dist_orig
            self._close_position(bar, exit_p, r, EXIT_TIMEOUT)
            return {"action": "EXIT", "reason": EXIT_TIMEOUT, "r": r}

        return None

    def _close_position(self, bar: Bar, exit_price: float, r: float, reason: str):
        """Закрывает позицию, пишет Trade. [BUG-2 FIX]: attempt берётся из pos."""
        if self.active_pos is None:
            return
        pos = self.active_pos
        self.trades.append(Trade(
            symbol       = self.symbol,
            tf           = self.tf,
            direction    = pos.direction,
            entry_bar    = pos.entry_bar,
            exit_bar     = self._bar_idx,
            entry_ts     = self._bars_ts[pos.entry_bar],
            exit_ts      = bar.ts,
            entry_price  = pos.entry_price,
            exit_price   = exit_price,
            sl_dist      = pos.sl_dist_orig,
            r_pnl        = r,
            exit_reason  = reason,
            attempt      = pos.attempt,   # [BUG-2 FIX]: из Position, не из active_oco
            structure    = S_NONE,        # TODO: сохранять structure при входе
            atr_signal   = 0.0,           # TODO: сохранять atr_signal из OCO
        ))
        self.active_pos = None

    # ─────────────────────────────────────────────────────────────────────
    # СЕРИАЛИЗАЦИЯ СОСТОЯНИЯ (daemon-совместимая)
    # ─────────────────────────────────────────────────────────────────────

    def save_state(self) -> dict:
        """
        Возвращает JSON-сериализуемый dict.
        Все numpy-типы кастируются в native Python (int/float).
        """
        oco = None
        if self.active_oco:
            o = self.active_oco
            oco = {
                "buy_stop": float(o.buy_stop), "sell_stop": float(o.sell_stop),
                "sl_long": float(o.sl_long), "sl_short": float(o.sl_short),
                "sl_dist_long": float(o.sl_dist_long),
                "sl_dist_short": float(o.sl_dist_short),
                "atr_signal": float(o.atr_signal),
                "placed_bar": int(o.placed_bar), "attempt": int(o.attempt),
            }

        pos = None
        if self.active_pos:
            p = self.active_pos
            pos = {
                "direction": int(p.direction),
                "entry_price": float(p.entry_price),
                "sl": float(p.sl), "tp": float(p.tp),
                "sl_dist_orig": float(p.sl_dist_orig),
                "entry_bar": int(p.entry_bar),
                "peak_price": float(p.peak_price),
                "attempt": int(p.attempt),
                "trail_active": bool(p.trail_active),
            }

        return {
            "bar_idx":         int(self._bar_idx),
            "zone_attempt":    int(self.zone_attempt),
            "zone_atr_start":  float(self.zone_atr_start),
            "active_oco":      oco,
            "active_pos":      pos,
        }

    def restore_state(self, state: dict):
        """Восстанавливает состояние после рестарта демона."""
        if not state:
            return
        self._bar_idx       = state.get("bar_idx", -1)
        self.zone_attempt   = state.get("zone_attempt", 0)
        self.zone_atr_start = state.get("zone_atr_start", 0.0)

        oco_d = state.get("active_oco")
        if oco_d:
            self.active_oco = OCOOrder(**oco_d)

        pos_d = state.get("active_pos")
        if pos_d:
            self.active_pos = Position(**pos_d)
