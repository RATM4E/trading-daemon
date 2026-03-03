# TRADING DAEMON — ПЛАН РЕФАКТОРИНГА БЭКЕНДА v1.0

**Дата**: 25.02.2026
**Контекст**: Проект в стадии live-тестирования (Phase 10). Фронтенд уже отрефакторен (index.html → 11 модулей). Бэкенд — 15,541 строк C# + Python — требует аналогичного разделения.

**Цель**: Разбить три монолитных файла на логические модули без изменения функциональности.

---

## 1. Текущее состояние бэкенда

### Карта файлов (по размеру)

| Файл | Строки | Роль | Статус |
|---|---|---|---|
| **DashboardServer.cs** | 3,328 | HTTP/WS + 42 хэндлера + SQL + бизнес-логика | 🔴 Монолит |
| **StateManager.cs** | 1,737 | Схема БД + 25+ CRUD секций | 🔴 God object |
| **Program.cs** | 1,619 | Engine loop (~420) + legacy тесты (~1,200) | 🟡 Тесты не нужны |
| AlertService.cs | 1,180 | Telegram bot + команды + отчёты | 🟢 OK |
| Scheduler.cs | 1,040 | Обработка сигналов, ордера, виртуал | 🟢 OK |
| mt5_worker.py | 905 | TCP сервер + 13 команд MT5 | 🟢 OK |
| RiskManager.cs | 800 | 13 risk gates | 🟢 OK |
| StrategyManager.cs | 552 | Discovery, lifecycle, R-cap | 🟢 OK |
| NewsService.cs | 544 | ForexFactory + news blocking | 🟢 OK |
| StrategyProcess.cs | 524 | TCP to Python strategy runner | 🟢 OK |
| ActiveProtection.cs | 422 | SL management, trailing, protector | 🟢 OK |
| ConnectorManager.cs | 414 | MT5 worker routing + symbol mapping | 🟢 OK |
| WorkerProcess.cs | 394 | TCP client to mt5_worker.py | 🟢 OK |
| VirtualTracker.cs | 361 | Virtual SL/TP checks, PnL calc | 🟢 OK |
| runner.py | 325 | Python strategy TCP bridge | 🟢 OK |
| Reconciler.cs | 304 | Live ↔ DB position sync | 🟢 OK |
| Protocol.cs | 285 | Strategy↔Daemon protocol models | 🟢 OK |
| DaemonConfig.cs | 190 | Config JSON model | 🟢 OK |
| BarsCache.cs | 188 | OHLC bar caching | 🟢 OK |
| Models.cs | — | InstrumentCard, AccountInfo etc. | 🟢 OK |

---

## 2. Рефакторинг #1: Program.cs — вынести тесты

### Проблема

1,619 строк, из которых ~1,200 — legacy тесты (TestProtocol, TestTcpHandshake, TestRiskManager, TestTelegram, RunReconciliationTest). Проект в live-стадии, тесты не используются.

### План

**Удалить тестовые методы из Program.cs**, оставить только:
- `Main()` — точка входа, парсинг аргументов (13-30)
- `RunEngine()` — основной движок (36-455)
- `RunWithMT5()` — legacy MT5 режим (461-522, если ещё нужен)
- `DetectBrokerTimezone()` — хелпер (943-977)

### Методы к удалению

```
static async Task TestProtocol(ConsoleLogger log)           // 528-755
static async Task<bool> TestTcpHandshake(...)               // 757-941
static async Task TestTelegram(ConsoleLogger log)           // 993-1042
static async Task TestRiskManager(ConsoleLogger log)        // 1048-1423
static async Task RunReconciliationTest(...)                // 1499-1554
// + все вспомогательные assert методы и тестовые данные    // 1425-1619
```

### Что обновить в Main()

Убрать `case` ветки для тестовых режимов (`--test-protocol`, `--test-risk`, `--test-telegram`, `--test-recon`). Оставить только `--engine` (дефолт) и `--mt5` (если нужен).

### Результат

Program.cs: 1,619 → ~500 строк. Чистый entry point + engine loop.

### Риск: **Минимальный** — удаление мёртвого кода.

---

## 3. Рефакторинг #2: DashboardServer.cs → partial class

### Проблема

3,328 строк. 42 хэндлера для WebSocket команд, HTTP file server, SQL запросы, бизнес-логика — всё в одном файле. При каждом баг-фиксе приходится скроллить через тысячи строк.

### Подход

**partial class** — самый безопасный рефакторинг для C#. Один класс, разнесённый по файлам. Нулевое влияние на runtime — компилятор собирает всё обратно.

### Структура после разбивки

