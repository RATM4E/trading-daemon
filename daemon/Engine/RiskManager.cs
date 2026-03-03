using System.Text.Json;
using Daemon.Connector;
using Daemon.Models;

namespace Daemon.Engine;

/// <summary>
/// Every order passes through a chain of 13 gates.
/// Any gate can reject the order with a reason.
/// Gates run sequentially; first reject stops the chain.
/// </summary>
public class RiskManager
{
    private readonly StateManager _state;
    private readonly ConnectorManager _connector;
    private readonly NewsService? _news;
    private readonly ConsoleLogger _log;
    private VirtualTracker? _virtualTracker;

    // --- Global Pause ---
    private volatile bool _globalPaused;
    private DateTime? _globalPauseUntil;   // null = indefinite
    private string? _globalPauseReason;
    private readonly object _pauseLock = new();

    public RiskManager(StateManager state, ConnectorManager connector, ConsoleLogger log,
                       NewsService? news = null)
    {
        _state = state;
        _connector = connector;
        _news = news;
        _log = log;

        // Restore pause state from DB
        var (paused, until, reason) = _state.LoadPauseState();
        if (paused && until.HasValue && until.Value <= DateTime.UtcNow)
        {
            // Timed pause expired while daemon was down — clear it
            _state.SavePauseState(false);
        }
        else if (paused)
        {
            _globalPaused = true;
            _globalPauseUntil = until;
            _globalPauseReason = reason;
            _log.Warn($"[RISK] Global pause restored from DB{(until.HasValue ? $" (until {until.Value:HH:mm} UTC)" : "")}");
        }
    }

    /// <summary>Set after construction (VirtualTracker is created later).</summary>
    public void SetVirtualTracker(VirtualTracker vt) => _virtualTracker = vt;

    // ===================================================================
    // Global Pause API
    // ===================================================================

    /// <summary>Pause all new trading globally.</summary>
    /// <param name="durationMin">Duration in minutes. 0 = indefinite.</param>
    /// <param name="reason">Optional reason string.</param>
    public void SetGlobalPause(bool paused, int durationMin = 0, string? reason = null)
    {
        lock (_pauseLock)
        {
            _globalPaused = paused;
            if (paused && durationMin > 0)
                _globalPauseUntil = DateTime.UtcNow.AddMinutes(durationMin);
            else
                _globalPauseUntil = null;
            _globalPauseReason = paused ? reason : null;

            _state.SavePauseState(_globalPaused, _globalPauseUntil, _globalPauseReason);

            if (paused)
                _log.Warn($"[RISK] Global PAUSE activated" +
                    (durationMin > 0 ? $" for {durationMin}m" : " (indefinite)") +
                    (reason != null ? $" — {reason}" : ""));
            else
                _log.Info("[RISK] Global PAUSE lifted — trading resumed");
        }
    }

    /// <summary>Get current pause state.</summary>
    public (bool Paused, DateTime? Until, string? Reason) GetPauseState()
    {
        lock (_pauseLock)
        {
            // Auto-expire timed pause
            if (_globalPaused && _globalPauseUntil.HasValue && _globalPauseUntil.Value <= DateTime.UtcNow)
            {
                _globalPaused = false;
                _globalPauseUntil = null;
                _globalPauseReason = null;
                _state.SavePauseState(false);
                _log.Info("[RISK] Global PAUSE expired — trading resumed");
            }
            return (_globalPaused, _globalPauseUntil, _globalPauseReason);
        }
    }

