using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Daemon.Connector;

namespace Daemon.Engine;

/// <summary>
/// Economic calendar management. Maintains a cache of upcoming news events
/// and provides IsBlocked() to check if trading should be paused for a symbol.
///
/// Data sources (in priority order):
///   1. ForexFactory JSON mirror (auto-fetched on startup + every 12h)
///   2. Local file: news_calendar.json (auto-written by fetcher, manual override OK)
///
/// Impact filtering (minImpact setting per terminal):
///   3 = High only — block red events only
///   2 = High+Medium — block red + yellow events
///   1 = All — block everything
/// USD news blocks ALL instruments when news_include_usd=true.
/// Currency ↔ symbol mapping: GBPUSD is affected by both GBP and USD news.
/// </summary>
public class NewsService : IDisposable
{
    private List<NewsEvent> _events = new();
    private readonly object _lock = new();
    private DateTime _lastFetch = DateTime.MinValue;
    private readonly TimeSpan _refreshInterval = TimeSpan.FromHours(12);
    private readonly ConsoleLogger _log;
    private readonly HttpClient _http;
    private Timer? _refreshTimer;

    private string? _calendarFilePath;
    private string _lastHash = "";

    // ForexFactory JSON mirror — free, no API key
    private static readonly string[] FF_URLS = new[]
    {
        "https://nfs.faireconomy.media/ff_calendar_thisweek.json",
        "https://nfs.faireconomy.media/ff_calendar_nextweek.json",
    };

    public int EventCount { get { lock (_lock) return _events.Count; } }

    public NewsService(ConsoleLogger log, string? calendarFilePath = null, string? calendarUrl = null)
    {
        _log = log;
        _calendarFilePath = calendarFilePath;
        // calendarUrl kept for ctor compat but no longer used (ForexFactory is hardcoded)
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        _http.DefaultRequestHeaders.Add("User-Agent", "TradingDaemon/1.0");
    }

    /// <summary>Load calendar data and start periodic refresh.</summary>
    public async Task StartAsync(CancellationToken ct = default)
    {
        await RefreshAsync(ct);

        // Schedule periodic refresh every 12 hours
        _refreshTimer = new Timer(async _ =>
        {
            try { await RefreshAsync(CancellationToken.None); }
            catch (Exception ex) { _log.Error($"News refresh failed: {ex.Message}"); }
        }, null, _refreshInterval, _refreshInterval);
    }

    /// <summary>Reload calendar: fetch from API, fall back to local file.</summary>
    public async Task RefreshAsync(CancellationToken ct = default)
    {
        // Try fetching from ForexFactory API
        var fetched = await FetchForexFactoryAsync(ct);

        if (fetched.Count > 0)
        {
            // Compute hash to detect changes
            var newHash = ComputeHash(fetched);
            if (newHash != _lastHash)
            {
                // Save to local file for persistence / offline fallback
                await SaveToFileAsync(fetched, ct);
                _lastHash = newHash;
                _log.Info($"News: calendar updated — {fetched.Count} events written to {_calendarFilePath}");
            }
            else
            {
                _log.Info($"News: no changes detected — {fetched.Count} events unchanged");
            }

            ApplyEvents(fetched);
            return;
        }

        // Fallback: load from local file
        _log.Warn("News: API fetch failed — falling back to local file");
        var fileEvents = await LoadFromFileAsync(ct);
        if (fileEvents.Count > 0)
        {
            ApplyEvents(fileEvents);
            _log.Info($"News: loaded {fileEvents.Count} events from local file");
        }
        else
        {
            _log.Warn("News: no events available from any source");
        }
    }

    // ===================================================================
    // ForexFactory Fetcher
    // ===================================================================

