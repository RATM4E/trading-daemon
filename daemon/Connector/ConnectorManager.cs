using System.Linq;
using System.Text.Json;
using Daemon.Config;
using Daemon.Models;

namespace Daemon.Connector;

/// <summary>
/// Manages all MT5 worker processes. Provides a unified interface for the daemon
/// to interact with any terminal by ID. Handles symbol mapping transparently.
/// </summary>
public class ConnectorManager : IDisposable
{
    private readonly DaemonConfig _config;
    private readonly ILogger _log;
    private readonly Dictionary<string, WorkerProcess> _workers = new();
    private readonly Dictionary<string, SymbolMapper> _symbolMappers = new();
    private Timer? _heartbeatTimer;
    private bool _disposed;
    private readonly Dictionary<string, bool> _lastConnState = new();

    /// <summary>Optional alias resolver: broker symbol → canonical. Set from CostModelLoader.</summary>
    private Func<string, string?>? _aliasResolver;

    /// <summary>Fires when terminal connectivity changes: (terminalId, newStatus: "connected"/"disconnected")</summary>
    public event Action<string, string>? OnTerminalStatusChanged;

    /// <summary>Set alias resolver for automatic broker→canonical symbol fallback.</summary>
    public void SetAliasResolver(Func<string, string?> resolver) => _aliasResolver = resolver;

    public ConnectorManager(DaemonConfig config, ILogger logger)
    {
        _config = config;
        _log = logger;
    }

    /// <summary>Start all auto-connect workers.</summary>
    public async Task StartAsync(CancellationToken ct = default)
    {
        _log.Info($"Starting ConnectorManager with {_config.Terminals.Count} terminal(s)...");

        foreach (var tc in _config.Terminals)
        {
            // Build symbol mapper
            _symbolMappers[tc.Id] = new SymbolMapper(tc.SymbolMap);

            if (!tc.Enabled)
            {
                _log.Info($"[{tc.Id}] Terminal disabled, skipping");
                continue;
            }

            if (!tc.AutoConnect)
            {
                _log.Info($"[{tc.Id}] auto_connect=false, skipping");
                continue;
            }

            var worker = new WorkerProcess(tc, _config.PythonPath, _config.WorkerScript, _log);
            _workers[tc.Id] = worker;

            try
            {
                await worker.StartAsync(ct);

                // Log initial account info
                var acc = await worker.GetAccountInfoAsync(ct);
                if (acc != null)
                {
                    _log.Info($"[{tc.Id}] Account {acc.Login} | Balance: {acc.Balance:F2} {acc.Currency} | "
                            + $"Leverage: {acc.Leverage} | Type: {acc.AccountType}");
                }
            }
            catch (Exception ex)
            {
                _log.Error($"[{tc.Id}] Failed to start: {ex.Message}");
            }
        }

        // Start heartbeat timer
        var intervalMs = _config.HeartbeatIntervalSec * 1000;
        _heartbeatTimer = new Timer(HeartbeatCallback, null, intervalMs, intervalMs);

        _log.Info($"ConnectorManager started: {_workers.Count(w => w.Value.IsConnected)}/{_config.Terminals.Count} connected");
    }

    private async void HeartbeatCallback(object? state)
    {
        foreach (var (id, worker) in _workers)
        {
            try
            {
                var alive = await worker.HeartbeatAsync();
                if (!alive)
                    _log.Warn($"[{id}] Heartbeat FAILED");

                // Detect state transitions
                var wasConnected = _lastConnState.GetValueOrDefault(id, true);
                var nowConnected = alive;

                if (wasConnected && !nowConnected)
                {
                    _log.Warn($"[{id}] Terminal DISCONNECTED (was connected)");
                    OnTerminalStatusChanged?.Invoke(id, "disconnected");
                }
                else if (!wasConnected && nowConnected)
                {
                    _log.Info($"[{id}] Terminal RECONNECTED");
                    OnTerminalStatusChanged?.Invoke(id, "connected");
                }

                _lastConnState[id] = nowConnected;
            }
            catch (Exception ex)
            {
                _log.Warn($"[{id}] Heartbeat error: {ex.Message}");

                var wasConnected = _lastConnState.GetValueOrDefault(id, true);
                if (wasConnected)
                {
                    _log.Warn($"[{id}] Terminal DISCONNECTED (heartbeat exception)");
                    OnTerminalStatusChanged?.Invoke(id, "disconnected");
                }
                _lastConnState[id] = false;
            }
        }
    }

