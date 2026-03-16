using System.Text.Json;
using Daemon.Config;
using Daemon.Models;
using Daemon.Tester;

namespace Daemon.Dashboard;

/// <summary>
/// Partial class for Backtest/Tester tab WebSocket commands.
/// Phase 10+: Data management, cost model, backtest execution.
/// </summary>
public partial class DashboardServer
{
    // ── Injected references (set from Program.cs) ──────────────

    private BarsHistoryDb? _barsHistoryDb;
    private CostModelLoader? _costModelLoader;

    /// <summary>Set BarsHistoryDb reference for backtest data storage.</summary>
    public void SetBarsHistoryDb(BarsHistoryDb db) => _barsHistoryDb = db;

    /// <summary>Set CostModelLoader reference for cost model queries.</summary>
    public void SetCostModelLoader(CostModelLoader loader) => _costModelLoader = loader;

    // Active download cancellation
    private CancellationTokenSource? _downloadCts;
    private bool _isDownloading;

    // Active backtest
    private BacktestEngine? _backtestEngine;
    private CancellationTokenSource? _backtestCts;
    private BacktestResult? _lastResult;

    // ===================================================================
    // bt_get_strategies — list available strategies with requirements
    // ===================================================================

    /// <summary>
    /// Returns strategies available for backtesting.
    /// Reads HELLO requirements from strategy config files to show symbols/timeframes.
    /// </summary>
    private object HandleBtGetStrategies()
    {
        var strategyNames = _strategyMgr.DiscoverStrategies();
        var strategyDir = Path.GetFullPath(_config.StrategyDir, AppContext.BaseDirectory);

        var strategies = new List<object>();
        foreach (var name in strategyNames)
        {
            var configPath = Path.Combine(strategyDir, name, "config.json");
            object? requirements = null;

            if (File.Exists(configPath))
            {
                try
                {
                    var json = File.ReadAllText(configPath);
                    using var doc = JsonDocument.Parse(json);
                    var root = doc.RootElement;

                    // Schema v1.1: symbols + timeframes from combos[]
                    var symbols = new List<string>();
                    var timeframes = new Dictionary<string, string>();
                    int historyBars = 300;

                    if (root.TryGetProperty("combos", out var combos))
                    {
                        foreach (var combo in combos.EnumerateArray())
                        {
                            var sym = combo.GetProperty("sym").GetString();
                            if (sym == null) continue;
                            symbols.Add(sym);

                            // Extract TF from directions.*.strat.tf (first found)
                            if (combo.TryGetProperty("directions", out var dirs))
                            {
                                foreach (var dir in dirs.EnumerateObject())
                                {
                                    if (dir.Value.TryGetProperty("strat", out var strat) &&
                                        strat.TryGetProperty("tf", out var tfVal))
                                    {
                                        timeframes[sym] = tfVal.GetString() ?? "M30";
                                        break;
                                    }
                                }
                            }
                            // Pairs strategy: strat at combo level
                            else if (combo.TryGetProperty("strat", out var pairsStrat) &&
                                     pairsStrat.TryGetProperty("tf", out var pairsTf))
                            {
                                timeframes[sym] = pairsTf.GetString() ?? "M30";
                            }

                            // For pairs: also add symA/symB if present
                            if (combo.TryGetProperty("symA", out var symA) &&
                                combo.TryGetProperty("symB", out var symB))
                            {
                                var a = symA.GetString();
                                var b = symB.GetString();
                                if (a != null && !symbols.Contains(a)) symbols.Add(a);
                                if (b != null && !symbols.Contains(b)) symbols.Add(b);
                                // Pairs use same TF for both legs
                                if (timeframes.ContainsKey(sym))
                                {
                                    if (a != null) timeframes[a] = timeframes[sym];
                                    if (b != null) timeframes[b] = timeframes[sym];
                                }
                            }
                        }
                    }

                    // params.history_bars
                    if (root.TryGetProperty("params", out var paramsProp))
                    {
                        if (paramsProp.TryGetProperty("history_bars", out var hbEl))
                            historyBars = hbEl.GetInt32();
                    }
                    // Fallback: root-level history_bars
                    if (root.TryGetProperty("history_bars", out var hbRoot))
                        historyBars = hbRoot.GetInt32();

                    // params.r_cap
                    double? rCap = null;
                    if (root.TryGetProperty("params", out var params2) &&
                        params2.TryGetProperty("r_cap", out var rcEl))
                        rCap = rcEl.GetDouble();

                    requirements = new
                    {
                        symbols,
                        timeframes,
                        history_bars = historyBars,
                        r_cap = rCap
                    };
                }
                catch (Exception ex)
                {
                    _log.Warn($"[Backtest] Failed to parse config for {name}: {ex.Message}");
                }
            }

            strategies.Add(new
            {
                name,
                has_config = File.Exists(configPath),
                requirements
            });
        }

