using System.Text.Json;
using Daemon.Config;
using Daemon.Connector;
using Daemon.Engine;
using Daemon.Models;

namespace Daemon.Strategy;

/// <summary>
/// The Scheduler is the heart of the trading engine.
///
/// Every N seconds (default 10):
///   For each running strategy:
///     1. Fetch latest bars for all strategy symbols from MT5 (via ConnectorManager)
///     2. Update BarsCache (detect new candle closes)
///     3. If any new candle closed â†’ build TICK message â†’ send to strategy
///     4. Receive ACTIONS from strategy
///     5. For each action:
///        ENTER      â†’ LotCalculator â†’ RiskManager (11 gates) â†’ MT5 or Virtual
///        EXIT       â†’ MT5 or Virtual
///        MODIFY_SL  â†’ MT5 or Virtual + SL history (universal)
///
/// The Scheduler does NOT own the timer â€” Program.cs owns the timer
/// and calls Scheduler.TickAsync() periodically.
/// </summary>
public class Scheduler
{
    private readonly DaemonConfig _config;
    private readonly ConnectorManager _connector;
    private readonly StrategyManager _strategies;
    private readonly Engine.StateManager _state;
    private readonly Engine.RiskManager _risk;
    private readonly Engine.BarsCache _barsCache;
    private readonly Engine.AlertService _alerts;
    private readonly ILogger _log;

    private long _cycleCounter;

    /// <summary>
    /// Tracks last daemonâ†’runner keepalive time per strategy key.
    /// Used to send periodic heartbeats between candles (prevents connection going stale
    /// and detects dead runners without waiting for the next candle).
    /// </summary>
    private readonly Dictionary<string, DateTime> _lastKeepAlive = new();
    private readonly Dictionary<string, string> _lastBrokerDates = new();

    /// <summary>Set after construction (VirtualTracker is created later).</summary>
    private VirtualTracker? _virtualTracker;
    public void SetVirtualTracker(VirtualTracker vt) => _virtualTracker = vt;

    /// <summary>Set after construction (PendingOrderManager is created later).</summary>
    private PendingOrderManager? _pendingMgr;
    public void SetPendingOrderManager(PendingOrderManager pm) => _pendingMgr = pm;

    /// <summary>Interval between daemon-initiated heartbeats when no new candle.</summary>
    private static readonly TimeSpan KeepAliveInterval = TimeSpan.FromSeconds(120);

    public Scheduler(
        DaemonConfig config,
        ConnectorManager connector,
        StrategyManager strategies,
        Engine.StateManager state,
        Engine.RiskManager risk,
        Engine.BarsCache barsCache,
        Engine.AlertService alerts,
        ILogger log)
    {
        _config = config;
        _connector = connector;
        _strategies = strategies;
        _state = state;
        _risk = risk;
        _barsCache = barsCache;
        _alerts = alerts;
        _log = log;
    }

    /// <summary>
    /// Main scheduler cycle. Called every scheduler_interval_sec by the engine timer.
    /// </summary>
    public async Task TickAsync(CancellationToken ct = default)
    {
        Interlocked.Increment(ref _cycleCounter);

        foreach (var process in _strategies.GetAllProcesses())
        {
            if (process.Status != StrategyStatus.Running)
                continue;

            if (!_connector.IsConnected(process.TerminalId))
                continue;

            if (process.Requirements == null)
                continue;

            try
            {
                await ProcessStrategyTickAsync(process, ct);
            }
            catch (Exception ex)
            {
                _log.Error($"[Scheduler] Error processing {process.StrategyName}@{process.TerminalId}: {ex.Message}");
            }
        }
    }

