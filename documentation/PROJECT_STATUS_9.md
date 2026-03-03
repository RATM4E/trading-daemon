# TRADING DAEMON -- Структура проекта и текущее состояние

**Дата**: 24.02.2026
**Прогресс**: Фазы 0-8 завершены | Фаза 9 -- боевое тестирование (Virtual Trading + Dashboard Polish + Schema Unification + R-cap Gate + Dashboard Polish II + Dashboard Optimization + Margin/Lot Fix реализованы)

---

## 1. Обзор файлов -- что есть сейчас

```
D:\trading-daemon\
|
|-- start_daemon.bat                     [Phase 8]   Лаунчер: консоль + браузер
|-- start_daemon.vbs                     [Phase 9]   Скрытый запуск (без консоли)
|-- watchdog.bat                         [Phase 9]   Crash recovery: VBS hidden launch
|-- watchdog_silent.vbs                  [Phase 9]   Обёртка для Task Scheduler
|-- setup_watchdog.ps1                   [Phase 9]   Установка задачи в Планировщик
|-- STRATEGY_CONFIG_SCHEMA.md            [Phase 9.R] Unified config schema v1.1 + r_cap documentation
|
|-- daemon/                              <-- C# .NET 8 daemon
|   |-- daemon.csproj                    [Phase 1]  FINAL
|   |-- Program.cs               ~1600L  [Phase 9.V] Engine loop + crash recovery + BarsCache->Dashboard
|   |                                                + VirtualTracker tick + equity snapshots (5min timer)
|   |-- config.json                      [Phase 9.R]  Terminals + auto-persist (strategies: [] with momentum_cont)
|   |-- news_calendar.json               [Phase 9]   Авто-обновление из ForexFactory каждые 12ч
|   |
|   |-- Config/
|   |   +-- DaemonConfig.cs       ~190L  [Phase 9.R] + Enabled, SortOrder, VirtualSlippagePts,
|   |                                                  CommissionPerLot, virtual mode support,
|   |                                                  StrategyAssignment.RCap (optional override)
|   |
|   |-- Connector/
|   |   |-- ConnectorManager.cs   ~380L  [Phase 9.M] + RestartWorkerAsync, OnTerminalStatusChanged,
|   |   |                                              GetEnabledTerminalIds, StopTerminalAsync,
|   |   |                                              CheckSymbolsAsync (symbol_map + worker),
|   |   |                                              CalcProfitAsync (MT5 OrderCalcProfit)
|   |   +-- WorkerProcess.cs      ~390L  [Phase 9.M] TCP клиент к Python worker + CalcLeverageAsync,
|   |                                                  CheckSymbolsAsync, CalcProfitAsync
|   |
|   |-- Engine/
|   |   |-- StateManager.cs      ~1680L  [Phase 9.M] + strategy_registry, DeleteTerminalData,
|   |   |                                              virtual_balance/margin CRUD, virtual_equity table,
|   |   |                                              sl_history table, trade_snapshots table,
|   |   |                                              null-safe DB reads, ResetVirtualTrading (purge),
|   |   |                                              NoTradeOn toggle, risk_factor column,
|   |   |                                              tier column + migration, DailyDdMode field,
|   |   |                                              InitSymbolSizingDefaults (RiskFactor,AssetClass,Tier),
|   |   |                                              ResetSizingFactors (risk_factor only),
|   |   |                                              daily_r table, protector_fired column,
|   |   |                                              MarkProtectorFired/AddDailyR/GetDailyR,
|   |   |                                              RCapOn/RCapLimit in TerminalProfile,
|   |   |                                              class_leverage table (persisted per-class leverage),
|   |   |                                              SaveClassLeverage/GetClassLeverage/GetEffectiveLeverage,
|   |   |                                              DefaultLeverage constants, AssetClassToLeverageClass mapping
|   |   |-- Reconciler.cs         ~310L  [Phase 9.R] + timezone-aware GetBrokerDate,
|   |   |                                              .Where(!IsVirtual) filter,
|   |   |                                              R-cap: RCalc on position close → AddDailyR
|   |   |-- RiskManager.cs       ~670L   [Phase 9.M] 12-gate chain (G1-G12),
|   |   |                                              Gate 1: "virtual" => Pass(),
|   |   |                                              Gate 2: Daily DD soft/hard mode,
|   |   |                                              Gate 5: effective leverage fallback + CalcProfit reduce,
|   |   |                                              Gate 6: Margin1Lot + effective leverage (not acc.Leverage),
|   |   |                                              Gate 7: minImpact from profile,
|   |   |                                              Gate 10: NoTradeOn toggle support,
|   |   |                                              Gate 11: Same Symbol Per Strategy,
|   |   |                                              Gate 12: R-cap (daily R-budget per strategy,
|   |   |                                                tri-state: auto/ON/OFF from dashboard)
|   |   |-- RCalc.cs              ~80L   [Phase 9.R] NEW: R-result calculator (TP→+tp_r,
|   |   |                                              SL→-1.0R, SL+protector→lock_r)
|   |   |-- LotCalculator.cs      ~140L  [Phase 9.M] Dual-mode: MT5 CalcProfit (preferred) / tick math (fallback),
|   |   |                                              loss1Lot parameter, CalcMethod tracking
|   |   |-- NewsService.cs       ~380L   [Phase 9.P] Календарь + IsBlocked (minImpact gate) +
|   |   |                                              IsBlockedGlobal (tile display) + авто-фетчер ForexFactory
|   |   |-- AlertService.cs      ~730L   [Phase 9]   + OnTerminalStatusChanged (disconnect alerts),
|   |   |                                              heartbeat AUDIT logging, BuildHeartbeatSummary,
|   |   |                                              polling timeout fix (retry instead of exit)
|   |   |-- ActiveProtection.cs  ~320L   [Phase 9.S] DD мониторинг: Yellow/Red/Emergency, timezone-aware,
|   |   |                                              soft/hard DD mode (soft: realized-only, no force-close)
|   |   |-- BarsCache.cs          188L   [Phase 6]   In-memory кэш баров, детекция новых свечей
|   |   +-- VirtualTracker.cs    ~330L   [Phase 9.M] NEW: SL/TP monitor для виртуальных позиций,
|   |                                                  gap execution (fill at bar Open), P&L calculation
|   |                                                  (tick-based + commission), trade snapshots,
|   |                                                  3SL guard integration, Telegram alerts,
|   |                                                  effective leverage for virtual margin
|   |
|   |-- Strategy/                        [Phase 6]
|   |   |-- Protocol.cs           282L   [Phase 9.R] HELLO/ACK/TICK/ACTIONS/STOP/GOODBYE + TpPrice field
|   |   |                                              + StrategyRequirements.RCap (from HELLO)
|   |   |-- Scheduler.cs         ~905L   [Phase 9.M] + per-symbol RiskFactor multiplier, enabled check, max_lot cap,
|   |   |                                              HandleVirtualEnterAsync, HandleVirtualExitAsync,
|   |   |                                              virtual MODIFY_SL path, spread+slippage model,
|   |   |                                              virtual margin tracking (effective leverage),
|   |   |                                              G2/G3 virtual balance,
|   |   |                                              TP pass-through (real + virtual), warmup gate,
|   |   |                                              CalcProfitAsync → loss1Lot → LotCalculator,
|   |   |                                              R-cap: MarkProtectorFired on MODIFY_SL,
|   |   |                                              RCalc on virtual close → AddDailyR,
|   |   |                                              RCap pass-through in TradeRequest
|   |   |-- StrategyProcess.cs   ~525L   [Phase 9.R] TCP listener + Python process lifecycle + WarmupDone
|   |   |                                              + RCap (assignment override ?? Requirements.RCap)
|   |   +-- StrategyManager.cs   ~450L   [Phase 9.O] Auto-discovery (30s timer), enable/disable,
|   |                                                  magic registry, OnStrategiesChanged event,
|   |                                                  _configRCapCache (r_cap from config.json),
|   |                                                  GetEffectiveRCapForTerminal (3-level fallback)
|   |
|   |-- Dashboard/                       [Phase 7]
|   |   +-- DashboardServer.cs  ~3185L   [Phase 9.M] + strategy enable/disable, terminal management,
|   |                                                   reorder, worker restart on Start Terminal,
|   |                                                   ~43 команд, strategy indicators,
|   |                                                   CHECK_SYMBOLS for availability,
|   |                                                   ParseStrategyConfigForSizing (unified, multi-format),
|   |                                                   init_sizing + reset_sizing commands,
|   |                                                   Virtual Trading UI + news always-on,
|   |                                                   RiskFactor sizing, newsMinImpact filter,
|   |                                                   toggle_no_trade, trade chart (digits, drag, bars),
|   |                                                   dailyDdMode profile support,
|   |                                                   rCapOn/rCapLimit/rCapConfigDefault,
|   |                                                   save_profile R-cap fields,
|   |                                                   IsBlockedGlobal news tile, probe_terminal,
|   |                                                   open_strategy_folder, WMI terminal discovery,
|   |                                                   virtual unrealized P/L cache,
|   |                                                   closed trade bars from BarsCache,
|   |                                                   HandleGetTerminalsAsync (non-blocking),
|   |                                                   batch GetAllProfiles, news pre-computation,
|   |                                                   leverage persistence (restore from DB on startup),
|   |                                                   virtual margin close with effective leverage
|   |
|   |-- Models/
|   |   +-- Models.cs            ~130L   [Phase 9]   Position, AccountInfo, Bar, Deal, InstrumentCard + Margin1Lot
|   |
|   |-- wwwroot/                         [Phase 7]
|   |   +-- index.html          ~1925L   [Phase 9.P] + strategy enable/disable toggle, drag & drop terminals,
|   |                                                   delete with confirmation, strategy pills on tiles,
|   |                                                   connecting state, resizable columns,
|   |                                                   symbol availability indicators,
|   |                                                   Virtual Tab: equity chart, stats, reset, export,
|   |                                                   RiskFactor slider (0.0-1.0) in settings,
|   |                                                   News: always-on red events, block level selector,
|   |                                                   Trading Hours: clickable ON/OFF toggle button,
|   |                                                   Trade Chart: centered, draggable, digits precision,
|   |                                                   live PnL, closed trade bars, TMM-style,
|   |                                                   HeartBeat hidden from All/AUDIT filters,
|   |                                                   Sizing: Tier column (T1/T2/T3 color-coded), tier filter,
|   |                                                   Reset Sizing button, All resets both filters,
|   |                                                   Daily DD soft/hard toggle badge,
|   |                                                   R-cap: ON/OFF toggle + value in Settings,
|   |                                                   RCap indicator on DD progress bar (green/gray),
|   |                                                   Settings grid reorder (Mode->left),
|   |                                                   Drag restricted to hamburger handles,
|   |                                                   Strategy folder open buttons,
|   |                                                   Strategy tiles drag reorder,
|   |                                                   Manual terminal probe + add,
|   |                                                   Closed trade chart buttons
|   |
|   +-- Data/
|       |-- state.db                     [runtime]   SQLite (+ symbol_sizing, strategy_registry,
|       |                                              virtual_equity, sl_history, trade_snapshots,
|       |                                              daily_r, class_leverage,
|       |                                              positions.is_virtual/timeframe/protector_fired,
|       |                                              terminal_profiles.virtual_balance/margin/commission/
|       |                                              no_trade_on, daily_dd_mode, r_cap_on, r_cap_limit,
|       |                                              symbol_sizing.risk_factor/tier)
|       +-- .clean_shutdown              [runtime]   Флаг штатного завершения
|
|-- strategies/                          [Phase 6+8]
|   |-- runner.py                ~290L   [Phase 9]   + config "strategy" field валидация
|   |-- compression_breakout/            [Phase 9.S]  Migrated to unified schema v1.1
|   |   |-- strategy.py                  [Phase 9.S] directions → flat combo_map (__init__ only)
|   |   +-- config.json                  [Phase 9.S] directions.BOTH.strat/daemon, 6 index symbols
|   |-- pairs_zscore/                    [Phase 9.S]  Migrated to unified schema v1.1
|   |   |-- strategy.py                  [Phase 9.S] combos → pairs_cfg (__init__ only), params block
|   |   +-- config.json                  [Phase 9.S] combos with symA/symB/strat/daemon
|   |-- vwap/                            [Phase 9.S]  Migrated to unified schema v1.1
|   |   |-- strategy.py                  [Phase 9.S] directions.LONG+SHORT → flat combos (__init__ only)
|   |   +-- config.json                  [Phase 9.S] 30 symbols × 2 directions, per-direction params
|   |-- fx_intraday/                     [Phase 9.S]  Migrated to unified schema v1.1
|   |   |-- strategy.py                  [Phase 9.S] directions.BOTH.strat → flat ComboConfig, JSON signal_data
|   |   +-- config.json                  [Phase 9.S] RSI+DVWAP combos, weight→size_r, params block
|   |-- signal_test_strategy/            [Phase 9.S]  Migrated to unified schema v1.1
|   |   |-- strategy.py          ~326L   [Phase 9.S] RSI mean-reversion, directions → flat (__init__ only)
|   |   +-- config.json                  [Phase 9.S] 8 symbols × LONG+SHORT directions
|   |-- momentum_cont/                   [Phase 9.R]  NEW: MTF momentum continuation strategy
|   |   |-- strategy.py          ~617L   [Phase 9.R] HTF impulse + LTF RSI pullback, tier-based sizing,
|   |   |                                              protector management, signal_data (tp_r, protector_lock_r),
|   |   |                                              get_requirements → r_cap from config
|   |   +-- config.json                  [Phase 9.R] 20 symbols, T1/T2 tiers, params.r_cap=1.5
|   |-- bb_breakout_v1/                  [future]    Пустая папка
|   +-- test_strategy/                   [Phase 6]   Тестовая стратегия
|       |-- strategy.py           137L   [Phase 6]   ENTER каждый 5-й tick, EXIT через 3
|       +-- config.json             8L   [Phase 6]   Параметры test_strategy
|
|-- workers/
|   |-- mt5_worker.py            ~870L   [Phase 9.M] + order_calc_margin -> margin_1lot
|   |                                                  + auto-reconnect после 3 heartbeat errors
|   |                                                  + CALC_LEVERAGE (auto-discovery, fuzzy resolve)
|   |                                                  + CHECK_SYMBOLS (alias table, suffix variations)
|   |                                                  + CALC_PROFIT (OrderCalcProfit for cross/JPY/metals/indices)
|   +-- probe_terminal.py          55L   [Phase 7]   Автообнаружение MT5 терминалов
|
|-- test_worker.py                       [Phase 1]   Утилита тестирования worker
|-- test_orders.py                       [Phase 1]   Утилита тестирования ордеров
+-- mt5_worker_patch.py                  [Phase 4]   Патч для history_deals
```

