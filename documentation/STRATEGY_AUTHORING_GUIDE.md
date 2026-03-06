# Руководство по оформлению стратегий и конфигов

*Версия: 1.0 — на основе runner.py, Protocol.cs, архива стратегий и STRATEGY_CONFIG_SCHEMA v1.2*

---

## 1. Структура каталогов

```
strategies/
└── <strategy_name>/        ← имя папки = имя стратегии (snake_case)
    ├── strategy.py         ← ОБЯЗАТЕЛЬНО, точное имя
    └── config.json         ← ОБЯЗАТЕЛЬНО, точное имя
```

**Правила именования:**

| Артефакт | Требование |
|----------|------------|
| Папка стратегии | `snake_case`, только `[a-z0-9_]`, без пробелов и дефисов |
| Файл стратегии | строго `strategy.py` |
| Файл конфига | строго `config.json` |
| Поле `"strategy"` в конфиге | **точное совпадение** с именем папки — runner.py завершается с ошибкой если не совпадает |

**Примеры корректных имён:** `remr`, `momentum_cont`, `compression_breakout`, `mtf_pullback`

Вспомогательные файлы (модули, заметки) можно добавлять в папку стратегии — runner.py автоматически добавляет её в `sys.path`.

---

## 2. Взаимодействие демона со стратегией

### Транспорт

TCP-сокет, `127.0.0.1:<port>`. Протокол: newline-delimited JSON (`\n`). Стратегия **соединяется** к демону (не наоборот).

### Жизненный цикл сессии

```
Стратегия                        Демон
─────────────────────────────────────────────────────
  connect()          →
  HELLO              →     регистрация, резервирование порта
                     ←     ACK  (magic, terminal_id, mode, [saved_state])
  [restore_state()]
  ──── рабочий цикл ────
                     ←     TICK  (bars, positions, pending_orders, equity)
  on_bars()
  ACTIONS            →     риск-гейты → исполнение
  ...
                     ←     HEARTBEAT  (периодически)
  HEARTBEAT_ACK      →
  ...
                     ←     STOP  (оператор или внешнее событие)
  save_state()
  GOODBYE            →     (state)
  disconnect()
```

**Ключевые правила протокола:**

- Стратегия **пассивна** — никогда не шлёт сообщения первой после HELLO.
- На каждый TICK обязательно отвечать ACTIONS (даже пустой список).
- Таймаут ожидания сообщения — 300 с. При 3 последовательных таймаутах runner завершается.
- `on_bars()` получает только `bars` и `positions`. Поле `pending_orders` из TICK runner.py **не передаёт** в стратегию. Для дедупликации ENTER_PENDING стратегия должна отслеживать своё pending-состояние самостоятельно — через внутренний флаг или словарь (см. раздел 4).

### Что демон передаёт в TICK

```json
{
  "type": "TICK",
  "tick_id": 1234,
  "server_time": 1700000000,
  "bars": {
    "EURUSD": [
      {"time": 1699999800, "open": 1.08, "high": 1.082, "low": 1.079, "close": 1.081, "volume": 1500}
    ]
  },
  "positions": [
    {"ticket": 101, "symbol": "EURUSD", "direction": "LONG",
     "volume": 0.01, "price_open": 1.079, "sl": 1.075, "tp": 1.089,
     "profit": 20.0, "open_time": 1699999800, "signal_data": "{...}"}
  ],
  "pending_orders": [
    {"ticket": 102, "symbol": "SP500", "direction": "LONG",
     "order_type": "BUY_STOP", "entry_price": 5280.0,
     "sl": 5200.0, "tp": 5450.0, "bars_remaining": 8, "signal_data": "{...}"}
  ],
  "equity": 10500.0,
  "is_delta": false
}
```

Bars приходят в таймфрейме, который стратегия объявила в `get_requirements()` → `timeframes`. Если стратегии нужен старший TF — она ресемплирует самостоятельно.

