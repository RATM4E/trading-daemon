using System.Diagnostics;
using System.Text.Json;
using Daemon.Config;
using Daemon.Connector;
using Daemon.Engine;
using Daemon.Models;
using Daemon.Strategy;

namespace Daemon.Tester;

/// <summary>
/// Backtest replay engine. Runs a strategy on historical bars using the same
/// StrategyProcess (runner.py → strategy.py) as live trading.
///
/// Flow:
///   1. Load bars from BarsHistoryDb
///   2. Build timeline (sorted unique timestamps)
///   3. Start StrategyProcess on dedicated TCP port
///   4. Warmup: first TICK suppresses ENTER actions
///   5. Replay: for each bar → TICK → ACTIONS → Execute
///   6. Finish: close remaining positions, compute metrics
///
/// Runs on a background task. Reports progress via callback.
/// Does NOT interfere with live trading (separate process, separate state).
/// </summary>
public class BacktestEngine
{
    private readonly BacktestConfig _btConfig;
    private readonly DaemonConfig _daemonConfig;
    private readonly BarsHistoryDb _barsDb;
    private readonly CostModelLoader? _costModelLoader;
    private readonly ConnectorManager _connector;
    private readonly StateManager _liveState;     // only for profile/sizing reads
    private readonly ILogger _log;

    private BacktestExecutor? _executor;
    private StrategyProcess? _process;
    private bool _isRunning;

    /// <summary>Bar cursor per symbol for O(1) sliding window in BuildBarsSnapshot.</summary>
    private Dictionary<string, int>? _barCursors;

    /// <summary>Cursor snapshot after warmup tick — used to compute deltas in replay loop.</summary>
    private Dictionary<string, int>? _deltaStartCursors;

    public bool IsRunning => _isRunning;

    /// <summary>Progress callback: (barsProcessed, totalBars, currentSymbol, currentTime)</summary>
    public Func<int, int, string, long, Task>? OnProgress { get; set; }

    public BacktestEngine(
        BacktestConfig btConfig,
        DaemonConfig daemonConfig,
        BarsHistoryDb barsDb,
        CostModelLoader? costModelLoader,
        ConnectorManager connector,
        StateManager liveState,
        ILogger log)
    {
        _btConfig = btConfig;
        _daemonConfig = daemonConfig;
        _barsDb = barsDb;
        _costModelLoader = costModelLoader;
        _connector = connector;
        _liveState = liveState;
        _log = log;
    }

    // ===================================================================
    //  RUN
    // ===================================================================

