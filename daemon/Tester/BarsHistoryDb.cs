using System.Text.Json;
using Microsoft.Data.Sqlite;
using Daemon.Models;

namespace Daemon.Tester;

/// <summary>
/// SQLite storage for historical bars (separate bars_history.db).
/// Tables: bars (OHLCV per source), download_meta (coverage info), backtest_runs (results + research ref).
/// Thread-safe via connection-per-call with WAL mode.
///
/// v2: PK includes source (terminal/broker) for multi-broker data isolation.
///     Generated columns dt/date_str for human-readable dates.
/// </summary>
public class BarsHistoryDb : IDisposable
{
    private readonly string _dbPath;
    private readonly string _connStr;

    public BarsHistoryDb(string dbPath)
    {
        _dbPath = dbPath;
        _connStr = $"Data Source={dbPath}";
        InitializeDatabase();
    }

    // ===================================================================
    // Schema
    // ===================================================================

    private void InitializeDatabase()
    {
        using var conn = Open();

        Exec(conn, "PRAGMA journal_mode=WAL");
        Exec(conn, "PRAGMA foreign_keys=ON");

        // Check if migration is needed (old bars table without source column)
        if (TableExists(conn, "bars") && !ColumnExists(conn, "bars", "source"))
        {
            MigrateToV2(conn);
        }
        else
        {
            // Fresh install or already migrated — create v2 schema
            Exec(conn, @"
            CREATE TABLE IF NOT EXISTS bars (
                symbol    TEXT    NOT NULL,
                timeframe TEXT    NOT NULL,
                time      INTEGER NOT NULL,
                source    TEXT    NOT NULL DEFAULT 'legacy',
                open      REAL    NOT NULL,
                high      REAL    NOT NULL,
                low       REAL    NOT NULL,
                close     REAL    NOT NULL,
                volume    INTEGER NOT NULL DEFAULT 0,
                PRIMARY KEY (symbol, timeframe, time, source)
            ) WITHOUT ROWID");

            Exec(conn, @"
            CREATE TABLE IF NOT EXISTS download_meta (
                symbol        TEXT NOT NULL,
                timeframe     TEXT NOT NULL,
                source        TEXT NOT NULL DEFAULT 'legacy',
                from_time     INTEGER NOT NULL,
                to_time       INTEGER NOT NULL,
                bar_count     INTEGER NOT NULL,
                downloaded_at TEXT NOT NULL,
                terminal_id   TEXT NOT NULL,
                server_tz     TEXT NOT NULL DEFAULT 'EET',
                PRIMARY KEY (symbol, timeframe, source)
            )");
        }

        // Generated columns for human-readable dates (zero storage, computed on read)
        MigrateAddColumn(conn, "bars", "dt",
            "TEXT GENERATED ALWAYS AS (datetime(time, 'unixepoch')) VIRTUAL");
        MigrateAddColumn(conn, "bars", "date_str",
            "TEXT GENERATED ALWAYS AS (date(time, 'unixepoch')) VIRTUAL");

        // Backtest run results + optional research reference
        Exec(conn, @"
        CREATE TABLE IF NOT EXISTS backtest_runs (
            id            INTEGER PRIMARY KEY AUTOINCREMENT,
            strategy_name TEXT NOT NULL,
            terminal_id   TEXT NOT NULL,
            from_time     INTEGER NOT NULL,
            to_time       INTEGER NOT NULL,
            config_json   TEXT NOT NULL,
            result_json   TEXT NOT NULL,
            research_ref  TEXT,
            run_at        TEXT NOT NULL
        )");
    }

    /// <summary>
    /// Migrate v1 schema (no source) to v2 (with source).
    /// WITHOUT ROWID tables can't ALTER PK, so we recreate.
    /// </summary>
    private void MigrateToV2(SqliteConnection conn)
    {
        using var tx = conn.BeginTransaction();

        // Drop leftover from a failed previous migration attempt
        if (TableExists(conn, "bars_v2"))
            Exec(conn, "DROP TABLE bars_v2");
        if (TableExists(conn, "download_meta_v2"))
            Exec(conn, "DROP TABLE download_meta_v2");

        // 1. Bars: create new, copy with real source from download_meta, swap
        Exec(conn, @"
        CREATE TABLE bars_v2 (
            symbol    TEXT    NOT NULL,
            timeframe TEXT    NOT NULL,
            time      INTEGER NOT NULL,
            source    TEXT    NOT NULL DEFAULT 'legacy',
            open      REAL    NOT NULL,
            high      REAL    NOT NULL,
            low       REAL    NOT NULL,
            close     REAL    NOT NULL,
            volume    INTEGER NOT NULL DEFAULT 0,
            PRIMARY KEY (symbol, timeframe, time, source)
        ) WITHOUT ROWID");

        // Join with download_meta to get actual terminal_id as source
        Exec(conn, @"
        INSERT INTO bars_v2 (symbol, timeframe, time, source, open, high, low, close, volume)
        SELECT b.symbol, b.timeframe, b.time,
               COALESCE(m.terminal_id, 'legacy'),
               b.open, b.high, b.low, b.close, b.volume
        FROM bars b
        LEFT JOIN download_meta m ON b.symbol = m.symbol AND b.timeframe = m.timeframe");

        Exec(conn, "DROP TABLE bars");
        Exec(conn, "ALTER TABLE bars_v2 RENAME TO bars");

        // 2. Download_meta: create new, copy, swap
        if (TableExists(conn, "download_meta"))
        {
            Exec(conn, @"
            CREATE TABLE download_meta_v2 (
                symbol        TEXT NOT NULL,
                timeframe     TEXT NOT NULL,
                source        TEXT NOT NULL DEFAULT 'legacy',
                from_time     INTEGER NOT NULL,
                to_time       INTEGER NOT NULL,
                bar_count     INTEGER NOT NULL,
                downloaded_at TEXT NOT NULL,
                terminal_id   TEXT NOT NULL,
                server_tz     TEXT NOT NULL DEFAULT 'EET',
                PRIMARY KEY (symbol, timeframe, source)
            )");

            Exec(conn, @"
            INSERT INTO download_meta_v2
                (symbol, timeframe, source, from_time, to_time, bar_count, downloaded_at, terminal_id, server_tz)
            SELECT symbol, timeframe, terminal_id, from_time, to_time, bar_count, downloaded_at, terminal_id, server_tz
            FROM download_meta");

            Exec(conn, "DROP TABLE download_meta");
            Exec(conn, "ALTER TABLE download_meta_v2 RENAME TO download_meta");
        }

        tx.Commit();
    }

    // ===================================================================
    // Bars — Write
    // ===================================================================

    /// <summary>
    /// Bulk-save bars with INSERT OR REPLACE in a single transaction.
    /// Also updates download_meta.
    /// </summary>
    public void SaveBarsBulk(string symbol, string timeframe, List<Bar> bars,
                              string terminalId, string serverTz = "EET",
                              string? source = null)
    {
        if (bars.Count == 0) return;
        source ??= terminalId;

        using var conn = Open();
        using var tx = conn.BeginTransaction();

        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            INSERT OR REPLACE INTO bars (symbol, timeframe, time, source, open, high, low, close, volume)
            VALUES ($symbol, $tf, $time, $source, $open, $high, $low, $close, $volume)";

        var pSymbol = cmd.Parameters.Add("$symbol", SqliteType.Text);
        var pTf = cmd.Parameters.Add("$tf", SqliteType.Text);
        var pTime = cmd.Parameters.Add("$time", SqliteType.Integer);
        var pSource = cmd.Parameters.Add("$source", SqliteType.Text);
        var pOpen = cmd.Parameters.Add("$open", SqliteType.Real);
        var pHigh = cmd.Parameters.Add("$high", SqliteType.Real);
        var pLow = cmd.Parameters.Add("$low", SqliteType.Real);
        var pClose = cmd.Parameters.Add("$close", SqliteType.Real);
        var pVol = cmd.Parameters.Add("$volume", SqliteType.Integer);

        foreach (var bar in bars)
        {
            pSymbol.Value = symbol;
            pTf.Value = timeframe;
            pTime.Value = bar.Time;
            pSource.Value = source;
            pOpen.Value = bar.Open;
            pHigh.Value = bar.High;
            pLow.Value = bar.Low;
            pClose.Value = bar.Close;
            pVol.Value = bar.Volume;
            cmd.ExecuteNonQuery();
        }

        // Update download_meta
        long fromTime = bars.Min(b => b.Time);
        long toTime = bars.Max(b => b.Time);

        Exec(conn, @"
            INSERT OR REPLACE INTO download_meta
                (symbol, timeframe, source, from_time, to_time, bar_count, downloaded_at, terminal_id, server_tz)
            VALUES ($symbol, $tf, $source, $from, $to, $count, $at, $tid, $tz)",
            ("$symbol", symbol), ("$tf", timeframe), ("$source", source),
            ("$from", fromTime), ("$to", toTime),
            ("$count", bars.Count),
            ("$at", DateTime.UtcNow.ToString("o")),
            ("$tid", terminalId), ("$tz", serverTz));

        tx.Commit();
    }

    // ===================================================================
    // Bars — Read
    // ===================================================================

    /// <summary>Get bars in a time range (inclusive), filtered by source.</summary>
    public List<Bar> GetBars(string symbol, string timeframe, long fromTime, long toTime,
                              string source = "legacy")
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT time, open, high, low, close, volume
            FROM bars
            WHERE symbol = $symbol AND timeframe = $tf AND source = $source
              AND time >= $from AND time <= $to
            ORDER BY time";
        cmd.Parameters.AddWithValue("$symbol", symbol);
        cmd.Parameters.AddWithValue("$tf", timeframe);
        cmd.Parameters.AddWithValue("$source", source);
        cmd.Parameters.AddWithValue("$from", fromTime);
        cmd.Parameters.AddWithValue("$to", toTime);

        return ReadBars(cmd);
    }

    /// <summary>Get ALL bars for a symbol/timeframe/source, ordered by time.</summary>
    public List<Bar> GetAllBars(string symbol, string timeframe, string source = "legacy")
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT time, open, high, low, close, volume
            FROM bars
            WHERE symbol = $symbol AND timeframe = $tf AND source = $source
            ORDER BY time";
        cmd.Parameters.AddWithValue("$symbol", symbol);
        cmd.Parameters.AddWithValue("$tf", timeframe);
        cmd.Parameters.AddWithValue("$source", source);

        return ReadBars(cmd);
    }

    /// <summary>Count bars in a range.</summary>
    public int CountBars(string symbol, string timeframe, long fromTime, long toTime,
                          string source = "legacy")
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT COUNT(*) FROM bars
            WHERE symbol = $symbol AND timeframe = $tf AND source = $source
              AND time >= $from AND time <= $to";
        cmd.Parameters.AddWithValue("$symbol", symbol);
        cmd.Parameters.AddWithValue("$tf", timeframe);
        cmd.Parameters.AddWithValue("$source", source);
        cmd.Parameters.AddWithValue("$from", fromTime);
        cmd.Parameters.AddWithValue("$to", toTime);

        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    // ===================================================================
    // Download metadata
    // ===================================================================

    /// <summary>Get download metadata for a symbol/timeframe/source.</summary>
    public DownloadMeta? GetMeta(string symbol, string timeframe, string source = "legacy")
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT symbol, timeframe, source, from_time, to_time, bar_count,
                   downloaded_at, terminal_id, server_tz
            FROM download_meta
            WHERE symbol = $symbol AND timeframe = $tf AND source = $source";
        cmd.Parameters.AddWithValue("$symbol", symbol);
        cmd.Parameters.AddWithValue("$tf", timeframe);
        cmd.Parameters.AddWithValue("$source", source);

        using var r = cmd.ExecuteReader();
        return r.Read() ? ReadMeta(r) : null;
    }

    /// <summary>Get all download metadata entries.</summary>
    public List<DownloadMeta> GetAllMeta()
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT symbol, timeframe, source, from_time, to_time, bar_count,
                   downloaded_at, terminal_id, server_tz
            FROM download_meta
            ORDER BY symbol, timeframe, source";

        var list = new List<DownloadMeta>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
            list.Add(ReadMeta(r));
        return list;
    }

    // ===================================================================
    // Data coverage check
    // ===================================================================

    /// <summary>Check bar data coverage for a list of symbols.</summary>
    public DataCoverage GetCoverage(List<string> symbols, string timeframe,
                                     long fromTime, long toTime,
                                     string source = "legacy")
    {
        var result = new DataCoverage();
        int totalRequired = 0;
        int totalAvailable = 0;

        foreach (var symbol in symbols)
        {
            var meta = GetMeta(symbol, timeframe, source);
            var info = new CoverageInfo();

            if (meta != null && meta.BarCount > 0)
            {
                info.HasData = true;
                info.AvailableFrom = meta.FromTime;
                info.AvailableTo = meta.ToTime;
                info.BarCount = meta.BarCount;

                // Check coverage
                info.FullyCovered = meta.FromTime <= fromTime && meta.ToTime >= toTime;
                info.PartialCovered = !info.FullyCovered && meta.FromTime <= toTime && meta.ToTime >= fromTime;
            }

            result.Symbols[symbol] = info;
            totalRequired++;
            if (info.FullyCovered) totalAvailable++;
        }

        result.TotalRequired = totalRequired;
        result.TotalAvailable = totalAvailable;
        result.Percent = totalRequired > 0 ? (double)totalAvailable / totalRequired : 0;

        return result;
    }

    // ===================================================================
    // Backtest runs
    // ===================================================================

    /// <summary>Save a backtest run result. Returns the run ID.</summary>
    public int SaveRun(string strategyName, string terminalId,
                       long fromTime, long toTime,
                       string configJson, string resultJson,
                       string? researchRefJson = null)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            INSERT INTO backtest_runs
                (strategy_name, terminal_id, from_time, to_time, config_json, result_json, research_ref, run_at)
            VALUES ($strat, $tid, $from, $to, $config, $result, $ref, $at);
            SELECT last_insert_rowid();";
        cmd.Parameters.AddWithValue("$strat", strategyName);
        cmd.Parameters.AddWithValue("$tid", terminalId);
        cmd.Parameters.AddWithValue("$from", fromTime);
        cmd.Parameters.AddWithValue("$to", toTime);
        cmd.Parameters.AddWithValue("$config", configJson);
        cmd.Parameters.AddWithValue("$result", resultJson);
        cmd.Parameters.AddWithValue("$ref", (object?)researchRefJson ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$at", DateTime.UtcNow.ToString("o"));

        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    /// <summary>Get list of backtest runs, optionally filtered by strategy name.</summary>
    public List<BacktestRunSummary> GetRuns(string? strategyName = null)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();

        if (strategyName != null)
        {
            cmd.CommandText = @"
                SELECT id, strategy_name, terminal_id, from_time, to_time,
                       config_json, result_json, research_ref, run_at
                FROM backtest_runs
                WHERE strategy_name = $strat
                ORDER BY run_at DESC";
            cmd.Parameters.AddWithValue("$strat", strategyName);
        }
        else
        {
            cmd.CommandText = @"
                SELECT id, strategy_name, terminal_id, from_time, to_time,
                       config_json, result_json, research_ref, run_at
                FROM backtest_runs
                ORDER BY run_at DESC";
        }

        var list = new List<BacktestRunSummary>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            list.Add(new BacktestRunSummary
            {
                Id = r.GetInt32(0),
                StrategyName = r.GetString(1),
                TerminalId = r.GetString(2),
                FromTime = r.GetInt64(3),
                ToTime = r.GetInt64(4),
                ConfigJson = r.GetString(5),
                ResultJson = r.GetString(6),
                ResearchRefJson = r.IsDBNull(7) ? null : r.GetString(7),
                RunAt = DateTime.Parse(r.GetString(8))
            });
        }
        return list;
    }

    /// <summary>Delete bars for a specific symbol/timeframe/source.</summary>
    public int DeleteBars(string symbol, string timeframe, string source = "legacy")
    {
        using var conn = Open();
        using var tx = conn.BeginTransaction();

        using var cmd1 = conn.CreateCommand();
        cmd1.CommandText = "DELETE FROM bars WHERE symbol = $s AND timeframe = $tf AND source = $src";
        cmd1.Parameters.AddWithValue("$s", symbol);
        cmd1.Parameters.AddWithValue("$tf", timeframe);
        cmd1.Parameters.AddWithValue("$src", source);
        int deleted = cmd1.ExecuteNonQuery();

        Exec(conn, "DELETE FROM download_meta WHERE symbol = $s AND timeframe = $tf AND source = $src",
             ("$s", symbol), ("$tf", timeframe), ("$src", source));

        tx.Commit();
        return deleted;
    }

    /// <summary>Delete all data (bars + meta). Preserves backtest_runs.</summary>
    public void ClearAllBars()
    {
        using var conn = Open();
        Exec(conn, "DELETE FROM bars");
        Exec(conn, "DELETE FROM download_meta");
    }

    /// <summary>Get list of distinct sources in the database.</summary>
    public List<string> GetSources()
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT DISTINCT source FROM download_meta ORDER BY source";
        var list = new List<string>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
            list.Add(r.GetString(0));
        return list;
    }

    /// <summary>Get DB file size in bytes.</summary>
    public long GetDbSizeBytes()
    {
        return File.Exists(_dbPath) ? new FileInfo(_dbPath).Length : 0;
    }

    // ===================================================================
    // Dispose
    // ===================================================================

    public void Dispose()
    {
        // Connection-per-call pattern — nothing to dispose at class level.
        // SQLite WAL checkpoint happens automatically.
    }

    // ===================================================================
    // Helpers
    // ===================================================================

    private SqliteConnection Open()
    {
        var conn = new SqliteConnection(_connStr);
        conn.Open();
        return conn;
    }

    private static void Exec(SqliteConnection conn, string sql,
                              params (string name, object value)[] parameters)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (name, value) in parameters)
            cmd.Parameters.AddWithValue(name, value);
        cmd.ExecuteNonQuery();
    }

    /// <summary>Idempotent: add column if it doesn't exist yet.</summary>
    private static void MigrateAddColumn(SqliteConnection conn, string table, string column, string definition)
    {
        if (ColumnExists(conn, table, column))
            return;
        try
        {
            Exec(conn, $"ALTER TABLE {table} ADD COLUMN {column} {definition}");
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode == 1
            && ex.Message.Contains("duplicate column", StringComparison.OrdinalIgnoreCase))
        {
            // Already exists — race or stale pragma cache, safe to ignore
        }
    }

    private static bool TableExists(SqliteConnection conn, string table)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name = $name";
        cmd.Parameters.AddWithValue("$name", table);
        return Convert.ToInt32(cmd.ExecuteScalar()) > 0;
    }

    private static bool ColumnExists(SqliteConnection conn, string table, string column)
    {
        using var cmd = conn.CreateCommand();
        // No parameters in pragma — some SQLite drivers don't bind them correctly
        cmd.CommandText = $"SELECT COUNT(*) FROM pragma_table_info('{table}') WHERE name = '{column}'";
        return Convert.ToInt32(cmd.ExecuteScalar()) > 0;
    }

    private static List<Bar> ReadBars(SqliteCommand cmd)
    {
        var list = new List<Bar>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            list.Add(new Bar
            {
                Time = r.GetInt64(0),
                Open = r.GetDouble(1),
                High = r.GetDouble(2),
                Low = r.GetDouble(3),
                Close = r.GetDouble(4),
                Volume = r.GetInt64(5)
            });
        }
        return list;
    }

    private static DownloadMeta ReadMeta(SqliteDataReader r)
    {
        return new DownloadMeta
        {
            Symbol = r.GetString(0),
            Timeframe = r.GetString(1),
            Source = r.GetString(2),
            FromTime = r.GetInt64(3),
            ToTime = r.GetInt64(4),
            BarCount = r.GetInt32(5),
            DownloadedAt = DateTime.Parse(r.GetString(6)),
            TerminalId = r.GetString(7),
            ServerTz = r.GetString(8)
        };
    }
}