    /// <summary>
    /// Run all risk gates on a proposed trade.
    /// Returns GateResult with allowed/rejected and reason.
    /// </summary>
    public async Task<GateResult> CheckAsync(TradeRequest request, CancellationToken ct = default)
    {
        var profile = _state.GetProfile(request.TerminalId);
        if (profile == null)
            return GateResult.Reject("PROFILE", "No terminal profile found");

        // Run gates sequentially
        GateResult result;
        GateResult? marginReduction = null;  // Track if G5 reduced the lot

        // Gate 0: Global Pause (blocks ALL new entries)
        result = CheckGlobalPause();
        if (!result.Allowed) return Log(request, result);

        // Gate 1: Operating Mode
        result = CheckOperatingMode(profile);
        if (!result.Allowed) return Log(request, result);

        // Gate 2: Daily DD
        result = await CheckDailyDD(request, profile, ct);
        if (!result.Allowed) return Log(request, result);

        // Gate 3: Cumulative DD
        result = await CheckCumulativeDD(request, profile, ct);
        if (!result.Allowed) return Log(request, result);

        // Gate 4: Risk Per Trade
        result = CheckRiskPerTrade(request, profile);
        if (!result.Allowed) return Log(request, result);

        // Gate 5: Margin Per Trade
        result = await CheckMarginPerTrade(request, profile, ct);
        if (!result.Allowed) return Log(request, result);
        if (result.ReducedLot.HasValue) marginReduction = result;

        // Gate 6: Deposit Load
        result = await CheckDepositLoad(request, profile, ct);
        if (!result.Allowed) return Log(request, result);

        // Gate 7: News Window
        result = CheckNewsWindow(request, profile);
        if (!result.Allowed) return Log(request, result);

        // Gate 8: 3SL Guard
        result = Check3SLGuard(request, profile);
        if (!result.Allowed) return Log(request, result);

        // Gate 9: Netting Check
        result = CheckNetting(request, profile);
        if (!result.Allowed) return Log(request, result);

        // Gate 10: Trading Hours (no-trade window)
        result = CheckTradingHours(profile);
        if (!result.Allowed) return Log(request, result);

        // Gate 11: Same Symbol Per Strategy (prevents duplicate positions per strategy)
        result = CheckSameCombo(request);
        if (!result.Allowed) return Log(request, result);

        // Gate 12: R-cap (strategy daily R-budget)
        result = CheckRCap(request, profile);
        if (!result.Allowed) return Log(request, result);

        // All gates passed
        var passed = GateResult.Pass();
        // Propagate margin reduction info if G5 reduced the lot
        if (marginReduction != null)
        {
            passed.OriginalLot = marginReduction.OriginalLot;
            passed.ReducedLot = marginReduction.ReducedLot;
            passed.Reason = marginReduction.Reason;
        }
        _log.Info($"[{request.TerminalId}] RISK: All 13 gates passed for " +
                  $"{request.Direction} {request.Symbol} {request.Lot:F2} lot" +
                  (marginReduction != null ? $" (reduced from {marginReduction.OriginalLot:F2})" : ""));
        return passed;
    }

    // ===================================================================
    // Gate 0: Global Pause
    // ===================================================================

    private GateResult CheckGlobalPause()
    {
        var (paused, until, reason) = GetPauseState();
        if (!paused) return GateResult.Pass();

        var msg = "Trading globally paused";
        if (until.HasValue)
        {
            var remaining = until.Value - DateTime.UtcNow;
            msg += $" ({(int)remaining.TotalMinutes}m remaining)";
        }
        if (!string.IsNullOrEmpty(reason))
            msg += $" — {reason}";
        return GateResult.Reject("G0_PAUSE", msg);
    }

    // ===================================================================
    // Gate 1: Operating Mode
    // ===================================================================

    private static GateResult CheckOperatingMode(TerminalProfile profile)
    {
        return profile.Mode.ToLowerInvariant() switch
        {
            "monitor" => GateResult.Reject("G1_MODE", "Terminal is in monitor-only mode"),
            "semi"    => GateResult.Reject("G1_MODE", "Terminal is in semi-auto mode â€” pending approval"),
            "auto"    => GateResult.Pass(),
            "virtual" => GateResult.Pass(),   // Phase 9.V: virtual passes all gates
            _         => GateResult.Reject("G1_MODE", $"Unknown mode: {profile.Mode}"),
        };
    }

    // ===================================================================
    // Gate 2: Daily Drawdown
    // ===================================================================

