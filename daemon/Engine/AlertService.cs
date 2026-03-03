using System.Net.Http.Json;
using System.Text.Json;
using Daemon.Connector;

namespace Daemon.Engine;

/// <summary>
/// Unified alert channel: logs to StateManager + sends to Telegram.
/// Debounce: same alert type + terminal no more than once per 5 minutes
/// (prevents spam when equity oscillates around threshold).
///
/// Phase 9 additions:
///   - Telegram Heartbeat: periodic status report when no signals (Addendum 23)
///   - Telegram Commands: readonly /status /positions /pnl /news (Addendum 24)
///
/// Telegram setup:
///   1. Create bot via @BotFather â†’ get token
///   2. Send /start to the bot from your Telegram account
///   3. Get chat_id via https://api.telegram.org/bot{token}/getUpdates
///   4. Set both in config.json or via dashboard
/// </summary>
public class AlertService : IDisposable
{
    private readonly StateManager _state;
    private readonly ConsoleLogger _log;
    private readonly HttpClient _http;
    private readonly HttpClient _httpLongPoll; // Separate client for 30s long-poll

    private string? _botToken;
    private string? _chatId;
    private bool _enabled;

    private readonly Dictionary<string, DateTime> _debounce = new();
    private readonly TimeSpan _debounceInterval = TimeSpan.FromMinutes(5);
    private readonly object _lock = new();

    // --- Heartbeat state ---
    private DateTime _lastHeartbeat = DateTime.UtcNow;
    private DateTime _lastSignalTime = DateTime.MinValue;
    private TimeSpan _heartbeatInterval = TimeSpan.FromHours(4);
    private DateTime _startTime = DateTime.UtcNow;

    // --- Telegram command polling ---
    private CancellationTokenSource? _pollingCts;
    private Task? _pollingTask;
    private int _updateOffset;

    // --- Mute mode ---
    private bool _muted;
    private DateTime _muteStart;
    private int _mutedEventCount;

    // --- Pending input state ---
    private string? _pendingCommand;  // e.g. "heartbeat" — waiting for user input

    // --- Service references for reports (wired via ConfigureServices) ---
    private ConnectorManager? _connector;
    private Daemon.Strategy.StrategyManager? _strategyMgr;
    private NewsService? _news;
    private VirtualTracker? _virtualTracker;
    private RiskManager? _riskManager;

    /// <summary>Set after construction (VirtualTracker is created later).</summary>
    public void SetVirtualTracker(VirtualTracker vt) => _virtualTracker = vt;

    /// <summary>Set after construction (RiskManager is created later).</summary>
    public void SetRiskManager(RiskManager rm) => _riskManager = rm;

    public bool TelegramConfigured => _enabled && !string.IsNullOrEmpty(_botToken) && !string.IsNullOrEmpty(_chatId);

    public AlertService(StateManager state, ConsoleLogger log)
    {
        _state = state;
        _log = log;
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        _httpLongPoll = new HttpClient { Timeout = TimeSpan.FromSeconds(40) }; // 30s poll + 10s buffer
    }

    /// <summary>Configure Telegram bot credentials. Can be called at any time (e.g. from dashboard).</summary>
    public void ConfigureTelegram(string? botToken, string? chatId)
    {
        _botToken = botToken?.Trim();
        _chatId = chatId?.Trim();
        _enabled = !string.IsNullOrEmpty(_botToken) && !string.IsNullOrEmpty(_chatId);

        if (_enabled)
            _log.Info($"Telegram alerts enabled (chat_id: {_chatId})");
        else
            _log.Info("Telegram alerts disabled (missing token or chat_id)");
    }

    /// <summary>Wire up service references needed for heartbeat/commands.</summary>
    public void ConfigureServices(
        ConnectorManager connector,
        Daemon.Strategy.StrategyManager strategyMgr,
        NewsService news,
        double heartbeatHours = 4)
    {
        _connector = connector;
        _strategyMgr = strategyMgr;
        _news = news;
        _heartbeatInterval = heartbeatHours > 0
            ? TimeSpan.FromHours(heartbeatHours)
            : TimeSpan.MaxValue; // disabled
        _startTime = DateTime.UtcNow;

        // Subscribe to terminal disconnect/reconnect events for immediate alerts
        connector.OnTerminalStatusChanged += OnTerminalStatusChanged;
    }

    private void OnTerminalStatusChanged(string terminalId, string newStatus)
    {
        if (newStatus == "disconnected")
        {
            _log.Warn($"[AlertService] Terminal {terminalId} disconnected â€” sending Telegram alert");
            _ = SendAsync("ALERT", terminalId,
                $"\U0001f534 Terminal {terminalId} DISCONNECTED", bypassDebounce: true);
            _state.LogEvent("ALERT", terminalId, null, "Terminal disconnected");
        }
        else if (newStatus == "connected")
        {
            _log.Info($"[AlertService] Terminal {terminalId} reconnected");
            _ = SendAsync("SYSTEM", terminalId,
                $"\U0001f7e2 Terminal {terminalId} reconnected", bypassDebounce: true);
            _state.LogEvent("SYSTEM", terminalId, null, "Terminal reconnected");
        }
    }

