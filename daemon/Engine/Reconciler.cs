using System.Text.Json;
using Daemon.Connector;
using Daemon.Models;

namespace Daemon.Engine;

/// <summary>
/// Reconciles daemon's position book (SQLite) with actual positions in MT5 terminal.
///
/// Cold reconciliation: runs at startup, catches everything that happened while daemon was offline.
/// Hot reconciliation: runs periodically (every 30s), catches real-time changes.
///
/// Scenarios handled:
///   1. Position in DB AND terminal → verify SL/TP/volume, update if changed
///   2. Position in DB but NOT terminal → closed while daemon was down → resolve from history
///   3. Position in terminal but NOT DB → opened externally → add to book
///
/// Phase 9.V: Virtual positions (IsVirtual=true) are excluded from reconciliation.
/// They live only in DB and are managed by VirtualTracker, not MT5.
/// </summary>
public class Reconciler
{
    private readonly StateManager _state;
    private readonly ConnectorManager _connector;
    private readonly ConsoleLogger _log;

    // Magic ranges that belong to our strategies
    // Each strategy owns [magicBase, magicBase + 1000) range for combo support
    private readonly List<(int Base, string Strategy)> _knownMagicRanges = new();

    public Reconciler(StateManager state, ConnectorManager connector, ConsoleLogger log)
    {
        _state = state;
        _connector = connector;
        _log = log;
    }

    /// <summary>Register a magic range as belonging to a strategy (1000 slots per strategy).</summary>
    public void RegisterMagicRange(int magicBase, string strategyName)
        => _knownMagicRanges.Add((magicBase, strategyName));

    /// <summary>
    /// Full reconciliation for a terminal.
    /// Returns a summary of what changed.
    /// </summary>
    public async Task<ReconResult> ReconcileAsync(string terminalId, CancellationToken ct = default)
    {
        var result = new ReconResult { TerminalId = terminalId };

        // 1. Get both sides
        // Phase 9.V: Filter out virtual positions — they don't exist in MT5
        var dbPositions = _state.GetOpenPositions(terminalId)
            .Where(p => !p.IsVirtual)
            .ToList();
        var livePositions = await _connector.GetPositionsAsync(terminalId, ct);

        var liveByTicket = livePositions.ToDictionary(p => p.Ticket);
        var dbByTicket = dbPositions.ToDictionary(p => p.Ticket);

        // 2. Check each DB position against terminal
        foreach (var dbPos in dbPositions)
        {
            if (liveByTicket.TryGetValue(dbPos.Ticket, out var livePos))
            {
                // Position exists in both — check for changes
                var changes = new List<string>();

                if (Math.Abs(dbPos.SL - livePos.SL) > 1e-10)
                {
                    changes.Add($"SL: {dbPos.SL} → {livePos.SL}");
                    dbPos.SL = livePos.SL;
                }
                if (Math.Abs(dbPos.TP - livePos.TP) > 1e-10)
                {
                    changes.Add($"TP: {dbPos.TP} → {livePos.TP}");
                    dbPos.TP = livePos.TP;
                }
                if (Math.Abs(dbPos.Volume - livePos.Volume) > 1e-10)
                {
                    changes.Add($"Volume: {dbPos.Volume} → {livePos.Volume}");
                    dbPos.Volume = livePos.Volume;
                }

                if (changes.Count > 0)
                {
                    _state.SavePosition(dbPos);
                    var changeStr = string.Join(", ", changes);
                    _state.LogEvent("RECON", terminalId, dbPos.Source,
                        $"Position #{dbPos.Ticket} {dbPos.Symbol} updated: {changeStr}");
                    _log.Info($"[{terminalId}] RECON: #{dbPos.Ticket} {dbPos.Symbol} updated: {changeStr}");
                    result.Updated++;
                }
                else
                {
                    result.Matched++;
                }
            }
            else
            {
                // Position in DB but not in terminal — closed while daemon was offline
                await ResolveClosedPositionAsync(terminalId, dbPos, ct);
                result.Closed++;
            }
        }

        // 3. Check for terminal positions not in DB — new/external positions
        foreach (var livePos in livePositions)
        {
            if (!dbByTicket.ContainsKey(livePos.Ticket))
            {
                var source = DetermineSource(livePos);
                var newPos = new PositionRecord
                {
                    Ticket = livePos.Ticket,
                    TerminalId = terminalId,
                    Symbol = _connector.UnmapSymbol(terminalId, livePos.Symbol),
                    Direction = livePos.IsBuy ? "LONG" : "SHORT",
                    Volume = livePos.Volume,
                    PriceOpen = livePos.PriceOpen,
                    SL = livePos.SL,
                    TP = livePos.TP,
                    Magic = livePos.Magic,
                    Source = source,
                    OpenedAt = DateTimeOffset.FromUnixTimeSeconds(livePos.Time).ToString("o"),
                };
                _state.SavePosition(newPos);

                if (source != "unmanaged")
                {
                    _state.LogEvent("RECON", terminalId, source,
                        $"New position discovered: #{livePos.Ticket} {livePos.Symbol} " +
                        $"{(livePos.IsBuy ? "LONG" : "SHORT")} {livePos.Volume} @ {livePos.PriceOpen}");
                    _log.Info($"[{terminalId}] RECON: New position #{livePos.Ticket} {livePos.Symbol} " +
                              $"source={source}");
                }
                result.Discovered++;
            }
        }

        return result;
    }