    /// <summary>Process a single scheduler tick for one strategy.</summary>
    private async Task ProcessStrategyTickAsync(StrategyProcess process, CancellationToken ct)
    {
        var terminalId = process.TerminalId;
        var reqs = process.Requirements!;
        var tag = $"[{process.StrategyName}@{terminalId}]";

        bool anyNewCandle = false;
        bool needsWarmup = false;

        // 1. Check if any symbol needs initial warm-up
        foreach (var symbol in reqs.Symbols)
        {
            var tf = reqs.Timeframes.GetValueOrDefault(symbol, "H1");
            if (!_barsCache.IsWarmedUp(terminalId, symbol, tf)
                || _barsCache.NeedsDeeper(terminalId, symbol, tf, reqs.HistoryBars))
            {
                needsWarmup = true;
                break;
            }
        }

        // 2. Fetch bars and update cache
        if (needsWarmup)
        {
            _log.Info($"{tag} Warming up: loading {reqs.HistoryBars} bars for {reqs.Symbols.Count} symbols...");

            foreach (var symbol in reqs.Symbols)
            {
                var tf = reqs.Timeframes.GetValueOrDefault(symbol, "H1");
                try
                {
                    var bars = await _connector.GetRatesAsync(terminalId, symbol, tf, reqs.HistoryBars, ct);
                    _barsCache.LoadFull(terminalId, symbol, tf, bars, bars.Count);
                    if (bars.Count < reqs.HistoryBars)
                        _log.Warn($"{tag} Partial fill: got {bars.Count}/{reqs.HistoryBars} bars for {symbol} {tf} — will retry next cycle");
                    else
                        _log.Info($"{tag} Loaded {bars.Count} bars for {symbol} {tf}");
                }
                catch (Exception ex)
                {
                    _log.Warn($"{tag} Failed to load bars for {symbol}: {ex.Message}");
                }
            }

            anyNewCandle = true; // First tick = always send
        }
        else
        {
            // Incremental update (last 3 bars per symbol)
            foreach (var symbol in reqs.Symbols)
            {
                var tf = reqs.Timeframes.GetValueOrDefault(symbol, "H1");
                try
                {
                    var freshBars = await _connector.GetRatesAsync(terminalId, symbol, tf, 3, ct);
                    if (_barsCache.Update(terminalId, symbol, tf, freshBars))
                        anyNewCandle = true;
                }
                catch (Exception ex)
                {
                    _log.Warn($"{tag} Failed to update bars for {symbol}: {ex.Message}");
                }
            }
        }

        // 3. If no new candle â€” send periodic keepalive heartbeat, or skip
        if (!anyNewCandle)
        {
            // Send daemon-initiated heartbeat every KeepAliveInterval to keep
            // TCP connection alive and detect dead runner processes early.
            var processKey = $"{process.StrategyName}@{terminalId}";
            var lastKA = _lastKeepAlive.GetValueOrDefault(processKey, DateTime.MinValue);
            if (DateTime.UtcNow - lastKA >= KeepAliveInterval)
            {
                var alive = await process.SendHeartbeatAsync(ct);
                _lastKeepAlive[processKey] = DateTime.UtcNow;
                if (!alive)
                    _log.Warn($"{tag} Runner did not respond to heartbeat");
            }
            return;
        }

        // 4. Build TICK message
        var tick = new TickMessage { ServerTime = DateTimeOffset.UtcNow.ToUnixTimeSeconds() };

        foreach (var symbol in reqs.Symbols)
        {
            var tf = reqs.Timeframes.GetValueOrDefault(symbol, "H1");
            var cachedBars = _barsCache.GetBars(terminalId, symbol, tf);
            if (cachedBars != null)
            {
                tick.Bars[symbol] = cachedBars
                    .Select(ProtocolSerializer.ToBarData)
                    .ToList();
            }
        }

        // Positions filtered by magic range (supports multi-combo strategies)
        var allPositions = _state.GetOpenPositions(terminalId);
        int magicBase = process.Magic;
        tick.Positions = allPositions
            .Where(p => p.Magic >= magicBase && p.Magic < magicBase + 1000)
            .Select(pr => new PositionData
            {
                Ticket = pr.Ticket,
                Symbol = pr.Symbol,
                Direction = pr.Direction == "BUY" ? "LONG" : pr.Direction == "SELL" ? "SHORT" : pr.Direction,
                Volume = pr.Volume,
                PriceOpen = pr.PriceOpen,
                SL = pr.SL,
                TP = pr.TP,
                SignalData = pr.SignalData,
            })
            .ToList();

        // Pending orders filtered by magic range
        tick.PendingOrders = _state.GetOpenPendingOrders(terminalId)
            .Where(p => p.Magic >= magicBase && p.Magic < magicBase + 1000)
            .Select(p => new PendingOrderData
            {
                Ticket        = p.Ticket,
                Symbol        = p.Symbol,
                Direction     = p.Direction == "BUY" ? "LONG" : "SHORT",
                OrderType     = p.OrderType,
                Volume        = p.Volume,
                EntryPrice    = p.EntryPrice,
                SL            = p.SL,
                TP            = p.TP,
                BarsRemaining = p.BarsRemaining,
                SignalData    = p.SignalData,
            })
            .ToList();

        // Account equity
        try
        {
            var acc = await _connector.GetAccountInfoAsync(terminalId, ct);
            if (acc != null) tick.Equity = acc.Equity;
        }
        catch { }

        // 5. Send TICK, receive ACTIONS
        var actions = await process.SendTickAsync(tick, ct);
        if (actions == null || actions.Count == 0)
            return;

        // 5b. Warmup gate: first tick after history load is observation-only.
        //     Suppress ENTER actions to prevent stale-data signals (e.g. weekend startup).
        //     EXIT and MODIFY_SL still pass through for crash-recovery.
        if (!process.WarmupDone)
        {
            process.WarmupDone = true;
            var enterCount = actions.Count(a =>
                a.Action.Equals("ENTER", StringComparison.OrdinalIgnoreCase) ||
                a.Action.Equals("ENTER_PENDING", StringComparison.OrdinalIgnoreCase));
            if (enterCount > 0)
            {
                _log.Info($"{tag} Warmup tick: suppressed {enterCount} ENTER action(s)");
                actions = actions.Where(a =>
                    !a.Action.Equals("ENTER", StringComparison.OrdinalIgnoreCase) &&
                    !a.Action.Equals("ENTER_PENDING", StringComparison.OrdinalIgnoreCase)).ToList();
                if (actions.Count == 0) return;
            }
        }

        _log.Info($"{tag} Received {actions.Count} action(s)");

        // 6. Process each action
        foreach (var action in actions)
        {
            try
            {
                await ProcessActionAsync(process, action, ct);
            }
            catch (Exception ex)
            {
                _log.Error($"{tag} Action error ({action.Action} {action.Symbol}): {ex.Message}");
                _state.LogEvent("ERROR", terminalId, process.StrategyName,
                    $"Action failed: {action.Action} {action.Symbol}: {ex.Message}");
            }
        }
    }

    private async Task ProcessActionAsync(StrategyProcess process, StrategyAction action,
                                            CancellationToken ct)
    {
        switch (action.Action.ToUpperInvariant())
        {
            case "ENTER":
                await HandleEnterAsync(process, action, ct);
                break;
            case "ENTER_PENDING":
                await HandleEnterPendingAsync(process, action, ct);
                break;
            case "EXIT":
                await HandleExitAsync(process, action, ct);
                break;
            case "MODIFY_SL":
                await HandleModifySLAsync(process, action, ct);
                break;
            default:
                _log.Warn($"[{process.StrategyName}] Unknown action: {action.Action}");
                break;
        }
    }

    // -----------------------------------------------------------------------
    //  ENTER
    // -----------------------------------------------------------------------