**Частота TICK:** демон присылает тик при каждом закрытии нового бара по любому из подписанных символов (или по расписанию Scheduler). Стратегия должна быть идемпотентна — один и тот же сигнал не должен генерировать несколько входов при повторных тиках без новых баров.

В backtest-режиме возможен `is_delta: true` — демон шлёт только новые бары. Runner автоматически накапливает буфер и передаёт в `on_bars()` полное окно. Стратегия этой разницы не видит.

---

## 3. Обязательная структура `strategy.py`

### Класс

Должен называться `Strategy`. Если класс назван иначе — он будет найден только если имеет метод `on_bars`, но это нежелательно — называть `Strategy`.

### Обязательные методы

#### `__init__(self, config: dict)`

Получает содержимое `config.json` целиком.

```python
def __init__(self, config: dict):
    p = config.get("params", {})
    self.syms = [c["sym"] for c in config.get("combos", [])]
    # читать per-symbol параметры из combos[].directions.*.strat
    # читать r_cap из params для включения в get_requirements
```

#### `get_requirements(self) → dict`

Вызывается один раз перед HELLO. Должен вернуть:

```python
def get_requirements(self) -> dict:
    return {
        "symbols":      ["EURUSD", "GBPUSD"],   # список канонических символов
        "timeframes":   {"EURUSD": "M15", "GBPUSD": "M15"},  # TF по символу
        "history_bars": 300,                    # глубина истории (целое число)
        # опционально:
        "r_cap":        1.5,                    # макс. дневной R-убыток; None = не установлен
    }
```

- `timeframes` — таймфрейм в котором демон будет передавать бары. Допустимые значения: `M1 M5 M15 M30 H1 H4 D1`. **H12 здесь не используется** — MT5 не знает такого TF. Если стратегии нужны H12-бары, она запрашивает M30 или M5 и ресемплирует внутри (например, `bucket = epoch // 43200`). H12 допустимо только в `strat.tf` как обозначение целевого TF агрегации — для документирования, не для запроса у демона.
- `history_bars` — сколько баров нужно на старте. Устанавливать с запасом: `max(atr_period, lookback) + 50`.
- `r_cap` — если указан, активирует Gate 12 у демона. Можно переопределить через Dashboard.

#### `on_bars(self, bars: dict, positions: list) → list`

Главный метод. Вызывается на каждый TICK.

**Формат бара** — словарь с полными именами ключей:

```python
{
    "time":   1699999800,   # int, Unix timestamp UTC (секунды)
    "open":   1.08000,      # float
    "high":   1.08200,      # float
    "low":    1.07900,      # float
    "close":  1.08100,      # float
    "volume": 1500          # int
}
```

> ⚠️ Ключи — **полные слова**: `open`, `high`, `low`, `close`. Короткие формы `o`, `h`, `l`, `c` не используются. Особенно важно при написании отдельных классов-ресемплеров.

```python
def on_bars(self, bars: dict, positions: list) -> list:
    actions = []
    # bars     = {"EURUSD": [bar_dict, bar_dict, ...], "GBPUSD": [...]}
    # positions = список открытых позиций этой стратегии (фильтр по magic)
    return actions   # список dict; пустой список — ОК, None недопустим (runner подставит [])
```

**Порядок баров:** от старых к новым. `bars[sym][0]` — самый старый, `bars[sym][-1]` — **последний закрытый** бар. Тик приходит в момент закрытия бара, поэтому `bars[-1]` уже завершён и безопасен для сигнала.

> ⚠️ При внутреннем ресемплинге в старший TF (например M5→H12) последний бакет в буфере может быть **незакрытым** (формирующимся). Его нельзя использовать как сигнальный бар. Закрытые бары — все кроме бакета с временем `>= (last_bar_time // htf_sec) * htf_sec`.

**Защитный доступ к барам:** `bars[sym]` может вернуть `None` или пустой список если по символу не пришли данные. Всегда проверять:

