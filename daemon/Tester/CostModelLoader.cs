using System.Text.Json;
using System.Text.RegularExpressions;
using Daemon.Models;

namespace Daemon.Tester;

/// <summary>
/// Loads and parses cost_model.json (v1 or v2).
/// Converts per-symbol costs (in asset-class units) into flat cost in price units
/// using InstrumentCard data from MT5.
///
/// v2 additions: _aliases section with known_variants and broker_map
/// for automatic broker symbol → canonical resolution.
///
/// Flat cost formula (matches research bt_trades()):
///   forex:  (spread + slippage) * pip_size       where pip_size = Point * 10 (5-digit) or Point (3-digit JPY)
///   index:  (spread + slippage) * Point
///   metal:  (spread + slippage) * Point
///   energy: (spread + slippage) * Point
///   crypto: (spread + slippage) * Point
/// </summary>
public class CostModelLoader
{
    // ── Raw JSON model ──────────────────────────────────────────

    public class CostModelFile
    {
        public Dictionary<string, string> Units { get; set; } = new();
        public Dictionary<string, CostSymbolEntry> Symbols { get; set; } = new();
    }

    public class CostSymbolEntry
    {
        public string AssetClass { get; set; } = "";
        public double Spread { get; set; }
        public double Slippage { get; set; }
        public double SpreadOpenWidened { get; set; }
    }

    // ── Resolved cost model ─────────────────────────────────────

    /// <summary>Per-symbol resolved cost info.</summary>
    public class ResolvedCost
    {
        public string Symbol { get; set; } = "";
        public string AssetClass { get; set; } = "";
        public string Unit { get; set; } = "";          // "pips", "points", "usd"
        public double SpreadRaw { get; set; }            // raw value from JSON
        public double SlippageRaw { get; set; }          // raw value from JSON
        public double FlatCostPrice { get; set; }        // converted to price units
        public double PipOrPointSize { get; set; }       // pip_size (forex) or Point (others)
    }

    /// <summary>Full resolved cost model.</summary>
    public class ResolvedCostModel
    {
        public Dictionary<string, ResolvedCost> Costs { get; set; } = new();

        /// <summary>Get flat cost in price units for a symbol. Returns 0 if unknown.</summary>
        public double GetFlatCost(string symbol)
        {
            return Costs.TryGetValue(symbol, out var c) ? c.FlatCostPrice : 0.0;
        }

        /// <summary>Check if symbol has cost data.</summary>
        public bool HasSymbol(string symbol) => Costs.ContainsKey(symbol);
    }

    // ── Alias resolution ─────────────────────────────────────────

    /// <summary>
    /// Reverse lookup: broker variant (case-insensitive) → canonical symbol.
    /// Built from _aliases.known_variants + _aliases.broker_map in cost model v2.
    /// </summary>
    private readonly Dictionary<string, string> _variantToCanonical
        = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Number of alias entries parsed (for logging).</summary>
    public int AliasCount => _variantToCanonical.Count;

    /// <summary>
    /// Strip algorithm from cost_model_v2:
    ///   1. Remove everything after special chars: . # ! + @ ' _ -
    ///   2. Remove known account-type suffixes: m, micro, pro, ecn, b, zero, c, i, t, f, h, x, me, check
    /// </summary>
    private static readonly Regex StripSpecialChars =
        new(@"[.#!+@'_\-].*$", RegexOptions.Compiled);

