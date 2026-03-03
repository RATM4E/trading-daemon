using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Collections.Concurrent;
using Daemon.Config;
using Daemon.Connector;
using Daemon.Engine;
using Daemon.Models;
using Daemon.Strategy;

namespace Daemon.Dashboard;

public partial class DashboardServer
{
 // ===================================================================
 // Phase 9.V: Virtual Trading Commands
 // ===================================================================

 // -------------------------------------------------------------------
 // get_virtual_equity -- equity curve + trade markers
 // -------------------------------------------------------------------

    private object HandleGetVirtualEquity(JsonElement root)
    {
        var terminal = root.GetProperty("terminal").GetString()!;

        var equityHistory = _state.GetVirtualEquityHistory(terminal, 2000);
        var data = equityHistory.Select(e => new
        {
            time = e.Timestamp,
            equity = Math.Round(e.Equity, 2),
            balance = Math.Round(e.Balance, 2),
            unrealized = Math.Round(e.UnrealizedPnl, 2),
            positions = e.OpenPositions
        }).ToList();

        var closedPositions = GetClosedPositions(terminal, 500);
        var trades = closedPositions
            .Where(p => p.Ticket < 0)
            .Select(p => new
            {
                time = p.ClosedAt,
                symbol = p.Symbol,
                dir = p.Direction,
                pnl = Math.Round(p.Pnl, 2),
                reason = p.CloseReason
            }).ToList();

        return new { cmd = "virtual_equity", terminal, data, trades };
    }

 // -------------------------------------------------------------------
 // get_virtual_stats -- statistics panel data
 // -------------------------------------------------------------------


    private object HandleGetVirtualStats(JsonElement root)
    {
        var terminal = root.GetProperty("terminal").GetString()!;
        var stats = _state.GetVirtualStats(terminal);

        return new
        {
            cmd = "virtual_stats",
            terminal,
            stats = new
            {
                totalTrades = stats.TotalTrades,
                wins = stats.Wins,
                losses = stats.Losses,
                winRate = Math.Round(stats.WinRate, 1),
                avgWin = Math.Round(stats.AvgWin, 2),
                avgLoss = Math.Round(stats.AvgLoss, 2),
                profitFactor = Math.Round(stats.ProfitFactor, 2),
                netPnl = Math.Round(stats.NetPnl, 2),
                maxDrawdown = Math.Round(stats.MaxDrawdown, 2),
                maxDrawdownPct = Math.Round(stats.MaxDrawdownPct, 1),
                expectancy = Math.Round(stats.Expectancy, 2)
            }
        };
    }

 // -------------------------------------------------------------------
 // get_trade_chart -- universal trade visualization (virtual + real)
 // -------------------------------------------------------------------


