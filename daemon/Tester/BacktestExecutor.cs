using Daemon.Models;
using Daemon.Engine;
using Daemon.Strategy;

namespace Daemon.Tester;

/// <summary>
/// Virtual execution engine for backtesting.
/// Manages in-memory positions, SL/TP hit detection, P&L calculation with flat cost model.
///
/// Key differences from VirtualTracker:
///   - No MT5 connector (all data from BarsHistoryDb)
///   - Flat cost model (spread+slippage as single deduction)
///   - In-memory state (no SQLite persistence during run)
///   - R-result tracking per trade
///   - Commission separate from cost model
/// </summary>
public class BacktestExecutor
{
    // ── State ────────────────────────────────────────────────

    public double InitialBalance { get; }
    public double Balance { get; private set; }
    public double Commission { get; private set; }      // $/lot round-trip
    public int NextTicket { get; private set; } = -100;  // virtual tickets (negative)

    public List<BtPosition> OpenPositions { get; } = new();
    public List<BtPendingOrder> PendingOrders { get; } = new();
    public List<BtTrade> ClosedTrades { get; } = new();
    public List<BtBlockedSignal> BlockedSignals { get; } = new();
    public List<BtEquitySnapshot> EquitySnapshots { get; } = new();
    public Dictionary<string, int> GateStats { get; } = new();

    // Cost model — flat cost per symbol (in price units)
    private readonly Dictionary<string, double> _flatCostPrice;

    // Instrument cards — cached for P&L calculation
    private readonly Dictionary<string, InstrumentCard> _cards;

    // Daily R tracking: brokerDate → totalR
    private readonly Dictionary<string, double> _dailyR = new();

    // Daily PnL tracking: brokerDate → totalPnl
    private readonly Dictionary<string, double> _dailyPnl = new();

    // ── Constructor ──────────────────────────────────────────

    public BacktestExecutor(
        double initialBalance,
        double commissionPerLot,
        Dictionary<string, double> flatCostPrice,
        Dictionary<string, InstrumentCard> instrumentCards)
    {
        InitialBalance = initialBalance;
        Balance = initialBalance;
        Commission = commissionPerLot;
        _flatCostPrice = flatCostPrice;
        _cards = instrumentCards;
    }

    // ── Open Position ────────────────────────────────────────

    /// <summary>
    /// Open a new position. Fill price = next bar's Open (already determined by engine).
    /// Returns the position, or null if instrument card missing.
    /// </summary>
    public BtPosition? OpenPosition(
        StrategyAction action,
        double fillPrice,
        long fillTime,
        double lot,
        string strategy,
        int magic)
    {
        var symbol = action.Symbol!;
        if (!_cards.TryGetValue(symbol, out var card))
            return null;

        var ticket = NextTicket--;
        var pos = new BtPosition
        {
            Ticket = ticket,
            Symbol = symbol,
            Direction = action.Direction!.ToUpperInvariant() == "LONG" ? "BUY" : "SELL",
            Volume = lot,
            PriceOpen = fillPrice,
            OpenTime = fillTime,
            SL = action.SlPrice ?? 0,
            TP = action.TpPrice ?? 0,
            Strategy = strategy,
            Magic = magic,
            SignalData = action.SignalData,
            Comment = action.Comment,
        };

        // Fix: capture original SL distance at entry (immutable through trail)
        double parsedSlDist = ParseSlDist(action.SignalData);
        pos.OriginalSlDist = parsedSlDist > 0 
            ? parsedSlDist 
            : Math.Abs(fillPrice - pos.SL);

        OpenPositions.Add(pos);
        return pos;
    }

