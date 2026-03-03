# TRADING DAEMON — ПЛАН РЕАЛИЗАЦИИ

## Что мы строим

Торговый демон — программа которая работает на Windows VPS рядом с терминалами MT5. Она подключается к терминалам, получает рыночные данные, передаёт их стратегиям (Python скрипты), принимает торговые сигналы, проверяет риски и исполняет ордера. Оператор управляет всем через браузер (dashboard).

## Структура проекта

```
trading-daemon/
│
├── daemon/                          ← Демон (C#, .NET 8)
│   ├── Program.cs                   ← Точка входа, запуск всех подсистем, mutex guard
│   ├── Config/
│   │   ├── DaemonConfig.cs          ← Модель конфигурации
│   │   └── config.json              ← Файл конфигурации
│   │
│   ├── Engine/
│   │   ├── Engine.cs                ← Главный координатор
│   │   ├── Lifecycle.cs             ← Запуск/остановка компонентов
│   │   ├── Scheduler.cs             ← Таймеры: когда опрашивать стратегии
│   │   ├── StateManager.cs          ← Чтение/запись состояния в SQLite
│   │   └── Reconciliation.cs        ← Сверка state vs терминал
│   │
│   ├── Risk/
│   │   ├── RiskManager.cs           ← Цепочка гейтов, вызывается при каждом ордере
│   │   ├── TerminalProfile.cs       ← Модель: тип, лимиты, режим, настройки гардов
│   │   ├── GateChain.cs             ← Последовательная проверка Gate1..Gate9
│   │   ├── NewsService.cs           ← Загрузка календаря, кэш, проверка block window
│   │   ├── ThreeSLGuard.cs          ← Счётчик SL, блокировка, сброс
│   │   ├── LotCalculator.cs         ← Расчёт лота из SL + карточки инструмента
│   │   └── ActiveProtection.cs      ← Непрерывный мониторинг DD, auto-BE
│   │
│   ├── Connector/
│   │   ├── ConnectorManager.cs      ← Управление воркерами, маршрутизация
│   │   ├── WorkerProcess.cs         ← Запуск/мониторинг одного Python воркера
│   │   ├── IConnector.cs            ← Интерфейс: GetPositions, SendOrder, etc.
│   │   ├── InstrumentCard.cs        ← Модель: данные symbol_info() из терминала
│   │   └── SymbolMapper.cs          ← Маппинг EURUSD ↔ EURUSDi per terminal
│   │
│   ├── Strategy/
│   │   ├── StrategyManager.cs       ← Запуск/остановка Python стратегий
│   │   ├── StrategyProcess.cs       ← Управление одним Python процессом
│   │   └── Protocol.cs              ← Сериализация/десериализация сообщений
│   │
│   ├── Alerts/
│   │   ├── AlertService.cs          ← Интерфейс отправки алертов
│   │   └── TelegramAlert.cs         ← Реализация через Telegram Bot API
│   │
│   ├── Dashboard/
│   │   ├── DashboardServer.cs       ← HTTP + WebSocket сервер
│   │   ├── wwwroot/                 ← HTML/JS/CSS файлы dashboard
│   │   └── Handlers/                ← Обработчики WebSocket команд
│   │
│   └── Data/
│       ├── state.db                 ← SQLite: позиции, события, профили
│       └── logs/                    ← Файловые логи
│
├── workers/
│   └── mt5_worker.py                ← Воркер MT5 (один скрипт, запускается N раз)
│
├── strategies/
│   ├── runner.py                    ← Универсальный runner (одинаковый для всех стратегий)
│   ├── bb_mr_v2/
│   │   ├── strategy.py              ← Логика стратегии (код из бэктеста)
│   │   ├── config.json              ← Параметры: символы, dev, N, exit type
│   │   └── models/                  ← ML модели если есть
│   └── bb_breakout_v1/
│       ├── strategy.py
│       └── config.json
│
└── docs/
    └── PROTOCOL.md                  ← Описание протокола обмена
```

---

## ФАЗА 0: Подготовка среды

Цель: убедиться что всё нужное установлено и работает.

### Шаг 0.1 — Инструменты

На Windows VPS (или рабочей машине для разработки) нужно:

1. **.NET 8 SDK** — скачать с dotnet.microsoft.com, установить. Проверка: открыть cmd, набрать `dotnet --version`, должно показать 8.x.x.
2. **Python 3.10+** — скачать с python.org, установить с галочкой "Add to PATH". Проверка: `python --version`.
3. **MetaTrader5 Python** — `pip install MetaTrader5`. Проверка: `python -c "import MetaTrader5; print('OK')"`.
4. **Терминал MT5** — хотя бы один, запущенный, залогиненный на демо-аккаунт.
5. **VS Code** или **Visual Studio 2022** — для редактирования C# и Python.
6. **DB Browser for SQLite** (опционально) — для просмотра state.db руками.

### Шаг 0.2 — Создать проект

```bash
mkdir trading-daemon
cd trading-daemon
dotnet new console -n daemon
cd daemon
dotnet add package Microsoft.Data.Sqlite
dotnet add package System.Text.Json
```

Создать папки:

```bash
mkdir workers
mkdir strategies
mkdir strategies\bb_mr_v2
```

### Шаг 0.3 — Проверить связь с MT5 из Python

Создать файл `workers/test_mt5.py`:

```python
import MetaTrader5 as mt5
import json

# Путь к вашему терминалу — поменять на свой
TERMINAL_PATH = r"C:\Program Files\MetaTrader 5\terminal64.exe"

if not mt5.initialize(path=TERMINAL_PATH):
    print(f"FAIL: {mt5.last_error()}")
    quit()

info = mt5.terminal_info()
print(f"Terminal: {info.name}")
print(f"Company: {info.company}")
print(f"Connected: {info.connected}")

acc = mt5.account_info()
print(f"Account: {acc.login}")
print(f"Balance: {acc.balance}")
print(f"Equity: {acc.equity}")

# Проверяем получение баров
rates = mt5.copy_rates_from_pos("EURUSD", mt5.TIMEFRAME_H1, 0, 5)
if rates is not None:
    print(f"Got {len(rates)} bars for EURUSD H1")
    for r in rates:
        print(f"  {r}")
else:
    print(f"FAIL getting rates: {mt5.last_error()}")

# Проверяем symbol_info
si = mt5.symbol_info("EURUSD")
if si:
    print(f"\nEURUSD card:")
    print(f"  digits: {si.digits}")
    print(f"  point: {si.point}")
    print(f"  trade_tick_size: {si.trade_tick_size}")
    print(f"  trade_tick_value: {si.trade_tick_value}")
    print(f"  volume_min: {si.volume_min}")
    print(f"  volume_max: {si.volume_max}")
    print(f"  volume_step: {si.volume_step}")
    print(f"  trade_contract_size: {si.trade_contract_size}")

mt5.shutdown()
print("\nDONE — MT5 connection works")
```

Запустить: `python workers/test_mt5.py`. Если всё печатается без FAIL — среда готова.

---

## ФАЗА 1: MT5 Worker

Цель: Python процесс который подключается к одному терминалу MT5, слушает TCP порт, принимает команды от демона в JSON, выполняет через MT5 API, возвращает результат в JSON.

Почему начинаем с этого: это фундамент. Без рабочей связи с MT5 всё остальное бесполезно. И это можно полностью протестировать отдельно, без демона.

### Шаг 1.1 — Протокол обмена воркера

Воркер слушает TCP порт. Каждое сообщение — JSON строка, завершённая символом `\n` (newline-delimited JSON). Это простейший протокол — читаем строки, парсим JSON.