```python
raw = bars.get(sym)
if not raw:
    continue
# теперь безопасно: raw[-1]["close"], raw[-1]["time"] и т.д.
```

**Формат позиции** — поля словаря, которые стратегия получает в `positions`:

| Ключ | Тип | Описание |
|------|-----|----------|
| `ticket` | int | Уникальный номер позиции |
| `symbol` | str | Канонический символ (`"EURUSD"`, не брокерский алиас) |
| `direction` | str | `"LONG"` / `"SHORT"` |
| `volume` | float | Объём в лотах |
| `price_open` | float | Цена открытия |
| `sl` | float | Текущий стоп-лосс (0 если не выставлен) |
| `tp` | float | Текущий тейк-профит (0 если не выставлен) |
| `profit` | float | Плавающий PnL в валюте депозита |
| `open_time` | int | Unix timestamp открытия |
| `signal_data` | str | Строка переданная при ENTER (или `null`) |

> ⚠️ `price_open` — не `entry`, не `open_price`, не `price`. `open_time` — не `entry_time`. Те же ловушки что и `bar["o"]`.

#### `save_state(self) → dict`

Вызывается при получении STOP. Должен вернуть JSON-сериализуемый dict — runner передаёт его через `json.dumps()`.

**Допустимые типы:** `str`, `int`, `float`, `bool`, `None`, `list`, `dict` с теми же типами рекурсивно.

**Сломают сериализацию:** `datetime`, `numpy.float64` / `numpy.int64`, `set`, `tuple` (станет list — терпимо), любые кастомные объекты без `__dict__`.

```python
# Правильно:
def save_state(self) -> dict:
    return {
        "last_ts": {s: int(v) for s, v in self._last.items()},   # int(), не numpy.int64
        "atr":     {s: float(v) for s, v in self._atr.items()},  # float(), не numpy.float64
        "pos":     {str(k): v.__dict__ for k, v in self._pos.items()},
    }
```

#### `restore_state(self, state: dict)`

Вызывается при старте если в ACK есть `saved_state`. `state` — то что вернул `save_state()`.

```python
def restore_state(self, state: dict):
    if not state:
        return
    # восстановить внутреннее состояние
```

### Минимальный шаблон стратегии

```python
"""
MyStrategy v1.0
===============
Краткое описание логики.
"""

class Strategy:

    def __init__(self, config: dict):
        p = config.get("params", {})
        combos = config.get("combos", [])

        self.syms = []
        self.timeframes = {}

        for c in combos:
            sym = c["sym"]
            self.syms.append(sym)
            # читаем TF из первого найденного direction
            for dk in ("BOTH", "LONG", "SHORT"):
                if dk in c.get("directions", {}):
                    tf = c["directions"][dk].get("strat", {}).get("tf", "M15")
                    self.timeframes[sym] = tf
                    break

        self.rcap = p.get("r_cap")
        self.hbars = p.get("history_bars", 300)
        # ... прочие параметры

    def get_requirements(self) -> dict:
        return {
            "symbols":      self.syms,
            "timeframes":   self.timeframes,     # TF из конфига, не хардкод
            "history_bars": self.hbars,
            "r_cap":        self.rcap,
        }

    def on_bars(self, bars: dict, positions: list) -> list:
        actions = []
        # ... логика
        return actions

    def save_state(self) -> dict:
        return {}

    def restore_state(self, state: dict):
        pass
```

---

## 4. Actions — формат команд

Стратегия возвращает список dict. Демон применяет риск-гейты и исполняет.

### ENTER — рыночный вход

```python
{
    "action":      "ENTER",
    "symbol":      "EURUSD",          # канонический символ
    "direction":   "LONG",            # "LONG" / "SHORT"
    "sl_price":    1.0750,            # обязательно
    "tp_price":    1.0950,            # 0 или None = нет TP
    "comment":     "my_signal_long",  # необязательно
    "signal_data": '{"sl_dist": 0.003, "tp_r": 2.0}',  # JSON-строка; sl_dist обязателен для R-calc
}
```

