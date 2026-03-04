using System.Text.Json;
using Daemon.Config;
using Daemon.Connector;
using Daemon.Engine;
using Daemon.Models;
using Daemon.Strategy;
using Daemon.Dashboard;
using Daemon.Tester;

namespace Daemon;

class Program
{
    static async Task Main(string[] args)
    {
        var log = new ConsoleLogger();
        await RunEngine(log);
    }

    // ===================================================================
    // ENGINE LOOP — Phase 6 + Phase 9.V: the actual trading daemon
    // ===================================================================

    static async Task RunEngine(ConsoleLogger log)
    {
        log.Info("=== Trading Daemon -- Engine Starting ===");

        // 1. Load config
        var configPath = FindConfig();
        if (configPath == null) { log.Error("config.json not found"); return; }

        var config = JsonSerializer.Deserialize<DaemonConfig>(await File.ReadAllTextAsync(configPath));
        if (config == null) { log.Error("Failed to parse config"); return; }

        // 2. StateManager (SQLite)
        var dbPath = Path.Combine(AppContext.BaseDirectory, "Data", "state.db");
        Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
        using var state = new StateManager(dbPath);
        log.Info($"StateManager: {dbPath}");

        // 3. ConnectorManager (MT5 workers)
        using var connector = new ConnectorManager(config, log);
        using var cts = new CancellationTokenSource();
        var dataDir = Path.Combine(AppContext.BaseDirectory, "Data");

        // 3b. SymbolResolver — unified symbol mapping (cost model aliases + per-terminal overrides)
        var symbolResolver = new SymbolResolver();

        // Load cost model aliases (canonical ↔ broker variants from cost_model_v2.json)
        var costModelPath = Path.IsPathRooted(config.CostModelPath)
            ? config.CostModelPath
            : Path.Combine(AppContext.BaseDirectory, config.CostModelPath);
        if (File.Exists(costModelPath))
        {
            symbolResolver.LoadCostModelAliases(File.ReadAllText(costModelPath));
            log.Info($"SymbolResolver: loaded {costModelPath} ({symbolResolver.AliasCount} aliases, " +
                     $"{symbolResolver.CanonicalCount} canonical symbols)");
        }

        // Load per-terminal symbol_map overrides from config.json
        foreach (var tc in config.Terminals.Where(t => t.Enabled))
            symbolResolver.LoadTerminalMap(tc.Id, tc.SymbolMap);

        connector.SetSymbolResolver(symbolResolver);
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            try { File.WriteAllText(Path.Combine(dataDir, ".clean_shutdown"), DateTime.UtcNow.ToString("o")); } catch { }
            cts.Cancel();
        };