Запрос от демона:

```json
{"cmd": "HEARTBEAT", "id": 1}
{"cmd": "ACCOUNT_INFO", "id": 2}
{"cmd": "GET_POSITIONS", "id": 3}
{"cmd": "GET_RATES", "id": 4, "symbol": "EURUSD", "timeframe": "H1", "count": 300}
{"cmd": "SYMBOL_INFO", "id": 5, "symbol": "EURUSD"}
{"cmd": "ORDER_SEND", "id": 6, "request": {"action": 1, "symbol": "EURUSD", "volume": 0.5, ...}}
{"cmd": "SHUTDOWN", "id": 99}
```

Ответ от воркера:

```json
{"id": 1, "status": "ok", "data": {"connected": true, "ping_ms": 12}}
{"id": 2, "status": "ok", "data": {"login": 12345, "balance": 100000.0, "equity": 99200.0, ...}}
{"id": 6, "status": "ok", "data": {"ticket": 12345, "price": 1.0872, "volume": 0.5}}
{"id": 6, "status": "error", "code": 10004, "message": "Requote", "retryable": true}
```

Поле `id` — демон присваивает каждому запросу номер, воркер возвращает тот же номер в ответе. Это позволяет матчить запросы и ответы.

### Шаг 1.2 — Код воркера

Файл: `workers/mt5_worker.py`

Структура:

```
1. Разбор аргументов командной строки (--port, --terminal-path, --login, --password, --server)
2. Подключение к MT5: mt5.initialize(path=...), mt5.login(login, password, server)
3. Запуск TCP сервера на указанном порту
4. Бесконечный цикл: принять соединение → читать строки → парсить JSON → выполнить команду → вернуть JSON
5. Обработка каждой команды — отдельная функция
```

Ключевые функции:

```python
def handle_heartbeat():
    """Проверить что терминал жив."""
    info = mt5.terminal_info()
    if info is None:
        return {"status": "error", "message": "terminal disconnected"}
    return {"status": "ok", "data": {"connected": info.connected}}

def handle_account_info():
    """Баланс, equity, margin, тип счёта, серверное время."""
    acc = mt5.account_info()
    if acc is None:
        return {"status": "error", "message": mt5.last_error()}
    
    # Серверное время — для определения торгового дня брокера
    server_time = mt5.symbol_info_tick("EURUSD")
    srv_ts = server_time.time if server_time else 0
    
    return {"status": "ok", "data": {
        "login": acc.login,
        "balance": acc.balance,
        "equity": acc.equity,
        "margin": acc.margin,
        "margin_free": acc.margin_free,
        "margin_level": acc.margin_level,
        "profit": acc.profit,
        "trade_mode": acc.trade_mode,       # 0=netting, 2=hedge
        "server_time": srv_ts               # unix timestamp серверного времени
    }}

def handle_get_positions():
    """Все открытые позиции."""
    positions = mt5.positions_get()
    if positions is None:
        return {"status": "ok", "data": []}
    result = []
    for p in positions:
        result.append({
            "ticket": p.ticket,
            "symbol": p.symbol,
            "type": "BUY" if p.type == 0 else "SELL",
            "volume": p.volume,
            "price_open": p.price_open,
            "sl": p.sl,
            "tp": p.tp,
            "profit": p.profit,
            "swap": p.swap,
            "time": p.time,
            "magic": p.magic,
            "comment": p.comment
        })
    return {"status": "ok", "data": result}

def handle_get_rates(symbol, timeframe, count):
    """Получить бары."""
    tf_map = {
        "M1": mt5.TIMEFRAME_M1, "M5": mt5.TIMEFRAME_M5,
        "M15": mt5.TIMEFRAME_M15, "M30": mt5.TIMEFRAME_M30,
        "H1": mt5.TIMEFRAME_H1, "H4": mt5.TIMEFRAME_H4,
        "D1": mt5.TIMEFRAME_D1
    }
    tf = tf_map.get(timeframe)
    if tf is None:
        return {"status": "error", "message": f"unknown timeframe: {timeframe}"}
    
    rates = mt5.copy_rates_from_pos(symbol, tf, 0, count)
    if rates is None:
        return {"status": "error", "message": str(mt5.last_error())}
    
    bars = []
    for r in rates:
        bars.append({
            "time": int(r[0]),
            "open": float(r[1]),
            "high": float(r[2]),
            "low": float(r[3]),
            "close": float(r[4]),
            "volume": int(r[5])
        })
    return {"status": "ok", "data": bars}

def handle_symbol_info(symbol):
    """Карточка инструмента — самое важное для расчёта лота."""
    si = mt5.symbol_info(symbol)
    if si is None:
        return {"status": "error", "message": f"symbol not found: {symbol}"}
    return {"status": "ok", "data": {
        "symbol": si.name,
        "digits": si.digits,
        "point": si.point,
        "trade_tick_size": si.trade_tick_size,
        "trade_tick_value": si.trade_tick_value,
        "trade_tick_value_profit": si.trade_tick_value_profit,
        "trade_tick_value_loss": si.trade_tick_value_loss,
        "trade_contract_size": si.trade_contract_size,
        "volume_min": si.volume_min,
        "volume_max": si.volume_max,
        "volume_step": si.volume_step,
        "margin_initial": si.margin_initial,
        "spread": si.spread,
        "currency_base": si.currency_base,
        "currency_profit": si.currency_profit,
        "currency_margin": si.currency_margin
    }}

def handle_order_send(request):
    """Отправить ордер."""
    # request — это dict с полями action, symbol, volume, type, price, sl, tp, ...
    # Конвертируем в mt5.TradeRequest
    req = mt5.TradeRequest()
    # ... заполняем поля из request ...
    result = mt5.order_send(req)
    if result is None:
        return {"status": "error", "message": str(mt5.last_error()), "retryable": False}
    if result.retcode != mt5.TRADE_RETCODE_DONE:
        retryable = result.retcode in [mt5.TRADE_RETCODE_REQUOTE, mt5.TRADE_RETCODE_PRICE_OFF]
        return {"status": "error", "code": result.retcode,
                "message": result.comment, "retryable": retryable}
    return {"status": "ok", "data": {
        "ticket": result.order,
        "price": result.price,
        "volume": result.volume
    }}
```

### Шаг 1.3 — Тестирование воркера

Запускаем воркер:

```bash
python workers/mt5_worker.py --port 5501 --terminal-path "C:\MT5\terminal64.exe" --login 12345 --password xxx --server "Demo"
```

Из другого терминала проверяем (Python или netcat):

```python
import socket, json

s = socket.socket()
s.connect(('localhost', 5501))

# Отправить команду
cmd = json.dumps({"cmd": "ACCOUNT_INFO", "id": 1}) + "\n"
s.sendall(cmd.encode())

# Получить ответ
data = s.makefile().readline()
resp = json.loads(data)
print(resp)
```

Тестируем все команды по очереди. Воркер должен корректно обрабатывать: нормальные запросы, несуществующие символы, невалидные ордера (будут rejected терминалом), потерю связи с терминалом (mt5 functions возвращают None).

### Критерий завершения фазы 1

Воркер запускается, подключается к MT5, отвечает на все команды правильным JSON. Можно вручную открыть и закрыть позицию через команды. Можно получить бары и symbol_info по любому инструменту.

---

## ФАЗА 2: Connector Manager (C#)

Цель: демон умеет запускать воркер-процессы, общаться с ними по TCP, получать данные из терминалов.

### Шаг 2.1 — Модели данных