    /// <summary>
    /// Execute the backtest. Blocking call, run on a background Task.
    /// </summary>
    public async Task<BacktestResult> RunAsync(CancellationToken ct)
    {
        _isRunning = true;
        var sw = Stopwatch.StartNew();
        var result = new BacktestResult
        {
            Strategy = _btConfig.Strategy,
            TerminalId = _btConfig.TerminalId,
            FromTs = _btConfig.FromTs,
            ToTs = _btConfig.ToTs,
            Timeframe = _btConfig.Timeframe,
            InitialBalance = _btConfig.Deposit,
        };

        try
        {
            // ── 1. Load bars ─────────────────────────────────
            _log.Info($"[Backtest] Loading bars for {_btConfig.Symbols.Count} symbols...");

            var allBars = new Dictionary<string, List<Bar>>();
            foreach (var symbol in _btConfig.Symbols)
            {
                var tf = _btConfig.Timeframes.GetValueOrDefault(symbol, _btConfig.Timeframe);
                var src = _btConfig.Source;
                var bars = _barsDb.GetBars(symbol, tf, _btConfig.FromTs, _btConfig.ToTs, src);

                // Also load warmup bars (before fromTs)
                var warmupBars = _barsDb.GetBars(symbol, tf, 0, _btConfig.FromTs, src);
                var warmupTail = warmupBars.TakeLast(_btConfig.HistoryBars).ToList();

                var combined = new List<Bar>(warmupTail.Count + bars.Count);
                combined.AddRange(warmupTail);
                combined.AddRange(bars);
                allBars[symbol] = combined;

                _log.Info($"[Backtest] {symbol}/{tf}: {bars.Count} bars + {warmupTail.Count} warmup");
            }

            // ── 2. Build timeline ────────────────────────────
            // Timeline = unique timestamps >= fromTs, sorted ascending
            var timeline = allBars.Values
                .SelectMany(bars => bars.Where(b => b.Time >= _btConfig.FromTs).Select(b => b.Time))
                .Distinct()
                .OrderBy(t => t)
                .ToList();

            _log.Info($"[Backtest] Timeline: {timeline.Count} steps " +
                      $"({DateTimeOffset.FromUnixTimeSeconds(timeline.First()):yyyy-MM-dd} → " +
                      $"{DateTimeOffset.FromUnixTimeSeconds(timeline.Last()):yyyy-MM-dd})");

            if (timeline.Count == 0)
            {
                result.Error = "No bars in the specified period";
                return result;
            }

            // ── 3. Resolve instrument cards ──────────────────
            var cards = new Dictionary<string, InstrumentCard>();
            foreach (var symbol in _btConfig.Symbols)
            {
                var card = await _connector.GetSymbolInfoAsync(_btConfig.TerminalId, symbol, ct);
                if (card != null) cards[symbol] = card;
                else _log.Warn($"[Backtest] No InstrumentCard for {symbol}, P&L will be 0");
            }

            // ── 4. Resolve cost model ────────────────────────
            var flatCostPrice = new Dictionary<string, double>();
            if (_costModelLoader != null)
            {
                var resolved = _costModelLoader.Resolve(cards);
                foreach (var kv in resolved.Costs)
                    flatCostPrice[kv.Key] = kv.Value.FlatCostPrice;
            }

            // ── 5. Create executor ───────────────────────────
            _executor = new BacktestExecutor(
                _btConfig.Deposit,
                _btConfig.CommissionPerLot,
                flatCostPrice,
                cards);

            // ── 6. Start strategy process ────────────────────
            var port = FindFreePort();
            var assignment = new StrategyAssignment
            {
                Strategy = _btConfig.Strategy,
                Terminal = _btConfig.TerminalId,
                Magic = _btConfig.Magic,
            };

            _process = new StrategyProcess(assignment, _daemonConfig, port, _log);

            _log.Info($"[Backtest] Starting strategy {_btConfig.Strategy} on port {port}...");
            var started = await _process.StartAsync(null, ct);
            if (!started || _process.Requirements == null)
            {
                result.Error = "Failed to start strategy process";
                return result;
            }

            _log.Info($"[Backtest] Strategy connected. Symbols: " +
                      string.Join(", ", _process.Requirements.Symbols));

            // ── FIX: Use strategy's actual history_bars requirement ──
            _btConfig.HistoryBars = _process.Requirements.HistoryBars;

            // Reset bar cursors for fresh run
            _barCursors = null;
            _deltaStartCursors = null;

            // ── 7. Warmup tick ───────────────────────────────
            {
                var warmupTime = timeline[0];
                var warmupBarsDict = BuildBarsSnapshot(allBars, warmupTime, _btConfig.HistoryBars);
                var warmupTick = BuildTick(warmupBarsDict, warmupTime, 0);
                var warmupActions = await _process.SendTickAsync(warmupTick, ct);

                int suppressed = warmupActions?.Count(a =>
                    a.Action.Equals("ENTER", StringComparison.OrdinalIgnoreCase)) ?? 0;
                if (suppressed > 0)
                    _log.Info($"[Backtest] Warmup tick: suppressed {suppressed} ENTER action(s)");

                // Process non-ENTER actions from warmup (EXIT, MODIFY_SL)
                if (warmupActions != null)
                {
                    foreach (var action in warmupActions)
                    {
                        if (action.Action.Equals("ENTER", StringComparison.OrdinalIgnoreCase))
                            continue;
                        // Unlikely during warmup, but handle defensively
                    }
                }

                // Snapshot cursor positions — replay loop sends only delta bars from here on
                _deltaStartCursors = new Dictionary<string, int>(_barCursors!);
            }

            // ── 8. Replay loop ───────────────────────────────
            int barsProcessed = 0;
            long lastProgressReport = 0;

            for (int i = 0; i < timeline.Count; i++)
            {
                ct.ThrowIfCancellationRequested();

                var barTime = timeline[i];
                var nextBarTime = i + 1 < timeline.Count ? timeline[i + 1] : barTime;

                // 8a. Build delta bars (only new bars since last tick) — ~2000x less JSON vs full snapshot
                var deltaSnapshot = BuildDeltaSnapshot(allBars, barTime);

                // 8b. Current bars (for SL/TP check) — O(1) via cursor position
                var currentBars = new Dictionary<string, Bar>();
                foreach (var kv in allBars)
                {
                    if (_barCursors!.TryGetValue(kv.Key, out int cursor) && cursor > 0)
                    {
                        var bar = kv.Value[cursor - 1]; // last bar <= upToTime
                        if (bar.Time == barTime)
                            currentBars[kv.Key] = bar;
                    }
                }

                // 8c. Check SL/TP on PREVIOUSLY opened positions
                var slTpClosed = _executor.CheckSLTP(currentBars, barTime);

                // 8d. Build TICK message
                var positions = _executor.OpenPositions.Select(p => new PositionData
                {
                    Ticket = p.Ticket,
                    Symbol = p.Symbol,
                    Direction = p.Direction == "BUY" ? "LONG" : "SHORT",
                    Volume = p.Volume,
                    PriceOpen = p.PriceOpen,
                    SL = p.SL,
                    TP = p.TP,
                    Profit = CalculateUnrealizedForPosition(p, currentBars),
                    OpenTime = p.OpenTime,
                    SignalData = p.SignalData,
                }).ToList();

                var tick = BuildTick(deltaSnapshot, barTime, _executor.GetEquity(currentBars));
                tick.IsDelta = true;
                tick.Positions = positions;

                // 8e. Send TICK → receive ACTIONS
                var actions = await _process.SendTickAsync(tick, ct);
                if (actions == null)
                {
                    _log.Warn($"[Backtest] No response at bar {barTime}, skipping");
                    barsProcessed++;
                    continue;
                }

                // 8f. Process actions
                var newTickets = new HashSet<long>();

                foreach (var action in actions)
                {
                    switch (action.Action.ToUpperInvariant())
                    {
                        case "ENTER":
                            await HandleEnterAsync(action, barTime, nextBarTime,
                                allBars, cards, currentBars, newTickets, ct);
                            break;

                        case "EXIT":
                            HandleExit(action, barTime, nextBarTime, allBars);
                            break;

                        case "MODIFY_SL":
                            HandleModifySL(action);
                            break;
                    }
                }

                // 8g. Equity snapshot (every bar, thin to ~2000 for UI later)
                _executor.RecordEquity(barTime, currentBars);

                barsProcessed++;

                // 8h. Progress report (every 500 bars or 2 seconds)
                if (barsProcessed % 500 == 0 ||
                    sw.ElapsedMilliseconds - lastProgressReport > 2000)
                {
                    lastProgressReport = sw.ElapsedMilliseconds;
                    if (OnProgress != null)
                        await OnProgress(barsProcessed, timeline.Count, "", barTime);
                }
            }

            // ── 9. Finish ────────────────────────────────────
            // Stop strategy
            try { await _process.StopAsync(CancellationToken.None); }
            catch { }

            // Close remaining positions at last bar Close
            var lastBars = new Dictionary<string, double>();
            foreach (var kv in allBars)
            {
                if (kv.Value.Count > 0)
                    lastBars[kv.Key] = kv.Value[^1].Close;
            }
            var eotTrades = _executor.CloseAllAtMarket(lastBars, timeline[^1]);
            if (eotTrades.Count > 0)
                _log.Info($"[Backtest] Closed {eotTrades.Count} positions at end of test");

            // ── 10. Build result ─────────────────────────────
            sw.Stop();
            result = BuildResult(result, barsProcessed, sw.Elapsed.TotalSeconds);

            _log.Info($"[Backtest] Complete: {result.TotalTrades} trades, " +
                      $"R={result.TotalR:+0.00;-0.00}, " +
                      $"WR={result.WinRate:P1}, " +
                      $"PF={result.ProfitFactor:F2}, " +
                      $"MaxDD(R)={result.MaxDdR:F2}, " +
                      $"{sw.Elapsed.TotalSeconds:F1}s");

            return result;
        }
        catch (OperationCanceledException)
        {
            _log.Info("[Backtest] Cancelled by user");
            result.Cancelled = true;

            // Still build partial results
            if (_executor != null)
            {
                sw.Stop();
                result = BuildResult(result, 0, sw.Elapsed.TotalSeconds);
            }

            return result;
        }
        catch (Exception ex)
        {
            _log.Error($"[Backtest] Error: {ex.Message}");
            result.Error = ex.Message;
            return result;
        }
        finally
        {
            _isRunning = false;

            // Cleanup strategy process
            try { _process?.Dispose(); }
            catch { }
            _process = null;
        }
    }

