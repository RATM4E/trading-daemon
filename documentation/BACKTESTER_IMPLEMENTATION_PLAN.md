# BACKTESTER — План реализации

**Дата**: 24.02.2026  
**Обновлено**: 27.02.2026 (по итогам обсуждения)  
**Цель**: Валидация корректности переноса стратегии из research в daemon  
**Принцип**: Реальный прогон через runner.py + strategy.py → те же гейты → виртуальное исполнение на исторических барах

---

## 1. Концепция

Тестер — не research-платформа, а инструмент подтверждения. Если результат тестера совпадает с research бектестом (±5-10% по ключевым метрикам), стратегия перенесена корректно. Если расходится — ищем баг.

**Путь данных идентичен live:**

```
runner.py → strategy.py → HELLO → ACK → TICK(bars) → ACTIONS
    → RiskManager (12 gates) → BacktestExecutor (virtual fill) → Results
```

---

## 2. Новые файлы

| Файл | Расположение | Namespace | Описание |
|------|-------------|-----------|----------|
| `BacktestEngine.cs` | `daemon/Tester/` | `Daemon.Tester` | Replay loop: загрузка баров → побарный прогон через StrategyProcess → исполнение → сбор результатов |
| `BacktestExecutor.cs` | `daemon/Tester/` | `Daemon.Tester` | Виртуальное исполнение сделок на исторических барах (SL/TP проверка, P&L) |
| `BarsHistoryDb.cs` | `daemon/Tester/` | `Daemon.Tester` | SQLite хранилище исторических баров (отдельная БД) |
| `CostModelLoader.cs` | `daemon/Tester/` | `Daemon.Tester` | Парсер cost_model.json, конвертация единиц |
| `DashboardServer_Backtest.cs` | `daemon/Dashboard/` | `Daemon.Dashboard` | Partial class, ~10 WS команд для вкладки Tester |
| `backtest.js` | `daemon/wwwroot/` | — | UI модуль для вкладки Tester |
| `cost_model.json` | `daemon/` | — | Per-symbol cost model: spread + slippage по asset class |

### Изменяемые файлы

| Файл | Что добавляется |
|------|----------------|
| `index.html` | Подключение backtest.js, вкладка Tester |
| `mt5_worker.py` | Команда `COPY_RATES_RANGE` (bulk download исторических баров) |
| `ConnectorManager.cs` | Метод `GetBarsRangeAsync()` для запроса исторических баров |
| `DaemonConfig.cs` | Путь к cost_model.json |

### Файловая структура

```
D:\trading-daemon\daemon\
├── Config/              ← DaemonConfig.cs
├── Connector/           ← ConnectorManager.cs, WorkerProcess.cs
├── Dashboard/
│   ├── DashboardServer.cs
│   ├── DashboardServer_Terminals.cs
│   ├── DashboardServer_Trading.cs
│   ├── DashboardServer_Virtual.cs
│   └── DashboardServer_Backtest.cs   ← NEW
├── Engine/              ← Scheduler.cs, RiskManager.cs, LotCalculator.cs, ...
├── Models/              ← Models.cs, Protocol.cs
├── State/               ← StateManager.cs, _Config, _Positions, _Profiles, ...
├── Strategy/            ← StrategyManager.cs, StrategyProcess.cs
├── Tester/              ← NEW
│   ├── BacktestEngine.cs
│   ├── BacktestExecutor.cs
│   ├── BarsHistoryDb.cs
│   └── CostModelLoader.cs
├── wwwroot/
│   ├── index.html
│   ├── app.js
│   ├── virtual.js
│   ├── strategies.js
│   ├── backtest.js      ← NEW
│   └── ...
├── config.json
├── cost_model.json      ← NEW
├── daemon.csproj
├── news_calendar.json
└── Program.cs
```

---

## 3. BarsHistoryDb.cs — Хранилище исторических баров

Отдельный файл `bars_history.db` рядом с `state.db`.

### Схема

```sql
CREATE TABLE bars (
    symbol    TEXT    NOT NULL,
    timeframe TEXT    NOT NULL,
    time      INTEGER NOT NULL,
    open      REAL    NOT NULL,
    high      REAL    NOT NULL,
    low       REAL    NOT NULL,
    close     REAL    NOT NULL,
    volume    INTEGER NOT NULL DEFAULT 0,
    PRIMARY KEY (symbol, timeframe, time)
) WITHOUT ROWID;

-- Метаданные загрузок
CREATE TABLE download_meta (
    symbol    TEXT NOT NULL,
    timeframe TEXT NOT NULL,
    from_time INTEGER NOT NULL,  -- earliest bar timestamp (server time!)
    to_time   INTEGER NOT NULL,  -- latest bar timestamp (server time!)
    bar_count INTEGER NOT NULL,
    downloaded_at TEXT NOT NULL,  -- ISO datetime
    terminal_id TEXT NOT NULL,    -- откуда скачали
    server_tz   TEXT NOT NULL,    -- timezone брокера (e.g. "EET", "UTC+2")
    PRIMARY KEY (symbol, timeframe)
);

-- Результаты прогонов + research reference
CREATE TABLE backtest_runs (
    id            INTEGER PRIMARY KEY AUTOINCREMENT,
    strategy_name TEXT NOT NULL,
    terminal_id   TEXT NOT NULL,
    from_time     INTEGER NOT NULL,
    to_time       INTEGER NOT NULL,
    config_json   TEXT NOT NULL,       -- BacktestConfig snapshot
    result_json   TEXT NOT NULL,       -- BacktestResult snapshot
    research_ref  TEXT,                -- ResearchMetrics JSON (nullable)
    run_at        TEXT NOT NULL        -- ISO datetime
);
```

`WITHOUT ROWID` — оптимизация для таблиц с составным PK, убирает overhead rowid.

### API класса

