using System.Text.Json.Serialization;

namespace Daemon.Config;

/// <summary>Root config loaded from config.json.</summary>
public class DaemonConfig
{
    [JsonPropertyName("python_path")]
    public string PythonPath { get; set; } = "python";

    [JsonPropertyName("worker_script")]
    public string WorkerScript { get; set; } = @"..\workers\mt5_worker.py";

    [JsonPropertyName("heartbeat_interval_sec")]
    public int HeartbeatIntervalSec { get; set; } = 10;

    [JsonPropertyName("terminals")]
    public List<TerminalConfig> Terminals { get; set; } = new();

    // Telegram alerts
    [JsonPropertyName("telegram_bot_token")]
    public string? TelegramBotToken { get; set; }

    [JsonPropertyName("telegram_chat_id")]
    public string? TelegramChatId { get; set; }

    /// <summary>Telegram heartbeat interval in hours (0 = disabled). Default: 4.</summary>
    [JsonPropertyName("telegram_heartbeat_hours")]
    public double TelegramHeartbeatHours { get; set; } = 4;

    // News calendar
    [JsonPropertyName("news_calendar_file")]
    public string? NewsCalendarFile { get; set; }       // Path to local JSON file

    [JsonPropertyName("news_calendar_url")]
    public string? NewsCalendarUrl { get; set; }        // HTTP endpoint for fetching calendar

    // -----------------------------------------------------------------------
    //  Phase 6: Strategy configuration
    // -----------------------------------------------------------------------

    /// <summary>Path to strategies root folder (contains subfolders per strategy).</summary>
    [JsonPropertyName("strategy_dir")]
    public string StrategyDir { get; set; } = @"..\strategies";

    /// <summary>Path to runner.py (universal runner script).</summary>
    [JsonPropertyName("runner_script")]
    public string RunnerScript { get; set; } = @"..\strategies\runner.py";

    /// <summary>Base port for strategy TCP connections. Each strategy gets base + offset.</summary>
    [JsonPropertyName("strategy_base_port")]
    public int StrategyBasePort { get; set; } = 5600;

    /// <summary>Scheduler poll interval -- how often to check for new candle closes (seconds).</summary>
    [JsonPropertyName("scheduler_interval_sec")]
    public int SchedulerIntervalSec { get; set; } = 10;

    /// <summary>Strategy assignments -- which strategy runs on which terminal.</summary>
    [JsonPropertyName("strategies")]
    public List<StrategyAssignment> Strategies { get; set; } = new();

    // -----------------------------------------------------------------------
    //  Phase 7: Dashboard configuration
    // -----------------------------------------------------------------------

    /// <summary>Enable/disable the embedded dashboard web server.</summary>
    [JsonPropertyName("dashboard_enabled")]
    public bool DashboardEnabled { get; set; } = true;

    /// <summary>Dashboard HTTP port. Default: 8080.</summary>
    [JsonPropertyName("dashboard_port")]
    public int DashboardPort { get; set; } = 8080;

    /// <summary>Dashboard listen host. Use "localhost" for local, "+" for all interfaces.</summary>
    [JsonPropertyName("dashboard_host")]
    public string DashboardHost { get; set; } = "localhost";

    /// <summary>Path to wwwroot folder with dashboard static files.</summary>
    [JsonPropertyName("dashboard_wwwroot")]
    public string? DashboardWwwroot { get; set; }

    // -----------------------------------------------------------------------
    //  Phase 9.V: Virtual Trading configuration
    // -----------------------------------------------------------------------

    /// <summary>
    /// Virtual slippage in points added to fill price.
    /// Always worsens the price (BUY higher, SELL lower).
    /// Default: 0.5 (about half a typical spread).
    /// Set to 0 for no slippage simulation.
    /// </summary>
    [JsonPropertyName("virtual_slippage_points")]
    public double VirtualSlippagePoints { get; set; } = 0.5;

    /// <summary>
    /// Default round-trip commission per lot in USD.
    /// Applied when terminal profile has no commission_per_lot set.
    /// Typical ECN values: $3-7 per lot round-trip.
    /// Default: 0 (no commission).
    /// </summary>
    [JsonPropertyName("default_commission_per_lot")]
    public double DefaultCommissionPerLot { get; set; } = 0;

