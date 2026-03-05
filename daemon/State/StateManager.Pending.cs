using Microsoft.Data.Sqlite;

namespace Daemon.Engine;

/// <summary>Record representing one pending (stop/limit) order.</summary>
public class PendingOrderRecord
{
    public long   Ticket        { get; set; }
    public string TerminalId    { get; set; } = "";
    public string Symbol        { get; set; } = "";
    public string Strategy      { get; set; } = "";
    public int    Magic         { get; set; }
    public string Direction     { get; set; } = "";  // "BUY" / "SELL"
    public string OrderType     { get; set; } = "";  // "BUY_STOP" / "SELL_STOP"
    public double Volume        { get; set; }
    public double EntryPrice    { get; set; }
    public double SL            { get; set; }
    public double TP            { get; set; }
    public int    BarsRemaining { get; set; }        // 0 = GTC
    public string? SignalData   { get; set; }
    public bool   IsVirtual     { get; set; }
    public string PlacedAt      { get; set; } = "";
    public string Status        { get; set; } = "open";
}

public partial class StateManager
{
    // ===================================================================
    // Pending Orders
    // ===================================================================

    /// <summary>
    /// Save a new pending order record (status = 'open').
    /// BarsRemaining convention: -1 = GTC (no expiry), >0 = countdown bars.
    /// Set BarsRemaining = ExpiryBars ?? -1 before calling.
    /// </summary>
    public void SavePendingOrder(PendingOrderRecord rec)
    {
        using var conn = Open();
        Exec(conn, @"
            INSERT OR REPLACE INTO pending_orders
                (ticket, terminal_id, symbol, strategy, magic, direction, order_type,
                 volume, entry_price, sl, tp, bars_remaining, signal_data,
                 is_virtual, placed_at, status)
            VALUES
                (@ticket, @tid, @sym, @strat, @magic, @dir, @otype,
                 @vol, @ep, @sl, @tp, @bars, @sd,
                 @virt, @placed, 'open')",
            ("@ticket",  rec.Ticket),
            ("@tid",     rec.TerminalId),
            ("@sym",     rec.Symbol),
            ("@strat",   rec.Strategy),
            ("@magic",   rec.Magic),
            ("@dir",     rec.Direction),
            ("@otype",   rec.OrderType),
            ("@vol",     rec.Volume),
            ("@ep",      rec.EntryPrice),
            ("@sl",      rec.SL),
            ("@tp",      rec.TP),
            ("@bars",    rec.BarsRemaining),
            ("@sd",      (object?)rec.SignalData ?? DBNull.Value),
            ("@virt",    rec.IsVirtual ? 1 : 0),
            ("@placed",  rec.PlacedAt));
    }

