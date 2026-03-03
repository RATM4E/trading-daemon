using System.Text.Json;
using Daemon.Connector;
using Daemon.Engine;

namespace Daemon.Engine;

/// <summary>
/// Monitors all open virtual positions for SL/TP hits.
/// Called every scheduler tick from the main engine loop.
///
/// Key features:
/// - Gap execution: if bar opens beyond SL/TP, fills at bar Open (not the level)
/// - SELL exit spread: closing SELL = buying at Ask (close + spread)
/// - Commission: round-trip per lot deducted from P&L
/// - Trade snapshots: saves bars + SL history JSON when position closes
/// - 3SL guard: virtual SL hits increment the counter
/// - Daily P&L tracking for G2 drawdown gate
/// </summary>
public class VirtualTracker
{
    private readonly StateManager _state;
    private readonly BarsCache _barsCache;
    private readonly ConnectorManager _connector;
    private readonly AlertService _alerts;
    private readonly ILogger _log;
    private readonly Config.DaemonConfig _config;
    private readonly Dictionary<string, Daemon.Models.InstrumentCard> _symbolCache = new();

    public VirtualTracker(
        StateManager state,
        BarsCache barsCache,
        ConnectorManager connector,
        AlertService alerts,
        Config.DaemonConfig config,
        ILogger log)
    {
        _state = state;
        _barsCache = barsCache;
        _connector = connector;
        _alerts = alerts;
        _config = config;
        _log = log;
    }

    /// <summary>
    /// Check all open virtual positions for SL/TP hits.
    /// Called from main engine loop every scheduler tick.
    /// </summary>
    public async Task TickAsync(CancellationToken ct)
    {
        var virtualPositions = _state.GetOpenVirtualPositions();
        if (virtualPositions.Count == 0) return;

        // Pre-cache symbol info for all virtual positions (ensures PnL calculation is accurate)
        foreach (var pos in virtualPositions)
        {
            var symKey = $"{pos.TerminalId}:{pos.Symbol}";
            if (!_symbolCache.ContainsKey(symKey))
            {
                try
                {
                    var card = await _connector.GetSymbolInfoAsync(pos.TerminalId, pos.Symbol, ct);
                    if (card != null) _symbolCache[symKey] = card;
                }
                catch { }
            }
        }

        foreach (var pos in virtualPositions)
        {
            try
            {
                await CheckPositionAsync(pos, ct);
            }
            catch (Exception ex)
            {
                _log.Error($"[VirtualTracker] Error checking #{pos.Ticket} {pos.Symbol}: {ex.Message}");
            }
        }
    }

    /// <summary>Cache an InstrumentCard for P&L calculations (called from Scheduler on position open).</summary>
    public void CacheSymbol(string terminalId, string symbol, Daemon.Models.InstrumentCard card)
    {
        _symbolCache[$"{terminalId}:{symbol}"] = card;
    }