> ⚠️ `signal_data` — **строка**, результат `json.dumps(...)`. Не dict. Демон принимает `string?` и парсит сам. Передача dict вместо строки приведёт к ошибке сериализации или тихому сбою R-расчёта.
>
> ```python
> # Правильно:
> "signal_data": json.dumps({"sl_dist": sl_dist, "tp_r": tp_r})
> # Неправильно:
> "signal_data": {"sl_dist": sl_dist, "tp_r": tp_r}
> ```

### ENTER_PENDING — стоп-ордер

```python
{
    "action":       "ENTER_PENDING",
    "symbol":       "SP500",
    "direction":    "LONG",
    "entry_price":  5280.0,   # BUY_STOP если > current price; SELL_STOP если direction=SHORT и < current
    "sl_price":     5200.0,
    "tp_price":     5450.0,   # необязательно
    "expiry_bars":  10,       # 0 / None = GTC
    "signal_data":  '{"sl_dist": 80.0, "oco_group": "cb_20240115_SP500"}',
}
```

Перед отправкой необходимо убедиться что pending по этому символу ещё не стоит. Так как `pending_orders` из TICK runner.py в `on_bars()` **не передаёт**, стратегия отслеживает состояние сама — например, через внутренний флаг:

```python
# в __init__:
self._pending = set()   # символы с активным pending ордером

# в on_bars() перед ENTER_PENDING:
if sym in self._pending:
    continue
self._pending.add(sym)
# ... формировать action

# при получении нового TICK: если позиция открылась — убрать из _pending
# (позиция появится в positions, pending_orders исчезнет)
for pos in positions:
    self._pending.discard(pos["symbol"])
```

Для OCO (Buy Stop + Sell Stop): оба action должны иметь **идентичную** строку `signal_data` — демон сравнивает строки целиком для отмены парного ордера.

### EXIT — закрытие позиции

```python
{"action": "EXIT", "ticket": 12345}
```

### MODIFY_SL — перенос стопа

```python
{"action": "MODIFY_SL", "ticket": 12345, "new_sl": 1.0800}
```

Стратегия обязана самостоятельно проверить что новый SL лучше текущего (LONG: новый > текущий; SHORT: новый < текущий) — демон отклонит ухудшение, но лучше не засорять лог.

---

## 5. Формат `config.json`

*Полная спецификация: STRATEGY_CONFIG_SCHEMA.md v1.2*

### Структура верхнего уровня

```json
{
  "strategy":    "my_strategy",
  "version":     "1.0",
  "description": "Краткое описание для человека",
  "params":      { ... },
  "combos":      [ ... ]
}
```

| Поле | Обязательно | Описание |
|------|-------------|----------|
| `strategy` | ✅ | Имя — **точно совпадает** с именем папки |
| `version` | ✅ | Версия конфига |
| `description` | ✅ | Строка для человека |
| `params` | ✅ | Общие параметры, одинаковые для всех символов |
| `combos` | ✅ | Массив символов/пар |

### params

Только параметры общие для **всех** символов. Per-symbol параметры — внутри combo.

```json
"params": {
  "atr_period":  14,
  "r_cap":       1.5,    // необязательно; передаётся в HELLO → Gate 12
  "history_bars": 1200   // глубина; стратегия читает при инициализации
}
```

### combos[]

Каждый элемент — один символ (или пара для pairs-стратегий).

```json
{
  "sym":    "EURUSD",    // канонический символ
  "aclass": "forex",     // forex / index / metal / crypto / energy
  "tier":   "T1",        // T1 / T2 / T3 — для Sizing tab
  "directions": {
    "BOTH": {            // или "LONG" / "SHORT" (раздельно если параметры разные)
      "strat":  { ... }, // параметры для Python
      "daemon": { ... }  // параметры для C# / Sizing
    }
  }
}
```