    // ===================================================================
    //  Action Handlers
    // ===================================================================

    private Task HandleEnterAsync(
        StrategyAction action, long barTime, long nextBarTime,
        Dictionary<string, List<Bar>> allBars,
        Dictionary<string, InstrumentCard> cards,
        Dictionary<string, Bar> currentBars,
        HashSet<long> newTickets,
        CancellationToken ct)
    {
        var symbol = action.Symbol!;
        var direction = action.Direction!.ToUpperInvariant();
        var slPrice = action.SlPrice ?? 0;

        // Fill price = next bar's Open (simulates market order filled on next candle)
        var nextBar = GetNextBar(allBars, symbol, nextBarTime);
        double fillPrice = nextBar?.Open ?? currentBars.GetValueOrDefault(symbol)?.Close ?? 0;
        if (fillPrice <= 0) return Task.CompletedTask;

        // Get instrument card
        if (!cards.TryGetValue(symbol, out var card)) return Task.CompletedTask;

        // Lot calculation (tick math fallback — no MT5 CalcProfit in backtest)
        var profile = _liveState.GetProfile(_btConfig.TerminalId);
        double riskMoney;
        if (profile != null)
        {
            double baseRisk = LotCalculator.GetRiskMoney(profile, _executor!.Balance);

            // Prefer sizing factors from btConfig (set at run time from sizing tab),
            // fall back to DB lookup, then 1.0
            double factor = _btConfig.SizingFactors.GetValueOrDefault(symbol, 0);
            if (factor <= 0)
            {
                var sizing = _liveState.GetSymbolSizing(_btConfig.TerminalId, symbol);
                factor = sizing?.RiskFactor ?? 1.0;
            }
            riskMoney = baseRisk * factor;
        }
        else
        {
            // Fallback: 1% of balance
            riskMoney = _executor!.Balance * 0.01;
        }

        var lotResult = LotCalculator.Calculate(
            entryPrice: fillPrice,
            slPrice: slPrice,
            riskMoney: riskMoney,
            card: card);

        if (!lotResult.Allowed || lotResult.Lot <= 0)
        {
            _executor.RecordBlockedSignal(action, "LOT_CALC", lotResult.Reason ?? "Lot=0", barTime, fillPrice);
            return Task.CompletedTask;
        }

        // Risk gates (lightweight: skip G0 pause, G5/G6 margin, G7 news for now)
        // G11: Same combo check (multi-combo) or same symbol (single-combo fallback)
        var signalComboKey = ParseComboKey(action.SignalData);
        if (signalComboKey != null)
        {
            // Multi-combo: block only same combo_key
            var existingCombo = _executor.OpenPositions.FirstOrDefault(p =>
                ParseComboKey(p.SignalData) == signalComboKey);
            if (existingCombo != null)
            {
                _executor.RecordBlockedSignal(action, "G11_SAME_COMBO",
                    $"Already has {existingCombo.Direction} {existingCombo.Symbol} ({signalComboKey})",
                    barTime, fillPrice);
                return Task.CompletedTask;
            }
        }
        else
        {
            // Single-combo fallback: block same symbol for this strategy
            var existingPos = _executor.OpenPositions.FirstOrDefault(p =>
                p.Symbol == symbol && p.Strategy == _btConfig.Strategy);
            if (existingPos != null)
            {
                _executor.RecordBlockedSignal(action, "G11_SAME_SYMBOL",
                    $"Already has {existingPos.Direction} {symbol}", barTime, fillPrice);
                return Task.CompletedTask;
            }
        }

        // G12 R-cap check
        if (_btConfig.RCap.HasValue)
        {
            var brokerDate = DateTimeOffset.FromUnixTimeSeconds(barTime).ToString("yyyy-MM-dd");
            var dailyR = _executor.GetDailyR(brokerDate);
            if (dailyR <= -_btConfig.RCap.Value)
            {
                _executor.RecordBlockedSignal(action, "G12_RCAP",
                    $"Daily R={dailyR:F2} >= cap {_btConfig.RCap.Value}", barTime, fillPrice);
                return Task.CompletedTask;
            }
        }

        // Open position
        var fillTime = nextBarTime;
        var pos = _executor.OpenPosition(action, fillPrice, fillTime, lotResult.Lot,
            _btConfig.Strategy, _btConfig.Magic);
        if (pos != null)
            newTickets.Add(pos.Ticket);
        return Task.CompletedTask;
    }
    private void HandleExit(StrategyAction action, long barTime, long nextBarTime,
                            Dictionary<string, List<Bar>> allBars)
    {
        var ticket = action.Ticket ?? 0;
        if (ticket == 0) return;

        var pos = _executor!.OpenPositions.FirstOrDefault(p => p.Ticket == ticket);
        if (pos == null) return;

        // Exit at next bar's Open
        var nextBar = GetNextBar(allBars, pos.Symbol, nextBarTime);
        double closePrice = nextBar?.Open ?? 0;
        if (closePrice <= 0) return;

        _executor.ClosePosition(ticket, closePrice, nextBarTime, "EXIT");
    }

