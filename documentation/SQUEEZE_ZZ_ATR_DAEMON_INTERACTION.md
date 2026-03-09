# SQUEEZE_ZZ_ATR — Поведение стратегии и взаимодействие с демоном

**Версия:** 1.0  
**Дата:** Март 2026

---

## 1. Ключевое решение: нет pending-ордеров на MT5

OCO реализуется **внутри стратегии**. На MT5 никакие pending-ордера не размещаются.

- Стратегия держит `active_oco` — внутренний объект с двумя уровнями
- На каждом M5 баре стратегия сама проверяет пересечение уровней
- При пересечении → стандартный `ENTER` (маркет) → демон входит на открытии следующего бара
- Демон **не знает** про OCO. Для него это обычный вход

Если демон/стратегия упали или разрыв связи → на MT5 чисто, никаких висящих ордеров.

---

## 2. Запрос к демону (get_requirements)

```python
def get_requirements(self) -> dict:
    return {
        "symbols":      [...],          # все символы из config combos
        "timeframes":   {sym: "M5"},    # всегда M5 — стратегия ресемплирует внутри
        "history_bars": 7200,           # 25 дней M5-баров — достаточно для H4 + warmup
        "r_cap":        p["r_cap"],     # из params
    }
```

Стратегия запрашивает у демона **M5 бары** для всех символов. Ресемплинг в целевой TF
(M30/H1/H4) выполняется внутри стратегии через:

```python
bucket = calendar.timegm(dt.timetuple()) // TF_SECONDS[tf]
```

`calendar.timegm()` обязателен — `datetime.timestamp()` применяет локальный TZ.

---

## 3. Внутренний объект OCO

```python
@dataclass
class OcoState:
    buy_level:    float   # sq_hi — проецированная верхняя граница на баре пробоя
    sell_level:   float   # sq_lo — проецированная нижняя граница
    sl_long:      float   # SL для лонга (за zz_L1 - sl_buffer)
    sl_short:     float   # SL для шорта (за zz_H1 + sl_buffer)
    sl_dist_long: float   # расстояние SL для лонга (для R-расчёта)
    sl_dist_short:float   # расстояние SL для шорта
    atr_signal:   float   # ATR на баре пробоя (для trail)
    placed_bar:   int     # индекс бара (timestamp) когда выставлен OCO
    attempt:      int     # номер попытки (1..max_attempts)
    bars_waiting: int     # счётчик баров ожидания (для tmo_order)
```

Стратегия держит `self._oco: dict[str, OcoState | None]` — по одному на символ.

---

## 4. Конечный автомат на символ

```
IDLE
 │
 │ atr_compressed AND zz_structure ∈ {TRIANGLE_*, CHAOS}
 │ AND close > upper (пробой вверх) OR close < lower (пробой вниз)
 ▼
OCO_ACTIVE  ←─────────────────────────────────────────┐
 │                                                     │
 │ close[t] > buy_level  → ENTER LONG                 │  tmo_order истёк
 │ close[t] < sell_level → ENTER SHORT                │  attempt < max_attempts
 │                          ↓                         │  AND структура ещё валидна
 │                       IN_POSITION                  │
 │                          │                         └─ (новый OCO с attempt+1)
 │ tmo_order истёк          │ SL / trail / tmo_position
 │ attempt >= max_attempts  ▼
 └────────────────────── IDLE
```

**Правила переходов:**

- `IDLE → OCO_ACTIVE`: обнаружен пробой на закрытии бара. OCO выставляется на **следующем** баре.
- `OCO_ACTIVE → IN_POSITION`: close пересёк один из уровней → `ENTER` маркетом, `active_oco = None`.
- `OCO_ACTIVE → IDLE (retry)`: `bars_waiting >= tmo_order` И `attempt < max_attempts` И структура всё ещё валидна → `attempt += 1`, пересчитать уровни, перезапустить.
- `OCO_ACTIVE → IDLE (final)`: `bars_waiting >= tmo_order` И (`attempt >= max_attempts` ИЛИ структура сломана) → сброс.
- `IN_POSITION → IDLE`: позиция закрыта (SL / trail / tmo_position).

---

## 5. Логика on_bars (псевдокод)

```python
def on_bars(self, bars: dict, positions: list) -> list:
    actions = []

    for sym, oco in self._oco.items():
        raw = bars.get(sym)
        if not raw:
            continue

        # Ресемплинг M5 → целевой TF
        htf_bars = self._resample(raw, tf_sec)

        # Последний закрытый HTF-бар
        last_b = htf_bars[-1]["time"] // tf_sec
        closed = [b for b in htf_bars if b["time"] // tf_sec < last_b]
        if len(closed) < warmup:
            continue

        # --- Управление позицией ---
        pos = self._find_position(sym, positions)
        if pos:
            action = self._manage_position(sym, pos, closed[-1])
            if action:
                actions.append(action)
            continue   # пока в позиции — не смотрим на вход

        # --- OCO ожидает ---
        if self._oco[sym]:
            result = self._check_oco_trigger(sym, closed[-1])
            if result == "ENTER_LONG":
                actions.append(self._make_enter(sym, "LONG"))
                self._oco[sym] = None
            elif result == "ENTER_SHORT":
                actions.append(self._make_enter(sym, "SHORT"))
                self._oco[sym] = None
            elif result == "TIMEOUT":
                self._handle_timeout(sym, closed)
            continue

        # --- Детектор компрессии + пробой ---
        if self._detect_breakout(sym, closed):
            self._oco[sym] = self._build_oco(sym, closed)

    return actions
```

