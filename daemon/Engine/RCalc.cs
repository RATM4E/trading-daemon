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
    /// </summary>
    public static double? GetRResult(string? closeReason, bool protectorFired, string? signalDataJson)
    {
        if (string.IsNullOrEmpty(closeReason))
            return null;

        var reason = closeReason.ToUpperInvariant();

        // TP hit → +tp_r from signal_data
        if (reason == "TP")
        {
            double tpR = ParseSignalField(signalDataJson, "tp_r") ?? 1.0;
            return tpR;
        }

        // SL hit
        if (reason == "SL")
        {
            if (protectorFired)
            {
                // Protector moved SL → use protector_lock_r from signal_data
                double lockR = ParseSignalField(signalDataJson, "protector_lock_r") ?? -0.5;
                return lockR;
            }
            else
            {
                // Raw SL hit → -1.0R
                return -1.0;
            }
        }

        // manual, signal, stopout, unknown → don't count in R-budget
        return null;
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
