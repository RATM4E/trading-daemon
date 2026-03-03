# TRADING DAEMON -- ДОПОЛНЕНИЯ К ПЛАНУ v3.5

Документ содержит архитектурные уточнения и дополнения, выявленные в ходе обсуждения и реализации. Применять поверх основного плана TRADING_DAEMON_IMPLEMENTATION_PLAN.md.

---

## ДОПОЛНЕНИЕ 4: Загрузка истории для криптобиржи (OKX)

### Принцип

Тот же что для MT5: BarsCache в демоне, стратегия получает полный массив с первого тика. Разница только в источнике -- вместо `mt5.copy_rates_from_pos()` используется `ccxt.fetch_ohlcv()`.

### Отличия от MT5

**1. Лимит на запрос.** MT5 отдаёт 700 баров одним вызовом. OKX API -- максимум 100-300 свечей за запрос. Для 700 баров нужна пагинация.

**2. Rate limits.** OKX ограничивает ~20 запросов в секунду на endpoint. Нужен контроль частоты запросов в OKX Worker.

**3. Нет локального кэша.** MT5 терминал хранит историю на диске и отдаёт мгновенно. OKX -- каждый запрос идёт в интернет. Первая загрузка медленнее.

### Когда реализовывать

После полной интеграции MT5 стратегий и стабильной live торговли. Sizing уже поддерживает `asset_class: "crypto"`.

---

## ДОПОЛНЕНИЕ 5: Replay Worker -- офлайн бэктест через демон

### Зачем

Перед выходом на live хочется убедиться что вся цепочка работает правильно: стратегия -> демон -> Risk Manager -> сигналы. Replay Worker позволяет прогнать месяцы истории за минуты.

### Три уровня тестирования стратегии

```
Уровень 1: Replay Worker (офлайн, минуты) -> сравнение с оригинальным бэктестом
Уровень 2: Virtual Trading (live данные, без ордеров, дни) -> проверка сигналов + P&L
Уровень 3: Live Auto (реальные ордера, недели) -> execution_quality
```

### Когда реализовывать

После обкатки Virtual Trading на live. Не блокирует текущую работу.

---

## ДОПОЛНЕНИЕ 10: Desktop App -- dashboard в отдельном окне

C# приложение с WebView2 (встроенный Edge). Окно без адресной строки, открывает `localhost:8080`. Отдельная иконка в taskbar. Можно встроить автозапуск демона.

### Когда реализовывать

После стабильной торговли. Браузер функционально эквивалентен.

---

## ДОПОЛНЕНИЕ 11: Деплоймент -- упаковка и установка

### Две среды

- **C# (.NET)** -- демон: `dotnet publish --self-contained -r win-x64 /p:PublishSingleFile=true` -> один `daemon.exe` (~60-70MB)
- **Python** -- воркеры/стратегии: устанавливается отдельно, `pip install MetaTrader5`

### WinExe для production

`<OutputType>WinExe</OutputType>` в csproj -- процесс без консольного окна.

### Portable MT5

Один терминал = один счёт = один воркер. Файл `portable.ini` в корне папки терминала -> всё хранится локально.

### Когда реализовывать

Phase 9.5 (перед переносом на VPS).

---

## ДОПОЛНЕНИЕ 13: Virtual desktops, скрытый MT5

### 13.1 -- Виртуальные рабочие столы для MT5

`MoveWindowToDesktop` через COM API. Terminal Profile: `virtual_desktop: 0/1/2/3`. Dashboard dropdown.

### 13.2 -- Скрытый MT5

`ProcessStartInfo { WindowStyle = ProcessWindowStyle.Hidden }`. Терминал работает, API отвечает, окно не видно. Toggle в dashboard.

### Когда реализовывать

После стабильной live торговли.

---

## ДОПОЛНЕНИЕ 17: Global Settings -- общие настройки + шифрованные секреты

### 17.1 -- Зашифрованные секреты

Хранилище: `secrets.dat` рядом с `state.db`. Шифрование: Windows DPAPI (`ProtectedData.Protect()`). Привязано к учётке Windows -- без логина не расшифровать.

Что хранится: Telegram bot_token/chat_id, OKX api_key/secret/passphrase, Dashboard auth_token, MT5 пароли (опционально).

Dashboard UI: поля с маской, кнопка Save -> зашифровать -> записать. Никогда не отдаётся через WebSocket в открытом виде.

### 17.2 -- Общие настройки демона

Переносятся из хардкода в единый UI: scheduler interval, news fetch interval, dashboard port, default mode, Telegram toggles (signals/blocks/crashes/DD warnings), debounce, default risk%, default no-trade window.

Хранилище: таблица `global_settings` в state.db (key-value).

### 17.3 -- Миграция

Первый запуск с Global Settings: прочитать Telegram token из config.json -> зашифровать -> записать в secrets.dat -> удалить из config.json.

### Когда реализовывать