        // Also include strategy assignments (which terminal they're assigned to)
        var assignments = _config.Strategies.Select(a => new
        {
            strategy = a.Strategy,
            terminal = a.Terminal,
            magic = a.Magic,
            r_cap = a.RCap
        }).ToList();

        return new { cmd = "bt_strategies", strategies, assignments };
    }

    // ===================================================================
    // bt_get_data_coverage — check what historical data we have
    // ===================================================================

    /// <summary>
    /// Check data coverage for given symbols/timeframe/period.
    /// Returns per-symbol coverage info and overall percentage.
    /// </summary>
    private object HandleBtGetDataCoverage(JsonElement root)
    {
        if (_barsHistoryDb == null)
            return new { cmd = "bt_data_coverage", error = "BarsHistoryDb not initialized" };

        // Parse request
        var symbols = new List<string>();
        if (root.TryGetProperty("symbols", out var symEl))
        {
            foreach (var s in symEl.EnumerateArray())
            {
                var sym = s.GetString();
                if (sym != null) symbols.Add(sym);
            }
        }

        var timeframe = root.TryGetProperty("timeframe", out var tfProp)
            ? tfProp.GetString() ?? "M30" : "M30";
        var fromTs = root.TryGetProperty("from_ts", out var fromProp) ? fromProp.GetInt64() : 0L;
        var toTs = root.TryGetProperty("to_ts", out var toProp) ? toProp.GetInt64() : 0L;
        var source = root.TryGetProperty("terminal", out var srcProp)
            ? srcProp.GetString() ?? "legacy" : "legacy";

        var coverage = _barsHistoryDb.GetCoverage(symbols, timeframe, fromTs, toTs, source);

        // Build response with per-symbol details
        var symbolDetails = coverage.Symbols.Select(kv => new
        {
            symbol = kv.Key,
            has_data = kv.Value.HasData,
            fully_covered = kv.Value.FullyCovered,
            partial = kv.Value.PartialCovered,
            bar_count = kv.Value.BarCount,
            from = kv.Value.AvailableFrom,
            to = kv.Value.AvailableTo
        }).ToList();

        return new
        {
            cmd = "bt_data_coverage",
            timeframe,
            from_ts = fromTs,
            to_ts = toTs,
            total_required = coverage.TotalRequired,
            total_available = coverage.TotalAvailable,
            percent = Math.Round(coverage.Percent * 100, 1),
            symbols = symbolDetails,
            db_size_mb = Math.Round(_barsHistoryDb.GetDbSizeBytes() / 1048576.0, 2)
        };
    }

    // ===================================================================
    // bt_download_bars — start downloading historical bars for symbols
    // ===================================================================

