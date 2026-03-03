using Daemon.Connector;
using Daemon.Models;

namespace Daemon.Engine;

/// <summary>
/// Background process that monitors equity and enforces daily drawdown limits.
/// Runs every 10 seconds, checks each terminal's P/L against thresholds.
///
/// Thresholds:
///   80% of daily DD limit → YELLOW alert (Telegram + dashboard)
///   95% of daily DD limit → RED alert + optional strategy pause
///  100% of daily DD limit → EMERGENCY: close all positions, block trading
///
/// Also: if NewsService shows a block window and there's a profitable position,
/// move SL to break-even (entry ± 2*spread).
/// </summary>
public class ActiveProtection : IDisposable
{
    private readonly StateManager _state;
    private readonly ConnectorManager _connector;
    private readonly AlertService _alerts;
    private readonly NewsService _news;
    private readonly ConsoleLogger _log;

    // Phase 10: Virtual mode support for DD monitoring + news auto-BE
    private VirtualTracker? _virtualTracker;
    private BarsCache? _barsCache;

    private Timer? _timer;
    private readonly TimeSpan _interval = TimeSpan.FromSeconds(10);
    private bool _running;

    // Track alert levels to avoid re-alerting same level
    private readonly Dictionary<string, AlertLevel> _lastAlertLevel = new();

    // Track tickets already moved to BE by news guard (cleared daily)
    private readonly HashSet<long> _newsBeApplied = new();
    private string _newsBeDate = "";

    public ActiveProtection(StateManager state, ConnectorManager connector,
                             AlertService alerts, NewsService news, ConsoleLogger log)
    {
        _state = state;
        _connector = connector;
        _alerts = alerts;
        _news = news;
        _log = log;
    }

    /// <summary>Set VirtualTracker + BarsCache for virtual mode DD monitoring and news auto-BE.</summary>
    public void SetVirtualTracker(VirtualTracker vt, BarsCache bc)
    {
        _virtualTracker = vt;
        _barsCache = bc;
    }

    /// <summary>Start the background monitoring loop.</summary>
    public void Start()
    {
        if (_running) return;
        _running = true;
        _timer = new Timer(async _ => await TickAsync(), null, _interval, _interval);
        _log.Info("ActiveProtection started (10s cycle)");
    }

    /// <summary>Stop the monitoring loop.</summary>
    public void Stop()
    {
        _running = false;
        _timer?.Dispose();
        _timer = null;
    }

    /// <summary>
    /// Single monitoring tick. Called every 10 seconds.
    /// Can also be called manually for testing.
    /// </summary>
    public async Task TickAsync()
    {
        try
        {
            foreach (var termId in _connector.GetAllTerminalIds())
            {
                if (!_connector.IsConnected(termId)) continue;

                var profile = _state.GetProfile(termId);
                if (profile == null || profile.Mode == "monitor") continue;

                await CheckDailyDD(termId, profile);
                await CheckNewsAutoBE(termId, profile);
            }
        }
        catch (Exception ex)
        {
            _log.Error($"ActiveProtection tick error: {ex.Message}");
        }
    }