    /// <summary>
    /// Resolve a position that was open in DB but is now gone from terminal.
    /// Queries deal history to determine close reason and P/L.
    /// </summary>
    private async Task ResolveClosedPositionAsync(string terminalId, PositionRecord dbPos,
                                                   CancellationToken ct)
    {
        _log.Info($"[{terminalId}] RECON: Position #{dbPos.Ticket} {dbPos.Symbol} " +
                  $"gone from terminal — resolving...");

        // Get deal history for this position
        var deals = await _connector.GetHistoryDealsAsync(terminalId, dbPos.Ticket, ct);

        // Find the OUT deal (entry=1)
        var closeDeal = deals
            .Where(d => d.Entry == 1) // OUT
            .OrderByDescending(d => d.Time)
            .FirstOrDefault();

        string closeReason = "unknown";
        double closePrice = 0;
        double pnl = 0;

        if (closeDeal != null)
        {
            closePrice = closeDeal.Price;
            pnl = closeDeal.Profit + closeDeal.Swap + closeDeal.Commission;

            // Determine close reason from comment
            closeReason = DetermineCloseReason(closeDeal.Comment, dbPos, closeDeal);

            _log.Info($"  Resolved: {closeReason} @ {closePrice}, P/L = {pnl:F2}");
        }
        else
        {
            // No OUT deal found — position may have been closed by partial fills
            // or deal history is not available yet
            _log.Warn($"  No OUT deal found for #{dbPos.Ticket} — marking as 'unknown'");
        }

        // Update position in DB
        _state.ClosePosition(dbPos.Ticket, terminalId, closePrice, closeReason, pnl);

        // Update daily P/L
        var profile = _state.GetProfile(terminalId);
        var serverDate = GetBrokerDate(profile?.ServerTimezone ?? "UTC");
        _state.AddRealizedPnl(terminalId, serverDate, pnl);

        // Phase 9.R: R-cap — calculate R-result and add to daily accumulator
        var rResult = RCalc.GetRResult(closeReason, dbPos.ProtectorFired, dbPos.SignalData);
        if (rResult.HasValue && dbPos.Source != "unmanaged")
        {
            _state.AddDailyR(terminalId, dbPos.Source, serverDate, rResult.Value);
            _log.Info($"  R-cap: {closeReason}" +
                      (dbPos.ProtectorFired ? " (protector)" : "") +
                      $" -> {rResult.Value:+0.00;-0.00}R");
        }

        // Update 3SL counter
        if (closeReason == "SL" && profile?.Sl3GuardOn == true)
        {
            _state.IncrementSLCount(terminalId);
            var (count, blocked) = _state.Get3SLState(terminalId);
            _log.Info($"  3SL counter: {count} consecutive SL hits");

            if (count >= 3 && !blocked)
            {
                _state.Block3SL(terminalId);
                _state.LogEvent("RISK", terminalId, null,
                    "3SL GUARD ACTIVATED — trading blocked after 3 consecutive SL hits");
                _log.Warn($"[{terminalId}] 3SL GUARD ACTIVATED — trading blocked!");
            }
        }
        else if (closeReason == "TP" || closeReason == "signal")
        {
            // Win — reset 3SL counter
            _state.ResetSLCount(terminalId);
        }

        // Log event
        _state.LogEvent("RECON", terminalId, dbPos.Source,
            $"Position #{dbPos.Ticket} {dbPos.Symbol} closed: {closeReason} " +
            $"@ {closePrice:F5}, P/L = {pnl:F2}",
            JsonSerializer.Serialize(new
            {
                ticket = dbPos.Ticket,
                symbol = dbPos.Symbol,
                direction = dbPos.Direction,
                closeReason,
                closePrice,
                pnl,
                dealTicket = closeDeal?.Ticket
            }));
    }

