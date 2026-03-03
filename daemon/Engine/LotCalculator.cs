using Daemon.Models;

namespace Daemon.Engine;

/// <summary>
/// Calculates position size (lots) based on risk in account currency.
///
/// Two modes:
///   1) Preferred: loss1Lot from MT5 OrderCalcProfit (exact, handles all conversions)
///      lot = risk_money / loss1Lot
///
///   2) Fallback: tick math (when MT5 unavailable)
///      distance = |entry - sl|
///      ticks = distance / tick_size
///      lot = risk_money / (ticks * tick_value_loss)
///
/// lot = floor(lot / volume_step) * volume_step
///
/// Uses tick_value_loss (not tick_value_profit) because we're sizing for the SL scenario.
/// For cross-pairs, MT5 provides live tick_value that accounts for currency conversion.
/// </summary>
public static class LotCalculator
{
    public static LotResult Calculate(
        double entryPrice,
        double slPrice,
        double riskMoney,
        InstrumentCard card,
        double loss1Lot = 0)
    {
        // Validate inputs
        if (riskMoney <= 0)
            return LotResult.Rejected("Risk money must be positive");
        if (Math.Abs(entryPrice - slPrice) < card.Point * 0.5)
            return LotResult.Rejected("SL too close to entry (< 1 point)");
        if (card.VolumeStep <= 0)
            return LotResult.Rejected("Invalid instrument card (volume_step = 0)");

        double distance = Math.Abs(entryPrice - slPrice);
        double rawLot;
        double tickValue;
        double ticks;
        string calcMethod;

        // --- Mode 1: MT5 OrderCalcProfit (preferred, exact for all symbol types) ---
        if (loss1Lot > 0)
        {
            rawLot = riskMoney / loss1Lot;
            ticks = card.TradeTickSize > 0 ? distance / card.TradeTickSize : 0;
            tickValue = ticks > 0 ? loss1Lot / ticks : 0;
            calcMethod = "mt5_calc_profit";
        }
        // --- Mode 2: Tick math fallback ---
        else
        {
            if (card.TradeTickSize <= 0)
                return LotResult.Rejected("Invalid instrument card (tick_size = 0)");

            // Use tick_value_loss for SL-based sizing (falls back to tick_value if loss = 0)
            tickValue = card.TradeTickValueLoss > 0 ? card.TradeTickValueLoss : card.TradeTickValue;
            if (tickValue <= 0)
                return LotResult.Rejected("tick_value is 0 — symbol may be offline");

            ticks = distance / card.TradeTickSize;
            rawLot = riskMoney / (ticks * tickValue);
            calcMethod = "tick_math";
        }

        // Round down to nearest volume_step
        double lot = Math.Floor(rawLot / card.VolumeStep) * card.VolumeStep;

        // Round to avoid floating-point artifacts (e.g. 0.049999999 → 0.05)
        int stepDecimals = CountDecimals(card.VolumeStep);
        lot = Math.Round(lot, stepDecimals);

        // Check bounds
        string? warning = null;

        if (lot < card.VolumeMin)
        {
            return LotResult.Rejected(
                $"Calculated lot {rawLot:F4} rounds to {lot:F2} < volume_min {card.VolumeMin}. " +
                $"Need more risk (${riskMoney:F0}) or wider SL.");
        }

        if (lot > card.VolumeMax)
        {
            warning = $"Lot capped at volume_max {card.VolumeMax} (calculated {lot:F2})";
            lot = card.VolumeMax;
        }

        // Calculate actual risk at this lot size
        double actualRisk = loss1Lot > 0 ? loss1Lot * lot : ticks * tickValue * lot;

        return new LotResult
        {
            Allowed = true,
            Lot = lot,
            ActualRisk = Math.Round(actualRisk, 2),
            RequestedRisk = riskMoney,
            Distance = distance,
            Ticks = ticks,
            TickValue = tickValue,
            Warning = warning,
            CalcMethod = calcMethod,
        };
    }

    /// <summary>
    /// Calculate risk money from profile settings.
    /// Supports both absolute USD and percentage of balance.
    /// </summary>
    public static double GetRiskMoney(TerminalProfile profile, double balance)
    {
        return profile.RiskType.ToLowerInvariant() switch
        {
            "pct" => balance * profile.MaxRiskTrade / 100.0,
            _ => profile.MaxRiskTrade, // "usd" or default
        };
    }

    private static int CountDecimals(double value)
    {
        var s = value.ToString("G", System.Globalization.CultureInfo.InvariantCulture);
        int dot = s.IndexOf('.');
        return dot < 0 ? 0 : s.Length - dot - 1;
    }
}

public class LotResult
{
    public bool Allowed { get; set; }
    public double Lot { get; set; }
    public double ActualRisk { get; set; }
    public double RequestedRisk { get; set; }
    public double Distance { get; set; }
    public double Ticks { get; set; }
    public double TickValue { get; set; }
    public string? Reason { get; set; }
    public string? Warning { get; set; }
    public string? CalcMethod { get; set; }

    public static LotResult Rejected(string reason) => new()
    {
        Allowed = false, Reason = reason
    };
}