---

## 6. Проверка триггера OCO

```python
def _check_oco_trigger(self, sym: str, bar: dict) -> str | None:
    oco = self._oco[sym]
    oco.bars_waiting += 1

    # Пересечение уровня по закрытию бара (не по фитилю)
    if bar["close"] > oco.buy_level:
        return "ENTER_LONG"
    if bar["close"] < oco.sell_level:
        return "ENTER_SHORT"

    if oco.bars_waiting >= self.p["tmo_order"]:
        return "TIMEOUT"

    return None
```

---

## 7. Формирование ENTER action

```python
def _make_enter(self, sym: str, direction: str) -> dict:
    oco = self._oco[sym]

    if direction == "LONG":
        sl_price = oco.sl_long
        sl_dist  = oco.sl_dist_long
    else:
        sl_price = oco.sl_short
        sl_dist  = oco.sl_dist_short

    return {
        "action":    "ENTER",
        "symbol":    sym,
        "direction": direction,
        "sl_price":  round(sl_price, 5),
        "tp_price":  0,                   # нет TP
        "signal_data": json.dumps({
            "sl_dist":    float(sl_dist),
            "atr_signal": float(oco.atr_signal),
            "attempt":    oco.attempt,
        }),
    }
```

**Обязательные поля ENTER:**

| Поле | Тип | Описание |
|------|-----|----------|
| `action` | str | `"ENTER"` |
| `symbol` | str | Canonical symbol |
| `direction` | str | `"LONG"` / `"SHORT"` |
| `sl_price` | float | Уровень стоп-лосса |
| `tp_price` | float | `0` — нет TP |
| `signal_data` | str | `json.dumps({...})` — обязательно строка, не dict |

`signal_data` **обязан** содержать `sl_dist` — иначе R-расчёт в демоне вернёт 0.

---

## 8. Управление позицией (трейлинг)

```python
def _manage_position(self, sym: str, pos: dict, bar: dict) -> dict | None:
    sd = json.loads(pos["signal_data"])
    sl_dist    = sd["sl_dist"]
    entry      = pos["price_open"]
    direction  = pos["direction"]
    current_sl = pos["sl"]

    if direction == "LONG":
        mfe = bar["high"] - entry
        if mfe >= self.p["trail_act_r"] * sl_dist:
            new_sl = bar["high"] - self.p["trail_mult"] * sl_dist
            if new_sl > current_sl:
                return {
                    "action":   "MODIFY_SL",
                    "ticket":   pos["ticket"],
                    "sl_price": round(new_sl, 5),
                }
    else:
        mfe = entry - bar["low"]
        if mfe >= self.p["trail_act_r"] * sl_dist:
            new_sl = bar["low"] + self.p["trail_mult"] * sl_dist
            if new_sl < current_sl:
                return {
                    "action":   "MODIFY_SL",
                    "ticket":   pos["ticket"],
                    "sl_price": round(new_sl, 5),
                }

    # tmo_position — принудительный выход
    bars_in_trade = self._bars_since_open(pos, bar)
    if bars_in_trade >= self.p["tmo_position"]:
        return {
            "action": "EXIT",
            "ticket": pos["ticket"],
            "reason": "tmo_position",
        }

    return None
```

---

## 9. save_state / restore_state

Стратегия должна сохранять `_oco` при остановке чтобы после рестарта не потерять
активный OCO или счётчик попыток.

```python
def save_state(self) -> dict:
    return {
        "oco": {
            sym: (dataclasses.asdict(state) if state else None)
            for sym, state in self._oco.items()
        },
        "last_ts": {s: int(v) for s, v in self._last_ts.items()},
    }

def restore_state(self, state: dict):
    for sym, d in state.get("oco", {}).items():
        self._oco[sym] = OcoState(**d) if d else None
    self._last_ts = {s: int(v) for s, v in state.get("last_ts", {}).items()}
```

---

## 10. Чего НЕТ в протоколе

| Что | Почему |
|-----|--------|
| `ENTER_PENDING` | Не используется — нет реальных стоп-ордеров на MT5 |
| `oco_group` | Не нужен — демон не знает про OCO |
| `CANCEL_PENDING` | Не нужен — нет pending |
| Два одновременных `ENTER` | Запрещено — только один ENTER при пересечении уровня |

---

## 11. Граничные случаи

**Оба уровня пересечены на одном баре (шип через оба):**
Приоритет — первая проверка. В коде порядок: сначала `buy_level`, потом `sell_level`.
Можно определять по `close`: если `close > buy_level` → лонг.

**Позиция уже открыта при появлении нового сигнала:**
Блокируется. Пока `pos != None` — детектор не работает, новый OCO не создаётся.

**Рестарт демона при активном OCO:**
`restore_state()` восстанавливает `_oco`. Стратегия продолжает мониторинг с того же бара.
На MT5 при этом ничего нет — никакой рассинхронизации.

**Рестарт при открытой позиции:**
`positions` придут в следующем TICK из демона. Стратегия их получит и продолжит управление.
