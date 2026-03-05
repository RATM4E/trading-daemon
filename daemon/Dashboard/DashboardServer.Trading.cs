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
 // -------------------------------------------------------------------
 // get_positions  â€” open positions across all terminals
 // -------------------------------------------------------------------

    private async Task<object> HandleGetPositions(JsonElement root, CancellationToken ct)
    {
        string? filterTerminal = null;
        if (root.TryGetProperty("terminal", out var termProp))
            filterTerminal = termProp.GetString();

 // Get live positions from MT5 for P/L, and DB positions for source
        var result = new List<object>();
        var terminalIds = filterTerminal != null
            ? new List<string> { filterTerminal }
            : _connector.GetAllTerminalIds();

        foreach (var termId in terminalIds)
        {
            if (!_connector.IsConnected(termId)) continue;

            List<Position>? livePositions = null;
            try
            {
                livePositions = await _connector.GetPositionsAsync(termId, CancellationToken.None);
            }
            catch { continue; }

            if (livePositions == null) continue;

 // Get DB records for source/signal info
            var dbPositions = _state.GetOpenPositions(termId);
            var dbMap = dbPositions.ToDictionary(p => p.Ticket);

            foreach (var pos in livePositions)
            {
                var dbPos = dbMap.GetValueOrDefault(pos.Ticket);
                var openTime = DateTimeOffset.FromUnixTimeSeconds(pos.Time).UtcDateTime;
                var age = DateTime.UtcNow - openTime;

                result.Add(new
                {
                    terminal = termId,
                    ticket = pos.Ticket,
                    symbol = pos.Symbol,
                    dir = pos.IsBuy ? "LONG" : "SHORT",
                    lot = pos.Volume,
                    entry = pos.PriceOpen,
                    sl = pos.SL,
                    tp = pos.TP,
                    current = pos.PriceCurrent,
                    pnl = pos.Profit,
                    swap = pos.Swap,
                    commission = pos.Commission,
                    magic = pos.Magic,
                    age = FormatDuration(age),
                    openTime = openTime.ToString("o"),
                    source = dbPos?.Source ?? "unmanaged",
                    isVirtual = false
                });
            }
        }

        // Phase 9.V: Add virtual positions (they don't exist in MT5)
        var allVirtualTermIds = filterTerminal != null
            ? new List<string> { filterTerminal }
            : _config.Terminals.Select(t => t.Id).ToList();

        var virtualUnrealizedAccum = new Dictionary<string, double>();

        foreach (var termId in allVirtualTermIds)
        {
            var vProfile = _state.GetProfile(termId);
            if (vProfile?.Mode != "virtual") continue;

            double termUnrealized = 0;
            var virtualPositions = _state.GetOpenVirtualPositions(termId);
            foreach (var vp in virtualPositions)
            {
                var tf = vp.Timeframe ?? "H1";
                var bars = _barsCache?.GetBars(termId, vp.Symbol, tf);
                double current = bars is { Count: > 0 } ? bars[^1].Close : vp.PriceOpen;

                double exitPrice = current;
                Daemon.Models.InstrumentCard? vpCard = null;
                try { vpCard = await _connector.GetSymbolInfoAsync(termId, vp.Symbol, ct); } catch { }

                if (vp.Direction == "SELL" && vpCard is { Spread: > 0, Point: > 0 })
                    exitPrice += vpCard.Spread * vpCard.Point;
                else if (vp.Direction == "SELL")
                    exitPrice += vp.Symbol.Contains("JPY") ? 0.03 : 0.00020;

                double unrealizedPnl = 0;
                if (_virtualTracker != null)
                {
                    if (vpCard != null) _virtualTracker.CacheSymbol(termId, vp.Symbol, vpCard);
                    unrealizedPnl = _virtualTracker.CalculateVirtualPnl(vp, exitPrice, termId);
                }
                else if (vpCard != null && vpCard.TradeTickSize > 0)
                {
                    double dirSign = vp.Direction == "BUY" ? 1 : -1;
                    double priceDiff = dirSign * (exitPrice - vp.PriceOpen);
                    double ticks = priceDiff / vpCard.TradeTickSize;
                    double tickValue = priceDiff >= 0 ? vpCard.TradeTickValueProfit : vpCard.TradeTickValueLoss;
                    if (tickValue <= 0) tickValue = vpCard.TradeTickValue;
                    unrealizedPnl = ticks * tickValue * vp.Volume;
                }

                termUnrealized += unrealizedPnl;

                var openTime = DateTime.TryParse(vp.OpenedAt, out var ot) ? ot : DateTime.UtcNow;
                var age = DateTime.UtcNow - openTime;

                result.Add(new
                {
                    terminal = termId,
                    ticket = vp.Ticket,
                    symbol = vp.Symbol,
                    dir = vp.Direction == "BUY" ? "LONG" : "SHORT",
                    lot = vp.Volume,
                    entry = vp.PriceOpen,
                    sl = vp.SL,
                    tp = vp.TP,
                    current,
                    pnl = Math.Round(unrealizedPnl, 2),
                    swap = 0.0,
                    commission = 0.0,
                    magic = vp.Magic,
                    age = FormatDuration(age),
                    openTime = vp.OpenedAt,
                    source = vp.Source ?? "virtual",
                    isVirtual = true
                });
            }
            virtualUnrealizedAccum[termId] = termUnrealized;
        }

        // Update cached virtual unrealized for use by terminal tiles
        foreach (var kvp in virtualUnrealizedAccum)
            _cachedVirtualUnrealized[kvp.Key] = kvp.Value;

        return new { cmd = "positions", data = result };
    }

 // -------------------------------------------------------------------
 // get_strategies  â€” discovered + running
 // -------------------------------------------------------------------

    private object HandleGetStrategies()
    {
        var infos = _strategyMgr.GetStrategyInfos();
        var registry = _state.GetRegisteredStrategies().ToDictionary(r => r.Name);

        var discovered = _strategyMgr.DiscoverStrategies().Select(name =>
        {
            var reg = registry.TryGetValue(name, out var r) ? r : null;
            return new
            {
                name,
                enabled = reg?.Enabled ?? false,
                magic_base = reg?.MagicBase ?? 0,
                discovered_at = reg?.DiscoveredAt
            };
        }).ToList();

        var running = infos
            .Where(i => i.Status == "running" || i.Status == "connecting")
            .Select(i => new
            {
                name = i.Name,
                terminal = i.Terminal,
                status = i.Status,
                magic = i.Magic,
                port = i.Port
            }).ToList();

        return new { cmd = "strategies", discovered, running };
    }

 // -------------------------------------------------------------------
 // get_events  â€” event log with optional filters
 // -------------------------------------------------------------------

    private object HandleGetEvents(JsonElement root)
    {
        string? filterType = null;
        string? filterTerminal = null;
        int limit = 100;

        if (root.TryGetProperty("filter", out var filterProp))
        {
            if (filterProp.TryGetProperty("type", out var tProp))
                filterType = tProp.GetString();
            if (filterProp.TryGetProperty("terminal", out var termProp))
                filterTerminal = termProp.GetString();
            if (filterProp.TryGetProperty("limit", out var limProp))
                limit = limProp.GetInt32();
        }

        var events = _state.GetEvents(filterTerminal, filterType, limit);
        var data = events.Select(e => new
        {
            id = e.Id,
            time = e.Timestamp,
            terminal = e.TerminalId ?? "SYSTEM",
            type = e.Type,
            strategy = e.Strategy,
            msg = e.Message
        }).ToList();

        return new { cmd = "events", data };
    }

 // -------------------------------------------------------------------
 // start_strategy / stop_strategy
 // -------------------------------------------------------------------

    private async Task<object> HandleStartStrategy(JsonElement root, CancellationToken ct)
    {
        var strategy = root.GetProperty("strategy").GetString()!;
        var terminal = root.GetProperty("terminal").GetString()!;

        var (ok, error) = await _strategyMgr.StartStrategyAsync(strategy, terminal, ct);

        if (ok)
        {
            await BroadcastAsync(new
            {
                @event = "strategy_status",
                data = new { name = strategy, terminal, status = "running" }
            });

            _ = _alerts.SendAsync("INFO", terminal,
                $"Strategy {strategy} started from dashboard",
                strategy: strategy, bypassDebounce: true);

            _state.LogEvent("AUDIT", terminal, strategy, $"start_strategy: {strategy}");
        }

        return new { cmd = "start_strategy", ok, strategy, terminal, error };
    }

    private async Task<object> HandleStopStrategy(JsonElement root, CancellationToken ct)
    {
        var strategy = root.GetProperty("strategy").GetString()!;
        var terminal = root.GetProperty("terminal").GetString()!;

        await _strategyMgr.StopStrategyAsync(strategy, terminal, ct);

        await BroadcastAsync(new
        {
            @event = "strategy_status",
            data = new { name = strategy, terminal, status = "stopped" }
        });

        _ = _alerts.SendAsync("INFO", terminal,
            $"Strategy {strategy} stopped from dashboard",
            strategy: strategy, bypassDebounce: true);

        _state.LogEvent("AUDIT", terminal, strategy, $"stop_strategy: {strategy}");

        return new { cmd = "stop_strategy", ok = true, strategy, terminal };
    }

    // -------------------------------------------------------------------
    //  reload_strategy â€” stop â†’ restart Python process without daemon restart
    // -------------------------------------------------------------------

    private async Task<object> HandleReloadStrategy(JsonElement root, CancellationToken ct)
    {
        var strategy = root.GetProperty("strategy").GetString()!;
        var terminal = root.GetProperty("terminal").GetString()!;

        var process = _strategyMgr.GetProcess(strategy, terminal);
        if (process == null || !process.IsRunning)
            return new { cmd = "reload_strategy", ok = false, error = $"Strategy {strategy}@{terminal} not running" };

        _log.Info($"[Dashboard] Reloading strategy {strategy}@{terminal}...");

        // 1. Stop
        await _strategyMgr.StopStrategyAsync(strategy, terminal, ct);

        await BroadcastAsync(new
        {
            @event = "strategy_status",
            data = new { name = strategy, terminal, status = "reloading" }
        });

        // Brief pause for process cleanup
        await Task.Delay(500, ct);

        // 2. Restart
        var (ok, _) = await _strategyMgr.StartStrategyAsync(strategy, terminal, ct);

        await BroadcastAsync(new
        {
            @event = "strategy_status",
            data = new { name = strategy, terminal, status = ok ? "running" : "error" }
        });

        _ = _alerts.SendAsync("INFO", terminal,
            $"Strategy {strategy} reloaded from dashboard (ok={ok})",
            strategy: strategy, bypassDebounce: true);

        _state.LogEvent("AUDIT", terminal, strategy, $"reload_strategy: ok={ok}");

        return new { cmd = "reload_strategy", ok, strategy, terminal };
    }

 // -------------------------------------------------------------------
 // enable_strategy / disable_strategy
 // -------------------------------------------------------------------

    private object HandleEnableStrategy(JsonElement root)
    {
        var strategy = root.GetProperty("strategy").GetString()!;

        _strategyMgr.EnableStrategy(strategy);
        _state.LogEvent("AUDIT", null, strategy, $"enable_strategy: {strategy}");
        _log.Info($"[Dashboard] Strategy {strategy} enabled");

        return new { cmd = "enable_strategy", ok = true, strategy };
    }

    private async Task<object> HandleDisableStrategy(JsonElement root, CancellationToken ct)
    {
        var strategy = root.GetProperty("strategy").GetString()!;

        await _strategyMgr.DisableStrategyAsync(strategy, ct);
        _state.LogEvent("AUDIT", null, strategy, $"disable_strategy: {strategy}");
        _log.Info($"[Dashboard] Strategy {strategy} disabled");

        return new { cmd = "disable_strategy", ok = true, strategy };
    }

 // -------------------------------------------------------------------
 // close_position / close_all / emergency_close_all
 // -------------------------------------------------------------------


    private async Task<object> HandleClosePosition(JsonElement root, CancellationToken ct)
    {
        var terminal = root.GetProperty("terminal").GetString()!;
        var ticket = root.GetProperty("ticket").GetInt64();

        // Phase 9.V: Virtual position (ticket < 0) â€” close via DB
        if (ticket < 0)
        {
            var vPos = _state.GetPositionByTicket(ticket, terminal);
            if (vPos == null)
                return new { cmd = "close_position", ok = false, error = $"Virtual position {ticket} not found" };

            // Get current price from BarsCache
            var tf = vPos.Timeframe ?? "H1";
            var bars = _barsCache?.GetBars(terminal, vPos.Symbol, tf);
            double closePrice = bars is { Count: > 0 } ? bars[^1].Close : vPos.PriceOpen;

            // Apply spread on SELL exit (closing SELL = buying at Ask)
            if (vPos.Direction == "SELL")
            {
                double spreadEst = vPos.Symbol.Contains("JPY") ? 0.03 : 0.00020;
                closePrice += spreadEst;
            }

            // P&L calculation
            double pnl = 0;
            if (_virtualTracker != null)
                pnl = _virtualTracker.CalculateVirtualPnl(vPos, closePrice, terminal);
            else
            {
                double contractSize = 100000;
                double priceDiff = vPos.Direction == "BUY" ? closePrice - vPos.PriceOpen : vPos.PriceOpen - closePrice;
                pnl = priceDiff * vPos.Volume * contractSize;
            }

            _state.ClosePosition(ticket, terminal, closePrice, "dashboard", pnl);
            _state.UpdateVirtualBalance(terminal, pnl);

            // Release virtual margin
            var card = await _connector.GetSymbolInfoAsync(terminal, vPos.Symbol, ct);
            if (card != null)
            {
                int effLeverage = _state.GetEffectiveLeverage(terminal, vPos.Symbol, 100);
                double marginRelease = vPos.Volume * (card.Margin1Lot > 0
                    ? card.Margin1Lot
                    : card.TradeContractSize * vPos.PriceOpen / effLeverage);
                _state.AddVirtualMargin(terminal, -marginRelease);
            }

            _state.LogEvent("AUDIT", terminal, null,
                $"Virtual close: ticket={ticket} {vPos.Symbol} pnl={pnl:F2}",
                JsonSerializer.Serialize(new { ticket, symbol = vPos.Symbol, pnl = Math.Round(pnl, 2) }));

            await BroadcastAsync(new
            {
                @event = "position_closed",
                data = new { terminal, ticket, reason = "dashboard", isVirtual = true }
            });

            return new { cmd = "close_position", ok = true, terminal, ticket, pnl = Math.Round(pnl, 2) };
        }

        if (!_connector.IsConnected(terminal))
            return new { cmd = "close_position", ok = false, error = "Terminal disconnected" };

 // Find the position in MT5
        var positions = await _connector.GetPositionsAsync(terminal, ct);
        var pos = positions?.FirstOrDefault(p => p.Ticket == ticket);
        if (pos == null)
            return new { cmd = "close_position", ok = false, error = $"Position {ticket} not found" };

 // Build close order: action=1 (DEAL), opposite type, position=ticket
        var orderReq = new Dictionary<string, object>
        {
            ["action"] = 1,
            ["symbol"] = pos.Symbol,
            ["volume"] = pos.Volume,
 ["type"] = pos.IsBuy ? 1 : 0, // opposite: BUY â€” SELL(1), SELL â€” BUY(0)
            ["position"] = ticket,
            ["magic"] = 0,
            ["comment"] = "D:dashboard:close",
            ["type_filling"] = 2
        };

        var result = await _connector.SendOrderAsync(terminal, orderReq, ct);

        if (result.IsOk)
        {
            _state.LogEvent("ORDER", terminal, null,
                $"Position {ticket} {pos.Symbol} closed from dashboard",
                JsonSerializer.Serialize(new { ticket, symbol = pos.Symbol, volume = pos.Volume }));

            _state.LogEvent("AUDIT", terminal, null,
                $"close_position: ticket={ticket} {pos.Symbol}",
                JsonSerializer.Serialize(new { action = "close_position", ticket, symbol = pos.Symbol, volume = pos.Volume }));

            await BroadcastAsync(new
            {
                @event = "position_closed",
                data = new { terminal, ticket, reason = "dashboard" }
            });
        }

        return new { cmd = "close_position", ok = result.IsOk, terminal, ticket };
    }

    private async Task<object> HandleCloseAll(JsonElement root, CancellationToken ct)
    {
        var terminal = root.GetProperty("terminal").GetString()!;
        int closed = 0;
        int virtualClosed = 0;

        // Phase 9.V: Close virtual positions first
        var openVirtual = _state.GetOpenVirtualPositions(terminal);
        foreach (var vp in openVirtual)
        {
            try
            {
                var tf = vp.Timeframe ?? "H1";
                var bars = _barsCache?.GetBars(terminal, vp.Symbol, tf);
                double closePrice = bars is { Count: > 0 } ? bars[^1].Close : vp.PriceOpen;

                if (vp.Direction == "SELL")
                {
                    double spreadEst = vp.Symbol.Contains("JPY") ? 0.03 : 0.00020;
                    closePrice += spreadEst;
                }

                double pnl = _virtualTracker != null
                    ? _virtualTracker.CalculateVirtualPnl(vp, closePrice, terminal)
                    : (vp.Direction == "BUY" ? closePrice - vp.PriceOpen : vp.PriceOpen - closePrice) * vp.Volume * 100000;

                _state.ClosePosition(vp.Ticket, terminal, closePrice, "close_all", pnl);
                _state.UpdateVirtualBalance(terminal, pnl);
                virtualClosed++;
            }
            catch (Exception ex) { _log.Error($"[Dashboard] Virtual close {vp.Ticket}: {ex.Message}"); }
        }
        if (virtualClosed > 0)
            _state.SetVirtualMargin(terminal, 0);

        // Close real positions via MT5
        if (_connector.IsConnected(terminal))
        {
            var positions = await _connector.GetPositionsAsync(terminal, ct);
            if (positions != null)
            {
                foreach (var pos in positions)
                {
                    try
                    {
                        var ok = await ClosePositionByOrderAsync(terminal, pos, ct);
                        if (ok) closed++;
                    }
                    catch (Exception ex)
                    {
                        _log.Error($"[Dashboard] Close {pos.Ticket}: {ex.Message}");
                    }
                }
            }
        }

        _state.LogEvent("ORDER", terminal, null,
            $"Close all: real={closed}, virtual={virtualClosed}");

        _state.LogEvent("AUDIT", terminal, null,
            $"close_all: real={closed}, virtual={virtualClosed}");

        _ = _alerts.SendAsync("YELLOW", terminal,
            $"Close all: {closed} real + {virtualClosed} virtual position(s) closed",
            bypassDebounce: true);

        return new { cmd = "close_all", ok = true, terminal, closed, virtualClosed, total = closed + virtualClosed };
    }

    private async Task<object> HandleEmergencyCloseAll(CancellationToken ct)
    {
        _log.Warn("[Dashboard] EMERGENCY CLOSE ALL triggered");

        int totalClosed = 0;
        var errors = new List<string>();

        foreach (var termId in _connector.GetAllTerminalIds())
        {
            if (!_connector.IsConnected(termId)) continue;

            try
            {
                var positions = await _connector.GetPositionsAsync(termId, ct);
                if (positions == null) continue;

                foreach (var pos in positions)
                {
                    try
                    {
                        var ok = await ClosePositionByOrderAsync(termId, pos, ct);
                        if (ok) totalClosed++;
                    }
                    catch (Exception ex) { errors.Add($"{termId}:{pos.Ticket} - {ex.Message}"); }
                }

 // Pause all strategies on this terminal
                var processes = _strategyMgr.GetProcessesForTerminal(termId);
                foreach (var p in processes)
                {
                    try { await _strategyMgr.StopStrategyAsync(p.StrategyName, termId, ct); }
                    catch { }
                }

 // Set mode to monitor
                var profile = _state.GetProfile(termId);
                if (profile != null)
                {
                    profile.Mode = "monitor";
                    _state.SaveProfile(profile);
                }
            }
            catch (Exception ex) { errors.Add($"{termId}: {ex.Message}"); }
        }

        _state.LogEvent("EMERGENCY", null, null,
            $"EMERGENCY CLOSE ALL: {totalClosed} position(s) closed",
            JsonSerializer.Serialize(new { totalClosed, errors }));

        _state.LogEvent("AUDIT", null, null,
            $"emergency_close_all: {totalClosed} position(s) closed",
            JsonSerializer.Serialize(new { action = "emergency_close_all", totalClosed, errors }));

        _ = _alerts.SendAsync("EMERGENCY", "ALL",
            $"ðŸš¨ EMERGENCY CLOSE ALL: {totalClosed} position(s) closed",
            bypassDebounce: true);

        await BroadcastAsync(new
        {
            @event = "emergency_close_all",
            data = new { totalClosed, errors }
        });

        return new { cmd = "emergency_close_all", ok = true, totalClosed, errors };
    }

 // -------------------------------------------------------------------
 // Helper: close a single position via SendOrderAsync
 // -------------------------------------------------------------------

    private async Task<bool> ClosePositionByOrderAsync(string terminalId, Position pos, CancellationToken ct)
    {
        var orderReq = new Dictionary<string, object>
        {
 ["action"] = 1, // TRADE_ACTION_DEAL
            ["symbol"] = pos.Symbol,
            ["volume"] = pos.Volume,
 ["type"] = pos.IsBuy ? 1 : 0, // opposite: BUY â€” SELL(1), SELL â€” BUY(0)
            ["position"] = pos.Ticket,
            ["magic"] = 0,
            ["comment"] = "D:dashboard:close",
            ["type_filling"] = 2
        };

        var result = await _connector.SendOrderAsync(terminalId, orderReq, ct);
        return result.IsOk;
    }

 // -------------------------------------------------------------------
 // save_profile  â€” update terminal risk settings
 // -------------------------------------------------------------------


    private async Task<object> HandleTogglePause(JsonElement root)
    {
        if (_riskManager == null)
            return new { cmd = "toggle_pause", ok = false, error = "RiskManager not available" };

        var (currentlyPaused, _, _) = _riskManager.GetPauseState();

        // If currently paused → resume; otherwise → pause
        bool newPaused = !currentlyPaused;

        int durationMin = 0;
        string? reason = null;

        if (newPaused)
        {
            if (root.TryGetProperty("duration_min", out var durProp))
                durationMin = durProp.GetInt32();
            if (root.TryGetProperty("reason", out var reasonProp))
                reason = reasonProp.GetString();
        }

        _riskManager.SetGlobalPause(newPaused, durationMin, reason);

        // Log event
        var (p, u, r) = _riskManager.GetPauseState();
        _state.LogEvent(newPaused ? "RISK" : "INFO", null, null,
            newPaused
                ? $"Global PAUSE activated" + (durationMin > 0 ? $" for {durationMin}m" : "") + (reason != null ? $" — {reason}" : "")
                : "Global PAUSE lifted — trading resumed");

        // Alert via Telegram
        if (_alerts != null)
        {
            var emoji = newPaused ? "\u23f8\ufe0f" : "\u25b6\ufe0f";
            var msg = newPaused
                ? $"{emoji} <b>Trading PAUSED</b>" + (durationMin > 0 ? $"\nDuration: {durationMin} minutes" : "\nIndefinite") + (reason != null ? $"\nReason: {reason}" : "")
                : $"{emoji} <b>Trading RESUMED</b>";
            _ = _alerts.SendAsync("RISK", "", msg, bypassDebounce: true);
        }

        // Broadcast updated state to all WS clients
        await BroadcastAsync(new
        {
            @event = "pause_state",
            paused = p,
            pauseUntil = u?.ToString("o"),
            pauseReason = r
        });

        return new { cmd = "toggle_pause", ok = true, paused = p,
            pauseUntil = u?.ToString("o"), pauseReason = r };
    }

    private object HandleGetPauseState()
    {
        if (_riskManager == null)
            return new { cmd = "pause_state", paused = false };

        var (p, u, r) = _riskManager.GetPauseState();
        return new { cmd = "pause_state", paused = p,
            pauseUntil = u?.ToString("o"), pauseReason = r };
    }

 // -------------------------------------------------------------------
 // shutdown_daemon  â€” " graceful daemon shutdown from dashboard
 // -------------------------------------------------------------------


    private async Task<object> HandleShutdownDaemon(JsonElement root, CancellationToken ct)
    {
        bool killTerminals = false;
        if (root.TryGetProperty("kill_terminals", out var kt))
            killTerminals = kt.GetBoolean();

        _log.Warn($"[Dashboard] SHUTDOWN requested (kill_terminals={killTerminals})");

 // Write clean shutdown flag â€” absence on next start means crash
        try
        {
            var flagPath = Path.Combine(AppContext.BaseDirectory, "Data", ".clean_shutdown");
            File.WriteAllText(flagPath, DateTime.UtcNow.ToString("o"));
        }
        catch { }

        _state.LogEvent("SYSTEM", null, null,
            $"Daemon shutdown requested from dashboard (kill_terminals={killTerminals})");

        _state.LogEvent("AUDIT", null, null,
            $"shutdown_daemon: kill_terminals={killTerminals}");

 // Send Telegram alert
        _ = _alerts.SendAsync("SYSTEM", "daemon",
            $"Daemon shutdown requested from dashboard" +
            (killTerminals ? " (terminals will be closed)" : ""),
            bypassDebounce: true);

 // Broadcast shutdown event to all WS clients
        await BroadcastAsync(new
        {
            @event = "daemon_shutdown",
            data = new { killTerminals }
        });

 // Kill MT5 terminal processes if requested
        if (killTerminals)
        {
            foreach (var termConfig in _config.Terminals)
            {
                if (string.IsNullOrEmpty(termConfig.TerminalPath)) continue;
                try
                {
                    var exeName = Path.GetFileNameWithoutExtension(termConfig.TerminalPath);
                    var processes = System.Diagnostics.Process.GetProcessesByName(exeName);
                    foreach (var proc in processes)
                    {
                        try
                        {
 // Match by path (case-insensitive)
                            if (proc.MainModule?.FileName?.Equals(termConfig.TerminalPath,
                                StringComparison.OrdinalIgnoreCase) == true)
                            {
                                _log.Info($"[Shutdown] Closing terminal: {termConfig.Id} (PID {proc.Id})");
 proc.CloseMainWindow(); // Graceful close
                                if (!proc.WaitForExit(5000))
                                {
                                    _log.Warn($"[Shutdown] Force-killing: {termConfig.Id}");
                                    proc.Kill();
                                }
                            }
                        }
                        catch { }
                        finally { proc.Dispose(); }
                    }
                }
                catch (Exception ex)
                {
                    _log.Error($"[Shutdown] Failed to close terminal {termConfig.Id}: {ex.Message}");
                }
            }
        }

 // Signal main loop to stop (deferred to let response be sent)
        _ = Task.Run(async () =>
        {
 await Task.Delay(500); // Let WS response reach the client
            OnShutdownRequested?.Invoke(killTerminals);
        });

        return new { cmd = "shutdown_daemon", ok = true, killTerminals };
    }

 // ===================================================================
 // Sizing â€” per-symbol risk management
 // ===================================================================


    private async Task<object> HandleGetSizing(JsonElement root, CancellationToken ct)
    {
        var terminal = root.GetProperty("terminal").GetString()!;

 // Load sizing from DB
        var sizingList = _state.GetAllSymbolSizing(terminal);

 // Get account info
        AccountInfo? acc = null;
        if (_connector.IsConnected(terminal))
        {
            try { acc = await _connector.GetAccountInfoAsync(terminal, ct); }
            catch { }
        }

        double balance = acc?.Balance ?? 0;

 // Check symbol availability on terminal via CHECK_SYMBOLS command
        HashSet<string>? availableSymbols = null;
        if (_connector.IsConnected(terminal) && sizingList.Count > 0)
        {
            try
            {
                var allSyms = sizingList.Select(s => s.Symbol).ToList();
                var (resolved, missing) = await _connector.CheckSymbolsAsync(terminal, allSyms, ct);
                availableSymbols = new HashSet<string>(resolved.Keys, StringComparer.OrdinalIgnoreCase);
            }
            catch (Exception ex)
            {
                _log.Error($"[Sizing] CHECK_SYMBOLS failed for {terminal}: {ex.Message}");
                // availableSymbols stays null = assume all available
            }
        }

 // Build preview for each symbol
        var rows = new List<object>();
        double totalMargin = 0;
        int enabledCount = 0;

        foreach (var s in sizingList)
        {
            double atr = 0, estSl = 0, estLot = 0, estMargin = 0;
            string tf = "";
            bool available = availableSymbols?.Contains(s.Symbol) ?? true;

 // Try to get live preview data
            if (available && _barsCache != null && _connector.IsConnected(terminal))
            {
 // Find timeframe from strategy config - check running processes
                var processes = _strategyMgr.GetProcessesForTerminal(terminal);
                foreach (var proc in processes)
                {
                    tf = proc.Requirements?.Timeframes?.GetValueOrDefault(s.Symbol, "") ?? "";
                    if (!string.IsNullOrEmpty(tf)) break;
                }

                if (!string.IsNullOrEmpty(tf))
                {
                    var bars = _barsCache.GetBars(terminal, s.Symbol, tf);
                    if (bars is { Count: >= 15 })
                    {
                        atr = ComputeAtr14(bars);
 // Estimate SL as ATR * 1.5 (conservative default)
 estSl = atr * 1.5;

                        if (estSl > 0 && balance > 0)
                        {
 // Risk = profile base risk × symbol factor
 var sizingProfile = _state.GetProfile(terminal);
 double baseRisk = sizingProfile != null
     ? Engine.LotCalculator.GetRiskMoney(sizingProfile, balance)
     : balance * 0.01; // fallback 1%
 double riskMoney = baseRisk * s.RiskFactor;
                            try
                            {
                                var card = await _connector.GetSymbolInfoAsync(terminal, s.Symbol, ct);
                                if (card != null)
                                {
                                    double currentPrice = bars[^1].Close;
 double slPrice = currentPrice - estSl; // approximate
                                    var lot = Engine.LotCalculator.Calculate(currentPrice, slPrice, riskMoney, card);
                                    if (lot.Allowed)
                                    {
                                        estLot = lot.Lot;
 // margin_1lot = exact margin for 1.0 lot from MT5's order_calc_margin
                                        double m1 = card.Margin1Lot > 0 ? card.Margin1Lot
 : (s.MarginInitial ?? 0); // fallback to cached
 estMargin = m1 * estLot;
                                        if (s.Enabled) totalMargin += estMargin;
                                    }

 // Cache margin_1lot for offline display
                                    if (card.Margin1Lot > 0 && Math.Abs(card.Margin1Lot - (s.MarginInitial ?? 0)) > 0.01)
                                        _state.UpdateSymbolMargin(terminal, s.Symbol, card.Margin1Lot);
                                }
                            }
                            catch { }
                        }
                    }
                }
            }

            if (s.Enabled && available) enabledCount++;

            rows.Add(new
            {
                symbol = s.Symbol,
                tf,
                assetClass = s.AssetClass,
                tier = s.Tier,
                enabled = s.Enabled,
                available,
                riskFactor = s.RiskFactor,
                maxLot = s.MaxLot,
                atr = Math.Round(atr, 6),
                estSl = Math.Round(estSl, 6),
                estLot = Math.Round(estLot, 2),
                estMargin = Math.Round(estMargin, 2),
                notes = s.Notes,
            });
        }

        // Calculate base risk from profile for display
        var sizingProfileForDisplay = _state.GetProfile(terminal);
        double baseRiskDisplay = sizingProfileForDisplay != null && balance > 0
            ? Engine.LotCalculator.GetRiskMoney(sizingProfileForDisplay, balance) : 0;

        return new
        {
            cmd = "sizing",
            terminal,
            balance = Math.Round(balance, 2),
            baseRisk = Math.Round(baseRiskDisplay, 2),
            riskType = sizingProfileForDisplay?.RiskType ?? "usd",
            riskValue = sizingProfileForDisplay?.MaxRiskTrade ?? 0,
            totalMargin = Math.Round(totalMargin, 2),
 totalMarginPct = balance > 0 ? Math.Round(totalMargin / balance * 100, 2) : 0,
            enabledCount,
            totalCount = sizingList.Count,
            rows
        };
    }

    private object HandleSaveSizing(JsonElement root)
    {
        var terminal = root.GetProperty("terminal").GetString()!;
        var symbol = root.GetProperty("symbol").GetString()!;

        var existing = _state.GetSymbolSizing(terminal, symbol);
        if (existing == null)
            return new { cmd = "save_sizing", ok = false, error = "Symbol not found in sizing" };

        if (root.TryGetProperty("enabled", out var ep))
            existing.Enabled = ep.GetBoolean();
        if (root.TryGetProperty("risk_factor", out var rf))
        {
            existing.RiskFactor = Math.Clamp(rf.GetDouble(), 0.0, 1.0);
            // Factor = 0 means effectively disabled
            if (existing.RiskFactor <= 0) existing.Enabled = false;
        }
        if (root.TryGetProperty("max_lot", out var ml))
            existing.MaxLot = ml.ValueKind == JsonValueKind.Null ? null : ml.GetDouble();
        if (root.TryGetProperty("notes", out var np))
            existing.Notes = np.ValueKind == JsonValueKind.Null ? null : np.GetString();

        _state.SaveSymbolSizing(existing);
        _log.Info($"[Sizing] Updated {symbol}@{terminal}: enabled={existing.Enabled}, factor={existing.RiskFactor:F2}");

        return new { cmd = "save_sizing", ok = true, symbol, terminal };
    }

    private async Task<object> HandleInitSizing(JsonElement root, CancellationToken ct)
    {
        var terminal = root.GetProperty("terminal").GetString()!;
        var strategy = root.GetProperty("strategy").GetString()!;

        var (symbolDefaults, error) = await ParseStrategyConfigForSizing(strategy, ct);
        if (error != null)
            return new { cmd = "init_sizing", ok = false, error };

        int created = _state.InitSymbolSizingDefaults(terminal, symbolDefaults);
        _log.Info($"[Sizing] Init {strategy}@{terminal}: {created} upserted, {symbolDefaults.Count} total");

        return new { cmd = "init_sizing", ok = true, created, total = symbolDefaults.Count };
    }

    private async Task<object> HandleResetSizing(JsonElement root, CancellationToken ct)
    {
        var terminal = root.GetProperty("terminal").GetString()!;
        var strategy = root.GetProperty("strategy").GetString()!;

        var (symbolDefaults, error) = await ParseStrategyConfigForSizing(strategy, ct);
        if (error != null)
            return new { cmd = "reset_sizing", ok = false, error };

        // Reset only risk_factor, keep enabled/notes/max_lot
        var factors = symbolDefaults.ToDictionary(kv => kv.Key, kv => kv.Value.RiskFactor);
        int updated = _state.ResetSizingFactors(terminal, factors);
        _log.Info($"[Sizing] Reset {strategy}@{terminal}: {updated} factors restored");

        return new { cmd = "reset_sizing", ok = true, updated, total = symbolDefaults.Count };
    }

    /// <summary>Parse strategy config.json and extract symbol defaults for sizing.</summary>
    private async Task<(Dictionary<string, (double RiskFactor, string AssetClass, string Tier)>, string?)>
        ParseStrategyConfigForSizing(string strategy, CancellationToken ct)
    {
        var symbolDefaults = new Dictionary<string, (double RiskFactor, string AssetClass, string Tier)>();

        var strategyDir = Path.GetFullPath(strategy,
            Path.GetFullPath(_config.StrategyDir, AppContext.BaseDirectory));
        var configPath = Path.Combine(strategyDir, "config.json");
        if (!File.Exists(configPath))
            return (symbolDefaults, $"Config not found: {configPath}");

        var configJson = await File.ReadAllTextAsync(configPath, ct);
        var configDoc = JsonDocument.Parse(configJson);

        if (configDoc.RootElement.TryGetProperty("symbols", out var symbolsProp))
        {
            // bb_mr_v2 format: { "symbols": { "GBPCAD": { "asset_class": "forex", ... } } }
            foreach (var sym in symbolsProp.EnumerateObject())
            {
                string aclass = "forex";
                if (sym.Value.TryGetProperty("asset_class", out var aclassProp))
                    aclass = aclassProp.GetString() ?? "forex";
                symbolDefaults[sym.Name] = (1.0, aclass, "T1");
            }
        }
        else if (configDoc.RootElement.TryGetProperty("combos", out var combosProp))
        {
            foreach (var combo in combosProp.EnumerateArray())
            {
                var sym = combo.GetProperty("sym").GetString()!;
                var aclass = combo.TryGetProperty("aclass", out var ac) ? ac.GetString() ?? "forex" : "forex";
                var tier = combo.TryGetProperty("tier", out var tp) ? tp.GetString() ?? "T1" : "T1";

                // New format: directions → daemon → size_r
                if (combo.TryGetProperty("directions", out var dirsProp))
                {
                    double maxSizeR = 0;
                    foreach (var dir in dirsProp.EnumerateObject())
                    {
                        if (dir.Value.TryGetProperty("daemon", out var daemon) &&
                            daemon.TryGetProperty("size_r", out var sr))
                        {
                            maxSizeR = Math.Max(maxSizeR, sr.GetDouble());
                        }
                    }
                    symbolDefaults[sym] = (maxSizeR > 0 ? maxSizeR : 1.0, aclass, tier);
                }
                // Old flat format: size_r directly on combo
                else if (combo.TryGetProperty("size_r", out var sizeR))
                {
                    double sr = sizeR.GetDouble();
                    // For old format with multiple combos per symbol (LONG+SHORT), take max
                    if (symbolDefaults.TryGetValue(sym, out var existing))
                        sr = Math.Max(existing.RiskFactor, sr);
                    symbolDefaults[sym] = (sr, aclass, tier);
                }
                else
                {
                    if (!symbolDefaults.ContainsKey(sym))
                        symbolDefaults[sym] = (1.0, aclass, tier);
                }
            }
        }
        else if (configDoc.RootElement.TryGetProperty("pairs", out var pairsProp))
        {
            // Pairs z-score format
            foreach (var pair in pairsProp.EnumerateArray())
            {
                var symA = pair.TryGetProperty("symA", out var a) ? a.GetString() : null;
                var symB = pair.TryGetProperty("symB", out var b) ? b.GetString() : null;
                var aclass = pair.TryGetProperty("aclass", out var ac) ? ac.GetString() ?? "forex" : "forex";
                var tier = pair.TryGetProperty("tier", out var tp) ? tp.GetString() ?? "T1" : "T1";
                var sr = pair.TryGetProperty("daemon", out var daemon) &&
                         daemon.TryGetProperty("size_r", out var srp) ? srp.GetDouble() : 1.0;
                // Fallback: old flat size_r on pair
                if (sr == 1.0 && pair.TryGetProperty("size_r", out var oldSr))
                    sr = oldSr.GetDouble();

                if (symA != null && !symbolDefaults.ContainsKey(symA))
                    symbolDefaults[symA] = (sr, aclass, tier);
                if (symB != null && !symbolDefaults.ContainsKey(symB))
                    symbolDefaults[symB] = (sr, aclass, tier);
            }
        }

        if (symbolDefaults.Count == 0)
            return (symbolDefaults, "No symbols found in strategy config");

        return (symbolDefaults, null);
    }

 /// <summary>Simple ATR(14) from bar list using Wilder's smoothing.</summary>
    private static double ComputeAtr14(List<Bar> bars, int period = 14)
    {
        if (bars.Count < period + 1) return 0;

 // True Range
        var tr = new double[bars.Count];
        tr[0] = bars[0].High - bars[0].Low;
        for (int i = 1; i < bars.Count; i++)
        {
            double hl = bars[i].High - bars[i].Low;
            double hc = Math.Abs(bars[i].High - bars[i - 1].Close);
            double lc = Math.Abs(bars[i].Low - bars[i - 1].Close);
            tr[i] = Math.Max(hl, Math.Max(hc, lc));
        }

 // Wilder's smoothing
        double atr = 0;
        for (int i = 0; i < period; i++) atr += tr[i];
        atr /= period;

        for (int i = period; i < bars.Count; i++)
 atr = (atr * (period - 1) + tr[i]) / period;

        return atr;
    }

    // -------------------------------------------------------------------
    // get_pending_orders — pending (stop) orders across all terminals
    // -------------------------------------------------------------------

    private object HandleGetPendingOrders(JsonElement root)
    {
        string? filterTerminal = null;
        if (root.TryGetProperty("terminal", out var termProp))
            filterTerminal = termProp.GetString();

        var terminalIds = filterTerminal != null
            ? new List<string> { filterTerminal }
            : _config.Terminals.Select(t => t.Id).ToList();

        var result = new List<object>();

        foreach (var termId in terminalIds)
        {
            var pending = _state.GetOpenPendingOrders(termId);
            foreach (var rec in pending)
            {
                // Current price for reference (from BarsCache)
                double current = 0;
                if (_barsCache != null)
                {
                    foreach (var tf in new[] { "M5", "M15", "M30", "H1", "H4", "D1" })
                    {
                        var bars = _barsCache.GetBars(termId, rec.Symbol, tf);
                        if (bars is { Count: > 0 }) { current = bars[^1].Close; break; }
                    }
                }

                string expiry = rec.BarsRemaining < 0
                    ? "GTC"
                    : rec.BarsRemaining.ToString();

                result.Add(new
                {
                    terminal     = termId,
                    ticket       = rec.Ticket,
                    symbol       = rec.Symbol,
                    dir          = rec.Direction == "BUY" ? "LONG" : "SHORT",
                    order_type   = rec.OrderType,
                    lot          = rec.Volume,
                    entry        = rec.EntryPrice,
                    current,
                    sl           = rec.SL,
                    expiry,
                    strategy     = rec.Strategy,
                    is_virtual   = rec.IsVirtual,
                    placed_at    = rec.PlacedAt,
                });
            }
        }

        return new { pending_orders = result };
    }

}