        try
        {
            await connector.StartAsync(cts.Token);

            // 4. Initialize terminal profiles from live account data
            foreach (var termId in connector.GetAllTerminalIds())
            {
                if (!connector.IsConnected(termId)) continue;

                var acc = await connector.GetAccountInfoAsync(termId, cts.Token);
                if (acc != null && state.GetProfile(termId) == null)
                {
                    var detectedTz = DetectTimezone(acc.ServerTime);
                    state.SaveProfile(new TerminalProfile
                    {
                        TerminalId = termId,
                        Type = acc.TradeMode == 0 ? "demo" : "real",
                        AccountType = acc.AccountType,
                        Mode = "monitor",
                        ServerTimezone = detectedTz,
                    });
                    log.Info($"[{termId}] Created profile: {acc.AccountType}, mode=monitor, tz={detectedTz}");
                }

                state.LogEvent("SYSTEM", termId, null, "Daemon started",
                    JsonSerializer.Serialize(new { acc?.Balance, acc?.Equity, acc?.Leverage }));
            }

            // 5. Detect crash recovery
            var shutdownFlagPath = Path.Combine(dataDir, ".clean_shutdown");
            bool crashRecovery = !File.Exists(shutdownFlagPath);
            if (!crashRecovery)
            {
                File.Delete(shutdownFlagPath);
                log.Info("Clean shutdown flag found — normal startup");
            }

            // 6. Services
            var news = new NewsService(log, config.NewsCalendarFile);
            await news.StartAsync(cts.Token);
            using var alerts = new AlertService(state, log);
            alerts.ConfigureTelegram(config.TelegramBotToken, config.TelegramChatId);

            // 6b. Crash recovery — auto-start terminals
            if (crashRecovery)
            {
                log.Warn("No clean shutdown flag — crash recovery, auto-starting terminals...");
                _ = alerts.SendAsync("SYSTEM", "daemon",
                    "\u26a0\ufe0f Crash recovery — auto-starting terminals", bypassDebounce: true);

                foreach (var termConfig in config.Terminals)
                {
                    if (string.IsNullOrEmpty(termConfig.TerminalPath) || !File.Exists(termConfig.TerminalPath))
                        continue;
                    if (connector.IsConnected(termConfig.Id))
                        continue;

                    try
                    {
                        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                        {
                            FileName = termConfig.TerminalPath,
                            UseShellExecute = true
                        });
                        log.Info($"[{termConfig.Id}] Auto-started terminal: {termConfig.TerminalPath}");
                        state.LogEvent("SYSTEM", termConfig.Id, null, "Terminal auto-started (crash recovery)");
                    }
                    catch (Exception ex)
                    {
                        log.Error($"[{termConfig.Id}] Failed to auto-start: {ex.Message}");
                    }
                }
            }

            // 6. Risk Manager
            var risk = new RiskManager(state, connector, log, news);

            // 7. BarsCache
            var barsCache = new BarsCache();

            // 8. Reconciler
            var reconciler = new Reconciler(state, connector, log);
            foreach (var strat in config.Strategies)
                reconciler.RegisterMagicRange(strat.Magic, strat.Strategy);

            // 9. ActiveProtection
            var protection = new ActiveProtection(state, connector, alerts, news, log);

            // 9b. Phase 9.V: VirtualTracker — monitors SL/TP for virtual positions
            var virtualTracker = new VirtualTracker(state, barsCache, connector, alerts, config, log);
            risk.SetVirtualTracker(virtualTracker);
            protection.SetVirtualTracker(virtualTracker, barsCache);

            // 9c. Backtest: BarsHistoryDb + CostModelLoader
            var barsHistoryDbPath = Path.Combine(dataDir, config.BarsHistoryDb);
            using var barsHistoryDb = new BarsHistoryDb(barsHistoryDbPath);
            log.Info($"BarsHistoryDb: {barsHistoryDbPath}");

            CostModelLoader? costModelLoader = null;
            if (File.Exists(costModelPath))
            {
                costModelLoader = CostModelLoader.FromFile(costModelPath);
                log.Info($"CostModel: loaded ({costModelLoader.GetRawEntries().Count} symbols for backtest costs)");
            }
            else
            {
                log.Warn($"CostModel: {costModelPath} not found, backtest cost model unavailable");
            }

            // 10. Strategy Manager + auto-discovery
            using var strategyMgr = new StrategyManager(config, state, log);
            strategyMgr.StartAutoDiscovery(intervalSec: 30);
            var discovered = strategyMgr.DiscoverStrategies();

            // 10b. Dashboard server (Phase 7)
            DashboardServer? dashboard = null;
            if (config.DashboardEnabled)
            {
                dashboard = new DashboardServer(
                    config, state, connector, strategyMgr, news, alerts, log, configPath);
                dashboard.OnShutdownRequested = (killTerminals) =>
                {
                    log.Info($"Shutdown requested from dashboard (killTerminals={killTerminals})");
                    cts.Cancel();
                };
                dashboard.SetBarsCache(barsCache);
                dashboard.SetRiskManager(risk);
                dashboard.SetBarsHistoryDb(barsHistoryDb);
                if (costModelLoader != null)
                    dashboard.SetCostModelLoader(costModelLoader);
                await dashboard.StartAsync(cts.Token);
            }