// ===================================================================
// DTOs
// ===================================================================

/// <summary>Download metadata for a symbol/timeframe/source.</summary>
public class DownloadMeta
{
    public string Symbol { get; set; } = "";
    public string Timeframe { get; set; } = "";
    public string Source { get; set; } = "legacy";
    public long FromTime { get; set; }
    public long ToTime { get; set; }
    public int BarCount { get; set; }
    public DateTime DownloadedAt { get; set; }
    public string TerminalId { get; set; } = "";
    public string ServerTz { get; set; } = "EET";
}

/// <summary>Data coverage check result.</summary>
public class DataCoverage
{
    public Dictionary<string, CoverageInfo> Symbols { get; set; } = new();
    public int TotalRequired { get; set; }
    public int TotalAvailable { get; set; }
    public double Percent { get; set; }
}

/// <summary>Per-symbol coverage info.</summary>
public class CoverageInfo
{
    public bool HasData { get; set; }
    public long? AvailableFrom { get; set; }
    public long? AvailableTo { get; set; }
    public int BarCount { get; set; }
    public bool FullyCovered { get; set; }
    public bool PartialCovered { get; set; }
}

/// <summary>Summary of a saved backtest run.</summary>
public class BacktestRunSummary
{
    public int Id { get; set; }
    public string StrategyName { get; set; } = "";
    public string TerminalId { get; set; } = "";
    public long FromTime { get; set; }
    public long ToTime { get; set; }
    public string ConfigJson { get; set; } = "";
    public string ResultJson { get; set; } = "";
    public string? ResearchRefJson { get; set; }
    public DateTime RunAt { get; set; }
}
