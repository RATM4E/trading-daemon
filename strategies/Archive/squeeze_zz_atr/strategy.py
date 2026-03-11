"""
squeeze_zz_atr — strategy.py  v1.0
====================================
Daemon-compatible wrapper вокруг strategy_core.SqueezeZZATRState.

Архитектура:
  - Демон присылает M5 бары для всех 29 символов
  - Каждый символ может иметь 1 или 2 экземпляра SqueezeZZATRState (M20 и/или M30)
  - Ресемплинг M5 → M20/M30 выполняется внутри Strategy через накопительный буфер
  - Дедупликация ENTER_PENDING: флаг per (sym, tf_sec) — не шлём второй ордер
    пока первый не отменён/исполнен
  - signal_data содержит sl_dist для корректного R-расчёта в демоне

Параметры per sym×TF читаются из combos[].directions.BOTH.strat:
  tf_bars, lb_hours, dp, zz_depth, zz_dev, zz_back, slope_flat,
  sl_cap_atr, tmo_order, tmo_position, prot_trigger, prot_target,
  trail_act_r, trail_mult

Протокол демона:
  TICK → on_bars() → [ENTER_PENDING (BUY_STOP+SELL_STOP), CANCEL_PENDING, EXIT, MODIFY_SL]
"""

import json
import math
from collections import defaultdict
from typing import Optional

from strategy_core import (
    SqueezeZZATRState, Bar,
    EXIT_SL, EXIT_TP, EXIT_TRAIL, EXIT_TIMEOUT,
)

# ─── КОНСТАНТЫ ───────────────────────────────────────────────────────────────
M5_SEC = 300
ATR_PERIOD_DEFAULT = 14
RATIO_THRESH_DEFAULT = 0.85
MAX_ATTEMPTS_DEFAULT = 3


# ─── РЕСЕМПЛЕР M5 → TF ───────────────────────────────────────────────────────
class M5Resampler:
    """
    Накапливает M5 бары и выдаёт закрытые TF-бары.
    Бар считается закрытым когда приходит первый M5 с новым bucket-ом.
    bucket = epoch // tf_sec
    """
    def __init__(self, tf_sec: int):
        self.tf_sec = tf_sec
        self._cur_bucket: Optional[int] = None
        self._o = self._h = self._l = self._c = 0.0
        self._ts = 0

    def push(self, bar: dict) -> Optional[Bar]:
        """
        Принимает M5 bar dict (ключи: time, open, high, low, close).
        Возвращает закрытый TF-бар если бакет сменился, иначе None.
        """
        ts     = int(bar["time"])
        bucket = ts // self.tf_sec
        closed = None

        if self._cur_bucket is None:
            # Первый бар
            self._cur_bucket = bucket
            self._o = bar["open"]
            self._h = bar["high"]
            self._l = bar["low"]
            self._c = bar["close"]
            self._ts = bucket * self.tf_sec
        elif bucket != self._cur_bucket:
            # Новый бакет → предыдущий закрыт
            closed = Bar(
                ts    = self._ts,
                open  = self._o,
                high  = self._h,
                low   = self._l,
                close = self._c,
            )
            self._cur_bucket = bucket
            self._o = bar["open"]
            self._h = bar["high"]
            self._l = bar["low"]
            self._c = bar["close"]
            self._ts = bucket * self.tf_sec
        else:
            # Тот же бакет → накапливаем
            if bar["high"] > self._h: self._h = bar["high"]
            if bar["low"]  < self._l: self._l = bar["low"]
            self._c = bar["close"]

        return closed

    def save(self) -> dict:
        return {
            "tf_sec": self.tf_sec,
            "cur_bucket": self._cur_bucket,
            "o": self._o, "h": self._h, "l": self._l, "c": self._c,
            "ts": self._ts,
        }

    def restore(self, d: dict):
        self._cur_bucket = d.get("cur_bucket")
        self._o = d.get("o", 0.0)
        self._h = d.get("h", 0.0)
        self._l = d.get("l", 0.0)
        self._c = d.get("c", 0.0)
        self._ts = d.get("ts", 0)