---

## 2. Что нового в Phase 9 (vs Phase 8)

### News Calendar -- автоматический фетчер

NewsService теперь сам скачивает календарь с ForexFactory API каждые 12 часов. Ручное заполнение `news_calendar.json` больше не нужно. Gate 7 (News Guard) полностью автономен -- блокирует входы перед NFP, FOMC, CPI без вмешательства оператора.

### Gate 10: Trading Hours

Новый гейт в RiskManager. Блокирует ENTER в заданном окне по серверному времени брокера (DST-aware). Покрывает rollover gaps, maintenance windows, периоды широких спредов. Поддерживает пересечение полночи (23:30 -> 01:30). Настраивается через dashboard per terminal.

### Gate 11: Same Symbol Per Strategy (17.02.2026)

Новый гейт -- запрет дублирования позиций по символу в рамках одной стратегии. Фильтрует по magic number: стратегия A (magic 100001) не откроет вторую EURUSD, но стратегия B (magic 100002) может торговать EURUSD параллельно. Итого **11 гейтов** в цепочке.

### Strategy Auto-Discovery (17.02.2026)

Полная автоматизация обнаружения и управления стратегиями -- никакого хардкода в config.json:

**Auto-discovery**: StrategyManager сканирует `strategies/` каждые 30с таймером. Папка с `strategy.py` + `config.json` -> авто-регистрация в `strategy_registry` (state.db). Папки с `_` или `.` в начале игнорируются. При удалении папки -- running instances останавливаются, запись удаляется из DB, плитка исчезает из dashboard.

**Enable/Disable**: новая стратегия появляется как Disabled. Оператор включает через dashboard. Disable автоматически останавливает все running instances. Состояние сохраняется в SQLite -> переживает рестарт.

**Magic allocation**: каждая стратегия получает блок из 1000 magic numbers. Первая = 100, вторая = 1100, и т.д. Magic хранится в `strategy_registry`, не в config.json.

**config.json** -> `"strategies": []` -- пустой массив. Runtime assignments создаются при Start из dashboard, magic берётся из registry.

**Dashboard UI**: плитки стратегий с кнопкой Enable/Disable, magic_base badge, затенение Assign+Start при disabled.

### CHECK_SYMBOLS -- terminal-native symbol availability (18.02.2026)

Определение доступности символов на терминале **без зависимости** от запущенных стратегий или загруженных баров:

**mt5_worker.py**: таблица `SYMBOL_ALIASES` -- 16 канонических символов -> варианты брокеров (XAUUSD->[GOLD, XAUUSD.fix, ...], BTCUSD->[XBTUSD, BTCUSDm, ...], индексы, нефть, крипто). Команда `CHECK_SYMBOLS`: получает список символов, пробует exact match -> aliases -> suffix variations (.m, .pro, .ecn, .fix, .i, .micro, .cash). Вызывает `mt5.symbols_get()` однократно, резолвит все символы за один проход.

**WorkerProcess.cs**: `CheckSymbolsAsync()` -- отправляет CHECK_SYMBOLS, парсит ответ в `(Dictionary<string,string> Resolved, List<string> Missing)`.

