using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Collections.Concurrent;
using Daemon.Config;
using Daemon.Connector;
using Daemon.Engine;
using Daemon.Models;
using Daemon.Strategy;

namespace Daemon.Dashboard;

public partial class DashboardServer
{
 // -------------------------------------------------------------------
 // get_terminals  â€” full terminal state snapshot
 // -------------------------------------------------------------------

    private async Task<object> HandleGetTerminalsAsync()
    {
        var terminals = new List<object>();

        // Sort terminals by sort_order from config
        var sortedTermIds = _config.Terminals
            .OrderBy(t => t.SortOrder)
            .ThenBy(t => t.Id)
            .Select(t => t.Id)
            .ToList();

        // Batch-load all profiles in a single DB call (instead of N × GetProfile)
        var allProfiles = _state.GetAllProfiles().ToDictionary(p => p.TerminalId);

        // Pre-compute news data once for all terminals (instead of per-terminal)
        var allNewsEvents = _news.GetAllEvents();
        var now = DateTime.UtcNow;

        foreach (var termId in sortedTermIds)
        {
            var termConfig = _config.Terminals.FirstOrDefault(t => t.Id == termId);
            if (termConfig == null) continue;

            var profile = allProfiles.GetValueOrDefault(termId);
            var enabled = termConfig.Enabled;
            var connected = enabled && _connector.IsConnected(termId);
            var (sl3Count, sl3Blocked) = _state.Get3SLState(termId);

            // Use broker timezone for "today" (Bug #6 fix)
            var brokerToday = GetBrokerDate(profile?.ServerTimezone ?? "UTC");
            var (dailyRealized, hwm, ddSnap) = _state.GetDailyPnl(termId, brokerToday);
            double effectiveDailyLimit = ddSnap > 0 ? ddSnap : (profile?.DailyDDLimit ?? 5000);

            AccountInfo? acc = null;
            double filteredUnrealized = 0;  // Phase 10: magic-filtered for real mode
            if (connected)
            {
                try { acc = await _connector.GetAccountInfoAsync(termId, CancellationToken.None); }
                catch { }

                // Phase 10: get unrealized PnL filtered by this terminal's magic numbers
                if (profile?.Mode != "virtual")
                {
                    try { filteredUnrealized = await _connector.GetFilteredUnrealizedPnlAsync(termId, CancellationToken.None); }
                    catch { filteredUnrealized = acc?.Profit ?? 0; }
                }

                // Auto-detect leverage per class on first dashboard load
                if (!_leverageDetectedAt.ContainsKey(termId))
                {
                    var tid = termId;
                    _ = Task.Run(async () =>
                    {
                        try { await DetectLeverageAsync(tid, CancellationToken.None); }
                        catch { }
                    });
                }
            }

 // News status — use pre-loaded events (computed once, not per terminal)
            string? nextNewsDesc = null;
            bool newsBlocked = false;
            string? activeNewsDesc = null;  // Currently active event in window (for tile display)
            {
                int windowMin = profile?.NewsWindowMin ?? 15;
                int minImpact = profile?.NewsMinImpact ?? 2;

                // Find next upcoming event (filtered by min impact)
                var nextNews = allNewsEvents
                    .Where(e => e.TimeUtc > now && e.Impact >= minImpact)
                    .OrderBy(e => e.TimeUtc)
                    .FirstOrDefault();
                if (nextNews != null)
                {
                    var delta = nextNews.TimeUtc - now;
                    string impactIcon = nextNews.Impact >= 3 ? "\ud83d\udfe5" : nextNews.Impact >= 2 ? "\ud83d\udfe8" : "\u2b1c";
                    nextNewsDesc = $"{impactIcon} {nextNews.Currency}: {nextNews.Title} in {FormatDuration(delta)}";
                }

                // Check if trading is actively blocked (only meaningful when guard ON)
                // Use IsBlockedGlobal — checks ALL currencies within the window at this minImpact
                if (profile?.NewsGuardOn == true)
                {
                    var blockResult = _news.IsBlockedGlobal(windowMin, minImpact);
                    newsBlocked = blockResult.Blocked;
                }

                // Find active events in window matching minImpact — shown even when guard OFF
                var activeEvents = allNewsEvents
                    .Where(e => e.Impact >= minImpact
                        && Math.Abs((e.TimeUtc - now).TotalMinutes) <= windowMin)
                    .OrderByDescending(e => e.Impact)
                    .ThenBy(e => Math.Abs((e.TimeUtc - now).TotalMinutes))
                    .ToList();
                if (activeEvents.Count > 0)
                {
                    var e = activeEvents[0];
                    int minTo = (int)Math.Round((e.TimeUtc - now).TotalMinutes);
                    string timing = minTo > 0 ? $"in {minTo}m" : minTo == 0 ? "NOW" : $"{-minTo}m ago";
                    activeNewsDesc = $"{e.Currency}: {e.Title} ({timing})";
                    if (activeEvents.Count > 1)
                        activeNewsDesc += $" +{activeEvents.Count - 1} more";
                }
            }

 // No-trade window status
            bool noTradeActive = false;
            string? noTradeDesc = null;
            if (!string.IsNullOrEmpty(profile?.NoTradeStart) && !string.IsNullOrEmpty(profile?.NoTradeEnd))
            {
                noTradeDesc = $"{profile.NoTradeStart}–{profile.NoTradeEnd}";
                if (profile.NoTradeOn)
                {
                    var g10 = RiskManager.CheckTradingHours(profile);
                    noTradeActive = !g10.Allowed;
                }
            }

            // Daily DD: virtual uses HWM-based, real uses P/L-based (magic-filtered)
            double dailyDD = 0;
            {
                if (profile?.Mode == "virtual")
                {
                    // Virtual: HWM − current virtual equity
                    double vBal = profile.VirtualBalance ?? 0;
                    double vUnrealized = _cachedVirtualUnrealized.TryGetValue(termId, out var vu2) ? vu2
                        : _virtualTracker?.GetUnrealizedPnl(termId) ?? 0;
                    double currentEquity = vBal + vUnrealized;
                    if (hwm > 0 && currentEquity > 0)
                        dailyDD = Math.Max(0, hwm - currentEquity);
                }
                else
                {
                    // Real: P/L-based DD with magic-filtered unrealized
                    double totalPnl = dailyRealized + filteredUnrealized;
                    dailyDD = totalPnl < 0 ? Math.Abs(totalPnl) : 0;
                }
            }

            // Deposit load: Margin / Equity * 100 (virtual-aware)
            double depositLoad;
            if (profile?.Mode == "virtual")
            {
                double vBal = profile.VirtualBalance ?? 0;
                double vUnr = _cachedVirtualUnrealized.TryGetValue(termId, out var vu3) ? vu3
                    : _virtualTracker?.GetUnrealizedPnl(termId) ?? 0;
                double vEquity = vBal + vUnr;
                double vMargin = _state.GetVirtualMargin(termId);
                depositLoad = vEquity > 0 ? vMargin / vEquity * 100 : 0;
            }
            else
            {
                depositLoad = acc != null && acc.Equity > 0 ? acc.Margin / acc.Equity * 100 : 0;
            }

            terminals.Add(new
            {
                id = termId,
                type = profile?.Type ?? "unknown",
                accountType = profile?.AccountType ?? "unknown",
                status = !enabled ? "disabled" : !connected ? "disconnected" : acc != null ? "connected" : "error",
                enabled,
                sortOrder = termConfig.SortOrder,
                mode = profile?.Mode ?? "monitor",
                terminalPath = termConfig.TerminalPath,

 // Account data (live from MT5)
                account = acc?.Login ?? 0,
                balance = acc?.Balance ?? 0,
                equity = acc?.Equity ?? 0,
                margin = acc?.Margin ?? 0,
                marginFree = acc?.MarginFree ?? 0,
                profit = acc?.Profit ?? 0,
                currency = acc?.Currency ?? "USD",
                leverage = acc?.Leverage ?? 0,
                leverageByClass = _classLeverage.TryGetValue(termId, out var lbc) ? lbc : null,
                server = "",

 // Phase 9.V: Virtual trading data (use already-loaded profile — no extra DB calls)
                virtualBalance = profile?.Mode == "virtual" ? profile.VirtualBalance : null,
                virtualUnrealized = profile?.Mode == "virtual"
                    ? (_cachedVirtualUnrealized.TryGetValue(termId, out var cachedVU) ? cachedVU
                        : _virtualTracker?.GetUnrealizedPnl(termId) ?? 0)
                    : (double?)null,
                virtualEquity = profile?.Mode == "virtual"
                    ? (profile.VirtualBalance ?? 0) +
                      (_cachedVirtualUnrealized.TryGetValue(termId, out var cachedVE) ? cachedVE
                        : _virtualTracker?.GetUnrealizedPnl(termId) ?? 0)
                    : (double?)null,
                virtualPositions = profile?.Mode == "virtual"
                    ? _state.GetOpenVirtualPositions(termId).Count
                    : (int?)null,

 // DD limits and current state
                // Daily DD = max(0, intraday_HWM − current_equity)
                // HWM is updated every cycle by Scheduler.MonitorDailyDDAsync
                dailyPnl = dailyDD,
                dailyLimit = effectiveDailyLimit,
                dailyDdMode = profile?.DailyDdMode ?? "hard",
                dailyDdPercent = profile?.DailyDdPercent ?? 0,
                dailyDdSnapshot = ddSnap,
                rCapOn = profile?.RCapOn ?? false,
                rCapLimit = profile?.RCapLimit ?? 0,
                rCapConfigDefault = _strategyMgr.GetEffectiveRCapForTerminal(termId),
                rCapReached = IsRCapReached(termId, profile, brokerToday),
 cumPnl = 0.0, // TODO: compute from HWM
                cumLimit = profile?.CumDDLimit ?? 10000,
 marginUsed = depositLoad,
                maxMargin = profile?.MaxDepositLoad ?? 50,

 // Guards
                guards = new
                {
                    news = profile?.NewsGuardOn ?? true,
                    sl3 = profile?.Sl3GuardOn ?? true,
                    newsBlock = newsBlocked,
                    sl3Count,
                    sl3Blocked,
                    nextNews = nextNewsDesc,
                    activeNews = activeNewsDesc,
                    noTradeOn = profile?.NoTradeOn ?? true,
                    noTradeActive,
                    noTradeDesc
                },

 // Profile settings (for config panel)
                profile = profile == null ? null : new
                {
                    dailyDDLimit = profile.DailyDDLimit,
                    dailyDdMode = profile.DailyDdMode,
                    dailyDdPercent = profile.DailyDdPercent,
                    cumDDLimit = profile.CumDDLimit,
                    maxRiskTrade = profile.MaxRiskTrade,
                    riskType = profile.RiskType,
                    maxMarginTrade = profile.MaxMarginTrade,
                    marginTradeMode = profile.MarginTradeMode,
                    maxDepositLoad = profile.MaxDepositLoad,
                    newsGuardOn = profile.NewsGuardOn,
                    newsWindowMin = profile.NewsWindowMin,
                    newsMinImpact = profile.NewsMinImpact,
                    newsBeEnabled = profile.NewsBeEnabled,
                    newsIncludeUsd = profile.NewsIncludeUsd,
                    sl3GuardOn = profile.Sl3GuardOn,
                    volumeMode = profile.VolumeMode,
                    serverTimezone = profile.ServerTimezone,
                    noTradeStart = profile.NoTradeStart,
                    noTradeEnd = profile.NoTradeEnd,
                    noTradeOn = profile.NoTradeOn,
                    rCapOn = profile.RCapOn,
                    rCapLimit = profile.RCapLimit
                }
            });
        }

        // Global pause state
        bool globalPaused = false;
        string? pauseUntil = null;
        string? pauseReason = null;
        if (_riskManager != null)
        {
            var (p, u, r) = _riskManager.GetPauseState();
            globalPaused = p;
            pauseUntil = u?.ToString("o");
            pauseReason = r;
        }

        return new { cmd = "terminals", data = terminals,
            globalPaused, pauseUntil, pauseReason };
    }

