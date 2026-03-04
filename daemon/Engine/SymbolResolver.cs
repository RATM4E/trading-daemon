namespace Daemon.Engine;

/// <summary>
/// Unified symbol resolution: canonical ↔ broker.
///
/// Sources (priority order):
///   1. Per-terminal symbol_map from config.json (explicit overrides)
///   2. Terminal symbol cache (learned from CHECK_SYMBOLS / MT5 symbols_get)
///   3. CostModel v2 aliases (known_variants + broker_map)
///   4. Strip algorithm (remove suffixes like .m, .pro, i, etc.)
///
/// Usage:
///   resolver.ToBroker("DE40", terminalId)  → "DAX40"  (for sending to MT5)
///   resolver.ToCanonical("DAX40")          → "DE40"   (for receiving from MT5)
///
/// Architecture:
///   - Single instance per daemon lifetime
///   - Per-terminal symbol_map loaded from config
///   - Terminal symbol cache updated after each CHECK_SYMBOLS
///   - CostModel aliases loaded once at startup
///   - Used by ConnectorManager, BacktestEngine, Sizing, everywhere
/// </summary>
public class SymbolResolver
{
    // ── Per-terminal explicit overrides (config.json symbol_map) ──
    // terminalId → { canonical → broker }
    private readonly Dictionary<string, Dictionary<string, string>> _explicitToB = new();
    // terminalId → { broker → canonical }
    private readonly Dictionary<string, Dictionary<string, string>> _explicitToC = new();

    // ── Terminal symbol cache: what symbols actually exist on each terminal ──
    // terminalId → { UPPER(brokerSymbol) → actualBrokerSymbol }
    private readonly Dictionary<string, Dictionary<string, string>> _terminalSymbols = new();

    // ── CostModel aliases: canonical ↔ variants ──
    // canonical → list of known variants (includes the canonical itself)
    private readonly Dictionary<string, List<string>> _canonicalToVariants = new(StringComparer.OrdinalIgnoreCase);
    // variant → canonical (reverse lookup, case-insensitive)
    private readonly Dictionary<string, string> _variantToCanonical = new(StringComparer.OrdinalIgnoreCase);

    // ── Strip patterns (same as CostModelLoader) ──
    private static readonly System.Text.RegularExpressions.Regex StripSpecialChars =
        new(@"[.#!+@'_\-].*$", System.Text.RegularExpressions.RegexOptions.Compiled);
    private static readonly System.Text.RegularExpressions.Regex StripAccountSuffix =
        new(@"(m|micro|pro|ecn|b|zero|c|i|t|f|h|x|me|check)$",
            System.Text.RegularExpressions.RegexOptions.Compiled | System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    // ═══════════════════════════════════════════════════════════
    //  SETUP
    // ═══════════════════════════════════════════════════════════

    /// <summary>Load per-terminal symbol_map from config.</summary>
    public void LoadTerminalMap(string terminalId, Dictionary<string, string> canonicalToBroker)
    {
        var toB = new Dictionary<string, string>(canonicalToBroker, StringComparer.OrdinalIgnoreCase);
        var toC = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var kv in canonicalToBroker)
            toC[kv.Value] = kv.Key;

        _explicitToB[terminalId] = toB;
        _explicitToC[terminalId] = toC;
    }