    private async Task<GateResult> CheckDailyDD(TradeRequest request, TerminalProfile profile,
                                                  CancellationToken ct)
    {
        var brokerDate = GetBrokerDate(profile.ServerTimezone);
        var (realizedPnl, _, ddSnapshot) = _state.GetDailyPnl(request.TerminalId, brokerDate);

        double effectiveLimit = ddSnapshot > 0 ? ddSnapshot : profile.DailyDDLimit;
        string limitSource = ddSnapshot > 0 ? $"snapshot({ddSnapshot:F2})" : $"profile({profile.DailyDDLimit:F2})";

        double currentLoss;
        string modeLabel;

        if (profile.DailyDdMode == "soft")
        {
            // Soft: realized-only latch — block new entries, don't count floating
            currentLoss = realizedPnl;
            modeLabel = "soft/realized";
        }
        else
        {
            // Hard: realized + unrealized
            double unrealized;
            if (profile.Mode == "virtual" && _virtualTracker != null)
            {
                unrealized = _virtualTracker.GetUnrealizedPnl(request.TerminalId);
            }
            else
            {
                // Phase 10: magic-filtered unrealized — only this terminal's positions
                unrealized = await _connector.GetFilteredUnrealizedPnlAsync(request.TerminalId, ct);
            }
            currentLoss = realizedPnl + unrealized;
            modeLabel = "hard/total";
        }

        double remaining = effectiveLimit + currentLoss; // currentLoss is negative when losing

        if (remaining < request.TradeRisk)
        {
            return GateResult.Reject("G2_DAILY_DD",
                $"Daily DD limit would be exceeded ({modeLabel}). " +
                $"Today's P/L: ${currentLoss:F2} (realized: ${realizedPnl:F2}). " +
                $"Remaining: ${remaining:F2}, trade risk: ${request.TradeRisk:F2}, " +
                $"limit: ${effectiveLimit:F2} [{limitSource}]");
        }

        return GateResult.Pass();
    }

    // ===================================================================
    // Gate 3: Cumulative Drawdown (equity vs HWM)
    // ===================================================================

    private async Task<GateResult> CheckCumulativeDD(TradeRequest request, TerminalProfile profile,
                                                      CancellationToken ct)
    {
        var acc = await _connector.GetAccountInfoAsync(request.TerminalId, ct);
        if (acc == null) return GateResult.Reject("G3_CUM_DD", "Cannot get account info");

        // Update HWM
        var brokerDate = GetBrokerDate(profile.ServerTimezone);
        _state.UpdateHWM(request.TerminalId, brokerDate, acc.Equity);

        var (_, hwm, _) = _state.GetDailyPnl(request.TerminalId, brokerDate);
        if (hwm <= 0) hwm = acc.Balance; // First run â€” use balance as HWM

        double currentDD = hwm - acc.Equity;
        double afterTrade = currentDD + request.TradeRisk;

        if (afterTrade > profile.CumDDLimit)
        {
            return GateResult.Reject("G3_CUM_DD",
                $"Cumulative DD limit would be exceeded. " +
                $"Current DD: ${currentDD:F2}, trade risk: ${request.TradeRisk:F2}, " +
                $"total: ${afterTrade:F2} > limit: ${profile.CumDDLimit:F2}");
        }

        return GateResult.Pass();
    }

    // ===================================================================
    // Gate 4: Risk Per Trade
    // ===================================================================

    private static GateResult CheckRiskPerTrade(TradeRequest request, TerminalProfile profile)
    {
        double maxRisk = profile.RiskType.ToLowerInvariant() == "pct"
            ? request.AccountBalance * profile.MaxRiskTrade / 100.0
            : profile.MaxRiskTrade;

        if (request.TradeRisk > maxRisk)
        {
            return GateResult.Reject("G4_RISK_TRADE",
                $"Trade risk ${request.TradeRisk:F2} exceeds max ${maxRisk:F2} per trade");
        }

        return GateResult.Pass();
    }

    // ===================================================================
    // Gate 5: Margin Per Trade
    // ===================================================================