    private async Task HandleEnterAsync(StrategyProcess process, StrategyAction action,
                                          CancellationToken ct)
    {
        var terminalId = process.TerminalId;
        var tag = $"[{process.StrategyName}@{terminalId}]";
        var symbol = action.Symbol!;
        var direction = action.Direction!.ToUpperInvariant();
        var slPrice = action.SlPrice ?? 0;

        _log.Info($"{tag} ENTER signal: {direction} {symbol} SL={slPrice}");
        _state.LogEvent("SIGNAL", terminalId, process.StrategyName,
            $"ENTER {direction} {symbol} SL={slPrice}");
        _ = _alerts.SendAsync("SIGNAL", terminalId,
            $"\u26a1 SIGNAL: {direction} {symbol} SL={slPrice}",
            process.StrategyName);
        _alerts.NotifySignalSent();

        // Terminal profile
        var profile = _state.GetProfile(terminalId);
        if (profile == null) { _log.Error($"{tag} No terminal profile"); return; }

        // Instrument card
        var card = await _connector.GetSymbolInfoAsync(terminalId, symbol, ct);
        if (card == null) { _log.Error($"{tag} No card for {symbol}"); return; }

        // Account info
        var acc = await _connector.GetAccountInfoAsync(terminalId, ct);
        if (acc == null) { _log.Error($"{tag} No account info"); return; }

        // Symbol sizing (per-symbol risk override)
        var sizing = _state.GetSymbolSizing(terminalId, symbol);
        if (sizing != null && !sizing.Enabled)
        {
            _log.Info($"{tag} Symbol {symbol} disabled in sizing config");
            _ = _alerts.SendAsync("RISK", terminalId,
                $"\U0001f6ab BLOCKED {direction} {symbol}: symbol disabled in sizing",
                process.StrategyName);
            _alerts.NotifySignalSent();
            return;
        }

        // Calculate risk money: base risk from profile × per-symbol factor from sizing
        double baseRisk = Engine.LotCalculator.GetRiskMoney(profile, acc.Balance);
        double factor = sizing?.RiskFactor ?? 1.0;

        // Factor = 0 means symbol is effectively disabled
        if (factor <= 0)
        {
            _log.Info($"{tag} Symbol {symbol} risk_factor=0, skipping");
            return;
        }

        double riskMoney = baseRisk * factor;
        _log.Info($"{tag} Risk: base=${baseRisk:F2} × factor={factor:F2} -> ${riskMoney:F2}");

        // Current price from cached bars
        var tf = process.Requirements?.Timeframes.GetValueOrDefault(symbol, "H1") ?? "H1";
        var lastBars = _barsCache.GetBars(terminalId, symbol, tf);
        double currentPrice = lastBars is { Count: > 0 } ? lastBars[^1].Close : 0;
        if (currentPrice <= 0) { _log.Error($"{tag} No price for {symbol}"); return; }

        // Get authoritative loss-per-1-lot from MT5 (handles cross-pairs, JPY, metals, indices)
        double loss1Lot = 0;
        try
        {
            var profit = await _connector.CalcProfitAsync(
                terminalId, symbol, direction, 1.0, currentPrice, slPrice, ct);
            if (profit.HasValue && profit.Value != 0)
            {
                loss1Lot = Math.Abs(profit.Value);
                _log.Info($"{tag} CalcProfit: loss1Lot=${loss1Lot:F2} for {symbol} {direction}");
            }
        }
        catch (Exception ex)
        {
            _log.Warn($"{tag} CalcProfit failed for {symbol}: {ex.Message}, falling back to tick math");
        }

        // Lot calculation (prefer MT5 CalcProfit, fallback to tick math)
        var lotResult = Engine.LotCalculator.Calculate(
            entryPrice: currentPrice,
            slPrice: slPrice,
            riskMoney: riskMoney,
            card: card,
            loss1Lot: loss1Lot
        );

        // Apply max_lot cap if set in sizing
        if (lotResult.Allowed && sizing?.MaxLot != null && lotResult.Lot > sizing.MaxLot.Value)
        {
            _log.Info($"{tag} Lot capped by sizing: {lotResult.Lot:F2} -> {sizing.MaxLot.Value:F2}");
            lotResult.Lot = sizing.MaxLot.Value;
            lotResult.ActualRisk = loss1Lot > 0
                ? Math.Round(loss1Lot * lotResult.Lot, 2)
                : lotResult.Ticks * lotResult.TickValue * lotResult.Lot;
        }

        if (!lotResult.Allowed || lotResult.Lot <= 0)
        {
            _log.Warn($"{tag} LotCalc rejected: lot={lotResult.Lot:F2} reason={lotResult.Reason}");
            _ = _alerts.SendAsync("RISK", terminalId,
                $"\U0001f6ab SIZING REJECT {symbol}: {lotResult.Reason}",
                process.StrategyName);
            return;
        }

        _log.Info($"{tag} Lot: {lotResult.Lot:F2} risk=${lotResult.ActualRisk:F2} via={lotResult.CalcMethod}");

        // Resolve combo magic (multi-combo strategies get unique magic per combo_key)
        int comboMagic = _state.ResolveComboMagic(
            process.StrategyName, process.Magic, action.SignalData);

        // Risk Manager â€” 11 gate check
        bool isBuy = direction == "LONG";
        var tradeReq = new Engine.TradeRequest
        {
            TerminalId = terminalId,
            Symbol = symbol,
            Direction = direction,
            EntryPrice = currentPrice,
            SlPrice = slPrice,
            Lot = lotResult.Lot,
            TradeRisk = lotResult.ActualRisk,
            AccountBalance = profile.Mode == "virtual"
                ? (_state.GetVirtualBalance(terminalId) ?? acc.Balance)
                : acc.Balance,
            Strategy = process.StrategyName,
            Magic = comboMagic,
            RCap = process.RCap,
            TerminalMagics = _state.GetOpenPositions(terminalId)
                .Select(p => p.Magic).Distinct().ToList(),
        };

        var gate = await _risk.CheckAsync(tradeReq, ct);

        if (!gate.Allowed)
        {
            _log.Warn($"{tag} Blocked by {gate.Gate}: {gate.Reason}");
            _state.LogEvent("RISK_BLOCK", terminalId, process.StrategyName,
                $"{gate.Gate}: {gate.Reason}",
                JsonSerializer.Serialize(new { symbol, direction, lot = lotResult.Lot, slPrice }));
            _ = _alerts.SendAsync("RISK", terminalId,
                $"\U0001f6ab BLOCKED {direction} {symbol}: {gate.Gate}",
                process.StrategyName);
            _alerts.NotifySignalSent();
            return;
        }

        // If G5 reduced the lot (margin reduce mode), sync lotResult with modified tradeReq
        if (gate.ReducedLot.HasValue)
        {
            _log.Info($"{tag} Margin reduce: lot {gate.OriginalLot:F2} \u2192 {gate.ReducedLot:F2}");
            lotResult.Lot = tradeReq.Lot;
            lotResult.ActualRisk = tradeReq.TradeRisk;
            _state.LogEvent("MARGIN_REDUCE", terminalId, process.StrategyName,
                gate.Reason ?? $"Lot reduced {gate.OriginalLot:F2} \u2192 {gate.ReducedLot:F2}",
                JsonSerializer.Serialize(new { symbol, direction, originalLot = gate.OriginalLot,
                    reducedLot = gate.ReducedLot, lot = lotResult.Lot, risk = lotResult.ActualRisk }));
            _ = _alerts.SendAsync("RISK", terminalId,
                $"\u26a0\ufe0f MARGIN REDUCE {direction} {symbol}: {gate.OriginalLot:F2}\u2192{gate.ReducedLot:F2} lot",
                process.StrategyName);
        }

        _log.Info($"{tag} Gates PASSED \u2192 {direction} {symbol} lot={lotResult.Lot:F2} SL={slPrice}");

        // Phase 9.V: Virtual mode â€” skip MT5, create virtual position
        if (profile.Mode == "virtual")
        {
            await HandleVirtualEnterAsync(process, symbol, direction, slPrice,
                                           lotResult, currentPrice, card, action, ct);
            return;
        }

        // Build MT5 order
        var orderReq = new Dictionary<string, object>
        {
            ["action"] = 1,                           // TRADE_ACTION_DEAL
            ["symbol"] = symbol,
            ["volume"] = lotResult.Lot,
            ["type"] = isBuy ? 0 : 1,                 // BUY=0, SELL=1
            ["sl"] = slPrice,
            ["tp"] = action.TpPrice ?? 0,
            ["magic"] = comboMagic,
            ["comment"] = $"D:{process.StrategyName}",
            ["type_filling"] = 2                       // IOC
        };

        var signalTime = DateTime.UtcNow;
        var result = await _connector.SendOrderAsync(terminalId, orderReq, ct);

        if (result.IsOk && result.Data != null)
        {
            var od = JsonSerializer.Deserialize<OrderResult>(result.Data.Value.GetRawText());
            if (od != null)
            {
                var fillTime = DateTime.UtcNow;
                _log.Info($"{tag} FILLED ticket={od.Order} @ {od.Price} vol={od.Volume}");

                _state.SavePosition(new Engine.PositionRecord
                {
                    Ticket = od.Order,
                    TerminalId = terminalId,
                    Symbol = symbol,
                    Direction = isBuy ? "BUY" : "SELL",
                    Volume = od.Volume,
                    PriceOpen = od.Price,
                    SL = slPrice,
                    TP = action.TpPrice ?? 0,
                    Magic = comboMagic,
                    Source = process.StrategyName,
                    SignalData = action.SignalData,
                    OpenedAt = DateTime.UtcNow.ToString("o")
                });

                _state.LogExecution(
                    ticket: od.Order,
                    terminalId: terminalId,
                    symbol: symbol,
                    direction: isBuy ? "LONG" : "SHORT",
                    signalPrice: currentPrice,
                    fillPrice: od.Price,
                    tickSize: card.TradeTickSize,
                    signalTime: signalTime,
                    fillTime: fillTime,
                    strategy: process.StrategyName
                );

                _state.LogEvent("ORDER", terminalId, process.StrategyName,
                    $"ENTER {direction} {symbol} lot={od.Volume:F2} @ {od.Price}",
                    JsonSerializer.Serialize(new { ticket = od.Order, slPrice }));

                _ = _alerts.SendAsync("ORDER", terminalId,
                    $"\u2705 {direction} {symbol} lot={od.Volume:F2} @ {od.Price}",
                    process.StrategyName);
            }
        }
        else
        {
            _log.Error($"{tag} FAILED: {result.Message} (code={result.Code})");
            _state.LogEvent("ORDER_FAIL", terminalId, process.StrategyName,
                $"ENTER {direction} {symbol} failed: {result.Message}");
        }
    }