Создать C# классы которые соответствуют JSON ответам воркера:

```
InstrumentCard — то что возвращает SYMBOL_INFO
Position — то что возвращает GET_POSITIONS  
AccountInfo — то что возвращает ACCOUNT_INFO
Bar — одна свеча из GET_RATES
OrderResult — результат ORDER_SEND
WorkerCommand — команда для воркера (cmd, id, параметры)
WorkerResponse — ответ воркера (id, status, data/error)
```

### Шаг 2.2 — WorkerProcess.cs

Класс который:

1. Запускает `python mt5_worker.py --port {port} --terminal-path {path} ...` как дочерний процесс (`System.Diagnostics.Process`).
2. Подключается к нему по TCP (`TcpClient`).
3. Имеет метод `SendCommand(WorkerCommand) → WorkerResponse` — отправить JSON строку, прочитать JSON строку ответа.
4. Имеет метод `Heartbeat() → bool` — отправить HEARTBEAT, проверить ответ.
5. При вызове `Stop()` — отправляет SHUTDOWN, ждёт завершения процесса, если не завершился за 5 секунд — убивает (`Process.Kill()`).

### Шаг 2.3 — ConnectorManager.cs

Класс который:

1. При старте читает конфиг (`config.json` → список терминалов).
2. Для каждого терминала с `auto_connect: true` — создаёт `WorkerProcess` и запускает.
3. Запускает фоновый таймер: каждые 10 секунд проверяет heartbeat всех воркеров.
4. Предоставляет методы:
   - `GetPositions(terminalId) → List<Position>`
   - `GetRates(terminalId, symbol, timeframe, count) → List<Bar>`
   - `SendOrder(terminalId, OrderRequest) → OrderResult`
   - `GetAccountInfo(terminalId) → AccountInfo`  (включает trade_mode и server_time)
   - `GetSymbolInfo(terminalId, symbol) → InstrumentCard`
   - `IsConnected(terminalId) → bool`
   - `GetAllTerminalIds() → List<string>`

При первом подключении к терминалу ConnectorManager:
   - Запрашивает `account_info()` → определяет `trade_mode` (hedge=2 / netting=0)
   - Определяет `server_utc_offset` из разницы серверного и UTC времени
   - Записывает оба значения в Terminal Profile (если не заданы вручную)

### Шаг 2.4 — Symbol Mapping

Один и тот же инструмент на разных брокерах называется по-разному: EURUSD, EURUSDi, EURUSD.raw, EURUSD.stp. Стратегия работает с каноническими именами (EURUSD). Демон маппит на имя брокера при обращении к терминалу.

В конфиге терминала:

```json
{
  "id": "IC-Markets",
  "symbol_map": {
    "EURUSD": "EURUSDi",
    "GBPUSD": "GBPUSDi",
    "XAUUSD": "XAUUSDi",
    "USDJPY": "USDJPYi",
    "US30":   "US30.cash"
  }
}
```

Если символа нет в маппинге — используется как есть (EURUSD → EURUSD). ConnectorManager подставляет маппинг прозрачно: стратегия прислала `ENTER EURUSD` → ConnectorManager видит что терминал IC-Markets → подставляет `EURUSDi` → отправляет в воркер. В обратную сторону: воркер вернул позицию `EURUSDi` → ConnectorManager переводит обратно в `EURUSD` → стратегия видит каноническое имя.

Добавить в ConnectorManager:
- `MapSymbol(terminalId, canonicalSymbol) → brokerSymbol`
- `UnmapSymbol(terminalId, brokerSymbol) → canonicalSymbol`

Два dict lookup. Без этого — при каждом новом брокере нужно патчить стратегию.

### Шаг 2.4 — Тестирование

Написать простую `Program.cs` которая:

1. Создаёт `ConnectorManager`.
2. Запускает воркеры.
3. Вызывает `GetAccountInfo` на каждом терминале, печатает баланс.
4. Вызывает `GetPositions`, печатает открытые позиции.
5. Вызывает `GetSymbolInfo("EURUSD")`, печатает карточку.
6. Завершается, воркеры останавливаются.

### Критерий завершения фазы 2

Демон стартует, поднимает воркеры для всех терминалов из конфига, получает данные из каждого. Heartbeat работает. При остановке демона воркеры корректно завершаются.

---

## ФАЗА 3: State Manager + SQLite

Цель: демон сохраняет и восстанавливает своё состояние. При перезапуске знает что было открыто.

### Шаг 3.1 — Схема БД

```sql
-- Профили терминалов (настройки оператора)
CREATE TABLE terminal_profiles (
    terminal_id     TEXT PRIMARY KEY,
    type            TEXT NOT NULL,         -- prop/real/demo/test
    account_type    TEXT NOT NULL DEFAULT 'hedge',  -- hedge/netting
    mode            TEXT NOT NULL,         -- auto/semi/monitor
    server_utc_offset INTEGER NOT NULL DEFAULT 0,   -- часовой пояс брокера (часы)
    daily_dd_limit  REAL NOT NULL,
    cum_dd_limit    REAL NOT NULL,
    max_risk_trade  REAL NOT NULL,
    risk_type       TEXT NOT NULL,         -- usd/pct
    max_margin_trade REAL NOT NULL,
    max_deposit_load REAL NOT NULL,
    news_guard_on   INTEGER NOT NULL DEFAULT 1,
    news_window_min INTEGER NOT NULL DEFAULT 15,
    news_min_impact INTEGER NOT NULL DEFAULT 2,
    news_be_enabled INTEGER NOT NULL DEFAULT 1,
    news_include_usd INTEGER NOT NULL DEFAULT 1,    -- USD новости для всех инструментов
    sl3_guard_on    INTEGER NOT NULL DEFAULT 1,
    volume_mode     TEXT NOT NULL DEFAULT 'full',
    updated_at      TEXT NOT NULL
);

-- Известные позиции (книга учёта демона)
CREATE TABLE positions (
    ticket          INTEGER NOT NULL,
    terminal_id     TEXT NOT NULL,
    symbol          TEXT NOT NULL,
    direction       TEXT NOT NULL,         -- LONG/SHORT
    volume          REAL NOT NULL,
    price_open      REAL NOT NULL,
    sl              REAL NOT NULL,
    tp              REAL NOT NULL DEFAULT 0,
    magic           INTEGER NOT NULL DEFAULT 0,
    source          TEXT NOT NULL,         -- strategy_name / "manual" / "unmanaged"
    signal_data     TEXT,                  -- JSON: deviation, regime, etc.
    opened_at       TEXT NOT NULL,
    closed_at       TEXT,
    close_price     REAL,
    close_reason    TEXT,                  -- SL/TP/trailing/manual/emergency/signal
    pnl             REAL,
    PRIMARY KEY (ticket, terminal_id)
);

-- Журнал событий
CREATE TABLE events (
    id              INTEGER PRIMARY KEY AUTOINCREMENT,
    timestamp       TEXT NOT NULL,
    terminal_id     TEXT,
    type            TEXT NOT NULL,         -- ORDER/RISK/SIGNAL/SYSTEM/RECON/ERROR
    strategy        TEXT,
    message         TEXT NOT NULL,
    data            TEXT                   -- JSON доп.данные
);

-- Состояние стратегий (для восстановления при рестарте)
CREATE TABLE strategy_state (
    strategy_name   TEXT NOT NULL,
    terminal_id     TEXT NOT NULL,
    state_json      TEXT NOT NULL,
    saved_at        TEXT NOT NULL,
    PRIMARY KEY (strategy_name, terminal_id)
);

-- Счётчик 3SL Guard (per terminal)
CREATE TABLE sl3_state (
    terminal_id     TEXT PRIMARY KEY,
    consecutive_sl  INTEGER NOT NULL DEFAULT 0,
    blocked         INTEGER NOT NULL DEFAULT 0,
    blocked_at      TEXT,
    last_sl_at      TEXT
);

-- Daily P/L tracking (per terminal per day)
-- ВАЖНО: date определяется по серверному времени брокера (server_utc_offset из профиля)
CREATE TABLE daily_pnl (
    terminal_id     TEXT NOT NULL,
    date            TEXT NOT NULL,         -- YYYY-MM-DD по времени БРОКЕРА, не оператора
    realized_pnl    REAL NOT NULL DEFAULT 0,
    high_water_mark REAL NOT NULL DEFAULT 0,
    PRIMARY KEY (terminal_id, date)
);

-- Качество исполнения — для сравнения бэктеста с реальностью
CREATE TABLE execution_quality (
    id              INTEGER PRIMARY KEY AUTOINCREMENT,
    ticket          INTEGER NOT NULL,
    terminal_id     TEXT NOT NULL,
    symbol          TEXT NOT NULL,
    direction       TEXT NOT NULL,
    signal_price    REAL NOT NULL,         -- цена когда стратегия дала сигнал
    fill_price      REAL NOT NULL,         -- цена реального исполнения
    slippage_pts    REAL NOT NULL,         -- разница в пунктах (+ = хуже, - = лучше)
    signal_time     TEXT NOT NULL,         -- когда стратегия сказала ENTER
    fill_time       TEXT NOT NULL,         -- когда ордер исполнен
    latency_ms      INTEGER NOT NULL,      -- время от сигнала до fill
    strategy        TEXT NOT NULL
);
```