```
daemon/
├── Dashboard/
│   ├── DashboardServer.cs              ~550L   Ядро: HTTP listener, WS accept, 
│   │                                            dispatch switch, SendToClientAsync,
│   │                                            helpers (GetBrokerDate, FormatDuration)
│   │
│   ├── DashboardServer.Terminals.cs    ~1,200L  Терминалы и профили:
│   │                                            HandleGetTerminalsAsync (огромный ~250L)
│   │                                            HandleGetTerminalDetail
│   │                                            HandleDiscoverTerminals
│   │                                            HandleProbeTerminal
│   │                                            HandleAddDiscoveredTerminal
│   │                                            HandleToggleTerminalEnabled
│   │                                            HandleDeleteTerminal
│   │                                            HandleReorderTerminals
│   │                                            HandleSaveProfile
│   │                                            HandleSetMode
│   │                                            HandleToggle* (news, 3sl, no-trade)
│   │                                            HandleUnblock3SL
│   │                                            HandleResetFlags
│   │                                            HandleOpenStrategyFolder
│   │                                            DetectTimezone, DetectLeverageAsync
│   │                                            IsRCapReached
│   │
│   ├── DashboardServer.Trading.cs      ~800L   Позиции и стратегии:
│   │                                            HandleGetPositions
│   │                                            HandleClosePosition
│   │                                            HandleCloseAll
│   │                                            HandleEmergencyCloseAll
│   │                                            HandleGetStrategies
│   │                                            HandleStartStrategy
│   │                                            HandleStopStrategy
│   │                                            HandleReloadStrategy
│   │                                            HandleEnableStrategy
│   │                                            HandleDisableStrategy
│   │                                            HandleGetEvents
│   │                                            HandleGetSizing / HandleSaveSizing
│   │                                            HandleResetSizing
│   │                                            HandleGetLeverage / HandleSaveLeverage
│   │                                            HandleTogglePause / HandleGetPauseState
│   │
│   └── DashboardServer.Virtual.cs      ~500L   Виртуальная торговля:
│   │                                            HandleGetVirtualEquity
│   │                                            HandleGetVirtualStats
│   │                                            HandleGetTradeChart (~160L)
│   │                                            HandleResetVirtual
│   │                                            HandleExportVirtualCsv
│   │                                            GetClosedPositions helper
│   │                                            ClosedPositionRecord class
```

### Как выглядит partial class

```csharp
// DashboardServer.cs (ядро)
public partial class DashboardServer : IDisposable
{
    private readonly StateManager _state;
    // ... все поля, конструктор, Start/Stop, HTTP/WS, dispatch
}

// DashboardServer.Terminals.cs
public partial class DashboardServer
{
    private async Task<object> HandleGetTerminalsAsync() { ... }
    private object HandleGetTerminalDetail(JsonElement root) { ... }
    // ... все терминальные хэндлеры
}
```

### Dispatch остаётся в ядре

```csharp
// DashboardServer.cs — switch не меняется, просто методы теперь в других файлах
var response = cmd switch
{
    "get_terminals"    => await HandleGetTerminalsAsync(),      // в Terminals.cs
    "get_positions"    => await HandleGetPositions(root, ct),   // в Trading.cs
    "get_virtual_equity" => HandleGetVirtualEquity(root),       // в Virtual.cs
    ...
};
```

### Результат

| Файл | Строки | Содержание |
|---|---|---|
| DashboardServer.cs | ~550 | Инфраструктура + dispatch |
| DashboardServer.Terminals.cs | ~1,200 | Терминалы, профили, гарды |
| DashboardServer.Trading.cs | ~800 | Позиции, стратегии, ордера |
| DashboardServer.Virtual.cs | ~500 | Виртуал, графики, экспорт |

### Риск: **Низкий** — partial class компилируется идентично монолиту.

---

## 4. Рефакторинг #3: StateManager.cs → partial class

### Проблема

1,737 строк. 25+ секций по таблицам БД. Каждая секция — самодостаточный CRUD для одной таблицы. God object, который знает про всё.

### Структура после разбивки