    // -----------------------------------------------------------------------
    //  ENTER_PENDING
    // -----------------------------------------------------------------------

    private async Task HandleEnterPendingAsync(StrategyProcess process, StrategyAction action,
                                                CancellationToken ct)
    {
        var terminalId = process.TerminalId;
        var tag = $"[{process.StrategyName}@{terminalId}]";
        var symbol     = action.Symbol!;
        var direction  = action.Direction!.ToUpperInvariant();
        var slPrice    = action.SlPrice ?? 0;
        var entryPrice = action.EntryPrice ?? 0;

        if (entryPrice <= 0)
        {
            _log.Warn($"{tag} ENTER_PENDING: entry_price missing or zero");
            return;
        }

        if (_pendingMgr == null)
        {
            _log.Warn($"{tag} ENTER_PENDING: PendingOrderManager not wired");
            return;
        }

        // Derive order type from direction vs current price
        var tf = process.Requirements?.Timeframes.GetValueOrDefault(symbol, "H1") ?? "H1";
        var lastBars = _barsCache.GetBars(terminalId, symbol, tf);
        double currentPrice = lastBars is { Count: > 0 } ? lastBars[^1].Close : 0;
        if (currentPrice <= 0) { _log.Error($"{tag} No price for {symbol}"); return; }

        string orderType;
        if (direction == "LONG" && entryPrice > currentPrice)
            orderType = "BUY_STOP";
        else if (direction == "SHORT" && entryPrice < currentPrice)
            orderType = "SELL_STOP";
        else
        {
            _log.Warn($"{tag} ENTER_PENDING: invalid direction/price combo " +
                      $"({direction} entry={entryPrice} current={currentPrice})");
            return;
        }

        _log.Info($"{tag} ENTER_PENDING signal: {orderType} {symbol} @ {entryPrice} SL={slPrice}");
        _state.LogEvent("SIGNAL", terminalId, process.StrategyName,
            $"ENTER_PENDING {orderType} {symbol} @ {entryPrice} SL={slPrice}");
        _ = _alerts.SendAsync("SIGNAL", terminalId,
            $"\u26a1 PENDING SIGNAL: {orderType} {symbol} @ {entryPrice}",
            process.StrategyName);
        _alerts.NotifySignalSent();

        var profile = _state.GetProfile(terminalId);
        if (profile == null) { _log.Error($"{tag} No terminal profile"); return; }

        var card = await _connector.GetSymbolInfoAsync(terminalId, symbol, ct);
        if (card == null) { _log.Error($"{tag} No card for {symbol}"); return; }

        var acc = await _connector.GetAccountInfoAsync(terminalId, ct);
        if (acc == null) { _log.Error($"{tag} No account info"); return; }

        var sizing = _state.GetSymbolSizing(terminalId, symbol);
        if (sizing != null && !sizing.Enabled)
        {
            _log.Info($"{tag} Symbol {symbol} disabled in sizing config");
            _ = _alerts.SendAsync("RISK", terminalId,
                $"\U0001f6ab BLOCKED {direction} {symbol}: symbol disabled", process.StrategyName);
            _alerts.NotifySignalSent();
            return;
        }

        double baseRisk  = Engine.LotCalculator.GetRiskMoney(profile, acc.Balance);
        double factor    = sizing?.RiskFactor ?? 1.0;
        if (factor <= 0) { _log.Info($"{tag} risk_factor=0, skipping"); return; }
        double riskMoney = baseRisk * factor;

        // Lot calc uses entry_price as fill reference
        double loss1Lot = 0;
        try
        {
            var profit = await _connector.CalcProfitAsync(
                terminalId, symbol, direction, 1.0, entryPrice, slPrice, ct);
            if (profit.HasValue && profit.Value != 0)
                loss1Lot = Math.Abs(profit.Value);
        }
        catch { }

        var lotResult = Engine.LotCalculator.Calculate(
            entryPrice: entryPrice,
            slPrice: slPrice,
            riskMoney: riskMoney,
            card: card,
            loss1Lot: loss1Lot);

        if (sizing?.MaxLot != null && lotResult.Allowed && lotResult.Lot > sizing.MaxLot.Value)
        {
            lotResult.Lot = sizing.MaxLot.Value;
            lotResult.ActualRisk = loss1Lot > 0
                ? Math.Round(loss1Lot * lotResult.Lot, 2)
                : lotResult.Ticks * lotResult.TickValue * lotResult.Lot;
        }

        if (!lotResult.Allowed || lotResult.Lot <= 0)
        {
            _log.Warn($"{tag} LotCalc rejected: {lotResult.Reason}");
            _ = _alerts.SendAsync("RISK", terminalId,
                $"\U0001f6ab SIZING REJECT {symbol}: {lotResult.Reason}", process.StrategyName);
            return;
        }

        int comboMagic = _state.ResolveComboMagic(
            process.StrategyName, process.Magic, action.SignalData);

        var tradeReq = new Engine.TradeRequest
        {
            TerminalId     = terminalId,
            Symbol         = symbol,
            Direction      = direction,
            EntryPrice     = entryPrice,
            SlPrice        = slPrice,
            Lot            = lotResult.Lot,
            TradeRisk      = lotResult.ActualRisk,
            AccountBalance = profile.Mode == "virtual"
                ? (_state.GetVirtualBalance(terminalId) ?? acc.Balance)
                : acc.Balance,
            Strategy       = process.StrategyName,
            Magic          = comboMagic,
            RCap           = process.RCap,
            TerminalMagics = _state.GetOpenPositions(terminalId)
                .Select(p => p.Magic).Distinct().ToList(),
        };

        var gate = await _risk.CheckAsync(tradeReq, ct);
        if (!gate.Allowed)
        {
            _log.Warn($"{tag} Blocked by {gate.Gate}: {gate.Reason}");
            _state.LogEvent("RISK_BLOCK", terminalId, process.StrategyName,
                $"{gate.Gate}: {gate.Reason}",
                JsonSerializer.Serialize(new { symbol, direction, entryPrice, slPrice }));
            _ = _alerts.SendAsync("RISK", terminalId,
                $"\U0001f6ab BLOCKED PENDING {direction} {symbol}: {gate.Gate}",
                process.StrategyName);
            _alerts.NotifySignalSent();
            return;
        }

        _log.Info($"{tag} Gates PASSED \u2192 {orderType} {symbol} lot={lotResult.Lot:F2} @ {entryPrice}");

        // Virtual mode
        if (profile.Mode == "virtual")
        {
            await HandleVirtualEnterPendingAsync(process, symbol, direction, orderType,
                entryPrice, slPrice, action.TpPrice ?? 0,
                lotResult, comboMagic, action, ct);
            return;
        }

        // Live: place via PendingOrderManager
        await _pendingMgr.PlacePendingAsync(
            strategyName: process.StrategyName,
            terminalId:   terminalId,
            magic:        comboMagic,
            symbol:       symbol,
            direction:    direction,
            orderType:    orderType,
            entryPrice:   entryPrice,
            slPrice:      slPrice,
            tpPrice:      action.TpPrice ?? 0,
            lot:          lotResult.Lot,
            expiryBars:   action.ExpiryBars ?? 0,
            signalData:   action.SignalData,
            ct:           ct);
    }

