using System.Text.Json;
using Daemon.Connector;
using Daemon.Models;
using Daemon.Strategy;

namespace Daemon.Engine;

/// <summary>
/// Manages live MT5 pending (stop) orders.
///
/// Responsibilities:
///   1. Place BUY_STOP / SELL_STOP orders after risk gates pass.
///   2. Each scheduler tick: reconcile pending_orders DB against MT5 ORDERS_GET.
///      - Still in MT5  → decrement bars_remaining, cancel if expired.
///      - Gone from MT5 → check positions: filled or cancelled_external.
///      - OCO: if one filled, cancel the other.
/// </summary>
public class PendingOrderManager
{
    private readonly StateManager    _state;
    private readonly ConnectorManager _connector;
    private readonly AlertService    _alerts;
    private readonly ILogger         _log;

    public PendingOrderManager(
        StateManager state,
        ConnectorManager connector,
        AlertService alerts,
        ILogger log)
    {
        _state     = state;
        _connector = connector;
        _alerts    = alerts;
        _log       = log;
    }

    // ===================================================================
    // Place pending order (called from Scheduler after risk gates)
    // ===================================================================

    /// <summary>
    /// Send BUY_STOP or SELL_STOP to MT5 and record in pending_orders DB.
    /// order_type is derived from direction + entry vs current price (already validated by caller).
    /// Returns MT5 ticket on success, 0 on failure.
    /// </summary>
    public async Task<long> PlacePendingAsync(
        string strategyName, string terminalId, int magic,
        string symbol, string direction, string orderType,
        double entryPrice, double slPrice, double tpPrice,
        double lot, int expiryBars, string? signalData,
        CancellationToken ct)
    {
        var tag = $"[{strategyName}@{terminalId}]";

        // MT5 order type int: BUY_STOP=4, SELL_STOP=5
        int mt5Type = orderType == "BUY_STOP" ? 4 : 5;

        var orderReq = new Dictionary<string, object>
        {
            ["action"]       = 5,          // TRADE_ACTION_PENDING
            ["symbol"]       = symbol,
            ["volume"]       = lot,
            ["type"]         = mt5Type,
            ["price"]        = entryPrice,
            ["sl"]           = slPrice,
            ["tp"]           = tpPrice,
            ["magic"]        = magic,
            ["comment"]      = $"D:{strategyName}",
            ["type_time"]    = 0,          // GTC (we manage expiry ourselves)
            ["type_filling"] = 2,          // IOC
        };

        var result = await _connector.SendOrderAsync(terminalId, orderReq, ct);

        if (!result.IsOk || result.Data == null)
        {
            _log.Error($"{tag} PENDING PLACE FAILED: {result.Message} (code={result.Code})");
            _state.LogEvent("ORDER_FAIL", terminalId, strategyName,
                $"PENDING {orderType} {symbol} failed: {result.Message}");
            return 0;
        }

        var od = JsonSerializer.Deserialize<OrderResult>(result.Data.Value.GetRawText());
        if (od == null || od.Order == 0)
        {
            _log.Error($"{tag} PENDING PLACE: no ticket returned");
            return 0;
        }

        // -1 = GTC (no expiry), >0 = countdown
        int barsRemaining = expiryBars > 0 ? expiryBars : -1;

        _state.SavePendingOrder(new PendingOrderRecord
        {
            Ticket        = od.Order,
            TerminalId    = terminalId,
            Symbol        = symbol,
            Strategy      = strategyName,
            Magic         = magic,
            Direction     = direction == "LONG" ? "BUY" : "SELL",
            OrderType     = orderType,
            Volume        = lot,
            EntryPrice    = entryPrice,
            SL            = slPrice,
            TP            = tpPrice,
            BarsRemaining = barsRemaining,
            SignalData    = signalData,
            IsVirtual     = false,
            PlacedAt      = DateTime.UtcNow.ToString("o"),
        });

        _log.Info($"{tag} PENDING PLACED ticket={od.Order} {orderType} {symbol} @ {entryPrice} " +
                  $"lot={lot:F2} expiry={expiryBars} bars");

        _state.LogEvent("ORDER", terminalId, strategyName,
            $"PENDING {orderType} {symbol} @ {entryPrice} lot={lot:F2}",
            JsonSerializer.Serialize(new { ticket = od.Order, slPrice, expiryBars }));

        _ = _alerts.SendAsync("ORDER", terminalId,
            $"⏳ PENDING {orderType} {symbol} @ {entryPrice} lot={lot:F2}",
            strategyName);

        return od.Order;
    }

    // ===================================================================
    // Tick reconciliation (called from Scheduler every bar)
    // ===================================================================

    /// <summary>
    /// Reconcile pending_orders DB against live MT5 state.
    /// Call once per scheduler tick, after BarsCache update.
    /// </summary>
    public async Task TickAsync(string terminalId, CancellationToken ct)
    {
        var dbPending = _state.GetOpenPendingOrders(terminalId, isVirtual: false);
        if (dbPending.Count == 0) return;

        // Fetch live orders from MT5 (one call for all)
        List<BrokerPendingOrder> mt5Orders;
        try
        {
            mt5Orders = await _connector.GetPendingOrdersAsync(terminalId, ct);
        }
        catch (Exception ex)
        {
            _log.Warn($"[PendingMgr@{terminalId}] ORDERS_GET failed: {ex.Message}");
            return;
        }

        var mt5ByTicket = mt5Orders.ToDictionary(o => o.Ticket);

        // Fetch current positions for fill detection
        List<Position> positions;
        try
        {
            positions = await _connector.GetFilteredPositionsAsync(terminalId, ct);
        }
        catch
        {
            positions = new();
        }

        foreach (var rec in dbPending)
        {
            try
            {
                await ProcessOnePendingAsync(rec, mt5ByTicket, positions, ct);
            }
            catch (Exception ex)
            {
                _log.Error($"[PendingMgr@{terminalId}] Error processing ticket={rec.Ticket}: {ex.Message}");
            }
        }
    }