    // ===================================================================
    //  Start / Stop Telegram services (heartbeat + command polling)
    // ===================================================================

    /// <summary>
    /// Start the Telegram polling loop for commands + heartbeat timer.
    /// Call after ConfigureTelegram + ConfigureServices.
    /// </summary>
    public void StartTelegramServices(CancellationToken externalCt)
    {
        if (!TelegramConfigured)
        {
            _log.Info("[Telegram] Services not started (not configured)");
            return;
        }

        _pollingCts = CancellationTokenSource.CreateLinkedTokenSource(externalCt);
        _pollingTask = Task.Run(() => PollLoopAsync(_pollingCts.Token));
        _ = RegisterMenuAsync();
        _log.Info("[Telegram] Command polling + heartbeat started");
    }

    /// <summary>Combined polling loop: getUpdates + heartbeat check.</summary>
    private async Task PollLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                // --- Heartbeat check ---
                if (_heartbeatInterval < TimeSpan.MaxValue)
                {
                    var sinceLast = DateTime.UtcNow - _lastHeartbeat;
                    var sinceSignal = DateTime.UtcNow - _lastSignalTime;

                    if (sinceLast > _heartbeatInterval && sinceSignal > _heartbeatInterval)
                    {
                        var report = BuildStatusReport();
                        await SendTelegramRawAsync(report);
                        _lastHeartbeat = DateTime.UtcNow;

                        // Log heartbeat to AUDIT for dashboard visibility
                        var summary = BuildHeartbeatSummary();
                        _state.LogEvent("AUDIT", null, null,
                            $"heartbeat_sent: {summary}");

                        _log.Info("[Telegram] Heartbeat sent");
                    }
                }

                // --- Poll for commands (long poll 30s) ---
                var url = $"https://api.telegram.org/bot{_botToken}/getUpdates?offset={_updateOffset}&timeout=30";
                var resp = await _httpLongPoll.GetAsync(url, ct);

                if (!resp.IsSuccessStatusCode)
                {
                    _log.Warn($"[Telegram] getUpdates failed: {(int)resp.StatusCode}");
                    await Task.Delay(5000, ct);
                    continue;
                }

                var json = await resp.Content.ReadAsStringAsync(ct);
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                if (!root.GetProperty("ok").GetBoolean())
                {
                    await Task.Delay(5000, ct);
                    continue;
                }

                var results = root.GetProperty("result");
                foreach (var update in results.EnumerateArray())
                {
                    var updateId = update.GetProperty("update_id").GetInt32();
                    _updateOffset = updateId + 1;

                    if (!update.TryGetProperty("message", out var msg))
                        continue;

                    // Authorization check
                    var chatId = msg.GetProperty("chat").GetProperty("id").GetInt64().ToString();
                    if (chatId != _chatId)
                    {
                        _log.Warn($"[Telegram] Unauthorized command from chat_id={chatId}");
                        continue;
                    }

                    if (!msg.TryGetProperty("text", out var textProp))
                        continue;

                    var text = textProp.GetString() ?? "";
                    if (_pendingCommand != null && !text.StartsWith("/") && !IsKnownButtonText(text))
                    {
                        var reply = HandlePendingInput(text.Trim());
                        await SendTelegramRawAsync(reply);
                    }
                    else if (text.StartsWith("/") || IsKnownButtonText(text))
                    {
                        _pendingCommand = null;  // cancel any pending input
                        await HandleCommandAsync(text.Trim(), ct);
                    }
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (OperationCanceledException)
            {
                // HttpClient timeout, not real cancellation — retry
                _log.Warn("[Telegram] Long-poll timeout, retrying...");
            }
            catch (Exception ex)
            {
                _log.Error($"[Telegram] Poll error: {ex.Message}");
                try { await Task.Delay(5000, ct); } catch { break; }
            }
        }