    /// <summary>Parse sl_dist from signal_data JSON.</summary>
    private static double ParseSlDist(string? signalDataJson)
    {
        if (string.IsNullOrEmpty(signalDataJson)) return 0;
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(signalDataJson);
            if (doc.RootElement.TryGetProperty("sl_dist", out var prop)
                && prop.ValueKind == System.Text.Json.JsonValueKind.Number)
                return prop.GetDouble();
        }
        catch { }
        return 0;
    }

    // ── Close Position ───────────────────────────────────────

    /// <summary>
    /// Close a position by ticket. Fill at given price.
    /// Applies flat cost and commission. Returns the trade record.
    /// </summary>
    public BtTrade? ClosePosition(long ticket, double closePrice, long closeTime, string reason)
    {
        var pos = OpenPositions.FirstOrDefault(p => p.Ticket == ticket);
        if (pos == null) return null;

        OpenPositions.Remove(pos);

        var trade = BuildTrade(pos, closePrice, closeTime, reason);
        ClosedTrades.Add(trade);

        // Update balance
        Balance += trade.PnlDollar;
        trade.BalanceAfter = Balance;  // set after balance update

        return trade;
    }

    /// <summary>Close all remaining positions at given prices (end of backtest).</summary>
    public List<BtTrade> CloseAllAtMarket(Dictionary<string, double> lastPrices, long closeTime)
    {
        var trades = new List<BtTrade>();
        foreach (var pos in OpenPositions.ToList())
        {
            var price = lastPrices.GetValueOrDefault(pos.Symbol, pos.PriceOpen);
            var trade = ClosePosition(pos.Ticket, price, closeTime, "EOT"); // End Of Test
            if (trade != null) trades.Add(trade);
        }
        return trades;
    }

    // ── Modify SL ────────────────────────────────────────────

    /// <summary>Modify SL on an open position (instant, like live).</summary>
    public bool ModifySL(long ticket, double newSl)
    {
        var pos = OpenPositions.FirstOrDefault(p => p.Ticket == ticket);
        if (pos == null) return false;
        pos.SL = newSl;
        return true;
    }

    // ── SL/TP Check ──────────────────────────────────────────

    /// <summary>
    /// Check all PREVIOUSLY opened positions for SL/TP hits on the given bar.
    /// Positions opened on THIS bar are excluded (they start checking on the next bar).
    /// Returns list of closed trades.
    ///
    /// Gap execution: if bar opens beyond SL/TP, fills at Open (not the level).
    /// Priority: SL wins over TP when both hit on same bar.
    /// </summary>
    public List<BtTrade> CheckSLTP(Dictionary<string, Bar> currentBars, long currentBarTime,
                                    HashSet<long>? excludeTickets = null)
    {
        var closed = new List<BtTrade>();

        foreach (var pos in OpenPositions.ToList())
        {
            // Skip positions opened on this bar
            if (excludeTickets != null && excludeTickets.Contains(pos.Ticket))
                continue;

            if (!currentBars.TryGetValue(pos.Symbol, out var bar))
                continue;

            bool isBuy = pos.Direction == "BUY";
            string? closeReason = null;
            double closePrice = 0;

            // ── SL check (priority) ──
            if (pos.SL > 0)
            {
                if (isBuy && bar.Low <= pos.SL)
                {
                    closeReason = "SL";
                    closePrice = bar.Open <= pos.SL ? bar.Open : pos.SL;
                }
                else if (!isBuy && bar.High >= pos.SL)
                {
                    closeReason = "SL";
                    closePrice = bar.Open >= pos.SL ? bar.Open : pos.SL;
                }
            }

            // ── TP check (only if SL didn't trigger) ──
            if (closeReason == null && pos.TP > 0)
            {
                if (isBuy && bar.High >= pos.TP)
                {
                    closeReason = "TP";
                    closePrice = bar.Open >= pos.TP ? bar.Open : pos.TP;
                }
                else if (!isBuy && bar.Low <= pos.TP)
                {
                    closeReason = "TP";
                    closePrice = bar.Open <= pos.TP ? bar.Open : pos.TP;
                }
            }

            if (closeReason == null) continue;

            // Close the position
            var trade = ClosePosition(pos.Ticket, closePrice, currentBarTime, closeReason);
            if (trade != null) closed.Add(trade);
        }

        return closed;
    }

    // ── Pending Orders ───────────────────────────────────────

    /// <summary>
    /// Register a pending stop order. Called when strategy sends ENTER_PENDING.
    /// Ticket is assigned immediately (negative counter).
    /// </summary>
    public BtPendingOrder PlacePending(
        StrategyAction action, long barTime, double lot, string strategy, int magic)
    {
        var ticket = NextTicket--;
        var pending = new BtPendingOrder
        {
            Ticket        = ticket,
            Symbol        = action.Symbol!,
            Direction     = action.Direction!.ToUpperInvariant() == "LONG" ? "BUY" : "SELL",
            OrderType     = action.Direction!.ToUpperInvariant() == "LONG" ? "BUY_STOP" : "SELL_STOP",
            Volume        = lot,
            EntryPrice    = action.EntryPrice!.Value,
            SL            = action.SlPrice ?? 0,
            TP            = action.TpPrice ?? 0,
            BarsRemaining = (action.ExpiryBars ?? 0) > 0 ? action.ExpiryBars!.Value : -1,
            SignalData    = action.SignalData,
            Strategy      = strategy,
            Magic         = magic,
            PlacedTime    = barTime,
        };
        PendingOrders.Add(pending);
        return pending;
    }

    /// <summary>
    /// Check pending orders for trigger conditions on the current bar.
    /// Must be called BEFORE CheckSLTP and BEFORE processing ACTIONS.
    /// Returns newly opened positions (to exclude from SL/TP check on same bar).
    /// </summary>
    public List<BtPosition> CheckPendingTriggers(
        Dictionary<string, Bar> currentBars, long currentBarTime, HashSet<long> newTickets)
    {
        var filled = new List<BtPosition>();

        foreach (var pending in PendingOrders.ToList())
        {
            // Decrement expiry
            if (pending.BarsRemaining > 0)
            {
                pending.BarsRemaining--;
                if (pending.BarsRemaining == 0)
                {
                    // Expired
                    PendingOrders.Remove(pending);
                    BlockedSignals.Add(new BtBlockedSignal
                    {
                        BarTime    = currentBarTime,
                        Symbol     = pending.Symbol,
                        Direction  = pending.Direction == "BUY" ? "LONG" : "SHORT",
                        Gate       = "PENDING_EXPIRED",
                        Reason     = $"Pending {pending.OrderType} expired after {pending.BarsRemaining} bars",
                        EntryPrice = pending.EntryPrice,
                        SignalData = pending.SignalData,
                    });
                    continue;
                }
            }

            if (!currentBars.TryGetValue(pending.Symbol, out var bar)) continue;

            bool isBuyStop = pending.OrderType == "BUY_STOP";
            bool triggered = isBuyStop
                ? bar.High >= pending.EntryPrice
                : bar.Low  <= pending.EntryPrice;

            if (!triggered) continue;

            // Gap fill: if bar opened beyond entry → fill at Open
            double fillPrice = isBuyStop
                ? (bar.Open >= pending.EntryPrice ? bar.Open : pending.EntryPrice)
                : (bar.Open <= pending.EntryPrice ? bar.Open : pending.EntryPrice);

            // Build synthetic action for OpenPosition
            var synthAction = new Daemon.Strategy.StrategyAction
            {
                Action     = "ENTER",
                Symbol     = pending.Symbol,
                Direction  = pending.Direction == "BUY" ? "LONG" : "SHORT",
                SlPrice    = pending.SL,
                TpPrice    = pending.TP > 0 ? pending.TP : null,
                SignalData = pending.SignalData,
                Comment    = pending.OrderType,
            };

            var pos = OpenPosition(synthAction, fillPrice, currentBarTime,
                pending.Volume, pending.Strategy, pending.Magic);

            if (pos != null)
            {
                filled.Add(pos);
                newTickets.Add(pos.Ticket);
            }

            PendingOrders.Remove(pending);

            // OCO: cancel siblings with same SignalData
            if (!string.IsNullOrEmpty(pending.SignalData))
            {
                var siblings = PendingOrders
                    .Where(p => p.SignalData == pending.SignalData && p.Ticket != pending.Ticket)
                    .ToList();
                foreach (var sib in siblings)
                    PendingOrders.Remove(sib);
            }
        }

        return filled;
    }

    // ── Record Blocked Signal ────────────────────────────────

    public void RecordBlockedSignal(StrategyAction action, string gate, string reason,
                                     long barTime, double entryPrice)
    {
        BlockedSignals.Add(new BtBlockedSignal
        {
            Symbol = action.Symbol ?? "",
            Direction = action.Direction ?? "",
            SlPrice = action.SlPrice ?? 0,
            TpPrice = action.TpPrice ?? 0,
            Gate = gate,
            Reason = reason,
            BarTime = barTime,
            EntryPrice = entryPrice,
        });

        // Increment gate stats
        if (!GateStats.ContainsKey(gate))
            GateStats[gate] = 0;
        GateStats[gate]++;
    }

    // ── Equity Snapshot ──────────────────────────────────────

    /// <summary>Record equity at the current bar time.</summary>
    public void RecordEquity(long barTime, Dictionary<string, Bar> currentBars)
    {
        double unrealized = GetUnrealizedPnl(currentBars);
        EquitySnapshots.Add(new BtEquitySnapshot
        {
            Time = barTime,
            Balance = Balance,
            Equity = Balance + unrealized,
            Unrealized = unrealized,
            OpenCount = OpenPositions.Count,
        });
    }

    // ── Unrealized P&L ───────────────────────────────────────

    public double GetUnrealizedPnl(Dictionary<string, Bar> currentBars)
    {
        double total = 0;
        foreach (var pos in OpenPositions)
        {
            if (!currentBars.TryGetValue(pos.Symbol, out var bar)) continue;
            total += CalculateRawPnl(pos, bar.Close);
        }
        return total;
    }

    // ── Daily R Tracking ─────────────────────────────────────

    /// <summary>Get total R spent today for a given strategy.</summary>
    public double GetDailyR(string brokerDate)
    {
        return _dailyR.GetValueOrDefault(brokerDate, 0);
    }

    /// <summary>Get daily realized PnL for a given date.</summary>
    public double GetDailyPnl(string brokerDate)
    {
        return _dailyPnl.GetValueOrDefault(brokerDate, 0);
    }

    // ── Current Equity ───────────────────────────────────────

    public double GetEquity(Dictionary<string, Bar> currentBars)
    {
        return Balance + GetUnrealizedPnl(currentBars);
    }

    // ══════════════════════════════════════════════════════════
    //  Private — P&L Calculation
    // ══════════════════════════════════════════════════════════

    private BtTrade BuildTrade(BtPosition pos, double closePrice, long closeTime, string reason)
    {
        // Raw P&L from price movement (using InstrumentCard tick math)
        double rawPnl = CalculateRawPnl(pos, closePrice);

        // Flat cost (spread + slippage in price units → converted to $)
        double flatCostDollar = 0;
        double flatCostR = 0;
        if (_flatCostPrice.TryGetValue(pos.Symbol, out var costPrice) && costPrice > 0
            && _cards.TryGetValue(pos.Symbol, out var card) && card.TradeTickSize > 0)
        {
            double costTicks = costPrice / card.TradeTickSize;
            double tickVal = card.TradeTickValueLoss > 0 ? card.TradeTickValueLoss : card.TradeTickValue;
            flatCostDollar = costTicks * tickVal * pos.Volume;

            // Cost in R-units: cost_price / sl_distance
            double slDist = pos.OriginalSlDist > 0 
                ? pos.OriginalSlDist 
                : Math.Abs(pos.PriceOpen - pos.SL);
            if (slDist > 0)
                flatCostR = costPrice / slDist;
        }

        // Commission ($/lot round-trip, affects $ only, not R)
        double commissionDollar = Commission * pos.Volume;

        // Dollar P&L = raw - cost - commission
        double pnlDollar = rawPnl - flatCostDollar - commissionDollar;

        // R-result: price movement / original SL distance - cost_in_R
        double rResult = 0;
        double slDistance = pos.OriginalSlDist > 0 
            ? pos.OriginalSlDist 
            : Math.Abs(pos.PriceOpen - pos.SL);
        if (slDistance > 0)
        {
            bool isBuy = pos.Direction == "BUY";
            double priceMove = isBuy ? closePrice - pos.PriceOpen : pos.PriceOpen - closePrice;
            rResult = (priceMove / slDistance) - flatCostR;
        }

        // Track daily R
        var brokerDate = DateTimeOffset.FromUnixTimeSeconds(closeTime).ToString("yyyy-MM-dd");
        if (!_dailyR.ContainsKey(brokerDate)) _dailyR[brokerDate] = 0;
        _dailyR[brokerDate] += rResult;

        // Track daily PnL
        if (!_dailyPnl.ContainsKey(brokerDate)) _dailyPnl[brokerDate] = 0;
        _dailyPnl[brokerDate] += pnlDollar;

        return new BtTrade
        {
            Ticket = pos.Ticket,
            Symbol = pos.Symbol,
            Direction = pos.Direction,
            Volume = pos.Volume,
            PriceOpen = pos.PriceOpen,
            PriceClose = closePrice,
            OpenTime = pos.OpenTime,
            CloseTime = closeTime,
            SL = pos.SL,
            TP = pos.TP,
            Reason = reason,
            PnlRaw = rawPnl,
            FlatCostDollar = flatCostDollar,
            CommissionDollar = commissionDollar,
            PnlDollar = pnlDollar,
            RResult = Math.Round(rResult, 4),
            FlatCostR = Math.Round(flatCostR, 4),
            Strategy = pos.Strategy,
            Magic = pos.Magic,
            SignalData = pos.SignalData,
        };
    }

    /// <summary>Raw P&L from price movement (no cost, no commission).</summary>
    private double CalculateRawPnl(BtPosition pos, double closePrice)
    {
        if (!_cards.TryGetValue(pos.Symbol, out var card) || card.TradeTickSize <= 0)
            return 0;

        double priceDiff = pos.Direction == "BUY"
            ? closePrice - pos.PriceOpen
            : pos.PriceOpen - closePrice;

        double ticks = priceDiff / card.TradeTickSize;
        double tickValue = priceDiff >= 0 ? card.TradeTickValueProfit : card.TradeTickValueLoss;
        if (tickValue <= 0) tickValue = card.TradeTickValue;
        if (tickValue <= 0) return 0;

        return ticks * tickValue * pos.Volume;
    }
}