    /// <summary>
    /// Check daily DD for a terminal against thresholds.
    /// Can be called directly for unit testing with mock data.
    /// </summary>
    public async Task<ProtectionResult> CheckDailyDD(string terminalId, TerminalProfile profile)
    {
        // Calculate daily P/L (broker timezone)
        var brokerDate = GetBrokerDate(profile.ServerTimezone);
        var (realizedPnl, hwm, _) = _state.GetDailyPnl(terminalId, brokerDate);

        double unrealized;

        // Phase 10 fix: virtual mode uses virtual equity, not real account equity
        if (profile.Mode == "virtual" && _virtualTracker != null)
        {
            var vBal = _state.GetVirtualBalance(terminalId) ?? 0;
            unrealized = _virtualTracker.GetUnrealizedPnl(terminalId);
            double vEquity = vBal + unrealized;
            _state.UpdateHWM(terminalId, brokerDate, vEquity);
        }
        else
        {
            // Real mode: magic-filtered unrealized — only this terminal's positions
            unrealized = await _connector.GetFilteredUnrealizedPnlAsync(terminalId, CancellationToken.None);
            // HWM still uses full account equity (for Gate 3 cumulative DD)
            var acc = await _connector.GetAccountInfoAsync(terminalId, CancellationToken.None);
            if (acc != null)
                _state.UpdateHWM(terminalId, brokerDate, acc.Equity);
        }

        double totalDailyPnl = realizedPnl + unrealized;

        bool isSoft = profile.DailyDdMode == "soft";
        string modeTag = isSoft ? "SOFT" : "HARD";

        // Soft: check realized only; Hard: check realized + unrealized
        double checkPnl = isSoft ? realizedPnl : totalDailyPnl;
        double dailyLoss = checkPnl < 0 ? Math.Abs(checkPnl) : 0;
        double limit = profile.DailyDDLimit;

        if (limit <= 0) return new ProtectionResult { Level = AlertLevel.None };

        double pctUsed = (dailyLoss / limit) * 100;

        var result = new ProtectionResult
        {
            DailyPnl = totalDailyPnl,
            RealizedPnl = realizedPnl,
            UnrealizedPnl = unrealized,
            DailyLoss = dailyLoss,
            Limit = limit,
            PctUsed = pctUsed,
        };

        // Determine alert level
        if (pctUsed >= 100)
        {
            result.Level = AlertLevel.Emergency;
            result.Message = $"[{modeTag}] DAILY DD LIMIT BREACHED! Loss: ${dailyLoss:F2} / ${limit:F2} ({pctUsed:F1}%)";
        }
        else if (pctUsed >= 95)
        {
            result.Level = AlertLevel.Red;
            result.Message = $"[{modeTag}] Daily DD at {pctUsed:F1}%: ${dailyLoss:F2} / ${limit:F2}";
        }
        else if (pctUsed >= 80)
        {
            result.Level = AlertLevel.Yellow;
            result.Message = $"[{modeTag}] Daily DD at {pctUsed:F1}%: ${dailyLoss:F2} / ${limit:F2}";
        }
        else
        {
            result.Level = AlertLevel.None;
            result.Message = $"Daily DD OK: {pctUsed:F1}%";
        }

        // Act on alert level
        var prevLevel = _lastAlertLevel.GetValueOrDefault(terminalId, AlertLevel.None);

        if (result.Level > AlertLevel.None && result.Level > prevLevel)
        {
            _lastAlertLevel[terminalId] = result.Level;

            switch (result.Level)
            {
                case AlertLevel.Yellow:
                    await _alerts.SendAsync("YELLOW", terminalId, result.Message!);
                    break;

                case AlertLevel.Red:
                    await _alerts.SendAsync("RED", terminalId, result.Message!);
                    break;

                case AlertLevel.Emergency:
                    await _alerts.SendAsync("EMERGENCY", terminalId, result.Message!, bypassDebounce: true);
                    // Soft: block new entries only (G2 gate handles this), no force-close
                    // Hard: force-close all positions
                    if (!isSoft)
                    {
                        await EmergencyCloseAll(terminalId);
                    }
                    break;
            }
        }

        // Reset level tracking when DD recovers
        if (result.Level < prevLevel)
        {
            _lastAlertLevel[terminalId] = result.Level;
            if (prevLevel >= AlertLevel.Yellow && result.Level == AlertLevel.None)
            {
                await _alerts.SendAsync("INFO", terminalId,
                    $"Daily DD recovered to {pctUsed:F1}%");
            }
        }

        return result;
    }

    /// <summary>
    /// Evaluate thresholds without live data (for unit testing).
    /// </summary>
    public static ProtectionResult EvaluateThresholds(double dailyLoss, double limit)
    {
        if (limit <= 0) return new ProtectionResult { Level = AlertLevel.None };

        double pctUsed = (dailyLoss / limit) * 100;
        var result = new ProtectionResult
        {
            DailyLoss = dailyLoss,
            Limit = limit,
            PctUsed = pctUsed,
        };

        if (pctUsed >= 100)
        {
            result.Level = AlertLevel.Emergency;
            result.Message = $"BREACHED: ${dailyLoss:F2} / ${limit:F2} ({pctUsed:F1}%)";
        }
        else if (pctUsed >= 95)
        {
            result.Level = AlertLevel.Red;
            result.Message = $"RED: ${dailyLoss:F2} / ${limit:F2} ({pctUsed:F1}%)";
        }
        else if (pctUsed >= 80)
        {
            result.Level = AlertLevel.Yellow;
            result.Message = $"YELLOW: ${dailyLoss:F2} / ${limit:F2} ({pctUsed:F1}%)";
        }
        else
        {
            result.Level = AlertLevel.None;
            result.Message = $"OK: {pctUsed:F1}%";
        }

        return result;
    }

