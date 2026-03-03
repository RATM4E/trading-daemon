using Microsoft.Data.Sqlite;

namespace Daemon.Engine;

public partial class StateManager
{
    // ===================================================================
    // Terminal Profiles
    // ===================================================================

    public void SaveProfile(TerminalProfile p)
    {
        using var conn = Open();
        Exec(conn, @"
            INSERT INTO terminal_profiles
                (terminal_id, type, account_type, mode, server_timezone,
                 daily_dd_limit, daily_dd_mode, daily_dd_percent, cum_dd_limit, max_risk_trade, risk_type,
                 max_margin_trade, margin_trade_mode, max_deposit_load,
                 news_guard_on, news_window_min, news_min_impact, news_be_enabled, news_include_usd,
                 sl3_guard_on, volume_mode, no_trade_start, no_trade_end, no_trade_on,
                 virtual_balance, virtual_margin, commission_per_lot,
                 r_cap_on, r_cap_limit, updated_at)
            VALUES
                (@id, @type, @at, @mode, @tz,
                 @ddl, @ddm, @ddp, @cdl, @mrt, @rt,
                 @mmt, @mtm, @mdl,
                 @ng, @nw, @ni, @nbe, @nusd,
                 @s3, @vm, @nts, @nte, @nto,
                 @vbal, @vmar, @cpl,
                 @rco, @rcl, datetime('now'))
            ON CONFLICT(terminal_id) DO UPDATE SET
                type=@type, account_type=@at, mode=@mode, server_timezone=@tz,
                daily_dd_limit=@ddl, daily_dd_mode=@ddm, daily_dd_percent=@ddp, cum_dd_limit=@cdl, max_risk_trade=@mrt, risk_type=@rt,
                max_margin_trade=@mmt, margin_trade_mode=@mtm, max_deposit_load=@mdl,
                news_guard_on=@ng, news_window_min=@nw, news_min_impact=@ni,
                news_be_enabled=@nbe, news_include_usd=@nusd,
                sl3_guard_on=@s3, volume_mode=@vm,
                no_trade_start=@nts, no_trade_end=@nte, no_trade_on=@nto,
                virtual_balance=@vbal, virtual_margin=@vmar, commission_per_lot=@cpl,
                r_cap_on=@rco, r_cap_limit=@rcl,
                updated_at=datetime('now')",
            ("@id", p.TerminalId), ("@type", p.Type), ("@at", p.AccountType),
            ("@mode", p.Mode), ("@tz", p.ServerTimezone),
            ("@ddl", p.DailyDDLimit), ("@ddm", p.DailyDdMode), ("@ddp", p.DailyDdPercent), ("@cdl", p.CumDDLimit),
            ("@mrt", p.MaxRiskTrade), ("@rt", p.RiskType),
            ("@mmt", p.MaxMarginTrade), ("@mtm", p.MarginTradeMode), ("@mdl", p.MaxDepositLoad),
            ("@ng", p.NewsGuardOn ? 1 : 0), ("@nw", p.NewsWindowMin),
            ("@ni", p.NewsMinImpact), ("@nbe", p.NewsBeEnabled ? 1 : 0),
            ("@nusd", p.NewsIncludeUsd ? 1 : 0),
            ("@s3", p.Sl3GuardOn ? 1 : 0), ("@vm", p.VolumeMode),
            ("@nts", (object?)p.NoTradeStart ?? DBNull.Value),
            ("@nte", (object?)p.NoTradeEnd ?? DBNull.Value),
            ("@nto", p.NoTradeOn ? 1 : 0),
            ("@vbal", p.VirtualBalance.HasValue ? (object)p.VirtualBalance.Value : DBNull.Value),
            ("@vmar", p.VirtualMargin),
            ("@cpl", p.CommissionPerLot),
            ("@rco", p.RCapOn ? 1 : 0),
            ("@rcl", p.RCapLimit));
    }