    /// <summary>
    /// Start async download of historical bars from MT5 terminal.
    /// Sends bt_download_progress pushes for each symbol.
    /// Sends bt_download_complete when done.
    /// </summary>
    private Task<object> HandleBtDownloadBars(JsonElement root, CancellationToken ct)
    {
        if (_barsHistoryDb == null)
            return Task.FromResult<object>(new { cmd = "bt_download_bars", error = "BarsHistoryDb not initialized" });

        if (_isDownloading)
            return Task.FromResult<object>(new { cmd = "bt_download_bars", error = "Download already in progress" });

        // Parse request
        var terminalId = root.GetProperty("terminal").GetString()!;
        var timeframe = root.TryGetProperty("timeframe", out var tfProp)
            ? tfProp.GetString() ?? "M30" : "M30";
        var fromTs = root.GetProperty("from_ts").GetInt64();
        var toTs = root.GetProperty("to_ts").GetInt64();

        // Extend download range to include warmup bars before fromTs.
        // Strategies need up to 1200 M30 bars for indicators (ATR, etc.).
        // 60 days covers worst case: 1200 M30 bars = 25 days + safety margin.
        long warmupBuffer = 60L * 24 * 3600;
        long downloadFromTs = fromTs - warmupBuffer;

        var symbols = new List<string>();
        if (root.TryGetProperty("symbols", out var symEl))
        {
            foreach (var s in symEl.EnumerateArray())
            {
                var sym = s.GetString();
                if (sym != null) symbols.Add(sym);
            }
        }

        if (symbols.Count == 0)
            return Task.FromResult<object>(new { cmd = "bt_download_bars", error = "No symbols specified" });

        // Verify terminal is connected
        if (!_connector.IsConnected(terminalId))
            return Task.FromResult<object>(new { cmd = "bt_download_bars", error = $"Terminal {terminalId} not connected" });

        // Start download in background
        _downloadCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _isDownloading = true;
        var downloadCt = _downloadCts.Token;

        // Detect server timezone from terminal
        var serverTz = "EET"; // default for The5ers
        try
        {
            // Try to get timezone from terminal profile
            var profile = _state.GetProfile(terminalId);
            if (profile != null && !string.IsNullOrEmpty(profile.ServerTimezone))
                serverTz = profile.ServerTimezone;
        }
        catch { }

        _ = Task.Run(async () =>
        {
            int completed = 0;
            int failed = 0;
            var errors = new List<string>();

            try
            {
                for (int i = 0; i < symbols.Count; i++)
                {
                    if (downloadCt.IsCancellationRequested) break;

                    var symbol = symbols[i];
                    var progressPct = Math.Round((double)i / symbols.Count * 100, 1);

                    // Push progress: starting symbol
                    await BroadcastAsync(new
                    {
                        cmd = "bt_download_progress",
                        symbol,
                        index = i,
                        total = symbols.Count,
                        percent = progressPct,
                        status = "downloading"
                    });

                    try
                    {
                        var bars = await _connector.GetBarsRangeAsync(
                            terminalId, symbol, timeframe, downloadFromTs, toTs, downloadCt);

                        if (bars != null && bars.Count > 0)
                        {
                            _barsHistoryDb.SaveBarsBulk(symbol, timeframe, bars, terminalId, serverTz, terminalId);
                            completed++;

                            _log.Info($"[Backtest] Downloaded {symbol} {timeframe}: {bars.Count} bars");
                        }
                        else
                        {
                            failed++;
                            errors.Add($"{symbol}: no data returned");
                            _log.Warn($"[Backtest] No data for {symbol} {timeframe}");
                        }
                    }
                    catch (Exception ex)
                    {
                        failed++;
                        errors.Add($"{symbol}: {ex.Message}");
                        _log.Error($"[Backtest] Failed to download {symbol}: {ex.Message}");
                    }

                    // Push progress: symbol done
                    await BroadcastAsync(new
                    {
                        cmd = "bt_download_progress",
                        symbol,
                        index = i + 1,
                        total = symbols.Count,
                        percent = Math.Round((double)(i + 1) / symbols.Count * 100, 1),
                        status = "done"
                    });
                }
            }
            catch (OperationCanceledException)
            {
                _log.Info("[Backtest] Download cancelled by user");
            }
            finally
            {
                _isDownloading = false;
                _downloadCts?.Dispose();
                _downloadCts = null;
            }

            // Push completion
            await BroadcastAsync(new
            {
                cmd = "bt_download_complete",
                completed,
                failed,
                total = symbols.Count,
                cancelled = downloadCt.IsCancellationRequested,
                errors
            });
        }, downloadCt);

        return Task.FromResult<object>(new
        {
            cmd = "bt_download_bars",
            status = "started",
            terminal = terminalId,
            timeframe,
            symbol_count = symbols.Count
        });
    }

