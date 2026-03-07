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
        // ── 1. Scan virtual pending orders ───────────────────────────────────
        await CheckVirtualPendingAsync(ct);

        // ── 2. Scan open virtual positions for SL/TP ─────────────────────────
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
    // Virtual Pending Orders
    // =================================================================

    /// <summary>
    /// Check all open virtual pending orders for trigger conditions.
    /// BUY_STOP: fires when bar High >= entry_price.
    /// SELL_STOP: fires when bar Low  <= entry_price.
    /// Gap fill: if bar opened beyond entry → fill at Open, not entry.
    /// Expiry: decrement bars_remaining; cancel if reaches 0.
    /// OCO: when one fires, cancel siblings with same signal_data.
    /// </summary>
    private async Task CheckVirtualPendingAsync(CancellationToken ct)
    {
        // Collect all terminals that have virtual pending orders
        var allPending = _state.GetOpenVirtualPendingOrders();
        if (allPending.Count == 0) return;

        // Decrement expiry bars per terminal and collect expired tickets
        var terminalIds = allPending.Select(p => p.TerminalId).Distinct().ToList();
        var expired = new HashSet<long>();
        foreach (var tid in terminalIds)
        {
            var expiredTickets = _state.DecrementPendingBars(tid, isVirtual: true);
            foreach (var t in expiredTickets) expired.Add(t);
        }

        // Tracks siblings already filled-and-cancelled due to OCO double-fill on the same bar.
        var resolvedAsDoubleFill = new HashSet<long>();

        foreach (var rec in allPending)
        {
            try
            {
                if (resolvedAsDoubleFill.Contains(rec.Ticket))
                {
                    _log.Info($"[V:{rec.TerminalId}] ticket={rec.Ticket} skipped – resolved as OCO double-fill sibling");
                    continue;
                }

                // Expiry handled by DecrementPendingBars above
                if (expired.Contains(rec.Ticket))
                {
                    _state.ClosePendingOrder(rec.Ticket, rec.TerminalId, "expired");
                    _log.Info($"[V:{rec.TerminalId}] VIRTUAL PENDING EXPIRED ticket={rec.Ticket} " +
                              $"{rec.OrderType} {rec.Symbol} @ {rec.EntryPrice}");
                    _state.LogEvent("VIRTUAL_ORDER", rec.TerminalId, rec.Strategy,
                        $"⌛ VIRTUAL PENDING EXPIRED {rec.Symbol} @ {rec.EntryPrice}",
                        JsonSerializer.Serialize(new { ticket = rec.Ticket }));
                    _ = _alerts.SendAsync("ORDER", rec.TerminalId,
                        $"⌛ VIRTUAL PENDING EXPIRED {rec.Symbol} @ {rec.EntryPrice}", rec.Strategy);
                    continue;
                }

                // Get last bar for the symbol
                // Strategy TF stored in BarsCache — use any TF that has bars for this symbol
                var bars = GetBarsForSymbol(rec.TerminalId, rec.Symbol);
                if (bars == null || bars.Count == 0) continue;

                var bar = bars[^1];
                bool isBuyStop  = rec.OrderType == "BUY_STOP";
                bool triggered  = isBuyStop
                    ? bar.High >= rec.EntryPrice
                    : bar.Low  <= rec.EntryPrice;

                if (!triggered) continue;

                // Gap fill: if bar opened beyond entry → fill at Open
                double fillPrice = isBuyStop
                    ? (bar.Open >= rec.EntryPrice ? bar.Open : rec.EntryPrice)
                    : (bar.Open <= rec.EntryPrice ? bar.Open : rec.EntryPrice);

                // Pre-cache symbol card for PnL
                var symKey = $"{rec.TerminalId}:{rec.Symbol}";
                if (!_symbolCache.ContainsKey(symKey))
                {
                    var symCard = await _connector.GetSymbolInfoAsync(rec.TerminalId, rec.Symbol, ct);
                    if (symCard != null) _symbolCache[symKey] = symCard;
                }

                // Create virtual position
                var posTicket = _state.NextVirtualTicket();
                _state.SavePosition(new PositionRecord
                {
                    Ticket     = posTicket,
                    TerminalId = rec.TerminalId,
                    Symbol     = rec.Symbol,
                    Direction  = rec.Direction,
                    Volume     = rec.Volume,
                    PriceOpen  = fillPrice,
                    SL         = rec.SL,
                    TP         = rec.TP,
                    Magic      = rec.Magic,
                    Source     = rec.Strategy,
                    SignalData = rec.SignalData,
                    OpenedAt   = DateTime.UtcNow.ToString("o"),
                    IsVirtual  = true,
                    Timeframe  = GetTimeframeForSymbol(rec.TerminalId, rec.Symbol),
                });

                _state.ClosePendingOrder(rec.Ticket, rec.TerminalId, "filled");

                _log.Info($"[V:{rec.TerminalId}] VIRTUAL PENDING FILLED ticket={rec.Ticket} " +
                          $"→ pos={posTicket} {rec.OrderType} {rec.Symbol} @ {fillPrice}");

                _state.LogEvent("VIRTUAL_ORDER", rec.TerminalId, rec.Strategy,
                    $"🟣 VIRTUAL PENDING FILLED {rec.Symbol} @ {fillPrice}",
                    JsonSerializer.Serialize(new { pendingTicket = rec.Ticket, posTicket, fillPrice }));
                _ = _alerts.SendAsync("ORDER", rec.TerminalId,
                    $"🟣 VIRTUAL PENDING FILLED {rec.Symbol} @ {fillPrice}", rec.Strategy);

                // OCO: cancel siblings
                if (!string.IsNullOrEmpty(rec.SignalData))
                {
                    var ocoTickets = _state.GetOcoPendingTickets(
                        rec.TerminalId, rec.SignalData, rec.Ticket);
                    foreach (var ocoTicket in ocoTickets)
                    {
                        // Check if sibling also triggered on this bar (double-fill on same bar).
                        var sibRec = allPending.FirstOrDefault(p => p.Ticket == ocoTicket);
                        if (sibRec != null && !expired.Contains(ocoTicket))
                        {
                            bool sibIsBuyStop = sibRec.OrderType == "BUY_STOP";
                            var sibBars = GetBarsForSymbol(sibRec.TerminalId, sibRec.Symbol);
                            if (sibBars != null && sibBars.Count > 0)
                            {
                                var sibBar = sibBars[^1];
                                bool sibTriggered = sibIsBuyStop
                                    ? sibBar.High >= sibRec.EntryPrice
                                    : sibBar.Low  <= sibRec.EntryPrice;

                                if (sibTriggered)
                                {
                                    // Both filled on the same bar: cancel the sibling position
                                    // that was already saved (it's the last entry in virtual positions).
                                    var sibVirtualPos = _state.GetOpenVirtualPositions(sibRec.TerminalId)
                                        .Where(p => p.Magic  == sibRec.Magic &&
                                                    p.Symbol == sibRec.Symbol &&
                                                    p.Direction == sibRec.Direction)
                                        .OrderByDescending(p => p.Ticket)
                                        .FirstOrDefault();

                                    if (sibVirtualPos != null)
                                    {
                                        _log.Error($"[V:{rec.TerminalId}] OCO DOUBLE FILL! sibling ticket={ocoTicket} " +
                                                   $"filled pos={sibVirtualPos.Ticket} {sibRec.Symbol} – cancelling virtual position");
                                        // Close at fill price (approximation: entry price)
                                        _state.ClosePosition(sibVirtualPos.Ticket, sibRec.TerminalId,
                                            sibRec.EntryPrice, "oco_double_fill", 0);
                                        _state.LogEvent("VIRTUAL_ORDER", rec.TerminalId, rec.Strategy,
                                            $"🚨 OCO DOUBLE FILL {sibRec.Symbol} – sibling virtual pos={sibVirtualPos.Ticket} cancelled",
                                            JsonSerializer.Serialize(new { pendingTicket = ocoTicket, posTicket = sibVirtualPos.Ticket }));
                                        _ = _alerts.SendAsync("RISK", rec.TerminalId,
                                            $"🚨 OCO DOUBLE FILL (VIRTUAL) {sibRec.Symbol} – sibling pos={sibVirtualPos.Ticket} cancelled",
                                            rec.Strategy);
                                    }

                                    resolvedAsDoubleFill.Add(ocoTicket);
                                }
                            }
                        }

                        _state.ClosePendingOrder(ocoTicket, rec.TerminalId, "cancelled");
                        _log.Info($"[V:{rec.TerminalId}] OCO cancel virtual ticket={ocoTicket}");
                        _state.LogEvent("VIRTUAL_ORDER", rec.TerminalId, rec.Strategy,
                            $"OCO CANCELLED virtual pending ticket={ocoTicket}");
                    }
                }
            }
            catch (Exception ex)
            {
                _log.Error($"[VirtualTracker] Pending check error ticket={rec.Ticket}: {ex.Message}");
            }
        }
    }

    // BarsCache lookup helpers for virtual pending (no TF stored on PendingOrderRecord)
    private List<Daemon.Models.Bar>? GetBarsForSymbol(string terminalId, string symbol)
    {
        foreach (var tf in new[] { "M5", "M15", "M30", "H1", "H4", "D1" })
        {
            var bars = _barsCache.GetBars(terminalId, symbol, tf);
            if (bars != null && bars.Count > 0) return bars;
        }
        return null;
    }

    private string GetTimeframeForSymbol(string terminalId, string symbol)
    {
        foreach (var tf in new[] { "M5", "M15", "M30", "H1", "H4", "D1" })
        {
            var bars = _barsCache.GetBars(terminalId, symbol, tf);
            if (bars != null && bars.Count > 0) return tf;
        }
        return "H1";
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
