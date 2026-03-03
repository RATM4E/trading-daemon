using Microsoft.Data.Sqlite;

namespace Daemon.Engine;

public partial class StateManager
{
    // ===================================================================
    // Events
    // ===================================================================

    public void LogEvent(string type, string? terminalId, string? strategy, string message, string? data = null)
    {
        using var conn = Open();
        Exec(conn, @"
            INSERT INTO events (type, terminal_id, strategy, message, data)
            VALUES (@type, @tid, @strat, @msg, @data)",
            ("@type", type),
            ("@tid", terminalId ?? (object)DBNull.Value),
            ("@strat", strategy ?? (object)DBNull.Value),
            ("@msg", message),
            ("@data", data ?? (object)DBNull.Value));
    }

    public List<EventRecord> GetEvents(string? terminalId = null, string? type = null, int limit = 50)
    {
        using var conn = Open();
        var sql = "SELECT * FROM events WHERE 1=1";
        if (terminalId != null) sql += " AND terminal_id = @tid";
        if (type != null) sql += " AND type = @type";
        sql += " ORDER BY id DESC LIMIT @limit";

        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        if (terminalId != null) cmd.Parameters.AddWithValue("@tid", terminalId);
        if (type != null) cmd.Parameters.AddWithValue("@type", type);
        cmd.Parameters.AddWithValue("@limit", limit);

        var result = new List<EventRecord>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            result.Add(new EventRecord
            {
                Id = r.GetInt32(r.GetOrdinal("id")),
                Timestamp = r.GetString(r.GetOrdinal("timestamp")),
                TerminalId = r.IsDBNull(r.GetOrdinal("terminal_id")) ? null : r.GetString(r.GetOrdinal("terminal_id")),
                Type = r.GetString(r.GetOrdinal("type")),
                Strategy = r.IsDBNull(r.GetOrdinal("strategy")) ? null : r.GetString(r.GetOrdinal("strategy")),
                Message = r.GetString(r.GetOrdinal("message")),
                Data = r.IsDBNull(r.GetOrdinal("data")) ? null : r.GetString(r.GetOrdinal("data")),
            });
        }
        return result;
    }

    // ===================================================================
    // Strategy State
    // ===================================================================

    public void SaveStrategyState(string name, string terminalId, string stateJson)
    {
        using var conn = Open();
        Exec(conn, @"
            INSERT INTO strategy_state (strategy_name, terminal_id, state_json, saved_at)
            VALUES (@name, @tid, @json, datetime('now'))
            ON CONFLICT(strategy_name, terminal_id) DO UPDATE SET
                state_json = @json, saved_at = datetime('now')",
            ("@name", name), ("@tid", terminalId), ("@json", stateJson));
    }

    public string? GetStrategyState(string name, string terminalId)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT state_json FROM strategy_state WHERE strategy_name = @n AND terminal_id = @tid";
        cmd.Parameters.AddWithValue("@n", name);
        cmd.Parameters.AddWithValue("@tid", terminalId);
        return cmd.ExecuteScalar() as string;
    }

    // ===================================================================
    // Active Strategies (crash recovery)
    // ===================================================================

    public void MarkStrategyActive(string name, string terminalId)
    {
        using var conn = Open();
        Exec(conn, @"
            INSERT INTO active_strategies (strategy_name, terminal_id, started_at)
            VALUES (@name, @tid, datetime('now'))
            ON CONFLICT(strategy_name, terminal_id) DO UPDATE SET started_at = datetime('now')",
            ("@name", name), ("@tid", terminalId));
    }

    public void MarkStrategyInactive(string name, string terminalId)
    {
        using var conn = Open();
        Exec(conn, "DELETE FROM active_strategies WHERE strategy_name = @name AND terminal_id = @tid",
            ("@name", name), ("@tid", terminalId));
    }

    public void ClearActiveStrategies()
    {
        using var conn = Open();
        Exec(conn, "DELETE FROM active_strategies");
    }

    public List<(string Name, string TerminalId)> GetActiveStrategies()
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT strategy_name, terminal_id FROM active_strategies";
        var result = new List<(string, string)>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            result.Add((reader.GetString(0), reader.GetString(1)));
        return result;
    }

    // ===================================================================
    // Strategy Registry (auto-discovery)
    // ===================================================================

    /// <summary>Register a newly discovered strategy (disabled by default).</summary>
    public void RegisterStrategy(string name, int magicBase)
    {
        using var conn = Open();
        Exec(conn, @"
            INSERT OR IGNORE INTO strategy_registry (strategy_name, magic_base, enabled, discovered_at)
            VALUES (@name, @magic, 0, datetime('now'))",
            ("@name", name), ("@magic", magicBase));
    }

    /// <summary>Enable or disable a strategy.</summary>
    public void SetStrategyEnabled(string name, bool enabled)
    {
        using var conn = Open();
        Exec(conn, @"
            UPDATE strategy_registry
            SET enabled = @enabled, enabled_at = CASE WHEN @enabled = 1 THEN datetime('now') ELSE NULL END
            WHERE strategy_name = @name",
            ("@enabled", enabled ? 1 : 0), ("@name", name));
    }

    /// <summary>Check if a strategy is enabled.</summary>
    public bool IsStrategyEnabled(string name)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT enabled FROM strategy_registry WHERE strategy_name = @name";
        cmd.Parameters.AddWithValue("@name", name);
        var result = cmd.ExecuteScalar();
        return result != null && Convert.ToInt32(result) == 1;
    }

    /// <summary>Get all registered strategies.</summary>
    public List<StrategyRegistryEntry> GetRegisteredStrategies()
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT strategy_name, enabled, magic_base, discovered_at, enabled_at, notes FROM strategy_registry";
        var result = new List<StrategyRegistryEntry>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            result.Add(new StrategyRegistryEntry
            {
                Name = r.GetString(0),
                Enabled = r.GetInt32(1) == 1,
                MagicBase = r.GetInt32(2),
                DiscoveredAt = r.GetString(3),
                EnabledAt = r.IsDBNull(4) ? null : r.GetString(4),
                Notes = r.IsDBNull(5) ? null : r.GetString(5)
            });
        }
        return result;
    }

    /// <summary>Remove a strategy from registry (folder deleted).</summary>
    public void UnregisterStrategy(string name)
    {
        using var conn = Open();
        Exec(conn, "DELETE FROM strategy_registry WHERE strategy_name = @name",
            ("@name", name));
    }

    /// <summary>Get the next available magic base (each strategy gets a block of 1000).</summary>
    public int GetNextMagicBase()
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT MAX(magic_base) FROM strategy_registry";
        var result = cmd.ExecuteScalar();
        if (result == null || result == DBNull.Value)
            return 100; // first strategy starts at magic 100
        return Convert.ToInt32(result) + 1000;
    }

    // ===================================================================
    // Execution Quality
    // ===================================================================

    public void LogExecution(long ticket, string terminalId, string symbol, string direction,
                              double signalPrice, double fillPrice, double tickSize,
                              DateTime signalTime, DateTime fillTime, string strategy)
    {
        var slippage = (fillPrice - signalPrice) / tickSize;
        if (direction == "SHORT") slippage = -slippage; // For shorts, higher fill = worse
        var latency = (int)(fillTime - signalTime).TotalMilliseconds;

        using var conn = Open();
        Exec(conn, @"
            INSERT INTO execution_quality
                (ticket, terminal_id, symbol, direction, signal_price, fill_price,
                 slippage_pts, signal_time, fill_time, latency_ms, strategy)
            VALUES (@t, @tid, @sym, @dir, @sp, @fp, @slip, @st, @ft, @lat, @strat)",
            ("@t", ticket), ("@tid", terminalId), ("@sym", symbol), ("@dir", direction),
            ("@sp", signalPrice), ("@fp", fillPrice), ("@slip", slippage),
            ("@st", signalTime.ToString("o")), ("@ft", fillTime.ToString("o")),
            ("@lat", latency), ("@strat", strategy));
    }

    public (double AvgSlippage, double AvgLatency, int Count) GetExecutionStats(
        string terminalId, string? since = null)
    {
        using var conn = Open();
        var sql = @"SELECT AVG(slippage_pts), AVG(latency_ms), COUNT(*)
                    FROM execution_quality WHERE terminal_id = @tid";
        if (since != null) sql += " AND fill_time >= @since";

        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.Parameters.AddWithValue("@tid", terminalId);
        if (since != null) cmd.Parameters.AddWithValue("@since", since);

        using var r = cmd.ExecuteReader();
        if (!r.Read() || r.IsDBNull(0)) return (0, 0, 0);
        return (r.GetDouble(0), r.GetDouble(1), r.GetInt32(2));
    }

    // ===================================================================
    // Symbol Sizing
    // ===================================================================

    public void SaveSymbolSizing(SymbolSizing s)
    {
        using var conn = Open();
        Exec(conn, @"
            INSERT INTO symbol_sizing (terminal_id, symbol, enabled, risk_pct, risk_factor, max_lot, margin_initial, asset_class, notes)
            VALUES (@tid, @sym, @enabled, @risk, @rfactor, @maxlot, @margin, @aclass, @notes)
            ON CONFLICT(terminal_id, symbol) DO UPDATE SET
                enabled = @enabled, risk_pct = @risk, risk_factor = @rfactor, max_lot = @maxlot,
                margin_initial = @margin, asset_class = @aclass, notes = @notes",
            ("@tid", s.TerminalId), ("@sym", s.Symbol),
            ("@enabled", s.Enabled ? 1 : 0), ("@risk", s.RiskFactor),
            ("@rfactor", s.RiskFactor),
            ("@maxlot", s.MaxLot.HasValue ? (object)s.MaxLot.Value : DBNull.Value),
            ("@margin", s.MarginInitial.HasValue ? (object)s.MarginInitial.Value : DBNull.Value),
            ("@aclass", s.AssetClass),
            ("@notes", s.Notes ?? (object)DBNull.Value));
    }

    public SymbolSizing? GetSymbolSizing(string terminalId, string symbol)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM symbol_sizing WHERE terminal_id = @tid AND symbol = @sym";
        cmd.Parameters.AddWithValue("@tid", terminalId);
        cmd.Parameters.AddWithValue("@sym", symbol);
        using var r = cmd.ExecuteReader();
        return r.Read() ? ReadSizing(r) : null;
    }

    public List<SymbolSizing> GetAllSymbolSizing(string terminalId)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM symbol_sizing WHERE terminal_id = @tid ORDER BY symbol";
        cmd.Parameters.AddWithValue("@tid", terminalId);
        using var r = cmd.ExecuteReader();
        var list = new List<SymbolSizing>();
        while (r.Read()) list.Add(ReadSizing(r));
        return list;
    }

    /// <summary>
    /// Initialize sizing defaults for all symbols if not already present.
    /// symbolDefaults: canonical_symbol -> (risk_factor, asset_class) from strategy config.
    /// risk_factor defaults to 1.0 (full base risk from terminal profile).
    /// </summary>
    public int InitSymbolSizingDefaults(string terminalId,
        Dictionary<string, (double RiskFactor, string AssetClass, string Tier)> symbolDefaults)
    {
        int created = 0;
        using var conn = Open();

        // Remove symbols not in the new config so sizing stays clean
        var inList = string.Join(",", symbolDefaults.Keys.Select(s => $"'{s.Replace("'", "''")}'"));
        Exec(conn, $"DELETE FROM symbol_sizing WHERE terminal_id = @tid AND symbol NOT IN ({inList})",
            ("@tid", terminalId));

        foreach (var (symbol, defaults) in symbolDefaults)
        {
            // Upsert: new rows get defaults, existing rows update asset_class + tier + risk_factor
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                INSERT INTO symbol_sizing (terminal_id, symbol, enabled, risk_factor, asset_class, tier)
                VALUES (@tid, @sym, 1, @rfactor, @aclass, @tier)
                ON CONFLICT(terminal_id, symbol) DO UPDATE SET
                    asset_class = @aclass, tier = @tier, risk_factor = @rfactor";
            cmd.Parameters.AddWithValue("@tid", terminalId);
            cmd.Parameters.AddWithValue("@sym", symbol);
            cmd.Parameters.AddWithValue("@rfactor", defaults.RiskFactor);
            cmd.Parameters.AddWithValue("@aclass", defaults.AssetClass);
            cmd.Parameters.AddWithValue("@tier", defaults.Tier);
            created += cmd.ExecuteNonQuery();
        }
        return created;
    }

    /// <summary>Update margin_initial cache for a symbol.</summary>
    public void UpdateSymbolMargin(string terminalId, string symbol, double marginInitial)
    {
        using var conn = Open();
        Exec(conn, @"
            UPDATE symbol_sizing SET margin_initial = @margin
            WHERE terminal_id = @tid AND symbol = @sym",
            ("@tid", terminalId), ("@sym", symbol), ("@margin", marginInitial));
    }

    /// <summary>Reset only risk_factor from config defaults. Does not touch enabled, notes, max_lot.</summary>
    public int ResetSizingFactors(string terminalId, Dictionary<string, double> symbolFactors)
    {
        int updated = 0;
        using var conn = Open();
        foreach (var (symbol, factor) in symbolFactors)
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                UPDATE symbol_sizing SET risk_factor = @rfactor
                WHERE terminal_id = @tid AND symbol = @sym";
            cmd.Parameters.AddWithValue("@tid", terminalId);
            cmd.Parameters.AddWithValue("@sym", symbol);
            cmd.Parameters.AddWithValue("@rfactor", factor);
            updated += cmd.ExecuteNonQuery();
        }
        return updated;
    }

    private static SymbolSizing ReadSizing(SqliteDataReader r)
    {
        var s = new SymbolSizing
        {
            TerminalId = r.GetString(r.GetOrdinal("terminal_id")),
            Symbol = r.GetString(r.GetOrdinal("symbol")),
            Enabled = r.GetInt32(r.GetOrdinal("enabled")) != 0,
            RiskFactor = TryGetDouble(r, "risk_factor") ?? r.GetDouble(r.GetOrdinal("risk_pct")),
            MaxLot = r.IsDBNull(r.GetOrdinal("max_lot")) ? null : r.GetDouble(r.GetOrdinal("max_lot")),
            MarginInitial = r.IsDBNull(r.GetOrdinal("margin_initial")) ? null : r.GetDouble(r.GetOrdinal("margin_initial")),
            AssetClass = r.GetString(r.GetOrdinal("asset_class")),
            Notes = r.IsDBNull(r.GetOrdinal("notes")) ? null : r.GetString(r.GetOrdinal("notes")),
        };
        // Migration-safe tier read
        try { var o = r.GetOrdinal("tier"); s.Tier = r.IsDBNull(o) ? "T1" : r.GetString(o); } catch { s.Tier = "T1"; }
        return s;
    }

    // ===================================================================
    // Global Pause State
    // ===================================================================

    /// <summary>Get a daemon_state value by key.</summary>
    public string? GetDaemonState(string key)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT value FROM daemon_state WHERE key = @k";
        cmd.Parameters.AddWithValue("@k", key);
        var result = cmd.ExecuteScalar();
        return result == DBNull.Value || result == null ? null : result.ToString();
    }

    /// <summary>Set a daemon_state value by key.</summary>
    public void SetDaemonState(string key, string? value)
    {
        using var conn = Open();
        if (value == null)
        {
            Exec(conn, "DELETE FROM daemon_state WHERE key = @k", ("@k", key));
        }
        else
        {
            Exec(conn, @"INSERT INTO daemon_state (key, value) VALUES (@k, @v)
                         ON CONFLICT(key) DO UPDATE SET value = @v",
                ("@k", key), ("@v", value));
        }
    }

    /// <summary>Load global pause state from DB. Returns (paused, pauseUntilUtc, reason).</summary>
    public (bool Paused, DateTime? Until, string? Reason) LoadPauseState()
    {
        var paused = GetDaemonState("global_pause") == "1";
        var untilStr = GetDaemonState("global_pause_until");
        var reason = GetDaemonState("global_pause_reason");
        DateTime? until = null;
        if (!string.IsNullOrEmpty(untilStr) && DateTime.TryParse(untilStr, null,
                System.Globalization.DateTimeStyles.RoundtripKind, out var dt))
            until = dt;
        return (paused, until, reason);
    }

    /// <summary>Save global pause state to DB.</summary>
    public void SavePauseState(bool paused, DateTime? until = null, string? reason = null)
    {
        SetDaemonState("global_pause", paused ? "1" : "0");
        SetDaemonState("global_pause_until", until?.ToString("o"));
        SetDaemonState("global_pause_reason", reason);
    }

}