### Шаг 3.2 — StateManager.cs

Класс с методами:

```
Позиции:
  SavePosition(position) — записать/обновить позицию
  ClosePosition(ticket, terminalId, closePrice, closeReason, pnl)
  GetOpenPositions(terminalId) → List<Position>
  GetOpenPositions() → List<Position>  (все терминалы)
  GetPositionByTicket(ticket, terminalId) → Position?

Профили:
  GetProfile(terminalId) → TerminalProfile
  SaveProfile(profile)
  GetAllProfiles() → List<TerminalProfile>

3SL Guard:
  IncrementSLCount(terminalId) — +1 к счётчику
  ResetSLCount(terminalId) — сброс на 0
  Get3SLState(terminalId) → {count, blocked}
  Block3SL(terminalId) — заблокировать
  Unblock3SL(terminalId) — разблокировать (ручной или по времени)

Daily P/L:
  AddRealizedPnl(terminalId, amount)
  GetDailyPnl(terminalId, date) → {realized, hwm}
  UpdateHWM(terminalId, currentEquity)

События:
  LogEvent(type, terminalId, strategy, message, data)
  GetEvents(filter) → List<Event>

Стратегии:
  SaveStrategyState(name, terminalId, stateJson)
  GetStrategyState(name, terminalId) → string?

Execution Quality:
  LogExecution(ticket, terminalId, symbol, direction, signalPrice, fillPrice, signalTime, fillTime, strategy)
    → автоматически считает slippage_pts и latency_ms
  GetExecutionStats(terminalId, period) → {avgSlippage, avgLatency, count}
    → для dashboard и сравнения с бэктестом
```

### Шаг 3.3 — Тестирование

Написать тесты: создать БД, записать позиции, прочитать обратно, проверить. Симулировать 3 SL подряд — проверить что блокировка срабатывает. Записать daily P/L — проверить что суммируется правильно.

### Критерий завершения фазы 3

Демон сохраняет состояние в SQLite. При перезапуске — читает и восстанавливает. Все CRUD операции работают.

---

## ФАЗА 4: Reconciliation

Цель: при старте демон сверяет свою книгу учёта с реальными позициями в терминалах и разрешает расхождения.

### Шаг 4.1 — Логика сверки

```
Для каждого подключённого терминала:

1. Прочитать из state: List<Position> known (наши записи)
2. Запросить у терминала: List<Position> actual (реальность)

3. Для каждой known позиции:
   a. Найти в actual по ticket
   b. Если найдена:
      - Сравнить SL: если отличается → обновить state, залогировать
      - Сравнить volume: если отличается → обновить, залогировать
      - Статус: OK
   c. Если НЕ найдена:
      - Позиция закрылась пока демон не работал
      - Определить причину: запросить историю сделок (mt5.history_deals_get)
      - Обновить state: closed_at, close_price, close_reason, pnl
      - Обновить daily_pnl
      - Обновить 3SL counter если close_reason = SL
      - Залогировать

4. Для каждой actual позиции которой НЕТ в known:
   - Открыта кем-то другим (руками, другой EA)
   - Если magic совпадает с нашей стратегией → записать в state как нашу
   - Иначе → записать как source="unmanaged", показать в dashboard
   - Залогировать
```

### Шаг 4.2 — Определение причины закрытия

Воркеру нужна новая команда:

```json
{"cmd": "HISTORY_DEALS", "id": 7, "ticket": 12345, "from_date": "2025-02-13"}
```

Которая вызывает `mt5.history_deals_get(position=ticket)` и возвращает список сделок по этой позиции. Из последней сделки определяем: SL, TP, или market close.

### Шаг 4.3 — Hot Reconciliation

Периодическая сверка во время работы (каждые 30 секунд):
- Сравнить список позиций из state с терминалом
- Если позиция исчезла — обработать как закрытие
- Если появилась новая — обработать как в п.4 выше

### Критерий завершения фазы 4

Тест: открыть позицию руками в терминале, запустить демон — он должен увидеть её как unmanaged. Закрыть позицию руками при работающем демоне — он должен обновить state в течение 30 секунд. Остановить демон, закрыть позицию по SL, запустить демон — он должен определить причину из истории и обновить счётчик 3SL.

---

## ФАЗА 5: Risk Manager

Цель: каждый ордер проходит цепочку проверок. Ни один ордер не уходит в терминал без одобрения Risk Manager.

### Шаг 5.1 — LotCalculator

Отдельный класс. Входные данные:
- entry_price (текущий ask/bid)
- sl_price (от стратегии)
- risk_money (из Terminal Profile: $1000 или 1% от balance)
- InstrumentCard (из терминала: tick_size, tick_value, volume_min/max/step)

Расчёт:

```
distance = |entry_price - sl_price|
ticks = distance / tick_size
value_per_lot_per_tick = tick_value
lot = risk_money / (ticks * value_per_lot_per_tick)

// Округление к ближайшему шагу вниз
lot = floor(lot / volume_step) * volume_step

// Проверки
if lot < volume_min → отклонить (риск слишком маленький для минимального лота)
if lot > volume_max → lot = volume_max (и залогировать предупреждение)

// Специальный случай: tick_value_loss может отличаться от tick_value_profit
// Для расчёта лота при SL используем tick_value_loss
```

**Важно**: для кросс-пар (EURGBP, AUDJPY) tick_value зависит от курса валюты profit к валюте аккаунта. MT5 отдаёт актуальное значение в symbol_info, поэтому карточку нужно обновлять перед расчётом лота (или использовать `mt5.order_calc_profit()` через воркер для точного расчёта).

### Шаг 5.2 — GateChain

Последовательная проверка. Каждый гейт возвращает `(allowed: bool, reason: string)`.