    // -----------------------------------------------------------------------
    // Public API â€” all methods apply symbol mapping transparently
    // -----------------------------------------------------------------------

    public List<string> GetAllTerminalIds() => _config.Terminals.Select(t => t.Id).ToList();

    /// <summary>Get terminal IDs that are enabled (not disabled in config).</summary>
    public List<string> GetEnabledTerminalIds() => _config.Terminals.Where(t => t.Enabled).Select(t => t.Id).ToList();

    public bool IsConnected(string terminalId) =>
        _workers.TryGetValue(terminalId, out var w) && w.IsConnected;

    public async Task<AccountInfo?> GetAccountInfoAsync(string terminalId, CancellationToken ct = default)
    {
        var worker = GetWorker(terminalId);
        return await worker.GetAccountInfoAsync(ct);
    }

    public async Task<List<Position>> GetPositionsAsync(string terminalId, CancellationToken ct = default)
    {
        var worker = GetWorker(terminalId);
        var positions = await worker.GetPositionsAsync(ct);

        // Unmap broker symbols â†’ canonical
        foreach (var p in positions)
            p.Symbol = UnmapSymbol(terminalId, p.Symbol);

        return positions;
    }

    public async Task<List<Bar>> GetRatesAsync(string terminalId, string symbol, string timeframe,
                                                int count = 300, CancellationToken ct = default)
    {
        var worker = GetWorker(terminalId);
        var brokerSymbol = GetMapper(terminalId).Map(symbol);
        return await worker.GetRatesAsync(brokerSymbol, timeframe, count, ct);
    }

    /// <summary>
    /// Bulk-download historical bars for a date range via COPY_RATES_RANGE.
    /// Used by BacktestEngine to populate BarsHistoryDb.
    /// from/to timestamps are in UTC (MT5 API requirement).
    /// Returned bar timestamps are in broker server time (e.g. EET for The5ers).
    /// </summary>
    public async Task<List<Bar>?> GetBarsRangeAsync(
        string terminalId, string symbol, string timeframe,
        long fromTimestamp, long toTimestamp, CancellationToken ct = default)
    {
        var worker = GetWorker(terminalId);
        var brokerSymbol = GetMapper(terminalId).Map(symbol);

        var parameters = new Dictionary<string, object>
        {
            ["symbol"] = brokerSymbol,
            ["timeframe"] = timeframe,
            ["from_ts"] = fromTimestamp,
            ["to_ts"] = toTimestamp
        };

        var resp = await worker.SendCommandAsync("COPY_RATES_RANGE", parameters, ct);
        if (!resp.IsOk || resp.Data == null)
            return null;

        // Response data: { "symbol": "...", "timeframe": "...", "bars": [...], "count": N }
        if (resp.Data.Value.TryGetProperty("bars", out var barsElement))
        {
            return barsElement.Deserialize<List<Bar>>() ?? new List<Bar>();
        }

        return null;
    }

    public async Task<InstrumentCard?> GetSymbolInfoAsync(string terminalId, string symbol,
                                                           CancellationToken ct = default)
    {
        var worker = GetWorker(terminalId);
        var brokerSymbol = GetMapper(terminalId).Map(symbol);
        var card = await worker.GetSymbolInfoAsync(brokerSymbol, ct);

        // Unmap symbol name in the card
        if (card != null)
            card.Symbol = GetMapper(terminalId).Unmap(card.Symbol);

        return card;
    }

    public async Task<WorkerResponse> SendOrderAsync(string terminalId, Dictionary<string, object> request,
                                                      CancellationToken ct = default)
    {
        var worker = GetWorker(terminalId);

        // Map canonical symbol â†’ broker symbol in the request
        if (request.TryGetValue("symbol", out var sym) && sym is string canonical)
            request["symbol"] = GetMapper(terminalId).Map(canonical);

        return await worker.SendOrderAsync(request, ct);
    }