    private async Task<object> HandleGetTradeChart(JsonElement root, CancellationToken ct)
    {
        var terminal = root.GetProperty("terminal").GetString()!;
        var ticket = root.GetProperty("ticket").GetInt64();

        // Try open position first
        var openPos = _state.GetOpenPositions(terminal)
            .FirstOrDefault(p => p.Ticket == ticket);

        if (openPos != null)
        {
            var tf = openPos.Timeframe ?? "H1";
            var allBars = _barsCache?.GetBars(terminal, openPos.Symbol, tf);
            var slHistory = _state.GetSlHistory(ticket, terminal);

            // Trim to last 250 bars (enough context for TMM-style visualization)
            var bars = allBars;
            if (bars != null && bars.Count > 250)
                bars = bars.Skip(bars.Count - 250).ToList();

            double current = bars is { Count: > 0 } ? bars[^1].Close : openPos.PriceOpen;
            double exitPrice = current;

            // Get symbol info for accurate P/L and precision
            Daemon.Models.InstrumentCard? card = null;
            try { card = await _connector.GetSymbolInfoAsync(terminal, openPos.Symbol, ct); } catch { }

            int digits = card?.Digits ?? (openPos.Symbol.Contains("JPY") ? 3 : 5);

            if (openPos.Direction == "SELL" && card is { Spread: > 0, Point: > 0 })
                exitPrice += card.Spread * card.Point;
            else if (openPos.Direction == "SELL")
                exitPrice += openPos.Symbol.Contains("JPY") ? 0.03 : 0.00020;

            double unrealizedPnl = 0;
            if (_virtualTracker != null && openPos.IsVirtual)
            {
                if (card != null) _virtualTracker.CacheSymbol(terminal, openPos.Symbol, card);
                unrealizedPnl = _virtualTracker.CalculateVirtualPnl(openPos, exitPrice, terminal);
            }
            else if (card != null && card.TradeTickSize > 0)
            {
                double dirSign = openPos.Direction == "BUY" ? 1 : -1;
                double priceDiff = dirSign * (exitPrice - openPos.PriceOpen);
                double ticks = priceDiff / card.TradeTickSize;
                double tickValue = priceDiff >= 0 ? card.TradeTickValueProfit : card.TradeTickValueLoss;
                if (tickValue <= 0) tickValue = card.TradeTickValue;
                unrealizedPnl = ticks * tickValue * openPos.Volume;
            }
            else
            {
                double dirSign = openPos.Direction == "BUY" ? 1 : -1;
                double priceDiff = dirSign * (exitPrice - openPos.PriceOpen);
                double tickSize = openPos.Symbol.Contains("JPY") ? 0.001 : 0.00001;
                double tickValue = openPos.Symbol.Contains("JPY") ? 0.01 : 0.10;
                unrealizedPnl = priceDiff / tickSize * tickValue * openPos.Volume;
            }

            var openTime = DateTime.TryParse(openPos.OpenedAt, out var ot) ? ot : DateTime.UtcNow;
            double duration = (DateTime.UtcNow - openTime).TotalSeconds;

            return new
            {
                cmd = "trade_chart",
                terminal,
                mode = "live",
                digits,
                trade = new
                {
                    ticket,
                    symbol = openPos.Symbol,
                    direction = openPos.Direction,
                    entryPrice = openPos.PriceOpen,
                    entryTime = openPos.OpenedAt,
                    currentSl = openPos.SL,
                    tp = openPos.TP,
                    volume = openPos.Volume,
                    strategy = openPos.Source,
                    isVirtual = openPos.IsVirtual,
                    unrealizedPnl = Math.Round(unrealizedPnl, 2),
                    durationSec = (int)duration,
                    timeframe = tf,
                    current = Math.Round(current, digits)
                },
                bars = bars != null
                    ? bars.Select(b => (object)new { time = b.Time, open = b.Open, high = b.High, low = b.Low, close = b.Close }).ToList()
                    : new List<object>(),
                slHistory = slHistory.Select(s => new { time = s.BarTime, sl = s.NewSl, changedAt = s.ChangedAt }).ToList()
            };
        }

        // Try closed position -- check snapshot first
        var snapshot = _state.GetTradeSnapshot(ticket, terminal);
        if (snapshot != null)
            return new { cmd = "trade_chart", terminal, mode = "snapshot", raw = snapshot };

        // Fall back to DB
        var closedPositions = GetClosedPositions(terminal, 500);
        var closedPos = closedPositions.FirstOrDefault(p => p.Ticket == ticket);

        if (closedPos == null)
            return new { cmd = "trade_chart", error = "Position not found" };

        var slHist = _state.GetSlHistory(ticket, terminal);
        int closedDigits = closedPos.Symbol.Contains("JPY") ? 3 : 5;

        // Try to get symbol info for precision
        try
        {
            var closedCard = await _connector.GetSymbolInfoAsync(terminal, closedPos.Symbol, ct);
            if (closedCard != null) closedDigits = closedCard.Digits;
        }
        catch { /* use default */ }

        // Try to fetch bars for the symbol (still in cache if terminal is connected)
        var closedTf = closedPos.Timeframe ?? "H1";
        var closedBarsRaw = _barsCache?.GetBars(terminal, closedPos.Symbol, closedTf);
        var closedBars = closedBarsRaw;
        if (closedBars != null && closedBars.Count > 250)
            closedBars = closedBars.Skip(closedBars.Count - 250).ToList();

        // Calculate duration
        var closedOpenTime = DateTime.TryParse(closedPos.OpenedAt, out var cot) ? cot : DateTime.UtcNow;
        var closedCloseTime = DateTime.TryParse(closedPos.ClosedAt, out var cct) ? cct : DateTime.UtcNow;
        double closedDuration = (closedCloseTime - closedOpenTime).TotalSeconds;

        return new
        {
            cmd = "trade_chart",
            terminal,
            mode = "snapshot",
            digits = closedDigits,
            trade = new
            {
                ticket,
                symbol = closedPos.Symbol,
                direction = closedPos.Direction,
                entryPrice = closedPos.PriceOpen,
                entry_price = closedPos.PriceOpen,
                exitPrice = closedPos.ClosePrice,
                exit_price = closedPos.ClosePrice,
                entryTime = closedPos.OpenedAt,
                entry_time = closedPos.OpenedAt,
                exit_time = closedPos.ClosedAt,
                openedAt = closedPos.OpenedAt,
                closedAt = closedPos.ClosedAt,
                closeReason = closedPos.CloseReason,
                close_reason = closedPos.CloseReason,
                volume = closedPos.Volume,
                pnl = Math.Round(closedPos.Pnl, 2),
                strategy = closedPos.Source,
                isVirtual = closedPos.Ticket < 0,
                is_virtual = closedPos.Ticket < 0,
                timeframe = closedTf,
                durationSec = (int)closedDuration,
                duration_sec = (int)closedDuration
            },
            bars = closedBars != null
                ? closedBars.Select(b => (object)new { time = b.Time, open = b.Open, high = b.High, low = b.Low, close = b.Close }).ToList()
                : new List<object>(),
            slHistory = slHist.Select(s => new { time = s.BarTime, sl = s.NewSl, changedAt = s.ChangedAt }).ToList()
        };
    }