    private async Task CheckPositionAsync(PositionRecord pos, CancellationToken ct)
    {
        var tf = pos.Timeframe ?? "H1";
        var bars = _barsCache.GetBars(pos.TerminalId, pos.Symbol, tf);
        if (bars == null || bars.Count == 0) return;

        var lastBar = bars[^1];
        bool isBuy = pos.Direction == "BUY";
        string? closeReason = null;
        double closePrice = 0;

        // SL check — priority (if both SL and TP on same bar, SL wins)
        // Gap execution: if bar opened beyond stop → fill at Open, not at SL level
        if (pos.SL > 0)
        {
            if (isBuy && lastBar.Low <= pos.SL)
            {
                closeReason = "SL";
                closePrice = lastBar.Open <= pos.SL ? lastBar.Open : pos.SL;
            }
            else if (!isBuy && lastBar.High >= pos.SL)
            {
                closeReason = "SL";
                closePrice = lastBar.Open >= pos.SL ? lastBar.Open : pos.SL;
            }
        }

        // TP check (only if SL didn't trigger)
        // Gap execution: fill at Open if bar gapped through TP
        if (closeReason == null && pos.TP > 0)
        {
            if (isBuy && lastBar.High >= pos.TP)
            {
                closeReason = "TP";
                closePrice = lastBar.Open >= pos.TP ? lastBar.Open : pos.TP;
            }
            else if (!isBuy && lastBar.Low <= pos.TP)
            {
                closeReason = "TP";
                closePrice = lastBar.Open <= pos.TP ? lastBar.Open : pos.TP;
            }
        }

        if (closeReason == null) return;

        // Save trade snapshot BEFORE closing (need open position data)
        SaveTradeSnapshot(pos, closePrice, closeReason, bars);

        // P&L calculation — ensure symbol info cached first
        var symKey = $"{pos.TerminalId}:{pos.Symbol}";
        if (!_symbolCache.ContainsKey(symKey))
        {
            var symCard = await _connector.GetSymbolInfoAsync(pos.TerminalId, pos.Symbol, ct);
            if (symCard != null) _symbolCache[symKey] = symCard;
        }
        double pnl = CalculateVirtualPnl(pos, closePrice, pos.TerminalId);
        _state.ClosePosition(pos.Ticket, pos.TerminalId, closePrice, closeReason, pnl);
        _state.UpdateVirtualBalance(pos.TerminalId, pnl);

        // Release virtual margin
        _symbolCache.TryGetValue(symKey, out var card);
        if (card == null) card = await _connector.GetSymbolInfoAsync(pos.TerminalId, pos.Symbol, ct);
        if (card != null)
        {
            int effLeverage = _state.GetEffectiveLeverage(pos.TerminalId, pos.Symbol, 100);
            double marginRelease = pos.Volume * (card.Margin1Lot > 0
                ? card.Margin1Lot
                : card.TradeContractSize * pos.PriceOpen / effLeverage);
            _state.AddVirtualMargin(pos.TerminalId, -marginRelease);
        }

        // Daily P&L (for G2 and 3SL)
        var profile = _state.GetProfile(pos.TerminalId);
        var brokerDate = GetBrokerDate(profile?.ServerTimezone ?? "UTC");
        _state.AddRealizedPnl(pos.TerminalId, brokerDate, pnl);

        // Phase 10: R-cap — calculate R-result for virtual closes
        var rResult = Engine.RCalc.GetRResult(
            closeReason, pos.ProtectorFired, pos.SignalData,
            pos.PriceOpen, closePrice, pos.Direction == "BUY");
        if (rResult.HasValue)
        {
            _state.AddDailyR(pos.TerminalId, pos.Source, brokerDate, rResult.Value);
            _log.Info($"[V:{pos.TerminalId}] R-cap: {closeReason}" +
                      (pos.ProtectorFired ? " (protector)" : "") +
                      $" -> {rResult.Value:+0.00;-0.00}R");
        }

        // 3SL Guard — virtual SL hits also increment counter
        if (closeReason == "SL" && profile?.Sl3GuardOn == true)
        {
            _state.IncrementSLCount(pos.TerminalId);
            var (count, blocked) = _state.Get3SLState(pos.TerminalId);
            if (count >= 3 && !blocked)
            {
                _state.Block3SL(pos.TerminalId);
                _log.Warn($"[{pos.TerminalId}] 3SL GUARD ACTIVATED (virtual)");
                _ = _alerts.SendAsync("RISK", pos.TerminalId,
                    "\ud83d\uded1 3SL GUARD (virtual) — trading blocked", pos.Source);
            }
        }
        else if (closeReason == "TP")
        {
            _state.ResetSLCount(pos.TerminalId);
        }

        string emoji = closeReason == "SL" ? "\ud83d\udd34" : "\ud83d\udfe2";
        _log.Info($"[V:{pos.TerminalId}] {emoji} VIRTUAL {closeReason} #{pos.Ticket} " +
                  $"{pos.Symbol} @ {closePrice} P/L={pnl:+0.00;-0.00}");

        _ = _alerts.SendAsync("ORDER", pos.TerminalId,
            $"\ud83d\udfe3 VIRTUAL {closeReason} {pos.Symbol} P/L={pnl:+0.00;-0.00}", pos.Source);

        _state.LogEvent("VIRTUAL_ORDER", pos.TerminalId, pos.Source,
            $"{emoji} VIRTUAL {closeReason} {pos.Symbol} @ {closePrice} P/L={pnl:+0.00;-0.00}",
            JsonSerializer.Serialize(new { ticket = pos.Ticket, closeReason, closePrice, pnl }));
    }

    // =================================================================
    // P&L Calculation
    // =================================================================

    /// <summary>
    /// Exact P&L via tick_value from InstrumentCard.
    /// Same principle as LotCalculator but in reverse:
    /// LotCalc: risk$ → lot.  Here: lot + price_diff → P&L$.
    /// Includes round-trip commission.
    /// Returns 0 if symbol info not available (better than a wildly wrong fallback).
    /// </summary>
    public double CalculateVirtualPnl(PositionRecord pos, double closePrice, string terminalId)
    {
        _symbolCache.TryGetValue($"{terminalId}:{pos.Symbol}", out var card);

        double priceDiff = pos.Direction == "BUY"
            ? closePrice - pos.PriceOpen
            : pos.PriceOpen - closePrice;

        double rawPnl;
        if (card != null && card.TradeTickSize > 0)
        {
            double ticks = priceDiff / card.TradeTickSize;
            double tickValue = priceDiff >= 0 ? card.TradeTickValueProfit : card.TradeTickValueLoss;
            if (tickValue <= 0) tickValue = card.TradeTickValue; // fallback
            rawPnl = ticks * tickValue * pos.Volume;
        }
        else
        {
            // No symbol card — return 0 rather than using a wrong contractSize
            // This is temporary until VirtualTracker.TickAsync caches the symbol
            _log.Warn($"[VirtualTracker] No card for {pos.Symbol}@{terminalId}, PnL=0 until cached");
            return 0;
        }

        // Commission: round-trip per lot
        var profile = _state.GetProfile(terminalId);
        double commissionPerLot = profile?.CommissionPerLot ?? _config.DefaultCommissionPerLot;
        double commission = commissionPerLot * pos.Volume;

        return rawPnl - commission;
    }