```csharp
public class BarsHistoryDb : IDisposable
{
    public BarsHistoryDb(string dbPath);

    // Загрузка
    void SaveBars(string symbol, string timeframe, List<Bar> bars, string terminalId);
    void SaveBarsBulk(string symbol, string timeframe, List<Bar> bars, string terminalId);  // INSERT OR REPLACE в транзакции

    // Чтение
    List<Bar> GetBars(string symbol, string timeframe, long fromTime, long toTime);
    List<Bar> GetAllBars(string symbol, string timeframe);  // для replay

    // Метаданные
    DownloadMeta? GetMeta(string symbol, string timeframe);
    List<DownloadMeta> GetAllMeta();

    // Проверка покрытия
    DataCoverage GetCoverage(List<string> symbols, string timeframe, long fromTime, long toTime);

    // Результаты прогонов
    int SaveRun(BacktestConfig config, BacktestResult result, ResearchMetrics? researchRef);
    List<BacktestRunSummary> GetRuns(string? strategyName = null);
}

public class DownloadMeta
{
    public string Symbol, Timeframe, TerminalId, ServerTz;
    public long FromTime, ToTime;  // server time timestamps
    public int BarCount;
    public DateTime DownloadedAt;
}

public class DataCoverage
{
    public Dictionary<string, CoverageInfo> Symbols;  // symbol → info
    public int TotalRequired;     // сколько символо-периодов нужно
    public int TotalAvailable;    // сколько есть
    public double Percent;        // 0.0 — 1.0
}

public class CoverageInfo
{
    public bool HasData;
    public long? AvailableFrom, AvailableTo;
    public int BarCount;
    public bool FullyCovered;     // покрывает запрошенный диапазон
    public bool PartialCovered;   // частично покрывает
}
```

### Объём данных

| Сценарий | Баров | Размер в SQLite |
|----------|------:|----------------:|
| 1 символ × M5 × 1 год | ~72,000 | ~4 MB |
| 28 символов × M5 × 1 год | ~2,016,000 | ~110 MB |
| 13 символов × M30 × 1 год | ~113,000 | ~6 MB |
| 28 символов × M30 × 1 год | ~245,000 | ~13 MB |

Для mtf_pullback (M30, 13 символов, год) — всего ~6 MB. Загрузка с warmup (1000 баров) — ~120K баров.

---

## 4. mt5_worker.py — Команда COPY_RATES_RANGE

Новая команда для bulk-загрузки исторических баров.

```python
# Обработчик команды
def handle_copy_rates_range(msg):
    """
    Request:  {"cmd": "COPY_RATES_RANGE", "id": 1,
               "symbol": "EURUSD", "timeframe": "M30",
               "from_ts": 1704067200, "to_ts": 1735689600}

    Response: {"id": 1, "status": "ok",
               "data": {"symbol": "EURUSD", "timeframe": "M30",
                         "bars": [...], "count": 17520}}

    mt5.copy_rates_range() принимает даты в UTC, но возвращает
    timestamps в СЕРВЕРНОМ ВРЕМЕНИ брокера (The5ers = UTC+2/+3 EET).
    Это важно: бары хранятся и используются в server time.

    M30 × 1 год ≈ 17,500 баров — MT5 отдаёт за ~2-3 секунды.
    M5 × 1 год ≈ 72,000 баров — MT5 отдаёт за ~5-10 секунд.
    """
    symbol = msg["symbol"]
    tf_str = msg["timeframe"]
    from_dt = datetime.fromtimestamp(msg["from_ts"], tz=timezone.utc)
    to_dt = datetime.fromtimestamp(msg["to_ts"], tz=timezone.utc)

    tf_map = {"M1": mt5.TIMEFRAME_M1, "M5": mt5.TIMEFRAME_M5,
              "M15": mt5.TIMEFRAME_M15, "M30": mt5.TIMEFRAME_M30,
              "H1": mt5.TIMEFRAME_H1, "H4": mt5.TIMEFRAME_H4,
              "D1": mt5.TIMEFRAME_D1}
    tf = tf_map.get(tf_str)
    if tf is None:
        return _error(msg, f"Unknown timeframe: {tf_str}")

    rates = mt5.copy_rates_range(symbol, tf, from_dt, to_dt)
    if rates is None or len(rates) == 0:
        return _error(msg, f"No data for {symbol} {tf_str}")

    bars = [{"time": int(r[0]), "open": float(r[1]), "high": float(r[2]),
             "low": float(r[3]), "close": float(r[4]), "volume": int(r[5])}
            for r in rates]

    return _ok(msg, {"symbol": symbol, "timeframe": tf_str,
                     "bars": bars, "count": len(bars)})
```

### ConnectorManager — новый метод

```csharp
public async Task<List<Bar>?> GetBarsRangeAsync(
    string terminalId, string symbol, string timeframe,
    long fromTimestamp, long toTimestamp, CancellationToken ct)
{
    var request = new Dictionary<string, object>
    {
        ["cmd"] = "COPY_RATES_RANGE",
        ["symbol"] = symbol,
        ["timeframe"] = timeframe,
        ["from_ts"] = fromTimestamp,
        ["to_ts"] = toTimestamp
    };
    var resp = await SendCommandAsync(terminalId, request, ct);
    // Parse resp.data.bars → List<Bar>
}
```

---

## 5. BacktestEngine.cs — Ядро тестера

### Конфигурация прогона

```csharp
public class BacktestConfig
{
    public string TerminalId { get; set; }      // для копирования настроек + загрузки данных
    public string StrategyName { get; set; }    // имя стратегии в strategies/
    public long FromTimestamp { get; set; }      // начало периода теста
    public long ToTimestamp { get; set; }        // конец периода теста
    public double StartDeposit { get; set; }    // стартовый баланс
    public double Leverage { get; set; }        // плечо
    public double CommissionPerLot { get; set; } // комиссия round-trip (только для $-метрик!)
    // Cost model
    public CostModelMode CostMode { get; set; } // FromFile, Custom, Zero
    public string? CostModelPath { get; set; }   // путь к cost_model.json (для FromFile)
    public double? CustomSpreadPips { get; set; } // глобальный override (для Custom)
    public double? CustomSlippagePips { get; set; }
    // Гейты — все включены по умолчанию, кроме G7 (News) — опционально
    public bool EnableNewsGate { get; set; }
    public bool EnableTradingHours { get; set; } = true;
    public double MaxRiskPerTrade { get; set; }  // из профиля терминала
    public double MaxDailyDD { get; set; }       // из профиля терминала
    public double MaxCumulativeDD { get; set; }  // из профиля терминала
    public double? RCap { get; set; }            // из конфига стратегии
    // Symbol sizing — копируется из terminal profile
    public Dictionary<string, SymbolSizingEntry> SymbolSizing { get; set; }
}

public enum CostModelMode { FromFile, Custom, Zero }

/// <summary>Per-symbol cost entry from cost_model.json</summary>
public class CostEntry
{
    public string AssetClass { get; set; }    // forex, index, metal, energy, crypto
    public double Spread { get; set; }         // в единицах asset class (pips/points/usd)
    public double Slippage { get; set; }
}
```