    /// <summary>
    /// Load aliases from cost_model_v2.json.
    /// Call once at startup after CostModelLoader is ready.
    /// </summary>
    public void LoadCostModelAliases(string costModelJson)
    {
        using var doc = System.Text.Json.JsonDocument.Parse(costModelJson);
        var root = doc.RootElement;

        // Also load symbol names as self-mapping
        if (root.TryGetProperty("symbols", out var symbolsEl))
        {
            foreach (var prop in symbolsEl.EnumerateObject())
            {
                var canonical = prop.Name;
                _variantToCanonical.TryAdd(canonical, canonical);
                if (!_canonicalToVariants.ContainsKey(canonical))
                    _canonicalToVariants[canonical] = new List<string> { canonical };
            }
        }

        if (!root.TryGetProperty("_aliases", out var aliasesEl))
            return;

        foreach (var prop in aliasesEl.EnumerateObject())
        {
            var canonical = prop.Name;

            // Self-mapping
            _variantToCanonical.TryAdd(canonical, canonical);
            if (!_canonicalToVariants.ContainsKey(canonical))
                _canonicalToVariants[canonical] = new List<string> { canonical };

            // known_variants
            if (prop.Value.TryGetProperty("known_variants", out var variants))
            {
                foreach (var v in variants.EnumerateArray())
                {
                    var variant = v.GetString();
                    if (string.IsNullOrEmpty(variant)) continue;
                    _variantToCanonical.TryAdd(variant, canonical);
                    _canonicalToVariants[canonical].Add(variant);
                }
            }

            // broker_map values
            if (prop.Value.TryGetProperty("broker_map", out var brokerMap))
            {
                foreach (var bm in brokerMap.EnumerateObject())
                {
                    var brokerSymbol = bm.Value.GetString();
                    if (string.IsNullOrEmpty(brokerSymbol)) continue;

                    foreach (var part in brokerSymbol.Split(','))
                    {
                        var clean = part.Trim();
                        var parenIdx = clean.IndexOf('(');
                        if (parenIdx > 0) clean = clean.Substring(0, parenIdx).Trim();
                        if (!string.IsNullOrEmpty(clean) && !clean.Contains(' '))
                        {
                            _variantToCanonical.TryAdd(clean, canonical);
                            _canonicalToVariants[canonical].Add(clean);
                        }
                    }
                }
            }
        }
    }