    /// <summary>Get all open virtual pending orders across all terminals (for VirtualTracker).</summary>
    public List<PendingOrderRecord> GetOpenVirtualPendingOrders()
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT ticket, terminal_id, symbol, strategy, magic, direction, order_type,
                   volume, entry_price, sl, tp, bars_remaining, signal_data,
                   is_virtual, placed_at, status
            FROM pending_orders
            WHERE status = 'open' AND is_virtual = 1
            ORDER BY placed_at";
        var result = new List<PendingOrderRecord>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
            result.Add(ReadPendingRecord(r));
        return result;
    }

    /// <summary>Get all open pending orders for a terminal.</summary>
    public List<PendingOrderRecord> GetOpenPendingOrders(string terminalId, bool? isVirtual = null)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();

        var filter = isVirtual.HasValue
            ? "AND is_virtual = @virt"
            : "";

        cmd.CommandText = $@"
            SELECT ticket, terminal_id, symbol, strategy, magic, direction, order_type,
                   volume, entry_price, sl, tp, bars_remaining, signal_data,
                   is_virtual, placed_at, status
            FROM pending_orders
            WHERE terminal_id = @tid AND status = 'open' {filter}
            ORDER BY placed_at";

        cmd.Parameters.AddWithValue("@tid", terminalId);
        if (isVirtual.HasValue)
            cmd.Parameters.AddWithValue("@virt", isVirtual.Value ? 1 : 0);

        var result = new List<PendingOrderRecord>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
            result.Add(ReadPendingRecord(r));
        return result;
    }

    /// <summary>Get a single pending order by ticket.</summary>
    public PendingOrderRecord? GetPendingOrder(long ticket, string terminalId)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT ticket, terminal_id, symbol, strategy, magic, direction, order_type,
                   volume, entry_price, sl, tp, bars_remaining, signal_data,
                   is_virtual, placed_at, status
            FROM pending_orders
            WHERE ticket = @ticket AND terminal_id = @tid";
        cmd.Parameters.AddWithValue("@ticket", ticket);
        cmd.Parameters.AddWithValue("@tid", terminalId);
        using var r = cmd.ExecuteReader();
        return r.Read() ? ReadPendingRecord(r) : null;
    }

    /// <summary>
    /// Close a pending order with a final status.
    /// status: 'filled' | 'cancelled' | 'expired' | 'cancelled_external'
    /// </summary>
    public void ClosePendingOrder(long ticket, string terminalId, string status)
    {
        using var conn = Open();
        Exec(conn, @"
            UPDATE pending_orders
            SET status = @status, closed_at = datetime('now')
            WHERE ticket = @ticket AND terminal_id = @tid",
            ("@status", status),
            ("@ticket", ticket),
            ("@tid",    terminalId));
    }

    /// <summary>
    /// Decrement bars_remaining for all open pending orders that have an expiry set.
    /// Returns list of tickets that just expired (bars_remaining reached 0 after decrement).
    /// Orders with bars_remaining = 0 at placement time are GTC and are never decremented.
    /// We distinguish GTC by storing -1 in bars_remaining: 0 = just expired, >0 = counting, -1 = GTC.
    /// Convention: ExpiryBars=null or 0 → stored as -1 (GTC). ExpiryBars>0 → stored as-is.
    /// </summary>
    public List<long> DecrementPendingBars(string terminalId, bool isVirtual)
    {
        using var conn = Open();

        // Decrement only orders with expiry (bars_remaining > 0)
        Exec(conn, @"
            UPDATE pending_orders
            SET bars_remaining = bars_remaining - 1
            WHERE terminal_id = @tid AND status = 'open'
              AND is_virtual = @virt AND bars_remaining > 0",
            ("@tid",  terminalId),
            ("@virt", isVirtual ? 1 : 0));

        // Return tickets that just hit zero (just expired this tick)
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT ticket FROM pending_orders
            WHERE terminal_id = @tid AND status = 'open'
              AND is_virtual = @virt AND bars_remaining = 0";
        cmd.Parameters.AddWithValue("@tid",  terminalId);
        cmd.Parameters.AddWithValue("@virt", isVirtual ? 1 : 0);

        var expired = new List<long>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
            expired.Add(r.GetInt64(0));
        return expired;
    }

    /// <summary>
    /// Find all open pending orders matching the same signal_data group (for OCO cancellation).
    /// Returns tickets of OTHER orders in the same OCO group, excluding the filled one.
    /// </summary>
    public List<long> GetOcoPendingTickets(string terminalId, string signalData, long excludeTicket)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT ticket FROM pending_orders
            WHERE terminal_id = @tid AND status = 'open'
              AND signal_data = @sd AND ticket != @excl";
        cmd.Parameters.AddWithValue("@tid",  terminalId);
        cmd.Parameters.AddWithValue("@sd",   signalData);
        cmd.Parameters.AddWithValue("@excl", excludeTicket);
        var result = new List<long>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
            result.Add(r.GetInt64(0));
        return result;
    }

    // ===================================================================
    // Helpers
    // ===================================================================

    private static PendingOrderRecord ReadPendingRecord(SqliteDataReader r) => new()
    {
        Ticket        = r.GetInt64(0),
        TerminalId    = r.GetString(1),
        Symbol        = r.GetString(2),
        Strategy      = r.GetString(3),
        Magic         = r.GetInt32(4),
        Direction     = r.GetString(5),
        OrderType     = r.GetString(6),
        Volume        = r.GetDouble(7),
        EntryPrice    = r.GetDouble(8),
        SL            = r.GetDouble(9),
        TP            = r.GetDouble(10),
        BarsRemaining = r.GetInt32(11),
        SignalData    = r.IsDBNull(12) ? null : r.GetString(12),
        IsVirtual     = r.GetInt32(13) == 1,
        PlacedAt      = r.GetString(14),
        Status        = r.GetString(15),
    };
}