### Основной алгоритм

```
1. ПОДГОТОВКА
   a. Прочитать strategy config (symbols, timeframes, history_bars из HELLO)
   b. Рассчитать warmup: from_time - history_bars × bar_duration
   c. Загрузить ВСЕ бары из BarsHistoryDb (warmup + test period)
   d. Построить timeline: отсортированный список уникальных timestamps
      - Если все символы на одном TF (mtf_pullback M30) — синхронные timestamps
      - Если разные TF на разных символах (fx_intraday: M15/M30/H1) —
        timeline = union timestamps всех TF, не все символы имеют новый бар
        на каждом шаге. Стратегия сама разбирается что обновилось (как в live).
   e. Инициализировать BacktestState (баланс, позиции, метрики)

2. ЗАПУСК СТРАТЕГИИ
   a. Стартовать StrategyProcess (runner.py + strategy.py) — ТОТ ЖЕ процесс что в live
   b. Получить HELLO → отправить ACK (magic, mode="backtest")
   c. Демон загружает warmup бары (history_bars штук до from_time)
   d. Первый TICK с warmup барами → получаем ACTIONS → демон подавляет все ENTER
      (аналогично live: "Warmup tick: suppressed N ENTER action(s)")

3. REPLAY LOOP
   for each bar_time in timeline:
       a. Собрать бары ДО bar_time включительно (sliding window = history_bars)
       b. Собрать текущие позиции (отфильтрованные по magic)
       c. Сформировать TICK message (bars + positions + equity)
       d. Отправить TICK → получить ACTIONS
       e. Для каждого ENTER:
          - LotCalculator (entry=open следующего бара, SL из action)
          - RiskManager.CheckAsync (все включённые гейты)
          - Если passed: BacktestExecutor.OpenPosition (fill at next bar open)
          - Если rejected: записать в gate_stats + BlockedSignals
       f. Для каждого EXIT:
          - BacktestExecutor.ClosePosition (fill at next bar open)
       g. Для каждого MODIFY_SL:
          - BacktestExecutor.ModifySL (применяется мгновенно, как в live)
       h. BacktestExecutor.CheckSLTP (проверить SL/TP hit на текущем баре
          для РАНЕЕ открытых позиций — позиции, открытые на этом шаге,
          проверяются начиная со следующего бара)
       i. Обновить equity snapshot
       j. Report progress (каждые 1000 баров → WS push)

4. ЗАВЕРШЕНИЕ
   a. Отправить STOP → получить GOODBYE
   b. Закрыть все оставшиеся позиции по цене последнего бара
   c. Рассчитать метрики
   d. Отправить результат на dashboard
```

### Ключевые детали

**Timeline:** Для M30 × 13 символов (mtf_pullback) — все символы имеют одинаковые timestamps (синхронные бары). Timeline = ~17,500 шагов для годового теста. Это ~17,500 TCP roundtrip-ов. Для стратегий с разными TF на разных символах (fx_intraday: M15/M30/H1) — timeline = union всех уникальных timestamps, ~35,000-96,000 шагов.

**Скорость:** Один TICK/ACTIONS roundtrip через TCP+JSON ≈ 1-5ms. Для 17,500 тиков = 17-90 секунд. С учётом overhead Python-стратегии (resample, indicators) — **реалистично 1-3 минуты** на годовой M30 тест.

**MTF стратегии:** Стратегия получает бары только по сигнальному TF. Старшие TF стратегия собирает (ресемплирует) внутри себя из сигнального — как в live. BacktestEngine не знает про MTF.

**Warmup:** Демон загружает history_bars штук ДО начала тестового периода (аналогично live запуску). Первый TICK подавляется на стороне демона — не gate в RiskManager, а флаг `_isWarmup` в BacktestEngine (как Scheduler делает в live).

**SL/TP проверка:** На каждом баре проверяем все РАНЕЕ открытые позиции:

LONG:
- SL hit: bar.Low ≤ SL → close at SL (или bar.Open если gap down: Open < SL)
- TP hit: bar.High ≥ TP → close at TP (или bar.Open если gap up: Open > TP)
- Одновременный SL+TP hit: Open < SL → SL; Open > TP → TP; иначе SL (conservative)

SHORT:
- SL hit: bar.High ≥ SL → close at SL (или bar.Open если gap up: Open > SL)
- TP hit: bar.Low ≤ TP → close at TP (или bar.Open если gap down: Open < TP)
- Одновременный SL+TP hit: Open > SL → SL; Open < TP → TP; иначе SL (conservative)

---

## 6. BacktestExecutor.cs — Исполнение сделок

Облегчённая версия VirtualTracker, работающая на исторических данных.

### Cost model — два режима

**v1 (default, для валидации с research):**
Flat cost как в research скрипте. Единая сумма `(spread + slippage) * pip_size` вычитается из PnL каждой сделки, независимо от SL/TP/signal exit. Это совпадает с `bt_trades()` в research:
```python
cost = (spread + slippage) * pip_size(sym)
# SL hit:  pnl = -sl_dist - cost
# TP hit:  pnl = +tp_dist - cost
# Signal:  pnl = delta - cost
```

**v2 (realistic, будущее расширение):**
Spread применяется при входе (LONG: entry + spread; SHORT: exit + spread), slippage при SL (против позиции), без slippage на TP. Не для первой версии.

