"""
double_rsi_ha  v1.1
===================
Double RSI & HA — H4 trend-following strategy.

Логика:
  Сигнальный TF : H4 (ресемплируется из M30 внутри)
  Индикаторы    : HA-close → RSI(p1) fast, RSI(p2) slow, SMA(sma_p) of RSI(p1)
  Сигнал LONG   : RSI_fast пересекает вверх min(RSI_slow, SMA) + th  (sig=ALL)
                  RSI_fast пересекает вверх max(RSI_slow, SMA) + th  (sig=STRONG)
  Сигнал SHORT  : симметрично
  SL            : signal_bar.close ± sl_mult × ATR(14)
  TP            : entry + sl_dist × tp_ratio  (≈ от signal_bar.close)
  Вход          : market, next bar open

v1.1: numpy indicators to match research float64 precision exactly.
"""

import json
import numpy as np


# ── Timeframe constants ────────────────────────────────────────────────────────
TF_M30_SEC = 1800
TF_H4_SEC  = 14400


# ── Numpy indicator functions ──────────────────────────────────────────────────

def _ha_close_np(o, h, l, c):
    """Heikin-Ashi close and open. Returns (ha_open, ha_close) as numpy arrays."""
    n = len(o)
    hac = np.empty(n); hao = np.empty(n)
    hac[0] = (o[0] + h[0] + l[0] + c[0]) * 0.25
    hao[0] = (o[0] + c[0]) * 0.5
    for i in range(1, n):
        hac[i] = (o[i] + h[i] + l[i] + c[i]) * 0.25
        hao[i] = (hao[i-1] + hac[i-1]) * 0.5
    return hao, hac


def _wilder_rsi_np(prices, period):
    """Wilder RSI. Returns numpy array, NaN for first `period` bars."""
    n = len(prices)
    out = np.full(n, np.nan)
    if n < period + 1:
        return out
    ag = al = 0.0
    for i in range(1, period + 1):
        d = prices[i] - prices[i-1]
        if d > 0: ag += d
        else:     al -= d
    ag /= period; al /= period
    out[period] = 100.0 if al == 0.0 else 100.0 - 100.0 / (1.0 + ag / al)
    for i in range(period + 1, n):
        d = prices[i] - prices[i-1]
        g = d if d > 0.0 else 0.0
        ls = -d if d < 0.0 else 0.0
        ag = (ag * (period - 1) + g) / period
        al = (al * (period - 1) + ls) / period
        out[i] = 100.0 if al == 0.0 else 100.0 - 100.0 / (1.0 + ag / al)
    return out


def _sma_np(arr, period):
    """SMA on numpy array that may contain NaNs at start."""
    n = len(arr)
    out = np.full(n, np.nan)
    start = 0
    while start < n and np.isnan(arr[start]):
        start += 1
    if start + period > n:
        return out
    s = arr[start:start + period].sum()
    out[start + period - 1] = s / period
    for i in range(start + period, n):
        s += arr[i] - arr[i - period]
        out[i] = s / period
    return out


def _atr_np(h, l, c, period):
    """Wilder ATR. Returns numpy array, NaN for first `period` bars."""
    n = len(c)
    out = np.full(n, np.nan)
    if n < period + 1:
        return out
    s = 0.0
    for i in range(1, period + 1):
        tr = h[i] - l[i]
        a = abs(h[i] - c[i-1]); b = abs(l[i] - c[i-1])
        if a > tr: tr = a
        if b > tr: tr = b
        s += tr
    out[period] = s / period
    for i in range(period + 1, n):
        tr = h[i] - l[i]
        a = abs(h[i] - c[i-1]); b = abs(l[i] - c[i-1])
        if a > tr: tr = a
        if b > tr: tr = b
        out[i] = (out[i-1] * (period - 1) + tr) / period
    return out


