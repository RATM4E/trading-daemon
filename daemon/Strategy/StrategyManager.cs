using System.Text.Json;
using Daemon.Config;
using Daemon.Engine;
using Daemon.Connector;

namespace Daemon.Strategy;

/// <summary>
/// Manages all strategy processes.
///
/// Responsibilities:
///   - Periodically scan strategies/ folder to discover available strategies
///   - Auto-register new strategies in state.db (disabled by default)
///   - Remove stale entries when folders are deleted
///   - Enforce enabled-check before allowing start
///   - Start/stop strategy processes
///   - Allocate TCP ports for each strategy
///   - Persist/restore strategy state via StateManager
///   - Provide status for dashboard
///   - Cache strategy config params (r_cap) for dashboard before HELLO
/// </summary>
public class StrategyManager : IDisposable
{
    private readonly DaemonConfig _config;
    private readonly StateManager _state;
    private readonly ILogger _log;

    private readonly Dictionary<string, StrategyProcess> _processes = new(); // key: "strategy@terminal"
    private int _nextPort;
    private bool _disposed;

    // Auto-discovery
    private Timer? _scanTimer;
    private readonly object _scanLock = new();

    /// <summary>
    /// Cached r_cap values parsed from strategy config.json files.
    /// Key = strategy folder name, Value = params.r_cap value.
    /// Updated during auto-discovery scans — available even when strategy is stopped.
    /// </summary>
    private readonly Dictionary<string, double> _configRCapCache = new();

    /// <summary>Fires when the discovered strategy set changes (for dashboard push).</summary>
    public event Action? OnStrategiesChanged;

    public StrategyManager(DaemonConfig config, StateManager state, ILogger logger)
    {
        _config = config;
        _state = state;
        _log = logger;
        _nextPort = config.StrategyBasePort;
    }

    /// <summary>Unique key for a strategy-terminal pair.</summary>
    private static string MakeKey(string strategy, string terminal) => $"{strategy}@{terminal}";

    // -----------------------------------------------------------------------
    //  Auto-Discovery
    // -----------------------------------------------------------------------

    /// <summary>Start periodic scanning for new/removed strategy folders.</summary>
    public void StartAutoDiscovery(int intervalSec = 30)
    {
        // Initial scan
        ScanAndRegister();

        // Periodic scan
        _scanTimer = new Timer(_ => ScanAndRegister(), null,
            TimeSpan.FromSeconds(intervalSec),
            TimeSpan.FromSeconds(intervalSec));

        _log.Info($"Strategy auto-discovery started (interval: {intervalSec}s)");
    }