    private void HandleModifySL(StrategyAction action)
    {
        var ticket = action.Ticket ?? 0;
        var newSl = action.NewSl ?? 0;
        if (ticket == 0 || newSl <= 0) return;

        _executor!.ModifySL(ticket, newSl);
    }

    // ===================================================================
    //  Helpers
    // ===================================================================

    /// <summary>Build bars dictionary (sliding window) for TICK message. O(1) per tick via cursors.</summary>
    private Dictionary<string, List<BarData>> BuildBarsSnapshot(
        Dictionary<string, List<Bar>> allBars, long upToTime, int maxBars)
    {
        // Initialize cursors on first call
        if (_barCursors == null)
        {
            _barCursors = new Dictionary<string, int>();
            foreach (var kv in allBars)
                _barCursors[kv.Key] = 0;
        }

        var snapshot = new Dictionary<string, List<BarData>>();
        foreach (var kv in allBars)
        {
            var bars = kv.Value;
            int cursor = _barCursors[kv.Key];

            // Advance cursor to include all bars <= upToTime
            while (cursor < bars.Count && bars[cursor].Time <= upToTime)
                cursor++;

            _barCursors[kv.Key] = cursor;

            // Slice: [max(0, cursor-maxBars) .. cursor)
            int startIdx = Math.Max(0, cursor - maxBars);
            int count = cursor - startIdx;

            var slice = new List<BarData>(count);
            for (int i = startIdx; i < cursor; i++)
            {
                var b = bars[i];
                slice.Add(new BarData
                {
                    Time = b.Time,
                    Open = b.Open,
                    High = b.High,
                    Low = b.Low,
                    Close = b.Close,
                    Volume = b.Volume,
                });
            }
            snapshot[kv.Key] = slice;
        }
        return snapshot;
    }