    // -----------------------------------------------------------------------
    //  VIRTUAL ENTER â€” Phase 9.V
    // -----------------------------------------------------------------------

    private async Task HandleVirtualEnterAsync(
        StrategyProcess process, string symbol, string direction,
        double slPrice, Engine.LotResult lotResult, double currentPrice,
        InstrumentCard card, StrategyAction action, CancellationToken ct)
    {
        var terminalId = process.TerminalId;
        var tag = $"[V:{process.StrategyName}@{terminalId}]";

        // Initialize virtual balance on first virtual order
        if (_state.GetVirtualBalance(terminalId) == null)
        {
            var acc = await _connector.GetAccountInfoAsync(terminalId, ct);
            if (acc != null)
            {
                _state.InitVirtualBalance(terminalId, acc.Balance);
                _log.Info($"{tag} Virtual balance initialized: ${acc.Balance:F2}");
            }
        }

        // Spread: BUY enters at Ask (close + spread), SELL enters at Bid (\u2248 close)
        double fillPrice = currentPrice;
        if (card.Spread > 0 && card.Point > 0)
        {
            double spreadCost = card.Spread * card.Point;
            // BUY = entry at Ask (above mid), SELL = entry at Bid
            fillPrice = direction == "LONG"
                ? currentPrice + spreadCost
                : currentPrice;
        }

        // Slippage model: always worsens price (BUY higher, SELL lower)
        double slippagePts = _config.VirtualSlippagePoints;
        if (slippagePts > 0 && card.Point > 0)
        {
            double slippageCost = slippagePts * card.Point;
            fillPrice += direction == "LONG" ? slippageCost : -slippageCost;
        }

        // Generate virtual ticket (negative)
        var virtualTicket = _state.NextVirtualTicket();

        // Timeframe from strategy requirements (for VirtualTracker)
        var tf = process.Requirements?.Timeframes.GetValueOrDefault(symbol, "H1") ?? "H1";

        bool isBuy = direction == "LONG";

        // Resolve combo magic (same as live path)
        int comboMagic = _state.ResolveComboMagic(
            process.StrategyName, process.Magic, action.SignalData);

        _log.Info($"{tag} VIRTUAL FILL ticket={virtualTicket} {direction} {symbol} " +
                  $"lot={lotResult.Lot:F2} @ {fillPrice}");

        _state.SavePosition(new Engine.PositionRecord
        {
            Ticket = virtualTicket,
            TerminalId = terminalId,
            Symbol = symbol,
            Direction = isBuy ? "BUY" : "SELL",
            Volume = lotResult.Lot,
            PriceOpen = fillPrice,
            SL = slPrice,
            TP = action.TpPrice ?? 0,
            Magic = comboMagic,
            Source = process.StrategyName,
            SignalData = action.SignalData,
            OpenedAt = DateTime.UtcNow.ToString("o"),
            IsVirtual = true,
            Timeframe = tf
        });

        // Virtual margin for G6 Deposit Load
        int effLeverage = _state.GetEffectiveLeverage(terminalId, symbol, 100);
        double marginEstimate = lotResult.Lot * (card.Margin1Lot > 0
            ? card.Margin1Lot
            : card.TradeContractSize * fillPrice / effLeverage);
        _state.AddVirtualMargin(terminalId, marginEstimate);

        // Execution quality log (slippage = spread for virtual)
        _state.LogExecution(virtualTicket, terminalId, symbol, direction,
            signalPrice: currentPrice, fillPrice: fillPrice,
            tickSize: card.TradeTickSize,
            signalTime: DateTime.UtcNow, fillTime: DateTime.UtcNow,
            strategy: process.StrategyName);

        // Event log + Telegram
        _state.LogEvent("VIRTUAL_ORDER", terminalId, process.StrategyName,
            $"\U0001f7e3 VIRTUAL ENTER {direction} {symbol} lot={lotResult.Lot:F2} @ {fillPrice}",
            JsonSerializer.Serialize(new { ticket = virtualTicket, slPrice, spread = card.Spread }));

        _ = _alerts.SendAsync("ORDER", terminalId,
            $"\U0001f7e3 VIRTUAL {direction} {symbol} lot={lotResult.Lot:F2} @ {fillPrice}",
            process.StrategyName);
    }

    // -----------------------------------------------------------------------
    //  VIRTUAL ENTER_PENDING
    // -----------------------------------------------------------------------