# ─── СТРАТЕГИЯ ───────────────────────────────────────────────────────────────
class Strategy:

    def __init__(self, config: dict):
        p = config.get("params", {})
        self._atr_period   = int(p.get("atr_period",   ATR_PERIOD_DEFAULT))
        self._ratio_thresh = float(p.get("ratio_thresh", RATIO_THRESH_DEFAULT))
        self._max_attempts = int(p.get("max_attempts",  MAX_ATTEMPTS_DEFAULT))

        # Инициализируем по одному SqueezeZZATRState на каждый sym×TF
        # Ключ: (sym, tf_sec)
        self._states:     dict[tuple, SqueezeZZATRState] = {}
        self._resamplers: dict[tuple, M5Resampler]        = {}

        # Дедупликация: (sym, tf_sec) → True если активный pending OCO отправлен
        self._has_pending: dict[tuple, bool] = {}

        # Маппинг ticket → (sym, tf_sec) для управления позициями
        self._ticket_map: dict[int, tuple] = {}

        # Список всех символов для get_requirements
        self._syms: set[str] = set()

        for combo in config.get("combos", []):
            sym    = combo["sym"]
            tf_sec = int(combo["directions"]["BOTH"]["strat"]["tf_bars"])
            s      = combo["directions"]["BOTH"]["strat"]

            self._syms.add(sym)

            key = (sym, tf_sec)
            self._resamplers[key] = M5Resampler(tf_sec)
            self._states[key] = SqueezeZZATRState(
                symbol          = sym,
                tf              = f"M{tf_sec // 60}",
                atr_period      = self._atr_period,
                lb_hours        = float(s["lb_hours"]),
                dp              = float(s["dp"]),
                zz_depth        = int(s["zz_depth"]),
                zz_deviation    = int(s["zz_dev"]),
                zz_backstep     = int(s["zz_back"]),
                slope_flat_frac = float(s["slope_flat"]),
                ratio_thresh    = self._ratio_thresh,
                sl_cap_atr      = float(s["sl_cap_atr"]),
                tmo_order       = int(s["tmo_order"]),
                tmo_position    = int(s["tmo_position"]),
                max_attempts    = self._max_attempts,
                trail_mult      = float(s["trail_mult"]),
                trail_act_r     = float(s["trail_act_r"]),
                cost            = 0.0,   # демон управляет cost через sizing
                point_size      = 0.00001,
                min_sl_dist     = 0.0,
            )
            # Протектор хранится per-state отдельно
            self._states[key]._prot_trigger = float(s["prot_trigger"])
            self._states[key]._prot_target  = float(s["prot_target"])
            self._has_pending[key] = False

    # ─── ТРЕБОВАНИЯ ──────────────────────────────────────────────────────────
    def get_requirements(self) -> dict:
        return {
            "symbols":      sorted(self._syms),
            "timeframes":   {sym: "M5" for sym in self._syms},
            "history_bars": 2000,   # M5 баров — достаточно для 2× lb_hours любого символа
        }

    # ─── ГЛАВНЫЙ МЕТОД ───────────────────────────────────────────────────────
    def on_bars(self, bars: dict, positions: list) -> list:
        actions = []

        # Строим маппинг ticket → key из текущих позиций
        # (сохраняем для управления позицией — MODIFY_SL при трейле)
        active_tickets = set()
        for pos in positions:
            sig = pos.get("signal_data")
            if not sig:
                continue
            try:
                sd = json.loads(sig)
                key = (pos["symbol"], int(sd.get("tf_sec", 0)))
                if key in self._states:
                    self._ticket_map[pos["ticket"]] = key
                    active_tickets.add(pos["ticket"])
            except Exception:
                pass

        # Очищаем устаревшие тикеты
        for t in list(self._ticket_map):
            if t not in active_tickets:
                del self._ticket_map[t]

        # Обновляем _has_pending по текущим позициям и ордерам
        # Если позиция по ключу есть → pending точно нет (исполнен)
        active_keys = set(self._ticket_map.values())

        # Обрабатываем символы
        for sym in self._syms:
            raw = bars.get(sym)
            if not raw:
                continue

            for m5_bar in raw:
                for key, resampler in self._resamplers.items():
                    if key[0] != sym:
                        continue

                    tf_bar = resampler.push(m5_bar)
                    if tf_bar is None:
                        continue

                    state = self._states[key]

                    # Позиция по этому sym×TF активна?
                    key_has_pos = key in active_keys

                    # Синхронизируем внутреннее состояние strategy_core
                    # с реальным состоянием демона (позиция могла закрыться
                    # пока стратегия была оффлайн)
                    if not key_has_pos and state.active_pos is not None:
                        state.active_pos = None

                    # Если есть активная позиция — управляем через MODIFY_SL
                    if key_has_pos:
                        result = state.on_bar(tf_bar)
                        if result and result.get("action") == "EXIT":
                            # Демон закроет по SL/TP сам; если это таймаут —
                            # шлём EXIT для принудительного закрытия
                            if result.get("reason") == EXIT_TIMEOUT:
                                ticket = self._find_ticket(key)
                                if ticket:
                                    actions.append({
                                        "type":   "EXIT",
                                        "ticket": ticket,
                                        "reason": "timeout",
                                    })
                        elif result and result.get("action") in ("FILLED_LONG", "FILLED_SHORT"):
                            # strategy_core думает что только что открылась позиция —
                            # это нормально при ресинхронизации, игнорируем
                            pass
                        # MODIFY_SL при трейле: state.active_pos.sl обновляется внутри
                        # Шлём MODIFY_SL если SL изменился
                        if state.active_pos is not None:
                            ticket = self._find_ticket(key)
                            if ticket:
                                # Находим текущий SL из positions
                                cur_sl = self._get_pos_sl(positions, ticket)
                                new_sl = state.active_pos.sl
                                if cur_sl is not None and abs(new_sl - cur_sl) > 1e-8:
                                    actions.append({
                                        "type":     "MODIFY_SL",
                                        "ticket":   ticket,
                                        "sl_price": float(new_sl),
                                    })
                        continue

                    # Нет позиции — ищем сигнал
                    result = state.on_bar(tf_bar)
                    if result is None:
                        continue

                    action = result.get("action")

                    if action == "CANCEL_OCO":
                        if self._has_pending[key]:
                            # Найти pending ticket по signal_data
                            # Демон отменит ордер через CANCEL_PENDING
                            # (требует ticket — ищем в positions)
                            # В текущей архитектуре pending_orders не приходят в on_bars
                            # Сбрасываем флаг — следующий тик создаст новый OCO если нужно
                            self._has_pending[key] = False

                    elif action == "PLACE_OCO":
                        if self._has_pending[key]:
                            # Уже есть активный pending — не дублируем
                            continue

                        sl_dist_long  = result.get("sl_long",   0)
                        sl_dist_short = result.get("sl_short",  0)
                        buy_stop      = result.get("buy_stop",  0)
                        sell_stop     = result.get("sell_stop", 0)
                        structure     = result.get("structure_name", "")

                        # signal_data для R-расчёта и идентификации
                        # signal_data различается по направлению — sl_dist разный.
                        # TODO: демон должен искать OCO-сиблингов НЕ по полному
                        # совпадению signal_data, а по отдельному полю oco_group.
                        # См. daemon_todo.md
                        sd_long = json.dumps({
                            "tf_sec":    key[1],
                            "sl_dist":   float(state.active_oco.sl_dist_long),
                            "structure": structure,
                            "attempt":   int(state.active_oco.attempt),
                        })
                        sd_short = json.dumps({
                            "tf_sec":    key[1],
                            "sl_dist":   float(state.active_oco.sl_dist_short),
                            "structure": structure,
                            "attempt":   int(state.active_oco.attempt),
                        })

                        # tmo_order в M5 барах → в TF барах (для демона в барах TF не нужно,
                        # демон считает в минутах или барах по настройке — передаём в TF-барах)
                        tmo_bars = int(state.tmo_order)

                        actions.append({
                            "type":          "ENTER_PENDING",
                            "symbol":        sym,
                            "order_type":    "BUY_STOP",
                            "entry_price":   float(buy_stop),
                            "sl_price":      float(state.active_oco.sl_long),
                            "tp_price":      float(state.active_oco.sl_long + state.active_oco.sl_dist_long * 2),
                            "timeout_bars":  tmo_bars,
                            "signal_data":   sd_long,
                            "oco_group":     f"{sym}_{key[1]}_{state.active_oco.placed_bar}",
                        })
                        actions.append({
                            "type":          "ENTER_PENDING",
                            "symbol":        sym,
                            "order_type":    "SELL_STOP",
                            "entry_price":   float(sell_stop),
                            "sl_price":      float(state.active_oco.sl_short),
                            "tp_price":      float(state.active_oco.sl_short - state.active_oco.sl_dist_short * 2),
                            "timeout_bars":  tmo_bars,
                            "signal_data":   sd_short,
                            "oco_group":     f"{sym}_{key[1]}_{state.active_oco.placed_bar}",
                        })
                        self._has_pending[key] = True

        return actions

    # ─── ВСПОМОГАТЕЛЬНЫЕ ─────────────────────────────────────────────────────
    def _find_ticket(self, key: tuple) -> Optional[int]:
        for ticket, k in self._ticket_map.items():
            if k == key:
                return ticket
        return None

    def _get_pos_sl(self, positions: list, ticket: int) -> Optional[float]:
        for pos in positions:
            if pos["ticket"] == ticket:
                return float(pos["sl"])
        return None

    # ─── СЕРИАЛИЗАЦИЯ ────────────────────────────────────────────────────────
    def save_state(self) -> dict:
        states = {}
        for key, state in self._states.items():
            states[f"{key[0]}_{key[1]}"] = state.save_state()

        resamplers = {}
        for key, rs in self._resamplers.items():
            resamplers[f"{key[0]}_{key[1]}"] = rs.save()

        return {
            "states":      states,
            "resamplers":  resamplers,
            "has_pending": {f"{k[0]}_{k[1]}": v for k, v in self._has_pending.items()},
            "ticket_map":  {str(t): f"{k[0]}_{k[1]}" for t, k in self._ticket_map.items()},
        }

    def restore_state(self, state: dict):
        if not state:
            return

        for key, core_state in self._states.items():
            skey = f"{key[0]}_{key[1]}"
            if skey in state.get("states", {}):
                core_state.restore_state(state["states"][skey])

        for key, rs in self._resamplers.items():
            skey = f"{key[0]}_{key[1]}"
            if skey in state.get("resamplers", {}):
                rs.restore(state["resamplers"][skey])

        for skey, val in state.get("has_pending", {}).items():
            sym, tf_s = skey.rsplit("_", 1)
            key = (sym, int(tf_s))
            if key in self._has_pending:
                self._has_pending[key] = bool(val)

        for ticket_s, skey in state.get("ticket_map", {}).items():
            sym, tf_s = skey.rsplit("_", 1)
            self._ticket_map[int(ticket_s)] = (sym, int(tf_s))