**ConnectorManager.cs**: `CheckSymbolsAsync()` -- применяет config `symbol_map` (пользовательские override'ы) перед вызовом worker, обратный маппинг результатов.

**DashboardServer.cs**: единый CHECK_SYMBOLS вызов перед циклом Sizing, результат в `HashSet<string> availableSymbols`. Graceful fallback: если CHECK_SYMBOLS не удался -- считаем все доступными.

### Phase 9.D: Dashboard Polish (21.02.2026)

Комплексная доработка dashboard UI и sizing архитектуры. 12 пунктов бэклога за одну сессию.

**Risk Factor рефакторинг** -- замена абсолютного `risk_pct` (0.5%) на мультипликатор `RiskFactor` (0.0-1.0). Scheduler умножает eff_risk_pct из strategy config на RiskFactor из sizing. Значение 1.0 = полный риск по конфигу стратегии, 0.5 = половина. Dashboard: слайдер вместо текстового поля, визуальная шкала. Новая колонка `risk_factor REAL DEFAULT 1.0` в `symbol_sizing`. Обратная совместимость: старые записи с risk_pct конвертируются автоматически.

**Virtual P&L исправления** -- VirtualTracker теперь предварительно кэширует InstrumentCard при каждом тике (убрана зависимость от отдельного fetch). Удалён fallback `contractSize=100000` который давал аномальный P&L для крипто/индексов. DashboardServer подтягивает свежие InstrumentCard при запросе позиций. Max Drawdown корректно показывает отрицательное значение. Equity snapshot добавляет sanity check (100 < equity < 10M). ResetVirtualTrading расширен: чистит closed positions, trade snapshots, daily P&L записи.

**News display always-on** -- высокоимпактные (красные) новости видны на плитке терминала ВСЕГДА, даже когда News Guard выключен. Логика: `activeNews` поле в guards -- находит события с Impact >= 3 в пределах newsWindow, показывает тайминг (in Xm / NOW / Xm ago). Приоритет отображения: newsBlock (amber) → activeNews (red) → nextNews (zinc). Иконки импакта: 🟥 high, 🟨 medium, ⬜ low.

**News importance filter** -- селектор в settings panel: Low (1), Medium (2), High only (3). Фильтрует "next news" отображение. Active news (красные) всегда показываются независимо от фильтра. Включает USD news toggle (ранее был в профиле, но без UI).

**Trading Hours clickable toggle** -- кнопка ON/OFF на плитке терминала, аналогично News/3SL guards. Новое поле `NoTradeOn` в TerminalProfile. При OFF -- Gate 10 пропускает (return Pass), но часы сохраняются в профиле. DB миграция: `no_trade_on INTEGER DEFAULT 1`. Новая команда `toggle_no_trade`. Визуально: зелёная кнопка когда ON, серая когда OFF.

**Trade Chart modal -- полная переработка (enhanced in 9.P):**
- Бэкенд: лимит 250 баров, `digits` из InstrumentCard, `current` price, closed trade bars from BarsCache
- Высота 420px, ширина max-w-4xl, **centered + draggable** (grab header to move)
- TP линия: зелёная dotted (ранее отсутствовала)
- SL trail: step-line серия вместо невидимых кружков -- видно движение SL, **digits precision**
- Entry/Exit маркеры: стрелки BUY↑/SELL↓ на entry, квадрат на exit с close_reason
- Auto-refresh: live trades обновляются каждые 10 секунд, **PnL обновляется в заголовке**
- Escape закрывает попап
- Footer: grid (Entry, Exit/Current, Volume, SL, TP, SL moves, Duration, Terminal)
- Header: бейдж таймфрейма, `● LIVE` анимация, цветные бейджи TP/SL для close_reason
- Snapshot compatibility: корректно парсит оба формата полей (camelCase и snake_case)
- **Closed trades**: бары подгружаются из BarsCache, entry/exit маркеры, duration

**HeartBeat фильтрация** -- heartbeat записи скрыты из фильтров "All" и "AUDIT" в Log tab. Доступны только через выделенную кнопку "⏱ HB". Запись в БД сохранена для диагностики.

### Phase 9.S: Schema Unification + Daemon Patches (22.02.2026)

Унификация конфигов всех стратегий под единую схему (STRATEGY_CONFIG_SCHEMA.md), разделение Init/Reset в sizing, TP support, G2 soft/hard DD modes, warmup gate.

**Unified Config Schema v1.1** -- стандарт для всех стратегий. Каждый combo содержит `directions` объект с вложенными `strat` (параметры для Python) и `daemon` (параметры для C# demon) блоками. Ключевые поля: `tier` (T1/T2/T3 -- качество combo из бэктеста), `aclass` (forex/indices/crypto/commodities), `size_r` (risk multiplier из бэктеста, в daemon блоке), `role` (PRIMARY/SECONDARY). Directions: LONG, SHORT, BOTH. Backward compatible -- старый flat формат работает через fallback в `__init__`. Документация: STRATEGY_CONFIG_SCHEMA.md.

**5 стратегий мигрированы:**
- **compression_breakout** (v1.0→1.1): 6 index symbols, directions.BOTH, params universal
- **pairs_zscore** (v1.0→1.1): combos with symA/symB/strat/daemon, params block
- **vwap** (v1.0→1.1): 30 symbols × LONG+SHORT → directions per symbol, per-direction params (tf, P, sl_mult, mode)
- **signal_test** (v1.0→1.1): 8 symbols × LONG+SHORT directions
- **fx_intraday** (v1.0→1.1): 12 RSI+DVWAP combos, weight→size_r, signal_data→JSON

Все миграции затрагивают **только `__init__`** -- стратегия разворачивает `directions.*.strat` в flat dict для существующего кода. Остальная логика (on_bars, entry/exit, state management) не меняется.

**Init vs Reset Sizing** -- разделение двух операций в DashboardServer + StateManager:
- **Init**: полная загрузка из config → symbols, aclass, tier, size_r → risk_factor. Добавляет новые символы, удаляет отсутствующие.
- **Reset**: только сброс risk_factor к значениям из config. Сохраняет enabled, notes, max_lot.
ParseStrategyConfigForSizing -- единый хелпер для парсинга всех форматов (directions/flat/pairs/symbols). Tier читается из combo level.

**Sizing Tab UI** -- новая колонка Tier с цветовой кодировкой: T1 emerald, T2 amber, T3 gray. Фильтр T1/T2/T3 кнопки с счётчиками (on/total). Bulk actions (Enable/Disable/Scale) учитывают оба фильтра (aclass + tier). Кнопка All сбрасывает оба фильтра. Reset button отправляет reset_sizing.

**TP Price support** -- Protocol.cs: `TpPrice` поле в `StrategyAction`. Scheduler передаёт TP в MT5 ордер (`["tp"] = action.TpPrice ?? 0`) и в виртуальную позицию (`TP = action.TpPrice ?? 0`). VirtualTracker уже обрабатывал TP hits -- теперь стратегии могут посылать tp_price в ENTER action.

**G2 Soft/Hard Daily DD** -- два режима проверки дневного drawdown:
- **Hard** (default): realized + unrealized P/L. При 100% -- EmergencyCloseAll (форс-закрытие всех позиций).
- **Soft**: только realized P/L. При 100% -- блокировка новых входов (G2 gate reject), но открытые позиции живут до SL/TP. Нет force-close.
TerminalProfile: `DailyDdMode` ("soft"/"hard"). DB миграция: `daily_dd_mode TEXT DEFAULT 'hard'`. RiskManager G2: branch по mode. ActiveProtection: soft пропускает EmergencyCloseAll. Dashboard: кликабельный badge SOFT (amber) / HARD (red) рядом с Daily DD limit. Индикатор режима на панели protection.

**Warmup Gate** -- StrategyProcess.WarmupDone (default false). Scheduler: первый тик после старта/рестарта стратегии -- observation-only. ENTER actions подавляются, EXIT/MODIFY_SL проходят (для crash recovery). Предотвращает ложные сигналы на stale данных (weekend startup, history load).

### Phase 9.P: Dashboard Polish II (23.02.2026)

Вторая волна полировки dashboard: UX, bug fixes, новые возможности.

**News impact filtering fix** -- NewsService разделена на два метода: `IsBlocked(minImpact)` для Gate 7 (использует настройку терминала) и `IsBlockedGlobal()` для отображения на плитке (всегда impact >= 3). RiskManager передаёт `profile.NewsMinImpact` в IsBlocked. Плитка показывает только высокоимпактные новости независимо от настройки гейта.

**Drag handles restricted** -- перетаскивание терминалов и стратегий теперь работает только за иконку-гамбургер (☰). Ранее drag инициировался при клике в любом месте плитки, мешая взаимодействию с элементами. Terminal drag: `draggable` на карточке + `onDragStart` проверяет `dragReady` ref, который ставится только mousedown на иконке. Strategy drag: аналогичная механика с `sDragReady` ref.

**Strategy folder open** -- кнопка 📂 рядом с каждой стратегией в dashboard. Отправляет `open_strategy_folder` → backend вызывает `explorer.exe /select,config.json` для быстрого доступа к конфигу стратегии.

**Virtual unrealized P/L caching** -- DashboardServer кэширует нереализованный P/L виртуальных позиций (`_virtualUnrealPnlCache`) с TTL 5 секунд. Устраняет повторные вызовы `GetSymbolInfoAsync` при каждом обновлении позиций.

**Trade Chart fixes (5 issues):**
1. **Центрирование** -- модалка по центру экрана (`items-center justify-center`), а не прижата к правому краю
2. **Перетаскивание** -- хедер `cursor-grab` с mouse events, позиция сохраняется при auto-refresh (10с)
3. **Ценовая шкала** -- бэкенд отправляет `digits` из `InstrumentCard.Digits`, фронтенд использует `priceFormat: { precision: digits, minMove: 1/10^digits }`. AUDCHF показывает 5 знаков (было 2)
4. **Live PnL** -- бэкенд вызывает `GetSymbolInfoAsync` + `CacheSymbol` перед расчётом P/L для виртуальных позиций. Для реальных использует точные tick values от коннектора
5. **Closed trade bars** -- бэкенд теперь берёт бары из BarsCache для закрытых позиций (было: `bars = new List<object>()`). Timeframe читается из DB. Entry/exit маркеры, close_reason, duration отображаются корректно

**Closed trade chart buttons** -- кнопка 📊 на каждой строке Recent Closed для просмотра графика закрытой сделки.

**Terminal discovery WMI fallback** -- HandleDiscoverTerminals сканирует несколько имён процессов (terminal64, terminal, metatrader64, metatrader). При AccessDeniedException на `proc.MainModule.FileName` использует WMI (`wmic process ... get ExecutablePath`) для получения пути. Предупреждение в UI о недоступных процессах.

**Manual terminal probe** -- новая команда `probe_terminal`: пользователь вставляет путь к terminal64.exe → бэкенд валидирует (File.Exists, filename check) → запускает probe_terminal.py → возвращает account info → можно добавить через UI. Улучшенная обработка ошибок: детальные сообщения вместо generic "Probe failed", 15с таймаут, поиск скрипта в нескольких директориях.

**RunProbeAsync improvements** -- возвращает ProbeResult с детальной ошибкой вместо null. Поиск probe_terminal.py в альтернативных локациях (AppContext.BaseDirectory, ../workers/, workers/). Логирование команды запуска и результата. 15-секундный таймаут.

### Telegram Polling Fix (BUG-6, 18.02.2026)

`HttpClient.GetAsync` бросает `TaskCanceledException` (наследник `OperationCanceledException`) при двух ситуациях: cancellation token отменён (штатный shutdown) и HttpClient timeout (сетевая задержка). Одиночный timeout убивал polling loop навсегда.

**Исправление** в AlertService.cs:
```csharp
catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
catch (OperationCanceledException) { _log.Warn("[Telegram] Long-poll timeout, retrying..."); }
```

### Multi-format Sizing Init (18.02.2026, extended 22.02.2026)

ParseStrategyConfigForSizing -- единый хелпер, поддерживает **все форматы** конфигов:

- **directions** (unified v1.1) -- `directions → {LONG/SHORT/BOTH} → daemon → size_r` (max across directions)
- **symbols** dict (legacy) -- `{ "symbols": { "EURUSD": {...}, ... } }`
- **combos** array (flat) -- `size_r` directly on combo (legacy)
- **pairs** array (pairs_zscore) -- `symA`/`symB` with daemon block or flat size_r

Извлекает уникальные символы, применяет дефолтный risk и asset_class. Автоматическое определение формата по наличию ключей.

### Phase 9.V: Virtual Trading (19.02.2026)

Полная реализация виртуального трейдинга по архитектуре VIRTUAL_TRADING_ARCHITECTURE.md (1068 строк). Позволяет тестировать стратегии на реальных рыночных данных без размещения ордеров в MT5. Все сигналы проходят через 12-gate risk chain, P&L считается точно, equity и статистика отображаются в dashboard.

**Core engine** -- VirtualTracker.cs (~330L): мониторит виртуальные позиции каждый тик из BarsCache. Проверяет SL (BUY: Low <= SL, SELL: High >= SL) и TP. Gap execution: если бар открывается за уровнем SL/TP -- заполнение по цене Open. P&L: tick-based (TradeTickSize/TradeTickValue) с fallback на contractSize*priceDiff. Round-trip commission per lot. Trade snapshot JSON сохраняется при закрытии (баров за период жизни сделки + SL trail).

**Scheduler paths** -- HandleVirtualEnterAsync: генерация отрицательного тикета, spread (BUY at Ask), slippage, сохранение позиции (is_virtual=1, timeframe), маржа. HandleVirtualExitAsync: P&L, обновление баланса, маржа release, daily P&L, 3SL guard. Virtual MODIFY_SL: обновление в DB + SL history logging.

**Virtual balance/margin** -- StateManager: GetVirtualBalance, InitVirtualBalance (auto-init при первом ордере или mode switch), UpdateVirtualBalance, AddVirtualMargin, GetVirtualMargin, SetVirtualMargin, ResetVirtualTrading. G2/G3 DD gates используют virtual_balance в virtual mode. G6 учитывает virtual_margin.

**Dashboard Virtual UI** -- mode dropdown теперь включает "virtual". Purple V.Balance/V.Equity/V.Unrealized/V.Positions панель. [V] badge на виртуальных позициях. Filter: All/Real/Virtual. Virtual Tab с equity chart (Lightweight Charts, trade markers green/red), statistics panel (Win Rate, Profit Factor, Max Drawdown, Expectancy), Reset Virtual + Export CSV. Trade Chart modal (TMM-style): candlestick chart с entry/exit lines (dashed), SL trail markers, P&L zone (green/red fill), header с symbol/direction/P&L/duration.

**Telegram** -- виртуальные алерты с фиолетовым префиксом: VIRTUAL ENTER/EXIT/SL/TP.

**DB migration** -- 8 новых таблиц/полей: positions.is_virtual, positions.timeframe, terminal_profiles.virtual_balance, terminal_profiles.virtual_margin, terminal_profiles.commission_per_lot, virtual_equity table (equity snapshots 5min), sl_history table (trail visualization), trade_snapshots table (chart caching).

**signal_test_strategy** -- тестовая стратегия для валидации виртуальных путей: RSI mean-reversion (RSI<30 + price>EMA50), SL=2xATR, TP=3xATR, trailing SL, signal EXIT на развороте RSI. Combos format, 5 символов.

**10 bug fixes**: close button для virtual positions, Close All virtual branch, null-safe DB reads (IsDBNull), crypto/index P&L (contractSize=1), correct P&L at close, encoding mojibake (15x em dash, 5x bullet, arrows), auto-init balance on mode switch, VirtualTracker _symbolCache, GetSpread() inline estimation, G2/G3 TradeRequest.AccountBalance virtual balance.

### Dashboard -- новые возможности

Панель Guards вынесена из Settings: 3SL Guard toggle, News Guard toggle + window настройка, Trading Hours toggle + no-trade window. Start Terminal кнопка (запускает `terminal64.exe` по path из конфига, проверяет через `Process.GetProcessesByName`, а не через TCP). Shutdown dialog с опцией "Also close MT5 terminals". Кнопка пишет `.clean_shutdown` флаг.

**Resizable columns** (18.02.2026) -- все таблицы (Sizing, Positions, Log) поддерживают drag-resize колонок. Символы с индикатором availability и левым отступом для alignment.

### Terminal Management (17.02.2026)

Полный цикл управления терминалами из dashboard:

**Enable/Disable** -- кнопка на плитке. Disabled: worker останавливается, стратегии стопятся, терминал исключается из heartbeat. Серый тайл + бейдж DISABLED. Поле `enabled` в config.json, сохраняется при перезапуске.

**Delete** -- кнопка с модальным окном подтверждения. Требует ввести имя терминала. Полная очистка DB: 9 таблиц (positions, events, strategy_state, active_strategies, sl3_state, daily_pnl, execution_quality, symbol_sizing, terminal_profiles) в транзакции. Удаляет из config.json + strategy assignments.

**Drag & Drop Reordering** -- HTML5 Drag API. Хэндл на каждой плитке, подсветка при drop. Порядок сохраняется в `sort_order` в config.json.

**Strategy Indicators** -- фиолетовые пилюли под хедером плитки. Показывают running стратегии: имя + magic number + StatusDot. Кликабельны -- переход на вкладку Strategies.

### Heartbeat Fix + Disconnect Alerts (17.02.2026)

**Heartbeat bug fix** -- `BuildStatusReport()` теперь вызывает `GetAccountInfoAsync()` вместо `TcpClient.Connected` для проверки реального состояния. Disabled терминалы исключаются из счётчика. Отключённые показываются поимённо.

**Heartbeat AUDIT** -- каждый heartbeat пишется в events table: `heartbeat_sent: terminals=2/3, positions=0, floating=$0.00`. В LogTab фильтр HB.

**Disconnect alerts** -- ConnectorManager отслеживает `_lastConnState` per terminal. При переходе connected->disconnected -> мгновенный Telegram. При reconnect -> Telegram reconnected.

**Worker restart on Start Terminal** -- `HandleStartTerminal` теперь не просто запускает MT5.exe, но через 12с (или 3с если MT5 уже запущен) автоматически вызывает `RestartWorkerAsync()`. Плитка показывает состояние `connecting` (жёлтая пульсация) до подключения.

### Telegram -- сигналы стратегии

Каждый сигнал от стратегии уходит в Telegram: SIGNAL если пропущен дальше по цепочке, BLOCKED с указанием гейта который заблокировал. Оператор видит все торговые решения в реальном времени с телефона.

### Signal Dedup

В `strategy.py` добавлен механизм `_last_signal_bar` -- один сигнал на свечу. Без повторов при M30 tick на H1 таймфрейме. Решает проблему дублирующих Telegram сообщений.

### Crash Recovery

Полная система восстановления после сбоев. При штатном завершении создаётся `Data/.clean_shutdown`. При старте демон проверяет флаг: если нет -- считает что был крэш и автоматически запускает терминалы, ждёт подключения, восстанавливает стратегии из `active_strategies`. Watchdog проверяет каждые 2 минуты через Task Scheduler. Запуск через VBS -- скрытый, без чёрного окна.

### Bug Fixes (16.02.2026)

**Shutdown fix (BUG-1/3)**: `OnShutdownRequested` подключён к `cts.Cancel()`. Cleanup с fresh 15s timeout. Воркеры корректно убиваются, демон завершается.

**MT5 reconnect (BUG-2)**: После 3 heartbeat errors воркер делает `mt5.shutdown()` -> `mt5.initialize()` -> retry (до 3 попыток). Start Terminal -> воркер переподключается автоматически через ~30с.

**Strategy display fix**: Стратегии запущенные через crash recovery теперь видны в dashboard. `StartStrategyAsync` добавляет assignment + broadcast.

**Config self-documentation**: runner.py валидирует `"strategy"` поле в config.json. Защита от подсунутого конфига.

**Watchdog hidden window**: Убрано чёрное окно консоли при crash recovery. Watchdog генерирует VBS и запускает через `WScript.Shell Run 0`.

### Position Sizing Management (Addendum 15)

Полная реализация per-symbol risk management:

**Бэкенд**: таблица `symbol_sizing` в SQLite (terminal_id, symbol, enabled, risk_pct, max_lot, margin_initial, asset_class). Scheduler читает sizing при каждом ENTER -> per-symbol risk%, enabled check, max_lot cap. Символ disabled -> блокируется + Telegram алерт.

**Маржа**: `mt5.order_calc_margin(BUY, symbol, 1.0, ask)` -> точная маржа за 1 лот в валюте счёта. Работает для forex, indices, metals, energy -- всё что MT5 поддерживает. Кэшируется в SQLite для offline display.

**Dashboard Sizing tab**: выбор терминала -> Init from strategy -> символы с live ATR, Est.SL, Est.Lot, Margin. Фильтр по asset_class (FX/IDX/OIL/XAU/CRYP) с счётчиками enabled/total. Bulk Enable/Disable по классу. Scale All slider x0.1-x2.0 (работает с учётом фильтра). Risk% редактируемый per symbol. Toggle ON/OFF per symbol.

**Универсальность**: один config.json стратегии с `asset_class` per symbol. Проп -> включаем FX+IDX, OKX -> включаем CRYP, другой проп -> только T1. Без изменения стратегии или конфига.

### Первый live сигнал

CADCHF LONG -- bb_mr_v2 (удалена) сгенерировала сигнал на нижней банде (wick rejection). Заблокирован Gate 1 (Monitor Only). Вся цепочка strategy -> risk -> alert подтверждена.

### Sticky Headers, Auto-backup, Hot-reload, Audit Log (17.02.2026)

**Sticky headers** -- CSS `position: sticky` на thead во всех таблицах (Positions, Sizing, Log, terminal_detail). При 192 позициях без этого навигация невозможна.

**Auto-backup state.db** -- Scheduler раз в сутки копирует `state.db` в `Data/backups/state_YYYY-MM-DD.db`. Ротация 14 дней. SQLite файл -- вся память системы.

**Strategy hot-reload** -- кнопка Reload в dashboard рядом со стратегией. WebSocket команда `reload_strategy` -> stop Python process -> restart -> HELLO/ACK. Без перезапуска демона.

**Audit log** -- каждое действие оператора через dashboard записывается в events table с `type: "AUDIT"`. Покрывает: close/emergency, start/stop/reload strategy, save_profile, mode change, shutdown, save_sizing, init_sizing, detect_leverage, enable/disable terminal, delete terminal, reorder terminals. Source tracking.

### Leverage Per Class (Addendum 22, 17.02.2026)

Автоматическое определение эффективного плеча per asset class из MT5:

**FX** -- `account_leverage` (cross-currency формулы ненадёжны для forex). **Non-FX** -- `order_calc_margin` -> `notional/margin` с 3-уровневым поиском символов: 1) запрошенный, 2) кандидаты из hardcoded списка, 3) fuzzy search через `mt5.symbols_get()`. Предупреждение при <50 символах в Market Watch.