    private static readonly Regex StripAccountSuffix =
        new(@"(m|micro|pro|ecn|b|zero|c|i|t|f|h|x|me|check)$",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>
    /// Resolve a broker symbol to its canonical name.
    /// Chain: exact match in symbols → known variant lookup → strip algorithm → null.
    /// </summary>
    public string? ResolveCanonical(string brokerSymbol)
    {
        if (string.IsNullOrEmpty(brokerSymbol))
            return null;

        // 1. Direct match in cost model symbols
        if (_rawModel != null && _rawModel.Symbols.ContainsKey(brokerSymbol))
            return brokerSymbol;

        // 2. Known variant lookup (case-insensitive)
        if (_variantToCanonical.TryGetValue(brokerSymbol, out var canonical))
            return canonical;

        // 3. Strip algorithm: remove suffix/prefix variations
        var stripped = StripSpecialChars.Replace(brokerSymbol, "");
        if (stripped != brokerSymbol)
        {
            // After stripping special chars, try direct + variant match
            if (_rawModel != null && _rawModel.Symbols.ContainsKey(stripped))
                return stripped;
            if (_variantToCanonical.TryGetValue(stripped, out var canon2))
                return canon2;
        }

        // 3b. Strip account-type suffix (e.g. EURUSDm → EURUSD)
        var stripped2 = StripAccountSuffix.Replace(stripped, "");
        if (stripped2 != stripped && stripped2.Length >= 3)
        {
            if (_rawModel != null && _rawModel.Symbols.ContainsKey(stripped2))
                return stripped2;
            if (_variantToCanonical.TryGetValue(stripped2, out var canon3))
                return canon3;
        }

        return null;
    }

    // ── Loading ─────────────────────────────────────────────────

    private CostModelFile? _rawModel;

    /// <summary>Load cost model from JSON file.</summary>
    public static CostModelLoader FromFile(string path)
    {
        var loader = new CostModelLoader();
        var json = File.ReadAllText(path);
        loader._rawModel = ParseJson(json);
        ParseAliases(json, loader._variantToCanonical);
        return loader;
    }

    /// <summary>Load cost model from JSON string.</summary>
    public static CostModelLoader FromJson(string json)
    {
        var loader = new CostModelLoader();
        loader._rawModel = ParseJson(json);
        ParseAliases(json, loader._variantToCanonical);
        return loader;
    }

    /// <summary>Create a "zero costs" model (for debugging).</summary>
    public static CostModelLoader Zero()
    {
        var loader = new CostModelLoader();
        loader._rawModel = new CostModelFile();
        return loader;
    }

    /// <summary>Create a "custom uniform" model (single spread+slippage for all symbols).</summary>
    public static CostModelLoader Custom(double spreadPips, double slippagePips)
    {
        var loader = new CostModelLoader
        {
            _customSpreadPips = spreadPips,
            _customSlippagePips = slippagePips
        };
        return loader;
    }

    private double? _customSpreadPips;
    private double? _customSlippagePips;

    // ── Resolution ──────────────────────────────────────────────

    /// <summary>
    /// Resolve raw costs into flat cost prices using InstrumentCards from MT5.
    /// Must be called once per backtest run after instrument cards are loaded.
    /// Falls back to alias resolution when direct symbol match fails.
    /// </summary>
    /// <param name="instruments">Symbol → InstrumentCard from MT5</param>
    /// <returns>Resolved cost model with flat costs in price units</returns>
    public ResolvedCostModel Resolve(Dictionary<string, InstrumentCard> instruments)
    {
        var result = new ResolvedCostModel();

        foreach (var (symbol, card) in instruments)
        {
            double spreadRaw, slippageRaw;
            string assetClass, unit;

            if (_customSpreadPips.HasValue)
            {
                // Custom uniform mode — treat everything as pips
                spreadRaw = _customSpreadPips.Value;
                slippageRaw = _customSlippagePips ?? 0;
                assetClass = GuessAssetClass(symbol);
                unit = "pips";
            }
            else if (_rawModel != null && _rawModel.Symbols.TryGetValue(symbol, out var entry))
            {
                // Direct match
                spreadRaw = entry.Spread;
                slippageRaw = entry.Slippage;
                assetClass = entry.AssetClass;
                unit = _rawModel.Units.GetValueOrDefault(assetClass, "pips");
            }
            else if (_rawModel != null && ResolveCanonical(symbol) is string canonical
                     && _rawModel.Symbols.TryGetValue(canonical, out var aliasEntry))
            {
                // Alias fallback: broker symbol resolved to canonical
                spreadRaw = aliasEntry.Spread;
                slippageRaw = aliasEntry.Slippage;
                assetClass = aliasEntry.AssetClass;
                unit = _rawModel.Units.GetValueOrDefault(assetClass, "pips");
            }
            else
            {
                // Symbol not in cost model even after alias resolution — zero cost
                result.Costs[symbol] = new ResolvedCost
                {
                    Symbol = symbol,
                    AssetClass = GuessAssetClass(symbol),
                    Unit = "unknown",
                    FlatCostPrice = 0
                };
                continue;
            }

            double pipOrPoint = GetPipOrPointSize(assetClass, card);
            double flatCost = (spreadRaw + slippageRaw) * pipOrPoint;

            result.Costs[symbol] = new ResolvedCost
            {
                Symbol = symbol,
                AssetClass = assetClass,
                Unit = unit,
                SpreadRaw = spreadRaw,
                SlippageRaw = slippageRaw,
                FlatCostPrice = flatCost,
                PipOrPointSize = pipOrPoint
            };
        }

        return result;
    }

    /// <summary>Get raw entries for display in UI (before resolution).</summary>
    public Dictionary<string, CostSymbolEntry> GetRawEntries()
    {
        return _rawModel?.Symbols ?? new Dictionary<string, CostSymbolEntry>();
    }

    /// <summary>Get units mapping for display.</summary>
    public Dictionary<string, string> GetUnits()
    {
        return _rawModel?.Units ?? new Dictionary<string, string>();
    }

    // ── Helpers ──────────────────────────────────────────────────

    /// <summary>
    /// For forex: pip_size (1 pip = 10 points for 5-digit, 1 pip = 10 points for 3-digit JPY).
    /// For others: Point directly.
    /// </summary>
    private static double GetPipOrPointSize(string assetClass, InstrumentCard card)
    {
        if (assetClass == "forex")
        {
            // 5-digit broker: Point = 0.00001, pip = 0.0001 = Point * 10
            // 3-digit JPY:    Point = 0.001,   pip = 0.01   = Point * 10
            // Both cases: pip_size = Point * 10
            return card.Point * 10.0;
        }

        // Index, metal, energy, crypto — cost is in "points" or "usd"
        // In both cases the JSON values are in the broker's point units
        return card.Point;
    }

    private static string GuessAssetClass(string symbol)
    {
        if (symbol.StartsWith("XAU") || symbol.StartsWith("XAG")) return "metal";
        if (symbol.StartsWith("XTI") || symbol.StartsWith("XBR") || symbol.StartsWith("XNG")) return "energy";
        if (symbol.StartsWith("BTC") || symbol.StartsWith("ETH")) return "crypto";
        if (symbol.Contains("500") || symbol.Contains("100") || symbol.Contains("30") ||
            symbol.Contains("40") || symbol.Contains("225")) return "index";
        return "forex";
    }

    private static CostModelFile ParseJson(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var model = new CostModelFile();

        // Parse units
        if (root.TryGetProperty("units", out var unitsEl))
        {
            foreach (var prop in unitsEl.EnumerateObject())
                model.Units[prop.Name] = prop.Value.GetString() ?? "pips";
        }

        // Parse symbols
        if (root.TryGetProperty("symbols", out var symbolsEl))
        {
            foreach (var prop in symbolsEl.EnumerateObject())
            {
                var entry = new CostSymbolEntry
                {
                    AssetClass = prop.Value.TryGetProperty("asset_class", out var ac) ? ac.GetString() ?? "" : "",
                    Spread = prop.Value.TryGetProperty("spread", out var sp) ? sp.GetDouble() : 0,
                    Slippage = prop.Value.TryGetProperty("slippage", out var sl) ? sl.GetDouble() : 0,
                    SpreadOpenWidened = prop.Value.TryGetProperty("spread_open_widened", out var sow) ? sow.GetDouble() : 0
                };
                model.Symbols[prop.Name] = entry;
            }
        }

        return model;
    }

    /// <summary>
    /// Parse _aliases section from cost model JSON.
    /// Builds variant → canonical reverse lookup from known_variants and broker_map.
    /// Silently skips if _aliases section is absent (v1 compatibility).
    /// </summary>
    private static void ParseAliases(string json, Dictionary<string, string> lookup)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        if (!root.TryGetProperty("_aliases", out var aliasesEl))
            return;

        foreach (var prop in aliasesEl.EnumerateObject())
        {
            var canonical = prop.Name;  // e.g. "EURUSD", "XAUUSD"

            // The canonical name itself maps to itself
            lookup.TryAdd(canonical, canonical);

            // known_variants: ["EURUSDm", "EURUSD.ecn", "GOLD", ...]
            if (prop.Value.TryGetProperty("known_variants", out var variants))
            {
                foreach (var v in variants.EnumerateArray())
                {
                    var variant = v.GetString();
                    if (!string.IsNullOrEmpty(variant))
                        lookup.TryAdd(variant, canonical);
                }
            }

            // broker_map: { "IC Markets": "XAUUSD", "XM": "GOLD", ... }
            // Values are broker symbol names — add as variants
            if (prop.Value.TryGetProperty("broker_map", out var brokerMap))
            {
                foreach (var bm in brokerMap.EnumerateObject())
                {
                    var brokerSymbol = bm.Value.GetString();
                    if (string.IsNullOrEmpty(brokerSymbol)) continue;

                    // Some entries have notes like "XAUUSDm (std), XAUUSD (pro)"
                    // Split on comma and extract clean symbol names
                    foreach (var part in brokerSymbol.Split(','))
                    {
                        var clean = part.Trim();
                        // Remove parenthetical notes: "XAUUSDm (std)" → "XAUUSDm"
                        var parenIdx = clean.IndexOf('(');
                        if (parenIdx > 0)
                            clean = clean.Substring(0, parenIdx).Trim();

                        // Skip notes like "not available" or multi-word descriptions
                        if (!string.IsNullOrEmpty(clean) && !clean.Contains(' '))
                            lookup.TryAdd(clean, canonical);
                    }
                }
            }
        }
    }
}