### Commission

Commission (`BacktestConfig.CommissionPerLot`) — **только для $-mode метрик**. Не участвует в R-расчётах. Вычитается из долларового PnL при закрытии позиции: `pnl_dollar -= volume * commission_per_lot`.

### API

```csharp
public class BacktestExecutor
{
    // Состояние
    double Balance;
    double Equity;           // Balance + unrealized P&L
    List<BtPosition> OpenPositions;
    List<BtTrade> ClosedTrades;
    List<BtBlockedSignal> BlockedSignals;  // rejected гейтами
    Dictionary<string, int> GateStats;  // gate_name → rejection_count

    // Cost model — flat cost per symbol (в price units)
    Dictionary<string, double> FlatCostPrice;  // symbol → (spread+slippage)*pip_size

    // Методы
    BtPosition? OpenPosition(StrategyAction action, Bar nextBar,
                              string symbol, LotResult lot, string strategy, int magic);

    BtTrade? ClosePosition(long ticket, double closePrice, string reason);

    void CheckSLTP(Dictionary<string, Bar> currentBars);  // проверка SL/TP hit

    void ModifySL(long ticket, double newSL);

    // P&L — переиспользуем логику из VirtualTracker.CalculateVirtualPnl
    double CalculatePnl(BtPosition pos, double closePrice);

    // R-result: pnl_points / initial_risk_points (cost включён в pnl_points)
    double CalculateRResult(BtPosition pos, double closePrice);

    // Snapshot для equity curve
    EquityPoint GetEquitySnapshot(long time);
    EquityPointR GetEquitySnapshotR(long time);  // в R-единицах
}

public class BtPosition
{
    public long Ticket;        // auto-increment, отрицательные
    public string Symbol, Direction;
    public double Volume, PriceOpen, SL, TP;
    public double InitialRiskPoints;  // |entry - SL| для R-calc
    public double FlatCost;            // (spread+slippage)*pip_size for this symbol
    public int Magic;
    public long OpenTime;
    public double MarginUsed;
    public bool ProtectorFired;
}

public class BtTrade
{
    public long Ticket;
    public string Symbol, Direction;
    public double Volume, PriceOpen, PriceClose;
    public double PnlPoints;        // raw pnl in points (перед cost)
    public double CostPoints;       // flat cost в points
    public double NetPnlPoints;     // PnlPoints - CostPoints
    public double RMultiple;        // NetPnlPoints / InitialRiskPoints
    public double PnlDollar;        // net dollar P&L (после commission)
    public double Commission;       // volume * commission_per_lot (только для $-mode)
    public long OpenTime, CloseTime;
    public string CloseReason;   // SL, TP, signal, protector, end_of_test
    public string GatesPassed;   // какие гейты проверялись
}

public class BtBlockedSignal
{
    public long Time;
    public string Symbol, Direction;
    public string BlockedByGate;  // "G4", "G8", "G11" etc.
    public string Reason;         // human-readable
}
```

### InstrumentCard в бектесте

Для LotCalculator и P&L нужны: Point, TradeTickSize, TradeTickValue, TradeContractSize, Margin1Lot. Запрашиваем один раз при старте теста через MT5 (терминал доступен) и кешируем на весь прогон. Для валидации переноса этого достаточно — исторические значения отличаются минимально.

---

## 7. RiskManager в бектесте

Переиспользуем **существующий** RiskManager без модификаций. Создаём изолированный StateManager (**in-memory**, без файла на диске) с бектест-профилем и позициями.

### Какие гейты работают

| Gate | Статус | Комментарий |
|------|--------|-------------|
| G1 Mode | ✅ Адаптирован | mode="backtest" в `TradeRequest.Mode` → пропускает (как virtual) |
| G2 Daily DD | ✅ Работает | Симулированный баланс, broker date из bar timestamp |
| G3 Cumulative DD | ✅ Работает | Аналогично G2 |
| G4 Risk Per Trade | ✅ Работает | Из BacktestConfig |
| G5 Margin Per Trade | ✅ Работает | Margin1Lot из кешированного InstrumentCard |
| G6 Deposit Load | ✅ Работает | Суммируем маржу открытых позиций |
| G7 News Guard | ⚙️ Опционально | Нужна история новостей. Если загружена — работает, иначе skip |
| G8 3SL Guard | ✅ Работает | Считает из бектест-позиций |
| G9 Netting | ✅ Работает | Из профиля терминала |
| G10 Trading Hours | ✅ Работает | Время из bar timestamp, timezone из профиля |
| G11 Same Symbol | ✅ Работает | Из бектест-позиций |
| G12 R-cap | ✅ Работает | Из бектест-позиций + daily R accumulator |

### Broker date в бектесте

Упрощается по сравнению с live: bar timestamps уже в broker server time (MT5 возвращает server time). Не нужна конвертация UTC → broker local.

BacktestEngine передаёт `barServerTime` из бара напрямую в контекст RiskManager. `GetBrokerDate()` просто извлекает дату из этого timestamp. Гейты G2/G3/G8/G10/G12 работают корректно без адаптации timezone logic.

Единственная адаптация: RiskManager и StateManager должны использовать бектест-время (`barServerTime`) вместо `DateTime.UtcNow`. Добавляем `DateTime? OverrideNow` в `TradeRequest`.

---

## 8. Dashboard — вкладка Tester

### Новые WS команды

| Команда | Направление | Описание |
|---------|-------------|----------|
| `bt_get_strategies` | → daemon | Список доступных стратегий с их requirements |
| `bt_get_data_coverage` | → daemon | Проверка покрытия данных для выбранной стратегии/периода |
| `bt_get_cost_model` | → daemon | Текущая cost model (per-symbol spread/slippage) |
| `bt_download_bars` | → daemon | Запуск загрузки исторических баров (async, с прогрессом) |
| `bt_download_progress` | ← daemon | Push: прогресс загрузки (symbol, %) |
| `bt_run` | → daemon | Запуск бектеста с конфигурацией (включая cost mode) |
| `bt_progress` | ← daemon | Push: прогресс прогона (bar_index/total, %, current trades) |
| `bt_result` | ← daemon | Push: финальный результат (R-метрики + $-метрики + trades + equity) |
| `bt_cancel` | → daemon | Отмена текущего прогона |
| `bt_save_research_ref` | → daemon | Сохранить reference метрики из research для сравнения |