// ══════════════════════════════════════════════════════════════
//  DTOs
// ══════════════════════════════════════════════════════════════

public class BtPosition
{
    public long Ticket { get; set; }
    public string Symbol { get; set; } = "";
    public string Direction { get; set; } = "";  // BUY/SELL
    public double Volume { get; set; }
    public double PriceOpen { get; set; }
    public long OpenTime { get; set; }
    public double SL { get; set; }
    public double TP { get; set; }
    public string Strategy { get; set; } = "";
    public int Magic { get; set; }
    public string? SignalData { get; set; }
    public string? Comment { get; set; }
    public double OriginalSlDist { get; set; }   // original SL distance from signal_data (fixed at entry)
}

public class BtPendingOrder
{
    public long   Ticket        { get; set; }
    public string Symbol        { get; set; } = "";
    public string Direction     { get; set; } = "";  // BUY/SELL
    public string OrderType     { get; set; } = "";  // BUY_STOP/SELL_STOP
    public double Volume        { get; set; }
    public double EntryPrice    { get; set; }
    public double SL            { get; set; }
    public double TP            { get; set; }
    public int    BarsRemaining { get; set; }        // -1 = GTC
    public string? SignalData   { get; set; }
    public string Strategy      { get; set; } = "";
    public int    Magic         { get; set; }
    public long   PlacedTime    { get; set; }
}