    private Task HandleVirtualEnterPendingAsync(
        StrategyProcess process, string symbol, string direction, string orderType,
        double entryPrice, double slPrice, double tpPrice,
        Engine.LotResult lotResult, int comboMagic, StrategyAction action,
        CancellationToken ct)
    {
        var terminalId = process.TerminalId;
        var tag = $"[V:{process.StrategyName}@{terminalId}]";

        var virtualTicket = _state.NextVirtualTicket();
        var tf = process.Requirements?.Timeframes.GetValueOrDefault(symbol, "H1") ?? "H1";
        int barsRemaining = (action.ExpiryBars ?? 0) > 0 ? action.ExpiryBars!.Value : -1;

        _state.SavePendingOrder(new Engine.PendingOrderRecord
        {
            Ticket        = virtualTicket,
            TerminalId    = terminalId,
            Symbol        = symbol,
            Strategy      = process.StrategyName,
            Magic         = comboMagic,
            Direction     = direction == "LONG" ? "BUY" : "SELL",
            OrderType     = orderType,
            Volume        = lotResult.Lot,
            EntryPrice    = entryPrice,
            SL            = slPrice,
            TP            = tpPrice,
            BarsRemaining = barsRemaining,
            SignalData    = action.SignalData,
            IsVirtual     = true,
            PlacedAt      = DateTime.UtcNow.ToString("o"),
        });

        _log.Info($"{tag} VIRTUAL PENDING ticket={virtualTicket} {orderType} {symbol} @ {entryPrice} " +
                  $"lot={lotResult.Lot:F2} expiry={action.ExpiryBars ?? 0} bars");

        _state.LogEvent("VIRTUAL_ORDER", terminalId, process.StrategyName,
            $"\U0001f7e3 VIRTUAL PENDING {orderType} {symbol} @ {entryPrice} lot={lotResult.Lot:F2}",
            JsonSerializer.Serialize(new { ticket = virtualTicket, slPrice, expiryBars = action.ExpiryBars }));

        _ = _alerts.SendAsync("ORDER", terminalId,
            $"\U0001f7e3 VIRTUAL PENDING {orderType} {symbol} @ {entryPrice} lot={lotResult.Lot:F2}",
            process.StrategyName);

        return Task.CompletedTask;
    }

    // -----------------------------------------------------------------------
    //  EXIT
    // -----------------------------------------------------------------------

    private async Task HandleExitAsync(StrategyProcess process, StrategyAction action,
                                         CancellationToken ct)
    {
        var terminalId = process.TerminalId;
        var tag = $"[{process.StrategyName}@{terminalId}]";
        var ticket = action.Ticket ?? 0;
        if (ticket == 0) { _log.Warn($"{tag} EXIT no ticket"); return; }

        _log.Info($"{tag} EXIT ticket={ticket}");
        _state.LogEvent("SIGNAL", terminalId, process.StrategyName,
            $"EXIT {action.Symbol ?? "?"} ticket={ticket}");

        var pos = _state.GetPositionByTicket(ticket, terminalId);
        if (pos == null) { _log.Warn($"{tag} Position {ticket} not found"); return; }

        // Phase 9.V: Virtual exit â€” no MT5
        if (pos.IsVirtual)
        {
            await HandleVirtualExitAsync(process, pos, "signal", ct);
            return;
        }

        bool isBuy = pos.Direction == "BUY";
        var orderReq = new Dictionary<string, object>
        {
            ["action"] = 1,
            ["symbol"] = pos.Symbol,
            ["volume"] = pos.Volume,
            ["type"] = isBuy ? 1 : 0,
            ["position"] = ticket,
            ["magic"] = pos.Magic,
            ["comment"] = $"D:exit:{process.StrategyName}",
            ["type_filling"] = 2
        };

        var result = await _connector.SendOrderAsync(terminalId, orderReq, ct);
        if (result.IsOk)
        {
            _log.Info($"{tag} EXIT done ticket={ticket}");
            _state.LogEvent("ORDER", terminalId, process.StrategyName, $"EXIT {pos.Symbol} ticket={ticket}");
            _ = _alerts.SendAsync("ORDER", terminalId, $"\u274c EXIT {pos.Symbol} ticket={ticket}",
                process.StrategyName);
        }
        else
        {
            _log.Error($"{tag} EXIT failed: {result.Message}");
            _state.LogEvent("ORDER_FAIL", terminalId, process.StrategyName,
                $"EXIT ticket={ticket}: {result.Message}");
        }
    }

    // -----------------------------------------------------------------------
    //  VIRTUAL EXIT â€” Phase 9.V
    // -----------------------------------------------------------------------

    private async Task HandleVirtualExitAsync(
        StrategyProcess process, Engine.PositionRecord pos, string reason, CancellationToken ct)
    {
        var terminalId = process.TerminalId;
        var tag = $"[V:{process.StrategyName}@{terminalId}]";

        // Current price from BarsCache
        var tf = pos.Timeframe
            ?? process.Requirements?.Timeframes.GetValueOrDefault(pos.Symbol, "H1")
            ?? "H1";
        var bars = _barsCache.GetBars(terminalId, pos.Symbol, tf);
        double closePrice = bars is { Count: > 0 } ? bars[^1].Close : 0;
        if (closePrice <= 0) { _log.Warn($"{tag} No price for virtual exit"); return; }

        // Spread on SELL exit: closing SELL = buying at Ask (close + spread)
        // BUY already paid spread at entry, exits at Bid \u2248 Close â€” correct
        var card = await _connector.GetSymbolInfoAsync(terminalId, pos.Symbol, ct);
        if (pos.Direction == "SELL" && card is { Spread: > 0, Point: > 0 })
        {
            closePrice += card.Spread * card.Point;
        }

        // P&L calculation
        double pnl = CalculateVirtualPnl(pos, closePrice, terminalId, card);

        _state.ClosePosition(pos.Ticket, terminalId, closePrice, reason, pnl);
        _state.UpdateVirtualBalance(terminalId, pnl);

        // Release virtual margin
        if (card != null)
        {
            int effLeverage = _state.GetEffectiveLeverage(terminalId, pos.Symbol, 100);
            double marginRelease = pos.Volume * (card.Margin1Lot > 0
                ? card.Margin1Lot
                : card.TradeContractSize * pos.PriceOpen / effLeverage);
            _state.AddVirtualMargin(terminalId, -marginRelease);
        }

        // Daily P&L (for G2 and 3SL)
        var profile = _state.GetProfile(terminalId);
        var brokerDate = GetBrokerDate(profile?.ServerTimezone ?? "UTC");
        _state.AddRealizedPnl(terminalId, brokerDate, pnl);

        // Phase 9.R: R-cap — calculate R-result and add to daily accumulator
        var rResult = Engine.RCalc.GetRResult(reason, pos.ProtectorFired, pos.SignalData);
        if (rResult.HasValue)
        {
            _state.AddDailyR(terminalId, process.StrategyName, brokerDate, rResult.Value);
            _log.Info($"{tag} R-cap: {reason}" +
                      (pos.ProtectorFired ? " (protector)" : "") +
                      $" -> {rResult.Value:+0.00;-0.00}R");
        }

        // 3SL Guard â€” virtual SL hits also increment counter
        if (reason == "SL" && profile?.Sl3GuardOn == true)
        {
            _state.IncrementSLCount(terminalId);
            var (count, blocked) = _state.Get3SLState(terminalId);
            if (count >= 3 && !blocked)
            {
                _state.Block3SL(terminalId);
                _log.Warn($"[{terminalId}] 3SL GUARD ACTIVATED (virtual)");
            }
        }
        else if (reason != "SL")
        {
            _state.ResetSLCount(terminalId);
        }

        // Save trade snapshot for Trade Chart (bars + SL history cached at close)
        try
        {
            var snapshotBars = _barsCache.GetBars(terminalId, pos.Symbol, tf);
            var slHistory = _state.GetSlHistory(pos.Ticket, terminalId);
            var snapshot = JsonSerializer.Serialize(new
            {
                ticket = pos.Ticket,
                symbol = pos.Symbol,
                direction = pos.Direction,
                volume = pos.Volume,
                price_open = pos.PriceOpen,
                price_close = closePrice,
                sl = pos.SL,
                tp = pos.TP,
                pnl,
                reason,
                timeframe = tf,
                opened_at = pos.OpenedAt,
                closed_at = DateTime.UtcNow.ToString("o"),
                sl_history = slHistory,
                bars = snapshotBars?.TakeLast(500).Select(b => new
                {
                    t = b.Time, o = b.Open, h = b.High, l = b.Low, c = b.Close
                })
            });
            _state.SaveTradeSnapshot(pos.Ticket, terminalId, snapshot);
        }
        catch (Exception ex)
        {
            _log.Warn($"{tag} Failed to save trade snapshot: {ex.Message}");
        }

        _log.Info($"{tag} VIRTUAL EXIT #{pos.Ticket} {pos.Symbol} {reason} " +
                  $"@ {closePrice} P/L={pnl:+0.00;-0.00}");

        _ = _alerts.SendAsync("ORDER", terminalId,
            $"\U0001f7e3 VIRTUAL {reason} {pos.Symbol} P/L={pnl:+0.00;-0.00}",
            process.StrategyName);
    }