    // -----------------------------------------------------------------------
    //  Phase 10+: Backtester configuration
    // -----------------------------------------------------------------------

    /// <summary>
    /// Path to cost model JSON (per-symbol spread + slippage + aliases).
    /// Relative to daemon working directory or absolute.
    /// Default: "cost_model_v2.json" (same folder as daemon executable).
    /// </summary>
    [JsonPropertyName("cost_model_path")]
    public string CostModelPath { get; set; } = "cost_model_v2.json";

    /// <summary>
    /// Path to bars_history.db (SQLite database for historical bars and backtest runs).
    /// Default: "bars_history.db" (same folder as state.db).
    /// </summary>
    [JsonPropertyName("bars_history_db")]
    public string BarsHistoryDb { get; set; } = "bars_history.db";
}

/// <summary>Config for a single MT5 terminal connection.</summary>
public class TerminalConfig
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("terminal_path")]
    public string TerminalPath { get; set; } = "";

    [JsonPropertyName("port")]
    public int Port { get; set; } = 5501;

    [JsonPropertyName("login")]
    public int? Login { get; set; }

    [JsonPropertyName("password")]
    public string? Password { get; set; }

    [JsonPropertyName("server")]
    public string? Server { get; set; }

    [JsonPropertyName("auto_connect")]
    public bool AutoConnect { get; set; } = true;

    /// <summary>If false, terminal is disabled -- worker not started, not included in heartbeat.
    /// Unlike auto_connect (which only skips initial startup), enabled=false is a persistent disable.</summary>
    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; } = true;

    /// <summary>Display order in dashboard. Lower values first.</summary>
    [JsonPropertyName("sort_order")]
    public int SortOrder { get; set; } = 0;

    /// <summary>Canonical -> Broker symbol mapping. e.g. EURUSD -> EURUSDi</summary>
    [JsonPropertyName("symbol_map")]
    public Dictionary<string, string> SymbolMap { get; set; } = new();
}

/// <summary>
/// Binds a strategy to a terminal.
/// Multiple strategies can run on different terminals.
/// </summary>
public class StrategyAssignment
{
    /// <summary>Strategy folder name inside strategy_dir (e.g. "bb_mr_v2").</summary>
    [JsonPropertyName("strategy")]
    public string Strategy { get; set; } = "";

    /// <summary>Terminal ID this strategy trades on.</summary>
    [JsonPropertyName("terminal")]
    public string Terminal { get; set; } = "";

    /// <summary>Magic number for this strategy-terminal pair. Unique per terminal.</summary>
    [JsonPropertyName("magic")]
    public int Magic { get; set; } = 100;

    /// <summary>Auto-start on daemon launch. If false, must be started from dashboard.</summary>
    [JsonPropertyName("auto_start")]
    public bool AutoStart { get; set; } = false;

    /// <summary>Override config file path (default: strategy_dir/strategy/config.json).</summary>
    [JsonPropertyName("config_override")]
    public string? ConfigOverride { get; set; }

    /// <summary>
    /// Override effective risk pct per trade for this assignment.
    /// If null, uses the strategy's own config.
    /// Useful when same strategy runs on terminals with different DD limits.
    /// </summary>
    [JsonPropertyName("risk_pct_override")]
    public double? RiskPctOverride { get; set; }

    /// <summary>
    /// R-cap: maximum cumulative R-loss per day for this strategy.
    /// When daily R-sum drops to or below -r_cap, Gate 12 blocks new entries.
    /// Example: r_cap=1.5 means the strategy stops after losing 1.5R in a day.
    /// null = disabled (no R-cap limit).
    /// This is INDEPENDENT of G2 daily DD (which works in dollars).
    /// R-result is calculated from close_reason + signal_data:
    ///   TP hit           → +tp_r
    ///   SL hit           → -1.0R
    ///   SL + protector   → protector_lock_r (e.g. -0.50R)
    /// </summary>
    [JsonPropertyName("r_cap")]
    public double? RCap { get; set; }
}