**Dashboard**: `FX 1:100 | IDX 1:25 | XAU 1:10 | OIL 1:5 | CRYP 1:2` на плитке каждого терминала. Кнопка для ручного re-detect. Persisted в SQLite (`class_leverage` table) -- переживает рестарт. Conservative defaults: `FX:100, IDX:20, XAU:10, OIL:5, CRYP:1`. Используется в Gate 5/6, virtual margin, lot calculation через `GetEffectiveLeverage()`.

**Результаты**: The5ers показывает реальные margin rates демо-аккаунта (FX=1%, IDX=4%, XAU=10%, OIL=20%, CRYP=50%). RoboForex -- FX 1:1000, XAU 1:1000 (только forex symbols). DEMO -- auto-discovery нашёл XAUUSD.fix и USOIL через fuzzy resolve.

### Telegram Commands (Addendum 24 Phase 1, 17.02.2026)

Readonly команды через Telegram Bot API long polling. Оператор может проверить статус с телефона без dashboard:

- `/status` -- полный отчёт: daemon uptime, terminals, strategy, open positions, daily P/L
- `/positions` -- список открытых позиций с floating P/L
- `/pnl` -- daily P/L по терминалам
- `/news` -- ближайшие новости (6ч вперёд)
- `/help` -- список команд

Polling через `getUpdates` с `timeout=30` (long polling). Отвечает только авторизованному `chat_id`. Phase 2 (action commands: /emergency, /close) -- после перехода на live.

**Telegram Heartbeat** -- периодический статус-отчёт. Если за настроенный период (по умолчанию 4ч) не было сигналов -- автоматически шлёт отчёт: uptime, terminals, strategy, positions, daily P/L, upcoming news. При каждом сигнале таймер сбрасывается -- heartbeat уходит только в тишине.

### Margin & Lot Calculation Fix (Phase 9.M, 24.02.2026)

Комплексное исправление расчёта маржи и лота для кросс-пар, JPY, индексов, металлов, энергоносителей и крипто. Обнаружено через ложные G6_DEPOSIT_LOAD блокировки на CHFJPY/AUDJPY (маржа завышалась в 150 раз).

**Корневые причины:**

1. Gate 6 использовал `contractSize × price / acc.Leverage` -- корректно только для FX majors где quote=USD. Для JPY пар `price` = 170 вместо ~1.13, для индексов account leverage (100) вместо реального (20).
2. LotCalculator использовал `ticks × tickValue` snapshot -- на кроссах tickValue требует конвертации через промежуточные курсы, которые могут быть устаревшими или 0.
3. Gate 5 reduce не пересчитывал TradeRisk при `tickValue=0` -- последующие гейты работали с завышенным риском.
4. Виртуальная маржа использовала хардкод `/100` вместо per-class leverage (5 мест в коде).
5. Per-class leverage из CALC_LEVERAGE хранился только in-memory в DashboardServer -- терялся при рестарте, недоступен из RiskManager/Scheduler.

**Решение — `OrderCalcProfit` как источник истины:**

Новая команда `CALC_PROFIT` в mt5_worker.py → `mt5.order_calc_profit()` -- MT5 сама делает все валютные конвертации. LotCalculator теперь dual-mode: prefer `loss1Lot` от MT5, fallback на tick math.

**Persistence leverage:**