    /// <summary>
    /// Scan strategies folder, register new strategies, remove stale entries.
    /// Thread-safe.
    /// </summary>
    public void ScanAndRegister()
    {
        lock (_scanLock)
        {
            try
            {
                var onDisk = DiscoverStrategiesRaw();
                var registered = _state.GetRegisteredStrategies()
                    .Select(r => r.Name).ToHashSet();

                var changed = false;

                // New strategies found on disk
                foreach (var name in onDisk)
                {
                    if (!registered.Contains(name))
                    {
                        var magic = _state.GetNextMagicBase();
                        _state.RegisterStrategy(name, magic);
                        _log.Info($"[Discovery] New strategy registered: {name} (magic_base={magic})");
                        changed = true;
                    }
                }

                // Strategies removed from disk — stop if running, unregister
                foreach (var name in registered)
                {
                    if (!onDisk.Contains(name))
                    {
                        // Stop any running instances
                        var toStop = _processes
                            .Where(p => p.Key.StartsWith(name + "@") && p.Value.IsRunning)
                            .Select(p => p.Value).ToList();

                        foreach (var p in toStop)
                        {
                            _log.Warn($"[Discovery] Strategy {name} folder removed, stopping {p.StrategyName}@{p.TerminalId}");
                            _ = p.StopAsync(CancellationToken.None);
                            _state.MarkStrategyInactive(p.StrategyName, p.TerminalId);
                        }

                        _state.UnregisterStrategy(name);
                        _log.Info($"[Discovery] Strategy unregistered (folder removed): {name}");
                        changed = true;
                    }
                }

                if (changed)
                    OnStrategiesChanged?.Invoke();

                // Parse r_cap from all discovered strategy configs
                UpdateConfigRCapCache(onDisk);
            }
            catch (Exception ex)
            {
                _log.Error($"[Discovery] Scan failed: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Raw filesystem scan — returns set of folder names containing strategy.py + config.json.
    /// Folders starting with _ or . are ignored.
    /// </summary>
    private HashSet<string> DiscoverStrategiesRaw()
    {
        var result = new HashSet<string>();
        var dir = Path.GetFullPath(_config.StrategyDir, AppContext.BaseDirectory);

        if (!Directory.Exists(dir))
            return result;

        foreach (var subDir in Directory.GetDirectories(dir))
        {
            var folderName = Path.GetFileName(subDir);

            // Skip hidden/disabled folders
            if (folderName.StartsWith("_") || folderName.StartsWith("."))
                continue;

            var strategyFile = Path.Combine(subDir, "strategy.py");
            var configFile = Path.Combine(subDir, "config.json");

            // Both strategy.py and config.json must exist
            if (File.Exists(strategyFile) && File.Exists(configFile))
                result.Add(folderName);
        }

        return result;
    }

    /// <summary>
    /// Parse params.r_cap from each strategy's config.json and cache the values.
    /// Called during auto-discovery scan — ensures r_cap is available
    /// in dashboard Settings even when strategy is stopped (before HELLO).
    /// </summary>
    private void UpdateConfigRCapCache(HashSet<string> strategyNames)
    {
        var baseDir = Path.GetFullPath(_config.StrategyDir, AppContext.BaseDirectory);

        foreach (var name in strategyNames)
        {
            try
            {
                var configPath = Path.Combine(baseDir, name, "config.json");
                if (!File.Exists(configPath)) continue;

                var json = File.ReadAllText(configPath);
                using var doc = JsonDocument.Parse(json);

                if (doc.RootElement.TryGetProperty("params", out var paramsProp) &&
                    paramsProp.TryGetProperty("r_cap", out var rCapProp) &&
                    rCapProp.ValueKind == JsonValueKind.Number)
                {
                    _configRCapCache[name] = rCapProp.GetDouble();
                }
                else
                {
                    // Strategy has no r_cap in params — remove stale cache entry
                    _configRCapCache.Remove(name);
                }
            }
            catch (Exception ex)
            {
                _log.Warn($"[Discovery] Failed to parse r_cap from {name}/config.json: {ex.Message}");
            }
        }

        // Remove entries for strategies no longer on disk
        var stale = _configRCapCache.Keys.Except(strategyNames).ToList();
        foreach (var key in stale)
            _configRCapCache.Remove(key);
    }

    /// <summary>
    /// Get cached r_cap value from strategy config.json (parsed during discovery).
    /// Returns null if strategy has no r_cap in config or strategy not found.
    /// This is available even when the strategy is not running.
    /// </summary>
    public double? GetConfigRCap(string strategyName) =>
        _configRCapCache.TryGetValue(strategyName, out var val) ? val : null;

    /// <summary>
    /// Get the effective r_cap for a terminal, checking all sources:
    /// 1. Running strategy process (HELLO requirements)
    /// 2. Daemon config assignment override
    /// 3. Strategy config.json cache (always available)
    /// </summary>
    public double GetEffectiveRCapForTerminal(string terminalId)
    {
        // 1. Running processes with HELLO requirements
        var fromProcess = GetProcessesForTerminal(terminalId)
            .Where(p => p.Requirements?.RCap > 0)
            .Select(p => p.Requirements!.RCap!.Value)
            .FirstOrDefault();
        if (fromProcess > 0) return fromProcess;

        // 2. Daemon config assignment override
        var fromAssignment = _config.Strategies
            .Where(s => s.Terminal == terminalId && s.RCap.HasValue && s.RCap > 0)
            .Select(s => s.RCap!.Value)
            .FirstOrDefault();
        if (fromAssignment > 0) return fromAssignment;

        // 3. Strategy config.json cache (parsed during discovery)
        var strategyNames = _config.Strategies
            .Where(s => s.Terminal == terminalId)
            .Select(s => s.Strategy);
        foreach (var name in strategyNames)
        {
            if (_configRCapCache.TryGetValue(name, out var val) && val > 0)
                return val;
        }

        return 0;
    }

    /// <summary>
    /// Discover available strategies by scanning the strategy_dir.
    /// Returns list of folder names that contain strategy.py + config.json.
    /// </summary>
    public List<string> DiscoverStrategies()
    {
        return DiscoverStrategiesRaw().ToList();
    }

    /// <summary>Check if a strategy is enabled (allowed to start).</summary>
    public bool IsEnabled(string strategyName) => _state.IsStrategyEnabled(strategyName);

    /// <summary>Enable a strategy.</summary>
    public void EnableStrategy(string strategyName)
    {
        _state.SetStrategyEnabled(strategyName, true);
        _log.Info($"[Strategy] {strategyName} enabled");
    }

    /// <summary>Disable a strategy. Stops all running instances.</summary>
    public async Task DisableStrategyAsync(string strategyName, CancellationToken ct = default)
    {
        _state.SetStrategyEnabled(strategyName, false);

        // Stop any running instances of this strategy
        var toStop = _processes
            .Where(p => p.Key.StartsWith(strategyName + "@") && p.Value.IsRunning)
            .Select(p => p.Value).ToList();

        foreach (var p in toStop)
        {
            _log.Info($"[Strategy] Stopping {strategyName}@{p.TerminalId} (strategy disabled)");
            await p.StopAsync(ct);
            _state.MarkStrategyInactive(strategyName, p.TerminalId);
        }

        _log.Info($"[Strategy] {strategyName} disabled" +
                  (toStop.Count > 0 ? $", stopped {toStop.Count} instance(s)" : ""));
    }

    // -----------------------------------------------------------------------
    //  Auto-start
    // -----------------------------------------------------------------------

    /// <summary>
    /// Auto-start all strategies marked auto_start=true in config.
    /// Called during daemon startup.
    /// </summary>
    public async Task AutoStartAsync(CancellationToken ct = default)
    {
        var autoStarts = _config.Strategies.Where(s => s.AutoStart).ToList();
        if (autoStarts.Count == 0)
        {
            _log.Info("No strategies configured for auto-start");
            return;
        }

        _log.Info($"Auto-starting {autoStarts.Count} strategy assignment(s)...");
        foreach (var assignment in autoStarts)
        {
            await StartStrategyAsync(assignment.Strategy, assignment.Terminal, ct);
        }
    }

    // -----------------------------------------------------------------------
    //  Start / Stop
    // -----------------------------------------------------------------------

    /// <summary>
    /// Start a strategy on a terminal.
    /// Returns (ok, error) — error is null on success.
    /// </summary>
    public async Task<(bool Ok, string? Error)> StartStrategyAsync(string strategyName, string terminalId,
                                                 CancellationToken ct = default)
    {
        // Check enabled
        if (!_state.IsStrategyEnabled(strategyName))
        {
            _log.Warn($"Cannot start {strategyName}: strategy is disabled");
            return (false, "Strategy is disabled. Enable it first.");
        }

        var key = MakeKey(strategyName, terminalId);

        // Already running?
        if (_processes.TryGetValue(key, out var existing) && existing.IsRunning)
        {
            _log.Warn($"Strategy {key} is already running");
            return (false, "Already running");
        }

        // Look up magic from registry
        var registry = _state.GetRegisteredStrategies().FirstOrDefault(r => r.Name == strategyName);
        var magic = registry?.MagicBase ?? 100;

        // Find or create runtime assignment
        var assignment = _config.Strategies.FirstOrDefault(
            s => s.Strategy == strategyName && s.Terminal == terminalId);

        if (assignment == null)
        {
            _log.Info($"Creating runtime assignment for {key} (magic={magic})");
            assignment = new StrategyAssignment
            {
                Strategy = strategyName,
                Terminal = terminalId,
                Magic = magic,
                AutoStart = false
            };
            _config.Strategies.Add(assignment);
        }

        // Verify strategy exists on disk
        var strategyDir = Path.GetFullPath(strategyName,
            Path.GetFullPath(_config.StrategyDir, AppContext.BaseDirectory));
        var strategyFile = Path.Combine(strategyDir, "strategy.py");
        if (!File.Exists(strategyFile))
        {
            _log.Error($"Strategy file not found: {strategyFile}");
            return (false, "strategy.py not found");
        }

        // Allocate port
        var port = _nextPort++;

        // Load saved state
        var savedState = _state.GetStrategyState(strategyName, terminalId);
        if (savedState != null)
            _log.Info($"[{key}] Found saved state ({savedState.Length} bytes)");

        // Create and start process
        var process = new StrategyProcess(assignment, _config, port, _log);
        process.OnStopped += OnStrategyProcessStopped;

        _processes[key] = process;

        var ok = await process.StartAsync(savedState, ct);
        if (!ok)
        {
            _processes.Remove(key);
            _log.Error($"[{key}] Failed to start");
            return (false, "Process failed to start");
        }

        _state.LogEvent("STRATEGY", terminalId, strategyName, "Strategy started",
            $"port={port}, magic={assignment.Magic}");
        _state.MarkStrategyActive(strategyName, terminalId);
        return (true, null);
    }

    /// <summary>Stop a specific strategy on a terminal.</summary>
    public async Task StopStrategyAsync(string strategyName, string terminalId,
                                         CancellationToken ct = default)
    {
        var key = MakeKey(strategyName, terminalId);
        if (!_processes.TryGetValue(key, out var process))
        {
            _log.Warn($"Strategy {key} not found");
            return;
        }

        await process.StopAsync(ct);
        _state.MarkStrategyInactive(strategyName, terminalId);
        _state.LogEvent("STRATEGY", terminalId, strategyName, "Strategy stopped");
    }

    /// <summary>Stop all running strategies.</summary>
    public async Task StopAllAsync(CancellationToken ct = default)
    {
        _log.Info("Stopping all strategies...");
        var tasks = _processes.Values
            .Where(p => p.IsRunning)
            .Select(p => p.StopAsync(ct));
        await Task.WhenAll(tasks);
        _log.Info("All strategies stopped");
    }

    /// <summary>Get a running strategy process by name + terminal.</summary>
    public StrategyProcess? GetProcess(string strategyName, string terminalId)
    {
        var key = MakeKey(strategyName, terminalId);
        return _processes.TryGetValue(key, out var p) ? p : null;
    }

    /// <summary>Get all running strategy processes.</summary>
    public IReadOnlyList<StrategyProcess> GetAllProcesses() =>
        _processes.Values.ToList().AsReadOnly();

    /// <summary>Get all running strategy processes for a specific terminal.</summary>
    public List<StrategyProcess> GetProcessesForTerminal(string terminalId) =>
        _processes.Values.Where(p => p.TerminalId == terminalId && p.IsRunning).ToList();

    /// <summary>Get status info for dashboard.</summary>
    public List<StrategyInfo> GetStrategyInfos()
    {
        var discovered = DiscoverStrategies();
        var registry = _state.GetRegisteredStrategies().ToDictionary(r => r.Name);
        var result = new List<StrategyInfo>();
        var reportedKeys = new HashSet<string>();

        foreach (var name in discovered)
        {
            var isEnabled = registry.TryGetValue(name, out var regEntry) && regEntry.Enabled;
            var magicBase = regEntry?.MagicBase ?? 0;

            // Find all assignments for this strategy (runtime only)
            var assignments = _config.Strategies.Where(s => s.Strategy == name).ToList();

            foreach (var a in assignments)
            {
                var key = MakeKey(a.Strategy, a.Terminal);
                reportedKeys.Add(key);
                var process = _processes.TryGetValue(key, out var p) ? p : null;
                result.Add(new StrategyInfo
                {
                    Name = name,
                    Terminal = a.Terminal,
                    Magic = a.Magic,
                    Status = process?.Status.ToString().ToLower() ?? "stopped",
                    Port = process?.Port ?? 0,
                    Enabled = isEnabled,
                    MagicBase = magicBase
                });
            }

            // Also check _processes for running instances not in config
            foreach (var kvp in _processes)
            {
                if (!kvp.Key.StartsWith(name + "@")) continue;
                if (reportedKeys.Contains(kvp.Key)) continue;
                reportedKeys.Add(kvp.Key);
                var proc = kvp.Value;
                result.Add(new StrategyInfo
                {
                    Name = name,
                    Terminal = proc.TerminalId,
                    Magic = proc.Magic,
                    Status = proc.Status.ToString().ToLower(),
                    Port = proc.Port,
                    Enabled = isEnabled,
                    MagicBase = magicBase
                });
            }

            // If no assignments and no running processes, show as available
            if (!result.Any(r => r.Name == name))
            {
                result.Add(new StrategyInfo
                {
                    Name = name,
                    Status = "available",
                    Enabled = isEnabled,
                    MagicBase = magicBase
                });
            }
        }

        return result;
    }

    // -----------------------------------------------------------------------
    //  Events
    // -----------------------------------------------------------------------

    private void OnStrategyProcessStopped(StrategyProcess process, string? savedState)
    {
        // Persist state for restart
        if (savedState != null)
        {
            _state.SaveStrategyState(process.StrategyName, process.TerminalId, savedState);
            _log.Info($"[{process.StrategyName}@{process.TerminalId}] State saved");
        }
        _state.MarkStrategyInactive(process.StrategyName, process.TerminalId);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _scanTimer?.Dispose();
        foreach (var p in _processes.Values)
            p.Dispose();
        _processes.Clear();
    }
}

/// <summary>Strategy info for dashboard.</summary>
public class StrategyInfo
{
    public string Name { get; set; } = "";
    public string? Terminal { get; set; }
    public int Magic { get; set; }
    public string Status { get; set; } = "unknown";
    public int Port { get; set; }
    public bool Enabled { get; set; }
    public int MagicBase { get; set; }
}