    /// <summary>
    /// Determine close reason from deal comment and position context.
    /// MT5 adds comments like "[sl]", "[tp]" when SL/TP is hit.
    /// </summary>
    private static string DetermineCloseReason(string? comment, PositionRecord dbPos, Deal closeDeal)
    {
        if (string.IsNullOrEmpty(comment))
            return "manual";

        var c = comment.ToLowerInvariant();

        if (c.Contains("[sl") || c.Contains("sl "))
            return "SL";
        if (c.Contains("[tp") || c.Contains("tp "))
            return "TP";
        if (c.Contains("so ") || c.Contains("stop out"))
            return "stopout";

        // Check if close price matches SL or TP level
        if (dbPos.SL > 0 && Math.Abs(closeDeal.Price - dbPos.SL) < dbPos.SL * 0.001)
            return "SL";
        if (dbPos.TP > 0 && Math.Abs(closeDeal.Price - dbPos.TP) < dbPos.TP * 0.001)
            return "TP";

        return "manual";
    }

    /// <summary>Determine position source from magic number (range-based for combo support).</summary>
    private string DetermineSource(Position livePos)
    {
        if (livePos.Magic == 0)
            return "unmanaged";
        foreach (var (magicBase, strategyName) in _knownMagicRanges)
        {
            if (livePos.Magic >= magicBase && livePos.Magic < magicBase + 1000)
                return strategyName;
        }
        return "unmanaged";
    }

    /// <summary>Get current date in broker server timezone.</summary>
    private static string GetBrokerDate(string serverTimezone)
    {
        try
        {
            var tz = TimeZoneInfo.FindSystemTimeZoneById(serverTimezone);
            var brokerNow = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, tz);
            return brokerNow.ToString("yyyy-MM-dd");
        }
        catch
        {
            return DateTime.UtcNow.ToString("yyyy-MM-dd");
        }
    }
}

/// <summary>Summary of reconciliation results.</summary>
public class ReconResult
{
    public string TerminalId { get; set; } = "";
    public int Matched { get; set; }      // DB == terminal, no changes
    public int Updated { get; set; }      // DB position updated (SL/TP/volume changed)
    public int Closed { get; set; }       // Was in DB, gone from terminal → resolved
    public int Discovered { get; set; }   // New in terminal, not in DB → added

    public int Total => Matched + Updated + Closed + Discovered;
    public bool HasChanges => Updated > 0 || Closed > 0 || Discovered > 0;

    public override string ToString() =>
        $"matched={Matched} updated={Updated} closed={Closed} discovered={Discovered}";
}