Таблица `class_leverage` в SQLite. Детектированные плечи переживают рестарт. DashboardServer восстанавливает из DB при запуске. Консервативные дефолты: `FX:100, IDX:20, XAU:10, OIL:5, CRYP:1` (завышают маржу = безопаснее).

**3-уровневый fallback для маржи:**
1. `Margin1Lot` от MT5 (exact, `order_calc_margin`)
2. Detected class leverage (из `CALC_LEVERAGE`, persisted в SQLite)
3. Conservative default (IDX:20, XAU:10 и т.д.)

**Маппинг asset class → leverage class:** `forex→FX, index→IDX, metal→XAU, energy→OIL, crypto→CRYP` через `SymbolSizing.AssetClass`.

**Масштаб проблемы до фикса:**

| Символ | Gate 6 (старая формула) | MT5 реальная маржа | Завышение |
|--------|------------------------|--------------------|-----------|
| CHFJPY | $170,500/лот | ~$1,130/лот | 151× |
| AUDJPY | $95,000/лот | ~$640/лот | 148× |
| US500 (IDX) | $55/лот | ~$275/лот | 0.2× (занижение) |
| XAUUSD | $265/лот | ~$2,650/лот | 0.1× (занижение) |

JPY пары ложно блокировались, а индексы/металлы наоборот пропускались с заниженной маржой.

**Затронутые файлы (9):** mt5_worker.py, WorkerProcess.cs, ConnectorManager.cs, StateManager.cs, LotCalculator.cs, RiskManager.cs, Scheduler.cs, VirtualTracker.cs, DashboardServer.cs.

---

## 3. Статус по файлам

### СТАБИЛЬНЫЕ файлы

| Файл | Строк | Фаза | Роль | Тесты |
|------|------:|:----:|------|-------|
| `WorkerProcess.cs` | ~390 | P9.M | TCP клиент к Python worker + CalcLeverageAsync + **CheckSymbolsAsync** + **CalcProfitAsync** | -- |
| `StateManager.cs` | ~1680 | P9.M | SQLite: профили, позиции, P/L, events, 3SL, active_strategies, symbol_sizing, **strategy_registry**, DeleteTerminalData, **virtual_balance/margin CRUD, virtual_equity, sl_history, trade_snapshots**, **class_leverage**, **GetEffectiveLeverage** | 34/34 |
| `Reconciler.cs` | ~294 | P9.V | Cold/hot reconciliation DB <-> MT5, timezone-aware, **virtual filter** | live-тест |
| `RiskManager.cs` | ~670 | P9.M | **12-gate chain** (G1-G12), Gate 1: virtual=>Pass(), Gate 5: **effective leverage + CalcProfit reduce**, Gate 6: **Margin1Lot + effective leverage**, Gate 7: **minImpact**, Gate 10: NoTradeOn toggle, Gate 11: Same Symbol Per Strategy, Gate 12: R-cap | 69/69+ |
| `LotCalculator.cs` | ~140 | P9.M | **Dual-mode**: MT5 CalcProfit (preferred) / tick math (fallback), **loss1Lot** param | 14/14 |
| `NewsService.cs` | ~380 | P9.P | Календарь (UTC), auto-fetch ForexFactory 12ч, currency mapping, **IsBlocked(minImpact)**, **IsBlockedGlobal** | 21/21 |
| `AlertService.cs` | ~730 | P9 | Telegram Bot API, heartbeat, commands, **disconnect alerts**, AUDIT logging, **polling timeout fix** | 9/9 |
| `ActiveProtection.cs` | 303 | P8 | DD мониторинг: Yellow/Red/Emergency, timezone-aware | 9/9 |
| `mt5_worker.py` | ~870 | P9.M | MT5 commands, order_calc_margin, auto-reconnect, CALC_LEVERAGE, **CHECK_SYMBOLS (alias table)**, **CALC_PROFIT (OrderCalcProfit)** | live-тест |
| `Protocol.cs` | 280 | P6 | Все типы сообщений протокола | 31/31 |
| `BarsCache.cs` | 188 | P6 | In-memory кэш баров, warmup, new candle detect | 17/17 |
| `VirtualTracker.cs` | ~330 | P9.M | **NEW**: SL/TP monitor, gap execution, P&L calc, trade snapshots, 3SL guard, **effective leverage for margin** | via engine test |
| `Scheduler.cs` | ~905 | P9.M | Candle detect -> TICK -> ACTIONS -> per-symbol sizing (RiskFactor) -> Risk -> Execute, **virtual ENTER/EXIT/MODIFY paths**, spread+slippage model, **CalcProfitAsync → loss1Lot**, **effective leverage for virtual margin** | via TCP test |
| `StrategyProcess.cs` | 400 | P6 | TCP listener + Python process lifecycle | via TCP test |
| `StrategyManager.cs` | ~350 | P9 | **Auto-discovery** (30s timer), enable/disable, magic registry, crash recovery | via TCP test |
| `runner.py` | ~290 | P9 | Python runner + config "strategy" validation | TCP handshake |
| `ConnectorManager.cs` | ~380 | P9.M | + **RestartWorkerAsync**, OnTerminalStatusChanged, GetEnabledTerminalIds, **CheckSymbolsAsync**, **CalcProfitAsync** | live-тест |
| `DashboardServer.cs` | ~3185 | P9.M | HTTP + WS, **~43 команд**, strategy enable/disable, terminal management, Sizing tab (multi-format init), leverage detect, **CHECK_SYMBOLS**, **Virtual Trading UI**, news always-on + minImpact filter, toggle_no_trade, **trade chart (digits/drag/bars/PnL)**, **WMI discovery**, **probe_terminal**, **open_strategy_folder**, **virtual PnL cache**, **leverage persistence**, **effective leverage for virtual margin** | live-тест |
| `index.html` | ~1925 | P9.P | SPA, strategy enable/disable, **drag & drop** (hamburger handles), delete, strategy pills, connecting state, **resizable columns**, **symbol availability**, **Virtual Tab**, RiskFactor slider, news always-on + filter, Trading Hours toggle, **Trade Chart (centered, draggable, digits, live PnL, closed bars)**, HeartBeat filter, **manual terminal probe**, **strategy folder open**, **closed trade chart buttons** | live-тест |
| `DaemonConfig.cs` | ~176 | P9.V | Terminals (+ Enabled, SortOrder), strategies, paths, **VirtualSlippagePts, CommissionPerLot** | -- |
| `Program.cs` | ~1600 | P9.V | Engine loop + crash recovery + BarsCache->Dashboard, **VirtualTracker.TickAsync**, **equity snapshots 5min** | -- |
| `Models.cs` | ~130 | P9 | Position, AccountInfo, Bar, Deal, InstrumentCard + Margin1Lot | -- |
| `probe_terminal.py` | 55 | P7 | Probe running MT5 terminals (no password needed) | live-тест |
| `strategy.py` (signal_test) | ~326 | P9.V | RSI mean-reversion, trailing SL, combos format | -- |

### БУДУТ ИЗМЕНЯТЬСЯ

| Файл | Текущая роль | Что изменится |
|------|-------------|---------------|
| `DaemonConfig.cs` | Terminals (+ Enabled, SortOrder, Virtual), strategies, paths | **Phase 9.5**: ENV: references |
| `config.json` (daemon) | Terminals + strategies + auto-persist | **Phase 9.5**: ENV: references |

---

## 4. Known Bugs

Все критические баги исправлены:

| # | Баг | Статус | Решение |
|---|-----|--------|---------|
| BUG-1 | Shutdown не завершает процесс | Fixed | OnShutdownRequested -> cts.Cancel() |
| BUG-2 | Воркер не reconnect к MT5 | Fixed | Auto-reconnect после 3 heartbeat errors |
| BUG-3 | Воркеры не убиваются | Fixed | Fresh 15s cleanup token, корректный StopAsync |
| BUG-4 | Heartbeat показывает 3/3 при disconnected | Fixed | GetAccountInfoAsync вместо TcpClient.Connected |
| BUG-5 | Start Terminal не оживляет плитку | Fixed | RestartWorkerAsync через 12с после запуска MT5 |
| BUG-6 | Telegram polling stopped | Fixed | Retry on HttpClient timeout, break only on real cancellation |
| BUG-7 | Close button не закрывает virtual position | Fixed | `if (ticket < 0)` branch в HandleClosePosition |
| BUG-8 | Close All не закрывает виртуальные позиции | Fixed | GetOpenVirtualPositions loop |
| BUG-9 | "View positions & stats" чёрный экран | Fixed | Null-safe DB reads (IsDBNull checks) |
| BUG-10 | P&L = -$1,376,500 для ETHUSD | Fixed | Crypto/index detection (contractSize=1) |
| BUG-11 | Reset Virtual ставит pnl=0 | Fixed | Корректный P&L calculation at close |
| BUG-12 | Encoding mojibake (em dash, bullet, arrows) | Fixed | 15x em dash, 5x bullet, 1x arrow + heartbeat + trash |
| BUG-13 | V.Balance не появляется при mode switch | Fixed | Auto-init в HandleSetMode |
| BUG-14 | VirtualTracker: _symbolCache не существует | Fixed | Added Dictionary, populated in async path |
| BUG-15 | DashboardServer: GetSpread() не существует | Fixed | Inline spread estimation (JPY: 0.03, others: 0.00020) |
| BUG-16 | G2/G3 DD gates используют реальный баланс в virtual | Fixed | Тернарник: virtual -> GetVirtualBalance, иначе acc.Balance |

---

## 5. Dashboard: все команды