```
daemon/
├── State/
│   ├── StateManager.cs                 ~350L   Ядро: конструктор, Open(), Exec(),
│   │                                            EnsureSchema() (CREATE TABLE),
│   │                                            CountDecimals, общие helpers
│   │
│   ├── StateManager.Positions.cs       ~250L   positions таблица:
│   │                                            SavePosition, ClosePosition,
│   │                                            GetOpenPositions, GetOpenVirtualPositions,
│   │                                            NextVirtualTicket, MarkProtectorFired
│   │
│   ├── StateManager.Profiles.cs        ~250L   terminal_profiles:
│   │                                            GetProfile, SaveProfile, 
│   │                                            GetAllProfiles, leverage classes,
│   │                                            GetEffectiveLeverage, DefaultLeverage
│   │
│   ├── StateManager.Virtual.cs         ~350L   Виртуальная торговля:
│   │                                            Virtual balance/margin (Get/Set/Init/Add)
│   │                                            ResetVirtualTrading, ResetBlockingFlags
│   │                                            Virtual equity snapshots
│   │                                            Virtual stats, SL history
│   │
│   ├── StateManager.Trading.cs         ~300L   Торговые данные:
│   │                                            Daily P&L, Daily R-cap, 
│   │                                            3SL Guard, Execution logging,
│   │                                            Trade snapshots
│   │
│   └── StateManager.Config.cs          ~250L   Конфигурация и стратегии:
│   │                                            Strategy state (enabled/started)
│   │                                            Strategy discovery cache
│   │                                            Events log
│   │                                            Daemon state (pause etc.)
│   │                                            Config persistence (sizing, symbol_map)
│   │                                            Purge methods
```

### Риск: **Низкий** — аналогично DashboardServer, partial class.

---

## 5. Порядок выполнения

| Шаг | Что | Описание | Время |
|---|---|---|---|
| 1 | **Program.cs: удалить тесты** | Удалить ~1,200 строк тестового кода, убрать case ветки | 15 мин |
| 2 | **DashboardServer → partial** | Разнести 42 хэндлера по 4 файлам | 1 сессия |
| 3 | **StateManager → partial** | Разнести 25+ секций по 6 файлам | 1 сессия |
| 4 | **Верификация** | Билд + запуск + проверка дашборда | 15 мин |

### Критически важно

- Каждый шаг — отдельный коммит. Не смешивать рефакторинг с фиксами.
- После каждого шага — полный билд и запуск.
- Не менять namespace, не менять имена классов, не менять сигнатуры методов.
- Все private поля остаются доступны во всех partial файлах.

---

## 6. Что НЕ рефакторим

- **AlertService.cs** (1,180L) — большой, но логически цельный: Telegram bot + команды. Разбивать нет смысла.
- **Scheduler.cs** (1,040L) — центральный координатор сигналов. Сложная логика, но единая ответственность.
- **RiskManager.cs** (800L) — 13 гейтов, линейная цепочка. Удобно читать последовательно.
- **mt5_worker.py** (905L) — Python, нет partial class. Размер приемлемый.

---

## 7. Ожидаемый результат

### До рефакторинга: 3 монолитных файла

```
DashboardServer.cs    3,328L
StateManager.cs       1,737L  
Program.cs            1,619L
                      ------
                      6,684L в 3 файлах
```

### После рефакторинга: 11 файлов

```
Dashboard/
  DashboardServer.cs              ~550L
  DashboardServer.Terminals.cs  ~1,200L
  DashboardServer.Trading.cs      ~800L
  DashboardServer.Virtual.cs      ~500L

State/
  StateManager.cs                 ~350L
  StateManager.Positions.cs       ~250L
  StateManager.Profiles.cs        ~250L
  StateManager.Virtual.cs         ~350L
  StateManager.Trading.cs         ~300L
  StateManager.Config.cs          ~250L

Program.cs                        ~500L
                                  ------
                              ~5,300L в 11 файлах (−1,200L тестов)
```

Максимальный файл: ~1,200 строк (Terminals) вместо 3,328. Средний: ~480 строк.

---

## 8. Изменения в этой сессии (25.02.2026)

Файлы, изменённые в текущей беседе, которые нужно перенести в проект ПЕРЕД началом рефакторинга:

### Бэкенд (C#)
- **RiskManager.cs** — G5/G6: CalcMarginAsync вместо фолбэка для кросс-пар, debug логи, safety check
- **ConnectorManager.cs** — новый метод CalcMarginAsync
- **WorkerProcess.cs** — новый метод CalcMarginAsync
- **StateManager.cs** — closed_at ISO формат, GetDailyRAll, ResetBlockingFlags, sl3 в виртуал ресете
- **DashboardServer.cs** — reset_flags команда, IsRCapReached, rCapReached в get_terminals

### Python
- **mt5_worker.py** — новая команда CALC_MARGIN

### Фронтенд (JS/HTML)
- **index.html** — 2 новых script тега (terminal-settings.js, terminal-discovery.js)
- **common.js** — formatUtc фикс для SQLite datetime
- **terminals.js** — разделён на 3 файла, + Reset Flags кнопка, RCAP badge
- **terminal-settings.js** — НОВЫЙ файл (ConfigPanel)
- **terminal-discovery.js** — НОВЫЙ файл (DiscoveryPanel)