    /// <summary>Get deal history filtered by position ticket.</summary>
    public async Task<List<Deal>> GetHistoryDealsAsync(string terminalId, long positionTicket,
                                                        CancellationToken ct = default)
    {
        var worker = GetWorker(terminalId);
        var parameters = new Dictionary<string, object>
        {
            ["position"] = positionTicket,
            ["from_ts"] = 0,
            ["to_ts"] = 2000000000
        };
        var resp = await worker.SendCommandAsync("HISTORY_DEALS", parameters, ct);
        if (!resp.IsOk || resp.Data == null) return new List<Deal>();

        var allDeals = resp.Data.Value.Deserialize<List<Deal>>() ?? new List<Deal>();

        // Filter by position_id (in case worker returns all deals)
        var filtered = allDeals.Where(d => d.PositionId == positionTicket).ToList();

        // Unmap symbols (with alias fallback)
        foreach (var d in filtered)
            d.Symbol = UnmapSymbol(terminalId, d.Symbol);

        return filtered;
    }

    /// <summary>Unmap a broker symbol to canonical for a terminal.
    /// Tries config symbol_map first, then alias resolver (cost model v2).</summary>
    public string UnmapSymbol(string terminalId, string brokerSymbol)
    {
        if (_symbolMappers.TryGetValue(terminalId, out var mapper))
        {
            var result = mapper.Unmap(brokerSymbol);
            if (result != brokerSymbol) return result; // explicit mapping found
        }

        // Alias fallback: resolve via cost model aliases
        if (_aliasResolver != null)
        {
            var canonical = _aliasResolver(brokerSymbol);
            if (canonical != null) return canonical;
        }

        return brokerSymbol;
    }

    /// <summary>Calculate effective leverage per asset class for a terminal.</summary>
    public async Task<Dictionary<string, int>> CalcLeverageAsync(string terminalId,
        Dictionary<string, string> classSymbols, CancellationToken ct = default)
    {
        var worker = GetWorker(terminalId);
        var mapper = GetMapper(terminalId);

        // Map canonical symbols â†’ broker symbols
        var mapped = new Dictionary<string, string>();
        foreach (var kv in classSymbols)
            mapped[kv.Key] = mapper.Map(kv.Value);

        return await worker.CalcLeverageAsync(mapped, ct);
    }

    /// <summary>Calculate profit/loss for a hypothetical trade via MT5 OrderCalcProfit.
    /// Handles symbol mapping. Returns profit in account currency, or null on failure.</summary>
    public async Task<double?> CalcProfitAsync(string terminalId, string symbol, string direction,
                                                double volume, double priceOpen, double priceClose,
                                                CancellationToken ct = default)
    {
        var worker = GetWorker(terminalId);
        var brokerSymbol = GetMapper(terminalId).Map(symbol);
        string action = direction.ToUpperInvariant() == "SHORT" ? "sell" : "buy";
        return await worker.CalcProfitAsync(brokerSymbol, action, volume, priceOpen, priceClose, ct);
    }

    /// <summary>Calculate margin for a hypothetical trade via MT5 OrderCalcMargin.
    /// Handles symbol mapping. Returns margin in account currency, or null on failure.</summary>
    public async Task<double?> CalcMarginAsync(string terminalId, string symbol, string direction,
                                                double volume, double price,
                                                CancellationToken ct = default)
    {
        var worker = GetWorker(terminalId);
        var brokerSymbol = GetMapper(terminalId).Map(symbol);
        string action = direction.ToUpperInvariant() == "SHORT" ? "sell" : "buy";
        return await worker.CalcMarginAsync(brokerSymbol, action, volume, price, ct);
    }

    /// <summary>Calculate per-position margin filtered by magic numbers.
    /// Used by G6 to separate "own" vs "foreign" margin on shared accounts.</summary>
    public async Task<PositionsMarginResult?> CalcPositionsMarginAsync(
        string terminalId, List<int>? magics = null, CancellationToken ct = default)
    {
        var worker = GetWorker(terminalId);
        return await worker.CalcPositionsMarginAsync(magics, ct);
    }