public class BtTrade
{
    public long Ticket { get; set; }
    public string Symbol { get; set; } = "";
    public string Direction { get; set; } = "";
    public double Volume { get; set; }
    public double PriceOpen { get; set; }
    public double PriceClose { get; set; }
    public long OpenTime { get; set; }
    public long CloseTime { get; set; }
    public double SL { get; set; }
    public double TP { get; set; }
    public string Reason { get; set; } = "";  // SL/TP/EXIT/EOT
    public double PnlRaw { get; set; }        // raw price P&L (no cost)
    public double FlatCostDollar { get; set; }
    public double CommissionDollar { get; set; }
    public double PnlDollar { get; set; }      // final: raw - cost - commission
    public double RResult { get; set; }        // R-units (cost included, commission excluded)
    public double FlatCostR { get; set; }      // cost in R-units
    public string Strategy { get; set; } = "";
    public int Magic { get; set; }
    public string? SignalData { get; set; }
    public double BalanceAfter { get; set; }   // balance after this trade
}

public class BtBlockedSignal
{
    public string Symbol { get; set; } = "";
    public string Direction { get; set; } = "";
    public double SlPrice { get; set; }
    public double TpPrice { get; set; }
    public string Gate { get; set; } = "";
    public string Reason { get; set; } = "";
    public long BarTime { get; set; }
    public double EntryPrice { get; set; }
    public string? SignalData { get; set; }
}