    /// <summary>
    /// Fetch thisweek + nextweek from ForexFactory JSON mirror.
    /// Returns empty list on failure.
    /// </summary>
    private async Task<List<NewsEvent>> FetchForexFactoryAsync(CancellationToken ct)
    {
        var allEvents = new List<NewsEvent>();

        foreach (var url in FF_URLS)
        {
            try
            {
                var resp = await _http.GetAsync(url, ct);
                if (!resp.IsSuccessStatusCode)
                {
                    _log.Warn($"News: HTTP {(int)resp.StatusCode} from {url}");
                    continue;
                }

                var json = await resp.Content.ReadAsStringAsync(ct);
                var ffEvents = JsonSerializer.Deserialize<List<FFEvent>>(json, _jsonOpts);
                if (ffEvents == null) continue;

                int parsed = 0;
                foreach (var ff in ffEvents)
                {
                    if (string.IsNullOrWhiteSpace(ff.Title) || string.IsNullOrWhiteSpace(ff.Country))
                        continue;

                    // Parse date — ForexFactory uses ET offset like "2025-03-07T08:30:00-05:00"
                    if (!DateTimeOffset.TryParse(ff.Date, out var dto))
                        continue;

                    var impact = ff.Impact?.ToLowerInvariant() switch
                    {
                        "high"   => 3,
                        "medium" => 2,
                        "low"    => 1,
                        _        => 0  // Holiday / Non-Economic
                    };

                    // Skip holidays and non-economic items
                    if (impact == 0) continue;

                    allEvents.Add(new NewsEvent
                    {
                        Id = $"ff-{ff.Country}-{dto.UtcDateTime:yyyyMMddHHmm}-{ff.Title.GetHashCode():X8}",
                        TimeUtc = dto.UtcDateTime,
                        Currency = ff.Country.ToUpperInvariant(),
                        Title = ff.Title,
                        Impact = impact,
                        Forecast = ff.Forecast,
                        Previous = ff.Previous,
                    });
                    parsed++;
                }

                var label = url.Contains("thisweek") ? "thisweek" : "nextweek";
                _log.Info($"News: fetched {parsed} events from {label} ({ffEvents.Count} raw)");
            }
            catch (TaskCanceledException) { throw; }
            catch (Exception ex)
            {
                _log.Error($"News: failed to fetch {url}: {ex.Message}");
            }
        }

        return allEvents;
    }

    // ===================================================================
    // Local File I/O
    // ===================================================================

    private async Task<List<NewsEvent>> LoadFromFileAsync(CancellationToken ct)
    {
        if (string.IsNullOrEmpty(_calendarFilePath) || !File.Exists(_calendarFilePath))
            return new List<NewsEvent>();

        try
        {
            var json = await File.ReadAllTextAsync(_calendarFilePath, ct);
            return JsonSerializer.Deserialize<List<NewsEvent>>(json, _jsonOpts) ?? new();
        }
        catch (Exception ex)
        {
            _log.Error($"News: failed to load {_calendarFilePath}: {ex.Message}");
            return new();
        }
    }

    private async Task SaveToFileAsync(List<NewsEvent> events, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(_calendarFilePath)) return;