            // 11. Scheduler
            var scheduler = new Scheduler(config, connector, strategyMgr, state,
                                          risk, barsCache, alerts, log);
            scheduler.SetVirtualTracker(virtualTracker);

            // 12. Auto-start strategies (from config)
            await strategyMgr.AutoStartAsync(cts.Token);

            // 12b. Crash recovery — auto-start strategies that were running before crash
            if (crashRecovery)
            {
                var activeStrategies = state.GetActiveStrategies();
                if (activeStrategies.Count > 0)
                {
                    log.Info($"Crash recovery: {activeStrategies.Count} strategy(-ies) to restore");
                    // Run in background — terminals need time to boot
                    _ = Task.Run(async () =>
                    {
                        // Wait for terminals to connect (up to 60s)
                        for (int i = 0; i < 12 && !cts.Token.IsCancellationRequested; i++)
                        {
                            await Task.Delay(5000, cts.Token);
                            var connectedCount = connector.GetAllTerminalIds()
                                .Count(t => connector.IsConnected(t));
                            if (connectedCount >= config.Terminals.Count)
                                break;
                            log.Info($"Crash recovery: waiting for terminals... {connectedCount}/{config.Terminals.Count}");
                        }

                        foreach (var (name, terminalId) in activeStrategies)
                        {
                            if (cts.Token.IsCancellationRequested) break;
                            if (!connector.IsConnected(terminalId))
                            {
                                log.Warn($"Crash recovery: terminal {terminalId} not connected, skipping {name}");
                                continue;
                            }
                            log.Info($"Crash recovery: restarting {name}@{terminalId}");
                            var result = await strategyMgr.StartStrategyAsync(name, terminalId, cts.Token);
                            if (result.Ok && dashboard != null)
                            {
                                await dashboard.BroadcastAsync(new
                                {
                                    @event = "strategy_status",
                                    data = new { name, terminal = terminalId, status = "running" }
                                });
                            }
                        }

                        _ = alerts.SendAsync("SYSTEM", "daemon",
                            $"\u2705 Crash recovery complete: {activeStrategies.Count} strategy(-ies) restored",
                            bypassDebounce: true);
                    }, cts.Token);
                }
                else
                {
                    state.ClearActiveStrategies(); // Clean slate
                }
            }

            log.Info("=== Engine Running -- Ctrl+C to stop ===");
            log.Info($"Terminals: {connector.GetAllTerminalIds().Count}");
            log.Info($"Strategies discovered: {discovered.Count}");
            log.Info($"Scheduler interval: {config.SchedulerIntervalSec}s");
            if (dashboard != null)
                log.Info($"Dashboard: http://{config.DashboardHost}:{config.DashboardPort}/");

            // Send startup alert
            _ = alerts.SendAsync("SYSTEM", "daemon",
                $"Daemon started: {connector.GetAllTerminalIds().Count} terminal(s), "
                + $"{discovered.Count} strategy(-ies) available"
                + (dashboard != null ? $", dashboard on :{config.DashboardPort}" : ""),
                bypassDebounce: true);

            // 13a. Telegram heartbeat + command polling
            alerts.ConfigureServices(connector, strategyMgr, news, config.TelegramHeartbeatHours);
            alerts.SetVirtualTracker(virtualTracker);
            alerts.SetRiskManager(risk);
            alerts.StartTelegramServices(cts.Token);

            // 13. Main loop
            var schedulerInterval = TimeSpan.FromSeconds(config.SchedulerIntervalSec);
            var reconcileInterval = TimeSpan.FromSeconds(60);
            var protectionInterval = TimeSpan.FromSeconds(15);
            var equitySnapshotInterval = TimeSpan.FromMinutes(5);  // Phase 9.V