    private object HandleSaveProfile(JsonElement root)
    {
        var terminal = root.GetProperty("terminal").GetString()!;
        var profileData = root.GetProperty("profile");

        var existing = _state.GetProfile(terminal);
        if (existing == null)
            return new { cmd = "save_profile", ok = false, error = "Terminal not found" };

 // Apply changes from dashboard
        if (profileData.TryGetProperty("type", out var v)) existing.Type = v.GetString()!;
        if (profileData.TryGetProperty("mode", out v)) existing.Mode = v.GetString()!;
        if (profileData.TryGetProperty("dailyDDLimit", out v)) existing.DailyDDLimit = v.GetDouble();
        if (profileData.TryGetProperty("dailyDdMode", out v)) existing.DailyDdMode = v.GetString()!;
        if (profileData.TryGetProperty("dailyDdPercent", out v)) existing.DailyDdPercent = v.GetDouble();
        if (profileData.TryGetProperty("cumDDLimit", out v)) existing.CumDDLimit = v.GetDouble();
        if (profileData.TryGetProperty("maxRiskTrade", out v)) existing.MaxRiskTrade = v.GetDouble();
        if (profileData.TryGetProperty("riskType", out v)) existing.RiskType = v.GetString()!;
        if (profileData.TryGetProperty("maxMarginTrade", out v)) existing.MaxMarginTrade = v.GetDouble();
        if (profileData.TryGetProperty("marginTradeMode", out v)) existing.MarginTradeMode = v.GetString()!;
        if (profileData.TryGetProperty("maxDepositLoad", out v)) existing.MaxDepositLoad = v.GetDouble();
        if (profileData.TryGetProperty("newsGuardOn", out v)) existing.NewsGuardOn = v.GetBoolean();
        if (profileData.TryGetProperty("newsWindowMin", out v)) existing.NewsWindowMin = v.GetInt32();
        if (profileData.TryGetProperty("newsMinImpact", out v)) existing.NewsMinImpact = v.GetInt32();
        if (profileData.TryGetProperty("newsBeEnabled", out v)) existing.NewsBeEnabled = v.GetBoolean();
        if (profileData.TryGetProperty("sl3GuardOn", out v)) existing.Sl3GuardOn = v.GetBoolean();
        if (profileData.TryGetProperty("volumeMode", out v)) existing.VolumeMode = v.GetString()!;
        if (profileData.TryGetProperty("serverTimezone", out v)) existing.ServerTimezone = v.GetString()!;
        if (profileData.TryGetProperty("noTradeStart", out v))
            existing.NoTradeStart = v.ValueKind == JsonValueKind.Null || v.GetString() == "" ? null : v.GetString();
        if (profileData.TryGetProperty("noTradeEnd", out v))
            existing.NoTradeEnd = v.ValueKind == JsonValueKind.Null || v.GetString() == "" ? null : v.GetString();
        if (profileData.TryGetProperty("noTradeOn", out v)) existing.NoTradeOn = v.GetBoolean();
        if (profileData.TryGetProperty("rCapOn", out v)) existing.RCapOn = v.GetBoolean();
        if (profileData.TryGetProperty("rCapLimit", out v)) existing.RCapLimit = v.GetDouble();