        try
        {
            var opts = new JsonSerializerOptions(_jsonOpts) { WriteIndented = true };
            var json = JsonSerializer.Serialize(events, opts);
            await File.WriteAllTextAsync(_calendarFilePath, json, ct);
        }
        catch (Exception ex)
        {
            _log.Error($"News: failed to save {_calendarFilePath}: {ex.Message}");
        }
    }

    // ===================================================================
    // Event Management
    // ===================================================================

    private void ApplyEvents(List<NewsEvent> events)
    {
        // Deduplicate by (currency, time, title)
        var deduped = events
            .GroupBy(e => $"{e.Currency}|{e.TimeUtc:yyyyMMddHHmm}|{e.Title}")
            .Select(g => g.First())
            .OrderBy(e => e.TimeUtc)
            .ToList();

        // Prune old events (> 24h ago)
        var cutoff = DateTime.UtcNow.AddHours(-24);
        deduped = deduped.Where(e => e.TimeUtc > cutoff).ToList();

        lock (_lock)
        {
            _events = deduped;
            _lastFetch = DateTime.UtcNow;
        }

        _log.Info($"News: {deduped.Count} events in cache (after dedup + prune)");
    }

    /// <summary>
    /// Load events directly (for testing or from external source).
    /// </summary>
    public void LoadEvents(List<NewsEvent> events)
    {
        lock (_lock)
        {
            _events = events.OrderBy(e => e.TimeUtc).ToList();
            _lastFetch = DateTime.UtcNow;
        }
    }

    // ===================================================================
    // IsBlocked — respects minImpact setting
    // ===================================================================

    /// <summary>
    /// Check if trading is blocked for a symbol due to upcoming news.
    /// Only events with impact >= minImpact will block.
    ///   minImpact=3 → block only High (red) events
    ///   minImpact=2 → block High + Medium (red + yellow)
    ///   minImpact=1 → block all events
    /// </summary>
    /// <param name="symbol">Canonical symbol (e.g. EURUSD, XAUUSD, BTCUSD)</param>
    /// <param name="windowMinutes">Block window before and after event</param>
    /// <param name="includeUsd">If true, USD news blocks ALL symbols</param>
    /// <param name="minImpact">Minimum impact level to block (3=High, 2=Medium, 1=All)</param>
    /// <param name="utcNow">Override current time (for testing)</param>
    public NewsBlockResult IsBlocked(string symbol, int windowMinutes = 15,
                                      bool includeUsd = true,
                                      int minImpact = 3,
                                      DateTime? utcNow = null)
    {
        var now = utcNow ?? DateTime.UtcNow;
        var relevant = GetRelevantCurrencies(symbol, includeUsd);

        lock (_lock)
        {
            foreach (var evt in _events)
            {
                // Filter by impact level
                if (evt.Impact < minImpact)
                    continue;

                if (!relevant.Contains(evt.Currency.ToUpperInvariant()))
                    continue;

                double minutesDiff = (evt.TimeUtc - now).TotalMinutes;

                // Block window: [event - window, event + window]
                if (minutesDiff >= -windowMinutes && minutesDiff <= windowMinutes)
                {
                    return new NewsBlockResult
                    {
                        Blocked = true,
                        EventName = evt.Title,
                        Currency = evt.Currency,
                        Impact = evt.Impact,
                        MinutesToEvent = (int)Math.Round(minutesDiff),
                        EventTime = evt.TimeUtc,
                    };
                }
            }
        }

        return NewsBlockResult.NotBlocked;
    }

    // Legacy overload removed — all callers now use named parameters with the main IsBlocked.

    /// <summary>
    /// Get all upcoming events within the next N hours for a symbol.
    /// </summary>
    public List<NewsEvent> GetUpcoming(string symbol, int hours = 24,
                                        bool includeUsd = true,
                                        int minImpact = 1,
                                        DateTime? utcNow = null)
    {
        var now = utcNow ?? DateTime.UtcNow;
        var relevant = GetRelevantCurrencies(symbol, includeUsd);
        var cutoff = now.AddHours(hours);

        lock (_lock)
        {
            return _events
                .Where(e => e.TimeUtc >= now && e.TimeUtc <= cutoff)
                .Where(e => e.Impact >= minImpact)
                .Where(e => relevant.Contains(e.Currency.ToUpperInvariant()))
                .ToList();
        }
    }

    // Legacy overload removed — callers use named parameters with the main GetUpcoming.

    /// <summary>
    /// Get all events in cache (for diagnostics / dashboard).
    /// </summary>
    public List<NewsEvent> GetAllEvents()
    {
        lock (_lock) return _events.ToList();
    }

    /// <summary>
    /// Check if ANY event with sufficient impact is within the blocking window.
    /// Used for dashboard tile display — not symbol-specific.
    /// Returns the highest-impact blocking event if any.
    /// </summary>
    public NewsBlockResult IsBlockedGlobal(int windowMinutes = 15,
                                            int minImpact = 3,
                                            DateTime? utcNow = null)
    {
        var now = utcNow ?? DateTime.UtcNow;

        lock (_lock)
        {
            NewsBlockResult? worst = null;

            foreach (var evt in _events)
            {
                if (evt.Impact < minImpact)
                    continue;

                double minutesDiff = (evt.TimeUtc - now).TotalMinutes;
                if (minutesDiff >= -windowMinutes && minutesDiff <= windowMinutes)
                {
                    if (worst == null || evt.Impact > worst.Impact
                        || (evt.Impact == worst.Impact && Math.Abs(minutesDiff) < Math.Abs(worst.MinutesToEvent)))
                    {
                        worst = new NewsBlockResult
                        {
                            Blocked = true,
                            EventName = evt.Title,
                            Currency = evt.Currency,
                            Impact = evt.Impact,
                            MinutesToEvent = (int)Math.Round(minutesDiff),
                            EventTime = evt.TimeUtc,
                        };
                    }
                }
            }

            return worst ?? NewsBlockResult.NotBlocked;
        }
    }

    // ===================================================================
    // Hash comparison — only rewrite file when data changed
    // ===================================================================

    private static string ComputeHash(List<NewsEvent> events)
    {
        var sb = new StringBuilder();
        foreach (var e in events.OrderBy(x => x.TimeUtc))
            sb.Append($"{e.Currency}|{e.TimeUtc:O}|{e.Title}|");

        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString()));
        return Convert.ToHexString(bytes);
    }

    // ===================================================================
    // Currency ↔ Symbol Mapping
    // ===================================================================

    /// <summary>
    /// Determine which currencies are relevant for a symbol.
    /// If includeUsd=true, USD is always added (USD moves everything).
    /// </summary>
    public static HashSet<string> GetRelevantCurrencies(string symbol, bool includeUsd = true)
    {
        var currencies = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var sym = symbol.ToUpperInvariant().Replace(".", "").Replace("_", "");

        // Known index → currency mappings
        if (IndexCurrencyMap.TryGetValue(sym, out var indexCcy))
        {
            currencies.Add(indexCcy);
        }
        // Known commodity → currency mappings
        else if (CommodityCurrencyMap.TryGetValue(sym, out var commCcy))
        {
            currencies.Add(commCcy);
        }
        // Crypto — only USD relevant
        else if (sym.StartsWith("BTC") || sym.StartsWith("ETH") || sym.StartsWith("LTC") ||
                 sym.StartsWith("XRP") || sym.StartsWith("SOL") || sym.StartsWith("ADA") ||
                 sym.StartsWith("DOGE"))
        {
            // Crypto: USD is the only meaningful news driver
        }
        // Forex: extract base (first 3) + quote (last 3)
        else if (sym.Length >= 6)
        {
            currencies.Add(sym[..3]);   // Base currency
            currencies.Add(sym[3..6]);  // Quote currency
        }
        else
        {
            // Unknown — just add USD if enabled
        }

        // USD news is always relevant (configurable)
        if (includeUsd)
            currencies.Add("USD");

        return currencies;
    }

    // Known stock indices → their country's currency
    private static readonly Dictionary<string, string> IndexCurrencyMap = new(StringComparer.OrdinalIgnoreCase)
    {
        // US indices
        ["US30"] = "USD", ["US500"] = "USD", ["USTEC"] = "USD",
        ["US2000"] = "USD", ["SPX500"] = "USD", ["NAS100"] = "USD",
        ["DJ30"] = "USD", ["SP500"] = "USD",
        // European indices
        ["DE30"] = "EUR", ["DE40"] = "EUR", ["DAX40"] = "EUR",
        ["FR40"] = "EUR", ["EU50"] = "EUR", ["SX5E"] = "EUR",
        // UK
        ["UK100"] = "GBP", ["FTSE100"] = "GBP",
        // Japan
        ["JP225"] = "JPY", ["JPN225"] = "JPY", ["NIKKEI"] = "JPY",
        // Australia
        ["AUS200"] = "AUD",
        // China / Hong Kong
        ["HK50"] = "HKD", ["CHINA50"] = "CNY",
    };

    // Known commodities → relevant currency
    private static readonly Dictionary<string, string> CommodityCurrencyMap = new(StringComparer.OrdinalIgnoreCase)
    {
        ["XAUUSD"] = "USD", ["XAGUSD"] = "USD",
        ["GOLD"] = "USD", ["SILVER"] = "USD",
        ["USOIL"] = "USD", ["UKOIL"] = "USD",
        ["WTIUSD"] = "USD", ["BRENT"] = "USD",
        ["XAUEUR"] = "EUR", ["XAUGBP"] = "GBP",
        ["NATGAS"] = "USD", ["COPPER"] = "USD",
    };

    private static readonly JsonSerializerOptions _jsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public void Dispose()
    {
        _refreshTimer?.Dispose();
        _http.Dispose();
    }
}