    // =================================================================
    // Unrealized P&L (for dashboard & equity snapshots)
    // =================================================================

    /// <summary>Calculate total unrealized P&L for all virtual positions on a terminal.</summary>
    public double GetUnrealizedPnl(string terminalId)
    {
        var positions = _state.GetOpenVirtualPositions(terminalId);
        double total = 0;
        foreach (var pos in positions)
        {
            var tf = pos.Timeframe ?? "H1";
            var bars = _barsCache.GetBars(terminalId, pos.Symbol, tf);
            if (bars == null || bars.Count == 0) continue;
            total += CalculateVirtualPnl(pos, bars[^1].Close, terminalId);
        }
        return total;
    }

    // =================================================================
    // Trade Snapshot (cached bars + SL history for Trade Chart)
    // =================================================================

    internal void SaveTradeSnapshot(PositionRecord pos, double closePrice, string closeReason,
                                    List<Daemon.Models.Bar> bars)
    {
        try
        {
            // SL history for this position
            var slHistory = _state.GetSlHistory(pos.Ticket, pos.TerminalId);

            // Calculate P&L for snapshot
            double pnl = CalculateVirtualPnl(pos, closePrice, pos.TerminalId);
            double pnlPct = pos.PriceOpen > 0
                ? (pos.Direction == "BUY"
                    ? (closePrice - pos.PriceOpen) / pos.PriceOpen * 100
                    : (pos.PriceOpen - closePrice) / pos.PriceOpen * 100)
                : 0;

            // Duration
            DateTime openTime = DateTime.TryParse(pos.OpenedAt, out var ot) ? ot : DateTime.UtcNow;
            int durationSec = (int)(DateTime.UtcNow - openTime).TotalSeconds;

            var snapshot = new
            {
                trade = new
                {
                    ticket = pos.Ticket,
                    symbol = pos.Symbol,
                    direction = pos.Direction == "BUY" ? "LONG" : "SHORT",
                    entry_price = pos.PriceOpen,
                    entry_time = pos.OpenedAt,
                    exit_price = closePrice,
                    exit_time = DateTime.UtcNow.ToString("o"),
                    close_reason = closeReason,
                    volume = pos.Volume,
                    pnl,
                    pnl_pct = Math.Round(pnlPct, 2),
                    strategy = pos.Source,
                    is_virtual = pos.IsVirtual,
                    duration_sec = durationSec,
                    sl = pos.SL,
                    tp = pos.TP
                },
                bars = bars.Select(b => new { time = b.Time, open = b.Open, high = b.High, low = b.Low, close = b.Close }).ToList(),
                sl_history = slHistory.Select(s => new { time = s.BarTime, sl = s.NewSl }).ToList()
            };

            var json = JsonSerializer.Serialize(snapshot);
            _state.SaveTradeSnapshot(pos.Ticket, pos.TerminalId, json);
        }
        catch (Exception ex)
        {
            _log.Warn($"[VirtualTracker] Failed to save trade snapshot #{pos.Ticket}: {ex.Message}");
        }
    }

    // =================================================================
    // Mode change: close all virtual positions
    // =================================================================

    /// <summary>Close all open virtual positions on a terminal (e.g. when switching Virtual → Auto).</summary>
    public void CloseAllVirtualPositions(string terminalId, string reason = "mode_change")
    {
        var openVirtual = _state.GetOpenVirtualPositions(terminalId);
        foreach (var pos in openVirtual)
        {
            var tf = pos.Timeframe ?? "H1";
            var bars = _barsCache.GetBars(terminalId, pos.Symbol, tf);
            double closePrice = bars is { Count: > 0 } ? bars[^1].Close : pos.PriceOpen;

            double pnl = CalculateVirtualPnl(pos, closePrice, terminalId);
            _state.ClosePosition(pos.Ticket, terminalId, closePrice, reason, pnl);
        }
        _state.SetVirtualMargin(terminalId, 0);
    }

    // =================================================================
    // Helpers
    // =================================================================

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
}