        _state.SaveProfile(existing);

        _state.LogEvent("CONFIG", terminal, null,
            "Profile updated from dashboard",
            JsonSerializer.Serialize(profileData));

        _state.LogEvent("AUDIT", terminal, null,
            "save_profile",
            JsonSerializer.Serialize(profileData));

        _log.Info($"[Dashboard] Profile {terminal} updated");

        return new { cmd = "save_profile", ok = true, terminal };
    }

 // -------------------------------------------------------------------
 // unblock_3sl / toggle_news_guard / set_mode
 // -------------------------------------------------------------------


    private object HandleUnblock3SL(JsonElement root)
    {
        var terminal = root.GetProperty("terminal").GetString()!;
        _state.Unblock3SL(terminal);

        _state.LogEvent("RISK", terminal, null, "3SL guard unblocked from dashboard");
        _state.LogEvent("AUDIT", terminal, null, "unblock_3sl");
        _log.Info($"[Dashboard] {terminal}: 3SL unblocked");

        return new { cmd = "unblock_3sl", ok = true, terminal };
    }


    private object HandleToggleNewsGuard(JsonElement root)
    {
        var terminal = root.GetProperty("terminal").GetString()!;
        var profile = _state.GetProfile(terminal);
        if (profile == null)
            return new { cmd = "toggle_news_guard", ok = false, error = "Terminal not found" };

        profile.NewsGuardOn = !profile.NewsGuardOn;
        _state.SaveProfile(profile);

        _state.LogEvent("CONFIG", terminal, null,
            $"News guard {(profile.NewsGuardOn ? "enabled" : "disabled")} from dashboard");
        _state.LogEvent("AUDIT", terminal, null,
            $"toggle_news_guard: {(profile.NewsGuardOn ? "ON" : "OFF")}");
        _log.Info($"[Dashboard] {terminal}: News guard  â†’  {(profile.NewsGuardOn ? "ON" : "OFF")}");

        return new { cmd = "toggle_news_guard", ok = true, terminal, enabled = profile.NewsGuardOn };
    }


    private object HandleToggleNoTrade(JsonElement root)
    {
        var terminal = root.GetProperty("terminal").GetString()!;
        var profile = _state.GetProfile(terminal);
        if (profile == null)
            return new { cmd = "toggle_no_trade", ok = false, error = "Terminal not found" };

        profile.NoTradeOn = !profile.NoTradeOn;
        _state.SaveProfile(profile);

        _state.LogEvent("CONFIG", terminal, null,
            $"No-trade hours {(profile.NoTradeOn ? "enabled" : "disabled")} from dashboard");
        _state.LogEvent("AUDIT", terminal, null,
            $"toggle_no_trade: {(profile.NoTradeOn ? "ON" : "OFF")}");
        _log.Info($"[Dashboard] {terminal}: No-trade hours -> {(profile.NoTradeOn ? "ON" : "OFF")}");

        return new { cmd = "toggle_no_trade", ok = true, terminal, enabled = profile.NoTradeOn };
    }



    private object HandleToggle3SLGuard(JsonElement root)
    {
        var terminal = root.GetProperty("terminal").GetString()!;
        var profile = _state.GetProfile(terminal);
        if (profile == null)
            return new { cmd = "toggle_3sl_guard", ok = false, error = "Terminal not found" };

        profile.Sl3GuardOn = !profile.Sl3GuardOn;
        _state.SaveProfile(profile);