    private async Task<GateResult> CheckMarginPerTrade(TradeRequest request, TerminalProfile profile,
                                                        CancellationToken ct)
    {
        var card = await _connector.GetSymbolInfoAsync(request.TerminalId, request.Symbol, ct);
        if (card == null) return GateResult.Pass(); // Can’t check — let it through

        var acc = await _connector.GetAccountInfoAsync(request.TerminalId, ct);
        if (acc == null) return GateResult.Pass();

        // Margin per 1.0 lot — prefer exact Margin1Lot from MT5, fallback to effective leverage
        string marginSource;
        double margin1Lot;
        if (card.Margin1Lot > 0)
        {
            margin1Lot = card.Margin1Lot;
            marginSource = "mt5_calc_margin";
        }
        else
        {
            // Margin1Lot=0 from SYMBOL_INFO — try explicit CalcMargin call
            var calcMargin = await _connector.CalcMarginAsync(
                request.TerminalId, request.Symbol, request.Direction,
                1.0, request.EntryPrice, ct);

            if (calcMargin.HasValue && calcMargin.Value > 0)
            {
                margin1Lot = calcMargin.Value;
                marginSource = "calc_margin_explicit";
                _log.Info($"[{request.TerminalId}] G5: Margin1Lot=0 for {request.Symbol}, " +
                          $"CalcMargin returned ${margin1Lot:F2}/lot");
            }
            else
            {
                // Last resort fallback — WARNING: inaccurate for cross-pairs!
                var effLev = _state.GetEffectiveLeverage(request.TerminalId, request.Symbol, acc.Leverage);
                margin1Lot = card.TradeContractSize * request.EntryPrice / effLev;
                marginSource = $"fallback_INACCURATE(price={request.EntryPrice:F5},lev={effLev})";
                _log.Warn($"[{request.TerminalId}] G5: Margin1Lot=0 AND CalcMargin failed for {request.Symbol}, " +
                          $"using {marginSource} -> ${margin1Lot:F2}/lot. INACCURATE FOR CROSS-PAIRS!");
            }
        }

        double marginEstimate = request.Lot * margin1Lot;
        // Use equity as base: reflects actual available funds with open positions
        double maxMargin = profile.MaxMarginTrade * acc.Equity / 100.0;

        _log.Info($"[{request.TerminalId}] G5: {request.Symbol} lot={request.Lot:F2}, " +
                   $"margin1Lot=${margin1Lot:F2} ({marginSource}), " +
                   $"est=${marginEstimate:F2}, max={profile.MaxMarginTrade}% = ${maxMargin:F2}, " +
                   $"equity=${acc.Equity:F2}, balance=${acc.Balance:F2}, " +
                   $"mode={profile.MarginTradeMode}, " +
                   $"volStep={card.VolumeStep}, volMin={card.VolumeMin}, volMax={card.VolumeMax}");

        if (marginEstimate <= maxMargin)
            return GateResult.Pass();

        // Margin exceeded — block or reduce?
        if (profile.MarginTradeMode.ToLowerInvariant() != "reduce")
        {
            return GateResult.Reject("G5_MARGIN_TRADE",
                $"Estimated margin ${marginEstimate:F2} exceeds max {profile.MaxMarginTrade}% " +
                $"of equity (${maxMargin:F2}). equity=${acc.Equity:F2}");
        }

        // === Reduce mode: calculate the max lot that fits within margin limit ===
        if (margin1Lot <= 0)
        {
            return GateResult.Reject("G5_MARGIN_TRADE",
                $"Cannot reduce lot — margin per lot is 0");
        }

        if (card.VolumeStep <= 0)
        {
            // VolumeStep missing/zero — use 0.01 as safe default for forex
            _log.Warn($"[{request.TerminalId}] G5: VolumeStep={card.VolumeStep} for {request.Symbol}, " +
                      $"using 0.01 as fallback");
            card.VolumeStep = 0.01;
        }

        double rawMaxLot = maxMargin / margin1Lot;

        // Round down to nearest volume_step
        double reducedLot = Math.Floor(rawMaxLot / card.VolumeStep) * card.VolumeStep;

        // Round to avoid floating-point artifacts
        int stepDecimals = CountDecimals(card.VolumeStep);
        reducedLot = Math.Round(reducedLot, stepDecimals);

        // Check minimum lot
        double volMin = card.VolumeMin > 0 ? card.VolumeMin : 0.01;
        if (reducedLot < volMin)
        {
            return GateResult.Reject("G5_MARGIN_TRADE",
                $"Margin-reduced lot {reducedLot:F4} < volume_min {volMin}. " +
                $"Max margin ${maxMargin:F2}, margin/lot ${margin1Lot:F2}");
        }

        // Apply reduction — modify request in-place so subsequent gates (G6 etc) see reduced values
        double originalLot = request.Lot;
        request.Lot = reducedLot;

        // Recalculate trade risk at reduced lot
        double distance = Math.Abs(request.EntryPrice - request.SlPrice);
        double tickValue = card.TradeTickValueLoss > 0 ? card.TradeTickValueLoss : card.TradeTickValue;
        if (card.TradeTickSize > 0 && tickValue > 0)
        {
            double ticks = distance / card.TradeTickSize;
            request.TradeRisk = Math.Round(ticks * tickValue * reducedLot, 2);
        }
        else
        {
            // tickValue=0 or tickSize=0: use MT5 OrderCalcProfit as authoritative fallback
            try
            {
                var loss1Lot = await _connector.CalcProfitAsync(
                    request.TerminalId, request.Symbol, request.Direction,
                    1.0, request.EntryPrice, request.SlPrice, ct);
                if (loss1Lot.HasValue && loss1Lot.Value != 0)
                {
                    request.TradeRisk = Math.Round(Math.Abs(loss1Lot.Value) * reducedLot, 2);
                }
                else
                {
                    // Proportional fallback: scale risk by lot ratio
                    request.TradeRisk = Math.Round(request.TradeRisk * reducedLot / originalLot, 2);
                    _log.Warn($"[{request.TerminalId}] G5: CalcProfit unavailable for {request.Symbol}, " +
                              $"using proportional risk fallback");
                }
            }
            catch
            {
                request.TradeRisk = Math.Round(request.TradeRisk * reducedLot / originalLot, 2);
                _log.Warn($"[{request.TerminalId}] G5: CalcProfit failed for {request.Symbol}, " +
                          $"using proportional risk fallback");
            }
        }

        double newMargin = reducedLot * margin1Lot;

        // POST-REDUCTION SAFETY CHECK: verify margin actually fits
        if (newMargin > maxMargin)
        {
            return GateResult.Reject("G5_MARGIN_TRADE",
                $"Post-reduction margin ${newMargin:F2} still exceeds limit ${maxMargin:F2} " +
                $"({profile.MaxMarginTrade}%). margin1Lot=${margin1Lot:F2} ({marginSource}), " +
                $"lot={reducedLot:F2}. This indicates a margin calculation inconsistency.");
        }

        string info = $"Lot reduced {originalLot:F2} → {reducedLot:F2} to fit margin limit. " +
                       $"Margin: ${newMargin:F2} ≤ ${maxMargin:F2} ({profile.MaxMarginTrade}% of equity), " +
                       $"margin1Lot=${margin1Lot:F2} ({marginSource}), equity=${acc.Equity:F2}";

        _log.Info($"[{request.TerminalId}] G5_MARGIN_REDUCE: {info}");

        return GateResult.PassWithReduction(originalLot, reducedLot, info);
    }