    /// <summary>Build a TICK message.</summary>
    private TickMessage BuildTick(Dictionary<string, List<BarData>> bars, long serverTime, double equity)
    {
        return new TickMessage
        {
            ServerTime = serverTime,
            Bars = bars,
            Equity = equity,
        };
    }

    /// <summary>
    /// Build delta bars: only the bars added since the previous tick.
    /// Advances _barCursors and updates _deltaStartCursors.
    /// Typically returns 0–1 bars per symbol per tick on same-timeframe strategies.
    /// </summary>
    private Dictionary<string, List<BarData>> BuildDeltaSnapshot(
        Dictionary<string, List<Bar>> allBars, long upToTime)
    {
        var delta = new Dictionary<string, List<BarData>>(allBars.Count);

        foreach (var kv in allBars)
        {
            var bars = kv.Value;
            int cursor = _barCursors![kv.Key];
            int prevCursor = _deltaStartCursors![kv.Key];

            // Advance cursor to include all bars <= upToTime
            while (cursor < bars.Count && bars[cursor].Time <= upToTime)
                cursor++;
            _barCursors[kv.Key] = cursor;

            // New bars = everything from prevCursor to current cursor
            int newCount = cursor - prevCursor;
            var newBars = new List<BarData>(newCount);
            for (int i = prevCursor; i < cursor; i++)
            {
                var b = bars[i];
                newBars.Add(new BarData
                {
                    Time = b.Time, Open = b.Open, High = b.High,
                    Low = b.Low, Close = b.Close, Volume = b.Volume,
                });
            }
            delta[kv.Key] = newBars;

            // Update delta start for next tick
            _deltaStartCursors[kv.Key] = cursor;
        }
        return delta;
    }