        _state.LogEvent("CONFIG", terminal, null,
            $"3SL guard {(profile.Sl3GuardOn ? "enabled" : "disabled")} from dashboard");
        _state.LogEvent("AUDIT", terminal, null,
            $"toggle_3sl_guard: {(profile.Sl3GuardOn ? "ON" : "OFF")}");
        _log.Info($"[Dashboard] {terminal}: 3SL guard {(profile.Sl3GuardOn ? "ON" : "OFF")}");

        return new { cmd = "toggle_3sl_guard", ok = true, terminal, enabled = profile.Sl3GuardOn };
    }


    private object HandleSetMode(JsonElement root)
    {
        var terminal = root.GetProperty("terminal").GetString()!;
        var mode = root.GetProperty("mode").GetString()!;

        if (mode != "auto" && mode != "semi" && mode != "monitor" && mode != "virtual")
            return new { cmd = "set_mode", ok = false, error = $"Invalid mode: {mode}" };

        var profile = _state.GetProfile(terminal);
        if (profile == null)
            return new { cmd = "set_mode", ok = false, error = "Terminal not found" };

        var oldMode = profile.Mode;

        // Phase 9.V: Close virtual positions when leaving virtual mode
        if (oldMode == "virtual" && mode != "virtual")
        {
            var openVirtual = _state.GetOpenVirtualPositions(terminal);
            foreach (var pos in openVirtual)
            {
                var tf = pos.Timeframe ?? "H1";
                var bars = _barsCache?.GetBars(terminal, pos.Symbol, tf);
                double closePrice = bars is { Count: > 0 } ? bars[^1].Close : pos.PriceOpen;

                if (pos.Direction == "SELL" && bars is { Count: > 0 })
                {
                    double spreadEst = pos.Symbol.Contains("JPY") ? 0.03 : 0.00020;
                    closePrice += spreadEst;
                }

                double pnl = 0;
                if (_virtualTracker != null)
                    pnl = _virtualTracker.CalculateVirtualPnl(pos, closePrice, terminal);
                else
                {
                    double dirSign = pos.Direction == "BUY" ? 1 : -1;
                    double priceDiff = dirSign * (closePrice - pos.PriceOpen);
                    bool isCrypto = new[] { "BTC", "ETH", "LTC", "XRP", "ADA", "SOL", "DOT", "DOGE" }
                        .Any(c => pos.Symbol.StartsWith(c, StringComparison.OrdinalIgnoreCase));
                    if (isCrypto)
                        pnl = priceDiff * pos.Volume;
                    else
                    {
                        double tickSize = pos.Symbol.Contains("JPY") ? 0.001 : 0.00001;
                        double tickValue = pos.Symbol.Contains("JPY") ? 0.01 : 0.10;
                        pnl = priceDiff / tickSize * tickValue * pos.Volume;
                    }
                }
                var commPerLot = profile.CommissionPerLot > 0 ? profile.CommissionPerLot : _config.DefaultCommissionPerLot;
                pnl -= commPerLot * pos.Volume;

                _state.ClosePosition(pos.Ticket, terminal, closePrice, "mode_change", pnl);
                _state.UpdateVirtualBalance(terminal, pnl);
            }
            _state.SetVirtualMargin(terminal, 0);
            _state.LogEvent("CONFIG", terminal, null,
                $"Virtual->{mode}: closed {openVirtual.Count} virtual positions");
        }

        profile.Mode = mode;
        _state.SaveProfile(profile);

        // Phase 9.V: Auto-initialize virtual balance when entering virtual mode
        if (mode == "virtual" && oldMode != "virtual")
        {
            var existingBal = _state.GetVirtualBalance(terminal);
            if (existingBal == null || existingBal <= 0)
            {
                // Use real account balance as starting virtual balance
                double startBalance = 100000; // default
                try
                {
                    var acc = _connector.GetAccountInfoAsync(terminal, CancellationToken.None).Result;
                    if (acc != null) startBalance = acc.Balance;
                }
                catch { /* use default */ }
                _state.InitVirtualBalance(terminal, startBalance);
                _log.Info($"[Dashboard] {terminal}: Virtual balance initialized at ${startBalance:F2}");
            }
        }

        _state.LogEvent("CONFIG", terminal, null, $"Mode set to {mode} from dashboard");
        _state.LogEvent("AUDIT", terminal, null, $"set_mode: {mode}");
        _log.Info($"[Dashboard] {terminal}: mode -> {mode}");

        return new { cmd = "set_mode", ok = true, terminal, mode };
    }

 // -------------------------------------------------------------------
 // start_terminal  â€” " launch MT5 terminal process
 // -------------------------------------------------------------------


    private object HandleStartTerminal(JsonElement root)
    {
        var terminal = root.GetProperty("terminal").GetString()!;

        var termConfig = _config.Terminals.FirstOrDefault(t => t.Id == terminal);
        if (termConfig == null)
            return new { cmd = "start_terminal", ok = false, error = "Terminal not found in config" };

        var termPath = termConfig.TerminalPath;
        if (string.IsNullOrEmpty(termPath) || !File.Exists(termPath))
            return new { cmd = "start_terminal", ok = false,
                error = $"Terminal executable not found: {termPath}" };

 // Check if MT5 terminal process is already running
        bool alreadyRunning = false;
        try
        {
            var exeName = Path.GetFileNameWithoutExtension(termPath);
            alreadyRunning = System.Diagnostics.Process.GetProcessesByName(exeName)
                .Any(p => {
                    try { return p.MainModule?.FileName?.Equals(termPath, StringComparison.OrdinalIgnoreCase) == true; }
                    catch { return false; }
                    finally { p.Dispose(); }
                });
        }
        catch { }

