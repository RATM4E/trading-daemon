# Strategy Config Schema v1.2

Единый формат `config.json` для всех стратегий. Обязателен к применению.

---

## Структура

```
{
  "strategy":     string    // имя стратегии (routing ключ)
  "version":      string    // версионирование
  "description":  string    // краткое описание для человека
  "params":       {}        // общие параметры, одинаковые для ВСЕХ символов
  "combos":       []        // массив символов с per-symbol настройками
}
```

## Правила

1. **`params`** — только параметры, общие для всех символов стратегии
2. **Per-symbol параметры** — живут внутри combo, не в params
3. **Directions** — объект с ключами `LONG` / `SHORT` / `BOTH`
4. **Внутри direction** — два блока: `strat` (для Python) и `daemon` (для C#)
5. **`aclass`** и **`tier`** — на уровне символа, не дублируются в direction
6. **`account`** — НЕ используется, sizing управляется через terminal profile + Sizing tab
7. **`r_cap`** — в `params`, передаётся демону через HELLO → `requirements.r_cap` → Gate 12

---

## Протокол действий (Actions)

Стратегия отвечает на каждый TICK массивом actions. Демон обрабатывает:

### ENTER — рыночный ордер

Немедленное исполнение по текущей цене (следующий бар в тестере).

```json
{
  "action":      "ENTER",
  "symbol":      "SP500",
  "direction":   "LONG",
  "sl_price":    5200.0,
  "tp_price":    5350.0,
  "signal_data": "{\"sl_dist\": 50.0, \"tp_r\": 2.0}"
}
```

### ENTER_PENDING — отложенный стоп-ордер

Размещает BUY_STOP или SELL_STOP. Тип определяется автоматически:
- `LONG` + `entry_price` > текущая цена → **BUY_STOP**
- `SHORT` + `entry_price` < текущая цена → **SELL_STOP**

```json
{
  "action":      "ENTER_PENDING",
  "symbol":      "SP500",
  "direction":   "LONG",
  "entry_price": 5280.0,
  "sl_price":    5200.0,
  "tp_price":    5450.0,
  "expiry_bars": 10,
  "signal_data": "{\"sl_dist\": 80.0, \"tp_r\": 2.0, \"oco_group\": \"cb_20240115_SP500\"}"
}
```

| Поле | Обязательно | Описание |
|------|-------------|----------|
| `entry_price` | ✅ | Уровень срабатывания стоп-ордера |
| `expiry_bars` | ❌ | Через сколько баров отменить если не исполнился. `null`/`0` = GTC |
| `sl_price` | ✅ | Стоп-лосс после исполнения |
| `tp_price` | ❌ | Тейк-профит после исполнения |
| `signal_data` | ❌ | JSON-метаданные; `sl_dist` обязателен для R-расчёта |

**Жизненный цикл (демон):**
1. Прошёл риск-гейты → MT5 ORDER_SEND (TRADE_ACTION_PENDING) / виртуальное размещение
2. Каждый тик: проверка MT5 ORDERS_GET → decrement `bars_remaining`
3. `bars_remaining` = 0 → ORDER_DELETE (expired)
4. Исполнился → позиция появляется в следующем TICK → стратегия шлёт MODIFY_SL / EXIT как обычно
5. `cancelled_external` — отменён брокером/вручную, логируется

**В бэктестере:** pending проверяется на каждом баре (до SL/TP). Gap-fill: если бар открылся за уровнем → исполнение по Open.

### OCO (One-Cancels-Other)

Стандартный паттерн для breakout-стратегий: Buy Stop + Sell Stop на один сигнал.

```python
# Стратегия генерирует два ENTER_PENDING с одинаковым signal_data (oco_group)
oco_id = f"cb_{date}_{symbol}"
signal_data = json.dumps({"sl_dist": sl_dist, "tp_r": tp_r, "oco_group": oco_id})

actions = [
    {
        "action": "ENTER_PENDING",
        "symbol": symbol,
        "direction": "LONG",
        "entry_price": level_hi,
        "sl_price": level_hi - sl_dist,
        "tp_price": level_hi + tp_dist,
        "expiry_bars": timeout_bars,
        "signal_data": signal_data,
    },
    {
        "action": "ENTER_PENDING",
        "symbol": symbol,
        "direction": "SHORT",
        "entry_price": level_lo,
        "sl_price": level_lo + sl_dist,
        "tp_price": level_lo - tp_dist,
        "expiry_bars": timeout_bars,
        "signal_data": signal_data,
    },
]
```

Демон при исполнении одного из ордеров автоматически отменяет второй — ищет все открытые pending с тем же `signal_data` (поле `oco_group` внутри JSON совпадает потому что весь `signal_data` совпадает).

> **Важно:** `signal_data` должен быть идентичной строкой для обоих ордеров в паре. Демон сравнивает строки целиком.

### EXIT — закрытие позиции

```json
{ "action": "EXIT", "ticket": 12345 }
```

### MODIFY_SL — перенос стопа

```json
{ "action": "MODIFY_SL", "ticket": 12345, "new_sl": 5250.0 }
```

---

### Когда слать ENTER vs ENTER_PENDING

| Ситуация | Action |
|----------|--------|
| Сигнал по закрытию бара, вход по рынку | `ENTER` |
| Breakout: вход выше/ниже текущей цены | `ENTER_PENDING` |
| Pullback: вход ниже/выше текущей цены | `ENTER_PENDING` (BUY_LIMIT/SELL_LIMIT — **не поддерживается**, только стоп-ордера) |
| Pending уже стоит, новый сигнал не нужен | Не слать ничего |

> ⚠️ **Стратегия не должна ставить дубль:** перед отправкой `ENTER_PENDING` нужно проверить в TICK, что pending ордера по этому символу уже нет.

---

## Шаблон: bidirectional стратегия (VWAP, signal_test)

```json
{
  "strategy": "vwap",
  "version": "1.0",
  "description": "VWAP σ-band breakout, EMA3 cross",
  "params": {
    "band_mult": 0.33,
    "ema_period": 3,
    "be_trigger_ratio": 0.2
  },
  "combos": [
    {
      "sym": "AUDCAD",
      "aclass": "forex",
      "tier": "T1",
      "directions": {
        "LONG": {
          "strat": { "tf": "H12", "P": 330, "sl_mult": 1.5, "mode": "A_fixed" },
          "daemon": { "size_r": 0.352, "role": "SECONDARY" }
        },
        "SHORT": {
          "strat": { "tf": "H12", "P": 330, "sl_mult": 1.5, "mode": "A_fixed" },
          "daemon": { "size_r": 1.0, "role": "PRIMARY" }
        }
      }
    },
    {
      "sym": "BTCUSD",
      "aclass": "crypto",
      "tier": "T1",
      "directions": {
        "LONG": {
          "strat": { "tf": "H4", "P": 450, "sl_mult": 0.67, "mode": "C_trail" },
          "daemon": { "size_r": 1.0, "role": "PRIMARY" }
        },
        "SHORT": {
          "strat": { "tf": "H4", "P": 670, "sl_mult": 0.67, "mode": "C_trail" },
          "daemon": { "size_r": 0.428, "role": "SECONDARY" }
        }
      }
    }
  ]
}
```

## Шаблон: symmetric стратегия с pending ордерами (compression_breakout)

Когда параметры одинаковые для LONG и SHORT — используем `"BOTH"`.
Стратегия с `ENTER_PENDING` указывает `timeout_bars` — это передаётся как `expiry_bars` в каждый ордер.

```json
{
  "strategy": "compression_breakout",
  "version": "1.0",
  "description": "Compression breakout, H4 EMA filter, indices only. Uses BUY_STOP+SELL_STOP OCO.",
  "params": {
    "atr_period": 100,
    "comp_bars": 20,
    "comp_thresh": 1.2,
    "wait_mult": 2,
    "filter_ema_period": 50,
    "day_pos_cutoff": 0.20,
    "sl_atr_mult": 3.0,
    "tp_r": 2.0,
    "timeout_bars": 80,
    "protector_trigger_r": 1.0,
    "protector_level_r": 0.0,
    "entry_mode": "pending"
  },
  "combos": [
    {
      "sym": "SP500",
      "aclass": "index",
      "tier": "T1",
      "directions": {
        "BOTH": {
          "strat": { "tf": "M15", "filter_tf": "H4" },
          "daemon": { "size_r": 1.0, "role": "PRIMARY" }
        }
      }
    },
    {
      "sym": "DAX40",
      "aclass": "index",
      "tier": "T2",
      "directions": {
        "BOTH": {
          "strat": { "tf": "M15", "filter_tf": "H4" },
          "daemon": { "size_r": 0.5, "role": "SECONDARY" }
        }
      }
    }
  ]
}
```

Поле `entry_mode` — только для Python. Демон его игнорирует.
При `entry_mode: "pending"` стратегия шлёт `ENTER_PENDING` вместо `ENTER`.
При `entry_mode: "market"` (или отсутствии) — обычный `ENTER`.

## Шаблон: pairs стратегия

Пара = один combo. Direction не используется (стратегия сама определяет кого long, кого short):

```json
{
  "strategy": "pairs_zscore",
  "version": "1.0",
  "description": "Z-score mean reversion pairs trading",
  "params": {
    "history_bars": 600,
    "atr_period": 14,
    "initial_sl_atr": 8.0,
    "be_z_ratio": 0.5,
    "trail_lock_atr": 1.0
  },
  "combos": [
    {
      "sym": "NZDJPY_CADJPY",
      "aclass": "forex",
      "tier": "T1",
      "symA": "NZDJPY",
      "symB": "CADJPY",
      "strat": {
        "lookback": 50,
        "entry_z": 2.5,
        "exit_z": 0.0,
        "stop_z": 4.5,
        "max_hold": 48,
        "vol_lookback": 200,
        "vol_pctl_min": 10
      },
      "daemon": {
        "size_r": 1.0,
        "role": "PRIMARY"
      }
    }
  ]
}
```

## Шаблон: MTF momentum стратегия (momentum_cont)

HTF импульс + LTF пулбэк. Tier-based sizing, protector на T1:

```json
{
  "strategy": "momentum_cont",
  "version": "1.0",
  "description": "MTF Momentum Continuation — HTF impulse detection + LTF RSI pullback entry",
  "params": {
    "atr_period": 14,
    "impulse_body_ratio": 0.7,
    "impulse_range_atr": 1.5,
    "impulse_decay_bars": 2,
    "rsi_period": 14,
    "rsi_pullback_thresh": 50,
    "min_stop_pips": 20,
    "r_cap": 1.5
  },
  "combos": [
    {
      "sym": "EURUSD",
      "aclass": "forex",
      "tier": "T1",
      "directions": {
        "BOTH": {
          "strat": {
            "htf": "H1", "ltf": "M15",
            "sl_atr": 1.5, "tp_r": 1.5,
            "protector_trigger_r": 1.0,
            "protector_lock_r": -0.50
          },
          "daemon": { "size_r": 1.0, "role": "PRIMARY" }
        }
      }
    },
    {
      "sym": "CHFJPY",
      "aclass": "forex",
      "tier": "T2",
      "directions": {
        "BOTH": {
          "strat": {
            "htf": "H1", "ltf": "M15",
            "sl_atr": 2.0, "tp_r": 1.0
          },
          "daemon": { "size_r": 0.5, "role": "SECONDARY" }
        }
      }
    }
  ]
}
```

**`r_cap`** в `params` — макс. дневной R-убыток. Передаётся демону через HELLO → Gate 12.
Можно переопределить / отключить в Dashboard → Settings → R-cap.

---

## Справочник полей

### Head

| Поле | Тип | Обязательно | Кто читает | Описание |
|------|-----|-------------|------------|----------|
| `strategy` | string | ✅ | Стратегия + Демон | Имя стратегии, ключ маршрутизации |
| `version` | string | ✅ | Человек | Версия конфига |
| `description` | string | ✅ | Человек | Краткое описание |
| `params` | object | ✅ | Стратегия | Параметры, общие для всех символов |
| `params.r_cap` | float | ❌ | Демон (Gate 12) | Макс. дневной R-убыток. Через HELLO → `requirements.r_cap` → Gate 12. |
| `combos` | array | ✅ | Стратегия + Демон | Массив символов/пар |

### Combo (уровень символа)

| Поле | Тип | Обязательно | Кто читает | Описание |
|------|-----|-------------|------------|----------|
| `sym` | string | ✅ | Оба | Канонический символ (или имя пары) |
| `aclass` | string | ✅ | Демон | Класс актива: `forex`, `index`, `metal`, `crypto`, `energy` |
| `tier` | string | ✅ | Демон + Человек | Качество из бэктеста: `T1` (лучшие), `T2` (средние), `T3` (слабые) |
| `directions` | object | ✅* | Оба | Ключи: `LONG`, `SHORT`, `BOTH` |
| `symA`, `symB` | string | pairs only | Стратегия | Составные символы пары |

*Для pairs вместо `directions` используются `strat` и `daemon` на уровне combo.

### Direction → strat (Python)

Содержимое зависит от стратегии. Общие поля:

| Поле | Тип | Описание |
|------|-----|----------|
| `tf` | string | Таймфрейм: M5, M15, M30, H1, H4, H12 |
| `htf`, `ltf` | string | Старший / младший таймфрейм (MTF стратегии). Только Python — стратегия ресемплирует из `tf` самостоятельно. Демон игнорирует. |
| `sl_mult` / `sl_atr` | float | Множитель для стоп-лосса |
| `tp_r` | float | Тейк-профит в R. Попадает в `signal_data` → RCalc |
| `mode` | string | Режим управления: `A_fixed`, `B_protect`, `C_trail` |
| `protector_trigger_r` | float | R-уровень срабатывания протектора. null = нет протектора |
| `protector_lock_r` | float | R-уровень фиксации при протекторе. Попадает в `signal_data` |
| `entry_mode` | string | `"market"` (default) или `"pending"`. Только Python, демон игнорирует. |
| `P` | int | Период расчёта (VWAP-specific) |
| `filter_tf` | string | Таймфрейм фильтра (compression_breakout-specific). Только Python, демон игнорирует. |

Стратегия может добавлять свои поля. Демон их игнорирует.

### Direction → daemon (C#)

| Поле | Тип | Обязательно | Описание |
|------|-----|-------------|----------|
| `size_r` | float | ✅ | Множитель риска: 1.0 = полный base risk, 0.5 = половина. При Init Sizing → `risk_factor` |
| `role` | string | ✅ | `PRIMARY` / `SECONDARY`. Информационное + Sizing tab |

### Поля tier

| Tier | Значение | Использование |
|------|----------|---------------|
| `T1` | Лучшие перформеры | Полный или близкий к полному size_r |
| `T2` | Средние | Уменьшенный size_r |
| `T3` | Слабые / hedge | Минимальный size_r |

---

## TICK — что стратегия получает от демона

```json
{
  "type": "TICK",
  "server_time": 1700000000,
  "bars": { "SP500": [[time, o, h, l, c], "..."] },
  "positions": [
    {
      "ticket": -101, "symbol": "SP500", "direction": "LONG",
      "volume": 0.01, "price_open": 5240.0,
      "sl": 5200.0, "tp": 5440.0, "signal_data": "{...}"
    }
  ],
  "pending_orders": [
    {
      "ticket": -102, "symbol": "SP500", "direction": "LONG",
      "order_type": "BUY_STOP", "volume": 0.01,
      "entry_price": 5280.0, "sl": 5200.0, "tp": 5450.0,
      "bars_remaining": 8, "signal_data": "{...}"
    }
  ],
  "equity": 10000.0
}
```

**`pending_orders`** — фильтруется по magic стратегии. Стратегия проверяет этот список перед отправкой нового `ENTER_PENDING` чтобы не ставить дубль.

---

## Что демон извлекает из конфига

1. **Список символов** — из `combos[].sym` (для init_sizing, CHECK_SYMBOLS)
2. **Asset class** — из `combos[].aclass` (для Sizing tab фильтрации)
3. **Tier** — из `combos[].tier` (для Sizing tab фильтрации)
4. **Risk factor** — из `combos[].directions.*.daemon.size_r` (для init_sizing → risk_factor)
5. **Timeframes** — из `combos[].directions.*.strat.tf` (для bars subscription)
6. **R-cap** — из `params.r_cap` (через HELLO → `requirements.r_cap` → Gate 12)

## Что стратегия извлекает из конфига

1. **Какие символы торговать** — `combos[].sym`
2. **В каких направлениях** — ключи `directions` (LONG/SHORT/BOTH)
3. **Параметры для каждого** — `directions.*.strat.*`
4. **Общие параметры** — `params.*`
5. **R-cap** — `params.r_cap` (включает в HELLO → `requirements.r_cap`)
6. **Entry mode** — `params.entry_mode` или `directions.*.strat.entry_mode`

---

## Запрещено

- ❌ `account` блок — sizing через terminal profile
- ❌ Параметры стратегии на root-уровне (вне `params`)
- ❌ Поля демона в `strat` блоке или наоборот
- ❌ Дублирование `aclass` / `tier` внутри каждого direction
- ❌ `ENTER_PENDING` с `entry_price` ≤ текущей цены для LONG (и ≥ для SHORT) — валидируется демоном
- ❌ Два `ENTER_PENDING` одного символа с разным `signal_data` — OCO не сработает