            var lastReconcile = DateTime.MinValue;
            var lastProtection = DateTime.MinValue;
            var lastBackup = DateTime.MinValue;
            var lastEquitySnapshot = DateTime.MinValue;  // Phase 9.V

            while (!cts.Token.IsCancellationRequested)
            {
                var cycleStart = DateTime.UtcNow;

                try
                {
                    // Scheduler
                    await scheduler.TickAsync(cts.Token);

                    // Phase 9.V: VirtualTracker — check SL/TP for virtual positions
                    await virtualTracker.TickAsync(cts.Token);

                    // Phase 10: Continuous DD monitor — check realized+floating vs limit
                    await scheduler.MonitorDailyDDAsync(cts.Token);

                    // Reconciler
                    if (DateTime.UtcNow - lastReconcile > reconcileInterval)
                    {
                        foreach (var termId in connector.GetAllTerminalIds())
                        {
                            if (!connector.IsConnected(termId)) continue;
                            try { await reconciler.ReconcileAsync(termId, cts.Token); }
                            catch (Exception ex) { log.Warn($"[Reconciler] {termId}: {ex.Message}"); }
                        }
                        lastReconcile = DateTime.UtcNow;
                    }

                    // ActiveProtection
                    if (DateTime.UtcNow - lastProtection > protectionInterval)
                    {
                        await protection.TickAsync();
                        lastProtection = DateTime.UtcNow;
                    }

                    // Phase 9.V: Virtual equity snapshots (every 5 min)
                    if (DateTime.UtcNow - lastEquitySnapshot > equitySnapshotInterval)
                    {
                        try
                        {
                            foreach (var termId in connector.GetAllTerminalIds())
                            {
                                var profile = state.GetProfile(termId);
                                if (profile?.Mode != "virtual") continue;

                                var vBalance = state.GetVirtualBalance(termId);
                                if (vBalance == null) continue;

                                double unrealized = virtualTracker.GetUnrealizedPnl(termId);
                                double equity = vBalance.Value + unrealized;

                                // Sanity check: skip snapshot if unrealized PnL is absurdly large
                                // (prevents corrupted data from bad symbol info fallbacks)
                                if (Math.Abs(unrealized) > vBalance.Value * 5)
                                {
                                    log.Warn($"[VirtualEquity] {termId} unrealized={unrealized:F2} seems wrong (balance={vBalance.Value:F2}), skipping snapshot");
                                    continue;
                                }

                                int openCount = state.GetOpenVirtualPositions()
                                    .Count(p => p.TerminalId == termId);

                                state.SaveVirtualEquitySnapshot(
                                    termId, equity, vBalance.Value, unrealized, openCount);
                            }
                        }
                        catch (Exception ex) { log.Warn($"[VirtualEquity] Snapshot failed: {ex.Message}"); }
                        lastEquitySnapshot = DateTime.UtcNow;
                    }

                    // Auto-backup state.db (once per 24h)
                    if (DateTime.UtcNow - lastBackup > TimeSpan.FromHours(24))
                    {
                        try
                        {
                            var backupDir = Path.Combine(AppContext.BaseDirectory, "Data", "backups");
                            Directory.CreateDirectory(backupDir);
                            var backupPath = Path.Combine(backupDir, $"state_{DateTime.Now:yyyy-MM-dd}.db");
                            if (!File.Exists(backupPath))
                            {
                                var srcDb = Path.Combine(AppContext.BaseDirectory, "Data", "state.db");
                                if (File.Exists(srcDb))
                                {
                                    File.Copy(srcDb, backupPath);
                                    log.Info($"[Backup] state.db → {Path.GetFileName(backupPath)}");

                                    // Prune backups older than 14 days
                                    foreach (var old in Directory.GetFiles(backupDir, "state_*.db"))
                                    {
                                        if (File.GetCreationTime(old) < DateTime.Now.AddDays(-14))
                                        {
                                            File.Delete(old);
                                            log.Info($"[Backup] Pruned old backup: {Path.GetFileName(old)}");
                                        }
                                    }
                                }
                            }
                        }
                        catch (Exception ex) { log.Warn($"[Backup] Failed: {ex.Message}"); }
                        lastBackup = DateTime.UtcNow;
                    }

                    // Dashboard -- push live data to connected clients
                    if (dashboard != null && dashboard.ClientCount > 0)
                    {
                        try
                        {
                            foreach (var termId in connector.GetAllTerminalIds())
                            {
                                if (!connector.IsConnected(termId)) continue;
                                var acc = await connector.GetAccountInfoAsync(termId, cts.Token);
                                if (acc != null)
                                {
                                    await dashboard.BroadcastAsync(new
                                    {
                                        @event = "terminal_status",
                                        data = new
                                        {
                                            id = termId,
                                            status = "connected",
                                            balance = acc.Balance,
                                            equity = acc.Equity,
                                            profit = acc.Profit,
                                            margin = acc.Margin,
                                            marginFree = acc.MarginFree,
                                        }
                                    });
                                }
                            }
                        }
                        catch { }
                    }
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex)
                {
                    log.Error($"[Engine] Cycle error: {ex.Message}");
                }

                // Wait until next cycle
                var elapsed = DateTime.UtcNow - cycleStart;
                var remaining = schedulerInterval - elapsed;
                if (remaining > TimeSpan.Zero)
                {
                    try { await Task.Delay(remaining, cts.Token); }
                    catch (OperationCanceledException) { break; }
                }
            }