### strat блок (Python читает, демон игнорирует)

| Поле | Описание |
|------|----------|
| `tf` | Таймфрейм тиков, запрашиваемый у демона: `M1 M5 M15 M30 H1 H4 D1`. Должен совпадать с `timeframes` в `get_requirements()`. |
| `htf`, `ltf` | Старший/младший TF (для MTF стратегий) — стратегия ресемплирует из `tf` сама. Демон игнорирует. |
| `sl_mult` / `sl_atr` | Множитель SL через ATR |
| `tp_r` | Тейк-профит в R |
| `mode` | `A_fixed` / `B_protect` / `C_trail` |
| `protector_trigger_r` | R-уровень срабатывания протектора |
| `protector_lock_r` | R-уровень фиксации SL при протекторе |
| `entry_mode` | `"market"` (default) или `"pending"` |
| любые другие | Стратегия может добавлять свои поля |

### daemon блок (C# читает, Python может игнорировать)

| Поле | Обязательно | Описание |
|------|-------------|----------|
| `size_r` | ✅ | Множитель риска: `1.0` = полный, `0.5` = половина |
| `role` | ✅ | `PRIMARY` / `SECONDARY` — для Sizing tab |

### Правила

- `aclass` и `tier` — на уровне символа, **не** внутри direction.
- `account` — **запрещён**. Sizing только через terminal profile + Sizing tab.
- Параметры стратегии — только в `params` или `strat`, не на root-уровне.
- Поля для демона только в `daemon`, для Python только в `strat` — не смешивать.
- `r_cap` — только в `params`, не в `strat`.

---

## 6. Шаблоны конфигов

### Симметричная (BOTH)

```json
{
  "strategy":    "my_strategy",
  "version":     "1.0",
  "description": "Описание",
  "params": {
    "atr_period": 14,
    "r_cap": 1.5
  },
  "combos": [
    {
      "sym": "EURUSD",
      "aclass": "forex",
      "tier": "T1",
      "directions": {
        "BOTH": {
          "strat":  {"tf": "M15", "sl_atr": 1.5, "tp_r": 2.0},
          "daemon": {"size_r": 1.0, "role": "PRIMARY"}
        }
      }
    }
  ]
}
```

### Асимметричная (LONG / SHORT)

```json
{
  "strategy": "vwap_asym",
  "version":  "1.0",
  "description": "Разные параметры по направлениям",
  "params": {"r_cap": 1.0},
  "combos": [
    {
      "sym": "BTCUSD",
      "aclass": "crypto",
      "tier": "T1",
      "directions": {
        "LONG":  {"strat": {"tf": "H4", "sl_atr": 1.0}, "daemon": {"size_r": 1.0,  "role": "PRIMARY"}},
        "SHORT": {"strat": {"tf": "H4", "sl_atr": 1.5}, "daemon": {"size_r": 0.5,  "role": "SECONDARY"}}
      }
    }
  ]
}
```

### Breakout с pending ордерами

```json
{
  "strategy": "compression_breakout",
  "version":  "1.0",
  "description": "OCO BUY_STOP + SELL_STOP",
  "params": {
    "atr_period":   100,
    "sl_atr_mult":  3.0,
    "tp_r":         2.0,
    "timeout_bars": 80,
    "entry_mode":   "pending"
  },
  "combos": [
    {
      "sym": "SP500",
      "aclass": "index",
      "tier": "T1",
      "directions": {
        "BOTH": {
          "strat":  {"tf": "M15", "filter_tf": "H4"},
          "daemon": {"size_r": 1.0, "role": "PRIMARY"}
        }
      }
    }
  ]
}
```

---

## 7. Запуск через runner.py

```bash
python runner.py \
  --port 5600 \
  --strategy my_strategy \
  --strategy-dir /path/to/strategies \
  --config /path/to/strategies/my_strategy/config.json
```