| Команда | Тип | Описание |
|---------|-----|----------|
| `get_terminals` | Query | Снапшот всех терминалов (4-state status, account, DD, guards, profile, **enabled, sortOrder, virtual balance/equity**) |
| `get_positions` | Query | Открытые позиции всех терминалов из MT5 + source из DB + **virtual positions** |
| `get_strategies` | Query | Discovered + running стратегии (enabled, magic_base) |
| `get_events` | Query | Лог событий с фильтрами (type, terminal, limit) |
| `get_terminal_detail` | Query | Закрытые позиции, статистика, equity curve |
| `get_sizing` | Query | Sizing таблица с live ATR, Est.SL, Est.Lot, Margin, **availability** |
| `get_virtual_equity` | Query | **NEW**: Equity snapshots для Virtual Tab chart |
| `get_trade_chart` | Query | Live/snapshot trade chart (**digits precision, closed bars from BarsCache**, SL trail, TP) |
| `get_trade_snapshot` | Query | Cached trade data (bars + SL trail) для Trade Chart modal |
| `probe_terminal` | Discovery | **NEW**: Manual terminal probe by path → account info → Add |
| `open_strategy_folder` | Action | **NEW**: Opens strategy folder in Explorer |
| `start_strategy` | Action | Запуск стратегии на терминале |
| `stop_strategy` | Action | Остановка стратегии |
| `enable_strategy` | Action | Включение стратегии (enabled -> state.db) |
| `disable_strategy` | Action | Выключение стратегии + остановка instances |
| `reload_strategy` | Action | Hot-reload стратегии (stop -> restart Python process) |
| `close_position` | Action | Закрытие одной позиции **(+ virtual branch)** |
| `close_all` | Action | Закрытие всех позиций терминала **(+ virtual positions)** |
| `emergency_close_all` | Action | Закрытие всего + стоп стратегий + monitor mode |
| `start_terminal` | Action | Запуск MT5 + **автоматический restart worker** |
| `toggle_terminal_enabled` | Action | Включение/выключение терминала (worker stop/start) |
| `delete_terminal` | Action | Удаление терминала + полная очистка DB (9 таблиц) |
| `reorder_terminals` | Config | Drag & drop -- сохранение порядка терминалов |
| `shutdown_daemon` | Action | Остановка демона + опционально kill terminals + .clean_shutdown |
| `save_profile` | Config | Обновление risk settings (включая serverTimezone, no_trade window) |
| `save_sizing` | Config | Обновление per-symbol risk%, enabled, max_lot |
| `init_sizing` | Config | Инициализация sizing из strategy config (unified/pairs/symbols) |
| `detect_leverage` | Query | Auto-detect leverage per class (FX/IDX/XAU/OIL/CRYP) |
| `unblock_3sl` | Config | Сброс 3SL guard |
| `toggle_news_guard` | Config | Вкл/выкл News Guard |
| `toggle_no_trade` | Config | **NEW**: Вкл/выкл Trading Hours guard (сохраняет часы) |
| `set_mode` | Config | auto / semi / monitor / **virtual** |
| `reset_virtual` | Action | **NEW**: Сброс виртуального баланса + удаление всех virtual данных |
| `export_virtual_csv` | Query | **NEW**: Экспорт виртуальных сделок в CSV |
| `discover_terminals` | Discovery | Сканирование запущенных MT5 процессов |
| `add_discovered_terminal` | Discovery | Hot-add + PersistConfig + DetectTimezone |
| `ping` | Util | Heartbeat |

Push events: `terminal_status`, `terminal_deleted`, `position_closed`, `strategy_status`, `log_entry`, `risk_alert`, `emergency_close_all`, `terminal_added`, `shutdown`. Cmd responses: `enable_strategy`, `disable_strategy` (trigger client re-fetch).

**Итого: ~43 команд** (было ~40)

---

## 6. Архитектура -- 4 слоя

```
+-----------------------------------------------------+
|  DASHBOARD (Phase 7-9.V)                             |
|  index.html -> WebSocket -> DashboardServer.cs       |
|  ~40 команд, 6 tabs (Terminals, Positions,           |
|  Strategies, Sizing, Log, Virtual),                   |
|  Guards panel, Terminal management,                   |
|  Strategy indicators, Resizable columns,              |
|  Symbol availability, Virtual Trading UI,             |
|  Trade Chart modal (TMM-style, auto-refresh)          |
+------------------+----------------------------------+
                   | WebSocket
+------------------v----------------------------------+
|  ENGINE (Phases 3-6, 8-9.V)                         |
|  Program.cs -> Scheduler -> StrategyManager(auto)    |
|  StateManager (SQLite, server_timezone,              |
|    symbol_sizing with asset_class,                   |
|    virtual_balance/equity/snapshots)                 |
|  RiskManager (12 gates, timezone-aware DD,           |
|    virtual mode support)                             |
|  VirtualTracker (SL/TP monitor, gap execution)       |
|  NewsService (auto-fetch) + AlertService (TG)        |
|  ActiveProtection + BarsCache + LotCalculator        |
|  Crash recovery + clean shutdown flag                |
|  Per-symbol sizing -> Scheduler ENTER flow           |
+----------+-------------------+----------------------+
           | TCP               | TCP
+----------v------+  +--------v----------------------+
|  CONNECTOR      |  |  STRATEGY PROTOCOL            |
|  (Phases 1-2)   |  |  runner.py -> strategy.py     |
|  ConnectorMgr   |  |  HELLO/ACK/TICK/ACTIONS       |
|  WorkerProcess  |  |  compression_breakout          |
|  SymbolMapper   |  |  pairs_zscore, vwap            |
|  + RestartWorker|  |  fx_intraday, signal_test      |
|  + Disconnect   |  |  (all unified schema v1.1)     |
|    alerts       |  |                               |
|  + CheckSymbols |  |                               |
+----------+------+  +-------------------------------+
           | TCP (port per terminal)
+----------v------+
|  MT5 WORKER     |
|  mt5_worker.py   |
|  (один на каждый |
|   терминал)      |
|  multi-symbol    |
|  server_time     |
|  order_calc_     |
|    margin        |
|  auto-reconnect  |
|  CHECK_SYMBOLS   |
|  (alias table)   |
+----------+------+
           | MT5 Python API
+----------v------+
|  MT5 Terminal    |
|  (The5ers,       |
|   RoboForex,     |
|   AudaCity)      |
+-----------------+

Внешние сервисы:
  -> Telegram Bot API (алерты, сигналы, команды, heartbeat, disconnect alerts)
  -> ForexFactory API (новостной календарь, каждые 12ч)
  -> Windows Task Scheduler (watchdog, каждые 2 мин)
```

---

## 7. 12 Risk Gates

```
Gate  1: Mode Check             -- блокирует ENTER в режиме monitor, virtual => Pass()
Gate  2: Daily Drawdown         -- лимит дневного DD (timezone-aware, virtual_balance в virtual mode)
Gate  3: Max Cumulative DD      -- лимит кумулятивного DD (virtual_balance в virtual mode)
Gate  4: Max Risk Per Trade     -- лимит суммы риска на трейд
Gate  5: Margin Per Trade       -- маржа на трейд
Gate  6: Deposit Load           -- общая загрузка депозита (acc.Margin + virtual_margin)
Gate  7: News Guard             -- блок +-N мин вокруг High-impact новостей (minImpact from profile)
Gate  8: 3SL Guard              -- стоп торговли после 3 SL подряд за день (virtual SL тоже считается)
Gate  9: Netting Check          -- hedge vs netting валидация
Gate 10: Trading Hours          -- no-trade window по серверному времени (DST-aware)
Gate 11: Same Symbol/Strategy   -- запрет дублирования позиции по символу внутри стратегии (по magic)
Gate 12: R-cap                  -- daily R-budget per strategy (R-пространство, не доллары)
```

**Pre-gate**: Symbol Sizing -- если symbol disabled в sizing -> ENTER блокируется до risk gates.

---

## 8. Position Sizing -- ENTER flow

```
Strategy -> ENTER LONG GBPCAD SL=1.2345
  |
  +- 1. Get InstrumentCard (symbol_info + margin_1lot via order_calc_margin)
  +- 2. Get AccountInfo (balance, equity, margin)
  +- 3. Check symbol_sizing:
  |     +- Disabled? -> BLOCK + Telegram alert
  |     +- risk_pct from sizing (not profile.MaxRiskTrade)
  +- 4. riskMoney = balance x risk_pct%
  +- 5. LotCalculator.Calculate(entry, sl, riskMoney, card)
  +- 6. Apply max_lot cap if set
  +- 7. RiskManager.CheckAsync (12 gates)
  +- 8a. Real mode: SendOrder to MT5
  +- 8b. Virtual mode: HandleVirtualEnterAsync (spread + slippage + negative ticket + DB)
  +- 9. Log + Telegram (VIRTUAL prefix in virtual mode)
```

---

## 9. Тесты -- сводка

| Команда | Фаза | Тестов | MT5 нужен? | Что проверяет |
|---------|:----:|-------:|:----------:|---------------|
| `--test-state` | P3+P8 | 34/34 | Нет | SQLite CRUD, профили (server_timezone), позиции, 3SL, daily P/L |
| `--test-risk` | P5+P9 | 69/69+ | Нет | LotCalc, 12 gates, NewsService, AlertService, ActiveProtection |
| `--test-protocol` | P6 | 80/80 | Нет | Protocol serde, BarsCache, TCP handshake, DaemonConfig |
| `--test-telegram` | P5 | 2 | Нет | Telegram Bot API connectivity |
| `--test-recon` | P4 | live | **Да** | Reconciliation DB <-> MT5 |
| `dotnet run` (default) | P9 | live | **Да** | Engine loop + dashboard on :8080 |

**Итого без MT5**: 183+ unit-тестов, все проходят

---

## 10. Engine Loop -- что делает `dotnet run`

При запуске без флагов Program.cs выполняет полный Engine loop:

1. Проверяет `.clean_shutdown` флаг -> определяет: штатный старт или crash recovery
2. Загружает `config.json` (включая hot-added терминалы, **enabled/sortOrder**)
3. Инициализирует StateManager (SQLite, server_timezone, symbol_sizing, **virtual tables**)
4. Запускает ConnectorManager (MT5 workers, **skip disabled terminals**)
5. Создаёт профили терминалов: auto-detect timezone из server_time, mode=**monitor**
6. Создаёт все сервисы: NewsService (auto-fetch), AlertService (**+ disconnect subscription + polling fix**), RiskManager (**12 gates, virtual support**), BarsCache, Reconciler (**virtual filter**), ActiveProtection, **VirtualTracker**
7. StrategyManager: auto-discovery (30s timer) сканирует `strategies/`, регистрирует в state.db
8. **Crash recovery**: если нет clean shutdown флага -> auto-start терминалов -> ожидание 60с -> восстановление стратегий из `active_strategies` -> dashboard broadcast
9. **DashboardServer** стартует HTTP + WebSocket на :8080 (с BarsCache для sizing preview, **CHECK_SYMBOLS для availability**, **Virtual Trading UI**)
10. Отправляет Telegram алерт о старте

**Основной цикл** (каждые `scheduler_interval_sec` секунд):