    /// <summary>
    /// Cache terminal's available symbols (from CHECK_SYMBOLS or symbols_get).
    /// Call after connecting to terminal or after CHECK_SYMBOLS response.
    /// </summary>
    public void CacheTerminalSymbols(string terminalId, IEnumerable<string> brokerSymbols)
    {
        var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var sym in brokerSymbols)
            dict[sym] = sym;  // preserves original casing
        _terminalSymbols[terminalId] = dict;
    }

    /// <summary>Update terminal cache with resolved pairs from CHECK_SYMBOLS.</summary>
    public void CacheResolvedSymbols(string terminalId, Dictionary<string, string> canonicalToBroker)
    {
        if (!_terminalSymbols.ContainsKey(terminalId))
            _terminalSymbols[terminalId] = new(StringComparer.OrdinalIgnoreCase);

        foreach (var kv in canonicalToBroker)
            _terminalSymbols[terminalId][kv.Value] = kv.Value;
    }

    // ═══════════════════════════════════════════════════════════
    //  RESOLVE: canonical → broker
    // ═══════════════════════════════════════════════════════════

    /// <summary>
    /// Resolve canonical symbol to broker symbol for a specific terminal.
    ///
    /// Chain:
    ///   1. Explicit symbol_map (user override)
    ///   2. Terminal symbol cache + alias variants (try each variant on this terminal)
    ///   3. Pass through as-is (let MT5 try)
    /// </summary>
    public string ToBroker(string canonical, string terminalId)
    {
        if (string.IsNullOrEmpty(canonical)) return canonical;

        // 1. Explicit override from config
        if (_explicitToB.TryGetValue(terminalId, out var explMap) &&
            explMap.TryGetValue(canonical, out var broker))
            return broker;

        // 2. Terminal has the exact canonical name?
        if (_terminalSymbols.TryGetValue(terminalId, out var termSyms))
        {
            if (termSyms.ContainsKey(canonical))
                return termSyms[canonical];

            // Try all known variants of this canonical
            if (_canonicalToVariants.TryGetValue(canonical, out var variants))
            {
                foreach (var v in variants)
                {
                    if (termSyms.ContainsKey(v))
                        return termSyms[v];
                }
            }

            // Maybe input is itself a variant? Resolve to canonical, then try its variants
            if (_variantToCanonical.TryGetValue(canonical, out var realCanonical) &&
                !realCanonical.Equals(canonical, StringComparison.OrdinalIgnoreCase))
            {
                if (termSyms.ContainsKey(realCanonical))
                    return termSyms[realCanonical];

                if (_canonicalToVariants.TryGetValue(realCanonical, out var variants2))
                {
                    foreach (var v in variants2)
                    {
                        if (termSyms.ContainsKey(v))
                            return termSyms[v];
                    }
                }
            }
        }

        // 3. Pass through — MT5 will try to resolve or fail
        return canonical;
    }

    /// <summary>Resolve canonical to broker, terminal-agnostic (for cost model).</summary>
    public string ToBrokerAny(string canonical)
    {
        // No terminal context — just return canonical (cost model uses canonical)
        return canonical;
    }

    // ═══════════════════════════════════════════════════════════
    //  RESOLVE: broker → canonical
    // ═══════════════════════════════════════════════════════════

    /// <summary>
    /// Resolve broker symbol to canonical name.
    ///
    /// Chain:
    ///   1. Explicit symbol_map reverse
    ///   2. CostModel variant lookup
    ///   3. Strip algorithm
    ///   4. Pass through as-is
    /// </summary>
    public string ToCanonical(string brokerSymbol, string? terminalId = null)
    {
        if (string.IsNullOrEmpty(brokerSymbol)) return brokerSymbol;

        // 1. Explicit reverse lookup
        if (terminalId != null &&
            _explicitToC.TryGetValue(terminalId, out var explRevMap) &&
            explRevMap.TryGetValue(brokerSymbol, out var canonical))
            return canonical;

        // 2. CostModel variant lookup (case-insensitive)
        if (_variantToCanonical.TryGetValue(brokerSymbol, out var canon2))
            return canon2;

        // 3. Strip algorithm
        var stripped = StripSpecialChars.Replace(brokerSymbol, "");
        if (stripped != brokerSymbol && _variantToCanonical.TryGetValue(stripped, out var canon3))
            return canon3;

        var stripped2 = StripAccountSuffix.Replace(stripped, "");
        if (stripped2 != stripped && stripped2.Length >= 3 &&
            _variantToCanonical.TryGetValue(stripped2, out var canon4))
            return canon4;

        // 4. Pass through
        return brokerSymbol;
    }

    // ═══════════════════════════════════════════════════════════
    //  QUERY HELPERS
    // ═══════════════════════════════════════════════════════════

    /// <summary>Get all known variants for a canonical symbol.</summary>
    public List<string> GetVariants(string canonical)
    {
        // Direct lookup
        if (_canonicalToVariants.TryGetValue(canonical, out var variants))
            return variants;

        // Maybe input is a variant? Resolve first
        if (_variantToCanonical.TryGetValue(canonical, out var realCanonical) &&
            _canonicalToVariants.TryGetValue(realCanonical, out var variants2))
            return variants2;

        return new List<string> { canonical };
    }

    /// <summary>Check if canonical symbol (or any variant) is available on a terminal.</summary>
    public bool IsAvailable(string canonical, string terminalId)
    {
        return ToBroker(canonical, terminalId) != canonical ||
               (_terminalSymbols.TryGetValue(terminalId, out var syms) && syms.ContainsKey(canonical));
    }

    /// <summary>Total alias entries loaded (for logging).</summary>
    public int AliasCount => _variantToCanonical.Count;

    /// <summary>Number of canonical symbols with aliases.</summary>
    public int CanonicalCount => _canonicalToVariants.Count;

    /// <summary>Export full alias table as dict for sending to Python worker.</summary>
    public Dictionary<string, List<string>> ExportAliasTable() =>
        new(_canonicalToVariants, StringComparer.OrdinalIgnoreCase);
}