`--config` необязателен: по умолчанию ищет `<strategy-dir>/<strategy>/config.json`.

### Регистрация стратегии в демоне

Вручную редактировать `daemon/config.json` не нужно. Регистрация стратегии выполняется через Dashboard — демон записывает запись в `strategies[]` сам. Разработчику достаточно положить папку стратегии в `strategies/` и указать путь в интерфейсе.

### Канонические символы

Стратегия везде использует **канонические** имена — демон транслирует их в брокерские алиасы через SymbolResolver. Canonical name ≠ брокерское имя (брокер может называть `EURUSDm`, `DE40Cash`, `XAUUSD.`).

Канонические имена для основных инструментов:

| Класс | Символы |
|-------|---------|
| Forex majors | `EURUSD` `GBPUSD` `USDJPY` `USDCHF` `USDCAD` `AUDUSD` `NZDUSD` |
| Forex crosses | `EURGBP` `EURJPY` `GBPJPY` `AUDJPY` `CADJPY` `CHFJPY` `NZDJPY` `EURAUD` `EURCAD` `EURNZD` `EURCHF` `GBPAUD` `GBPCAD` `GBPNZD` `GBPCHF` `AUDCAD` `AUDNZD` `AUDCHF` `CADCHF` `NZDCAD` `NZDCHF` |
| Indices | `SP500` `NAS100` `US30` `DAX40` `UK100` `JPN225` |
| Metals | `XAUUSD` `XAGUSD` |
| Energy | `XTIUSD` `XBRUSD` |
| Crypto | `BTCUSD` `ETHUSD` |

> ⚠️ DAX — `DAX40`, не `DE40` / `GER40` (DE40 — вариант, не canonical). Japan — `JPN225`, не `JP225`. Поля `symbol` в positions содержат canonical имена (то что вернул `ToCanonical`). Если в конфиге `sym` написан вариант (`DE40`), а не canonical (`DAX40`), стратегия не найдёт свои позиции при трекинге.

---

## 8. Частые ошибки

| Ошибка | Последствие |
|--------|-------------|
| `bar["o"]` / `bar["h"]` вместо `bar["open"]` / `bar["high"]` | `KeyError` в первом же тике |
| `pos["entry"]` / `pos["price"]` вместо `pos["price_open"]` | `KeyError` в трекинге позиций |
| `pos["id"]` вместо `pos["ticket"]` | `KeyError` при EXIT / MODIFY_SL |
| `"signal_data": {...}` (dict) вместо `json.dumps({...})` (str) | сбой R-расчёта или ошибка десериализации |
| `bars["EURUSD"][-1]` без проверки `bars.get(sym)` | `IndexError` / `TypeError` если нет данных |
| `numpy.float64` / `datetime` в `save_state()` | `TypeError` при `json.dumps()` на STOP |
| `"strategy"` в конфиге ≠ имя папки | runner завершается с FATAL |
| `on_bars()` возвращает `None` | runner подставляет `[]`, но лучше явно |
| `get_requirements()` без `timeframes` | демон не знает какой TF подписать |
| Отсутствует `sl_price` в ENTER | позиция без SL — нарушение риск-правил |
| `sl_dist` нет в `signal_data` | R-расчёт вернёт 0 или ошибку |
| Повторный `ENTER_PENDING` без внутреннего флага | дублирующий ордер |
| Использование последнего HTF-бакета без проверки закрытости | look-ahead на незакрытом баре |
| Ресемплинг через `dt.timestamp()` | timezone-баг если локаль ≠ UTC; использовать `calendar.timegm()` |
| `DE40` / `GER40` вместо `DAX40`, `JP225` вместо `JPN225` в sym конфига | стратегия не найдёт свои позиции — `pos["symbol"]` придёт как canonical (`DAX40`), а стратегия ищет по варианту |