        _log.Info("[Telegram] Polling stopped");
    }

    // ===================================================================
    //  Telegram Commands (Phase 1 â€” readonly)
    // ===================================================================

    private async Task HandleCommandAsync(string text, CancellationToken ct)
    {
        // Map keyboard button text to commands
        var mapped = MapKeyboardText(text);
        var parts = mapped.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var cmd = parts[0].ToLowerInvariant();

        _log.Info($"[Telegram] Command: {text}{(mapped != text ? $" → {mapped}" : "")}");
        _state.LogEvent("AUDIT", null, null, $"Telegram command: {text}");

        try
        {
            var response = cmd switch
            {
                "/status"    => BuildStatusReport(),
                "/positions" => BuildPositionsReport(),
                "/pnl"       => BuildPnlReport(),
                "/news"      => BuildNewsReport(),
                "/help"      => BuildHelpText(),
                "/mute"      => HandleMute(),
                "/unmute"    => HandleUnmute(),
                "/settings"  => BuildSettingsReport(),
                "/heartbeat" => HandleHeartbeat(parts),
                "/pause"     => HandlePause(parts),
                "/resume"    => HandleResume(),
                "/emergency" => "\U0001f6a7 <b>Not yet implemented</b>\nUse the Dashboard to manage positions.",
                "/close"     => "\U0001f6a7 <b>Not yet implemented</b>\nUse the Dashboard to close positions.",
                "/mode"      => "\U0001f6a7 <b>Not yet implemented</b>\nUse the Dashboard to switch terminal modes.",
                _            => $"Unknown command: {EscapeHtml(cmd)}\nType /help for available commands."
            };

            await SendTelegramRawAsync(response);
        }
        catch (Exception ex)
        {
            _log.Error($"[Telegram] Command {cmd} failed: {ex.Message}");
            await SendTelegramRawAsync($"\u26a0\ufe0f Command failed: {EscapeHtml(ex.Message)}");
        }
    }

    private string HandleHeartbeat(string[] parts)
    {
        // If called with argument — apply immediately
        if (parts.Length >= 2 && double.TryParse(parts[1], out var hours) && hours >= 0 && hours <= 24)
        {
            return ApplyHeartbeat(hours);
        }

        // No argument — ask for input
        var current = _heartbeatInterval < TimeSpan.MaxValue
            ? $"{_heartbeatInterval.TotalHours:F0}h"
            : "disabled";
        _pendingCommand = "heartbeat";
        return $"\U0001f493 Heartbeat: <b>{current}</b>\n\nEnter hours (1-24), or 0 to disable:";
    }

    private string ApplyHeartbeat(double hours)
    {
        _pendingCommand = null;
        if (hours == 0)
        {
            _heartbeatInterval = TimeSpan.MaxValue;
            _log.Info("[Telegram] Heartbeat disabled");
            return "\U0001f493 Heartbeat <b>disabled</b>";
        }
        _heartbeatInterval = TimeSpan.FromHours(hours);
        _lastHeartbeat = DateTime.UtcNow;
        _log.Info($"[Telegram] Heartbeat set to {hours}h");
        return $"\U0001f493 Heartbeat set to <b>every {hours}h</b>";
    }

    private string HandlePendingInput(string text)
    {
        if (_pendingCommand == "heartbeat")
        {
            if (double.TryParse(text, out var hours) && hours >= 0 && hours <= 24)
                return ApplyHeartbeat(hours);
            _pendingCommand = null;
            return "\u26a0\ufe0f Invalid. Enter a number 0-24 (or press any button to cancel).";
        }
        _pendingCommand = null;
        return "";
    }

    /// <summary>Map persistent keyboard button text → slash commands.</summary>
    private static string MapKeyboardText(string text)
    {
        var t = text.Trim();
        if (t.StartsWith("/")) return t;
        var lower = t.ToLowerInvariant();
        if (lower.Contains("status")) return "/status";
        if (lower.Contains("positions")) return "/positions";
        if (lower.Contains("p/l")) return "/pnl";
        if (lower.Contains("pnl")) return "/pnl";
        if (lower.Contains("unmute")) return "/unmute";
        if (lower.Contains("mute")) return "/mute";
        if (lower.Contains("settings")) return "/settings";
        if (lower.Contains("heartbeat")) return "/heartbeat";
        if (lower.Contains("resume")) return "/resume";
        if (lower.Contains("pause")) return "/pause";
        if (lower.Contains("news")) return "/news";
        if (lower.Contains("help")) return "/help";
        return $"/{t.Trim()}";
    }

    private static bool IsKnownButtonText(string text)
    {
        var t = text.ToLowerInvariant();
        return t.Contains("status") || t.Contains("positions") || t.Contains("p/l")
            || t.Contains("pnl") || t.Contains("news") || t.Contains("mute")
            || t.Contains("settings") || t.Contains("heartbeat") || t.Contains("help")
            || t.Contains("pause") || t.Contains("resume");
    }

    private string BuildHelpText()
    {
        var muteStatus = _muted ? " \U0001f507 <i>(muted)</i>" : "";
        return "\U0001f4cb <b>Trading Daemon — Commands</b>" + muteStatus + "\n\n"
             + "<b>\U0001f4ca Monitoring</b>\n"
             + "/status — Full status report\n"
             + "/positions — Open positions with P/L\n"
             + "/pnl — Daily P/L by terminal\n"
             + "/news — Upcoming news events (6h)\n"
             + "/settings — Terminal modes & config\n"
             + "/heartbeat [h] — Set heartbeat interval\n\n"
             + "<b>\u23f8 Pause trading</b>\n"
             + "/pause [min] [reason] — Pause all trading\n"
             + "/resume — Resume trading\n\n"
             + "<b>\U0001f507 Quiet mode</b>\n"
             + "/mute — Suppress non-critical alerts\n"
             + "/unmute — Resume alerts + summary\n\n"
             + "<b>\u26a1 Management</b> <i>(coming soon)</i>\n"
             + "/emergency — Close ALL positions\n"
             + "/close SYMBOL — Close by symbol\n"
             + "/mode MODE TERM — Switch terminal mode\n\n"
             + "/help — This message";
    }

    // ===================================================================
    //  Pause / Resume handlers
    // ===================================================================

    private string HandlePause(string[] parts)
    {
        if (_riskManager == null)
            return "\u26a0\ufe0f RiskManager not available";

        var (paused, _, _) = _riskManager.GetPauseState();
        if (paused)
            return "\u23f8 Already paused. Use /resume to resume.";

        int durationMin = 0;
        string? reason = null;

        // Parse: /pause [minutes] [reason...]
        if (parts.Length >= 2 && int.TryParse(parts[1], out var mins))
        {
            durationMin = mins;
            if (parts.Length >= 3)
                reason = string.Join(" ", parts.Skip(2));
        }
        else if (parts.Length >= 2)
        {
            // No duration, everything is reason
            reason = string.Join(" ", parts.Skip(1));
        }

        _riskManager.SetGlobalPause(true, durationMin, reason);

        var msg = "\u23f8 <b>Trading PAUSED</b>";
        if (durationMin > 0)
            msg += $"\nDuration: {durationMin}m (auto-resume)";
        else
            msg += "\nIndefinite — use /resume to resume";
        if (!string.IsNullOrEmpty(reason))
            msg += $"\nReason: {EscapeHtml(reason)}";

        return msg;
    }

    private string HandleResume()
    {
        if (_riskManager == null)
            return "\u26a0\ufe0f RiskManager not available";

        var (paused, _, _) = _riskManager.GetPauseState();
        if (!paused)
            return "\u25b6 Trading is already active.";

        _riskManager.SetGlobalPause(false);
        return "\u25b6 <b>Trading RESUMED</b>";
    }

    private string BuildSettingsReport()
    {
        if (_connector == null) return "\u26a0\ufe0f Not ready";

        var sb = new System.Text.StringBuilder();
        sb.AppendLine("\u2699\ufe0f <b>Settings</b>\n");

        // Pause state
        if (_riskManager != null)
        {
            var (paused, until, reason) = _riskManager.GetPauseState();
            if (paused)
            {
                sb.Append("\u23f8 <b>PAUSED</b>");
                if (until.HasValue) sb.Append($" until {until.Value:HH:mm} UTC");
                if (!string.IsNullOrEmpty(reason)) sb.Append($" — {EscapeHtml(reason)}");
                sb.AppendLine();
            }
            else
            {
                sb.AppendLine("\u25b6 Trading: Active");
            }
        }

        // Mute state
        sb.AppendLine(_muted
            ? $"\U0001f507 Mute: ON (since {_muteStart:HH:mm} UTC)"
            : "\U0001f50a Mute: OFF");

        // Heartbeat
        if (_heartbeatInterval < TimeSpan.MaxValue)
            sb.AppendLine($"\U0001f493 Heartbeat: every {_heartbeatInterval.TotalHours:F0}h");
        else
            sb.AppendLine("\U0001f493 Heartbeat: disabled");

        // Terminal modes
        sb.AppendLine();
        foreach (var termId in _connector.GetAllTerminalIds())
        {
            var profile = _state.GetProfile(termId);
            var mode = profile?.Mode ?? "unknown";
            var modeEmoji = mode switch { "live" => "\U0001f7e2", "virtual" => "\U0001f7e3", "monitor" => "\u26aa", _ => "\u2753" };
            sb.AppendLine($"  {modeEmoji} {EscapeHtml(termId)}: {mode}");
        }

        return sb.ToString().TrimEnd();
    }

    // ===================================================================
    //  Mute mode
    // ===================================================================

    private string HandleMute()
    {
        if (_muted) return "\U0001f507 Already muted (since " + _muteStart.ToString("HH:mm") + " UTC)";
        _muted = true;
        _muteStart = DateTime.UtcNow;
        _mutedEventCount = 0;
        _log.Info("[Telegram] Mute mode enabled");
        return "\U0001f507 <b>Muted</b>\n"
             + "Non-critical alerts suppressed.\n"
             + "\U0001f6a8 EMERGENCY alerts still pass through.\n"
             + "Use /unmute to resume.";
    }

    private string HandleUnmute()
    {
        if (!_muted) return "\U0001f50a Alerts already active.";
        _muted = false;
        var duration = DateTime.UtcNow - _muteStart;
        var durStr = duration.TotalHours >= 1
            ? $"{(int)duration.TotalHours}h {duration.Minutes}m"
            : $"{(int)duration.TotalMinutes}m";
        _log.Info($"[Telegram] Mute mode disabled ({_mutedEventCount} events suppressed)");
        return $"\U0001f50a <b>Unmuted</b>\n"
             + $"Was muted for {durStr}.\n"
             + $"\U0001f4e8 {_mutedEventCount} alert(s) suppressed during mute.";
    }

    /// <summary>Check if an alert should be muted.</summary>
    private bool ShouldMute(string level)
    {
        if (!_muted) return false;
        // EMERGENCY always passes through
        if (level == "EMERGENCY") return false;
        _mutedEventCount++;
        return true;
    }

    // ===================================================================
    //  Telegram Menu Registration (setMyCommands)
    // ===================================================================

    private async Task RegisterMenuAsync()
    {
        if (!TelegramConfigured) return;
        try
        {
            // Clear slash-command menu (we use keyboard buttons instead)
            var url = $"https://api.telegram.org/bot{_botToken}/deleteMyCommands";
            await _http.PostAsync(url, null);

            // Set persistent keyboard
            await SendPersistentKeyboardAsync();
        }
        catch (Exception ex)
        {
            _log.Warn($"[Telegram] Keyboard setup failed: {ex.Message}");
        }
    }

    /// <summary>Build the persistent keyboard reply_markup object.</summary>
    private static object BuildKeyboardMarkup()
    {
        return new
        {
            keyboard = new[]
            {
                new[] { new { text = "\U0001f4ca Status" },  new { text = "\U0001f4e6 Positions" }, new { text = "\U0001f4b0 P/L" } },
                new[] { new { text = "\U0001f4f0 News" },    new { text = "\u2699\ufe0f Settings" }, new { text = "\U0001f493 Heartbeat" } },
                new[] { new { text = "\u23f8 Pause" },       new { text = "\u25b6 Resume" },        new { text = "\U0001f507 Mute" } },
                new[] { new { text = "\U0001f50a Unmute" },   new { text = "\u2753 Help" } },
            },
            resize_keyboard = true,
        };
    }

    /// <summary>Send a startup message that sets the persistent keyboard.</summary>
    private async Task SendPersistentKeyboardAsync()
    {
        if (!TelegramConfigured) return;
        try
        {
            var url = $"https://api.telegram.org/bot{_botToken}/sendMessage";
            var payload = new
            {
                chat_id = _chatId,
                text = "\U0001f7e2 Daemon online",
                parse_mode = "HTML",
                disable_web_page_preview = true,
                reply_markup = BuildKeyboardMarkup(),
            };
            await _http.PostAsJsonAsync(url, payload);
        }
        catch (Exception ex)
        {
            _log.Warn($"[Telegram] Keyboard setup failed: {ex.Message}");
        }
    }

    /// <summary>Send a Telegram message with the persistent keyboard attached.</summary>
    private async Task<bool> SendTelegramWithKeyboardAsync(string htmlText)
    {
        try
        {
            var url = $"https://api.telegram.org/bot{_botToken}/sendMessage";
            var payload = new
            {
                chat_id = _chatId,
                text = htmlText,
                parse_mode = "HTML",
                disable_web_page_preview = true,
                reply_markup = BuildKeyboardMarkup(),
            };

            var resp = await _http.PostAsJsonAsync(url, payload);
            if (!resp.IsSuccessStatusCode)
            {
                var body = await resp.Content.ReadAsStringAsync();
                _log.Error($"Telegram API error {(int)resp.StatusCode}: {body}");
                return false;
            }
            return true;
        }
        catch (Exception ex)
        {
            _log.Error($"Telegram send failed: {ex.Message}");
            return false;
        }
    }

    // ===================================================================
    //  Report builders
    // ===================================================================

    private string BuildStatusReport()
    {
        var sb = new System.Text.StringBuilder();
        var uptime = DateTime.UtcNow - _startTime;
        var uptimeStr = uptime.TotalDays >= 1
            ? $"{(int)uptime.TotalDays}d {uptime.Hours}h"
            : $"{(int)uptime.TotalHours}h {uptime.Minutes}m";

        var intervalLabel = _heartbeatInterval.TotalHours >= 1
            ? $"{_heartbeatInterval.TotalHours:F0}h"
            : $"{_heartbeatInterval.TotalMinutes:F0}m";

        sb.AppendLine($"\U0001f4ca <b>Status Report</b> ({intervalLabel})");

        // Daemon uptime
        sb.AppendLine($"\U0001f7e2 Daemon: OK (uptime {uptimeStr})");

        // Pause state
        if (_riskManager != null)
        {
            var (paused, until, reason) = _riskManager.GetPauseState();
            if (paused)
            {
                sb.Append("\u23f8 <b>PAUSED</b>");
                if (until.HasValue)
                    sb.Append($" (until {until.Value:HH:mm} UTC)");
                if (!string.IsNullOrEmpty(reason))
                    sb.Append($" — {EscapeHtml(reason)}");
                sb.AppendLine();
            }
        }

        // Terminals â€” verify actual connectivity via AccountInfo (TcpClient.Connected is unreliable)
        if (_connector != null)
        {
            var allIds = _connector.GetEnabledTerminalIds();
            var disconnected = new List<string>();
            int connCount = 0;

            foreach (var id in allIds)
            {
                bool alive = false;
                if (_connector.IsConnected(id))
                {
                    try
                    {
                        var acc = _connector.GetAccountInfoAsync(id, CancellationToken.None).Result;
                        alive = acc != null;
                    }
                    catch { }
                }
                if (alive) connCount++;
                else disconnected.Add(id);
            }

            var dot = connCount == allIds.Count ? "\U0001f7e2" : "\U0001f7e1";
            sb.AppendLine($"{dot} Terminals: {connCount}/{allIds.Count} connected");
            foreach (var dc in disconnected)
                sb.AppendLine($"  \U0001f534 {EscapeHtml(dc)}: disconnected");
        }

        // Strategy
        if (_strategyMgr != null)
        {
            var running = _strategyMgr.GetAllProcesses()
                .Where(p => p.Status == Daemon.Strategy.StrategyStatus.Running)
                .ToList();
            if (running.Count > 0)
            {
                var names = string.Join(", ", running.Select(r => r.StrategyName).Distinct());
                sb.AppendLine($"\U0001f7e2 Strategy: {names} running");
            }
            else
            {
                sb.AppendLine("\u26aa Strategy: none running");
            }
        }

        // Last signal
        var lastSignalEvents = _state.GetEvents(type: "SIGNAL", limit: 1);
        if (lastSignalEvents.Count > 0)
        {
            var e = lastSignalEvents[0];
            try
            {
                var age = DateTime.UtcNow - DateTime.Parse(e.Timestamp);
                var ageStr = age.TotalHours >= 1 ? $"{(int)age.TotalHours}h ago" : $"{(int)age.TotalMinutes}m ago";
                sb.AppendLine($"\nSignals: last {ageStr}");
            }
            catch { sb.AppendLine("\nSignals: --"); }
        }
        else
        {
            sb.AppendLine("\nSignals: none");
        }

        // Positions (real + virtual)
        int posCount = 0;
        double totalFloating = 0;
        if (_connector != null)
        {
            foreach (var termId in _connector.GetAllTerminalIds())
            {
                if (!_connector.IsConnected(termId)) continue;
                try
                {
                    var profile = _state.GetProfile(termId);
                    if (profile?.Mode == "virtual" && _virtualTracker != null)
                    {
                        var vPositions = _state.GetOpenVirtualPositions(termId);
                        posCount += vPositions.Count;
                        totalFloating += _virtualTracker.GetUnrealizedPnl(termId);
                    }
                    else
                    {
                        var acc = _connector.GetAccountInfoAsync(termId, CancellationToken.None).Result;
                        if (acc != null) totalFloating += acc.Profit;
                        var positions = _connector.GetPositionsAsync(termId, CancellationToken.None).Result;
                        if (positions != null) posCount += positions.Count;
                    }
                }
                catch { }
            }
        }

        if (posCount > 0)
            sb.AppendLine($"Open: {posCount} positions, floating {FormatPnl(totalFloating)}");
        else
            sb.AppendLine("Open positions: 0");

        // Daily P/L (per-terminal broker timezone)
        double totalDailyPnl = 0;
        if (_connector != null)
        {
            foreach (var termId in _connector.GetAllTerminalIds())
            {
                var profile = _state.GetProfile(termId);
                var brokerDate = GetBrokerDate(profile?.ServerTimezone ?? "UTC");
                var (realized, _, _) = _state.GetDailyPnl(termId, brokerDate);
                if (_connector.IsConnected(termId))
                {
                    try
                    {
                        if (profile?.Mode == "virtual" && _virtualTracker != null)
                        {
                            totalDailyPnl += realized + _virtualTracker.GetUnrealizedPnl(termId);
                        }
                        else
                        {
                            var acc = _connector.GetAccountInfoAsync(termId, CancellationToken.None).Result;
                            if (acc != null) totalDailyPnl += realized + acc.Profit;
                            else totalDailyPnl += realized;
                        }
                    }
                    catch { totalDailyPnl += realized; }
                }
                else
                {
                    totalDailyPnl += realized;
                }
            }
        }
        sb.AppendLine($"Daily P/L: {FormatPnl(totalDailyPnl)}");

        // Upcoming news
        if (_news != null)
        {
            var upcoming = _news.GetAllEvents()
                .Where(e => e.TimeUtc > DateTime.UtcNow && e.TimeUtc < DateTime.UtcNow.AddHours(6))
                .OrderBy(e => e.TimeUtc)
                .Take(3)
                .ToList();

            if (upcoming.Count > 0)
            {
                var first = upcoming[0];
                var delta = first.TimeUtc - DateTime.UtcNow;
                var deltaStr = delta.TotalHours >= 1 ? $"{(int)delta.TotalHours}h {delta.Minutes}m" : $"{delta.Minutes}m";
                sb.AppendLine($"News ahead: {first.Currency} {EscapeHtml(first.Title)} in {deltaStr}");
            }
        }

        return sb.ToString().TrimEnd();
    }

    /// <summary>Build a compact one-line summary for AUDIT log.</summary>
    private string BuildHeartbeatSummary()
    {
        var parts = new List<string>();
        if (_connector != null)
        {
            var allIds = _connector.GetEnabledTerminalIds();
            int connCount = 0;
            foreach (var id in allIds)
            {
                if (_connector.IsConnected(id))
                {
                    try { if (_connector.GetAccountInfoAsync(id, CancellationToken.None).Result != null) connCount++; }
                    catch { }
                }
            }
            parts.Add($"terminals={connCount}/{allIds.Count}");
        }

        int posCount = 0;
        double floating = 0;
        if (_connector != null)
        {
            foreach (var termId in _connector.GetAllTerminalIds())
            {
                if (!_connector.IsConnected(termId)) continue;
                try
                {
                    var positions = _connector.GetPositionsAsync(termId, CancellationToken.None).Result;
                    if (positions != null) posCount += positions.Count;
                    var acc = _connector.GetAccountInfoAsync(termId, CancellationToken.None).Result;
                    if (acc != null) floating += acc.Profit;
                }
                catch { }
            }
        }
        parts.Add($"positions={posCount}");
        parts.Add($"floating={FormatPnl(floating)}");

        return string.Join(", ", parts);
    }

    private string BuildPositionsReport()
    {
        if (_connector == null) return "\u26a0\ufe0f Not ready";

        var lines = new List<string> { "\U0001f4cb <b>Open Positions</b>\n" };
        int totalCount = 0;
        double totalPnl = 0;

        foreach (var termId in _connector.GetAllTerminalIds())
        {
            if (!_connector.IsConnected(termId)) continue;
            var profile = _state.GetProfile(termId);

            try
            {
                // Virtual positions
                if (profile?.Mode == "virtual" && _virtualTracker != null)
                {
                    var vPositions = _state.GetOpenVirtualPositions(termId);
                    if (vPositions.Count == 0) continue;

                    lines.Add($"<b>{EscapeHtml(termId)}</b> [V]");
                    foreach (var vp in vPositions)
                    {
                        var dir = vp.Direction == "BUY" ? "LONG" : "SHORT";
                        lines.Add($"  \ud83d\udfe3{EscapeHtml(vp.Symbol)} {dir} {vp.Volume}");
                        totalCount++;
                    }
                    double vPnl = _virtualTracker.GetUnrealizedPnl(termId);
                    totalPnl += vPnl;
                    lines.Add($"  floating {FormatPnl(vPnl)}");
                }
                else
                {
                    // Real positions
                    var positions = _connector.GetPositionsAsync(termId, CancellationToken.None).Result;
                    if (positions == null || positions.Count == 0) continue;

                    lines.Add($"<b>{EscapeHtml(termId)}</b>");
                    foreach (var p in positions)
                    {
                        var dir = p.IsBuy ? "LONG" : "SHORT";
                        lines.Add($"  {EscapeHtml(p.Symbol)} {dir} {p.Volume} {FormatPnl(p.Profit)}");
                        totalPnl += p.Profit;
                        totalCount++;
                    }
                }
            }
            catch { }
        }

        if (totalCount == 0)
            return "\U0001f4cb <b>Open Positions</b>\n\nNo open positions";

        lines.Add($"\nTotal: {totalCount} position(s), {FormatPnl(totalPnl)}");
        return string.Join("\n", lines);
    }

    private string BuildPnlReport()
    {
        if (_connector == null) return "\u26a0\ufe0f Not ready";

        var lines = new List<string> { "\U0001f4b0 <b>Daily P/L</b>\n" };
        double grandTotal = 0;

        foreach (var termId in _connector.GetAllTerminalIds())
        {
            var profile = _state.GetProfile(termId);
            var brokerDate = GetBrokerDate(profile?.ServerTimezone ?? "UTC");
            var (realized, _, _) = _state.GetDailyPnl(termId, brokerDate);
            double floating = 0;

            if (_connector.IsConnected(termId))
            {
                try
                {
                    if (profile?.Mode == "virtual" && _virtualTracker != null)
                    {
                        floating = _virtualTracker.GetUnrealizedPnl(termId);
                    }
                    else
                    {
                        var acc = _connector.GetAccountInfoAsync(termId, CancellationToken.None).Result;
                        if (acc != null) floating = acc.Profit;
                    }
                }
                catch { }
            }

            var total = realized + floating;
            grandTotal += total;

            lines.Add($"  {EscapeHtml(termId)}: {FormatPnl(total)}");
            if (Math.Abs(realized) > 0.01 || Math.Abs(floating) > 0.01)
                lines.Add($"    realized {FormatPnl(realized)} + floating {FormatPnl(floating)}");
        }

        lines.Add($"\nTotal: {FormatPnl(grandTotal)}");
        return string.Join("\n", lines);
    }

    private string BuildNewsReport()
    {
        if (_news == null) return "\u26a0\ufe0f News service not available";

        var upcoming = _news.GetAllEvents()
            .Where(e => e.TimeUtc > DateTime.UtcNow && e.TimeUtc < DateTime.UtcNow.AddHours(6))
            .OrderBy(e => e.TimeUtc)
            .Take(10)
            .ToList();

        if (upcoming.Count == 0)
            return "\U0001f4f0 <b>Upcoming News (6h)</b>\n\nNo high-impact events";

        var lines = new List<string> { "\U0001f4f0 <b>Upcoming News (6h)</b>\n" };
        foreach (var e in upcoming)
        {
            var delta = e.TimeUtc - DateTime.UtcNow;
            var deltaStr = delta.TotalHours >= 1 ? $"{(int)delta.TotalHours}h {delta.Minutes}m" : $"{delta.Minutes}m";
            var impact = e.Impact >= 3 ? "\U0001f534" : e.Impact >= 2 ? "\U0001f7e1" : "\u26aa";
            lines.Add($"  {impact} {e.Currency} {EscapeHtml(e.Title)} in {deltaStr}");
        }

        return string.Join("\n", lines);
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

    private static string FormatPnl(double pnl)
    {
        var sign = pnl >= 0 ? "+" : "";
        return $"{sign}${pnl:F2}";
    }

    // ===================================================================
    //  Signal tracking (for heartbeat timer reset)
    // ===================================================================

    /// <summary>
    /// Call when any signal is sent (signal or block). Resets the heartbeat timer
    /// so heartbeat is not sent when there's been recent activity.
    /// </summary>
    public void NotifySignalSent()
    {
        _lastSignalTime = DateTime.UtcNow;
        _lastHeartbeat = DateTime.UtcNow;
    }

    // ===================================================================
    //  Core alert sending (unchanged from original)
    // ===================================================================

    /// <summary>
    /// Send an alert. Level: INFO, YELLOW, RED, EMERGENCY.
    /// Debounce prevents same alert from firing more than once per 5 minutes.
    /// </summary>
    public async Task SendAsync(string level, string terminalId, string message,
                                 string? strategy = null, bool bypassDebounce = false,
                                 bool rawHtml = false)
    {
        // Debounce check
        var debounceKey = $"{level}:{terminalId}:{message.GetHashCode()}";

        if (!bypassDebounce)
        {
            lock (_lock)
            {
                if (_debounce.TryGetValue(debounceKey, out var lastSent))
                {
                    if (DateTime.UtcNow - lastSent < _debounceInterval)
                    {
                        return; // Suppress duplicate alert
                    }
                }
                _debounce[debounceKey] = DateTime.UtcNow;
            }
        }
        else
        {
            lock (_lock) { _debounce[debounceKey] = DateTime.UtcNow; }
        }

        // Log to StateManager (always)
        _state.LogEvent("ALERT", terminalId, strategy,
            $"[{level}] {message}",
            JsonSerializer.Serialize(new { level, terminalId, message, timestamp = DateTime.UtcNow }));

        // Console log
        switch (level)
        {
            case "EMERGENCY":
            case "RED":
                _log.Error($"[{terminalId}] ALERT [{level}]: {message}");
                break;
            case "YELLOW":
                _log.Warn($"[{terminalId}] ALERT [{level}]: {message}");
                break;
            default:
                _log.Info($"[{terminalId}] ALERT [{level}]: {message}");
                break;
        }

        // Send to Telegram (if configured)
        if (TelegramConfigured)
        {
            if (!ShouldMute(level))
            {
                if (rawHtml)
                    await SendTelegramRawAsync(message);
                else
                    await SendTelegramAsync(level, terminalId, message);
            }
        }
    }

    /// <summary>Send test message to verify Telegram configuration.</summary>
    public async Task<(bool Success, string Message)> TestTelegramAsync()
    {
        if (!TelegramConfigured)
            return (false, "Telegram not configured (missing bot_token or chat_id)");

        try
        {
            var text = "\u2705 <b>Trading Daemon</b>\nTelegram alert test \u2014 connection OK!";
            var success = await SendTelegramRawAsync(text);
            return success
                ? (true, "Test message sent successfully")
                : (false, "Telegram API returned error");
        }
        catch (Exception ex)
        {
            return (false, $"Failed: {ex.Message}");
        }
    }

    /// <summary>Clear debounce cache (for testing or daily reset).</summary>
    public void ResetDebounce()
    {
        lock (_lock) { _debounce.Clear(); }
    }

    // ===================================================================
    // Telegram Bot API
    // ===================================================================

    private async Task SendTelegramAsync(string level, string terminalId, string message)
    {
        var emoji = level switch
        {
            "EMERGENCY" => "\U0001f6a8",
            "RED"       => "\U0001f534",
            "YELLOW"    => "\U0001f7e1",
            "INFO"      => "\u2139\ufe0f",
            "SIGNAL"    => "\u26a1",
            "RISK"      => "\U0001f6ab",
            "ORDER"     => "\U0001f4c8",
            _           => "\U0001f4cb",
        };

        var text = $"{emoji} <b>{EscapeHtml(level)}</b> [{EscapeHtml(terminalId)}]\n{EscapeHtml(message)}";
        await SendTelegramRawAsync(text);
    }

    private async Task<bool> SendTelegramRawAsync(string htmlText)
    {
        try
        {
            var url = $"https://api.telegram.org/bot{_botToken}/sendMessage";
            var payload = new
            {
                chat_id = _chatId,
                text = htmlText,
                parse_mode = "HTML",
                disable_web_page_preview = true,
            };

            var resp = await _http.PostAsJsonAsync(url, payload);

            if (!resp.IsSuccessStatusCode)
            {
                var body = await resp.Content.ReadAsStringAsync();
                _log.Error($"Telegram API error {(int)resp.StatusCode}: {body}");
                return false;
            }

            return true;
        }
        catch (TaskCanceledException)
        {
            _log.Error("Telegram send timed out");
            return false;
        }
        catch (Exception ex)
        {
            _log.Error($"Telegram send failed: {ex.Message}");
            return false;
        }
    }

    private static string EscapeHtml(string text) =>
        text.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");

    public void Dispose()
    {
        _pollingCts?.Cancel();
        try { _pollingTask?.Wait(TimeSpan.FromSeconds(3)); } catch { }
        _pollingCts?.Dispose();
        _http.Dispose();
        _httpLongPoll.Dispose();
    }
}