// ===================================================================
// ForexFactory JSON model
// ===================================================================

/// <summary>Raw event from ForexFactory JSON mirror (nfs.faireconomy.media)</summary>
internal class FFEvent
{
    [JsonPropertyName("title")]    public string Title { get; set; } = "";
    [JsonPropertyName("country")]  public string Country { get; set; } = "";
    [JsonPropertyName("date")]     public string Date { get; set; } = "";
    [JsonPropertyName("impact")]   public string? Impact { get; set; }
    [JsonPropertyName("forecast")] public string? Forecast { get; set; }
    [JsonPropertyName("previous")] public string? Previous { get; set; }
}

// ===================================================================
// Internal Models
// ===================================================================

/// <summary>Single economic calendar event.</summary>
public class NewsEvent
{
    [JsonPropertyName("id")]       public string Id { get; set; } = "";
    [JsonPropertyName("time_utc")] public DateTime TimeUtc { get; set; }
    [JsonPropertyName("currency")] public string Currency { get; set; } = "";
    [JsonPropertyName("title")]    public string Title { get; set; } = "";
    [JsonPropertyName("impact")]   public int Impact { get; set; }           // 1=low, 2=medium, 3=high
    [JsonPropertyName("actual")]   public string? Actual { get; set; }
    [JsonPropertyName("forecast")] public string? Forecast { get; set; }
    [JsonPropertyName("previous")] public string? Previous { get; set; }
}

/// <summary>Result of news block check.</summary>
public class NewsBlockResult
{
    public bool Blocked { get; set; }
    public string? EventName { get; set; }
    public string? Currency { get; set; }
    public int Impact { get; set; }
    public int MinutesToEvent { get; set; }
    public DateTime EventTime { get; set; }

    public static NewsBlockResult NotBlocked => new() { Blocked = false };

    public override string ToString() => Blocked
        ? $"BLOCKED: {EventName} ({Currency}) in {MinutesToEvent}min"
        : "OK";
}