def _resample_h4(m30_bars):
    """Resample list of M30 bar dicts to H4. Returns list of bar dicts."""
    if not m30_bars:
        return []
    h4 = {}
    for b in m30_bars:
        bkt = (b["time"] // TF_H4_SEC) * TF_H4_SEC
        if bkt not in h4:
            h4[bkt] = {"time": bkt, "open": b["open"],
                       "high": b["high"], "low": b["low"], "close": b["close"]}
        else:
            if b["high"] > h4[bkt]["high"]: h4[bkt]["high"] = b["high"]
            if b["low"]  < h4[bkt]["low"]:  h4[bkt]["low"]  = b["low"]
            h4[bkt]["close"] = b["close"]
    return [h4[k] for k in sorted(h4)]


def _price_digits(sym):
    return 3 if "JPY" in sym else 5


# ── Strategy class ─────────────────────────────────────────────────────────────

class Strategy:

    def __init__(self, config: dict):
        p = config.get("params", {})
        self._atr_period = int(p.get("atr_period", 14))
        self._sl_mult    = float(p.get("sl_mult", 2.0))
        self._tp_ratio   = float(p.get("tp_ratio", 3.0))
        self._hbars      = int(p.get("history_bars", 3200))
        self._rcap       = p.get("r_cap", None)

        self._sym_params = {}
        self._timeframes = {}

        for c in config.get("combos", []):
            sym = c["sym"]
            for dk in ("BOTH", "LONG", "SHORT"):
                if dk in c.get("directions", {}):
                    st = c["directions"][dk].get("strat", {})
                    self._sym_params[sym] = {
                        "p1":       int(st.get("p1",   25)),
                        "p2":       int(st.get("p2",  100)),
                        "sma_p":    int(st.get("sma_p", 150)),
                        "th":     float(st.get("th",   1.5)),
                        "sig_type": str(st.get("sig_type", "ALL")),
                    }
                    self._timeframes[sym] = "M30"
                    break

        self._syms = list(self._sym_params.keys())
        self._last_signal_h4_ts = {sym: 0 for sym in self._syms}

    # ── Protocol ──────────────────────────────────────────────────────────────

    def get_requirements(self) -> dict:
        return {
            "symbols":      self._syms,
            "timeframes":   self._timeframes,
            "history_bars": self._hbars,
            "r_cap":        self._rcap,
        }

    def on_bars(self, bars: dict, positions: list) -> list:
        actions   = []
        open_syms = {p["symbol"] for p in positions}

        for sym in self._syms:
            raw = bars.get(sym)
            if not raw or len(raw) < 2:
                continue

            last_m30_ts = raw[-1]["time"]
            if (last_m30_ts % TF_H4_SEC) != (TF_H4_SEC - TF_M30_SEC):
                continue

            h4 = _resample_h4(raw)
            if len(h4) < 3:
                continue

            h4_bar_ts = h4[-1]["time"]
            if h4_bar_ts <= self._last_signal_h4_ts[sym]:
                continue
            if sym in open_syms:
                continue

            sp       = self._sym_params[sym]
            p1       = sp["p1"];  p2    = sp["p2"]
            sma_p    = sp["sma_p"]; th  = sp["th"]
            sig_type = sp["sig_type"]

            min_bars = p2 + sma_p + 5
            if len(h4) < min_bars:
                continue

            # Build numpy arrays from H4 bar list
            n4 = len(h4)
            o4 = np.array([b["open"]  for b in h4], dtype=np.float64)
            h4a= np.array([b["high"]  for b in h4], dtype=np.float64)
            l4 = np.array([b["low"]   for b in h4], dtype=np.float64)
            c4 = np.array([b["close"] for b in h4], dtype=np.float64)

            _, hac   = _ha_close_np(o4, h4a, l4, c4)
            rsi_f    = _wilder_rsi_np(hac, p1)
            rsi_s    = _wilder_rsi_np(hac, p2)
            sma_r    = _sma_np(rsi_f, sma_p)
            atr      = _atr_np(h4a, l4, c4, self._atr_period)

            i  = n4 - 1
            i1 = n4 - 2

            if (np.isnan(rsi_f[i])  or np.isnan(rsi_s[i])  or np.isnan(sma_r[i])  or
                np.isnan(rsi_f[i1]) or np.isnan(rsi_s[i1]) or np.isnan(sma_r[i1]) or
                np.isnan(atr[i])):
                continue

            cur_atr = float(atr[i])
            if cur_atr <= 0.0:
                continue

            rf_i = float(rsi_f[i]);  rs_i = float(rsi_s[i]);  sm_i = float(sma_r[i])
            rf_p = float(rsi_f[i1]); rs_p = float(rsi_s[i1]); sm_p = float(sma_r[i1])

            mn_i = min(rs_i, sm_i); mx_i = max(rs_i, sm_i)
            mn_p = min(rs_p, sm_p); mx_p = max(rs_p, sm_p)

            direction = None
            if sig_type == "ALL":
                if   rf_i >= mn_i + th and rf_p < mn_p + th: direction = "LONG"
                elif rf_i <= mx_i - th and rf_p > mx_p - th: direction = "SHORT"
            else:
                if   rf_i >= mx_i + th and rf_p < mx_p + th: direction = "LONG"
                elif rf_i <= mn_i - th and rf_p > mn_p - th: direction = "SHORT"

            if direction is None:
                continue

            signal_close = float(c4[i])
            sl_dist      = self._sl_mult * cur_atr
            digits       = _price_digits(sym)

            if direction == "LONG":
                sl_price = round(signal_close - sl_dist, digits)
                tp_price = round(signal_close + sl_dist * self._tp_ratio, digits)
            else:
                sl_price = round(signal_close + sl_dist, digits)
                tp_price = round(signal_close - sl_dist * self._tp_ratio, digits)

            signal_data = json.dumps({
                "sl_dist":   round(sl_dist, 6),
                "tp_r":      self._tp_ratio,
                "atr":       round(cur_atr, 6),
                "h4_bar_ts": int(h4_bar_ts),
                "rsi_fast":  round(rf_i, 2),
                "rsi_slow":  round(rs_i, 2),
                "sma_rsi":   round(sm_i, 2),
            })

            actions.append({
                "action":      "ENTER",
                "symbol":      sym,
                "direction":   direction,
                "sl_price":    sl_price,
                "tp_price":    tp_price,
                "comment":     f"drsi_ha_{direction.lower()}",
                "signal_data": signal_data,
            })

            self._last_signal_h4_ts[sym] = int(h4_bar_ts)

        return actions

    # ── State persistence ─────────────────────────────────────────────────────

    def save_state(self) -> dict:
        return {
            "last_signal_h4_ts": {
                sym: int(ts)
                for sym, ts in self._last_signal_h4_ts.items()
            }
        }

    def restore_state(self, state: dict):
        if not state:
            return
        saved = state.get("last_signal_h4_ts", {})
        for sym in self._syms:
            if sym in saved:
                self._last_signal_h4_ts[sym] = int(saved[sym])