    // ===================================================================
    // News Auto-BE: move profitable positions to breakeven before news
    // ===================================================================

    private async Task CheckNewsAutoBE(string terminalId, TerminalProfile profile)
    {
        if (!profile.NewsBeEnabled || !profile.NewsGuardOn) return;
        if (_news == null) return;

        // Reset tracking set on new day
        var brokerDate = GetBrokerDate(profile.ServerTimezone);
        if (brokerDate != _newsBeDate)
        {
            _newsBeApplied.Clear();
            _newsBeDate = brokerDate;
        }

        int windowMin = profile.NewsWindowMin > 0 ? profile.NewsWindowMin : 15;
        int minImpact = profile.NewsMinImpact > 0 ? profile.NewsMinImpact : 2;
        bool includeUsd = profile.NewsIncludeUsd;

        if (profile.Mode != "virtual")
        {
            // Real positions
            try
            {
                var positions = await _connector.GetPositionsAsync(terminalId, CancellationToken.None);
                if (positions == null) return;

                foreach (var pos in positions)
                {
                    if (_newsBeApplied.Contains(pos.Ticket)) continue;

                    var block = _news.IsBlocked(pos.Symbol, windowMin, includeUsd, minImpact);
                    if (!block.Blocked) continue;
                    if (pos.Profit <= 0) continue;

                    double bePrice = pos.PriceOpen;
                    if (pos.IsBuy && pos.SL >= bePrice) continue;
                    if (!pos.IsBuy && pos.SL > 0 && pos.SL <= bePrice) continue;

                    var orderReq = new Dictionary<string, object>
                    {
                        ["action"] = 3,  // TRADE_ACTION_SLTP
                        ["symbol"] = pos.Symbol,
                        ["position"] = pos.Ticket,
                        ["sl"] = bePrice,
                        ["magic"] = pos.Magic
                    };

                    var result = await _connector.SendOrderAsync(terminalId, orderReq, CancellationToken.None);
                    if (result.IsOk)
                    {
                        _newsBeApplied.Add(pos.Ticket);
                        _log.Info($"[{terminalId}] NEWS AUTO-BE #{pos.Ticket} {pos.Symbol} SL\u2192{bePrice} " +
                                  $"(news: {block.Currency} {block.EventName}, {block.MinutesToEvent}m)");
                        _state.LogEvent("RISK", terminalId, null,
                            $"News auto-BE #{pos.Ticket} {pos.Symbol} SL\u2192{bePrice} ({block.Currency}: {block.EventName})");
                        _ = _alerts.SendAsync("RISK", terminalId,
                            $"\ud83d\udce1 News auto-BE: {pos.Symbol} #{pos.Ticket} SL\u2192{bePrice}\n" +
                            $"News: {block.Currency} {block.EventName} in {block.MinutesToEvent}m");
                    }
                    else
                    {
                        _log.Warn($"[{terminalId}] NEWS AUTO-BE failed #{pos.Ticket}: {result.Message}");
                    }
                }
            }
            catch (Exception ex) { _log.Warn($"[{terminalId}] News auto-BE error: {ex.Message}"); }
        }
        else
        {
            // Virtual positions: update SL directly in DB
            var vPositions = _state.GetOpenVirtualPositions(terminalId);
            foreach (var pos in vPositions)
            {
                if (_newsBeApplied.Contains(pos.Ticket)) continue;

                var block = _news.IsBlocked(pos.Symbol, windowMin, includeUsd, minImpact);
                if (!block.Blocked) continue;

                // Phase 10 fix: check profitability before moving to BE
                // (mirrors real-mode check: if (pos.Profit <= 0) continue)
                bool isBuy = pos.Direction == "BUY";
                if (_barsCache != null)
                {
                    var tf = pos.Timeframe ?? "H1";
                    var bars = _barsCache.GetBars(terminalId, pos.Symbol, tf);
                    if (bars != null && bars.Count > 0)
                    {
                        double currentPrice = bars[^1].Close;
                        bool isProfitable = isBuy
                            ? currentPrice > pos.PriceOpen
                            : currentPrice < pos.PriceOpen;
                        if (!isProfitable)
                        {
                            _log.Info($"[V:{terminalId}] NEWS AUTO-BE skipped #{pos.Ticket} {pos.Symbol} — not profitable");
                            continue;
                        }
                    }
                    else
                    {
                        // No bars available — skip to be safe (don't move SL blindly)
                        continue;
                    }
                }
                else
                {
                    // No BarsCache — skip to be safe
                    continue;
                }

                double bePrice = pos.PriceOpen;
                if (isBuy && pos.SL >= bePrice) continue;
                if (!isBuy && pos.SL > 0 && pos.SL <= bePrice) continue;

                pos.SL = bePrice;
                _state.SavePosition(pos);
                _newsBeApplied.Add(pos.Ticket);

                _log.Info($"[V:{terminalId}] NEWS AUTO-BE #{pos.Ticket} {pos.Symbol} SL\u2192{bePrice} " +
                          $"(news: {block.Currency} {block.EventName})");
                _state.LogEvent("RISK", terminalId, null,
                    $"Virtual news auto-BE #{pos.Ticket} {pos.Symbol} SL\u2192{bePrice} ({block.Currency}: {block.EventName})");
            }
        }
    }