    private static int CountDecimals(double value)
    {
        string s = value.ToString("G", System.Globalization.CultureInfo.InvariantCulture);
        int idx = s.IndexOf('.');
        return idx < 0 ? 0 : s.Length - idx - 1;
    }


    // ===================================================================
    // Gate 6: Deposit Load (total margin utilization)
    // ===================================================================

    private async Task<GateResult> CheckDepositLoad(TradeRequest request, TerminalProfile profile,
                                                     CancellationToken ct)
    {
        var acc = await _connector.GetAccountInfoAsync(request.TerminalId, ct);
        if (acc == null) return GateResult.Pass();

        var card = await _connector.GetSymbolInfoAsync(request.TerminalId, request.Symbol, ct);
        if (card == null) return GateResult.Pass();

        // --- New trade margin estimate ---
        double margin1Lot = card.Margin1Lot;
        string marginSource = "mt5_symbol_info";

        if (margin1Lot <= 0)
        {
            var calcMargin = await _connector.CalcMarginAsync(
                request.TerminalId, request.Symbol, request.Direction,
                1.0, request.EntryPrice, ct);

            if (calcMargin.HasValue && calcMargin.Value > 0)
            {
                margin1Lot = calcMargin.Value;
                marginSource = "calc_margin_explicit";
            }
            else
            {
                margin1Lot = card.TradeContractSize * request.EntryPrice
                    / _state.GetEffectiveLeverage(request.TerminalId, request.Symbol, acc.Leverage);
                marginSource = "fallback";
            }
        }
        double newMarginEstimate = request.Lot * margin1Lot;

        // --- Current "own" margin: only positions belonging to this terminal's strategies ---
        double ownMargin = 0;
        double ownProfit = 0;
        double accountTotalMargin = acc.Margin;
        string marginMethod = "account_wide";  // fallback label

        if (profile.Mode == "virtual")
        {
            // Virtual mode: MT5 has no real positions, use tracked virtual margin
            ownMargin = _state.GetVirtualMargin(request.TerminalId);
            marginMethod = "virtual_tracker";
        }
        else if (request.TerminalMagics != null && request.TerminalMagics.Count > 0)
        {
            // Live mode with magic filter: get authoritative per-position margin from MT5
            var posMargin = await _connector.CalcPositionsMarginAsync(
                request.TerminalId, request.TerminalMagics, ct);

            if (posMargin != null)
            {
                ownMargin = posMargin.OwnMargin;
                ownProfit = posMargin.OwnProfit;
                accountTotalMargin = posMargin.TotalMargin;
                marginMethod = $"per_position(magics=[{string.Join(",", request.TerminalMagics)}], " +
                               $"{posMargin.Positions.Count} own positions)";
            }
            else
            {
                // CalcPositionsMargin failed — fall back to account-wide margin (conservative)
                ownMargin = acc.Margin;
                marginMethod = "account_wide_FALLBACK";
                _log.Warn($"[{request.TerminalId}] G6: CalcPositionsMargin failed, " +
                          $"falling back to account-wide margin ${acc.Margin:F2}");
            }
        }
        else
        {
            // No magic filter available — use account-wide margin (legacy behavior)
            ownMargin = acc.Margin;
        }

        double totalOwnMargin = ownMargin + newMarginEstimate;

        // Use equity as base: reflects actual available funds with open positions
        double depositBase = acc.Equity;
        double maxLoad = profile.MaxDepositLoad * depositBase / 100.0;

        // Diagnostic logging (always)
        _log.Info($"[{request.TerminalId}] G6: {request.Symbol} lot={request.Lot:F2}, " +
                  $"margin1Lot=${margin1Lot:F2} ({marginSource}), " +
                  $"newMargin=${newMarginEstimate:F2}, ownMargin=${ownMargin:F2}, " +
                  $"totalOwn=${totalOwnMargin:F2}, accountMargin=${accountTotalMargin:F2}, " +
                  $"equity=${acc.Equity:F2}, balance=${acc.Balance:F2}, " +
                  $"maxLoad={profile.MaxDepositLoad}% = ${maxLoad:F2}, " +
                  $"method={marginMethod}");

        if (totalOwnMargin > maxLoad)
        {
            return GateResult.Reject("G6_DEPOSIT_LOAD",
                $"Own margin ${totalOwnMargin:F2} would exceed {profile.MaxDepositLoad}% " +
                $"of equity (${maxLoad:F2}). " +
                $"Own open: ${ownMargin:F2}, new: ${newMarginEstimate:F2}, " +
                $"account total: ${accountTotalMargin:F2}, equity: ${depositBase:F2}");
        }

        return GateResult.Pass();
    }