- **Scheduler** -> проверяет новые свечи -> TICK стратегиям -> получает ACTIONS -> **Symbol sizing check** -> Risk check (12 gates) -> Execute **(real or virtual)**
- **VirtualTracker** -> TickAsync: проверяет SL/TP для виртуальных позиций из BarsCache
- **Reconciler** -> каждые 60с сверяет DB <-> MT5 **(skip virtual)**
- **ActiveProtection** -> каждые 15с проверяет DD лимиты (timezone-aware broker date)
- **NewsService** -> авто-обновление календаря каждые 12ч
- **Equity snapshots** -> каждые 5 мин сохраняет виртуальный equity в DB
- **Dashboard broadcast** -> terminal_status push (4-state: connected/error/disconnected/**connecting**)
- **Telegram** -> SIGNAL или BLOCKED при каждом торговом сигнале **(VIRTUAL prefix)**
- **Telegram** -> DISCONNECTED / reconnected при изменении состояния терминала

**Режим virtual** (новый): Gate 1 пропускает ENTER. Scheduler маршрутизирует в HandleVirtualEnterAsync. VirtualTracker мониторит SL/TP. P&L считается точно. Equity отображается в Virtual Tab. Реальные ордера НЕ размещаются.

---

## 11. Конфигурация -- ключевые файлы данных

| Файл | Где | Роль |
|------|-----|------|
| `config.json` (daemon) | daemon/ | Терминалы (+ enabled, sort_order), symbol map, Telegram, dashboard. **strategies: []** -- auto-discovery |
| `config.json` (strategies) | strategies/*/  | Unified schema v1.1: combos, directions, strat/daemon blocks |
| `STRATEGY_CONFIG_SCHEMA.md` | root | Unified config schema documentation |
| `news_calendar.json` | daemon/ | Экономический календарь (авто-фетчер ForexFactory, 12ч) |
| `state.db` | daemon/Data/ | SQLite: позиции (+is_virtual, timeframe), профили (+virtual_balance/margin/commission/daily_dd_mode), events, daily P/L, strategy state, active_strategies, symbol_sizing (+tier), **strategy_registry**, **virtual_equity**, **sl_history**, **trade_snapshots** |
| `.clean_shutdown` | daemon/Data/ | Флаг штатного завершения для crash recovery |

---

## 12. Roadmap -- что дальше

```
Phase 0: Подготовка среды              -- done
Phase 1: MT5 Worker                    -- done (550L Python)
Phase 2: Connector Manager             -- done (583L C#)
Phase 3: State Manager + SQLite        -- done (597L, 34 теста)
Phase 4: Reconciliation                -- done (278L, live тест)
Phase 5: Risk Manager                  -- done (1332L, 69 тестов)
Phase 6: Strategy Protocol + Runner    -- done (2853L, 80 тестов)
Phase 7: Dashboard                     -- done (~1935L)
Phase 8: Timezone + Persist + Strategy  -- done (~1400L новых, 52 теста; bb_mr_v2 удалена)

Phase 9: Боевое тестирование (В ПРОЦЕССЕ)
   [done] News Calendar Fetcher               -- ForexFactory API, авто-обновление 12ч
   [done] Dashboard guard settings             -- 3SL/News toggles на панели
   [done] Start terminals from dashboard       -- кнопка Start Terminal + worker restart
   [done] Shutdown from dashboard              -- SHUTDOWN + kill terminals + VBS launcher
   [done] Trading Hours gate                   -- Gate 10, no-trade window, midnight crossing
   [done] Telegram signal alerts               -- SIGNAL + BLOCKED в Telegram
   [done] Signal dedup в strategy.py           -- один сигнал на свечу
   [done] Crash recovery                       -- .clean_shutdown flag + watchdog + auto-start
   [done] Clean start detection                -- штатный shutdown -> терминалы НЕ стартуют
   [done] BUG-1/3: Shutdown + worker cleanup   -- OnShutdownRequested -> cts.Cancel(), fresh 15s token
   [done] BUG-2: MT5 reconnect                 -- auto-reconnect после 3 heartbeat errors
   [done] Strategy display fix                 -- crash recovery -> видны в dashboard
   [done] Config self-documentation            -- "strategy" field + валидация
   [done] Watchdog hidden window               -- VBS launch, нет чёрного окна
   [done] Position Sizing Management           -- symbol_sizing, asset_class, order_calc_margin,
                                                 dashboard Sizing tab, filter/bulk/scale
   [done] Sticky headers                       -- все таблицы: Positions, Sizing, Log
   [done] Auto-backup state.db                 -- ежедневно, 14 дней ротация
   [done] Strategy hot-reload                  -- Reload кнопка в dashboard
   [done] Audit log                            -- type:AUDIT в events table, все действия оператора
   [done] Leverage per class                   -- auto-detect FX/IDX/XAU/OIL/CRYP, auto-discovery,
                                                 account_leverage для FX, order_calc_margin для CFD
   [done] Telegram commands (Phase 1)          -- /status, /positions, /pnl, /news, /help
   [done] Telegram heartbeat                   -- периодический статус-отчёт, сброс при сигналах
   [done] Terminal management                  -- enable/disable, delete + DB cleanup, drag & drop reorder
   [done] Heartbeat bug fix                    -- GetAccountInfoAsync вместо TcpClient.Connected
   [done] Disconnect alerts                    -- DISCONNECTED / reconnected в Telegram мгновенно
   [done] Worker restart on Start Terminal     -- автоматический RestartWorkerAsync через 12с
   [done] Strategy indicators on tiles         -- фиолетовые пилюли с именем + magic на плитках
   [done] Gate 11: Same Symbol Per Strategy    -- проверка по magic, разные стратегии = независимы
   [done] Strategy Auto-Discovery              -- timer scan 30s, enable/disable в dashboard,
                                                 magic registry в state.db, config.json пустой
   [done] CHECK_SYMBOLS                        -- terminal-native symbol availability (alias table,
                                                 suffix variations, single mt5.symbols_get() call)
   [done] BUG-6: Telegram polling timeout      -- retry on HttpClient timeout, break only on cancellation
   [done] Multi-format Sizing init             -- unified (directions), symbols (legacy), pairs_zscore
   [done] Resizable columns                    -- drag-resize в Sizing/Positions/Log
   [done] Symbol availability indicators       -- в Sizing, CHECK_SYMBOLS independent of strategy
   --- Phase 9.V: Virtual Trading ---
   [done] VirtualTracker.cs                    -- SL/TP monitor, gap execution, P&L, snapshots
   [done] Virtual ENTER/EXIT/MODIFY_SL paths   -- Scheduler + spread + slippage + commission
   [done] Virtual balance/margin management    -- StateManager CRUD + auto-init + reset
   [done] G2/G3 virtual balance integration    -- TradeRequest.AccountBalance тернарник
   [done] G6 virtual margin integration        -- acc.Margin + virtual_margin
   [done] Reconciler virtual filter            -- .Where(!IsVirtual)
   [done] Equity snapshots (5min timer)        -- virtual_equity table + Program.cs timer
   [done] Dashboard Virtual Tab                -- equity chart, statistics, reset, export CSV
   [done] Dashboard Trade Chart modal          -- TMM-style: candlestick + SL trail + P&L zone
   [done] Virtual mode in dropdown             -- auto/semi/monitor/virtual
   [done] Purple V.Balance/V.Equity panel      -- [V] badge, All/Real/Virtual filter
   [done] Telegram VIRTUAL alerts              -- purple prefix
   [done] signal_test_strategy                 -- RSI mean-reversion, combos format
   [done] 10 bug fixes (BUG-7 through BUG-16) -- crypto P&L, encoding, close handlers, etc.
   --- Phase 9.D: Dashboard Polish (done) ---
   [done] Risk Factor refactor               -- absolute % -> multiplier 0.0-1.0, slider UI
   [done] Virtual P&L fixes                  -- InstrumentCard pre-cache, MaxDD display,
                                                equity sanity checks, ResetVirtualTrading purge
   [done] News display always-on             -- red events visible even with guard OFF
   [done] News importance filter             -- Low/Medium/High selector in settings
   [done] Trading Hours toggle               -- clickable ON/OFF on tile (like News/3SL guards)
   [done] Trade Chart modal rewrite          -- 250 bars, TP line, SL step-line, auto-refresh 10s,
                                                entry/exit markers, grid footer, Escape,
                                                centered+draggable, digits precision, live PnL,
                                                closed trade bars (enhanced in 9.P)
   [done] HeartBeat filtering                -- hidden from All/AUDIT, dedicated HB button
   --- Phase 9.S: Schema Unification + Daemon Patches (done) ---
   [done] Unified Config Schema v1.1         -- STRATEGY_CONFIG_SCHEMA.md, directions/strat/daemon
   [done] 5 strategy migrations              -- compression_breakout, pairs_zscore, vwap,
                                                signal_test, fx_intraday → unified schema
   [done] Init vs Reset Sizing               -- Init (full load) vs Reset (risk_factor only),
                                                ParseStrategyConfigForSizing shared helper
   [done] Sizing: Tier column + filter       -- T1/T2/T3 color-coded, filter buttons, All resets both
   [done] TP Price support                   -- Protocol TpPrice field, Scheduler pass-through
   [done] G2 Soft/Hard Daily DD              -- soft=realized-only latch, hard=realized+unrealized+force-close,
                                                DailyDdMode in profile, UI toggle badge
   [done] Warmup Gate                        -- suppress ENTER on first tick after start/restart,
                                                StrategyProcess.WarmupDone, EXIT/MODIFY_SL pass through
   --- Phase 9.R: R-cap Gate + momentum_cont strategy (done) ---
   [done] Gate 12: R-cap                       -- daily R-budget per strategy, independent of dollar-space G2.
                                                   R-result: TP→+tp_r, SL→-1.0R, SL+protector→lock_r.
                                                   RCalc.cs (shared calculator), daily_r table accumulator,
                                                   protector_fired tracking on MODIFY_SL,
                                                   tri-state dashboard control (auto/ON/OFF, sentinel -1)
   [done] R-cap data flow                      -- strategy config params.r_cap → get_requirements() →
                                                   HELLO requirements.r_cap → StrategyRequirements.RCap →
                                                   StrategyProcess.RCap (assignment override ?? requirements) →
                                                   TradeRequest.RCap → Gate 12.
                                                   Dashboard override: profile.RCapOn + RCapLimit.
                                                   Extended in 9.O: 3-level rCapConfigDefault fallback
                                                   (HELLO → assignment → config.json cache)
   [done] R-cap dashboard UI                   -- Settings: ON/OFF toggle + value input (placeholder from config),
                                                   DD progress bar: RCap indicator (green=active, gray=disabled),
                                                   Settings grid reorder (Mode→left column, DailyDD+RCap→right)
   [done] momentum_cont strategy               -- MTF Momentum Continuation (HTF impulse + LTF RSI pullback),
                                                   20 forex symbols, T1/T2 tier-based sizing,
                                                   protector management, r_cap=1.5, signal_data with
                                                   tp_r + protector_lock_r for RCalc integration
   [done] STRATEGY_CONFIG_SCHEMA v1.1          -- r_cap in params, protector fields in strat reference,
                                                   momentum_cont template, daemon/strategy extraction lists
   --- Phase 9.P: Dashboard Polish II (done, 23.02.2026) ---
   [done] News impact filtering fix            -- IsBlocked(minImpact) for gate, IsBlockedGlobal for tile
   [done] Drag handles restricted              -- hamburger icon only (terminals + strategies)
   [done] Strategy folder open buttons         -- 📂 button → explorer.exe /select,config.json
   [done] Virtual unrealized P/L caching       -- _virtualUnrealPnlCache with 5s TTL
   [done] Trade Chart: centered + draggable    -- items-center justify-center, mousedown drag on header
   [done] Trade Chart: price precision         -- digits from InstrumentCard, priceFormat on candle+SL series
   [done] Trade Chart: live PnL                -- GetSymbolInfoAsync + CacheSymbol before P/L calc
   [done] Trade Chart: closed trade bars       -- BarsCache fetch for closed positions, timeframe from DB
   [done] Closed trade chart buttons           -- 📊 on Recent Closed rows
   [done] Terminal discovery WMI fallback      -- multi-name scan, wmic process for AccessDenied paths
   [done] Manual terminal probe + add          -- probe_terminal command, path validation, UI with Probe button
   [done] RunProbeAsync improvements           -- detailed errors, alt path search, 15s timeout, logging
   --- Phase 9.O: Dashboard Optimization (done, 24.02.2026) ---
   [done] R-cap immediate extraction           -- StrategyManager parses params.r_cap from strategy
                                                   config.json during auto-discovery (30s scan),
                                                   _configRCapCache dictionary, GetEffectiveRCapForTerminal()
                                                   3-level fallback: HELLO → assignment → config.json cache.
                                                   R-cap visible in Settings even with strategy stopped.
   [done] HandleGetTerminals async             -- removed .Result blocking on GetAccountInfoAsync,
                                                   HandleGetTerminalsAsync + await, thread pool unblocked
   [done] HandleGetPositions .Result fix       -- await GetPositionsAsync instead of .Result
   [done] Batch profile loading                -- GetAllProfiles().ToDictionary() instead of N × GetProfile(),
                                                   1 DB query instead of N per poll cycle (10s)
   [done] Virtual balance dupe elimination     -- profile.VirtualBalance directly instead of
                                                   GetVirtualBalance() (which called GetProfile again 2×)
   [done] News pre-computation                 -- GetAllEvents() + DateTime.UtcNow computed once per poll,
                                                   shared across all terminals instead of per-terminal
   --- Testing (current) ---
   [ ] Virtual Trading на live данных -> набор статистики 1-2 недели
   [ ] Сравнить virtual P&L с бэктестом
   [ ] Full Auto на демо -> execution quality
   [ ] Execution quality analysis -> slippage, WR, Exp vs backtest

Phase 9.5: Security + VPS
   [ ] Mutex guard (один инстанс)
   [ ] SecretResolver (ENV: prefix)
   [ ] .env файл для секретов
   [ ] Self-contained publish
   [ ] WinExe (без консоли)
   [ ] WireGuard VPN + dashboard auth

Phase 10+: После стабильной торговли
   [ ] OKX криптобиржа                     -- sizing уже поддерживает crypto asset_class
   [ ] Replay Worker (офлайн бэктест через демон)
   [ ] Desktop App (WebView2)
   [ ] Virtual desktops + скрытый MT5
   [ ] P/L per strategy (фильтр по magic)
   [ ] Import/Export sizing между терминалами
```

**TOTAL**: ~18,500+ строк кода, 183+ тестов

---

## 13. На что обратить внимание при дальнейшей работе

### Порядок перехода к live торговле

1. **Phase 9 infrastructure** -- сделано (все пункты выше)
2. **Phase 9.V Virtual Trading** -- сделано (полная реализация)
3. **Phase 9.S Schema Unification** -- сделано (5 стратегий мигрированы, sizing tier/init/reset, G2 soft/hard, warmup)
4. **Phase 9.R R-cap Gate** -- сделано (Gate 12, RCalc, daily_r, dashboard tri-state, momentum_cont strategy)
5. **Phase 9.P Dashboard Polish II** -- сделано (trade chart 5 fixes, discovery WMI+probe, drag handles, news filtering, strategy folders)
6. **Phase 9.O Dashboard Optimization** -- сделано (r_cap immediate extraction, async get_terminals, batch profiles, dupe elimination, news pre-computation)
7. **Virtual Testing на live данных** -- набрать статистику 1-2 недели (ТЕКУЩИЙ ЭТАП)
8. **Сравнить virtual P&L с бэктестом** -- совпадают ли entry points, правильно ли работают параметры
9. **Full Auto на демо-счёте** -- первые реальные ордера, проверка execution quality
10. **Phase 9.5** -- security перед VPS деплоем
11. **Full Auto на prop** -- финальный шаг

### Параллельная работа -- стратегии

- **momentum_cont** -- 20 forex symbols, HTF impulse + LTF RSI pullback, T1/T2 tiers, r_cap=1.5, protector
- **compression_breakout** -- 6 индексов, unified schema v1.1, BOTH direction
- **pairs_zscore** -- SL оптимизирован (initial_sl_atr: 2.0->8.0), unified schema v1.1
- **vwap** -- 30 symbols × LONG+SHORT, per-direction params, unified schema v1.1
- **fx_intraday** -- 12 RSI+DVWAP combos, tp_price support, unified schema v1.1
- **signal_test_strategy** -- тестовая для валидации virtual trading, unified schema v1.1

### Архитектурные заметки

**Разделение ответственности** остаётся ключевым: Python стратегии дают только сигналы (ENTER/EXIT/MODIFY_SL), C# демон делает всё остальное -- расчёт лотов (per-symbol sizing), 12 проверок риска, исполнение (real или virtual), мониторинг, алерты, сверку. Стратегия не знает про лоты, маржу, DD лимиты, новости, trading hours, sizing.

**Unified Config Schema** -- все стратегии используют единую структуру конфига (STRATEGY_CONFIG_SCHEMA.md v1.1). Каждый combo: `directions → {LONG/SHORT/BOTH} → {strat: {python params}, daemon: {size_r, role}}`. Плюс combo-level: `sym`, `aclass`, `tier`. Глобальные params -- в блоке `params` (включая `r_cap`). Python читает только `strat`, daemon читает только `daemon` + `r_cap`. Sizing workflow: backtest → config size_r → Init button → DB risk_factor → Scheduler.

**R-cap (Gate 12)** -- daily R-budget per strategy, работает в R-пространстве (не в долларах). Решает проблему: когда margin gate уменьшает лот 5→1, долларовый G2 DD позволяет 7.5 сделок вместо 1.5 из бэктеста. R-cap считает результат в R: TP→+tp_r, SL→-1.0R, SL+protector→lock_r. Когда sum ≤ -r_cap -- блокирует вход. R-cap data flow: 3-level fallback для rCapConfigDefault: (1) HELLO requirements (стратегия Running), (2) assignment override (daemon config.json), (3) config.json cache (StrategyManager парсит params.r_cap при auto-discovery, доступен всегда). Dashboard может override/отключить (tri-state: auto/ON/OFF).

**Position Sizing** -- стратегия отвечает за КОГДА торговать (сигналы). Демон отвечает за СКОЛЬКО (sizing). RiskFactor мультипликатор (0.0-1.0) масштабирует eff_risk_pct из стратегии -- 1.0 = полный риск, 0.5 = половина. `size_r` из config (бэктест) → `risk_factor` в DB (Init). Reset обновляет только risk_factor, сохраняя enabled/notes/max_lot.

**G2 Daily DD -- Soft vs Hard** -- два режима для разных prop firm правил. Hard (default): realized + unrealized, force-close при 100%. Soft: только realized, блокировка новых входов без закрытия -- позиции доживают до SL/TP. Prop firms часто считают daily DD по realized only.

**Virtual Trading** -- полный цикл от сигнала до P&L без реальных ордеров. Стратегия не знает что работает в virtual mode -- все пути маршрутизируются в Scheduler. VirtualTracker мониторит SL/TP из BarsCache, gap execution заполняет по Open если бар открывается за уровнем. Trade Chart modal показывает каждую сделку с полной визуализацией: candlestick с правильной ценовой точностью (digits из InstrumentCard), entry/exit маркеры, SL trail step-line, TP/SL price lines, live P&L в заголовке. Модалка центрирована и перетаскиваемая. Закрытые сделки тоже показывают бары (из BarsCache).

**Multi-strategy ready** -- Gate 11, magic numbers, strategy indicators, reconciler magic filtering, dashboard pills, **auto-discovery** (положи папку -> появится в dashboard -> Enable -> Start) -- всё готово для запуска второй стратегии.

**Crash recovery** протестирована: watchdog -> VBS hidden launch -> auto-start терминалов -> восстановление стратегий -> dashboard broadcast. Все баги в цепочке исправлены.

**Telegram** стал основным каналом мониторинга -- сигналы, блокировки, crash recovery, disconnect alerts, heartbeat, commands, **VIRTUAL alerts**. Dashboard дополняет, но Telegram доступен всегда с телефона.

**Symbol availability** определяется terminal-native через CHECK_SYMBOLS -- независимо от запущенных стратегий или загруженных баров. Alias table покрывает 16 канонических символов с вариантами брокеров + suffix variations для нестандартных именований.

---

## 14. Принцип разделения ответственности

**Python стратегии** дают только сигналы: ENTER (symbol, direction, sl_price, tp_price?), EXIT (ticket), MODIFY_SL (ticket, new_sl).

**C# daemon** делает всё остальное:

- Per-symbol sizing check (symbol_sizing -> enabled, risk_factor, tier)
- Расчёт лота (LotCalculator, dual-mode: MT5 CalcProfit preferred / tick math fallback, per-symbol riskMoney)
- 12 проверок риска (RiskManager, timezone-aware, Gate 2 soft/hard DD, Gate 5/6 effective leverage, Gate 11 per-strategy, Gate 12 R-cap, virtual support)
- R-cap tracking (RCalc → daily_r accumulator, protector_fired flag, tri-state dashboard override)
- Effective leverage per asset class (CALC_LEVERAGE → SQLite persistence, conservative defaults, 3-level fallback)
- Warmup gate (suppress ENTER on first tick after start -- prevent stale-data signals)
- Исполнение ордеров -- real (ConnectorManager -> WorkerProcess -> MT5, TP pass-through) или **virtual** (HandleVirtualEnterAsync -> DB)
- **VirtualTracker** -- SL/TP мониторинг для виртуальных позиций (gap execution, trade snapshots)
- Запись состояния (StateManager, включая symbol_sizing + tier, virtual_balance/equity/snapshots, daily_dd_mode, daily_r, r_cap_on/limit)
- Мониторинг DD (ActiveProtection, timezone-aware, soft/hard mode)
- Алерты (AlertService -> Telegram: сигналы, disconnect alerts, heartbeat, commands, **VIRTUAL prefix**)
- Сверка позиций (Reconciler, **skip virtual**, R-calc on close)
- Кэш баров + прогрев индикаторов (BarsCache)
- Dispatch свечей стратегиям (Scheduler, **virtual routing**, protector_fired tracking)
- Web-панель управления (DashboardServer, ~43 команд, terminal management, **Virtual Trading UI**, init/reset sizing, R-cap settings, **trade chart with digits/drag/bars**, **async non-blocking polling**, **batch profile loading**)
- Auto-discovery MT5 терминалов (**WMI fallback**, **manual probe by path**)
- Автодетект timezone из MT5 server_time
- Автодетект leverage per class из MT5 order_calc_margin (persisted в SQLite, conservative defaults)
- Авто-фетч новостного календаря (ForexFactory)
- Crash recovery + watchdog
- Запуск/остановка/restart MT5 терминалов и воркеров
- Auto-discovery стратегий (scan -> register -> enable/disable -> magic allocation -> **r_cap config cache**)
- CHECK_SYMBOLS -- terminal-native symbol availability (alias table)

Стратегия **не знает** про лоты, маржу, DD лимиты, news calendar, 3SL guard, timezone, trading hours, position sizing, RiskFactor, asset classes, magic numbers, symbol availability, **virtual vs real mode**, tier, daily DD mode, R-cap enforcement, **per-class leverage**, **OrderCalcProfit**. Она знает только бары и свои позиции. Стратегия объявляет `r_cap` в конфиге и передаёт `tp_r`/`protector_lock_r` в signal_data -- демон сам считает R-результат и применяет бюджет.