    /// <summary>Check which canonical symbols are available on a terminal.
    /// Applies config symbol_map first, then worker resolves via alias table.</summary>
    public async Task<(Dictionary<string, string> Resolved, List<string> Missing)>
        CheckSymbolsAsync(string terminalId, List<string> canonicalSymbols, CancellationToken ct = default)
    {
        var worker = GetWorker(terminalId);

        // Pre-map via config symbol_map (user overrides take priority)
        var mapper = GetMapper(terminalId);
        var toCheck = canonicalSymbols.Select(s => mapper.Map(s)).ToList();

        var (resolved, missing) = await worker.CheckSymbolsAsync(toCheck, ct);

        // Unmap resolved broker names back to canonical (with alias fallback)
        var canonical = new Dictionary<string, string>();
        foreach (var kv in resolved)
            canonical[UnmapSymbol(terminalId, kv.Key)] = kv.Value;

        var missingCanonical = missing.Select(s => UnmapSymbol(terminalId, s)).ToList();
        return (canonical, missingCanonical);
    }

    /// <summary>Add and start a new terminal worker at runtime (from dashboard discovery).</summary>
    public async Task AddTerminalAsync(TerminalConfig tc, CancellationToken ct = default)
    {
        if (_workers.ContainsKey(tc.Id))
            throw new InvalidOperationException($"Terminal '{tc.Id}' already exists");

        _symbolMappers[tc.Id] = new SymbolMapper(tc.SymbolMap);

        var worker = new WorkerProcess(tc, _config.PythonPath, _config.WorkerScript, _log);
        _workers[tc.Id] = worker;

        await worker.StartAsync(ct);

        var acc = await worker.GetAccountInfoAsync(ct);
        if (acc != null)
        {
            _log.Info($"[{tc.Id}] Account {acc.Login} | Balance: {acc.Balance:F2} {acc.Currency} | "
                    + $"Leverage: {acc.Leverage} | Type: {acc.AccountType}");
        }
    }

    /// <summary>Stop all workers gracefully.</summary>
    public async Task StopAsync(CancellationToken ct = default)
    {
        _log.Info("Stopping ConnectorManager...");
        _heartbeatTimer?.Dispose();
        _heartbeatTimer = null;

        var tasks = _workers.Values.Select(w => w.StopAsync(ct));
        await Task.WhenAll(tasks);

        _log.Info("All workers stopped");
    }

    /// <summary>Stop a single terminal worker (for disable). Worker can be restarted later.</summary>
    public async Task StopTerminalAsync(string terminalId, CancellationToken ct = default)
    {
        if (_workers.TryGetValue(terminalId, out var worker))
        {
            _log.Info($"[{terminalId}] Stopping worker...");
            await worker.StopAsync(ct);
            _workers.Remove(terminalId);
            _log.Info($"[{terminalId}] Worker stopped");
        }
    }

    /// <summary>Restart worker for a terminal (stop existing + start new). Used after MT5 restart.</summary>
    public async Task RestartWorkerAsync(string terminalId, CancellationToken ct = default)
    {
        var termConfig = _config.Terminals.FirstOrDefault(t => t.Id == terminalId);
        if (termConfig == null) throw new KeyNotFoundException($"Terminal '{terminalId}' not in config");

        // Stop existing worker if any
        if (_workers.TryGetValue(terminalId, out var oldWorker))
        {
            _log.Info($"[{terminalId}] Stopping old worker for restart...");
            try { await oldWorker.StopAsync(ct); } catch { }
            oldWorker.Dispose();
            _workers.Remove(terminalId);
        }

        // Create and start new worker
        _symbolMappers[terminalId] = new SymbolMapper(termConfig.SymbolMap);
        var worker = new WorkerProcess(termConfig, _config.PythonPath, _config.WorkerScript, _log);
        _workers[terminalId] = worker;
        await worker.StartAsync(ct);

        var acc = await worker.GetAccountInfoAsync(ct);
        if (acc != null)
        {
            _log.Info($"[{terminalId}] Reconnected: Account {acc.Login} | Balance: {acc.Balance:F2} {acc.Currency}");
            _lastConnState[terminalId] = true;
            OnTerminalStatusChanged?.Invoke(terminalId, "connected");
        }
        else
        {
            _log.Warn($"[{terminalId}] Worker started but MT5 not responding");
            _lastConnState[terminalId] = false;
        }
    }

