using Microsoft.Data.Sqlite;

namespace Daemon.Engine;

/// <summary>
/// Persists daemon state in SQLite: positions, terminal profiles, 3SL guard,
/// daily P/L, events, strategy state, execution quality, virtual trading.
/// Thread-safe via connection-per-call with WAL mode.
/// </summary>
public partial class StateManager : IDisposable
{
    private readonly string _dbPath;
    private readonly string _connStr;

    // Virtual ticket counter: negative numbers, decreasing (-1, -2, -3, ...)
    private long _nextVirtualTicket = -1;

    public StateManager(string dbPath)
    {
        _dbPath = dbPath;
        _connStr = $"Data Source={dbPath}";
        InitializeDatabase();
        InitVirtualTicketCounter();
    }

    // ===================================================================
    // Schema
    // ===================================================================

    private void InitializeDatabase()
    {
        using var conn = Open();

        // WAL mode for concurrent reads + single writer
        Exec(conn, "PRAGMA journal_mode=WAL");
        Exec(conn, "PRAGMA foreign_keys=ON");

        Exec(conn, @"
        CREATE TABLE IF NOT EXISTS terminal_profiles (
            terminal_id     TEXT PRIMARY KEY,
            type            TEXT NOT NULL DEFAULT 'demo',
            account_type    TEXT NOT NULL DEFAULT 'hedge',
            mode            TEXT NOT NULL DEFAULT 'auto',
            server_timezone TEXT NOT NULL DEFAULT 'UTC',
            daily_dd_limit  REAL NOT NULL DEFAULT 5000,
            cum_dd_limit    REAL NOT NULL DEFAULT 10000,
            max_risk_trade  REAL NOT NULL DEFAULT 1000,
            risk_type       TEXT NOT NULL DEFAULT 'usd',
            max_margin_trade REAL NOT NULL DEFAULT 20,
            max_deposit_load REAL NOT NULL DEFAULT 50,
            news_guard_on   INTEGER NOT NULL DEFAULT 1,
            news_window_min INTEGER NOT NULL DEFAULT 15,
            news_min_impact INTEGER NOT NULL DEFAULT 2,
            news_be_enabled INTEGER NOT NULL DEFAULT 1,
            news_include_usd INTEGER NOT NULL DEFAULT 1,
            sl3_guard_on    INTEGER NOT NULL DEFAULT 1,
            volume_mode     TEXT NOT NULL DEFAULT 'full',
            no_trade_start  TEXT DEFAULT NULL,
            no_trade_end    TEXT DEFAULT NULL,
            updated_at      TEXT NOT NULL DEFAULT (datetime('now'))
        )");

        // Migrations for terminal_profiles
        try { Exec(conn, "ALTER TABLE terminal_profiles ADD COLUMN no_trade_start TEXT DEFAULT NULL"); } catch { }
        try { Exec(conn, "ALTER TABLE terminal_profiles ADD COLUMN no_trade_end TEXT DEFAULT NULL"); } catch { }
        try { Exec(conn, "ALTER TABLE terminal_profiles ADD COLUMN no_trade_on INTEGER NOT NULL DEFAULT 1"); } catch { }
        // Phase 9.V: Virtual trading columns
        try { Exec(conn, "ALTER TABLE terminal_profiles ADD COLUMN virtual_balance REAL DEFAULT NULL"); } catch { }
        try { Exec(conn, "ALTER TABLE terminal_profiles ADD COLUMN virtual_margin REAL NOT NULL DEFAULT 0"); } catch { }
        try { Exec(conn, "ALTER TABLE terminal_profiles ADD COLUMN commission_per_lot REAL NOT NULL DEFAULT 0"); } catch { }
        try { Exec(conn, "ALTER TABLE terminal_profiles ADD COLUMN margin_trade_mode TEXT NOT NULL DEFAULT 'block'"); } catch { }
        try { Exec(conn, "ALTER TABLE terminal_profiles ADD COLUMN daily_dd_mode TEXT NOT NULL DEFAULT 'hard'"); } catch { }
        try { Exec(conn, "ALTER TABLE terminal_profiles ADD COLUMN r_cap_on INTEGER NOT NULL DEFAULT 0"); } catch { }
        try { Exec(conn, "ALTER TABLE terminal_profiles ADD COLUMN r_cap_limit REAL NOT NULL DEFAULT 0"); } catch { }
        try { Exec(conn, "ALTER TABLE terminal_profiles ADD COLUMN daily_dd_percent REAL NOT NULL DEFAULT 0"); } catch { }

        Exec(conn, @"
        CREATE TABLE IF NOT EXISTS positions (
            ticket          INTEGER NOT NULL,
            terminal_id     TEXT NOT NULL,
            symbol          TEXT NOT NULL,
            direction       TEXT NOT NULL,
            volume          REAL NOT NULL,
            price_open      REAL NOT NULL,
            sl              REAL NOT NULL DEFAULT 0,
            tp              REAL NOT NULL DEFAULT 0,
            magic           INTEGER NOT NULL DEFAULT 0,
            source          TEXT NOT NULL DEFAULT 'unmanaged',
            signal_data     TEXT,
            opened_at       TEXT NOT NULL,
            closed_at       TEXT,
            close_price     REAL,
            close_reason    TEXT,
            pnl             REAL,
            PRIMARY KEY (ticket, terminal_id)
        )");

        // Phase 9.V: Virtual trading columns on positions
        try { Exec(conn, "ALTER TABLE positions ADD COLUMN is_virtual INTEGER NOT NULL DEFAULT 0"); } catch { }
        try { Exec(conn, "ALTER TABLE positions ADD COLUMN timeframe TEXT DEFAULT NULL"); } catch { }

        Exec(conn, @"
        CREATE TABLE IF NOT EXISTS events (
            id              INTEGER PRIMARY KEY AUTOINCREMENT,
            timestamp       TEXT NOT NULL DEFAULT (datetime('now')),
            terminal_id     TEXT,
            type            TEXT NOT NULL,
            strategy        TEXT,
            message         TEXT NOT NULL,
            data            TEXT
        )");

        Exec(conn, @"
        CREATE TABLE IF NOT EXISTS strategy_state (
            strategy_name   TEXT NOT NULL,
            terminal_id     TEXT NOT NULL,
            state_json      TEXT NOT NULL,
            saved_at        TEXT NOT NULL DEFAULT (datetime('now')),
            PRIMARY KEY (strategy_name, terminal_id)
        )");

        Exec(conn, @"
        CREATE TABLE IF NOT EXISTS active_strategies (
            strategy_name   TEXT NOT NULL,
            terminal_id     TEXT NOT NULL,
            started_at      TEXT NOT NULL DEFAULT (datetime('now')),
            PRIMARY KEY (strategy_name, terminal_id)
        )");

        Exec(conn, @"
        CREATE TABLE IF NOT EXISTS strategy_registry (
            strategy_name   TEXT PRIMARY KEY,
            enabled         INTEGER NOT NULL DEFAULT 0,
            magic_base      INTEGER NOT NULL DEFAULT 0,
            discovered_at   TEXT NOT NULL DEFAULT (datetime('now')),
            enabled_at      TEXT DEFAULT NULL,
            notes           TEXT DEFAULT NULL
        )");

        Exec(conn, @"
        CREATE TABLE IF NOT EXISTS combo_magics (
            strategy_name   TEXT NOT NULL,
            combo_key       TEXT NOT NULL,
            magic_offset    INTEGER NOT NULL,
            created_at      TEXT NOT NULL DEFAULT (datetime('now')),
            PRIMARY KEY (strategy_name, combo_key)
        )");

        Exec(conn, @"
        CREATE TABLE IF NOT EXISTS sl3_state (
            terminal_id     TEXT PRIMARY KEY,
            consecutive_sl  INTEGER NOT NULL DEFAULT 0,
            blocked         INTEGER NOT NULL DEFAULT 0,
            blocked_at      TEXT,
            last_sl_at      TEXT
        )");

        Exec(conn, @"
        CREATE TABLE IF NOT EXISTS daily_pnl (
            terminal_id     TEXT NOT NULL,
            date            TEXT NOT NULL,
            realized_pnl    REAL NOT NULL DEFAULT 0,
            high_water_mark REAL NOT NULL DEFAULT 0,
            PRIMARY KEY (terminal_id, date)
        )");
        try { Exec(conn, "ALTER TABLE daily_pnl ADD COLUMN dd_snapshot REAL NOT NULL DEFAULT 0"); } catch { }

        Exec(conn, @"
        CREATE TABLE IF NOT EXISTS execution_quality (
            id              INTEGER PRIMARY KEY AUTOINCREMENT,
            ticket          INTEGER NOT NULL,
            terminal_id     TEXT NOT NULL,
            symbol          TEXT NOT NULL,
            direction       TEXT NOT NULL,
            signal_price    REAL NOT NULL,
            fill_price      REAL NOT NULL,
            slippage_pts    REAL NOT NULL,
            signal_time     TEXT NOT NULL,
            fill_time       TEXT NOT NULL,
            latency_ms      INTEGER NOT NULL,
            strategy        TEXT NOT NULL
        )");

        // Indexes
        Exec(conn, "CREATE INDEX IF NOT EXISTS idx_positions_terminal ON positions(terminal_id, closed_at)");
        Exec(conn, "CREATE INDEX IF NOT EXISTS idx_events_terminal ON events(terminal_id, timestamp)");
        Exec(conn, "CREATE INDEX IF NOT EXISTS idx_events_type ON events(type)");

        Exec(conn, @"
        CREATE TABLE IF NOT EXISTS symbol_sizing (
            terminal_id     TEXT NOT NULL,
            symbol          TEXT NOT NULL,
            enabled         INTEGER NOT NULL DEFAULT 1,
            risk_pct        REAL NOT NULL DEFAULT 0.50,
            max_lot         REAL DEFAULT NULL,
            margin_initial  REAL DEFAULT NULL,
            asset_class     TEXT NOT NULL DEFAULT 'forex',
            notes           TEXT DEFAULT NULL,
            PRIMARY KEY (terminal_id, symbol)
        )");

        // Migration: add asset_class to existing DBs
        try { Exec(conn, "ALTER TABLE symbol_sizing ADD COLUMN asset_class TEXT NOT NULL DEFAULT 'forex'"); } catch { }
        // Migration: add risk_factor (multiplier 0.0-1.0 of profile base risk). Reset all to 1.0.
        try { Exec(conn, "ALTER TABLE symbol_sizing ADD COLUMN risk_factor REAL NOT NULL DEFAULT 1.0"); } catch { }
        // Migration: add tier (T1/T2/T3 quality tier from backtest)
        try { Exec(conn, "ALTER TABLE symbol_sizing ADD COLUMN tier TEXT NOT NULL DEFAULT 'T1'"); } catch { }

        // ===============================================================
        // Phase 9.V: Virtual Trading Tables
        // ===============================================================

        // Equity snapshots for virtual positions (charting)
        Exec(conn, @"
        CREATE TABLE IF NOT EXISTS virtual_equity (
            id              INTEGER PRIMARY KEY AUTOINCREMENT,
            terminal_id     TEXT NOT NULL,
            timestamp       TEXT NOT NULL DEFAULT (datetime('now')),
            equity          REAL NOT NULL,
            balance         REAL NOT NULL,
            unrealized_pnl  REAL NOT NULL DEFAULT 0,
            open_positions  INTEGER NOT NULL DEFAULT 0
        )");
        Exec(conn, "CREATE INDEX IF NOT EXISTS idx_virtual_equity ON virtual_equity(terminal_id, timestamp)");

        // SL history for Trade Chart trail visualization (universal: virtual + real)
        Exec(conn, @"
        CREATE TABLE IF NOT EXISTS sl_history (
            id          INTEGER PRIMARY KEY AUTOINCREMENT,
            ticket      INTEGER NOT NULL,
            terminal_id TEXT NOT NULL,
            old_sl      REAL NOT NULL,
            new_sl      REAL NOT NULL,
            bar_time    INTEGER,
            changed_at  TEXT NOT NULL DEFAULT (datetime('now'))
        )");
        Exec(conn, "CREATE INDEX IF NOT EXISTS idx_sl_history_ticket ON sl_history(ticket, terminal_id)");

        // Trade snapshots: cached bars + SL history JSON for closed trades
        // BarsCache stores limited history; snapshots preserve chart data permanently
        Exec(conn, @"
        CREATE TABLE IF NOT EXISTS trade_snapshots (
            ticket      INTEGER NOT NULL,
            terminal_id TEXT NOT NULL,
            snapshot    TEXT NOT NULL,
            created_at  TEXT NOT NULL DEFAULT (datetime('now')),
            PRIMARY KEY (ticket, terminal_id)
        )");

        // Phase 9.R: R-cap daily accumulator (per strategy per terminal per day)
        Exec(conn, @"
        CREATE TABLE IF NOT EXISTS daily_r (
            terminal_id     TEXT NOT NULL,
            strategy        TEXT NOT NULL,
            date            TEXT NOT NULL,
            r_sum           REAL NOT NULL DEFAULT 0,
            trade_count     INTEGER NOT NULL DEFAULT 0,
            PRIMARY KEY (terminal_id, strategy, date)
        )");

        // Phase 9.R: protector_fired flag on positions
        try { Exec(conn, "ALTER TABLE positions ADD COLUMN protector_fired INTEGER NOT NULL DEFAULT 0"); } catch { }

        // Phase 9.M: class leverage per terminal (persisted from MT5 CALC_LEVERAGE)
        Exec(conn, @"
        CREATE TABLE IF NOT EXISTS class_leverage (
            terminal_id     TEXT NOT NULL,
            asset_class     TEXT NOT NULL,
            leverage        INTEGER NOT NULL,
            detected_at     TEXT NOT NULL,
            PRIMARY KEY (terminal_id, asset_class)
        )");

        // Global daemon state (pause, etc.)
        Exec(conn, @"
        CREATE TABLE IF NOT EXISTS daemon_state (
            key     TEXT PRIMARY KEY,
            value   TEXT NOT NULL
        )");

        // Pending orders (live MT5 + virtual simulation)
        Exec(conn, @"
        CREATE TABLE IF NOT EXISTS pending_orders (
            ticket          INTEGER NOT NULL,
            terminal_id     TEXT NOT NULL,
            symbol          TEXT NOT NULL,
            strategy        TEXT NOT NULL,
            magic           INTEGER NOT NULL,
            direction       TEXT NOT NULL,      -- 'BUY'/'SELL'
            order_type      TEXT NOT NULL,      -- 'BUY_STOP'/'SELL_STOP'
            volume          REAL NOT NULL,
            entry_price     REAL NOT NULL,
            sl              REAL NOT NULL,
            tp              REAL NOT NULL DEFAULT 0,
            bars_remaining  INTEGER NOT NULL DEFAULT -1,  -- -1 = GTC, >0 = counting down, 0 = expired
            signal_data     TEXT,
            is_virtual      INTEGER NOT NULL DEFAULT 0,
            placed_at       TEXT NOT NULL,
            status          TEXT NOT NULL DEFAULT 'open', -- 'open'/'filled'/'cancelled'/'expired'
            closed_at       TEXT,
            PRIMARY KEY (ticket, terminal_id)
        )");
    }

    // ===================================================================
    // Virtual Ticket Counter
    // ===================================================================

    /// <summary>Get next virtual ticket (negative, thread-safe).</summary>
    public long NextVirtualTicket()
    {
        return Interlocked.Decrement(ref _nextVirtualTicket);
    }

    /// <summary>Restore virtual ticket counter from DB on startup.</summary>
    private void InitVirtualTicketCounter()
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT MIN(ticket) FROM positions WHERE is_virtual = 1";
        var result = cmd.ExecuteScalar();
        if (result != null && result != DBNull.Value)
        {
            _nextVirtualTicket = Convert.ToInt64(result) - 1;
        }
    }


    // ===================================================================
    // Helpers
    // ===================================================================

    /// <summary>Safely try to read a double column that may not exist (migration safety).</summary>
    private static double? TryGetDouble(SqliteDataReader r, string column)
    {
        try
        {
            var ordinal = r.GetOrdinal(column);
            return r.IsDBNull(ordinal) ? null : r.GetDouble(ordinal);
        }
        catch { return null; }
    }

    private SqliteConnection Open()
    {
        var conn = new SqliteConnection(_connStr);
        conn.Open();
        return conn;
    }

    private static void Exec(SqliteConnection conn, string sql, params (string name, object value)[] parameters)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (name, value) in parameters)
            cmd.Parameters.AddWithValue(name, value);
        cmd.ExecuteNonQuery();
    }

    // ===================================================================
    // Terminal lifecycle â€” delete all data for a terminal
    // ===================================================================

    /// <summary>Delete all data for a terminal from all tables. Runs in a transaction.</summary>
    public int DeleteTerminalData(string terminalId)
    {
        using var conn = Open();
        using var tx = conn.BeginTransaction();
        int totalRows = 0;

        var tables = new[]
        {
            "terminal_profiles", "positions", "events", "strategy_state",
            "active_strategies", "sl3_state", "daily_pnl",
            "execution_quality", "symbol_sizing",
            "virtual_equity", "sl_history", "trade_snapshots",
            "daily_r",
            "class_leverage"
        };

        foreach (var table in tables)
        {
            using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = $"DELETE FROM {table} WHERE terminal_id = @tid";
            cmd.Parameters.AddWithValue("@tid", terminalId);
            totalRows += cmd.ExecuteNonQuery();
        }

        tx.Commit();
        return totalRows;
    }

    public void Dispose()
    {
        // Nothing to dispose -- connections are opened/closed per call
    }

    // ===================================================================
    // Combo Magic Resolution
    // ===================================================================

    /// <summary>
    /// Resolve magic number for a combo. Multi-combo strategies (REMR etc.)
    /// get unique magic per combo_key = magic_base + offset.
    /// Single-combo strategies (no combo_key in signal_data) get magic_base as-is.
    ///
    /// Offsets are auto-assigned sequentially and persisted in combo_magics table.
    /// Range: magic_base + 0 .. magic_base + 999 (1000 slots per strategy).
    /// </summary>
    public int ResolveComboMagic(string strategyName, int magicBase, string? signalData)
    {
        // No signal_data → single-combo strategy, use base magic
        if (string.IsNullOrEmpty(signalData))
            return magicBase;

        // Try to extract combo_key from signal_data JSON
        string? comboKey = null;
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(signalData);
            if (doc.RootElement.TryGetProperty("combo_key", out var ck))
                comboKey = ck.GetString();
        }
        catch { }

        // No combo_key in signal_data → single-combo behavior
        if (string.IsNullOrEmpty(comboKey))
            return magicBase;

        using var conn = Open();

        // Look up existing offset
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT magic_offset FROM combo_magics WHERE strategy_name = @s AND combo_key = @k";
            cmd.Parameters.AddWithValue("@s", strategyName);
            cmd.Parameters.AddWithValue("@k", comboKey);
            var result = cmd.ExecuteScalar();
            if (result != null && result != DBNull.Value)
                return magicBase + Convert.ToInt32(result);
        }

        // Assign next offset
        int nextOffset;
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT COALESCE(MAX(magic_offset), -1) + 1 FROM combo_magics WHERE strategy_name = @s";
            cmd.Parameters.AddWithValue("@s", strategyName);
            nextOffset = Convert.ToInt32(cmd.ExecuteScalar());
        }

        // Persist
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = @"INSERT OR IGNORE INTO combo_magics (strategy_name, combo_key, magic_offset)
                                VALUES (@s, @k, @o)";
            cmd.Parameters.AddWithValue("@s", strategyName);
            cmd.Parameters.AddWithValue("@k", comboKey);
            cmd.Parameters.AddWithValue("@o", nextOffset);
            cmd.ExecuteNonQuery();
        }

        return magicBase + nextOffset;
    }

    /// <summary>Get all combo magic offsets for a strategy (for diagnostics/dashboard).</summary>
    public Dictionary<string, int> GetComboMagics(string strategyName)
    {
        var result = new Dictionary<string, int>();
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT combo_key, magic_offset FROM combo_magics WHERE strategy_name = @s ORDER BY magic_offset";
        cmd.Parameters.AddWithValue("@s", strategyName);
        using var r = cmd.ExecuteReader();
        while (r.Read())
            result[r.GetString(0)] = r.GetInt32(1);
        return result;
    }
}

// ===================================================================
// Record types for StateManager
// ===================================================================

public class PositionRecord
{
    public long Ticket { get; set; }
    public string TerminalId { get; set; } = "";
    public string Symbol { get; set; } = "";
    public string Direction { get; set; } = "";     // BUY / SELL
    public double Volume { get; set; }
    public double PriceOpen { get; set; }
    public double SL { get; set; }
    public double TP { get; set; }
    public int Magic { get; set; }
    public string Source { get; set; } = "unmanaged";
    public string? SignalData { get; set; }
    public string OpenedAt { get; set; } = "";
    public string? ClosedAt { get; set; }
    public double? ClosePrice { get; set; }
    public string? CloseReason { get; set; }
    public double? PnL { get; set; }

    // Phase 9.V: Virtual trading fields
    public bool IsVirtual { get; set; }
    public string? Timeframe { get; set; }

    // Phase 9.R: Protector flag — set when MODIFY_SL (protector) fires
    public bool ProtectorFired { get; set; }

    public bool IsOpen => ClosedAt == null;
}

public class TerminalProfile
{
    public string TerminalId { get; set; } = "";
    public string Type { get; set; } = "demo";          // prop/real/demo/test
    public string AccountType { get; set; } = "hedge";   // hedge/netting
    public string Mode { get; set; } = "auto";           // auto/semi/monitor/virtual
    public string ServerTimezone { get; set; } = "UTC";
    public double DailyDDLimit { get; set; } = 5000;
    public string DailyDdMode { get; set; } = "hard";    // "soft" = realized-only latch, "hard" = realized+unrealized + force-close
    public double DailyDdPercent { get; set; } = 0;       // 0 = disabled (use fixed $), >0 = % for EOD snapshot recalc
    public double CumDDLimit { get; set; } = 10000;
    public double MaxRiskTrade { get; set; } = 1000;
    public string RiskType { get; set; } = "usd";        // usd/pct
    public double MaxMarginTrade { get; set; } = 20;
    public string MarginTradeMode { get; set; } = "block";   // "block" = reject, "reduce" = shrink lot to fit
    public double MaxDepositLoad { get; set; } = 50;
    public bool NewsGuardOn { get; set; } = true;
    public int NewsWindowMin { get; set; } = 15;
    public int NewsMinImpact { get; set; } = 2;
    public bool NewsBeEnabled { get; set; } = true;
    public bool NewsIncludeUsd { get; set; } = true;
    public bool Sl3GuardOn { get; set; } = true;
    public string VolumeMode { get; set; } = "full";
    public string? NoTradeStart { get; set; }          // "23:30" broker time, null = disabled
    public string? NoTradeEnd { get; set; }            // "01:30" broker time
    public bool NoTradeOn { get; set; } = true;        // toggle on tile (keeps times saved)

    // Phase 9.V: Virtual trading fields
    public double? VirtualBalance { get; set; }        // null = not initialized
    public double VirtualMargin { get; set; }          // current virtual margin usage
    public double CommissionPerLot { get; set; }       // round-trip commission per lot ($)

    // Phase 9.R: R-cap dashboard override
    public bool RCapOn { get; set; }                   // toggle from dashboard
    public double RCapLimit { get; set; }              // R-cap value (0 = use strategy config default)
}

public class EventRecord
{
    public int Id { get; set; }
    public string Timestamp { get; set; } = "";
    public string? TerminalId { get; set; }
    public string Type { get; set; } = "";
    public string? Strategy { get; set; }
    public string Message { get; set; } = "";
    public string? Data { get; set; }
}

public class SymbolSizing
{
    public string TerminalId { get; set; } = "";
    public string Symbol { get; set; } = "";
    public bool Enabled { get; set; } = true;
    public double RiskFactor { get; set; } = 1.0;
    public double? MaxLot { get; set; }
    public double? MarginInitial { get; set; }
    public string AssetClass { get; set; } = "forex";
    public string Tier { get; set; } = "T1";
    public string? Notes { get; set; }
}

public class StrategyRegistryEntry
{
    public string Name { get; set; } = "";
    public bool Enabled { get; set; }
    public int MagicBase { get; set; }
    public string DiscoveredAt { get; set; } = "";
    public string? EnabledAt { get; set; }
    public string? Notes { get; set; }
}

// Phase 9.V: Virtual trading record types

public class VirtualEquityPoint
{
    public string Timestamp { get; set; } = "";
    public double Equity { get; set; }
    public double Balance { get; set; }
    public double UnrealizedPnl { get; set; }
    public int OpenPositions { get; set; }
}

public class SlHistoryEntry
{
    public double OldSl { get; set; }
    public double NewSl { get; set; }
    public long BarTime { get; set; }
    public string ChangedAt { get; set; } = "";
}

public class VirtualStats
{
    public int TotalTrades { get; set; }
    public int Wins { get; set; }
    public int Losses { get; set; }
    public double WinRate { get; set; }
    public double AvgWin { get; set; }
    public double AvgLoss { get; set; }
    public double ProfitFactor { get; set; }
    public double NetPnl { get; set; }
    public double MaxDrawdown { get; set; }
    public double MaxDrawdownPct { get; set; }
    public double Expectancy { get; set; }
}