Phase 9.5 (Security). Секреты -- обязательно перед VPS.

---

## ДОПОЛНЕНИЕ 19: Multi-strategy per terminal

### Текущее состояние

Архитектурно и инфраструктурно полностью готово. Auto-discovery + enable/disable + magic registry -- всё реализовано.

**Уже реализовано:**

- **Strategy Auto-Discovery** -- StrategyManager сканирует `strategies/` каждые 30с. Новая папка с `strategy.py` + `config.json` -> автоматическая регистрация в `strategy_registry` (state.db). Удалённая папка -> unregister + stop.
- **Enable/Disable** -- новые стратегии появляются как Disabled. Оператор включает через dashboard. Disable останавливает running instances. Состояние в SQLite.
- **Magic allocation** -- блок по 1000 на стратегию (100, 1100, 2100...). Хранится в `strategy_registry`, не в config.json.
- `config.json` -> `"strategies": []` -- **пустой массив**, runtime assignments создаются при Start.
- `Scheduler.cs` подставляет `process.Magic` в ORDER_SEND и сохраняет в позицию
- `Reconciler.cs` имеет `RegisterMagic()` и `_knownMagics` для фильтрации
- Dashboard: пилюли стратегий на плитках терминалов, Enable/Disable toggle, magic_base badge
- **Gate 11 (Same Symbol Per Strategy)** -- проверяет по magic, не по терминалу

### Что доделать

**1. P/L per strategy** в dashboard. Фильтр по magic в terminal_detail.

### Запуск второй стратегии

```
1. Создать strategies/my_strategy/ с strategy.py и config.json
2. <=30 сек -- плитка появится в dashboard как Disabled
3. Нажать Enable
4. Выбрать терминал -> Start
```

Больше никаких правок в config.json не нужно.

### Когда реализовывать

Когда будет готова вторая стратегия. Инфраструктура полностью готова.

---

## ДОПОЛНЕНИЕ 21: Operations -- обслуживание и мониторинг

### 21.3 -- Log rotation

Один файл в день: `daemon_2026-02-16.log`. Хранить 30 дней. Serilog (NuGet) или простой `StreamWriter` с датой в имени.

### 21.4 -- Terminal health monitor

```csharp
// Каждые 5 минут
long memMB = proc.WorkingSet64 / 1024 / 1024;
if (memMB > 500) _alerts.Send($"Warning: Worker using {memMB}MB RAM");
```

Пороги: Python worker > 500MB, MT5 > 2GB -> Telegram.

### 21.6 -- Tray launcher

Иконка в системном трее. Зелёный = демон жив, красный = остановлен. Правый клик: Open Dashboard, Start/Stop Daemon, Quit. WinForms `NotifyIcon`. Автозапуск через реестр.

### Когда реализовывать

```
Phase 9.5:    Log rotation (21.3)
После P9:     Health monitor (21.4), Tray launcher (21.6)
```

---

## ДОПОЛНЕНИЕ 24: Telegram Commands Phase 2 -- действия с подтверждением

Phase 1 (readonly: /status, /positions, /pnl, /news, /help) -- реализован.

### Phase 2 -- действия

```
/emergency              -> "Close ALL positions on ALL terminals? Reply /confirm"
/confirm                -> выполнить последнюю запрошенную команду
/close EURUSD           -> закрыть позицию по символу
/mode monitor The5ers   -> переключить режим терминала
/mode virtual The5ers   -> переключить в виртуальный режим
```

Действия требуют `/confirm` в течение 30 секунд. Без подтверждения -- отмена. Защита от случайного нажатия.

### Когда реализовывать

После перехода на live торговлю.

---

## ПРИОРИТЕТЫ

### Phase 9: Боевое тестирование (ТЕКУЩАЯ ФАЗА)

```
--- Infrastructure (done) ---
[done] News Calendar Fetcher, Dashboard guards, Start terminals, Shutdown
[done] Trading Hours gate, Signal alerts, Signal dedup, Crash recovery
[done] BUG-1 through BUG-16 fixes
[done] Position Sizing, Sticky headers, Auto-backup, Hot-reload, Audit log
[done] Leverage per class, Telegram commands Phase 1, Telegram heartbeat
[done] Terminal management, Strategy auto-discovery, Gate 11
[done] CHECK_SYMBOLS, Multi-format Sizing init, Resizable columns

--- Phase 9.V: Virtual Trading (done) ---
[done] Virtual mode, virtual tickets, DB migration, virtual ENTER/EXIT/MODIFY
[done] VirtualTracker (SL/TP monitor, gap execution, trade snapshots)
[done] P&L calculation, balance/margin management, equity snapshots
[done] Dashboard: Virtual tab, equity chart, trade chart modal, statistics
[done] 10 bug fixes (BUG-7 through BUG-16)

--- Phase 9.D: Dashboard Polish (done) ---
[done] Risk Factor refactor (absolute % -> multiplier 0.0-1.0)
[done] Virtual P&L fixes (InstrumentCard pre-cache, Max DD display, reset purge)
[done] News display always-on (red events visible even with guard OFF)
[done] News importance filter selector (Low/Medium/High)
[done] Trading Hours clickable ON/OFF toggle
[done] Trade Chart modal rewrite (100 bars, TP line, SL step-line, auto-refresh,
       entry/exit markers, grid footer, Escape close, snapshot compatibility)
[done] HeartBeat filtering (hidden from All/AUDIT, dedicated HB button)

--- Testing (in progress) ---
[ ] Virtual Trading на live данных -> набор статистики 1-2 недели
[ ] Сравнить virtual P&L с бэктестом
[ ] Full Auto на демо -> execution quality
[ ] Execution quality analysis -> slippage, WR, Exp vs backtest
```