    // ===================================================================
    // Gate 7: News Window
    // ===================================================================

    private GateResult CheckNewsWindow(TradeRequest request, TerminalProfile profile)
    {
        if (!profile.NewsGuardOn)
            return GateResult.Pass();

        if (_news == null)
            return GateResult.Pass(); // NewsService not injected â€” skip

        var result = _news.IsBlocked(
            request.Symbol,
            windowMinutes: profile.NewsWindowMin,
            includeUsd: profile.NewsIncludeUsd,
            minImpact: profile.NewsMinImpact);

        if (result.Blocked)
        {
            return GateResult.Reject("G7_NEWS",
                $"News block: {result.EventName} ({result.Currency}) " +
                $"in {result.MinutesToEvent} min. Window: \u00b1{profile.NewsWindowMin}min");
        }

        return GateResult.Pass();
    }

    // ===================================================================
    // Gate 8: 3SL Guard
    // ===================================================================

    private GateResult Check3SLGuard(TradeRequest request, TerminalProfile profile)
    {
        if (!profile.Sl3GuardOn)
            return GateResult.Pass();

        var (count, blocked) = _state.Get3SLState(request.TerminalId);

        if (blocked)
        {
            return GateResult.Reject("G8_3SL",
                $"3SL Guard is active â€” trading blocked after {count} consecutive SL hits. " +
                $"Unblock manually or wait for reset.");
        }

        return GateResult.Pass();
    }

    // ===================================================================
    // Gate 9: Netting Check
    // ===================================================================