    /// <summary>Get the bar at or after a given time for a symbol. O(1) with cursor hint, O(log N) fallback.</summary>
    private Bar? GetNextBar(Dictionary<string, List<Bar>> allBars, string symbol, long time)
    {
        if (!allBars.TryGetValue(symbol, out var bars) || bars.Count == 0) return null;

        // Cursor hint: cursor points past last bar <= upToTime, often the "next bar"
        if (_barCursors != null && _barCursors.TryGetValue(symbol, out int cursor))
        {
            if (cursor < bars.Count && bars[cursor].Time >= time)
                return bars[cursor];
        }

        // Binary search fallback
        int lo = 0, hi = bars.Count - 1;
        Bar? result = null;
        while (lo <= hi)
        {
            int mid = (lo + hi) / 2;
            if (bars[mid].Time >= time) { result = bars[mid]; hi = mid - 1; }
            else lo = mid + 1;
        }
        return result;
    }

    /// <summary>Extract combo_key from signal_data JSON. Returns null if absent.</summary>
    private static string? ParseComboKey(string? signalData)
    {
        if (string.IsNullOrEmpty(signalData)) return null;
        try
        {
            using var doc = JsonDocument.Parse(signalData);
            if (doc.RootElement.TryGetProperty("combo_key", out var ck))
                return ck.GetString();
        }
        catch { }
        return null;
    }

    private double CalculateUnrealizedForPosition(BtPosition pos, Dictionary<string, Bar> currentBars)
    {
        if (!currentBars.TryGetValue(pos.Symbol, out var bar)) return 0;
        double diff = pos.Direction == "BUY" ? bar.Close - pos.PriceOpen : pos.PriceOpen - bar.Close;
        return diff * pos.Volume * 100000; // rough estimate for display only
    }

