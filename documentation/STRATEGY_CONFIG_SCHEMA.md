# Strategy Config Schema v1.1

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

## Шаблон: symmetric стратегия (compression_breakout)

Когда параметры одинаковые для LONG и SHORT — используем `"BOTH"`:

```json
{
  "strategy": "compression_breakout",
  "version": "1.0",
  "description": "Compression breakout, H4 EMA filter, indices only",
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
    "protector_level_r": 0.0
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
| `params.r_cap` | float | ❌ | Демон (Gate 12) | Макс. дневной R-убыток для стратегии. Передаётся через HELLO → `requirements.r_cap`. null/отсутствует = Gate 12 не активен. Dashboard может override/disable. |
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
| `htf`, `ltf` | string | Старший / младший таймфрейм (MTF стратегии) |
| `sl_mult` / `sl_atr` | float | Множитель для стоп-лосса (интерпретация зависит от стратегии) |
| `tp_r` | float | Тейк-профит в R (1.0 = 1R, 1.5 = 1.5R). Попадает в `signal_data` → RCalc |
| `mode` | string | Режим управления: `A_fixed`, `B_protect`, `C_trail` |
| `protector_trigger_r` | float | R-уровень срабатывания протектора (e.g. 1.0 = на 1R прибыли). null = нет протектора |
| `protector_lock_r` | float | R-уровень фиксации при протекторе (e.g. -0.50). Попадает в `signal_data` → RCalc |
| `P` | int | Период расчёта (VWAP-specific) |
| `filter_tf` | string | Таймфрейм фильтра (compression_breakout-specific) |

Стратегия может добавлять свои поля. Демон их игнорирует.

### Direction → daemon (C#)

| Поле | Тип | Обязательно | Описание |
|------|-----|-------------|----------|
| `size_r` | float | ✅ | Множитель риска: 1.0 = полный base risk, 0.5 = половина. При Init Sizing маппится в `risk_factor` |
| `role` | string | ✅ | `PRIMARY` (сильнее в бэктесте) / `SECONDARY` (слабее). Информационное + Sizing tab |

### Поля tier

| Tier | Значение | Использование |
|------|----------|---------------|
| `T1` | Лучшие перформеры | Полный или близкий к полному size_r |
| `T2` | Средние | Уменьшенный size_r |
| `T3` | Слабые / hedge | Минимальный size_r |

Tier используется для фильтрации в Sizing tab: выбрал T3 → уменьшил size_r всей группе.

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

---

## Запрещено

- ❌ `account` блок — sizing через terminal profile
- ❌ Параметры стратегии на root-уровне (вне `params`) — например `max_positions`, `cooldown_bars`
- ❌ Поля демона в `strat` блоке или наоборот
- ❌ Дублирование `aclass` / `tier` внутри каждого direction