    private GateResult CheckNetting(TradeRequest request, TerminalProfile profile)
    {
        if (profile.AccountType == "hedge")
            return GateResult.Pass(); // Hedge accounts can have multiple positions per symbol

        // Netting: check if there's already a position on this symbol
        var openPositions = _state.GetOpenPositions(request.TerminalId);
        var existing = openPositions.FirstOrDefault(p => p.Symbol == request.Symbol);

        if (existing != null)
        {
            if (existing.Direction == request.Direction)
            {
                return GateResult.Reject("G9_NETTING",
                    $"Netting account: already have {existing.Direction} {existing.Symbol} " +
                    $"#{existing.Ticket}. Cannot add to position.");
            }
            else
            {
                return GateResult.Reject("G9_NETTING",
                    $"Netting account: already have {existing.Direction} {existing.Symbol} " +
                    $"#{existing.Ticket}. Close existing position first.");
            }
        }

        return GateResult.Pass();
    }

    // ===================================================================
    // Gate 10: Trading Hours (No-Trade Window)
    // ===================================================================

    /// <summary>
    /// Blocks new entries during a configured no-trade window (broker time).
    /// Handles midnight crossing: if start > end, the window wraps around midnight.
    /// Example: no_trade 23:30 to 01:30 blocks from 23:30 to midnight + midnight to 01:30.
    /// </summary>
    internal static GateResult CheckTradingHours(TerminalProfile profile, DateTime? utcNowOverride = null)
    {
        if (!profile.NoTradeOn)
            return GateResult.Pass(); // Toggle OFF - allow trading

        if (string.IsNullOrEmpty(profile.NoTradeStart) || string.IsNullOrEmpty(profile.NoTradeEnd))
            return GateResult.Pass(); // Not configured â€” trade 24/5

        if (!TimeOnly.TryParse(profile.NoTradeStart, out var windowStart) ||
            !TimeOnly.TryParse(profile.NoTradeEnd, out var windowEnd))
        {
            return GateResult.Pass(); // Invalid format â€” skip rather than block
        }

        // Get current broker time
        var brokerTime = GetBrokerTime(profile.ServerTimezone, utcNowOverride);

        bool inWindow;
        if (windowStart > windowEnd)
        {
            // Wraps midnight: e.g. 23:30 â†’ 01:30
            // Blocked if now >= 23:30 OR now <= 01:30
            inWindow = brokerTime >= windowStart || brokerTime <= windowEnd;
        }
        else
        {
            // Same day: e.g. 12:00 â†’ 13:00
            // Blocked if now >= 12:00 AND now <= 13:00
            inWindow = brokerTime >= windowStart && brokerTime <= windowEnd;
        }

        if (inWindow)
        {
            return GateResult.Reject("G10_TRADING_HOURS",
                $"No-trade window: {profile.NoTradeStart}\u2013{profile.NoTradeEnd} broker time. " +
                $"Current broker time: {brokerTime:HH:mm}");
        }

        return GateResult.Pass();
    }

    // ===================================================================
    // Gate 11: Same Combo (symbol + magic)
    // ===================================================================

    /// <summary>
    /// Prevents a strategy from opening a second position on the same combo.
    /// With combo magic, each combo_key gets its own magic number, so
    /// magic match = same combo. Different combos on the same symbol pass through.
    /// </summary>
    private GateResult CheckSameCombo(TradeRequest request)
    {
        var openPositions = _state.GetOpenPositions(request.TerminalId);

        var existing = openPositions.FirstOrDefault(p =>
            p.Symbol == request.Symbol && p.Magic == request.Magic);

        if (existing != null)
        {
            return GateResult.Reject("G11_SAME_COMBO",
                $"Already has {existing.Direction} {existing.Symbol} " +
                $"#{existing.Ticket} (magic {request.Magic})");
        }

        return GateResult.Pass();
    }

    // ===================================================================
    // Gate 12: R-cap (Strategy daily R-budget)
    // ===================================================================

