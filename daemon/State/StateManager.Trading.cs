using Microsoft.Data.Sqlite;

namespace Daemon.Engine;

public partial class StateManager
{
    // ===================================================================
    // 3SL Guard
    // ===================================================================

    public void EnsureSl3State(string terminalId)
    {
        using var conn = Open();
        Exec(conn, @"INSERT OR IGNORE INTO sl3_state (terminal_id) VALUES (@tid)",
            ("@tid", terminalId));
    }

    public (int Count, bool Blocked) Get3SLState(string terminalId)
    {
        EnsureSl3State(terminalId);
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT consecutive_sl, blocked FROM sl3_state WHERE terminal_id = @tid";
        cmd.Parameters.AddWithValue("@tid", terminalId);
        using var r = cmd.ExecuteReader();
        if (!r.Read()) return (0, false);
        return (r.GetInt32(0), r.GetInt32(1) == 1);
    }

    public void IncrementSLCount(string terminalId)
    {
        EnsureSl3State(terminalId);
        using var conn = Open();
        Exec(conn, @"
            UPDATE sl3_state SET consecutive_sl = consecutive_sl + 1,
                                 last_sl_at = datetime('now')
            WHERE terminal_id = @tid",
            ("@tid", terminalId));
    }

    public void ResetSLCount(string terminalId)
    {
        EnsureSl3State(terminalId);
        using var conn = Open();
        Exec(conn, "UPDATE sl3_state SET consecutive_sl = 0 WHERE terminal_id = @tid",
            ("@tid", terminalId));
    }

    public void Block3SL(string terminalId)
    {
        EnsureSl3State(terminalId);
        using var conn = Open();
        Exec(conn, @"UPDATE sl3_state SET blocked = 1, blocked_at = datetime('now')
                     WHERE terminal_id = @tid", ("@tid", terminalId));
    }

    public void Unblock3SL(string terminalId)
    {
        using var conn = Open();
        Exec(conn, @"UPDATE sl3_state SET blocked = 0, consecutive_sl = 0,
                     blocked_at = NULL WHERE terminal_id = @tid", ("@tid", terminalId));
    }

    // ===================================================================
    // Daily P/L
    // ===================================================================

    public void AddRealizedPnl(string terminalId, string date, double amount)
    {
        using var conn = Open();
        Exec(conn, @"
            INSERT INTO daily_pnl (terminal_id, date, realized_pnl)
            VALUES (@tid, @date, @amt)
            ON CONFLICT(terminal_id, date) DO UPDATE SET
                realized_pnl = realized_pnl + @amt",
            ("@tid", terminalId), ("@date", date), ("@amt", amount));
    }

    public (double Realized, double HWM, double DdSnapshot) GetDailyPnl(string terminalId, string date)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT realized_pnl, high_water_mark, dd_snapshot FROM daily_pnl WHERE terminal_id = @tid AND date = @d";
        cmd.Parameters.AddWithValue("@tid", terminalId);
        cmd.Parameters.AddWithValue("@d", date);
        using var r = cmd.ExecuteReader();
        if (!r.Read()) return (0, 0, 0);
        double snapshot = 0;
        try { snapshot = r.GetDouble(2); } catch { }
        return (r.GetDouble(0), r.GetDouble(1), snapshot);
    }

    public void SetDailyDdSnapshot(string terminalId, string date, double snapshot)
    {
        using var conn = Open();
        Exec(conn, @"
            INSERT INTO daily_pnl (terminal_id, date, dd_snapshot)
            VALUES (@tid, @date, @snap)
            ON CONFLICT(terminal_id, date) DO UPDATE SET
                dd_snapshot = @snap",
            ("@tid", terminalId), ("@date", date), ("@snap", snapshot));
    }

    public void UpdateHWM(string terminalId, string date, double equity)
    {
        using var conn = Open();
        Exec(conn, @"
            INSERT INTO daily_pnl (terminal_id, date, high_water_mark)
            VALUES (@tid, @date, @eq)
            ON CONFLICT(terminal_id, date) DO UPDATE SET
                high_water_mark = MAX(high_water_mark, @eq)",
            ("@tid", terminalId), ("@date", date), ("@eq", equity));
    }

    // ===================================================================
    // R-cap (Phase 9.R)
    // ===================================================================

    /// <summary>Mark a position as having protector SL modification applied.</summary>
    public void MarkProtectorFired(long ticket, string terminalId)
    {
        using var conn = Open();
        Exec(conn, @"
            UPDATE positions SET protector_fired = 1
            WHERE ticket = @ticket AND terminal_id = @tid AND closed_at IS NULL",
            ("@ticket", ticket), ("@tid", terminalId));
    }

    /// <summary>
    /// Add an R-result to the daily accumulator for a strategy.
    /// Called when a position closes (SL/TP/protector).
    /// </summary>
    public void AddDailyR(string terminalId, string strategy, string date, double rValue)
    {
        using var conn = Open();
        Exec(conn, @"
            INSERT INTO daily_r (terminal_id, strategy, date, r_sum, trade_count)
            VALUES (@tid, @strat, @date, @r, 1)
            ON CONFLICT(terminal_id, strategy, date) DO UPDATE SET
                r_sum = r_sum + @r,
                trade_count = trade_count + 1",
            ("@tid", terminalId), ("@strat", strategy), ("@date", date), ("@r", rValue));
    }

    /// <summary>
    /// Get current daily R-sum for a strategy on a terminal.
    /// Returns (r_sum, trade_count). Defaults to (0, 0) if no trades today.
    /// </summary>
    public (double RSum, int TradeCount) GetDailyR(string terminalId, string strategy, string date)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT r_sum, trade_count FROM daily_r WHERE terminal_id = @tid AND strategy = @strat AND date = @d";
        cmd.Parameters.AddWithValue("@tid", terminalId);
        cmd.Parameters.AddWithValue("@strat", strategy);
        cmd.Parameters.AddWithValue("@d", date);
        using var r = cmd.ExecuteReader();
        if (!r.Read()) return (0, 0);
        return (r.GetDouble(0), r.GetInt32(1));
    }

    /// <summary>Get daily R-sum for ALL strategies on a terminal for a given date.</summary>
    public List<(string Strategy, double RSum, int TradeCount)> GetDailyRAll(string terminalId, string date)
    {
        var result = new List<(string, double, int)>();
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT strategy, r_sum, trade_count FROM daily_r WHERE terminal_id = @tid AND date = @d";
        cmd.Parameters.AddWithValue("@tid", terminalId);
        cmd.Parameters.AddWithValue("@d", date);
        using var r = cmd.ExecuteReader();
        while (r.Read())
            result.Add((r.GetString(0), r.GetDouble(1), r.GetInt32(2)));
        return result;
    }


}