```
Gate 1: Operating Mode
  - Читаем mode из Terminal Profile
  - monitor → reject, reason: "Monitor only mode"
  - semi → пометить "pending_approval", вернуть в dashboard
  - auto → pass

Gate 2: Daily DD
  - ВАЖНО: "сегодня" определяется по серверному времени БРОКЕРА (server_utc_offset)
  - The5ers (UTC+2): день начинается в 00:00 UTC+2
  - AudaCity (UTC+0): день начинается в 00:00 UTC+0
  - Текущий daily_pnl + unrealized P/L с учетом всех позиций
  - Если |daily_loss| + trade_risk > daily_limit → reject

Gate 3: Cumulative DD
  - equity сейчас vs high_water_mark
  - Если HWM - equity + trade_risk > cum_limit → reject

Gate 4: Risk Per Trade
  - trade_risk = |entry - sl| * lot * tick_value (уже посчитано в LotCalculator)
  - Если > max_risk_trade → reject

Gate 5: Margin Per Trade
  - Вызвать через воркер: mt5.order_calc_margin(action, symbol, lot, price)
  - Если margin > max_margin_trade * balance / 100 → reject

Gate 6: Deposit Load
  - Текущая margin_used (из account_info) + margin нового ордера
  - Если суммарная > max_deposit_load * balance / 100 → reject

Gate 7: News Window
  - Спросить NewsService: есть ли block сейчас для валют этого символа?
  - Если да → reject

Gate 8: 3SL Guard
  - Спросить StateManager: blocked?
  - Если да → reject

Gate 9: Netting Check (только для account_type = netting)
  - Есть ли уже открытая позиция по этому символу?
  - Если да и в том же направлении → reject (нельзя добавлять к позиции)
  - Если да и в противоположном → reject (закрытие через стратегию, не через новый вход)
  - Для hedge-счетов этот гейт пропускается
```

### Шаг 5.3 — NewsService

Один экземпляр на весь демон. При старте:

1. Для каждого подключённого терминала запросить `CALENDAR` через воркер.
2. Объединить события (убрать дубли — разные терминалы вернут одни и те же новости).
3. Построить кэш: `List<NewsEvent>` отсортированный по времени.
4. Для каждого символа из конфига — определить релевантные валюты (логика из вашего NewsGuard: EURUSD → EUR+USD, XAUUSD → USD, DAX → EUR, etc.).

**ВАЖНО: USD-новости (NFP, CPI, FOMC, etc.) всегда релевантны для ВСЕХ инструментов.** Флаг `news_include_usd` в Terminal Profile включён по умолчанию. USD двигает всё — золото, индексы, кросс-пары через корреляцию.

Метод `IsBlocked(symbol, DateTime now) → (blocked, eventName, minutesTo)`:

```
Для каждого события в кэше:
  Если валюта события == "USD" и news_include_usd == true → релевантно
  Иначе если валюта события релевантна для symbol (маппинг)
  
  И now >= event.time - window_minutes
  И now <= event.time + window_minutes
  → return (true, eventName, minutesToEvent)
```

Обновление кэша: каждые 12 часов запрашивать заново.

### Шаг 5.4 — ActiveProtection

Фоновый процесс. Каждые 10 секунд:

1. Для каждого терминала: запросить account_info (equity).
2. Посчитать текущий daily P/L (realized + unrealized). **ВАЖНО: "сегодня" по серверному времени брокера.**
3. Если daily P/L превысил 80% лимита → жёлтый алерт в dashboard + Telegram.
4. Если daily P/L превысил 95% лимита → красный алерт + Telegram + опционально приостановить стратегии.
5. Если daily P/L достиг лимита → EMERGENCY: закрыть все позиции на этом терминале, заблокировать торговлю, Telegram.

Алерты отправляются через `AlertService` — единый канал. Dashboard push + Telegram одновременно. AlertService использует debounce: один и тот же алерт не чаще раза в 5 минут (чтобы не заспамить Telegram при осцилляции equity около порога).

Также: если NewsService показывает block window и есть позиция в плюсе → подвинуть SL на break-even (логика из вашего NewsGuard: entry ± 2*spread).

### Критерий завершения фазы 5

Тест: вручную отправить ордер через демон с нарушением каждого гейта — каждый раз должен быть reject с правильной причиной. Протестировать LotCalculator на разных инструментах (EURUSD, XAUUSD, USDJPY, GBPJPY) — лот должен давать заданный risk в долларах.

---

## ФАЗА 6: Strategy Protocol + Runner

Цель: демон умеет запускать Python стратегию, отправлять ей бары, получать сигналы.

### Шаг 6.1 — Runner (Python)

Файл: `strategies/runner.py` — универсальный для всех стратегий.

```
1. Аргументы: --port, --strategy (имя папки), --config (путь к config.json стратегии)
2. Импортировать strategy.py из указанной папки
3. Создать экземпляр стратегии, передать конфиг
4. Подключиться к демону по TCP (демон слушает, runner подключается)
5. Отправить HELLO с requirements (список символов и таймфреймов из конфига)
6. Получить ACK (может содержать сохранённый state)
7. Цикл:
   - Получить TICK (бары + позиции)
   - Вызвать strategy.on_bars(bars, positions)
   - Отправить ACTIONS (список: ENTER/EXIT/MODIFY_SL, или пустой)
   - Каждые 5 секунд без TICK — отправить HEARTBEAT
   - При получении STOP — вызвать strategy.save_state(), отправить GOODBYE
```

### Шаг 6.2 — Интерфейс стратегии

Каждая стратегия реализует:

```python
class Strategy:
    def __init__(self, config: dict):
        """Инициализация. config — параметры из config.json стратегии."""
        pass
    
    def get_requirements(self) -> dict:
        """Что нужно от демона: символы, таймфреймы, глубина истории."""
        return {
            "symbols": [...],
            "timeframes": {...},
            "history_bars": 300
        }
    
    def on_bars(self, bars: dict, positions: list) -> list:
        """Главная функция. Получает данные, возвращает действия.
        
        bars: {"EURUSD": DataFrame, "GBPUSD": DataFrame, ...}
        positions: [{"ticket":..., "symbol":..., "direction":..., "sl":...}, ...]
        
        returns: [{"action": "ENTER", "symbol": "EURUSD", "direction": "LONG", "sl_price": 1.083}, ...]
        """
        return []
    
    def save_state(self) -> dict:
        """Сохранить внутреннее состояние для восстановления."""
        return {}
    
    def restore_state(self, state: dict):
        """Восстановить состояние после перезапуска."""
        pass
```

### Шаг 6.3 — StrategyManager (C#)

Класс в демоне:

1. Знает папку `strategies/` и что в ней лежит (сканирует при старте).
2. По команде из dashboard: запустить стратегию X на терминале Y.
3. Создаёт `StrategyProcess`: открывает TCP listener на свободном порту, запускает `python runner.py --port {port} --strategy {name}`.
4. Ждёт HELLO от стратегии, проверяет requirements, отправляет ACK (с сохранённым state если есть).
5. Регистрирует стратегию в Scheduler: "для терминала Y, при закрытии свечи H1, отправить TICK стратегии X".

### Шаг 6.4 — Scheduler

Знает какие таймфреймы нужны каждой стратегии. Работает так:

```
Каждые 10 секунд:
  Для каждой running стратегии:
    Для каждого символа стратегии:
      Проверить: закрылась ли новая свеча на нужном таймфрейме?
      (Сравниваем time последнего бара с предыдущим запросом)
    
    Если хотя бы одна новая свеча:
      Собрать бары по всем символам стратегии (через ConnectorManager)
      Собрать позиции стратегии (из StateManager, фильтр по magic)
      Отправить TICK стратегии
      Получить ACTIONS
      Для каждого action:
        Если ENTER → рассчитать лот (LotCalculator) → проверить (RiskManager) → исполнить (ConnectorManager) → записать (StateManager)
        Если EXIT → исполнить → записать
        Если MODIFY_SL → исполнить → записать
```