    /// <summary>
    /// Tracks cumulative R-result for each strategy per day.
    /// When daily R-sum drops to or below -r_cap, blocks new entries.
    /// Operates in R-space (independent of lot size / margin).
    /// This ensures strategy behaves like its backtest even when
    /// margin gates reduce position sizes.
    ///
    /// Tri-state logic via rCapOn + rCapLimit:
    ///   rCapOn=true                  → explicitly ON (use rCapLimit or strategy default)
    ///   rCapOn=false, rCapLimit >= 0 → never configured → auto-active from strategy config
    ///   rCapOn=false, rCapLimit < 0  → explicitly OFF from dashboard
    /// </summary>
    private GateResult CheckRCap(TradeRequest request, TerminalProfile profile)
    {
        double capValue;
        if (profile.RCapOn)
        {
            // Explicitly ON: use dashboard override, or fallback to strategy config
            capValue = profile.RCapLimit > 0 ? profile.RCapLimit : (request.RCap ?? 0);
        }
        else if (profile.RCapLimit < 0)
        {
            // Explicitly disabled from dashboard (sentinel -1)
            return GateResult.Pass();
        }
        else
        {
            // Never configured — auto-active from strategy config
            capValue = request.RCap ?? 0;
        }

        if (capValue <= 0)
            return GateResult.Pass(); // R-cap not configured

        var brokerDate = GetBrokerDate(profile.ServerTimezone);
        var (rSum, tradeCount) = _state.GetDailyR(request.TerminalId, request.Strategy, brokerDate);

        // RED flag: daily R-sum has reached or exceeded the cap
        if (rSum <= -capValue)
        {
            return GateResult.Reject("G12_RCAP",
                $"R-cap reached for strategy '{request.Strategy}'. " +
                $"Daily R-sum: {rSum:+0.00;-0.00}R ({tradeCount} trades), " +
                $"limit: -{capValue:F2}R. Trading blocked until next day.");
        }

        return GateResult.Pass();
    }

    // ===================================================================
    // Helpers
    // ===================================================================

    private static TimeOnly GetBrokerTime(string serverTimezone, DateTime? utcNowOverride = null)
    {
        var utcNow = utcNowOverride ?? DateTime.UtcNow;
        try
        {
            var tz = TimeZoneInfo.FindSystemTimeZoneById(serverTimezone);
            var brokerNow = TimeZoneInfo.ConvertTimeFromUtc(
                DateTime.SpecifyKind(utcNow, DateTimeKind.Utc), tz);
            return TimeOnly.FromDateTime(brokerNow);
        }
        catch
        {
            return TimeOnly.FromDateTime(utcNow);
        }
    }

    private GateResult Log(TradeRequest req, GateResult result)
    {
        _log.Warn($"[{req.TerminalId}] RISK REJECTED [{result.Gate}]: {result.Reason}");
        _state.LogEvent("RISK", req.TerminalId, req.Strategy,
            $"Order rejected by {result.Gate}: {result.Reason}",
            JsonSerializer.Serialize(new
            {
                gate = result.Gate,
                symbol = req.Symbol,
                direction = req.Direction,
                lot = req.Lot,
                tradeRisk = req.TradeRisk,
                reason = result.Reason
            }));
        return result;
    }

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

// ===================================================================
// Request/Result types
// ===================================================================

/// <summary>Proposed trade that must pass risk gates.</summary>
public class TradeRequest
{
    public string TerminalId { get; set; } = "";
    public string Symbol { get; set; } = "";
    public string Direction { get; set; } = "";   // LONG/SHORT
    public double EntryPrice { get; set; }
    public double SlPrice { get; set; }
    public double Lot { get; set; }
    public double TradeRisk { get; set; }          // $ at risk (from LotCalculator)
    public double AccountBalance { get; set; }
    public string Strategy { get; set; } = "";
    public int Magic { get; set; }

    /// <summary>R-cap limit for the strategy. null = no R-cap limit.</summary>
    public double? RCap { get; set; }

    /// <summary>All magic numbers assigned to this terminal (for own/foreign margin split in G6).</summary>
    public List<int>? TerminalMagics { get; set; }
}

/// <summary>Result of risk gate evaluation.</summary>
public class GateResult
{
    public bool Allowed { get; set; }
    public string Gate { get; set; } = "";         // Which gate rejected (e.g. "G2_DAILY_DD")
    public string? Reason { get; set; }

    /// <summary>If G5 reduced the lot in "reduce" mode, original lot before reduction.</summary>
    public double? OriginalLot { get; set; }
    /// <summary>If G5 reduced the lot in "reduce" mode, the reduced lot value.</summary>
    public double? ReducedLot { get; set; }

    public static GateResult Pass() => new() { Allowed = true };
    public static GateResult Reject(string gate, string reason) => new()
    {
        Allowed = false, Gate = gate, Reason = reason
    };
    public static GateResult PassWithReduction(double originalLot, double reducedLot, string info) => new()
    {
        Allowed = true, Gate = "G5_MARGIN_REDUCE", Reason = info,
        OriginalLot = originalLot, ReducedLot = reducedLot
    };
}