    /// <summary>Remove a terminal completely (for delete). Stops worker and removes all references.</summary>
    public async Task RemoveTerminalAsync(string terminalId, CancellationToken ct = default)
    {
        await StopTerminalAsync(terminalId, ct);
        _symbolMappers.Remove(terminalId);
        _log.Info($"[{terminalId}] Terminal removed from ConnectorManager");
    }

    // -----------------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------------

    private WorkerProcess GetWorker(string terminalId)
    {
        if (!_workers.TryGetValue(terminalId, out var worker))
            throw new KeyNotFoundException($"Terminal '{terminalId}' not found");
        if (!worker.IsConnected)
            throw new InvalidOperationException($"Terminal '{terminalId}' is not connected");
        return worker;
    }

    private SymbolMapper GetMapper(string terminalId)
    {
        if (!_symbolMappers.TryGetValue(terminalId, out var mapper))
            throw new KeyNotFoundException($"No symbol mapper for terminal '{terminalId}'");
        return mapper;
    }

    // ===================================================================
    // Magic-filtered unrealized PnL (Phase 10: multi-terminal isolation)
    // ===================================================================

    /// <summary>
    /// Get unrealized PnL for a terminal, filtered by magic numbers assigned to it.
    /// On shared MT5 accounts, acc.Profit includes ALL strategies' floating P/L.
    /// This method returns only the P/L of positions belonging to this terminal's strategies.
    /// Falls back to total acc.Profit if no strategy assignments exist for this terminal.
    /// </summary>
    public async Task<double> GetFilteredUnrealizedPnlAsync(string terminalId, CancellationToken ct = default)
    {
        var positions = await GetPositionsAsync(terminalId, ct);
        if (positions == null || positions.Count == 0) return 0;

        var magics = _config.Strategies
            .Where(s => s.Terminal == terminalId)
            .Select(s => s.Magic)
            .ToHashSet();

        // No strategy assignments → fallback to all positions (backward compat)
        if (magics.Count == 0) return positions.Sum(p => p.Profit);

        return positions.Where(p => magics.Contains(p.Magic)).Sum(p => p.Profit);
    }

    /// <summary>
    /// Get MT5 positions filtered by this terminal's magic numbers.
    /// Used by emergency close to only close positions belonging to this terminal's strategies.
    /// </summary>
    public async Task<List<Position>> GetFilteredPositionsAsync(string terminalId, CancellationToken ct = default)
    {
        var positions = await GetPositionsAsync(terminalId, ct);
        if (positions == null) return new List<Position>();

        var magics = _config.Strategies
            .Where(s => s.Terminal == terminalId)
            .Select(s => s.Magic)
            .ToHashSet();

        if (magics.Count == 0) return positions;

        return positions.Where(p => magics.Contains(p.Magic)).ToList();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _heartbeatTimer?.Dispose();
        foreach (var w in _workers.Values)
            w.Dispose();
    }
}

/// <summary>
/// Two-way symbol mapping: canonical â†” broker.
/// e.g. EURUSD â†” EURUSDi
/// If not in map, returns as-is.
/// </summary>
public class SymbolMapper
{
    private readonly Dictionary<string, string> _toB;   // canonical â†’ broker
    private readonly Dictionary<string, string> _toC;   // broker â†’ canonical

    public SymbolMapper(Dictionary<string, string> canonicalToBroker)
    {
        _toB = new Dictionary<string, string>(canonicalToBroker, StringComparer.OrdinalIgnoreCase);
        _toC = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var kv in canonicalToBroker)
            _toC[kv.Value] = kv.Key;
    }

    /// <summary>Map canonical â†’ broker symbol.</summary>
    public string Map(string canonical) => _toB.TryGetValue(canonical, out var broker) ? broker : canonical;

    /// <summary>Unmap broker â†’ canonical symbol.</summary>
    public string Unmap(string broker) => _toC.TryGetValue(broker, out var canonical) ? canonical : broker;
}