        try
        {
            if (!alreadyRunning)
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = termPath,
                    UseShellExecute = true
                });
                _log.Info($"[Dashboard] Launched MT5 terminal: {termPath}");
            }
            else
            {
                _log.Info($"[Dashboard] MT5 already running, reconnecting worker: {terminal}");
            }

            _state.LogEvent("SYSTEM", terminal, null,
                alreadyRunning ? "Reconnecting worker (MT5 already running)" : "Terminal launched from dashboard");

 // Broadcast starting status
            _ = BroadcastAsync(new
            {
                @event = "terminal_status",
                data = new { id = terminal, status = "connecting" }
            });

 // Schedule delayed worker restart (MT5 needs time to initialize)
            var delayMs = alreadyRunning ? 3000 : 12000;
            _ = Task.Run(async () =>
            {
                try
                {
                    _log.Info($"[{terminal}] Waiting {delayMs / 1000}s for MT5 to initialize before worker restart...");
                    await Task.Delay(delayMs);
                    await _connector.RestartWorkerAsync(terminal);

                    _state.LogEvent("SYSTEM", terminal, null, "Worker reconnected after terminal start");

                    await BroadcastAsync(new
                    {
                        @event = "terminal_status",
                        data = new { id = terminal, status = "connected" }
                    });
                }
                catch (Exception ex)
                {
                    _log.Error($"[{terminal}] Worker restart failed: {ex.Message}");
                    _state.LogEvent("ERROR", terminal, null, $"Worker restart failed: {ex.Message}");

                    await BroadcastAsync(new
                    {
                        @event = "terminal_status",
                        data = new { id = terminal, status = "error" }
                    });
                }
            });

            return new { cmd = "start_terminal", ok = true, terminal };
        }
        catch (Exception ex)
        {
            _log.Error($"[Dashboard] Failed to launch {termPath}: {ex.Message}");
            return new { cmd = "start_terminal", ok = false, error = ex.Message };
        }
    }


 // -------------------------------------------------------------------
 // toggle_pause  — global trading pause
 // -------------------------------------------------------------------


    private object HandleResetFlags(JsonElement root)
    {
        var terminal = root.GetProperty("terminal").GetString()!;

        var profile = _state.GetProfile(terminal);
        if (profile == null)
            return new { cmd = "reset_flags", ok = false, error = "Terminal not found" };

        _state.ResetBlockingFlags(terminal);
        _state.LogEvent("CONFIG", terminal, null, "Blocking flags reset (daily_r, sl3, daily_pnl)");
        _log.Info($"[Dashboard] {terminal}: blocking flags reset");

        return new { cmd = "reset_flags", ok = true, terminal };
    }

 // -------------------------------------------------------------------
 // export_virtual_csv -- export closed virtual trades as CSV
 // -------------------------------------------------------------------


    private async Task<object> HandleDiscoverTerminals(CancellationToken ct)
    {
        _log.Info("[Dashboard] Scanning for MT5 terminals...");

        var results = new List<object>();
        var configuredPaths = _config.Terminals
            .Select(t => t.TerminalPath.ToLowerInvariant())
            .ToHashSet();
        var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

 // Scan for common MT5 process names
        var processNames = new[] { "terminal64", "terminal", "metatrader64", "metatrader" };
        var allProcesses = new List<System.Diagnostics.Process>();
        foreach (var pname in processNames)
        {
            try { allProcesses.AddRange(System.Diagnostics.Process.GetProcessesByName(pname)); }
            catch { /* ignore */ }
        }

        _log.Info($"[Dashboard] Found {allProcesses.Count} MT5-like process(es)");

 // Build PID -> exe path map. First try MainModule, then WMI fallback.
        var pidPaths = new Dictionary<int, string>();
        var failedPids = new List<int>();

        foreach (var proc in allProcesses)
        {
            try
            {
                var path = proc.MainModule?.FileName;
                if (!string.IsNullOrEmpty(path))
                    pidPaths[proc.Id] = path;
                else
                    failedPids.Add(proc.Id);
            }
            catch
            {
                failedPids.Add(proc.Id);
            }
        }

        // WMI fallback for processes where MainModule was inaccessible (AccessDenied)
        if (failedPids.Count > 0 && OperatingSystem.IsWindows())
        {
            _log.Info($"[Dashboard] MainModule failed for {failedPids.Count} process(es), trying WMI fallback...");
            try
            {
                var pidFilter = string.Join(" OR ", failedPids.Select(p => $"ProcessId={p}"));
                var psi = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "wmic",
                    Arguments = $"process where \"{pidFilter}\" get ProcessId,ExecutablePath /format:csv",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };
                using var wmiProc = System.Diagnostics.Process.Start(psi);
                if (wmiProc != null)
                {
                    var output = await wmiProc.StandardOutput.ReadToEndAsync(ct);
                    await wmiProc.WaitForExitAsync(ct);

                    // Parse CSV: Node,ExecutablePath,ProcessId
                    foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
                    {
                        var parts = line.Trim().Split(',');
                        if (parts.Length >= 3 && int.TryParse(parts[^1].Trim(), out var pid))
                        {
                            var exePath = parts[^2].Trim();
                            if (!string.IsNullOrEmpty(exePath) && exePath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                                pidPaths.TryAdd(pid, exePath);
                        }
                    }

                    _log.Info($"[Dashboard] WMI recovered {pidPaths.Count - (allProcesses.Count - failedPids.Count)} additional path(s)");
                }
            }
            catch (Exception ex)
            {
                _log.Warn($"[Dashboard] WMI fallback failed: {ex.Message}");
            }
        }

        // Probe each unique path
        foreach (var exePath in pidPaths.Values)
        {
            if (!seenPaths.Add(exePath)) continue; // deduplicate

            var probeResult = await RunProbeAsync(exePath, ct);
            if (probeResult == null) continue;

            bool alreadyConfigured = configuredPaths.Contains(exePath.ToLowerInvariant());
            string? configuredAs = alreadyConfigured
                ? _config.Terminals.FirstOrDefault(
                    t => t.TerminalPath.Equals(exePath, StringComparison.OrdinalIgnoreCase))?.Id
                : null;

            results.Add(new
            {
                path = exePath,
                status = probeResult.Status,
                company = probeResult.Company,
                name = probeResult.Name,
                login = probeResult.Login,
                server = probeResult.Server,
                balance = probeResult.Balance,
                equity = probeResult.Equity,
                leverage = probeResult.Leverage,
                currency = probeResult.Currency,
                tradeMode = probeResult.TradeMode,
                marginMode = probeResult.MarginMode,
                connected = probeResult.Connected,
                error = probeResult.Error,
                alreadyConfigured,
                configuredAs
            });
        }

        int unreachable = failedPids.Count - pidPaths.Keys.Intersect(failedPids).Count();
        if (unreachable > 0)
            _log.Warn($"[Dashboard] {unreachable} MT5 process(es) could not be accessed - try running daemon as Administrator");

        _log.Info($"[Dashboard] Discovery complete: {results.Count} terminal(s) found");

        return new { cmd = "discover_terminals", data = results, unreachable };
    }

 // -------------------------------------------------------------------
 // probe_terminal -- probe a manually-specified terminal path
 // -------------------------------------------------------------------


    private async Task<object> HandleProbeTerminal(JsonElement root, CancellationToken ct)
    {
        var path = root.GetProperty("path").GetString()!;

        // Basic validation
        if (string.IsNullOrWhiteSpace(path))
            return new { cmd = "probe_terminal", ok = false, error = "Path is empty" };

        path = path.Trim().Trim('"'); // Remove quotes that users might paste

        if (!File.Exists(path))
            return new { cmd = "probe_terminal", ok = false, error = $"File not found: {path}" };

        var fileName = Path.GetFileName(path).ToLowerInvariant();
        if (!fileName.Contains("terminal") && !fileName.Contains("metatrader"))
            return new { cmd = "probe_terminal", ok = false,
                error = $"Doesn't look like an MT5 terminal: {fileName}" };

        _log.Info($"[Dashboard] Manual probe: {path}");

        var probeResult = await RunProbeAsync(path, ct);
        if (probeResult == null)
            return new { cmd = "probe_terminal", ok = false,
                error = "Probe failed -- is the terminal running and logged in?" };

        var configuredPaths = _config.Terminals
            .Select(t => t.TerminalPath.ToLowerInvariant())
            .ToHashSet();

        bool alreadyConfigured = configuredPaths.Contains(path.ToLowerInvariant());
        string? configuredAs = alreadyConfigured
            ? _config.Terminals.FirstOrDefault(
                t => t.TerminalPath.Equals(path, StringComparison.OrdinalIgnoreCase))?.Id
            : null;

        return new
        {
            cmd = "probe_terminal",
            ok = true,
            data = new
            {
                path,
                status = probeResult.Status,
                company = probeResult.Company,
                name = probeResult.Name,
                login = probeResult.Login,
                server = probeResult.Server,
                balance = probeResult.Balance,
                equity = probeResult.Equity,
                leverage = probeResult.Leverage,
                currency = probeResult.Currency,
                tradeMode = probeResult.TradeMode,
                marginMode = probeResult.MarginMode,
                connected = probeResult.Connected,
                error = probeResult.Error,
                alreadyConfigured,
                configuredAs
            }
        };
    }

    // -------------------------------------------------------------------
    //  detect_leverage â€” auto-detect effective leverage per asset class
    // -------------------------------------------------------------------


    private async Task<object> HandleDetectLeverage(JsonElement root, CancellationToken ct)
    {
        var terminal = root.GetProperty("terminal").GetString()!;

        if (!_connector.IsConnected(terminal))
            return new { cmd = "detect_leverage", ok = false, error = "Terminal disconnected" };

        var leverage = await DetectLeverageAsync(terminal, ct);

        return new { cmd = "detect_leverage", ok = true, terminal, leverageByClass = leverage };
    }

    /// <summary>Detect effective leverage per asset class.
    /// Sends representative symbols to worker; worker auto-discovers alternatives
    /// if symbols don't exist on the broker. FX uses account_leverage.</summary>
    private async Task<Dictionary<string, int>> DetectLeverageAsync(string terminalId, CancellationToken ct)
    {
        try
        {
            // Use fixed representative symbols per class
            // Worker handles: FXâ†’account_leverage, non-FXâ†’auto-discovery if needed
            var classSymbols = new Dictionary<string, string>(DefaultLeverageSymbols);

            _log.Info($"[Leverage] Detecting for {terminalId}: {string.Join(", ", classSymbols.Select(kv => $"{kv.Key}={kv.Value}"))}");

            var result = await _connector.CalcLeverageAsync(terminalId, classSymbols, ct);

            if (result.Count > 0)
            {
                _classLeverage[terminalId] = result;
                _leverageDetectedAt[terminalId] = DateTime.UtcNow;
                _state.SaveClassLeverage(terminalId, result);
                _log.Info($"[Leverage] {terminalId}: {string.Join(", ", result.Select(kv => $"{kv.Key} 1:{kv.Value}"))}");
            }
            else
            {
                _log.Warn($"[Leverage] {terminalId}: no leverage data returned");
            }

            return result;
        }
        catch (Exception ex)
        {
            _log.Warn($"[Leverage] {terminalId}: detection failed: {ex.Message}");
            return new Dictionary<string, int>();
        }
    }

    private async Task<ProbeResult?> RunProbeAsync(string terminalPath, CancellationToken ct)
    {
        try
        {
 // probe_terminal.py sits next to mt5_worker.py in the workers/ folder
 // worker_script is relative to CWD (daemon/), so probe is in the same dir
            var workerDir = Path.GetDirectoryName(_config.WorkerScript);
            var probePath = Path.Combine(workerDir ?? ".", "probe_terminal.py");

            if (!File.Exists(probePath))
            {
                _log.Warn($"[Dashboard] probe_terminal.py not found at {probePath}");
                return null;
            }

            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = _config.PythonPath,
                Arguments = $"\"{probePath}\" \"{terminalPath}\"",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            using var process = System.Diagnostics.Process.Start(psi);
            if (process == null) return null;

            var output = await process.StandardOutput.ReadToEndAsync(ct);
            var stderr = await process.StandardError.ReadToEndAsync(ct);
            await process.WaitForExitAsync(ct);

            if (!string.IsNullOrWhiteSpace(stderr))
                _log.Warn($"[Dashboard] Probe stderr: {stderr.Trim()}");

            if (string.IsNullOrWhiteSpace(output)) return null;

            return JsonSerializer.Deserialize<ProbeResult>(output, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });
        }
        catch (Exception ex)
        {
            _log.Error($"[Dashboard] Probe failed for {terminalPath}: {ex.Message}");
            return null;
        }
    }

 // -------------------------------------------------------------------
 // add_discovered_terminal  â€” add a found terminal to config at runtime
 // -------------------------------------------------------------------


    private async Task<object> HandleAddDiscoveredTerminal(JsonElement root, CancellationToken ct)
    {
        var path = root.GetProperty("path").GetString()!;
        var name = root.GetProperty("name").GetString()!;

 // Check if already exists
        if (_config.Terminals.Any(t => t.Id == name))
            return new { cmd = "add_discovered_terminal", ok = false,
                         error = $"Terminal '{name}' already exists in config" };

 // Allocate next port
        var maxPort = _config.Terminals.Count > 0
            ? _config.Terminals.Max(t => t.Port) : 5500;
        var newPort = maxPort + 1;

 // Add to in-memory config
        var newTerminal = new TerminalConfig
        {
            Id = name,
            TerminalPath = path,
            Port = newPort,
            AutoConnect = true,
            SymbolMap = new()
        };
        _config.Terminals.Add(newTerminal);

 // Start worker for the new terminal
        try
        {
            await _connector.AddTerminalAsync(newTerminal, ct);
            _log.Info($"[Dashboard] Terminal '{name}' added and connecting on port {newPort}");

 // Create default profile
            var acc = await _connector.GetAccountInfoAsync(name, ct);
            if (acc != null)
            {
                _state.SaveProfile(new TerminalProfile
                {
                    TerminalId = name,
                    Type = acc.TradeMode == 0 ? "demo" : "real",
                    AccountType = acc.AccountType,
                    Mode = "monitor",
                    ServerTimezone = DetectTimezone(acc.ServerTime),
                });
            }

            _state.LogEvent("SYSTEM", name, null, $"Terminal added from dashboard: {path}");

            await BroadcastAsync(new
            {
                @event = "terminal_added",
                data = new { id = name, status = "connected", port = newPort }
            });

 // Persist terminal to config.json so it survives restart
            PersistConfig();
        }
        catch (Exception ex)
        {
            _log.Error($"[Dashboard] Failed to add terminal: {ex.Message}");
            return new { cmd = "add_discovered_terminal", ok = false, error = ex.Message };
        }

        return new { cmd = "add_discovered_terminal", ok = true, id = name, port = newPort };
    }

 // -------------------------------------------------------------------
 // DetectTimezone  â€” auto-detect broker timezone from server_time
 // -------------------------------------------------------------------

 /// <summary>
 /// Auto-detect broker timezone from server_time epoch vs UTC.
 /// Maps known offsets to Windows timezone IDs.
 /// </summary>

    private static string DetectTimezone(long serverTimeEpoch)
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


 // -------------------------------------------------------------------
 // PersistConfig  â€” save current DaemonConfig back to config.json
 // -------------------------------------------------------------------

    private void PersistConfig()
    {
        if (string.IsNullOrEmpty(_configPath)) return;
        try
        {
            var json = JsonSerializer.Serialize(_config, new JsonSerializerOptions
            {
                WriteIndented = true,
                DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
            });
            File.WriteAllText(_configPath, json);
            _log.Info($"[Dashboard] Config saved to {_configPath}");
        }
        catch (Exception ex)
        {
            _log.Error($"[Dashboard] Failed to save config: {ex.Message}");
        }
    }

 // -------------------------------------------------------------------
 // toggle_terminal_enabled â€” enable/disable terminal without deleting
 // -------------------------------------------------------------------


    private async Task<object> HandleToggleTerminalEnabled(JsonElement root, CancellationToken ct)
    {
        var terminal = root.GetProperty("terminal").GetString()!;
        var termConfig = _config.Terminals.FirstOrDefault(t => t.Id == terminal);
        if (termConfig == null)
            return new { cmd = "toggle_terminal_enabled", ok = false, error = "Terminal not found" };

        termConfig.Enabled = !termConfig.Enabled;
        var nowEnabled = termConfig.Enabled;

        if (!nowEnabled)
        {
            // Stop strategies on this terminal
            var processes = _strategyMgr.GetProcessesForTerminal(terminal);
            foreach (var p in processes)
            {
                try { await _strategyMgr.StopStrategyAsync(p.StrategyName, terminal, ct); }
                catch { }
            }

            // Stop worker
            try { await _connector.StopTerminalAsync(terminal, ct); }
            catch (Exception ex) { _log.Warn($"[Dashboard] Error stopping worker for {terminal}: {ex.Message}"); }

            _log.Info($"[Dashboard] Terminal {terminal} DISABLED");
        }
        else
        {
            // Re-start worker
            try
            {
                await _connector.AddTerminalAsync(termConfig, ct);
                _log.Info($"[Dashboard] Terminal {terminal} ENABLED and reconnecting");
            }
            catch (Exception ex)
            {
                _log.Error($"[Dashboard] Failed to re-enable {terminal}: {ex.Message}");
                return new { cmd = "toggle_terminal_enabled", ok = false, error = ex.Message };
            }
        }

        PersistConfig();

        _state.LogEvent("AUDIT", terminal, null,
            $"toggle_terminal_enabled: {(nowEnabled ? "ENABLED" : "DISABLED")}");

        await BroadcastAsync(new
        {
            @event = "terminal_status",
            data = new { id = terminal, status = nowEnabled ? "connecting" : "disabled", enabled = nowEnabled }
        });

        return new { cmd = "toggle_terminal_enabled", ok = true, terminal, enabled = nowEnabled };
    }

 // -------------------------------------------------------------------
 // delete_terminal â€” remove terminal and purge all DB data
 // -------------------------------------------------------------------


    private async Task<object> HandleDeleteTerminal(JsonElement root, CancellationToken ct)
    {
        var terminal = root.GetProperty("terminal").GetString()!;

        if (!root.TryGetProperty("confirm", out var confirmProp) || !confirmProp.GetBoolean())
            return new { cmd = "delete_terminal", ok = false, error = "Confirmation required" };

        var termConfig = _config.Terminals.FirstOrDefault(t => t.Id == terminal);
        if (termConfig == null)
            return new { cmd = "delete_terminal", ok = false, error = "Terminal not found" };

        _log.Warn($"[Dashboard] DELETING terminal {terminal} and all associated data");

        // 1. Stop strategies
        var processes = _strategyMgr.GetProcessesForTerminal(terminal);
        foreach (var p in processes)
        {
            try { await _strategyMgr.StopStrategyAsync(p.StrategyName, terminal, ct); }
            catch { }
        }

        // 2. Stop and remove worker
        try { await _connector.RemoveTerminalAsync(terminal, ct); }
        catch (Exception ex) { _log.Warn($"[Dashboard] Error removing worker: {ex.Message}"); }

        // 3. Remove from config
        _config.Terminals.RemoveAll(t => t.Id == terminal);

        // 4. Remove strategy assignments for this terminal
        _config.Strategies.RemoveAll(s => s.Terminal == terminal);

        // 5. Purge all DB data
        int deletedRows = _state.DeleteTerminalData(terminal);
        _log.Info($"[Dashboard] Deleted {deletedRows} DB rows for {terminal}");

        // 6. Persist config
        PersistConfig();

        // 7. Log (to events â€” terminal_id=null since terminal is gone)
        _state.LogEvent("AUDIT", null, null,
            $"delete_terminal: {terminal} removed, {deletedRows} DB rows purged");

        // 8. Broadcast
        await BroadcastAsync(new
        {
            @event = "terminal_deleted",
            data = new { id = terminal }
        });

        // 9. Remove from leverage cache
        _classLeverage.Remove(terminal);
        _leverageDetectedAt.Remove(terminal);

        return new { cmd = "delete_terminal", ok = true, terminal, deletedRows };
    }

 // -------------------------------------------------------------------
 // reorder_terminals â€” save new terminal display order
 // -------------------------------------------------------------------


    private object HandleReorderTerminals(JsonElement root)
    {
        if (!root.TryGetProperty("order", out var orderProp))
            return new { cmd = "reorder_terminals", ok = false, error = "Missing order array" };

        var order = new List<string>();
        foreach (var item in orderProp.EnumerateArray())
        {
            var id = item.GetString();
            if (id != null) order.Add(id);
        }

        for (int i = 0; i < order.Count; i++)
        {
            var tc = _config.Terminals.FirstOrDefault(t => t.Id == order[i]);
            if (tc != null) tc.SortOrder = i;
        }

        PersistConfig();

        _state.LogEvent("AUDIT", null, null,
            $"reorder_terminals: {string.Join(" â†’ ", order)}");

        _log.Info($"[Dashboard] Terminals reordered: {string.Join(", ", order)}");

        return new { cmd = "reorder_terminals", ok = true, order };
    }

 // -------------------------------------------------------------------
 // open_strategy_folder — open strategy directory in OS file explorer
 // -------------------------------------------------------------------


    private object HandleOpenStrategyFolder(JsonElement root)
    {
        var strategy = root.GetProperty("strategy").GetString()!;

        // Sanitize: prevent path traversal
        if (strategy.Contains("..") || strategy.Contains('/') || strategy.Contains('\\'))
            return new { cmd = "open_strategy_folder", ok = false, error = "Invalid strategy name" };

        var strategyDir = Path.GetFullPath(
            Path.Combine(_config.StrategyDir, strategy),
            AppContext.BaseDirectory);

        if (!Directory.Exists(strategyDir))
            return new { cmd = "open_strategy_folder", ok = false, error = $"Directory not found: {strategyDir}" };

        try
        {
            // Open in OS file explorer (Windows: explorer.exe, Linux: xdg-open)
            if (OperatingSystem.IsWindows())
                System.Diagnostics.Process.Start("explorer.exe", strategyDir);
            else if (OperatingSystem.IsLinux())
                System.Diagnostics.Process.Start("xdg-open", strategyDir);
            else if (OperatingSystem.IsMacOS())
                System.Diagnostics.Process.Start("open", strategyDir);

            _log.Info($"[Dashboard] Opened strategy folder: {strategyDir}");
            return new { cmd = "open_strategy_folder", ok = true, path = strategyDir };
        }
        catch (Exception ex)
        {
            return new { cmd = "open_strategy_folder", ok = false, error = ex.Message };
        }
    }

 // ===================================================================
 // NEW: Terminal detail  â€” positions + stats + equity curve
 // ===================================================================


    private object HandleGetTerminalDetail(JsonElement root)
    {
        var terminal = root.GetProperty("terminal").GetString()!;

 // Open positions (from DB for source info)
        var openPositions = _state.GetOpenPositions(terminal).Select(p => new
        {
            ticket = p.Ticket,
            symbol = p.Symbol,
            dir = p.Direction,
            volume = p.Volume,
            priceOpen = p.PriceOpen,
            sl = p.SL,
            tp = p.TP,
            source = p.Source,
            openedAt = p.OpenedAt
        }).ToList();

 // Closed positions (last 200)
        var closedPositions = GetClosedPositions(terminal, 200);

 // Stats
        var wins = closedPositions.Count(p => p.Pnl > 0);
        var losses = closedPositions.Count(p => p.Pnl <= 0);
        var totalPnl = closedPositions.Sum(p => p.Pnl);
        var avgWin = wins > 0 ? closedPositions.Where(p => p.Pnl > 0).Average(p => p.Pnl) : 0;
        var avgLoss = losses > 0 ? closedPositions.Where(p => p.Pnl <= 0).Average(p => p.Pnl) : 0;
 var winRate = closedPositions.Count > 0 ? (double)wins / closedPositions.Count * 100 : 0;

 // Equity curve  â€” cumulative P/L from closed positions
        double cumPnl = 0;
        var equityCurve = closedPositions
            .OrderBy(p => p.ClosedAt)
            .Select(p =>
            {
                cumPnl += p.Pnl;
                return new { date = p.ClosedAt, pnl = Math.Round(cumPnl, 2) };
            }).ToList();

 // Execution quality
        var (avgSlippage, avgLatency, execCount) = _state.GetExecutionStats(terminal);

        return new
        {
            cmd = "terminal_detail",
            terminal,
            openPositions,
            closedPositions = closedPositions.Select(p => new
            {
                ticket = p.Ticket,
                symbol = p.Symbol,
                dir = p.Direction,
                volume = p.Volume,
                priceOpen = p.PriceOpen,
                closePrice = p.ClosePrice,
                pnl = p.Pnl,
                closeReason = p.CloseReason,
                source = p.Source,
                openedAt = p.OpenedAt,
                closedAt = p.ClosedAt
            }),
            stats = new
            {
                totalTrades = closedPositions.Count,
                wins,
                losses,
                winRate = Math.Round(winRate, 1),
                totalPnl = Math.Round(totalPnl, 2),
                avgWin = Math.Round(avgWin, 2),
                avgLoss = Math.Round(avgLoss, 2),
                avgSlippage = Math.Round(avgSlippage, 2),
                avgLatencyMs = Math.Round(avgLatency, 0),
                execCount
            },
            equityCurve
        };
    }


}
