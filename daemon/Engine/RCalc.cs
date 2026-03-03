using System.Text.Json;

namespace Daemon.Engine;

/// <summary>
/// Calculates R-result for a closed position.
/// R is measured relative to the intended SL distance, not dollars.
///
/// Results:
///   TP hit              → +tp_r  (from signal_data, e.g. +1.0 or +1.5)
///   SL hit, no protector → -1.0R
///   SL hit, protector   → protector_lock_r (from signal_data, e.g. -0.50)
///   manual/other        → null   (skip, don't count in R-budget)
///
/// signal_data JSON example from strategy:
///   { "tp_r": 1.5, "protector_lock_r": -0.5, "sl_atr": 2.0, ... }
/// </summary>
public static class RCalc
{
    /// <summary>
    /// Calculate R-result for a closed position.
    /// Returns null if R-result cannot be determined (manual close, no signal_data, etc).
    ///
    /// Price-based R (when sl_dist is in signal_data):
    ///   R = (closePrice - entryPrice) / original_sl_dist   (LONG; mirror for SHORT)
    ///   Works correctly for trail strategies where SL moves after entry.
    ///
    /// Fixed R (fallback when sl_dist is NOT in signal_data):
    ///   TP → +tp_r, SL → -1.0, protector → protector_lock_r
    /// </summary>
    public static double? GetRResult(
        string? closeReason, 
        bool protectorFired, 
        string? signalDataJson,
        double entryPrice = 0,
        double closePrice = 0,
        bool isBuy = true)
    {
        if (string.IsNullOrEmpty(closeReason))
            return null;

        var reason = closeReason.ToUpperInvariant();

        // manual, signal, stopout, unknown → don't count in R-budget
        if (reason != "TP" && reason != "SL")
            return null;

        // ── Price-based R (for trail strategies with sl_dist) ──
        double slDist = ParseSignalField(signalDataJson, "sl_dist") ?? 0;
        if (slDist > 0 && entryPrice > 0 && closePrice > 0)
        {
            double priceMove = isBuy ? closePrice - entryPrice : entryPrice - closePrice;
            return Math.Round(priceMove / slDist, 4);
        }

        // ── Fixed R fallback (non-trail strategies) ──
        if (reason == "TP")
        {
            return ParseSignalField(signalDataJson, "tp_r") ?? 1.0;
        }

        // SL
        if (protectorFired)
        {
            return ParseSignalField(signalDataJson, "protector_lock_r") ?? -0.5;
        }
        return -1.0;
    }

    /// <summary>Parse a numeric field from signal_data JSON string.</summary>
    private static double? ParseSignalField(string? json, string fieldName)
    {
        if (string.IsNullOrEmpty(json))
            return null;

        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty(fieldName, out var prop))
            {
                if (prop.ValueKind == JsonValueKind.Number)
                    return prop.GetDouble();
            }
        }
        catch
        {
            // Malformed JSON — ignore
        }

        return null;
    }
}