### Шаг 6.5 — Тестирование

Создать тестовую стратегию `strategies/test_strategy/strategy.py` которая:
- Требует один символ EURUSD H1.
- На каждом тике печатает полученные бары (последний бар).
- Каждый 5-й тик генерирует сигнал ENTER EURUSD LONG с SL = close - 50 pips.
- Через 3 тика после входа генерирует EXIT.

Запустить демон → подключить стратегию к тестовому терминалу → проверить что ордера открываются и закрываются. Проверить что Risk Manager отклоняет ордер если выставить daily_dd_limit = $1.

### Критерий завершения фазы 6

Полный цикл: демон → воркер → стратегия → сигнал → risk check → ордер → позиция в терминале → reconciliation → state обновлён. Тестовая стратегия торгует на демо автоматически.

---

## ФАЗА 7: Dashboard

Цель: web интерфейс для управления демоном.

### Шаг 7.1 — HTTP + WebSocket сервер

В C# проекте:
- Встроенный `HttpListener` или Kestrel (если .NET 8).
- Отдаёт статические файлы из папки `wwwroot/` (HTML, JS, CSS).
- WebSocket endpoint `/ws` — двусторонний канал для real-time обновлений.

### Шаг 7.2 — WebSocket протокол

Dashboard → Демон (команды):

```json
{"cmd": "get_terminals"}
{"cmd": "get_positions"}
{"cmd": "get_strategies"}
{"cmd": "get_events", "filter": {"type": "ORDER", "limit": 50}}
{"cmd": "start_strategy", "strategy": "bb_mr_v2", "terminal": "FTMO-1"}
{"cmd": "stop_strategy", "strategy": "bb_mr_v2", "terminal": "FTMO-1"}
{"cmd": "pause_strategy", "strategy": "bb_mr_v2", "terminal": "FTMO-1"}
{"cmd": "close_position", "terminal": "FTMO-1", "ticket": 12345}
{"cmd": "close_all", "terminal": "FTMO-1"}
{"cmd": "emergency_close_all"}
{"cmd": "save_profile", "terminal": "FTMO-1", "profile": {...}}
{"cmd": "connect_terminal", "terminal": "Test"}
{"cmd": "approve_order", "order_id": 1}
{"cmd": "reject_order", "order_id": 1}
{"cmd": "unblock_3sl", "terminal": "FTMO-1"}
{"cmd": "toggle_news_guard", "terminal": "FTMO-1"}
```

Демон → Dashboard (обновления, pushes):

```json
{"event": "terminal_status", "data": {"id": "FTMO-1", "status": "connected", ...}}
{"event": "position_update", "data": {"ticket": 12345, "pnl": 80, ...}}
{"event": "position_opened", "data": {...}}
{"event": "position_closed", "data": {...}}
{"event": "strategy_status", "data": {"name": "bb_mr_v2", "status": "running"}}
{"event": "risk_alert", "data": {"terminal": "FTMO-1", "type": "daily_dd", "level": "warning", "pct": 82}}
{"event": "order_pending", "data": {"id": 1, "symbol": "EURUSD", ...}}
{"event": "log_entry", "data": {"time": "...", "type": "ORDER", ...}}
```

### Шаг 7.3 — AlertService + Telegram

`AlertService` — единый канал алертов. При событии → отправляет в два места одновременно:
1. Dashboard (через WebSocket push).
2. Telegram (через Bot API).

Telegram конфигурация в config.json:

```json
{
  "alerts": {
    "telegram_enabled": true,
    "telegram_bot_token": "ENV:TELEGRAM_BOT_TOKEN",
    "telegram_chat_id": "ENV:TELEGRAM_CHAT_ID",
    "debounce_seconds": 300
  }
}
```

Реализация `TelegramAlert.cs` — один HTTP POST:

```
POST https://api.telegram.org/bot{token}/sendMessage
body: {"chat_id": "...", "text": "🔴 FTMO-1: Daily DD 95% ($4,750 / $5,000)", "parse_mode": "HTML"}
```

Типы алертов:
- 🟡 DD Warning (80%) — информация
- 🔴 DD Critical (95%) — требует внимания
- ⛔ DD Limit reached — позиции закрыты автоматически
- ⚠ 3SL triggered — торговля приостановлена
- 📰 News block — вход заблокирован
- ❌ Terminal disconnected
- ❌ Strategy disconnected
- 🚨 Emergency close all

Debounce: один и тот же тип алерта для одного терминала — не чаще раза в 5 минут.

### Шаг 7.4 — Frontend

Взять прототип dashboard (HTML файл который мы уже сделали). Заменить mock данные на WebSocket подключение. При открытии страницы:

1. Подключиться к `ws://localhost:8080/ws`.
2. Запросить `get_terminals`, `get_positions`, `get_strategies`, `get_events`.
3. Заполнить UI полученными данными.
4. Слушать push-события, обновлять UI в реальном времени.

Кнопки привязать к WebSocket командам: нажал "Close" → отправить `close_position`, нажал "Start" → отправить `start_strategy`.

### Шаг 7.5 — Тестирование

Открыть dashboard, убедиться:
- Терминалы показывают правильный статус и баланс.
- Позиции обновляются в реальном времени (P/L меняется).
- Кнопка Close закрывает позицию (проверить в MT5).
- Start/Stop стратегии работает.
- Emergency Close All закрывает всё.
- Лог показывает события в реальном времени.

### Критерий завершения фазы 7

Оператор может полностью управлять демоном из браузера: подключать терминалы, настраивать профили, запускать/останавливать стратегии, закрывать позиции, видеть все риски и логи.

---

## ФАЗА 8: Интеграция BB_MR_v2

Цель: перенести реальную стратегию из бэктеста в формат Strategy Protocol.

### Шаг 8.1 — Подготовка strategy.py

Взять код из бэктеста. Выделить в класс `BBMeanReversionV2`:
- `__init__`: загрузить конфиг (bb_mr_config.csv → dict), загрузить ML модели если есть.
- `get_requirements`: вернуть список символов и таймфреймов из конфига.
- `on_bars`: для каждого символа посчитать BB, проверить entry/exit, вернуть actions.

Ключевое: функции `compute_bb()`, `check_entry()`, `check_exit()` — копируются из бэктеста без изменений. Оборачиваются в класс.

### Шаг 8.2 — config.json стратегии

Генерируется из bb_mr_config.csv:

```json
{
  "EURUSD": {"tf": "H1", "dev_l": 2.5, "n_l": 200, "exit_l": "C_half",
             "dev_s": 2.5, "n_s": 200, "exit_s": "C_half"},
  "GBPUSD": {"tf": "H1", "dev_l": 2.5, "n_l": 200, ...},
  ...
}
```

### Шаг 8.3 — Валидация

Прогнать стратегию на исторических данных через демон (в режиме Monitor Only):
1. Запустить стратегию на Test терминале.
2. Собрать все сигналы за N дней (из лога).
3. Сравнить с результатами бэктеста за те же дни.
4. Если расхождение — искать баг.

### Критерий завершения фазы 8

BB_MR_v2 торгует на демо-аккаунте автоматически. Сигналы совпадают с бэктестом. Ордера исполняются, SL управляются, trailing работает.

---