    // ===================================================================
    // Internal
    // ===================================================================

    private async Task ProcessOnePendingAsync(
        PendingOrderRecord rec,
        Dictionary<long, BrokerPendingOrder> mt5ByTicket,
        List<Position> positions,
        CancellationToken ct)
    {
        var tag = $"[PendingMgr@{rec.TerminalId}]";

        if (mt5ByTicket.ContainsKey(rec.Ticket))
        {
            // ── Still pending in MT5 ──────────────────────────────────
            // Decrement handled in bulk by DecrementPendingBars; check for expiry
            if (rec.BarsRemaining == 0)
            {
                // Just expired (DecrementPendingBars returned this ticket)
                // Actually DecrementPendingBars is called before TickAsync by Scheduler.
                // If bars_remaining=0 here and still in MT5 → cancel it now.
                _log.Info($"{tag} ticket={rec.Ticket} EXPIRED → ORDER_DELETE");
                var del = await _connector.DeletePendingOrderAsync(rec.TerminalId, rec.Ticket, ct);
                _state.ClosePendingOrder(rec.Ticket, rec.TerminalId, "expired");

                _state.LogEvent("ORDER", rec.TerminalId, rec.Strategy,
                    $"PENDING EXPIRED {rec.OrderType} {rec.Symbol} @ {rec.EntryPrice}",
                    JsonSerializer.Serialize(new { ticket = rec.Ticket }));
                _ = _alerts.SendAsync("ORDER", rec.TerminalId,
                    $"⌛ PENDING EXPIRED {rec.Symbol} @ {rec.EntryPrice}", rec.Strategy);
            }
            // else: still counting, nothing to do this tick
        }
        else
        {
            // ── Gone from MT5 pending list ────────────────────────────
            // Either filled → became a position, or cancelled externally

            // Check if a position exists with the same magic + symbol
            var filledPos = positions.FirstOrDefault(p =>
                p.Magic == rec.Magic &&
                p.Symbol == rec.Symbol &&
                p.IsBuy == (rec.Direction == "BUY"));

            if (filledPos != null)
            {
                // Filled → register as real position in StateManager
                _log.Info($"{tag} ticket={rec.Ticket} FILLED → position={filledPos.Ticket}");
                _state.ClosePendingOrder(rec.Ticket, rec.TerminalId, "filled");

                _state.SavePosition(new PositionRecord
                {
                    Ticket     = filledPos.Ticket,
                    TerminalId = rec.TerminalId,
                    Symbol     = rec.Symbol,
                    Direction  = rec.Direction,
                    Volume     = filledPos.Volume,
                    PriceOpen  = filledPos.PriceOpen,
                    SL         = filledPos.SL,
                    TP         = filledPos.TP,
                    Magic      = rec.Magic,
                    Source     = rec.Strategy,
                    SignalData = rec.SignalData,
                    OpenedAt   = DateTime.UtcNow.ToString("o"),
                });

                _state.LogEvent("ORDER", rec.TerminalId, rec.Strategy,
                    $"PENDING FILLED {rec.OrderType} {rec.Symbol} @ {filledPos.PriceOpen}",
                    JsonSerializer.Serialize(new { pendingTicket = rec.Ticket, posTicket = filledPos.Ticket }));
                _ = _alerts.SendAsync("ORDER", rec.TerminalId,
                    $"✅ PENDING FILLED {rec.Symbol} @ {filledPos.PriceOpen}", rec.Strategy);

                // OCO: cancel sibling orders in the same signal group
                if (!string.IsNullOrEmpty(rec.SignalData))
                {
                    var ocoTickets = _state.GetOcoPendingTickets(
                        rec.TerminalId, rec.SignalData, rec.Ticket);
                    foreach (var ocoTicket in ocoTickets)
                    {
                        _log.Info($"{tag} OCO cancel ticket={ocoTicket}");
                        await _connector.DeletePendingOrderAsync(rec.TerminalId, ocoTicket, ct);
                        _state.ClosePendingOrder(ocoTicket, rec.TerminalId, "cancelled");
                        _state.LogEvent("ORDER", rec.TerminalId, rec.Strategy,
                            $"OCO CANCELLED pending ticket={ocoTicket}");
                    }
                }
            }
            else
            {
                // Not in pending, not in positions → cancelled externally (broker/manual)
                _log.Warn($"{tag} ticket={rec.Ticket} CANCELLED EXTERNALLY (not in positions)");
                _state.ClosePendingOrder(rec.Ticket, rec.TerminalId, "cancelled_external");

                _state.LogEvent("ORDER", rec.TerminalId, rec.Strategy,
                    $"PENDING CANCELLED (external) {rec.Symbol} ticket={rec.Ticket}");
                _ = _alerts.SendAsync("RISK", rec.TerminalId,
                    $"⚠️ PENDING CANCELLED (external) {rec.Symbol}", rec.Strategy);
            }
        }
    }
}