### UI Layout

```
┌─ Tester ─────────────────────────────────────────────────────┐
│                                                               │
│  ┌─ Configuration ─────────────────────────────────────────┐  │
│  │ Terminal: [The5ers-1 ▼]     Strategy: [mtf_pullback ▼]  │  │
│  │ Period:   [2025-01-01] → [2025-12-31]                    │  │
│  │ Deposit:  [100000]   Leverage: [1:100]                   │  │
│  │ Commission: [7.0 $/lot]                                  │  │
│  │                                                           │  │
│  │ Costs: [From cost_model.json ▼]                          │  │
│  │                                                           │  │
│  │ Gates: ☑ DD limits  ☑ 3SL Guard  ☑ R-cap  ☑ Hours       │  │
│  │        ☐ News Guard (no history)                          │  │
│  └───────────────────────────────────────────────────────────┘  │
│                                                               │
│  ┌─ Data ──────────────────────────────────────────────────┐  │
│  │ EURUSD  M30  ████████████████████ 100%  17,520  │ 1.1p  │  │
│  │ GBPJPY  M30  ████████████████████ 100%  17,520  │ 2.2p  │  │
│  │ USDJPY  M30  ░░░░░░░░░░░░░░░░░░░░   0%  —      │ 1.1p  │  │
│  │ XAUUSD  M30  ████████████████████ 100%  17,520  │ $0.60 │  │
│  │ ...                                                       │  │
│  │ Total: 78% coverage     [Download Missing Data]           │  │
│  └───────────────────────────────────────────────────────────┘  │
│                                                               │
│  [▶ Run Backtest]  [Cancel]           Status: idle            │
│  ═══════════════════════════════════════════════════════════   │
│  Progress: ██████████████████░░ 87%  (15,242 / 17,520 bars)  │
│                                                               │
│  ┌─ Results ──────────────────── [$] [R] toggle ───────────┐  │
│  │ ┌─ Summary (R-mode) ───────────────────────────────┐    │  │
│  │ │ Trades: 342  │  Total: +226.6R  │  Annual: 226.6R/yr  │  │
│  │ │ Max DD: -16.9R  │  Calmar_R: 13.4  │  WR: 41%        │  │
│  │ │ Worst day: -1.5R  │  Best day: +25.1R                  │  │
│  │ │ Neg months: 2/12  │  Max DD days: 102                  │  │
│  │ └───────────────────────────────────────────────────┘    │  │
│  │ ┌─ Summary ($-mode) ───────────────────────────────┐    │  │
│  │ │ Trades: 342  │  Net P&L: +$124,630  │  +124.6%        │  │
│  │ │ Max DD: -$16,900 (-4.2%)  │  PF: 1.82                 │  │
│  │ │ Avg Trade: +$364  │  Avg Duration: 14.2h               │  │
│  │ │ Sharpe: 1.34  │  Expectancy: +0.18R                    │  │
│  │ └───────────────────────────────────────────────────┘    │  │
│  │                                                          │  │
│  │ ┌─ vs Research ─────────────────────────────────────┐   │  │
│  │ │              Daemon      Research     Delta        │   │  │
│  │ │ Trades:      342         348          -1.7% ✅     │   │  │
│  │ │ Total R:     226.6       226.6        +0.0% ✅     │   │  │
│  │ │ Calmar_R:    13.4        13.4         +0.0% ✅     │   │  │
│  │ │ Max DD (R):  16.9        16.9         +0.0% ✅     │   │  │
│  │ │ Worst day:   -1.5R       -1.5R        +0.0% ✅     │   │  │
│  │ │ (⚠ >5% delta = yellow, >10% = red)                │   │  │
│  │ └───────────────────────────────────────────────────┘   │  │
│  │                                                          │  │
│  │ ┌─ Gate Statistics ─────────────────────────────────┐   │  │
│  │ │ G4 Risk/Trade: 12 rejected  │  G8 3SL: 3 blocked  │   │  │
│  │ │ G11 Duplicate: 89 blocked   │  G12 R-cap: 7        │   │  │
│  │ │ G2 Daily DD: 0  │  G5 Margin reduce: 24 times     │   │  │
│  │ └───────────────────────────────────────────────────┘   │  │
│  │                                                          │  │
│  │ [📈 Equity]  [📋 Trades]  [📊 Symbols]  [💾 Export]   │  │
│  └──────────────────────────────────────────────────────────┘  │
└───────────────────────────────────────────────────────────────┘
```

### Equity Chart — dual mode

Переиспользуем Chart.js (уже подключён). Два режима:

**R-mode** (для сравнения с research):
- Portfolio equity в R-единицах (area chart) — прямо как в research HTML
- Raw vs R-cap overlay (если R-cap включён в гейтах)
- Drawdown subplot (R-единицы, красная зона)

**$-mode** (для понимания реальных сумм):
- Equity curve ($)
- Trade markers (green △ entry, red ▽ exit)
- Drawdown subplot ($, % от peak)

### Per-Symbol Chart

Как в research: каждый символ — отдельная линия (кумулятивный R), toggle через legend click.

### Comparison Panel — "vs Research"

Пользователь вводит research метрики вручную (6 полей):
- Total R, Annual R/yr, Max DD (R), Calmar_R, Worst day (R), Neg months

Тестер автоматически считает delta и подсвечивает:
- ✅ зелёный: delta ≤ 5%
- ⚠️ жёлтый: 5% < delta ≤ 10%
- 🔴 красный: delta > 10%

Research метрики сохраняются в `backtest_runs` (таблица в `bars_history.db`) для повторных сравнений.

### Trade List — таблица (dual mode)

**R-mode:**