## ФАЗА 9: Боевое тестирование (на локальной машине)

Цель: убедиться что система надёжна. Всё тестируется локально, без security обвязки. Dashboard на localhost, пароли в .env. Security — фаза 9.5, перед переносом на VPS.

### Шаг 9.1 — Daemon Watchdog

Демон должен автоматически перезапускаться если упал. В Program.cs при старте:

```csharp
// Named mutex — не запускать второй экземпляр
using var mutex = new Mutex(true, "TradingDaemon", out bool created);
if (!created) {
    Console.WriteLine("Daemon already running, exiting.");
    return;
}
```

Windows Task Scheduler: задача `TradingDaemon_Watchdog`, запуск `daemon.exe` каждые 5 минут. Если демон уже работает — mutex не даст запустить второй, процесс сразу выйдет. Если демон упал — Task Scheduler поднимет заново. Настроить: "Run whether user is logged on or not", "Run with highest privileges", trigger "At startup" + trigger "Repeat every 5 minutes".

Это три строки кода + одна задача в Task Scheduler. Без этого: VPS перезагрузился ночью, обновился Windows, завис процесс — и вы утром обнаруживаете что демон не работал 8 часов.

### Шаг 9.2 — Стресс-тесты

1. Убить процесс воркера во время торговли → демон должен обнаружить, перезапустить, сделать reconciliation.
2. Убить процесс стратегии → демон обнаруживает, алертит, позиции с SL остаются.
3. Перезапустить терминал MT5 → воркер переподключается, reconciliation.
4. Перезапустить демон при открытых позициях → при старте reconciliation, подхватить управление.
5. Отключить интернет на 5 минут → терминал отключается, воркер видит disconnect, при восстановлении — reconciliation.
6. Убить daemon.exe → Task Scheduler перезапускает через ≤5 минут → reconciliation.

### Шаг 9.3 — Неделя на демо

Запустить полный стек (демон + BB_MR_v2 + два терминала) на неделю. Каждый день проверять:
- Все позиции в state совпадают с терминалом?
- Daily P/L считается правильно (по серверному времени брокера)?
- 3SL guard сработал когда нужно?
- News guard заблокировал когда нужно?
- Нет утечек памяти (процессы не растут)?
- Symbol mapping работает корректно на всех терминалах?

### Шаг 9.4 — Анализ качества исполнения

После недели торговли — выгрузить execution_quality из SQLite. Проверить:

- Средний slippage по инструментам. Норма для форекса: 0-1 пункт. Если >3 — проблема с брокером или таймингом.
- Средняя latency (сигнал → fill). Норма на VPS: <500ms. Если >2 сек — проблема с воркером или терминалом.
- Сравнить с бэктестом: WR и Exp за эту неделю в рамках ожиданий? Slippage не съедает edge?

Это главный ответ на вопрос "работает ли стратегия в реале как в бэктесте".

### Шаг 9.5 — Документация

Записать:
- Как развернуть на новом VPS с нуля.
- Как добавить новый терминал (включая symbol mapping).
- Как добавить новую стратегию.
- Что делать при каждом типе ошибки.

### Критерий завершения фазы 9

Неделя без вмешательства на локальной машине, все стресс-тесты пройдены, расхождений нет. Система готова к фазе 9.5 (security) и переносу на VPS.

---

## ПОРЯДОК И ОЦЕНКА ВРЕМЕНИ

```
Фаза 0: Подготовка среды              1 день
Фаза 1: MT5 Worker                    3-5 дней
Фаза 2: Connector Manager             3-5 дней
Фаза 3: State Manager + SQLite        2-3 дня
Фаза 4: Reconciliation                3-5 дней
Фаза 5: Risk Manager (9 гейтов)       5-7 дней
Фаза 6: Strategy Protocol + Runner    5-7 дней
Фаза 7: Dashboard + AlertService      5-8 дней
Фаза 8: Интеграция BB_MR_v2           3-5 дней
Фаза 9: Боевое тестирование (локально) 7-14 дней
--- после успешных локальных тестов ---
Фаза 9.5: Security (перед VPS)        2-3 дня
--- перенос на VPS ---

ИТОГО: 6-9 недель локальная разработка + тесты
       + 1 неделя security + перенос на VPS
```

Фазы 1-4 можно делать последовательно — каждая зависит от предыдущей. Фаза 7 (Dashboard) можно начинать параллельно с фазой 5, используя mock данные и доводить по мере готовности бэкенда. Фаза 5 и 6 можно вести параллельно если два человека.

---

## ЗАВИСИМОСТИ МЕЖДУ ФАЗАМИ

```
Фаза 0 ──→ Фаза 1 ──→ Фаза 2 ──→ Фаза 4
                         │
                         ├──→ Фаза 3 ──→ Фаза 4
                         │
                         └──→ Фаза 7 (параллельно, на mock данных)

Фаза 3 + Фаза 4 ──→ Фаза 5
Фаза 2 + Фаза 5 ──→ Фаза 6
Фаза 6 + Фаза 7 ──→ Фаза 8 ──→ Фаза 9 (локально)

--- всё работает на локальной машине ---

Фаза 9 ──→ Фаза 9.5 (security) ──→ перенос на VPS ──→ production
```

---

## ФАЗА 9.5: SECURITY — перед переносом на VPS

НЕ делаем во время локальной разработки и тестирования. Реализуем и тестируем ПЕРЕД переносом на production VPS. На локальной машине dashboard на localhost, пароли в .env — достаточно.

### Шаг 9.5.1 — Хранение секретов

Принцип: config.json не содержит ни одного пароля. Только ссылки на переменные окружения.

```json
{
  "terminals": [
    {"id": "FTMO-1", "login": 12345, "password": "ENV:MT5_FTMO_PASS", "server": "FTMO-Demo"}
  ],
  "exchanges": [
    {"id": "OKX-main", "api_key": "ENV:OKX_API_KEY", "api_secret": "ENV:OKX_API_SECRET",
     "passphrase": "ENV:OKX_PASSPHRASE"}
  ],
  "alerts": {
    "telegram_bot_token": "ENV:TELEGRAM_BOT_TOKEN",
    "telegram_chat_id": "ENV:TELEGRAM_CHAT_ID"
  },
  "dashboard": {
    "auth_token": "ENV:DASHBOARD_TOKEN"
  }
}
```

Демон при старте: видит `"ENV:MT5_FTMO_PASS"` → читает переменную окружения `MT5_FTMO_PASS` → использует. В логах, state.db, dashboard — пароли не появляются никогда.

Источник переменных окружения:

**Этап 1 — .env файл.** Рядом с daemon.exe. Права доступа только для текущего пользователя Windows. Не коммитится в git.

```
MT5_FTMO_PASS=MySecretPassword123
MT5_IC_PASS=AnotherPassword456
OKX_API_KEY=abc123...
OKX_API_SECRET=def456...
OKX_PASSPHRASE=mypass
TELEGRAM_BOT_TOKEN=123456:ABC-DEF...
TELEGRAM_CHAT_ID=987654321
DASHBOARD_TOKEN=random-long-string-here
```

**Этап 2 — Windows Credential Manager.** Секреты зашифрованы ключом Windows-аккаунта. Даже если скопировать диск VPS — без логина не расшифровать. Демон читает через `System.Security.Cryptography.ProtectedData`.

### Шаг 9.5.2 — Транспорт: WireGuard VPN

WireGuard VPN на VPS — только для доступа оператора к dashboard. Торговые соединения (MT5 → брокеры, ccxt → OKX, Telegram API) идут напрямую, без VPN.