    /// <summary>EMERGENCY: close all positions on a terminal and block trading.</summary>
    private async Task EmergencyCloseAll(string terminalId)
    {
        _log.Error($"[{terminalId}] EMERGENCY — closing all positions and blocking trading!");

        var positions = _state.GetOpenPositions(terminalId);
        foreach (var pos in positions)
        {
            try
            {
                var closeReq = new Dictionary<string, object>
                {
                    ["action"] = "CLOSE",
                    ["ticket"] = pos.Ticket,
                    ["symbol"] = pos.Symbol,
                    ["volume"] = pos.Volume,
                    ["type"] = pos.Direction == "LONG" ? "SELL" : "BUY",
                };
                await _connector.SendOrderAsync(terminalId, closeReq);
                _log.Info($"  Closed #{pos.Ticket} {pos.Symbol}");
            }
            catch (Exception ex)
            {
                _log.Error($"  Failed to close #{pos.Ticket}: {ex.Message}");
            }
        }

        // Block trading via profile
        var profile = _state.GetProfile(terminalId);
        if (profile != null)
        {
            profile.Mode = "monitor";
            _state.SaveProfile(profile);
        }

        _state.LogEvent("RISK", terminalId, null,
            "EMERGENCY: All positions closed, terminal switched to monitor mode");
    }

    /// <summary>Reset daily tracking (call at broker day boundary).</summary>
    public void ResetDailyTracking()
    {
        _lastAlertLevel.Clear();
    }

    private static string GetBrokerDate(string serverTimezone)
    {
        try
        {
            var tz = TimeZoneInfo.FindSystemTimeZoneById(serverTimezone);
            var brokerNow = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, tz);
            return brokerNow.ToString("yyyy-MM-dd");
        }
        catch
        {
            return DateTime.UtcNow.ToString("yyyy-MM-dd");
        }
    }

    public void Dispose()
    {
        Stop();
    }
}

// ===================================================================
// Types
// ===================================================================

public enum AlertLevel
{
    None = 0,
    Yellow = 1,
    Red = 2,
    Emergency = 3,
}

public class ProtectionResult
{
    public AlertLevel Level { get; set; }
    public string? Message { get; set; }
    public double DailyPnl { get; set; }
    public double RealizedPnl { get; set; }
    public double UnrealizedPnl { get; set; }
    public double DailyLoss { get; set; }
    public double Limit { get; set; }
    public double PctUsed { get; set; }
}
