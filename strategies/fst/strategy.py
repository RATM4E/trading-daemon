"""
FST — Fractal Structure Trend  v1.0
=====================================
Вход по зоне откатa в H4-структурном тренде.

Логика:
  HTF (H4, ресемплируется внутри из M30):
    - Фрактальные пивоты k=3 с swing-фильтром 0.75 ATR
    - UPTREND:   последний HH > prev HH  И  последний HL > prev HL
    - DOWNTREND: последний LH < prev LH  И  последний LL < prev LL

  Вход (M30):
    - LONG:  low[i] <= last_HL + zone_buf*ATR  AND  close[i] > last_HL
    - SHORT: high[i] >= last_LH - zone_buf*ATR AND  close[i] < last_LH
    - Confirm: tick_volume > vol_ma(20)

  SL структурный:
    - LONG:  close - max(close - (last_HL - sl_buf*ATR), bmin)
    - SHORT: close + max((last_LH + sl_buf*ATR) - close, bmin)

  TP:  tp_r * sl_dist (5R)

  D1 EMA(21) slope filter: только для DAX40 и XAUUSD (lb=3 D1 бара)

  Timeout: EXIT через timeout_bars M30 баров (48 = 24 часа)

Не в стратегии (daemon responsibility):
  - NTW gate (Gate 6)
  - R-cap (Gate 12, r_cap=3 из params)
  - Sizing / риск на R
  - Challenge cycle brake: brake_thresh=-3000, brake_factor=0.3, rcap_reduced=2
    → конфигурируется в daemon Challenge Mode, не здесь
"""

import json
import math

H4_SEC = 4 * 3600
D1_SEC = 24 * 3600
M30_SEC = 30 * 60

JPY_SYMS = {"USDJPY", "EURJPY", "AUDJPY", "CADJPY", "NZDJPY", "CHFJPY", "GBPJPY"}


# ── Утилиты ───────────────────────────────────────────────────────────────────

def _pip(sym):
    return 0.01 if sym in JPY_SYMS else 0.0001


def _wilder_atr(bars, period=14):
    """Wilder ATR (alpha = 1/period). Возвращает nan если данных недостаточно."""
    n = len(bars)
    if n < period + 1:
        return float("nan")
    trs = []
    for i in range(1, n):
        h = bars[i]["high"]
        l = bars[i]["low"]
        pc = bars[i - 1]["close"]
        trs.append(max(h - l, abs(h - pc), abs(l - pc)))
    if len(trs) < period:
        return float("nan")
    atr = sum(trs[:period]) / period
    for i in range(period, len(trs)):
        atr = (atr * (period - 1) + trs[i]) / period
    return atr


def _vol_ma(bars, period=20):
    if len(bars) < period:
        return float("nan")
    return sum(b["volume"] for b in bars[-period:]) / period