| # | Symbol | Dir | Open Time | Close Time | R-result | Cumul R | Reason | Gate |
|---|--------|-----|-----------|------------|----------|---------|--------|------|
| 1 | EURUSD | LONG | 2025-01-03 08:30 | 2025-01-03 16:00 | +1.2R | +1.2R | TP | — |
| 2 | GBPJPY | SHORT | 2025-01-03 12:00 | 2025-01-04 09:30 | -1.0R | +0.2R | SL | — |
| 3 | AUDUSD | LONG | 2025-01-03 14:00 | — | — | — | BLOCKED | G11 |

**$-mode:**

| # | Symbol | Dir | Open Time | Close Time | Entry | Exit | Lot | P&L | R | Reason |
|---|--------|-----|-----------|------------|-------|------|-----|-----|---|--------|
| 1 | EURUSD | LONG | 2025-01-03 08:30 | 2025-01-03 16:00 | 1.0412 | 1.0458 | 0.15 | +$69 | +1.2R | TP |
| 2 | GBPJPY | SHORT | 2025-01-03 12:00 | 2025-01-04 09:30 | 191.45 | 192.10 | 0.08 | -$52 | -1.0R | SL |

Trade list включает BLOCKED сигналы (rejected гейтами) для полной картины.

### By Symbol — breakdown (R-mode primary)

| Symbol | Trades | Win% | Total R | Avg R | Max DD (R) | Best/Worst |
|--------|-------:|-----:|--------:|------:|-----------:|------------|
| EURUSD | 48 | 44% | +32.4R | +0.68R | -4.2R | +3.1R / -1.0R |
| GBPUSD | 35 | 37% | +18.2R | +0.52R | -6.1R | +2.8R / -1.0R |
| XAUUSD | 22 | 50% | +28.9R | +1.31R | -3.0R | +4.2R / -1.0R |

Кликабельные символы → per-symbol equity curve (как в research HTML с toggle legend).

---

## 9. Порядок реализации

### Этап 1: Данные + Cost Model (1-2 сессии)

1. **cost_model.json** — положить файл в daemon/, парсер `CostModelLoader.cs`
2. **mt5_worker.py** — добавить `COPY_RATES_RANGE`
3. **ConnectorManager.cs** — метод `GetBarsRangeAsync()`
4. **BarsHistoryDb.cs** — SQLite хранилище + CRUD + coverage check
5. **DashboardServer_Backtest.cs** — команды `bt_get_data_coverage`, `bt_download_bars`, `bt_download_progress`
6. **backtest.js** — секция Data в Tester tab (progress bars, download button, cost display)

**Тест:** загрузить год M30 для 13 символов, проверить в SQLite. Убедиться что cost model парсится и конвертируется в flat cost price.

### Этап 2: Replay Engine (2-3 сессии)

1. **BacktestExecutor.cs** — виртуальное исполнение (open/close/SL/TP/P&L) с flat cost model
2. **BacktestEngine.cs** — основной replay loop:
   - Загрузка баров из BarsHistoryDb
   - Построение timeline (с поддержкой multi-TF)
   - Запуск StrategyProcess
   - Warmup: подавление ENTER на первом тике (как в live)
   - Побарный прогон: TICK → ACTIONS → Execute (с flat cost)
   - SL/TP проверка на каждом баре (LONG + SHORT логика)
   - R-result расчёт для каждой сделки
   - Прогресс-репорты
3. **Адаптация RiskManager** — `OverrideNow` в TradeRequest (bar server time вместо DateTime.UtcNow)
4. **DashboardServer_Backtest.cs** — команды `bt_run`, `bt_progress`, `bt_cancel`

**Тест:** прогнать mtf_pullback на 3 месяцах, сверить количество сделок и R-метрики с research.

### Этап 3: Результаты + UI (1-2 сессии)

1. **BacktestEngine.cs** — расчёт метрик:
   - R-based: TotalR, AnnualR, MaxDD(R), Calmar_R, WorstDay(R), NegMonths
   - $-based: PF, WR, Sharpe, MaxDD($), Calmar (с commission)
   - Gate stats, cost breakdown
2. **DashboardServer_Backtest.cs** — команда `bt_result` (полный результат)
3. **backtest.js**:
   - [$]/[R] toggle для всех панелей
   - Summary panel (dual mode)
   - Equity Chart (R-units primary, $-units secondary)
   - Per-Symbol chart (cumulative R, legend toggle)
   - Trade List (включая BLOCKED signals)
   - Gate Statistics
   - vs Research comparison panel (manual input, auto delta)
   - Export: CSV trades list

**Тест:** полный годовой прогон, сравнение equity curve (R) с research HTML. Delta по ключевым метрикам < 5%.

### Этап 4: Polish (1 сессия)

1. Pre-flight validation (конфиг стратегии, символы, параметры, cost model coverage)
2. Сохранение результатов прогонов в `backtest_runs` (для сравнения "до/после" и research ref)
3. `bt_get_strategies` — список стратегий с auto-detected requirements
4. Auto-fill настроек из терминал-профиля при выборе терминала
5. Cost model editor: view/edit costs прямо в dashboard (convenience)

---

## 10. Подводные камни и решения

### 10.1 Bar timestamps — broker server time, не UTC

**MT5 `copy_rates_range()` возвращает timestamps в серверном времени брокера**, не UTC. The5ers — UTC+2 (зимой UTC+2, летом UTC+3, EET). Это значит:

- Bar time 2025-01-03 08:00:00 = 08:00 **server time** (= 06:00 UTC зимой)
- Все бары в `bars_history.db` хранятся в server time (как получили от MT5)
- Гейты G2/G3/G8/G10/G12 используют broker server time для daily reset → **бары уже в правильном timezone**, не нужна конвертация

**Следствия для BacktestEngine:**
- `barTime` из БД = broker server time → напрямую передаём в RiskManager
- Daily boundaries: полночь по server time = корректная граница дня
- G10 Trading Hours: время бара = server time → сравнение с no-trade window без конвертации
- При отображении в UI: показываем server time (как в MT5), опционально UTC

**Важно при загрузке:** `copy_rates_range(from_dt, to_dt)` — даты передавать в UTC (API requirement), но timestamps в ответе приходят в server time. `BarsHistoryDb` хранит `terminal_id` в meta чтобы знать какой timezone.