    // -----------------------------------------------------------------------
    //  MODIFY_SL
    // -----------------------------------------------------------------------

    private async Task HandleModifySLAsync(StrategyProcess process, StrategyAction action,
                                              CancellationToken ct)
    {
        var terminalId = process.TerminalId;
        var tag = $"[{process.StrategyName}@{terminalId}]";
        var ticket = action.Ticket ?? 0;
        var newSl = action.NewSl ?? 0;
        if (ticket == 0) { _log.Warn($"{tag} MODIFY_SL no ticket"); return; }

        _log.Info($"{tag} MODIFY_SL ticket={ticket} sl={newSl}");

        var pos = _state.GetPositionByTicket(ticket, terminalId);

        // Phase 9.V: Virtual MODIFY_SL â€” update DB directly
        if (pos?.IsVirtual == true)
        {
            // SL history â€” for Trade Chart trail visualization (universal: virtual + real)
            if (Math.Abs(pos.SL - newSl) > 1e-10)
            {
                var tf = pos.Timeframe ?? "H1";
                var slBars = _barsCache.GetBars(terminalId, pos.Symbol, tf);
                long barTime = slBars is { Count: > 0 } ? slBars[^1].Time : 0;
                _state.SaveSlHistory(pos.Ticket, terminalId, pos.SL, newSl, barTime);
            }

            pos.SL = newSl;
            _state.SavePosition(pos);  // Upsert
            _state.MarkProtectorFired(ticket, terminalId);  // Phase 9.R: track for R-cap
            _log.Info($"[V] MODIFY_SL virtual #{ticket} sl={newSl}");
            return;
        }

        // Real MODIFY_SL path
        var orderReq = new Dictionary<string, object>
        {
            ["action"] = 3,                            // TRADE_ACTION_SLTP
            ["symbol"] = pos?.Symbol ?? "",
            ["position"] = ticket,
            ["sl"] = newSl,
            ["magic"] = pos?.Magic ?? process.Magic
        };

        var result = await _connector.SendOrderAsync(terminalId, orderReq, ct);
        if (result.IsOk)
        {
            _log.Info($"{tag} MODIFY_SL done ticket={ticket} sl={newSl}");
            _state.LogEvent("MODIFY", terminalId, process.StrategyName, $"SL ticket={ticket} \u2192 {newSl}");

            // SL history â€” for Trade Chart trail visualization (universal: virtual + real)
            if (pos != null && Math.Abs(pos.SL - newSl) > 1e-10)
            {
                var tf = pos.Timeframe
                    ?? process.Requirements?.Timeframes.GetValueOrDefault(pos.Symbol, "H1")
                    ?? "H1";
                var slBars = _barsCache.GetBars(terminalId, pos.Symbol, tf);
                long barTime = slBars is { Count: > 0 } ? slBars[^1].Time : 0;
                _state.SaveSlHistory(pos.Ticket, terminalId, pos.SL, newSl, barTime);
            }

            _state.MarkProtectorFired(ticket, terminalId);  // Phase 9.R: track for R-cap
        }
        else
        {
            _log.Error($"{tag} MODIFY_SL failed: {result.Message}");
        }
    }

    // -----------------------------------------------------------------------
    //  Virtual P&L calculation (shared logic with VirtualTracker)
    // -----------------------------------------------------------------------

    /// <summary>
    /// Calculate virtual P&L using tick_value from InstrumentCard + commission.
    /// </summary>
    private double CalculateVirtualPnl(Engine.PositionRecord pos, double closePrice, string terminalId,
        Daemon.Models.InstrumentCard? card = null)
    {
        var profile = _state.GetProfile(terminalId);
        double commission = (profile?.CommissionPerLot ?? _config.DefaultCommissionPerLot) * pos.Volume;

        double dirSign = pos.Direction == "BUY" ? 1.0 : -1.0;
        double priceDiff = closePrice - pos.PriceOpen;

        double rawPnl;
        if (card != null && card.TradeTickSize > 0)
        {
            double ticks = priceDiff / card.TradeTickSize;
            double tickValue = (dirSign * priceDiff) >= 0 ? card.TradeTickValueProfit : card.TradeTickValueLoss;
            if (tickValue <= 0) tickValue = card.TradeTickValue;
            rawPnl = dirSign * ticks * tickValue * pos.Volume;
        }
        else
        {
            // Fallback: approximate tick parameters
            double tickSize = pos.Symbol.Contains("JPY") ? 0.001 : 0.00001;
            double tickValue = 1.0;
            double ticks = priceDiff / tickSize;
            rawPnl = dirSign * ticks * tickValue * pos.Volume;
        }

        return Math.Round(rawPnl - commission, 2);
    }

    // -----------------------------------------------------------------------
    //  Continuous Daily DD Monitor (Phase 10)
    //  Called every engine cycle. Checks realized + floating against DD limit.
    //  If breached → emergency close all + stop strategies + mode=monitor.
    // -----------------------------------------------------------------------