def _resample(m30_bars, tf_sec):
    """
    Ресемплирует M30 бары в старший TF.
    Возвращает только ЗАКРЫТЫЕ бакеты (последний дропается — может быть неполным).
    """
    buckets = {}
    order = []
    for b in m30_bars:
        bk = (b["time"] // tf_sec) * tf_sec
        if bk not in buckets:
            buckets[bk] = {
                "time":   bk,
                "open":   b["open"],
                "high":   b["high"],
                "low":    b["low"],
                "close":  b["close"],
                "volume": b["volume"],
            }
            order.append(bk)
        else:
            e = buckets[bk]
            e["high"]   = max(e["high"],  b["high"])
            e["low"]    = min(e["low"],   b["low"])
            e["close"]  = b["close"]
            e["volume"] += b["volume"]
    # Последний бакет может быть незакрытым — убираем
    if len(order) < 2:
        return []
    return [buckets[bk] for bk in order[:-1]]


def _ema_last(values, period, lb=0):
    """
    Вычисляет EMA(period) по списку значений.
    Возвращает (ema_now, ema_lb) или (nan, nan) если данных недостаточно.
    lb — отступ от конца (для slope: lb=3 → ema[-1] vs ema[-1-lb]).
    """
    n = len(values)
    if n < period + lb:
        return float("nan"), float("nan")
    k = 2.0 / (period + 1)
    ema = sum(values[:period]) / period
    results = [ema]
    for i in range(period, n):
        ema = values[i] * k + ema * (1.0 - k)
        results.append(ema)
    # results[0] = EMA на period-1 (индекс), results[-1] = последний
    if len(results) <= lb:
        return float("nan"), float("nan")
    return results[-1], results[-1 - lb]


def _fractal_pivots(bars, k=3, swing_mult=0.75, atr_period=14):
    """
    Фрактальные пивоты на bars (H4).
    Возвращает список {"time", "price", "type"} (type=1 HIGH, -1 LOW),
    с принудительным чередованием и swing-фильтром.
    Только пивоты, подтверждённые k барами справа.
    """
    n = len(bars)
    if n < 2 * k + 1 + atr_period:
        return []
    atr = _wilder_atr(bars, atr_period)
    if math.isnan(atr) or atr <= 0:
        return []

    highs = [b["high"]  for b in bars]
    lows  = [b["low"]   for b in bars]

    pivots = []
    last_type  = 0
    last_price = 0.0

    # Проверяем индексы [k .. n-k-1] (нужно k баров справа)
    for i in range(k, n - k):
        hi = highs[i]
        lo = lows[i]

        is_high = (all(hi > highs[i - j] for j in range(1, k + 1)) and
                   all(hi >= highs[i + j] for j in range(1, k + 1)))
        is_low  = (all(lo < lows[i - j]  for j in range(1, k + 1)) and
                   all(lo <= lows[i + j]  for j in range(1, k + 1)))

        for ptype, price, flag in ((1, hi, is_high), (-1, lo, is_low)):
            if not flag:
                continue
            if pivots:
                swing_ok = abs(price - last_price) >= swing_mult * atr
                same_type = ptype == last_type
                if same_type:
                    # Обновляем если лучше (HH выше / LL ниже)
                    better = (ptype == 1 and price > pivots[-1]["price"]) or \
                             (ptype == -1 and price < pivots[-1]["price"])
                    if better:
                        pivots[-1] = {"time": bars[i]["time"],
                                      "price": price, "type": ptype}
                        last_price = price
                    continue
                if not swing_ok:
                    # Swing фильтр: не добавляем новый, но обновляем тот же тип
                    if last_type == ptype:
                        better = (ptype == 1 and price > pivots[-1]["price"]) or \
                                 (ptype == -1 and price < pivots[-1]["price"])
                        if better:
                            pivots[-1]["time"]  = bars[i]["time"]
                            pivots[-1]["price"] = price
                            last_price = price
                    continue

            pivots.append({"time": bars[i]["time"], "price": price, "type": ptype})
            last_type  = ptype
            last_price = price

    return pivots


def _get_regime(pivots):
    """
    Определяет режим из списка пивотов.
    Возвращает (regime, zone_price, zone_ts):
      +1 / -1 / 0;  zone_price — уровень зоны;  zone_ts — время H4 бара зоны.
    """
    highs = [p for p in pivots if p["type"] ==  1]
    lows  = [p for p in pivots if p["type"] == -1]

    if len(highs) < 2 or len(lows) < 2:
        return 0, float("nan"), 0

    h1, h2 = highs[-1], highs[-2]  # h1 — новее
    l1, l2 = lows[-1],  lows[-2]   # l1 — новее

    if h1["price"] > h2["price"] and l1["price"] > l2["price"]:
        # UPTREND: зона = последний HL = l1
        return 1, l1["price"], l1["time"]
    elif h1["price"] < h2["price"] and l1["price"] < l2["price"]:
        # DOWNTREND: зона = последний LH = h1
        return -1, h1["price"], h1["time"]

    return 0, float("nan"), 0


# ── Стратегия ─────────────────────────────────────────────────────────────────

class Strategy:

    def __init__(self, config: dict):
        p       = config.get("params", {})
        combos  = config.get("combos", [])

        self.syms       = []
        self.timeframes = {}
        self._sym_params = {}

        for c in combos:
            sym  = c["sym"]
            self.syms.append(sym)

            # Берём первый найденный direction (стратегия симметрична)
            strat = {}
            for dk in ("BOTH", "LONG", "SHORT"):
                if dk in c.get("directions", {}):
                    strat = c["directions"][dk].get("strat", {})
                    break

            tf = strat.get("tf", "M30")
            self.timeframes[sym] = tf

            self._sym_params[sym] = {
                "zone_buf":  strat.get("zone_buf",  1.0),
                "d1_filter": strat.get("d1_filter", False),
                "d1_lb":     strat.get("d1_lb",     3),
                "pip_size":  _pip(sym),
            }

        # Глобальные параметры
        self._fractal_k    = p.get("fractal_k",    3)
        self._swing_atr    = p.get("swing_atr",    0.75)
        self._sl_buf       = p.get("sl_buf",       0.2)
        self._tp_r         = p.get("tp_r",         5.0)
        self._atr_period   = p.get("atr_period",   14)
        self._vol_period   = p.get("vol_period",   20)
        self._ema_period   = p.get("ema_period",   21)
        self._timeout_bars = p.get("timeout_bars", 48)
        self._hbars        = p.get("history_bars", 1200)
        self._rcap         = p.get("r_cap",        3)

        # Состояние: ts зоны (H4), от которой уже был вход (deduplicate)
        self._last_zone_ts: dict = {s: 0 for s in self.syms}

    # ─────────────────────────────────────────────────────────────────────────

    def get_requirements(self) -> dict:
        return {
            "symbols":      self.syms,
            "timeframes":   self.timeframes,
            "history_bars": self._hbars,
            "r_cap":        self._rcap,
        }

    # ─────────────────────────────────────────────────────────────────────────

    def on_bars(self, bars: dict, positions: list) -> list:
        actions = []

        # Символы с открытой позицией
        active_syms = {pos["symbol"] for pos in positions}

        # Timeout: EXIT позиций, которые висят дольше timeout_bars
        for pos in positions:
            sym = pos["symbol"]
            raw = bars.get(sym)
            if not raw:
                continue
            cur_ts  = raw[-1]["time"]
            open_ts = pos["open_time"]
            elapsed = (cur_ts - open_ts) // M30_SEC
            if elapsed >= self._timeout_bars:
                actions.append({"action": "EXIT", "ticket": pos["ticket"]})

        # Новые сигналы — только если нет открытой позиции по символу
        for sym in self.syms:
            if sym in active_syms:
                continue
            raw = bars.get(sym)
            if not raw:
                continue
            sig = self._check_signal(sym, raw)
            if sig is None:
                continue
            # Обновляем deduplicate-метку
            self._last_zone_ts[sym] = sig["zone_ts"]
            actions.append({
                "action":      "ENTER",
                "symbol":      sym,
                "direction":   sig["direction"],
                "sl_price":    round(sig["sl_price"], 6),
                "tp_price":    round(sig["tp_price"], 6),
                "comment":     f"fst_{sig['direction'].lower()}",
                "signal_data": json.dumps({
                    "sl_dist": round(sig["sl_dist"], 6),
                    "tp_r":    float(self._tp_r),
                }),
            })

        return actions

    # ─────────────────────────────────────────────────────────────────────────

    def _check_signal(self, sym: str, m30_bars: list):
        """
        Проверяет условия входа для символа на последнем закрытом M30 баре.
        Возвращает dict с параметрами или None.
        """
        p = self._sym_params[sym]

        if len(m30_bars) < 50:
            return None

        cur = m30_bars[-1]

        # ── Volume confirm ────────────────────────────────────────────────────
        vma = _vol_ma(m30_bars, self._vol_period)
        if math.isnan(vma) or vma <= 0 or cur["volume"] <= vma:
            return None

        # ── M30 ATR ───────────────────────────────────────────────────────────
        atr_m30 = _wilder_atr(m30_bars, self._atr_period)
        if math.isnan(atr_m30) or atr_m30 <= 0:
            return None

        # ── H4 структура ─────────────────────────────────────────────────────
        h4_bars = _resample(m30_bars, H4_SEC)
        # Минимум: 2*k + 1 + atr_period + 4 пивота (2H + 2L)
        if len(h4_bars) < 2 * self._fractal_k + 1 + self._atr_period + 8:
            return None

        pivots = _fractal_pivots(h4_bars, self._fractal_k,
                                  self._swing_atr, self._atr_period)
        regime, zone_price, zone_ts = _get_regime(pivots)

        if regime == 0 or math.isnan(zone_price) or zone_ts == 0:
            return None

        # ── Deduplication: эта зона уже использовалась ───────────────────────
        if self._last_zone_ts.get(sym, 0) == zone_ts:
            return None

        # ── D1 EMA slope filter (только для DAX40, XAUUSD) ───────────────────
        if p["d1_filter"]:
            d1_bars = _resample(m30_bars, D1_SEC)
            if len(d1_bars) < self._ema_period + p["d1_lb"] + 2:
                return None
            closes_d1 = [b["close"] for b in d1_bars]
            ema_now, ema_prev = _ema_last(closes_d1, self._ema_period, lb=p["d1_lb"])
            if math.isnan(ema_now) or math.isnan(ema_prev):
                return None
            if regime == 1 and ema_now <= ema_prev:
                return None
            if regime == -1 and ema_now >= ema_prev:
                return None

        # ── Entry conditions ──────────────────────────────────────────────────
        zb   = p["zone_buf"]
        bmin = 20.0 * p["pip_size"]   # минимальный SL (20 пипов для FX)

        if regime == 1:   # UPTREND → LONG
            # Касание зоны снизу + закрытие выше неё
            if not (cur["low"] <= zone_price + zb * atr_m30 and
                    cur["close"] > zone_price):
                return None
            sl_dist  = max(cur["close"] - (zone_price - self._sl_buf * atr_m30), bmin)
            sl_price = cur["close"] - sl_dist
            tp_price = cur["close"] + self._tp_r * sl_dist
            direction = "LONG"

        else:             # DOWNTREND → SHORT
            # Касание зоны сверху + закрытие ниже неё
            if not (cur["high"] >= zone_price - zb * atr_m30 and
                    cur["close"] < zone_price):
                return None
            sl_dist  = max((zone_price + self._sl_buf * atr_m30) - cur["close"], bmin)
            sl_price = cur["close"] + sl_dist
            tp_price = cur["close"] - self._tp_r * sl_dist
            direction = "SHORT"

        return {
            "direction": direction,
            "sl_price":  sl_price,
            "tp_price":  tp_price,
            "sl_dist":   sl_dist,
            "zone_ts":   zone_ts,
        }

    # ─────────────────────────────────────────────────────────────────────────

    def save_state(self) -> dict:
        return {
            "last_zone_ts": {s: int(v) for s, v in self._last_zone_ts.items()},
        }

    def restore_state(self, state: dict):
        if not state:
            return
        self._last_zone_ts = {
            s: int(v)
            for s, v in state.get("last_zone_ts", {}).items()
        }