    public TerminalProfile? GetProfile(string terminalId)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM terminal_profiles WHERE terminal_id = @id";
        cmd.Parameters.AddWithValue("@id", terminalId);
        using var r = cmd.ExecuteReader();
        return r.Read() ? ReadProfile(r) : null;
    }

    public List<TerminalProfile> GetAllProfiles()
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM terminal_profiles";
        var result = new List<TerminalProfile>();
        using var r = cmd.ExecuteReader();
        while (r.Read()) result.Add(ReadProfile(r));
        return result;
    }

    private static TerminalProfile ReadProfile(SqliteDataReader r)
    {
        var p = new TerminalProfile
        {
            TerminalId = r.GetString(r.GetOrdinal("terminal_id")),
            Type = r.GetString(r.GetOrdinal("type")),
            AccountType = r.GetString(r.GetOrdinal("account_type")),
            Mode = r.GetString(r.GetOrdinal("mode")),
            ServerTimezone = r.GetString(r.GetOrdinal("server_timezone")),
            DailyDDLimit = r.GetDouble(r.GetOrdinal("daily_dd_limit")),
            CumDDLimit = r.GetDouble(r.GetOrdinal("cum_dd_limit")),
            MaxRiskTrade = r.GetDouble(r.GetOrdinal("max_risk_trade")),
            RiskType = r.GetString(r.GetOrdinal("risk_type")),
            MaxMarginTrade = r.GetDouble(r.GetOrdinal("max_margin_trade")),
            MaxDepositLoad = r.GetDouble(r.GetOrdinal("max_deposit_load")),
            NewsGuardOn = r.GetInt32(r.GetOrdinal("news_guard_on")) == 1,
            NewsWindowMin = r.GetInt32(r.GetOrdinal("news_window_min")),
            NewsMinImpact = r.GetInt32(r.GetOrdinal("news_min_impact")),
            NewsBeEnabled = r.GetInt32(r.GetOrdinal("news_be_enabled")) == 1,
            NewsIncludeUsd = r.GetInt32(r.GetOrdinal("news_include_usd")) == 1,
            Sl3GuardOn = r.GetInt32(r.GetOrdinal("sl3_guard_on")) == 1,
            VolumeMode = r.GetString(r.GetOrdinal("volume_mode")),
        };
        // Nullable/migration-safe fields
        try { var o = r.GetOrdinal("no_trade_start"); p.NoTradeStart = r.IsDBNull(o) ? null : r.GetString(o); } catch { }
        try { var o = r.GetOrdinal("no_trade_end"); p.NoTradeEnd = r.IsDBNull(o) ? null : r.GetString(o); } catch { }
        try { var o = r.GetOrdinal("no_trade_on"); p.NoTradeOn = r.IsDBNull(o) || r.GetInt32(o) == 1; } catch { }
        try { var o = r.GetOrdinal("margin_trade_mode"); p.MarginTradeMode = r.IsDBNull(o) ? "block" : r.GetString(o); } catch { }
        try { var o = r.GetOrdinal("daily_dd_mode"); p.DailyDdMode = r.IsDBNull(o) ? "hard" : r.GetString(o); } catch { }
        try { var o = r.GetOrdinal("daily_dd_percent"); p.DailyDdPercent = r.IsDBNull(o) ? 0 : r.GetDouble(o); } catch { }
        // Phase 9.V: Virtual trading fields
        try { var o = r.GetOrdinal("virtual_balance"); p.VirtualBalance = r.IsDBNull(o) ? null : r.GetDouble(o); } catch { }
        try { var o = r.GetOrdinal("virtual_margin"); p.VirtualMargin = r.IsDBNull(o) ? 0 : r.GetDouble(o); } catch { }
        try { var o = r.GetOrdinal("commission_per_lot"); p.CommissionPerLot = r.IsDBNull(o) ? 0 : r.GetDouble(o); } catch { }
        try { var o = r.GetOrdinal("r_cap_on"); p.RCapOn = !r.IsDBNull(o) && r.GetInt32(o) == 1; } catch { }
        try { var o = r.GetOrdinal("r_cap_limit"); p.RCapLimit = r.IsDBNull(o) ? 0 : r.GetDouble(o); } catch { }
        return p;
    }

    // ===================================================================
    // Class Leverage (per asset class per terminal)
    // ===================================================================

    /// <summary>Conservative default leverage per class when MT5 detection unavailable.
    /// Values intentionally low → overestimates margin → gates block earlier = safer.</summary>
    public static readonly Dictionary<string, int> DefaultLeverage = new()
    {
        ["FX"]   = 100,
        ["IDX"]  = 20,
        ["XAU"]  = 10,
        ["OIL"]  = 5,
        ["CRYP"] = 1,
    };

    /// <summary>Map strategy/sizing asset class names to leverage class keys.</summary>
    private static readonly Dictionary<string, string> AssetClassToLeverageClass = new(
        StringComparer.OrdinalIgnoreCase)
    {
        ["forex"]   = "FX",
        ["fx"]      = "FX",
        ["index"]   = "IDX",
        ["idx"]     = "IDX",
        ["metal"]   = "XAU",
        ["xau"]     = "XAU",
        ["mtl"]     = "XAU",
        ["energy"]  = "OIL",
        ["oil"]     = "OIL",
        ["crypto"]  = "CRYP",
        ["cryp"]    = "CRYP",
    };

    /// <summary>Map a sizing/strategy asset class to leverage class key.</summary>
    public static string MapToLeverageClass(string assetClass)
    {
        if (string.IsNullOrEmpty(assetClass)) return "FX";
        return AssetClassToLeverageClass.TryGetValue(assetClass, out var lc) ? lc : "FX";
    }

    /// <summary>Save detected leverage per asset class for a terminal.</summary>
    public void SaveClassLeverage(string terminalId, Dictionary<string, int> leverageByClass)
    {
        using var conn = Open();
        var now = DateTime.UtcNow.ToString("o");
        foreach (var (aclass, leverage) in leverageByClass)
        {
            Exec(conn, @"
                INSERT INTO class_leverage (terminal_id, asset_class, leverage, detected_at)
                VALUES (@tid, @ac, @lev, @at)
                ON CONFLICT(terminal_id, asset_class) DO UPDATE SET
                    leverage = @lev, detected_at = @at",
                ("@tid", terminalId), ("@ac", aclass), ("@lev", leverage), ("@at", now));
        }
    }

    /// <summary>Load all detected leverage for a terminal.</summary>
    public Dictionary<string, int> GetClassLeverage(string terminalId)
    {
        var result = new Dictionary<string, int>();
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT asset_class, leverage FROM class_leverage WHERE terminal_id = @tid";
        cmd.Parameters.AddWithValue("@tid", terminalId);
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            result[reader.GetString(0)] = reader.GetInt32(1);
        return result;
    }

    /// <summary>Get effective leverage for a symbol on a terminal.
    /// Priority: 1) detected per-class, 2) conservative default, 3) account leverage.
    /// For FX, account leverage is always authoritative.</summary>
    public int GetEffectiveLeverage(string terminalId, string symbol, int accountLeverage)
    {
        // Determine leverage class from sizing asset_class
        var sizing = GetSymbolSizing(terminalId, symbol);
        string leverageClass = MapToLeverageClass(sizing?.AssetClass ?? "forex");

        // FX: account leverage is authoritative (MT5 uses it directly)
        if (leverageClass == "FX" && accountLeverage > 0)
            return accountLeverage;

        // Try detected leverage
        var classLev = GetClassLeverage(terminalId);
        if (classLev.TryGetValue(leverageClass, out var detected) && detected > 0)
            return detected;

        // Conservative default
        if (DefaultLeverage.TryGetValue(leverageClass, out var def))
            return def;

        // Last resort
        return accountLeverage > 0 ? accountLeverage : 100;
    }

}