    public async Task MonitorDailyDDAsync(CancellationToken ct)
    {
        foreach (var termId in _connector.GetAllTerminalIds())
        {
            if (!_connector.IsConnected(termId)) continue;

            var profile = _state.GetProfile(termId);
            if (profile == null || profile.DailyDDLimit <= 0) continue;
            if (profile.Mode == "monitor") continue; // already safe

            var brokerDate = GetBrokerDate(profile.ServerTimezone);

            // --- Rollover detection: compute DD snapshot on date change ---
            _lastBrokerDates.TryGetValue(termId, out var prevDate);
            if (prevDate != brokerDate)
            {
                _lastBrokerDates[termId] = brokerDate;

                if (profile.DailyDdPercent > 0)
                {
                    // Get equity at rollover moment
                    double rolloverEquity;
                    if (profile.Mode == "virtual" && _virtualTracker != null)
                    {
                        var vBal = _state.GetVirtualBalance(termId) ?? 0;
                        rolloverEquity = vBal + _virtualTracker.GetUnrealizedPnl(termId);
                    }
                    else
                    {
                        var acc = await _connector.GetAccountInfoAsync(termId, ct);
                        rolloverEquity = acc?.Equity ?? 0;
                    }

                    if (rolloverEquity > 0)
                    {
                        double ddFromEquity = rolloverEquity * profile.DailyDdPercent / 100.0;
                        double snapshot = Math.Min(profile.DailyDDLimit, ddFromEquity);
                        _state.SetDailyDdSnapshot(termId, brokerDate, snapshot);
                        _log.Info($"[{termId}] DD rollover: date={brokerDate}, equity={rolloverEquity:F2}, " +
                                  $"{profile.DailyDdPercent}% = {ddFromEquity:F2}, " +
                                  $"capped={snapshot:F2} (initial={profile.DailyDDLimit:F2})");
                    }
                    else
                    {
                        _log.Warn($"[{termId}] DD rollover: could not get equity, using profile limit {profile.DailyDDLimit:F2}");
                    }
                }
            }

            // Only hard mode does continuous monitoring (soft is entry-gate only)
            if (profile.DailyDdMode == "soft") continue;

            var (realizedPnl, _, ddSnapshot) = _state.GetDailyPnl(termId, brokerDate);

            // Effective DD limit: snapshot if available, else profile
            double effectiveLimit = ddSnapshot > 0 ? ddSnapshot : profile.DailyDDLimit;

            double unrealized;
            double currentEquity;

            if (profile.Mode == "virtual" && _virtualTracker != null)
            {
                unrealized = _virtualTracker.GetUnrealizedPnl(termId);
                var vBal = _state.GetVirtualBalance(termId) ?? 0;
                currentEquity = vBal + unrealized;
            }
            else
            {
                // Phase 10: magic-filtered unrealized — only this terminal's positions
                unrealized = await _connector.GetFilteredUnrealizedPnlAsync(termId, ct);
                var acc = await _connector.GetAccountInfoAsync(termId, ct);
                currentEquity = acc?.Equity ?? 0;
            }

            double totalPnl = realizedPnl + unrealized;

            // Update intraday HWM with current equity
            if (currentEquity > 0)
                _state.UpdateHWM(termId, brokerDate, currentEquity);

            // DD limit is positive (e.g. 2500 means max $2500 loss). Breach when loss exceeds it.
            if (totalPnl >= -effectiveLimit) continue;

            // ======== DD LIMIT BREACHED — EMERGENCY ========
            _log.Error($"[{termId}] DD LIMIT BREACHED! P/L={totalPnl:F2} " +
                       $"(realized={realizedPnl:F2}, floating={unrealized:F2}) limit=-{effectiveLimit:F2}");

            int closed = 0;
            var errors = new List<string>();

            // 1) Close real MT5 positions (filtered by magic — only this terminal's strategies)
            try
            {
                var positions = await _connector.GetFilteredPositionsAsync(termId, ct);
                if (positions != null)
                {
                    foreach (var pos in positions)
                    {
                        try
                        {
                            var orderReq = new Dictionary<string, object>
                            {
                                ["action"] = 1,
                                ["symbol"] = pos.Symbol,
                                ["volume"] = pos.Volume,
                                ["type"] = pos.IsBuy ? 1 : 0,
                                ["position"] = pos.Ticket,
                                ["magic"] = 0,
                                ["comment"] = "D:dd_monitor:emergency",
                                ["type_filling"] = 2
                            };
                            var result = await _connector.SendOrderAsync(termId, orderReq, ct);
                            if (result.IsOk) closed++;
                        }
                        catch (Exception ex) { errors.Add($"real:{pos.Ticket} - {ex.Message}"); }
                    }
                }
            }
            catch (Exception ex) { errors.Add($"get_positions: {ex.Message}"); }

            // 2) Close virtual positions
            var virtualPositions = _state.GetOpenVirtualPositions(termId);
            foreach (var vp in virtualPositions)
            {
                try
                {
                    var tf = vp.Timeframe ?? "H1";
                    var bars = _barsCache.GetBars(termId, vp.Symbol, tf);
                    double closePrice = bars != null && bars.Count > 0
                        ? bars[^1].Close : vp.PriceOpen;
                    double vpPnl = _virtualTracker?.CalculateVirtualPnl(vp, closePrice, termId) ?? 0;
                    _state.ClosePosition(vp.Ticket, termId, closePrice, "DD_EMERGENCY", vpPnl);
                    _state.UpdateVirtualBalance(termId, vpPnl);
                    closed++;
                }
                catch (Exception ex) { errors.Add($"virtual:{vp.Ticket} - {ex.Message}"); }
            }

            // 3) Stop strategies
            var processes = _strategies.GetProcessesForTerminal(termId);
            foreach (var p in processes)
            {
                try { await _strategies.StopStrategyAsync(p.StrategyName, termId, ct); }
                catch { }
            }

            // 4) Set mode to monitor
            profile.Mode = "monitor";
            _state.SaveProfile(profile);

            // 5) Log + Alert
            _state.LogEvent("EMERGENCY", termId, null,
                $"DD LIMIT BREACHED: P/L={totalPnl:F2} vs limit=-{effectiveLimit:F2}. " +
                $"Closed {closed} position(s). Mode -> MONITOR.",
                JsonSerializer.Serialize(new { totalPnl, realizedPnl, unrealized, limit = effectiveLimit, closed, errors }));

            _ = _alerts.SendAsync("EMERGENCY", termId,
                $"\ud83d\udea8 DD LIMIT BREACHED on {termId}!\n" +
                $"P/L: ${totalPnl:F2} (limit: -${effectiveLimit:F2})\n" +
                $"Closed {closed} position(s). Mode → MONITOR.",
                bypassDebounce: true);
        }
    }

    // -----------------------------------------------------------------------
    //  Helpers
    // -----------------------------------------------------------------------

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