```
УСТРОЙСТВА ОПЕРАТОРА                    VPS
┌──────────────┐                        ┌──────────────────────────────┐
│ Компьютер    │                        │                              │
│ Телефон      │◄── WireGuard VPN ────►│  Dashboard :8080             │
│              │    (10.0.0.x сеть)     │  (слушает ТОЛЬКО 10.0.0.1)  │
└──────────────┘                        │                              │
                                        │  daemon.exe                  │
    Из интернета dashboard              │    ├── MT5 Worker ──────► FTMO
    НЕ ВИДЕН. Порт 8080                │    ├── MT5 Worker ──────► IC-Markets
    закрыт в firewall.                  │    ├── OKX Worker ──────► OKX API
                                        │    └── Telegram ────────► Telegram
                                        │                              │
                                        │  MT5 Terminal ×3 ──────► брокеры
                                        └──────────────────────────────┘
```

Настройка:

1. Установить WireGuard на VPS: `apt install wireguard` (если Linux) или WireGuard for Windows.
2. Сгенерировать ключи: server + client (компьютер) + client (телефон).
3. VPS конфиг: `Address = 10.0.0.1/24`, `ListenPort = 51820`.
4. Client конфиг: `Endpoint = vps-ip:51820`, `AllowedIPs = 10.0.0.1/32` (только трафик к VPS через VPN, остальной интернет напрямую).
5. Dashboard настроить слушать на `10.0.0.1:8080` (не `0.0.0.0`).
6. Firewall VPS: открыт только 51820/UDP снаружи. Порт 8080 закрыт снаружи.

WireGuard клиенты: Windows — официальный, Android — WireGuard app, iOS — WireGuard app. Подключение в одно нажатие.

### Шаг 9.5.3 — Аутентификация dashboard

Даже внутри VPN — dashboard требует токен. Защита от случая когда кто-то получил доступ к VPN (украли телефон).

При открытии dashboard — форма логина. Токен из config.json (`ENV:DASHBOARD_TOKEN`). После ввода — сохраняется в browser session. Каждый WebSocket message содержит токен в header. Нет токена → disconnect.

### Шаг 9.5.4 — OKX API ключи

При создании API key на OKX обязательно настроить:

1. **IP whitelist** — ключ работает только с IP вашего VPS. Украли ключ → с другого IP бесполезен.
2. **Permission: Trade only** — никогда не давать withdraw permission через API.
3. **Sub-account** — торговать через sub-account, не основной. Ограничивает ущерб.

### Шаг 9.5.5 — Audit log

Каждое действие через dashboard записывается в events table:

```json
{
  "type": "DASHBOARD",
  "source_ip": "10.0.0.2",
  "action": "close_position",
  "terminal": "FTMO-1",
  "ticket": 12345,
  "timestamp": "2025-03-01T10:15:03Z"
}
```

### Шаг 9.5.6 — Тестирование безопасности

1. Попробовать открыть dashboard из интернета (не через VPN) → не должно работать.
2. Подключиться через VPN без токена → логин-форма, не пускает.
3. Проверить что .env / credentials не утекают в логи.
4. Проверить что config.json не содержит паролей в открытом виде.
5. Для OKX: попробовать запрос с другого IP → отказ.

### Критерий завершения

Dashboard доступен только через VPN + токен. Секреты зашифрованы. OKX ключи с IP-whitelist. Audit log пишется. Можно переносить на production VPS.

---

## TODO — ПОСЛЕ ЗАПУСКА ОСНОВНОЙ СИСТЕМЫ

Расширения которые не входят в MVP, но заложены в архитектуре.

### TODO 1: Dashboard как точка запуска

Сейчас: запускаем daemon.exe → он поднимает dashboard → открываем браузер.
Хотим: открываем приложение → из него запускаем демон и всё остальное.

**Фаза A — Tray launcher.** Маленький .exe который садится в системный трей Windows. При клике → открывает браузер с dashboard. Кнопки в меню трея: Start Daemon, Stop Daemon, Open Dashboard. При загрузке Windows — автозапуск launcher через реестр.

**Фаза B — Desktop app.** Electron или .NET MAUI приложение. Встроенный браузер показывает dashboard. Демон встроен или запускается как дочерний процесс. Одно окно вместо трёх (cmd + browser + terminal). Иконка в taskbar с индикацией статуса.

**Фаза C — Mobile app (Android).** WebSocket клиент к демону (через VPN/tunnel к VPS). Мониторинг + emergency кнопки. Не full dashboard — только статус, алерты, Close All. Основное управление через Telegram + мобильный dashboard.

### TODO 2: Подключение криптобиржи OKX

Архитектура уже готова. Нужно написать `OKXWorker` — аналог MT5 Worker, но через ccxt/OKX API.

```
workers/
├── mt5_worker.py           ← уже есть
└── okx_worker.py           ← новый
```

OKX Worker реализует тот же набор команд (HEARTBEAT, ACCOUNT_INFO, GET_POSITIONS, GET_RATES, ORDER_SEND, SYMBOL_INFO), но вызывает ccxt вместо mt5. ConnectorManager работает с ним через тот же IConnector интерфейс.

Нюансы:
- OKX — futures, есть funding rate (учесть в P/L)
- Margin model отличается (cross/isolated)
- Нет MqlCalendar — NewsService использует внешний API (ForexFactory, Investing.com)
- Rate limits — OKX ограничивает частоту запросов (10 req/sec на endpoint)
- WebSocket API OKX — можно подписаться на тики вместо polling

Спецификация уже написана (BB_CRYPTO_EXCHANGE_SPEC.md в проекте).

### TODO 3: Equity chart в dashboard

Should Have из оригинального плана. Per terminal, обновляется в реальном времени.
- Lightweight-charts (TradingView open source) или Chart.js
- Данные: equity сэмплируется каждые 5 минут, хранится в SQLite
- Отметки: входы (зелёные/красные стрелки), выходы, DD warning levels
- Переключатель: день / неделя / месяц / всё время

### TODO 4: Trade History + Analytics

Таблица закрытых сделок с фильтрами и экспортом в CSV.

Analytics page:
- WR, Expectancy, Profit Factor за период
- Сравнение бэктест vs лайв (загрузить CSV из бэктеста, наложить)
- Distribution: P/L по символам, по дням недели, по часам
- Streak analysis: максимальная серия побед/проигрышей

### TODO 5: Multi-strategy per terminal

Сейчас: одна стратегия на один терминал. TODO: несколько стратегий на одном терминале с разными magic numbers. Risk Manager суммирует риски всех стратегий. Dashboard показывает P/L per strategy. Нужен арбитраж конфликтов: стратегия A хочет Long EURUSD, стратегия B хочет Short EURUSD на том же терминале.

### TODO 6: Графики инструментов в dashboard

Свечной график с BB полосами прямо в dashboard. Lightweight-charts + WebSocket feed от демона. Отметки точек входа/выхода. Полезно для визуального контроля: "правильно ли стратегия видит этот инструмент?"

---

## CHANGELOG

- v1.0 — Initial plan, 9 phases
- v1.1 — Added: account_type hedge/netting (Gate 9), server timezone tracking for daily DD, USD always-included in NewsService, AlertService + Telegram, TODO section
- v1.2 — Added: Phase 9.5 Security (WireGuard VPN, secret storage, dashboard auth, OKX IP whitelist, audit log)
- v1.3 — Added: Symbol Mapping (EURUSD ↔ EURUSDi per terminal), Execution Quality log (slippage + latency tracking), Daemon Watchdog (named mutex + Task Scheduler auto-restart)
