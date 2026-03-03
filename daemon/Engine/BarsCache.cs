using Daemon.Models;

namespace Daemon.Engine;

/// <summary>
/// In-memory cache for OHLCV bars. Provides instant access to historical bars
/// for strategy warm-up (Bollinger Bands need N=200..670 bars before first signal).
///
/// Lifecycle:
///   1. Strategy starts → first TICK triggers LoadFull (history_bars from MT5)
///   2. Subsequent ticks → Update (last 3 bars, detect new closes)
///   3. Strategy stops → cache stays (other strategies may share it)
///   4. Daemon restarts → cache rebuilt from MT5 on first TICK
///
/// Cache key: "TerminalId:Symbol:Timeframe" e.g. "The5ers-1:EURUSD:H1"
/// Two strategies on the same terminal+symbol+tf share one cache entry.
/// </summary>
public class BarsCache
{
    private readonly Dictionary<string, CacheEntry> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _lock = new();

    /// <summary>Build cache key from components.</summary>
    private static string Key(string terminalId, string symbol, string tf) =>
        $"{terminalId}:{symbol}:{tf}";

    /// <summary>
    /// Full load: store entire bar array from MT5. Used on first TICK.
    /// Replaces any existing entry.
    /// </summary>
    public void LoadFull(string terminalId, string symbol, string timeframe, List<Bar> bars, int maxBars)
    {
        var key = Key(terminalId, symbol, timeframe);
        lock (_lock)
        {
            _cache[key] = new CacheEntry
            {
                Bars = new List<Bar>(bars),
                MaxBars = maxBars,
                LastBarTime = bars.Count > 0 ? bars[^1].Time : 0,
                IsWarmedUp = bars.Count > 0   // don't mark warmed-up if MT5 returned nothing
            };
        }
    }

    /// <summary>
    /// Incremental update: merge fresh bars from MT5 (typically last 3).
    /// Detects new candle closes and updates forming candle.
    /// Returns true if a new candle closed (strategy should receive TICK).
    /// </summary>
    public bool Update(string terminalId, string symbol, string timeframe, List<Bar> freshBars)
    {
        var key = Key(terminalId, symbol, timeframe);
        lock (_lock)
        {
            if (!_cache.TryGetValue(key, out var entry))
                return false;

            if (freshBars.Count == 0)
                return false;

            bool newCandleClosed = false;
            var cached = entry.Bars;

            foreach (var fresh in freshBars)
            {
                // Find matching bar by time
                int idx = -1;
                for (int i = cached.Count - 1; i >= Math.Max(0, cached.Count - 5); i--)
                {
                    if (cached[i].Time == fresh.Time)
                    {
                        idx = i;
                        break;
                    }
                }

                if (idx >= 0)
                {
                    // Update existing bar (forming candle OHLCV update)
                    cached[idx] = fresh;
                }
                else if (fresh.Time > entry.LastBarTime)
                {
                    // New bar — candle closed
                    cached.Add(fresh);
                    newCandleClosed = true;

                    // Trim oldest bars to maintain max size
                    while (cached.Count > entry.MaxBars)
                        cached.RemoveAt(0);
                }
            }

            // Update last bar time
            if (cached.Count > 0)
                entry.LastBarTime = cached[^1].Time;

            return newCandleClosed;
        }
    }

    /// <summary>Get full bar array for a symbol (read-only snapshot).</summary>
    public List<Bar>? GetBars(string terminalId, string symbol, string timeframe)
    {
        var key = Key(terminalId, symbol, timeframe);
        lock (_lock)
        {
            if (!_cache.TryGetValue(key, out var entry))
                return null;
            return new List<Bar>(entry.Bars); // defensive copy
        }
    }

    /// <summary>Get last bar time for a symbol (for new-candle detection).</summary>
    public long GetLastBarTime(string terminalId, string symbol, string timeframe)
    {
        var key = Key(terminalId, symbol, timeframe);
        lock (_lock)
        {
            return _cache.TryGetValue(key, out var entry) ? entry.LastBarTime : 0;
        }
    }

    /// <summary>Check if cache has been warmed up (full load done).</summary>
    public bool IsWarmedUp(string terminalId, string symbol, string timeframe)
    {
        var key = Key(terminalId, symbol, timeframe);
        lock (_lock)
        {
            return _cache.TryGetValue(key, out var entry) && entry.IsWarmedUp;
        }
    }

    /// <summary>
    /// Ensure cache depth is sufficient. If a new strategy needs more bars
    /// than currently cached, returns true (caller should reload with deeper history).
    /// </summary>
    public bool NeedsDeeper(string terminalId, string symbol, string timeframe, int requiredBars)
    {
        var key = Key(terminalId, symbol, timeframe);
        lock (_lock)
        {
            if (!_cache.TryGetValue(key, out var entry))
                return true; // no cache at all
            return entry.MaxBars < requiredBars;
        }
    }

    /// <summary>Remove cache entry (e.g. when no strategies need it).</summary>
    public void Remove(string terminalId, string symbol, string timeframe)
    {
        var key = Key(terminalId, symbol, timeframe);
        lock (_lock)
        {
            _cache.Remove(key);
        }
    }

    /// <summary>Clear all cached bars (e.g. on daemon shutdown).</summary>
    public void Clear()
    {
        lock (_lock)
        {
            _cache.Clear();
        }
    }

    /// <summary>Get cache stats for logging.</summary>
    public (int entryCount, int totalBars) GetStats()
    {
        lock (_lock)
        {
            var total = _cache.Values.Sum(e => e.Bars.Count);
            return (_cache.Count, total);
        }
    }

    // -----------------------------------------------------------------------

    private class CacheEntry
    {
        public List<Bar> Bars { get; set; } = new();
        public int MaxBars { get; set; }
        public long LastBarTime { get; set; }
        public bool IsWarmedUp { get; set; }
    }
}
