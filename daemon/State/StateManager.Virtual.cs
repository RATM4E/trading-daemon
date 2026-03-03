using Microsoft.Data.Sqlite;

namespace Daemon.Engine;

public partial class StateManager
{
    // ===================================================================
    // Virtual Balance & Margin
    // ===================================================================

    /// <summary>Get virtual balance for a terminal (null = not initialized).</summary>
    public double? GetVirtualBalance(string terminalId)
    {
        var profile = GetProfile(terminalId);
        return profile?.VirtualBalance;
    }

    /// <summary>Initialize virtual balance (called on first virtual order).</summary>
    public void InitVirtualBalance(string terminalId, double startBalance)
    {
        using var conn = Open();
        Exec(conn, @"UPDATE terminal_profiles SET virtual_balance = @bal WHERE terminal_id = @tid",
            ("@tid", terminalId), ("@bal", startBalance));
    }

    /// <summary>Add realized P&L to virtual balance.</summary>
    public void UpdateVirtualBalance(string terminalId, double pnlDelta)
    {
        using var conn = Open();
        Exec(conn, @"UPDATE terminal_profiles SET virtual_balance = virtual_balance + @delta
                     WHERE terminal_id = @tid AND virtual_balance IS NOT NULL",
            ("@tid", terminalId), ("@delta", pnlDelta));
    }

    /// <summary>Add or release virtual margin. Positive = position opened, negative = closed.</summary>
    public void AddVirtualMargin(string terminalId, double delta)
    {
        using var conn = Open();
        Exec(conn, @"UPDATE terminal_profiles SET virtual_margin = MAX(0, virtual_margin + @delta)
                     WHERE terminal_id = @tid",
            ("@tid", terminalId), ("@delta", delta));
    }

    /// <summary>Get current virtual margin.</summary>
    public double GetVirtualMargin(string terminalId)
    {
        var profile = GetProfile(terminalId);
        return profile?.VirtualMargin ?? 0;
    }

    /// <summary>Set virtual margin directly (e.g. reset to 0 on mode change).</summary>
    public void SetVirtualMargin(string terminalId, double value)
    {
        using var conn = Open();
        Exec(conn, @"UPDATE terminal_profiles SET virtual_margin = @val WHERE terminal_id = @tid",
            ("@tid", terminalId), ("@val", value));
    }

    /// <summary>Reset virtual trading: close all positions, reset balance, clear equity history.</summary>
    public void ResetVirtualTrading(string terminalId, double newBalance)
    {
        using var conn = Open();
        using var tx = conn.BeginTransaction();

        // Close all open virtual positions
        using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = @"UPDATE positions SET closed_at = @closedAt,
                                close_reason = 'reset', pnl = 0
                                WHERE terminal_id = @tid AND is_virtual = 1 AND closed_at IS NULL";
            cmd.Parameters.AddWithValue("@tid", terminalId);
            cmd.Parameters.AddWithValue("@closedAt", DateTime.UtcNow.ToString("o"));
            cmd.ExecuteNonQuery();
        }