    // ===================================================================
    // bt_cancel_download — cancel ongoing download
    // ===================================================================

    private object HandleBtCancelDownload()
    {
        if (!_isDownloading || _downloadCts == null)
            return new { cmd = "bt_cancel_download", status = "no_download" };

        _downloadCts.Cancel();
        return new { cmd = "bt_cancel_download", status = "cancelling" };
    }

    // ===================================================================
    // bt_get_cost_model — return current cost model data
    // ===================================================================

    /// <summary>
    /// Returns cost model data for display in the Data section.
    /// Shows per-symbol spread/slippage in their native units.
    /// </summary>
    private object HandleBtGetCostModel()
    {
        if (_costModelLoader == null)
            return new { cmd = "bt_cost_model", error = "Cost model not loaded" };

        var entries = _costModelLoader.GetRawEntries();
        var units = _costModelLoader.GetUnits();

        var symbols = entries.Select(kv =>
        {
            var unit = units.GetValueOrDefault(kv.Value.AssetClass, "pips");
            return new
            {
                symbol = kv.Key,
                asset_class = kv.Value.AssetClass,
                spread = kv.Value.Spread,
                slippage = kv.Value.Slippage,
                spread_open_widened = kv.Value.SpreadOpenWidened,
                total = Math.Round(kv.Value.Spread + kv.Value.Slippage, 2),
                unit
            };
        }).ToList();

        // Build alias lookup: variant → canonical symbol name in cost model
        // So UI can resolve DAX40 → DE40, JPN225 → JP225, etc.
        var aliasLookup = new Dictionary<string, string>();
        var resolver = _connector.GetSymbolResolver();
        foreach (var canonical in entries.Keys)
        {
            foreach (var variant in resolver.GetVariants(canonical))
            {
                if (!variant.Equals(canonical, StringComparison.OrdinalIgnoreCase))
                    aliasLookup.TryAdd(variant, canonical);
            }
        }

        return new { cmd = "bt_cost_model", units, symbols, aliases = aliasLookup };
    }

    // ===================================================================
    // bt_get_download_meta — list all downloaded data
    // ===================================================================

    private object HandleBtGetDownloadMeta()
    {
        if (_barsHistoryDb == null)
            return new { cmd = "bt_download_meta", error = "BarsHistoryDb not initialized" };

        var meta = _barsHistoryDb.GetAllMeta();
        var data = meta.Select(m => new
        {
            symbol = m.Symbol,
            timeframe = m.Timeframe,
            source = m.Source,
            from_time = m.FromTime,
            to_time = m.ToTime,
            bar_count = m.BarCount,
            downloaded_at = m.DownloadedAt.ToString("o"),
            terminal_id = m.TerminalId,
            server_tz = m.ServerTz
        }).ToList();

        return new
        {
            cmd = "bt_download_meta",
            data,
            db_size_mb = Math.Round(_barsHistoryDb.GetDbSizeBytes() / 1048576.0, 2)
        };
    }

    // ===================================================================
    // bt_delete_bars — delete bars for a symbol/timeframe
    // ===================================================================

    private object HandleBtDeleteBars(JsonElement root)
    {
        if (_barsHistoryDb == null)
            return new { cmd = "bt_delete_bars", error = "BarsHistoryDb not initialized" };

        var symbol = root.GetProperty("symbol").GetString()!;
        var timeframe = root.GetProperty("timeframe").GetString()!;
        var source = root.TryGetProperty("source", out var srcProp)
            ? srcProp.GetString() ?? "legacy" : "legacy";

        var deleted = _barsHistoryDb.DeleteBars(symbol, timeframe, source);
        _log.Info($"[Backtest] Deleted {deleted} bars for {symbol} {timeframe} source={source}");

        return new { cmd = "bt_delete_bars", symbol, timeframe, source, deleted };
    }

    // ===================================================================
    // bt_run — start a backtest
    // ===================================================================