### Phase 9.5: Security + VPS

```
[ ] Global Settings + DPAPI секреты (Доп. 17)
[ ] Mutex guard (один инстанс)
[ ] Log rotation (Доп. 21.3)
[ ] Self-contained publish (Доп. 11)
[ ] WinExe (без консоли)
[ ] WireGuard VPN + dashboard auth
```

### После стабильной торговли

```
[ ] P/L per strategy (Доп. 19)             -- фильтр по magic в dashboard
[ ] Telegram action commands (Доп. 24)     -- /emergency, /close с подтверждением
[ ] OKX криптобиржа (Доп. 4)               -- sizing уже поддерживает asset_class: "crypto"
[ ] Replay Worker (Доп. 5)
[ ] Desktop App (Доп. 10)
[ ] Tray launcher (Доп. 21.6)
[ ] Terminal health monitor (Доп. 21.4)
[ ] Virtual desktops + скрытый MT5 (Доп. 13.1, 13.2)
[ ] Import/Export sizing между терминалами
```

---

## CHANGELOG ДОПОЛНЕНИЙ

- v1 -- Дополнение 1: прогрев индикаторов (BarsCache), Дополнение 2: серверное время DST, Дополнение 3: статус реализации
- v2 -- Дополнение 4: загрузка истории для криптобиржи (OKX)
- v3 -- Дополнение 5: Replay Worker (офлайн бэктест через демон)
- v4 -- Дополнение 6: автообнаружение терминалов
- v5 -- Удалены реализованные: 1 (BarsCache -- Phase 6), 3 (статус -- PROJECT_STATUS), 6 (автообнаружение -- Phase 7)
- v6 -- Удалено реализованное: 2 (Server Timezone -- Phase 8). Добавлено: 7 (News Calendar Fetcher)
- v7 -- Добавлены: 8 (Dashboard UI: guard settings), 9 (Start terminals), 10 (Desktop App), 11 (Деплоймент)
- v8 -- Добавлено: 12 (Shutdown из dashboard с опцией kill terminals, VBS launcher, WinExe для production)
- v9 -- Добавлено: 13 (Virtual desktops для MT5, скрытое окно, визуализация сделок)
- v10 -- Добавлено: 14 (Trading Hours -- Gate 10, no-trade window)
- v11 -- Удалены реализованные: 7, 8, 9, 12, 14. Phase 9 запущена.
- v12 -- Добавлено: 15 (Position Sizing). Crash recovery. BUG-1/2/3.
- v13 -- Реализовано: BUG-1/3, BUG-2, Position Sizing (Доп. 15) с asset_class и order_calc_margin.
- v14 -- Добавлены: 16 (Drag & Drop), 17 (Global Settings), 18 (Instrument charts), 19 (Multi-strategy), 20 (Sticky headers), 21 (Operations), 22 (Leverage per class), 23 (Telegram Heartbeat), 24 (Telegram Commands).
- v15 -- Удалены реализованные: 15 (Position Sizing), 16 (Drag & Drop), 20 (Sticky headers), 21.1 (Auto-backup), 21.2 (Hot-reload), 21.5 (Audit log), 22 (Leverage per class), 23 (Telegram heartbeat), 24 Phase 1 (readonly commands). Обновлено: 19 (Gate 11 реализован, осталось P/L per strategy).
- v16 -- Strategy Auto-Discovery реализован. Обновлено: 19 (auto-discovery + enable/disable реализованы).
- v17 -- CHECK_SYMBOLS, Telegram polling fix (BUG-6), multi-format Sizing init, resizable columns.
- v18 -- Phase 9.V Virtual Trading полностью реализован. Удалены: 13.3 (визуализация сделок), 18 (Instrument charts). Обновлены ПРИОРИТЕТЫ.
- **v19 -- Phase 9.D Dashboard Polish. Обновлены ПРИОРИТЕТЫ: добавлена секция Phase 9.D (done). Risk Factor refactor, Virtual P&L fixes, News always-on display, News importance filter, Trading Hours toggle, Trade Chart rewrite, HeartBeat filtering.**