**При смене брокера:** бары от разных брокеров имеют разный timezone offset. Нельзя смешивать бары The5ers и RoboForex в одном тесте. Meta-таблица фиксирует источник.

### 10.2 Margin calculation

Margin1Lot зависит от текущей цены и leverage. В бектесте цена меняется. Для точности можно пересчитывать margin_1lot на каждом баре, но это overkill для валидации.

**Решение:** Запросить margin_1lot один раз при старте теста. Погрешность ~3-5% за год — приемлемо для валидации переноса.

### 10.3 Cost Model — flat cost как в research

Research скрипт (`bt_trades()`) применяет flat cost: `cost = (spread + slippage) * pip_size(sym)`. Эта сумма вычитается из PnL каждой сделки (и SL, и TP, и signal exit).

**Решение:** Тестер v1 копирует эту логику один в один. Файл `cost_model.json` с per-symbol данными → `CostModelLoader` конвертирует в flat cost price per symbol → `BacktestExecutor` применяет.

**UI в тестере:**
```
Cost model: [From cost_model.json ▼]     ← default, per-symbol costs
            [Custom (single value)]       ← глобальный override для быстрого теста
            [Zero costs]                  ← для отладки
```

При выборе "From cost_model.json" в Data секции показываем costs рядом с coverage:
```
EURUSD  M30  ████████ 100%  17,520 bars  │ spread: 1.1 pip  slip: 0.6 pip
GBPJPY  M30  ████████ 100%  17,520 bars  │ spread: 2.2 pip  slip: 1.4 pip
```

**Загрузка:** `DaemonConfig.CostModelPath` → при старте daemon/backtest парсим JSON → `Dictionary<string, double>` (symbol → flat cost price).

**Расширяемость:** Когда появятся данные по конкретным брокерам — добавляем файлы `cost_model_the5ers.json`, `cost_model_audacity.json` и selector в UI. Realistic mode (v2) — отдельная опция в Cost model selector.

### 10.4 Weekend/Holiday gaps

Пятничный бар 23:30 → понедельник 00:00 — gap в данных. Позиция открытая в пятницу не закроется по SL до понедельника. Это корректно и совпадает с live поведением.

Gap execution: если понедельник Open ниже SL (для BUY), позиция закрывается по Open, не по SL. Аналогично VirtualTracker.

### 10.5 Одновременный запуск

Бектест не должен мешать live торговле. BacktestEngine использует:
- Отдельный StrategyProcess (отдельный TCP порт)
- Отдельный StateManager (in-memory)
- Отдельный RiskManager (привязан к бектест StateManager)
- MT5 worker используется только для загрузки данных (не во время replay)

Live engine продолжает работать параллельно.

### 10.6 Отмена прогона

`bt_cancel` устанавливает CancellationToken. BacktestEngine проверяет его на каждом баре. При отмене — отправляет STOP стратегии, закрывает процесс, возвращает partial results.

---

## 11. Метрики результата

### Research output format (target для сравнения)

Research HTML выдаёт метрики в R-единицах:
- Total R, Annual R/yr, MaxDD (R), Calmar_R
- Worst day (R), Best day (R), Neg months, Max DD days
- Per-symbol breakdown: cumulative R per symbol
- R-cap heatmap: R% × cap → Annual/PASS/FAIL matrix

Тестер должен считать **идентичные** метрики для прямого сравнения.

### BacktestResult

```csharp
public class BacktestResult
{
    // Идентификация
    public string StrategyName;
    public string Period;          // "2025-01-01 → 2025-12-31"
    public double StartDeposit;
    public DateTime RunAt;
    public CostModelMode CostMode;
    public string CostModelFile;   // "cost_model.json" или "custom" или "zero"

    // ── R-based метрики (primary, для сравнения с research) ──
    public double TotalR;           // сумма всех R-results
    public double AnnualR;          // TotalR / years
    public double MaxDrawdownR;     // max drawdown в R-единицах
    public double CalmarR;          // AnnualR / MaxDrawdownR
    public double WorstDayR;        // худший день (R)
    public double BestDayR;         // лучший день (R)
    public int NegMonths;           // количество отрицательных месяцев
    public int TotalMonths;         // всего месяцев
    public int MaxDDDays;           // длительность max drawdown (дни)

    // ── $-based метрики (для понимания реальных сумм, commission включена) ──
    public int TotalTrades;
    public int WinTrades, LossTrades;
    public double WinRate;          // %
    public double ProfitFactor;
    public double NetPnl;           // $
    public double NetPnlPercent;    // % от депозита
    public double MaxDrawdown;      // $ (абсолютный)
    public double MaxDrawdownPct;   // % от peak equity
    public double CalmarRatio;      // Annual return % / MaxDD %
    public double SharpeRatio;      // Annualized
    public double AvgR;             // средний R-multiple per trade
    public double Expectancy;       // avg_win × win_rate - avg_loss × loss_rate

    // Детализация
    public double AvgWin, AvgLoss;  // $
    public double MaxWin, MaxLoss;  // $
    public double AvgDuration;      // hours
    public int MaxConsecutiveWins, MaxConsecutiveLosses;
    public int BlockedSignals;      // rejected гейтами (не вошли в рынок)

    // Gate statistics
    public Dictionary<string, int> GateRejections;  // gate → count
    public int MarginReductions;                      // G5 reduce count

    // Cost model impact
    public double TotalCostFlat;     // суммарный flat cost ($) — spread+slippage
    public double TotalCommission;   // суммарная комиссия ($) — только $-mode

    // Данные для графиков
    public List<EquityPointR> EquityCurveR;  // time → cumulative R
    public List<EquityPoint> EquityCurve;     // time → equity $
    public List<DailyR> DailyReturns;         // per-day R totals (для worst/best day)
    public List<BtTrade> Trades;
    public Dictionary<string, SymbolStatsR> BySymbol;  // per-symbol R breakdown

    // Reference: research метрики (user-provided для сравнения)
    public ResearchMetrics? ResearchRef;
}

public class EquityPointR
{
    public long Time;
    public double CumulativeR;      // кумулятивный R
    public double DrawdownR;        // текущий drawdown от peak (R)
}

public class DailyR
{
    public string Date;             // "2025-01-03"
    public double TotalR;           // сумма R за день
    public int TradeCount;
}

public class SymbolStatsR
{
    public int Trades;
    public double WinRate;
    public double TotalR;
    public double AvgR;
    public double MaxDrawdownR;
    public double BestR, WorstR;
    public List<double> CumulativeR;  // для per-symbol equity chart
}

public class ResearchMetrics
{
    public double? TotalR, AnnualR, MaxDrawdownR, CalmarR;
    public double? WorstDayR, BestDayR;
    public int? NegMonths, TotalMonths;
}
```