    private Task<object> HandleBtRun(JsonElement root, CancellationToken ct)
    {
        if (_barsHistoryDb == null)
            return Task.FromResult<object>(new { cmd = "bt_run", error = "BarsHistoryDb not initialized" });

        if (_backtestEngine != null && _backtestEngine.IsRunning)
            return Task.FromResult<object>(new { cmd = "bt_run", error = "Backtest already running" });

        // Parse config
        var strategy = root.GetProperty("strategy").GetString()!;
        var terminal = root.GetProperty("terminal").GetString()!;
        var fromTs = root.GetProperty("from_ts").GetInt64();
        var toTs = root.GetProperty("to_ts").GetInt64();
        var deposit = root.TryGetProperty("deposit", out var depProp) ? depProp.GetDouble() : 100000;
        var commission = root.TryGetProperty("commission", out var comProp) ? comProp.GetDouble() : 7.0;
        var leverage = root.TryGetProperty("leverage", out var levProp) ? levProp.GetDouble() : 100.0;
        var maxMarginPct = leverage > 0 ? Math.Round(100.0 / leverage, 4) : 0;
        var timeframe = root.TryGetProperty("timeframe", out var tfProp2) ? tfProp2.GetString() ?? "M30" : "M30";

        // Parse symbols and timeframes from strategy config
        var symbols = new List<string>();
        var timeframes = new Dictionary<string, string>();
        int historyBars = 300;
        double? rCap = null;

        var configPath = Path.Combine(
            Path.GetFullPath(_config.StrategyDir, AppContext.BaseDirectory), strategy, "config.json");
        if (File.Exists(configPath))
        {
            try
            {
                var json = File.ReadAllText(configPath);
                using var doc = JsonDocument.Parse(json);
                var cfgRoot = doc.RootElement;

                // Schema v1.1: combos[]
                if (cfgRoot.TryGetProperty("combos", out var combos))
                {
                    foreach (var combo in combos.EnumerateArray())
                    {
                        var sym = combo.GetProperty("sym").GetString();
                        if (sym == null) continue;
                        symbols.Add(sym);

                        // TF from directions.*.strat.tf
                        if (combo.TryGetProperty("directions", out var dirs))
                        {
                            foreach (var dir in dirs.EnumerateObject())
                            {
                                if (dir.Value.TryGetProperty("strat", out var strat) &&
                                    strat.TryGetProperty("tf", out var tfVal))
                                {
                                    timeframes[sym] = tfVal.GetString() ?? "M30";
                                    break;
                                }
                            }
                        }
                        else if (combo.TryGetProperty("strat", out var pairsStrat) &&
                                 pairsStrat.TryGetProperty("tf", out var pairsTf))
                        {
                            timeframes[sym] = pairsTf.GetString() ?? "M30";
                        }

                        // Pairs: symA/symB
                        if (combo.TryGetProperty("symA", out var symA) &&
                            combo.TryGetProperty("symB", out var symB))
                        {
                            var a = symA.GetString();
                            var b = symB.GetString();
                            if (a != null && !symbols.Contains(a)) symbols.Add(a);
                            if (b != null && !symbols.Contains(b)) symbols.Add(b);
                            if (timeframes.ContainsKey(sym))
                            {
                                if (a != null) timeframes[a] = timeframes[sym];
                                if (b != null) timeframes[b] = timeframes[sym];
                            }
                        }
                    }
                }

                // params.history_bars or root.history_bars
                if (cfgRoot.TryGetProperty("params", out var paramsProp) &&
                    paramsProp.TryGetProperty("history_bars", out var hbEl))
                    historyBars = hbEl.GetInt32();
                else if (cfgRoot.TryGetProperty("history_bars", out var hbRoot))
                    historyBars = hbRoot.GetInt32();

                // params.r_cap
                if (cfgRoot.TryGetProperty("params", out var params2) &&
                    params2.TryGetProperty("r_cap", out var rcEl))
                    rCap = rcEl.GetDouble();
            }
            catch (Exception ex)
            {
                _log.Warn($"[Backtest] Failed to parse config for {strategy}: {ex.Message}");
            }
        }

        // Override from request
        if (root.TryGetProperty("rcap_off", out var rcapOffProp) && rcapOffProp.GetBoolean())
            rCap = null;

        if (root.TryGetProperty("symbols", out var reqSyms))
        {
            symbols.Clear();
            foreach (var s in reqSyms.EnumerateArray())
                if (s.GetString() is string sym) symbols.Add(sym);
        }

        // Apply sizing filter: remove symbols disabled in sizing config for this terminal
        // This ensures backtest respects the same enabled/disabled toggles as live trading
        bool useSizing = !root.TryGetProperty("ignore_sizing", out var ignoreSzProp) || !ignoreSzProp.GetBoolean();
        var sizingFactors = new Dictionary<string, double>();  // symbol → risk_factor from sizing

        if (useSizing)
        {
            var allSizing = _state.GetAllSymbolSizing(terminal);
            if (allSizing.Count > 0)
            {
                var sizingMap = allSizing.ToDictionary(s => s.Symbol, StringComparer.OrdinalIgnoreCase);
                var filtered = new List<string>();
                foreach (var sym in symbols)
                {
                    if (sizingMap.TryGetValue(sym, out var sizing))
                    {
                        if (!sizing.Enabled)
                        {
                            _log.Info($"[Backtest] Skipping {sym}: disabled in sizing for {terminal}");
                            continue;
                        }
                        sizingFactors[sym] = sizing.RiskFactor;
                    }
                    filtered.Add(sym);
                }
                symbols = filtered;
                _log.Info($"[Backtest] After sizing filter: {symbols.Count} symbols " +
                          $"({allSizing.Count - symbols.Count} disabled)");
            }
        }

        if (symbols.Count == 0)
            return Task.FromResult<object>(new { cmd = "bt_run", error = "No symbols" });

        // Build config
        var btConfig = new BacktestConfig
        {
            Strategy = strategy,
            TerminalId = terminal,
            FromTs = fromTs,
            ToTs = toTs,
            Timeframe = timeframe,
            Deposit = deposit,
            CommissionPerLot = commission,
            Magic = 9000,
            RCap = rCap,
            Symbols = symbols,
            Timeframes = timeframes,
            HistoryBars = historyBars,
            Source = terminal,
            SizingFactors = sizingFactors,
            MaxMarginPct = maxMarginPct,
        };

        // Start backtest in background
        _backtestCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _lastResult = null;

        _backtestEngine = new BacktestEngine(
            btConfig, _config, _barsHistoryDb, _costModelLoader,
            _connector, _state, _log);

        _backtestEngine.OnProgress = async (processed, total, symbol, barTime) =>
        {
            await BroadcastAsync(new
            {
                cmd = "bt_progress",
                processed,
                total,
                percent = Math.Round((double)processed / total * 100, 1),
                bar_time = barTime,
            });
        };

        var backtestCt = _backtestCts.Token;

        _ = Task.Run(async () =>
        {
            try
            {
                var result = await _backtestEngine.RunAsync(backtestCt);
                _lastResult = result;

                // Push completion
                await BroadcastAsync(new
                {
                    cmd = "bt_complete",
                    strategy = result.Strategy,
                    total_trades = result.TotalTrades,
                    win_rate = Math.Round(result.WinRate * 100, 1),
                    total_r = result.TotalR,
                    max_dd_r = result.MaxDdR,
                    profit_factor = result.ProfitFactor,
                    final_balance = result.FinalBalance,
                    duration_sec = Math.Round(result.DurationSec, 1),
                    cancelled = result.Cancelled,
                    error = result.Error,
                    blocked_signals = result.BlockedSignals,
                });
            }
            catch (Exception ex)
            {
                _log.Error($"[Backtest] Run failed: {ex.Message}");
                await BroadcastAsync(new { cmd = "bt_complete", error = ex.Message });
            }
            finally
            {
                _backtestCts?.Dispose();
                _backtestCts = null;
            }
        }, backtestCt);

        // Collect disabled symbols for UI feedback
        var disabledSymbols = new List<string>();
        if (useSizing)
        {
            var allSizing2 = _state.GetAllSymbolSizing(terminal);
            foreach (var s in allSizing2)
                if (!s.Enabled && !symbols.Contains(s.Symbol))
                    disabledSymbols.Add(s.Symbol);
        }

        return Task.FromResult<object>(new
        {
            cmd = "bt_run",
            status = "started",
            strategy,
            terminal,
            symbols = symbols.Count,
            symbols_list = symbols,
            sizing_applied = useSizing && sizingFactors.Count > 0,
            sizing_disabled = disabledSymbols,
            sizing_factors = sizingFactors.Count > 0 ? sizingFactors : null,
        });
    }