public class BtEquitySnapshot
{
    public long Time { get; set; }
    public double Balance { get; set; }
    public double Equity { get; set; }
    public double Unrealized { get; set; }
    public int OpenCount { get; set; }
}

/// <summary>Configuration for a backtest run.</summary>
public class BacktestConfig
{
    public string Strategy { get; set; } = "";
    public string TerminalId { get; set; } = "";
    public long FromTs { get; set; }
    public long ToTs { get; set; }
    public string Timeframe { get; set; } = "M30";
    public double Deposit { get; set; } = 100000;
    public double CommissionPerLot { get; set; } = 7.0;
    public int Magic { get; set; } = 9000;          // backtest magic
    public double? RCap { get; set; }

    /// <summary>Symbols to include (from strategy requirements).</summary>
    public List<string> Symbols { get; set; } = new();

    /// <summary>Timeframes per symbol (from strategy requirements).</summary>
    public Dictionary<string, string> Timeframes { get; set; } = new();

    /// <summary>Warmup bars count (from strategy requirements).</summary>
    public int HistoryBars { get; set; } = 300;

    /// <summary>Data source identifier (terminal/broker). Used to select correct bar data.</summary>
    public string Source { get; set; } = "legacy";

    /// <summary>Per-symbol risk factors from sizing config. Overrides strategy's own sizing.</summary>
    public Dictionary<string, double> SizingFactors { get; set; } = new();