### R-result calculation

R-result для каждой сделки считается как в research `bt_trades()`:
- `pnl_points = raw price delta` (close - open для LONG, open - close для SHORT)
- `cost_points = flat_cost / Point` (единый cost из cost_model.json)
- `net_pnl_points = pnl_points - cost_points`
- `R = net_pnl_points / initial_risk_points` (initial_risk = |entry - SL|)

Соответствие с research:
- SL hit → R ≈ -1.0 - cost/risk (чуть хуже -1.0 из-за cost)
- TP hit → R = tp_ratio - cost/risk
- Signal exit → variable R

---

## 12. Что НЕ входит в первую версию

- Оптимизация параметров (walk-forward) — это задача research
- Мультистратегийный портфельный тест — по одной стратегии за раз
- Исторические спреды (tick-level) — flat cost model достаточен
- Исторические новости — G7 опционально (если есть данные)
- R-cap heatmap (R% × cap grid) — красиво, но для валидации не нужно
- Broker-specific cost models — один файл default, расширяется по мере сбора данных
- Monte Carlo simulation — это задача research pipeline
- Realistic cost mode (v2) — spread на entry, slippage на SL, без slippage на TP

Эти фичи можно добавить итеративно после базовой валидации работоспособности тестера.

---

## 13. cost_model.json — формат

```json
{
  "units": {
    "forex": "pips",
    "index": "points",
    "metal": "usd",
    "energy": "usd",
    "crypto": "usd"
  },
  "symbols": {
    "EURUSD":  { "asset_class": "forex",  "spread": 1.1,  "slippage": 0.6,  "spread_open_widened": 2.0 },
    "GBPUSD":  { "asset_class": "forex",  "spread": 1.2,  "slippage": 0.6,  "spread_open_widened": 2.2 },
    "GBPNZD":  { "asset_class": "forex",  "spread": 3.0,  "slippage": 1.8,  "spread_open_widened": 5.5 },
    "SP500":   { "asset_class": "index",  "spread": 0.7,  "slippage": 1.0,  "spread_open_widened": 2.0 },
    "XAUUSD":  { "asset_class": "metal",  "spread": 0.6,  "slippage": 0.3,  "spread_open_widened": 1.2 },
    "XTIUSD":  { "asset_class": "energy", "spread": 0.06, "slippage": 0.05, "spread_open_widened": 0.15 },
    "BTCUSD":  { "asset_class": "crypto", "spread": 6.5,  "slippage": 8.0,  "spread_open_widened": 15.0 }
  }
}
```

> `spread_open_widened` хранится в JSON для справки (может пригодиться для будущих стратегий), но тестер использует только `spread` и `slippage`.

### Конвертация в flat cost price

`CostModelLoader` конвертирует per-symbol costs в единую flat cost price (в price units):

| Asset class | Единица в JSON | Формула | Пример |
|-------------|---------------|---------|--------|
| forex | pips | `(spread + slippage) * pip_size` | EURUSD: (1.1 + 0.6) × 0.0001 = 0.00017 |
| index | points | `(spread + slippage) * Point` | SP500: (0.7 + 1.0) × 0.01 = 0.017 |
| metal | usd | `(spread + slippage) * Point` | XAUUSD: (0.6 + 0.3) × 0.01 = 0.009 |
| energy | usd | `(spread + slippage) * Point` | XTIUSD: (0.06 + 0.05) × 0.01 = 0.0011 |
| crypto | usd | `(spread + slippage) * Point` | BTCUSD: (6.5 + 8.0) × 0.01 = 0.145 |

> Для forex: `pip_size` = 0.0001 (или 0.01 для JPY пар). Point для 5-digit broker = 0.00001, pip = 10 points.
> Для index/metal/energy/crypto: Point берётся из InstrumentCard (запрошен при старте теста).

BacktestExecutor хранит `Dictionary<string, double> FlatCostPrice` с пересчитанными значениями.

---

## 14. Решения из обсуждения (27.02.2026)

Зафиксированные решения, повлиявшие на план:

1. **Cost model v1** — flat cost как в research `bt_trades()`: `(spread + slippage) * pip_size`, вычитается из PnL любой сделки. Realistic mode (v2) — будущее расширение.
2. **Commission** — только $-mode метрики (`pnl_dollar -= volume * commission`). Не участвует в R-расчётах.
3. **Warmup** — демон загружает warmup бары и подавляет ENTER на первом тике (флаг в BacktestEngine, аналогично live Scheduler).
4. **MTF** — стратегия сама ресемплирует старшие TF из сигнального. BacktestEngine не знает про MTF.
5. **Мульти-TF символы** (fx_intraday: M15/M30/H1) — timeline = union timestamps всех TF. Стратегия сама разбирается что обновилось.
6. **MODIFY_SL** — стратегия даёт сигнал, BacktestExecutor применяет мгновенно.
7. **SL/TP приоритет** — прописан для LONG и SHORT отдельно (section 5).
8. **Бектест StateManager** — in-memory, без файла на диске.
9. **Research ref + backtest_runs** — хранятся в `bars_history.db`.
10. **Asset class naming** — стандартизировано: `forex`, `index`, `metal`, `energy`, `crypto`.
11. **Файловая структура** — `daemon/Tester/` (namespace `Daemon.Tester`), partial class в `Dashboard/`, UI в `wwwroot/backtest.js`.