    private static int FindFreePort()
    {
        var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        int port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    // ===================================================================
    //  Metrics
    // ===================================================================

    private BacktestResult BuildResult(BacktestResult result, int barsProcessed, double durationSec)
    {
        if (_executor == null) return result;

        var trades = _executor.ClosedTrades;

        result.TotalTrades = trades.Count;
        result.Wins = trades.Count(t => t.RResult > 0);
        result.Losses = trades.Count(t => t.RResult <= 0);
        result.WinRate = trades.Count > 0 ? (double)result.Wins / trades.Count : 0;
        result.BlockedSignals = _executor.BlockedSignals.Count;
        result.BarsProcessed = barsProcessed;
        result.DurationSec = durationSec;

        // R-metrics
        result.TotalR = Math.Round(trades.Sum(t => t.RResult), 2);

        // Max DD (R) — peak-to-trough of cumulative R
        double cumR = 0, peakR = 0, maxDdR = 0;
        foreach (var t in trades)
        {
            cumR += t.RResult;
            if (cumR > peakR) peakR = cumR;
            double dd = peakR - cumR;
            if (dd > maxDdR) maxDdR = dd;
        }
        result.MaxDdR = Math.Round(maxDdR, 2);

        // Calmar R = TotalR / MaxDD(R) (annualized if > 1 year)
        double years = (result.ToTs - result.FromTs) / (365.25 * 86400);
        double annualR = years > 0 ? result.TotalR / years : result.TotalR;
        result.CalmarR = maxDdR > 0 ? Math.Round(annualR / maxDdR, 2) : 0;

        // Daily R stats
        var dailyRValues = _executor.ClosedTrades
            .GroupBy(t => DateTimeOffset.FromUnixTimeSeconds(t.CloseTime).ToString("yyyy-MM-dd"))
            .ToDictionary(g => g.Key, g => g.Sum(t => t.RResult));

        result.BestDayR = dailyRValues.Count > 0 ? Math.Round(dailyRValues.Values.Max(), 2) : 0;
        result.WorstDayR = dailyRValues.Count > 0 ? Math.Round(dailyRValues.Values.Min(), 2) : 0;

        // $-metrics
        result.FinalBalance = Math.Round(_executor.Balance, 2);
        result.TotalPnlDollar = Math.Round(_executor.Balance - _executor.InitialBalance, 2);
        result.TotalCommission = Math.Round(trades.Sum(t => t.CommissionDollar), 2);
        result.TotalCost = Math.Round(trades.Sum(t => t.FlatCostDollar), 2);

        // Max DD ($) — peak-to-trough of equity curve
        double peakEquity = _executor.InitialBalance, maxDdDollar = 0;
        foreach (var snap in _executor.EquitySnapshots)
        {
            if (snap.Equity > peakEquity) peakEquity = snap.Equity;
            double dd = peakEquity - snap.Equity;
            if (dd > maxDdDollar) maxDdDollar = dd;
        }
        result.MaxDdDollar = Math.Round(maxDdDollar, 2);

        // Profit Factor
        double grossProfit = trades.Where(t => t.PnlDollar > 0).Sum(t => t.PnlDollar);
        double grossLoss = Math.Abs(trades.Where(t => t.PnlDollar < 0).Sum(t => t.PnlDollar));
        result.ProfitFactor = grossLoss > 0 ? Math.Round(grossProfit / grossLoss, 2) : 0;

        // Per-symbol R
        result.PerSymbolR = trades
            .GroupBy(t => t.Symbol)
            .ToDictionary(g => g.Key, g => Math.Round(g.Sum(t => t.RResult), 2));

        // Gate stats
        result.GateStats = new Dictionary<string, int>(_executor.GateStats);

        // Detailed data
        result.Trades = trades;
        result.BlockedSignalsList = _executor.BlockedSignals;

        // Thin equity curve to ~2000 points for UI
        var snapshots = _executor.EquitySnapshots;
        if (snapshots.Count > 2000)
        {
            int step = snapshots.Count / 2000;
            result.EquityCurve = snapshots.Where((_, idx) => idx % step == 0 || idx == snapshots.Count - 1).ToList();
        }
        else
        {
            result.EquityCurve = snapshots;
        }

        result.RCap = _btConfig.RCap;

        return result;
    }
}