        // Reset balance and margin
        using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = @"UPDATE terminal_profiles SET virtual_balance = @bal, virtual_margin = 0
                                WHERE terminal_id = @tid";
            cmd.Parameters.AddWithValue("@tid", terminalId);
            cmd.Parameters.AddWithValue("@bal", newBalance);
            cmd.ExecuteNonQuery();
        }

        // Clear equity history
        using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = "DELETE FROM virtual_equity WHERE terminal_id = @tid";
            cmd.Parameters.AddWithValue("@tid", terminalId);
            cmd.ExecuteNonQuery();
        }

        // Delete ALL closed virtual positions (stats come from these — must be purged)
        using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = @"DELETE FROM positions
                                WHERE terminal_id = @tid AND is_virtual = 1 AND closed_at IS NOT NULL";
            cmd.Parameters.AddWithValue("@tid", terminalId);
            cmd.ExecuteNonQuery();
        }

        // Delete trade snapshots for virtual trades
        using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = "DELETE FROM trade_snapshots WHERE terminal_id = @tid";
            cmd.Parameters.AddWithValue("@tid", terminalId);
            cmd.ExecuteNonQuery();
        }

        // Clear daily P&L (virtual trades contributed to these)
        using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = "DELETE FROM daily_pnl WHERE terminal_id = @tid";
            cmd.Parameters.AddWithValue("@tid", terminalId);
            cmd.ExecuteNonQuery();
        }

        // Clear daily R-cap accumulators (virtual trades contributed to these)
        using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = "DELETE FROM daily_r WHERE terminal_id = @tid";
            cmd.Parameters.AddWithValue("@tid", terminalId);
            cmd.ExecuteNonQuery();
        }

        // Reset 3SL guard state (virtual SL hits contributed to these)
        using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = "DELETE FROM sl3_state WHERE terminal_id = @tid";
            cmd.Parameters.AddWithValue("@tid", terminalId);
            cmd.ExecuteNonQuery();
        }

        tx.Commit();
    }

    /// <summary>Reset blocking flags only (daily R-cap, 3SL guard, daily P&L).
    /// Does NOT touch trade history, positions, or equity data.
    /// Safe for real accounts.</summary>
    public void ResetFlags(string terminalId)
    {
        using var conn = Open();
        using var tx = conn.BeginTransaction();

        // Reset daily R-cap accumulators
        using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = "DELETE FROM daily_r WHERE terminal_id = @tid";
            cmd.Parameters.AddWithValue("@tid", terminalId);
            cmd.ExecuteNonQuery();
        }

        // Reset 3SL guard state
        using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = "DELETE FROM sl3_state WHERE terminal_id = @tid";
            cmd.Parameters.AddWithValue("@tid", terminalId);
            cmd.ExecuteNonQuery();
        }

        // Reset daily P&L counters
        using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = "DELETE FROM daily_pnl WHERE terminal_id = @tid";
            cmd.Parameters.AddWithValue("@tid", terminalId);
            cmd.ExecuteNonQuery();
        }

        tx.Commit();
    }

    /// <summary>Reset blocking flags only (for real accounts): daily_r, sl3_state, daily_pnl.
    /// Does NOT touch trade history, positions, equity, or snapshots.</summary>
    public void ResetBlockingFlags(string terminalId)
    {
        using var conn = Open();
        using var tx = conn.BeginTransaction();

        using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = "DELETE FROM daily_r WHERE terminal_id = @tid";
            cmd.Parameters.AddWithValue("@tid", terminalId);
            cmd.ExecuteNonQuery();
        }

        using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = "DELETE FROM sl3_state WHERE terminal_id = @tid";
            cmd.Parameters.AddWithValue("@tid", terminalId);
            cmd.ExecuteNonQuery();
        }

        using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = "DELETE FROM daily_pnl WHERE terminal_id = @tid";
            cmd.Parameters.AddWithValue("@tid", terminalId);
            cmd.ExecuteNonQuery();
        }

        tx.Commit();
    }

    // ===================================================================
    // Virtual Equity Snapshots
    // ===================================================================

    /// <summary>Save an equity snapshot (called every 5 minutes for virtual terminals).</summary>
    public void SaveVirtualEquitySnapshot(string terminalId, double equity, double balance,
                                           double unrealizedPnl, int openPositions)
    {
        using var conn = Open();
        Exec(conn, @"
            INSERT INTO virtual_equity (terminal_id, equity, balance, unrealized_pnl, open_positions)
            VALUES (@tid, @eq, @bal, @upnl, @cnt)",
            ("@tid", terminalId), ("@eq", equity), ("@bal", balance),
            ("@upnl", unrealizedPnl), ("@cnt", openPositions));
    }

    /// <summary>Get equity history for charting.</summary>
    public List<VirtualEquityPoint> GetVirtualEquityHistory(string terminalId, int limit = 2000)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT timestamp, equity, balance, unrealized_pnl, open_positions
            FROM virtual_equity WHERE terminal_id = @tid
            ORDER BY id DESC LIMIT @limit";
        cmd.Parameters.AddWithValue("@tid", terminalId);
        cmd.Parameters.AddWithValue("@limit", limit);

        var result = new List<VirtualEquityPoint>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            result.Add(new VirtualEquityPoint
            {
                Timestamp = r.GetString(0),
                Equity = r.GetDouble(1),
                Balance = r.GetDouble(2),
                UnrealizedPnl = r.GetDouble(3),
                OpenPositions = r.GetInt32(4)
            });
        }
        result.Reverse(); // chronological order
        return result;
    }

    // ===================================================================
    // SL History (universal: virtual + real)
    // ===================================================================

    /// <summary>Save an SL change for Trade Chart trail visualization.</summary>
    public void SaveSlHistory(long ticket, string terminalId, double oldSl, double newSl, long barTime)
    {
        using var conn = Open();
        Exec(conn, @"
            INSERT INTO sl_history (ticket, terminal_id, old_sl, new_sl, bar_time)
            VALUES (@t, @tid, @old, @new, @bt)",
            ("@t", ticket), ("@tid", terminalId),
            ("@old", oldSl), ("@new", newSl), ("@bt", barTime));
    }

    /// <summary>Get SL history for a position.</summary>
    public List<SlHistoryEntry> GetSlHistory(long ticket, string terminalId)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"SELECT old_sl, new_sl, bar_time, changed_at
                            FROM sl_history WHERE ticket = @t AND terminal_id = @tid
                            ORDER BY id ASC";
        cmd.Parameters.AddWithValue("@t", ticket);
        cmd.Parameters.AddWithValue("@tid", terminalId);

        var result = new List<SlHistoryEntry>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            result.Add(new SlHistoryEntry
            {
                OldSl = r.GetDouble(0),
                NewSl = r.GetDouble(1),
                BarTime = r.IsDBNull(2) ? 0 : r.GetInt64(2),
                ChangedAt = r.GetString(3)
            });
        }
        return result;
    }

    // ===================================================================
    // Trade Snapshots
    // ===================================================================

    /// <summary>Save a trade snapshot JSON (bars + SL history + trade info).</summary>
    public void SaveTradeSnapshot(long ticket, string terminalId, string snapshotJson)
    {
        using var conn = Open();
        Exec(conn, @"
            INSERT INTO trade_snapshots (ticket, terminal_id, snapshot)
            VALUES (@t, @tid, @snap)
            ON CONFLICT(ticket, terminal_id) DO UPDATE SET snapshot = @snap",
            ("@t", ticket), ("@tid", terminalId), ("@snap", snapshotJson));
    }

    /// <summary>Get a trade snapshot.</summary>
    public string? GetTradeSnapshot(long ticket, string terminalId)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT snapshot FROM trade_snapshots WHERE ticket = @t AND terminal_id = @tid";
        cmd.Parameters.AddWithValue("@t", ticket);
        cmd.Parameters.AddWithValue("@tid", terminalId);
        return cmd.ExecuteScalar() as string;
    }

    // ===================================================================
    // Virtual Statistics
    // ===================================================================

    /// <summary>Get virtual trading statistics for a terminal.</summary>
    public VirtualStats GetVirtualStats(string terminalId)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT
                COUNT(*) as total,
                SUM(CASE WHEN pnl > 0 THEN 1 ELSE 0 END) as wins,
                SUM(CASE WHEN pnl <= 0 THEN 1 ELSE 0 END) as losses,
                AVG(CASE WHEN pnl > 0 THEN pnl END) as avg_win,
                AVG(CASE WHEN pnl <= 0 THEN pnl END) as avg_loss,
                SUM(CASE WHEN pnl > 0 THEN pnl ELSE 0 END) as gross_profit,
                SUM(CASE WHEN pnl < 0 THEN ABS(pnl) ELSE 0 END) as gross_loss,
                SUM(pnl) as net_pnl,
                MIN(pnl) as worst_trade,
                MAX(pnl) as best_trade
            FROM positions
            WHERE terminal_id = @tid AND is_virtual = 1 AND closed_at IS NOT NULL";
        cmd.Parameters.AddWithValue("@tid", terminalId);

        using var r = cmd.ExecuteReader();
        if (!r.Read()) return new VirtualStats();

        int total = r.IsDBNull(0) ? 0 : r.GetInt32(0);
        int wins = r.IsDBNull(1) ? 0 : r.GetInt32(1);
        int losses = r.IsDBNull(2) ? 0 : r.GetInt32(2);
        double avgWin = r.IsDBNull(3) ? 0 : r.GetDouble(3);
        double avgLoss = r.IsDBNull(4) ? 0 : r.GetDouble(4);
        double grossProfit = r.IsDBNull(5) ? 0 : r.GetDouble(5);
        double grossLoss = r.IsDBNull(6) ? 0 : r.GetDouble(6);
        double netPnl = r.IsDBNull(7) ? 0 : r.GetDouble(7);

        // Max drawdown from equity history
        // First, get initial virtual balance as reference for sanity filtering
        var profile = GetProfile(terminalId);
        double initBalance = profile != null ? (GetVirtualBalance(terminalId) ?? 0) : 0;
        // Use max of current balance and 2× initial as upper bound for valid equity
        // (catches snapshots corrupted by old contractSize fallback bug)
        double maxReasonableEquity = Math.Max(initBalance, 100_000) * 3;

        double maxDD = 0;
        using var cmd2 = conn.CreateCommand();
        cmd2.CommandText = @"SELECT equity FROM virtual_equity WHERE terminal_id = @tid ORDER BY id ASC";
        cmd2.Parameters.AddWithValue("@tid", terminalId);
        using var r2 = cmd2.ExecuteReader();
        double peak = 0;
        while (r2.Read())
        {
            double eq = r2.GetDouble(0);
            // Skip obviously corrupted snapshots
            if (eq > maxReasonableEquity || eq < -maxReasonableEquity) continue;
            if (eq > peak) peak = eq;
            double dd = peak - eq;
            if (dd > maxDD) maxDD = dd;
        }

        return new VirtualStats
        {
            TotalTrades = total,
            Wins = wins,
            Losses = losses,
            WinRate = total > 0 ? (double)wins / total * 100 : 0,
            AvgWin = avgWin,
            AvgLoss = avgLoss,
            ProfitFactor = grossLoss > 0 ? grossProfit / grossLoss : grossProfit > 0 ? 999 : 0,
            NetPnl = netPnl,
            MaxDrawdown = maxDD,
            MaxDrawdownPct = peak > 0 ? maxDD / peak * 100 : 0,
            Expectancy = total > 0 ? netPnl / total : 0
        };
    }

    /// <summary>Export closed virtual trades as CSV rows.</summary>
    public List<Dictionary<string, object>> ExportVirtualTrades(string terminalId)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT ticket, symbol, direction, volume, price_open, close_price, sl, tp,
                   pnl, close_reason, source, opened_at, closed_at
            FROM positions
            WHERE terminal_id = @tid AND is_virtual = 1 AND closed_at IS NOT NULL
            ORDER BY closed_at ASC";
        cmd.Parameters.AddWithValue("@tid", terminalId);

        var result = new List<Dictionary<string, object>>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            result.Add(new Dictionary<string, object>
            {
                ["ticket"] = r.GetInt64(0),
                ["symbol"] = r.GetString(1),
                ["direction"] = r.GetString(2),
                ["volume"] = r.GetDouble(3),
                ["price_open"] = r.GetDouble(4),
                ["close_price"] = r.IsDBNull(5) ? 0 : r.GetDouble(5),
                ["sl"] = r.GetDouble(6),
                ["tp"] = r.GetDouble(7),
                ["pnl"] = r.IsDBNull(8) ? 0 : r.GetDouble(8),
                ["close_reason"] = r.IsDBNull(9) ? "" : r.GetString(9),
                ["strategy"] = r.GetString(10),
                ["opened_at"] = r.GetString(11),
                ["closed_at"] = r.IsDBNull(12) ? "" : r.GetString(12)
            });
        }
        return result;
    }


}