    // ===================================================================
    // bt_cancel — cancel running backtest
    // ===================================================================

    private object HandleBtCancel()
    {
        if (_backtestEngine == null || !_backtestEngine.IsRunning || _backtestCts == null)
            return new { cmd = "bt_cancel", status = "no_backtest" };

        _backtestCts.Cancel();
        return new { cmd = "bt_cancel", status = "cancelling" };
    }

    // ===================================================================
    // bt_get_result — get last backtest result
    // ===================================================================

    private object HandleBtGetResult()
    {
        if (_lastResult == null)
            return new { cmd = "bt_result", error = "No result available" };

        var r = _lastResult;
        return new
        {
            cmd = "bt_result",
            strategy = r.Strategy,
            terminal = r.TerminalId,
            from_ts = r.FromTs,
            to_ts = r.ToTs,
            timeframe = r.Timeframe,
            initial_balance = r.InitialBalance,
            final_balance = r.FinalBalance,

            // Summary
            total_trades = r.TotalTrades,
            wins = r.Wins,
            losses = r.Losses,
            win_rate = Math.Round(r.WinRate * 100, 1),
            blocked_signals = r.BlockedSignals,
            bars_processed = r.BarsProcessed,
            duration_sec = Math.Round(r.DurationSec, 1),

            // R-metrics
            total_r = r.TotalR,
            max_dd_r = r.MaxDdR,
            calmar_r = r.CalmarR,
            best_day_r = r.BestDayR,
            worst_day_r = r.WorstDayR,

            // $-metrics
            total_pnl = r.TotalPnlDollar,
            max_dd_dollar = r.MaxDdDollar,
            profit_factor = r.ProfitFactor,
            total_commission = r.TotalCommission,
            total_cost = r.TotalCost,

            // Breakdown
            per_symbol_r = r.PerSymbolR,
            gate_stats = r.GateStats,
            r_cap = r.RCap,

            // Trades (limited for WS message size)
            trades = r.Trades.Select(t => new
            {
                ticket = t.Ticket,
                symbol = t.Symbol,
                dir = t.Direction,
                volume = t.Volume,
                price_open = t.PriceOpen,
                price_close = t.PriceClose,
                open_time = t.OpenTime,
                close_time = t.CloseTime,
                sl = t.SL,
                tp = t.TP,
                reason = t.Reason,
                pnl_raw = Math.Round(t.PnlRaw, 2),
                cost = Math.Round(t.FlatCostDollar, 2),
                commission_dollar = Math.Round(t.CommissionDollar, 2),
                pnl = Math.Round(t.PnlDollar, 2),
                r = t.RResult,
                cost_r = t.FlatCostR,
                balance = Math.Round(t.BalanceAfter, 2),
                strategy = t.Strategy,
                magic = t.Magic,
                signal_data = t.SignalData,
            }).ToList(),

            // Equity curve
            equity_curve = r.EquityCurve.Select(s => new
            {
                time = s.Time,
                balance = Math.Round(s.Balance, 2),
                equity = Math.Round(s.Equity, 2),
            }).ToList(),

            // Blocked signals (full list for CSV export)
            blocked_list = r.BlockedSignalsList.Select(b => new
            {
                symbol = b.Symbol,
                dir = b.Direction,
                sl = b.SlPrice,
                tp = b.TpPrice,
                gate = b.Gate,
                reason = b.Reason,
                bar_time = b.BarTime,
                entry_price = b.EntryPrice,
            }).ToList(),

            cancelled = r.Cancelled,
            error = r.Error,
        };
    }
}