    /// <summary>Max margin per trade as % of balance (G5). 0 = disabled. E.g. 1.0 = 1% for The5ers 1:100.</summary>
    public double MaxMarginPct { get; set; } = 0;
}

/// <summary>Complete result of a backtest run.</summary>
public class BacktestResult
{
    public string Strategy { get; set; } = "";
    public string TerminalId { get; set; } = "";
    public long FromTs { get; set; }
    public long ToTs { get; set; }
    public string Timeframe { get; set; } = "";
    public double InitialBalance { get; set; }

    // Summary
    public int TotalTrades { get; set; }
    public int Wins { get; set; }
    public int Losses { get; set; }
    public double WinRate { get; set; }
    public int BlockedSignals { get; set; }
    public int BarsProcessed { get; set; }
    public double DurationSec { get; set; }

    // R-metrics
    public double TotalR { get; set; }
    public double MaxDdR { get; set; }
    public double CalmarR { get; set; }
    public double BestDayR { get; set; }
    public double WorstDayR { get; set; }

    // $-metrics
    public double FinalBalance { get; set; }
    public double TotalPnlDollar { get; set; }
    public double MaxDdDollar { get; set; }
    public double ProfitFactor { get; set; }
    public double TotalCommission { get; set; }
    public double TotalCost { get; set; }
    public double? RCap { get; set; }

    // Per-symbol R breakdown
    public Dictionary<string, double> PerSymbolR { get; set; } = new();

    // Gate stats
    public Dictionary<string, int> GateStats { get; set; } = new();

    // Detailed data (for charts)
    public List<BtTrade> Trades { get; set; } = new();
    public List<BtBlockedSignal> BlockedSignalsList { get; set; } = new();
    public List<BtEquitySnapshot> EquityCurve { get; set; } = new();

    // Status
    public bool Cancelled { get; set; }
    public string? Error { get; set; }
}