 // -------------------------------------------------------------------
 // reset_virtual -- reset virtual trading for a terminal
 // -------------------------------------------------------------------


    private object HandleResetVirtual(JsonElement root)
    {
        var terminal = root.GetProperty("terminal").GetString()!;

        var profile = _state.GetProfile(terminal);
        if (profile == null)
            return new { cmd = "reset_virtual", ok = false, error = "Terminal not found" };

        double newBalance = 0;
        try
        {
            var acc = _connector.GetAccountInfoAsync(terminal, CancellationToken.None).Result;
            newBalance = acc?.Balance ?? 100000;
        }
        catch { newBalance = 100000; }

        _state.ResetVirtualTrading(terminal, newBalance);
        _state.LogEvent("CONFIG", terminal, null, $"Virtual trading reset. New balance: {newBalance:F2}");
        _log.Info($"[Dashboard] {terminal}: virtual trading reset, balance={newBalance:F2}");

        return new { cmd = "reset_virtual", ok = true, terminal, newBalance = Math.Round(newBalance, 2) };
    }

 // -------------------------------------------------------------------
 // reset_flags -- reset blocking flags (daily_r, sl3, daily_pnl) for a terminal
 // -------------------------------------------------------------------


    private object HandleExportVirtualCsv(JsonElement root)
    {
        var terminal = root.GetProperty("terminal").GetString()!;
        var trades = _state.ExportVirtualTrades(terminal);

        if (trades.Count == 0)
            return new { cmd = "virtual_csv", terminal, csv = "No virtual trades found" };

        var sb = new StringBuilder();
        var headers = new[] { "ticket", "symbol", "direction", "volume", "price_open",
            "close_price", "pnl", "close_reason", "source", "opened_at", "closed_at" };
        sb.AppendLine(string.Join(",", headers));

        foreach (var trade in trades)
        {
            var values = headers.Select(h => trade.TryGetValue(h, out var v) ? v?.ToString() ?? "" : "");
            sb.AppendLine(string.Join(",", values));
        }

        return new { cmd = "virtual_csv", terminal, csv = sb.ToString() };
    }

 /// <summary>Query closed positions from the state database.</summary>
    private List<ClosedPositionRecord> GetClosedPositions(string terminalId, int limit)
    {
        var result = new List<ClosedPositionRecord>();

        try
        {
            var dbPath = Path.Combine(AppContext.BaseDirectory, "Data", "state.db");
            if (!File.Exists(dbPath)) return result;

            using var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={dbPath}");
            conn.Open();

            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                SELECT ticket, symbol, direction, volume, price_open,
                       close_price, pnl, close_reason, source, opened_at, closed_at, timeframe
                FROM positions
                WHERE terminal_id = @tid AND closed_at IS NOT NULL
                ORDER BY closed_at DESC
                LIMIT @limit";
            cmd.Parameters.AddWithValue("@tid", terminalId);
            cmd.Parameters.AddWithValue("@limit", limit);

            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                result.Add(new ClosedPositionRecord
                {
                    Ticket = r.GetInt64(0),
                    Symbol = r.IsDBNull(1) ? "" : r.GetString(1),
                    Direction = r.IsDBNull(2) ? "" : r.GetString(2),
                    Volume = r.IsDBNull(3) ? 0 : r.GetDouble(3),
                    PriceOpen = r.IsDBNull(4) ? 0 : r.GetDouble(4),
                    ClosePrice = r.IsDBNull(5) ? 0 : r.GetDouble(5),
                    Pnl = r.IsDBNull(6) ? 0 : r.GetDouble(6),
                    CloseReason = r.IsDBNull(7) ? "" : r.GetString(7),
                    Source = r.IsDBNull(8) ? "" : r.GetString(8),
                    OpenedAt = r.IsDBNull(9) ? "" : r.GetString(9),
                    ClosedAt = r.IsDBNull(10) ? "" : r.GetString(10),
                    Timeframe = r.IsDBNull(11) ? null : r.GetString(11)
                });
            }
        }
        catch (Exception ex)
        {
            _log.Error($"[Dashboard] GetClosedPositions error: {ex.Message}");
        }

        return result;
    }

    private class ClosedPositionRecord
    {
        public long Ticket { get; set; }
        public string Symbol { get; set; } = "";
        public string Direction { get; set; } = "";
        public double Volume { get; set; }
        public double PriceOpen { get; set; }
        public double ClosePrice { get; set; }
        public double Pnl { get; set; }
        public string CloseReason { get; set; } = "";
        public string Source { get; set; } = "";
        public string OpenedAt { get; set; } = "";
        public string ClosedAt { get; set; } = "";
        public string? Timeframe { get; set; }
    }

}
