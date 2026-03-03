using Microsoft.Data.Sqlite;

namespace Daemon.Engine;

public partial class StateManager
{
    // ===================================================================
    // Positions
    // ===================================================================

    public void SavePosition(PositionRecord pos)
    {
        using var conn = Open();
        Exec(conn, @"
            INSERT INTO positions (ticket, terminal_id, symbol, direction, volume, price_open,
                                   sl, tp, magic, source, signal_data, opened_at, is_virtual, timeframe)
            VALUES (@ticket, @tid, @symbol, @dir, @vol, @price,
                    @sl, @tp, @magic, @source, @signal, @opened, @virt, @tf)
            ON CONFLICT(ticket, terminal_id) DO UPDATE SET
                sl = @sl, tp = @tp, volume = @vol, signal_data = @signal",
            ("@ticket", pos.Ticket), ("@tid", pos.TerminalId), ("@symbol", pos.Symbol),
            ("@dir", pos.Direction), ("@vol", pos.Volume), ("@price", pos.PriceOpen),
            ("@sl", pos.SL), ("@tp", pos.TP), ("@magic", pos.Magic),
            ("@source", pos.Source), ("@signal", pos.SignalData ?? (object)DBNull.Value),
            ("@opened", pos.OpenedAt),
            ("@virt", pos.IsVirtual ? 1 : 0),
            ("@tf", (object?)pos.Timeframe ?? DBNull.Value));
    }

    public void ClosePosition(long ticket, string terminalId, double closePrice,
                               string closeReason, double pnl)
    {
        using var conn = Open();
        Exec(conn, @"
            UPDATE positions SET closed_at = @closedAt, close_price = @price,
                                 close_reason = @reason, pnl = @pnl
            WHERE ticket = @ticket AND terminal_id = @tid AND closed_at IS NULL",
            ("@ticket", ticket), ("@tid", terminalId),
            ("@price", closePrice), ("@reason", closeReason), ("@pnl", pnl),
            ("@closedAt", DateTime.UtcNow.ToString("o")));
    }

    public List<PositionRecord> GetOpenPositions(string? terminalId = null)
    {
        using var conn = Open();
        var sql = "SELECT * FROM positions WHERE closed_at IS NULL";
        if (terminalId != null)
            sql += " AND terminal_id = @tid";

        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        if (terminalId != null)
            cmd.Parameters.AddWithValue("@tid", terminalId);

        var result = new List<PositionRecord>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
            result.Add(ReadPosition(r));
        return result;
    }

    public PositionRecord? GetPositionByTicket(long ticket, string terminalId)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM positions WHERE ticket = @t AND terminal_id = @tid";
        cmd.Parameters.AddWithValue("@t", ticket);
        cmd.Parameters.AddWithValue("@tid", terminalId);
        using var r = cmd.ExecuteReader();
        return r.Read() ? ReadPosition(r) : null;
    }

    /// <summary>Get all open virtual positions, optionally filtered by terminal.</summary>
    public List<PositionRecord> GetOpenVirtualPositions(string? terminalId = null)
    {
        using var conn = Open();
        var sql = "SELECT * FROM positions WHERE closed_at IS NULL AND is_virtual = 1";
        if (terminalId != null)
            sql += " AND terminal_id = @tid";

        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        if (terminalId != null)
            cmd.Parameters.AddWithValue("@tid", terminalId);

        var result = new List<PositionRecord>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
            result.Add(ReadPosition(r));
        return result;
    }

    private static PositionRecord ReadPosition(SqliteDataReader r)
    {
        var pos = new PositionRecord
        {
            Ticket = r.GetInt64(r.GetOrdinal("ticket")),
            TerminalId = r.GetString(r.GetOrdinal("terminal_id")),
            Symbol = r.GetString(r.GetOrdinal("symbol")),
            Direction = r.GetString(r.GetOrdinal("direction")),
            Volume = r.GetDouble(r.GetOrdinal("volume")),
            PriceOpen = r.GetDouble(r.GetOrdinal("price_open")),
            SL = r.GetDouble(r.GetOrdinal("sl")),
            TP = r.GetDouble(r.GetOrdinal("tp")),
            Magic = r.GetInt32(r.GetOrdinal("magic")),
            Source = r.GetString(r.GetOrdinal("source")),
            SignalData = r.IsDBNull(r.GetOrdinal("signal_data")) ? null : r.GetString(r.GetOrdinal("signal_data")),
            OpenedAt = r.GetString(r.GetOrdinal("opened_at")),
            ClosedAt = r.IsDBNull(r.GetOrdinal("closed_at")) ? null : r.GetString(r.GetOrdinal("closed_at")),
            ClosePrice = r.IsDBNull(r.GetOrdinal("close_price")) ? null : r.GetDouble(r.GetOrdinal("close_price")),
            CloseReason = r.IsDBNull(r.GetOrdinal("close_reason")) ? null : r.GetString(r.GetOrdinal("close_reason")),
            PnL = r.IsDBNull(r.GetOrdinal("pnl")) ? null : r.GetDouble(r.GetOrdinal("pnl")),
        };
        // Virtual trading fields (safe read for pre-migration DBs)
        try
        {
            var virtOrd = r.GetOrdinal("is_virtual");
            pos.IsVirtual = !r.IsDBNull(virtOrd) && r.GetInt32(virtOrd) == 1;
        }
        catch { pos.IsVirtual = false; }
        try
        {
            var tfOrd = r.GetOrdinal("timeframe");
            pos.Timeframe = r.IsDBNull(tfOrd) ? null : r.GetString(tfOrd);
        }
        catch { pos.Timeframe = null; }
        try
        {
            var pfOrd = r.GetOrdinal("protector_fired");
            pos.ProtectorFired = !r.IsDBNull(pfOrd) && r.GetInt32(pfOrd) == 1;
        }
        catch { pos.ProtectorFired = false; }
        return pos;
    }

}