            // 14. Graceful shutdown
            log.Info("Shutting down...");

            dashboard?.Stop();
            dashboard?.Dispose();

            // Use a fresh token for cleanup — cts.Token is already cancelled
            using var shutdownCts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            var shutdownToken = shutdownCts.Token;

            try
            {
                _ = alerts.SendAsync("SYSTEM", "daemon", "Daemon stopping",
                    bypassDebounce: true);

                await strategyMgr.StopAllAsync(shutdownToken);
                state.ClearActiveStrategies(); // Safety net — all stopped
                await connector.StopAsync(shutdownToken);
            }
            catch (OperationCanceledException)
            {
                log.Warn("Graceful shutdown timed out (15s), forcing...");
            }

            var (cacheEntries, totalBars) = barsCache.GetStats();
            log.Info($"BarsCache: {cacheEntries} entries, {totalBars} total bars (released)");
            barsCache.Clear();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            log.Error($"Engine fatal: {ex}");
        }

        log.Info("=== Daemon Stopped ===");
    }


    // ===================================================================
    //  Helper
    // ===================================================================

    /// <summary>
    /// Auto-detect broker timezone from MT5 server_time epoch.
    /// Same logic as DashboardServer.DetectTimezone.
    /// </summary>
    static string DetectTimezone(long serverTimeEpoch)
    {
        if (serverTimeEpoch == 0) return "UTC";

        var serverTime = DateTimeOffset.FromUnixTimeSeconds(serverTimeEpoch);
        var utcNow = DateTimeOffset.UtcNow;
        var offsetHours = (int)Math.Round((serverTime - utcNow).TotalHours);

        return offsetHours switch
        {
            2 or 3 => "E. Europe Standard Time",
            0 => "UTC",
            1 => "W. Europe Standard Time",
            -4 or -5 => "Eastern Standard Time",
            -6 or -7 => "Central Standard Time",
            9 => "Tokyo Standard Time",
            10 or 11 => "AUS Eastern Standard Time",
            _ => "UTC",
        };
    }

    static string? FindConfig()
    {
        if (File.Exists("config.json")) return "config.json";
        var path = Path.Combine(AppContext.BaseDirectory, "config.json");
        return File.Exists(path) ? path : null;
    }
}
