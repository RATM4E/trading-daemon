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
using Daemon.Tester;

namespace Daemon.Dashboard;

/// <summary>
/// Embedded HTTP + WebSocket server for the Trading Daemon dashboard.
///
/// HTTP: serves static files from wwwroot/ folder (index.html, app.js, style.css).
/// WebSocket (/ws): bidirectional channel for commands and push events.
///
/// Architecture:
/// - HttpListener on configurable port (default 8080)
/// - All WS clients tracked in ConcurrentDictionary for broadcast
/// - Commands from dashboard are processed and responded to inline
/// - Push events (position changes, alerts, strategy status) are broadcast to all clients
///
/// Phase 7 of Trading Daemon Implementation Plan.
/// </summary>
public partial class DashboardServer : IDisposable
{
    private readonly DaemonConfig _config;
    private readonly StateManager _state;
    private readonly ConnectorManager _connector;
    private readonly StrategyManager _strategyMgr;
    private readonly NewsService _news;
    private readonly AlertService _alerts;
    private readonly ILogger _log;
    private readonly string? _configPath;
    private BarsCache? _barsCache;
    private VirtualTracker? _virtualTracker;
    private RiskManager? _riskManager;

    private HttpListener? _listener;
    private readonly ConcurrentDictionary<string, WebSocket> _clients = new();
    private readonly string _wwwrootPath;
    private bool _disposed;
    private int _clientIdCounter;

    // Per-class leverage cache: terminalId â†’ { "FX": 100, "IDX": 50, ... }
    private readonly Dictionary<string, Dictionary<string, int>> _classLeverage = new();
    private readonly Dictionary<string, DateTime> _leverageDetectedAt = new();
    // Cached virtual unrealized P/L (updated by HandleGetPositions every 5s)
    private readonly Dictionary<string, double> _cachedVirtualUnrealized = new();

    // Representative symbols per asset class for leverage detection
    // FX: worker uses account_leverage directly (ignores symbol)
    // Non-FX: worker tries these first, then auto-discovers alternatives
    private static readonly Dictionary<string, string> DefaultLeverageSymbols = new()
    {
        ["FX"] = "EURUSD", ["IDX"] = "NAS100", ["XAU"] = "XAUUSD",
        ["OIL"] = "XTIUSD", ["CRYP"] = "BTCUSD"
    };

 // MIME type map for static files
    private static readonly Dictionary<string, string> MimeTypes = new()
    {
        [".html"] = "text/html; charset=utf-8",
        [".js"]   = "application/javascript; charset=utf-8",
        [".css"]  = "text/css; charset=utf-8",
        [".json"] = "application/json; charset=utf-8",
        [".png"]  = "image/png",
        [".svg"]  = "image/svg+xml",
        [".ico"]  = "image/x-icon",
        [".woff2"] = "font/woff2",
    };

    public int Port => _config.DashboardPort;
    public int ClientCount => _clients.Count;

 /// <summary>
 /// Callback invoked when shutdown is requested from dashboard.
 /// Parameter: killTerminals (true = also close MT5 processes).
 /// Program.cs wires this to cts.Cancel().
 /// </summary>
    public Action<bool>? OnShutdownRequested { get; set; }

 /// <summary>Set BarsCache reference for sizing preview calculations.</summary>
    public void SetBarsCache(BarsCache cache) => _barsCache = cache;

 /// <summary>Set VirtualTracker reference for unrealized P&L.</summary>
    public void SetVirtualTracker(VirtualTracker tracker) => _virtualTracker = tracker;

 /// <summary>Set RiskManager reference for global pause control.</summary>
    public void SetRiskManager(RiskManager rm) => _riskManager = rm;

    public DashboardServer(
        DaemonConfig config,
        StateManager state,
        ConnectorManager connector,
        StrategyManager strategyMgr,
        NewsService news,
        AlertService alerts,
        ILogger log,
        string? configPath = null)
    {
        _config = config;
        _state = state;
        _connector = connector;
        _strategyMgr = strategyMgr;
        _news = news;
        _alerts = alerts;
        _log = log;
        _configPath = configPath;

 // wwwroot path: relative to the executable
        _wwwrootPath = Path.GetFullPath(
            _config.DashboardWwwroot ?? "wwwroot",
            AppContext.BaseDirectory);

        // Restore persisted leverage from DB (survives restarts)
        foreach (var term in _config.Terminals)
        {
            var lev = _state.GetClassLeverage(term.Id);
            if (lev.Count > 0)
            {
                _classLeverage[term.Id] = lev;
                _leverageDetectedAt[term.Id] = DateTime.UtcNow; // mark as loaded
            }
        }
    }

 // ===================================================================
 // Start / Stop
 // ===================================================================

 /// <summary>Start the HTTP + WebSocket server (non-blocking).</summary>
    public Task StartAsync(CancellationToken ct = default)
    {
        if (!_config.DashboardEnabled)
        {
            _log.Info("[Dashboard] Disabled in config");
            return Task.CompletedTask;
        }

 var prefix = $"http://{_config.DashboardHost}:{_config.DashboardPort}/";

        _listener = new HttpListener();
        _listener.Prefixes.Add(prefix);

        try
        {
            _listener.Start();
            _log.Info($"[Dashboard] Listening on {prefix}");
            _log.Info($"[Dashboard] wwwroot: {_wwwrootPath}");
        }
        catch (HttpListenerException ex)
        {
            _log.Error($"[Dashboard] Failed to start: {ex.Message}");
            _log.Error($"[Dashboard] Try running: netsh http add urlacl url={prefix} user=Everyone");
            return Task.CompletedTask;
        }

 // Fire-and-forget accept loop
        _ = AcceptLoopAsync(ct);

 // Push updated strategy list when auto-discovery detects changes
        _strategyMgr.OnStrategiesChanged += () =>
        {
            _ = Task.Run(async () =>
            {
                try { await BroadcastAsync(HandleGetStrategies()); }
                catch (Exception ex) { _log.Error($"[Dashboard] Failed to push strategy changes: {ex.Message}"); }
            });
        };

        return Task.CompletedTask;
    }

    public void Stop()
    {
        try
        {
            _listener?.Stop();
            _listener?.Close();
        }
        catch { }

 // Close all WebSocket connections
        foreach (var (id, ws) in _clients)
        {
            try
            {
                if (ws.State == WebSocketState.Open)
                    ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "Server stopping",
                        CancellationToken.None).Wait(TimeSpan.FromSeconds(2));
            }
            catch { }
        }
        _clients.Clear();

        _log.Info("[Dashboard] Stopped");
    }

 // ===================================================================
 // Accept loop
 // ===================================================================

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && _listener?.IsListening == true)
        {
            try
            {
                var context = await _listener.GetContextAsync();

 // Handle each request in background
                _ = Task.Run(() => HandleRequestAsync(context, ct), ct);
            }
            catch (ObjectDisposedException) { break; }
            catch (HttpListenerException) { break; }
            catch (Exception ex)
            {
                _log.Error($"[Dashboard] Accept error: {ex.Message}");
            }
        }
    }

    private async Task HandleRequestAsync(HttpListenerContext context, CancellationToken ct)
    {
        var path = context.Request.Url?.AbsolutePath ?? "/";

        try
        {
 // WebSocket upgrade
            if (path == "/ws" && context.Request.IsWebSocketRequest)
            {
                await HandleWebSocketAsync(context, ct);
                return;
            }

 // Not a WebSocket request on /ws
            if (path == "/ws")
            {
                context.Response.StatusCode = 400;
                context.Response.Close();
                return;
            }

 // API: health check
            if (path == "/api/health")
            {
                await RespondJsonAsync(context.Response, new { status = "ok", uptime = Environment.TickCount64 / 1000 });
                return;
            }

 // Static files
            await ServeStaticFileAsync(context, path);
        }
        catch (Exception ex)
        {
            _log.Error($"[Dashboard] Request error {path}: {ex.Message}");
            try
            {
                context.Response.StatusCode = 500;
                context.Response.Close();
            }
            catch { }
        }
    }

 // ===================================================================
 // Static file serving
 // ===================================================================

    private async Task ServeStaticFileAsync(HttpListenerContext context, string path)
    {
 // Default to index.html
        if (path == "/") path = "/index.html";

 // Security: prevent path traversal
        var fullPath = Path.GetFullPath(Path.Combine(_wwwrootPath, path.TrimStart('/')));
        if (!fullPath.StartsWith(_wwwrootPath, StringComparison.OrdinalIgnoreCase))
        {
            context.Response.StatusCode = 403;
            context.Response.Close();
            return;
        }

        if (!File.Exists(fullPath))
        {
 // SPA fallback: serve index.html for unknown routes
            fullPath = Path.Combine(_wwwrootPath, "index.html");
            if (!File.Exists(fullPath))
            {
                context.Response.StatusCode = 404;
                var msg = Encoding.UTF8.GetBytes("Not found");
                context.Response.ContentType = "text/plain";
                await context.Response.OutputStream.WriteAsync(msg);
                context.Response.Close();
                return;
            }
        }

        var ext = Path.GetExtension(fullPath).ToLowerInvariant();
        context.Response.ContentType = MimeTypes.GetValueOrDefault(ext, "application/octet-stream");
        context.Response.StatusCode = 200;

 // No caching — ensures fresh files on every load
        context.Response.Headers["Cache-Control"] = "no-cache, no-store, must-revalidate";

        var content = await File.ReadAllBytesAsync(fullPath);
        context.Response.ContentLength64 = content.Length;
        await context.Response.OutputStream.WriteAsync(content);
        context.Response.Close();
    }

 // ===================================================================
 // WebSocket handling
 // ===================================================================

    private async Task HandleWebSocketAsync(HttpListenerContext context, CancellationToken ct)
    {
        WebSocketContext wsContext;
        try
        {
            wsContext = await context.AcceptWebSocketAsync(null);
        }
        catch (Exception ex)
        {
            _log.Error($"[Dashboard] WebSocket upgrade failed: {ex.Message}");
            context.Response.StatusCode = 500;
            context.Response.Close();
            return;
        }

        var ws = wsContext.WebSocket;
        var clientId = $"ws-{Interlocked.Increment(ref _clientIdCounter)}";
        _clients[clientId] = ws;

        _log.Info($"[Dashboard] Client {clientId} connected ({_clients.Count} total)");

        var buffer = new byte[8192];

        try
        {
            while (ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
            {
                var result = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), ct);

                if (result.MessageType == WebSocketMessageType.Close)
                {
                    await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "Bye", ct);
                    break;
                }

                if (result.MessageType == WebSocketMessageType.Text)
                {
                    var message = Encoding.UTF8.GetString(buffer, 0, result.Count);
                    await ProcessCommandAsync(ws, clientId, message, ct);
                }
            }
        }
        catch (WebSocketException) { }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _log.Error($"[Dashboard] Client {clientId} error: {ex.Message}");
        }
        finally
        {
            _clients.TryRemove(clientId, out _);
            _log.Info($"[Dashboard] Client {clientId} disconnected ({_clients.Count} remaining)");
            ws.Dispose();
        }
    }

 // ===================================================================
 // Command processing  â€” Dashboard  â€” Daemon
 // ===================================================================

    private async Task ProcessCommandAsync(WebSocket ws, string clientId, string rawMessage,
                                            CancellationToken ct)
    {
        JsonDocument? doc = null;
        string cmd = "";

        try
        {
            doc = JsonDocument.Parse(rawMessage);
            cmd = doc.RootElement.GetProperty("cmd").GetString() ?? "";
        }
        catch
        {
            await SendToClientAsync(ws, new { error = "Invalid JSON" }, ct);
            return;
        }

        try
        {
            object? response = cmd switch
            {
                "get_terminals"   => await HandleGetTerminalsAsync(),
                "get_positions"   => await HandleGetPositions(doc.RootElement, ct),
                "get_strategies"  => HandleGetStrategies(),
                "get_events"      => HandleGetEvents(doc.RootElement),
                "start_strategy"  => await HandleStartStrategy(doc.RootElement, ct),
                "stop_strategy"   => await HandleStopStrategy(doc.RootElement, ct),
                "reload_strategy" => await HandleReloadStrategy(doc.RootElement, ct),
                "enable_strategy"  => HandleEnableStrategy(doc.RootElement),
                "disable_strategy" => await HandleDisableStrategy(doc.RootElement, ct),
                "close_position"  => await HandleClosePosition(doc.RootElement, ct),
                "close_all"       => await HandleCloseAll(doc.RootElement, ct),
                "emergency_close_all" => await HandleEmergencyCloseAll(ct),
                "save_profile"    => HandleSaveProfile(doc.RootElement),
                "unblock_3sl"     => HandleUnblock3SL(doc.RootElement),
                "toggle_3sl_guard" => HandleToggle3SLGuard(doc.RootElement),
                "toggle_news_guard" => HandleToggleNewsGuard(doc.RootElement),
                "toggle_no_trade"   => HandleToggleNoTrade(doc.RootElement),
                "set_mode"        => HandleSetMode(doc.RootElement),
 // === NEW: auto-discovery + terminal detail ===
                "discover_terminals"      => await HandleDiscoverTerminals(ct),
                "probe_terminal"          => await HandleProbeTerminal(doc.RootElement, ct),
                "add_discovered_terminal" => await HandleAddDiscoveredTerminal(doc.RootElement, ct),
                "get_terminal_detail"     => HandleGetTerminalDetail(doc.RootElement),
                "start_terminal"          => HandleStartTerminal(doc.RootElement),
                "shutdown_daemon"         => await HandleShutdownDaemon(doc.RootElement, ct),
 // === Sizing ===
                "get_sizing"              => await HandleGetSizing(doc.RootElement, ct),
                "save_sizing"             => HandleSaveSizing(doc.RootElement),
                "init_sizing"             => await HandleInitSizing(doc.RootElement, ct),
                "reset_sizing"            => await HandleResetSizing(doc.RootElement, ct),
 // === Leverage ===
                "detect_leverage"         => await HandleDetectLeverage(doc.RootElement, ct),
 // === Terminal management ===
                "toggle_terminal_enabled" => await HandleToggleTerminalEnabled(doc.RootElement, ct),
                "delete_terminal"         => await HandleDeleteTerminal(doc.RootElement, ct),
                "reorder_terminals"       => HandleReorderTerminals(doc.RootElement),
                "open_strategy_folder"    => HandleOpenStrategyFolder(doc.RootElement),
 // === Phase 9.V: Virtual trading ===
                "get_virtual_equity"      => HandleGetVirtualEquity(doc.RootElement),
                "get_virtual_stats"       => HandleGetVirtualStats(doc.RootElement),
                "get_trade_chart"         => await HandleGetTradeChart(doc.RootElement, ct),
                "reset_virtual"           => HandleResetVirtual(doc.RootElement),
                "reset_flags"             => HandleResetFlags(doc.RootElement),
 // === Global Pause ===
                "toggle_pause"            => await HandleTogglePause(doc.RootElement),
                "get_pause_state"         => HandleGetPauseState(),
                "export_virtual_csv"      => HandleExportVirtualCsv(doc.RootElement),
 // === Phase 10+: Backtest/Tester ===
                "bt_get_strategies"       => HandleBtGetStrategies(),
                "bt_get_data_coverage"    => HandleBtGetDataCoverage(doc.RootElement),
                "bt_download_bars"        => await HandleBtDownloadBars(doc.RootElement, ct),
                "bt_cancel_download"      => HandleBtCancelDownload(),
                "bt_get_cost_model"       => HandleBtGetCostModel(),
                "bt_get_download_meta"    => HandleBtGetDownloadMeta(),
                "bt_delete_bars"          => HandleBtDeleteBars(doc.RootElement),
                "bt_run"                  => await HandleBtRun(doc.RootElement, ct),
                "bt_cancel"               => HandleBtCancel(),
                "bt_get_result"           => HandleBtGetResult(),
                "ping"            => new { cmd = "pong", time = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() },
                _ => new { error = $"Unknown command: {cmd}" }
            };

            if (response != null)
                await SendToClientAsync(ws, response, ct);
        }
        catch (Exception ex)
        {
            _log.Error($"[Dashboard] Command '{cmd}' failed: {ex.Message}");
            await SendToClientAsync(ws, new { cmd, error = ex.Message }, ct);
        }
        finally
        {
            doc?.Dispose();
        }
    }

 // ===================================================================
 // Broadcast  â€” Daemon  â€” Dashboard (push events)
 // ===================================================================

 /// <summary>Broadcast a push event to all connected WebSocket clients.</summary>
    public async Task BroadcastAsync(object data)
    {
        if (_clients.IsEmpty) return;

        var json = JsonSerializer.Serialize(data, _jsonOpts);
        var bytes = Encoding.UTF8.GetBytes(json);
        var segment = new ArraySegment<byte>(bytes);

        var deadClients = new List<string>();

        foreach (var (id, ws) in _clients)
        {
            try
            {
                if (ws.State == WebSocketState.Open)
                    await ws.SendAsync(segment, WebSocketMessageType.Text, true, CancellationToken.None);
                else
                    deadClients.Add(id);
            }
            catch
            {
                deadClients.Add(id);
            }
        }

        foreach (var id in deadClients)
            _clients.TryRemove(id, out _);
    }

 /// <summary>Push a log event to all dashboard clients.</summary>
    public async Task PushLogAsync(string type, string? terminalId, string message)
    {
        await BroadcastAsync(new
        {
            @event = "log_entry",
            data = new
            {
                time = DateTime.UtcNow.ToString("HH:mm:ss"),
                terminal = terminalId ?? "SYSTEM",
                type,
                msg = message
            }
        });
    }

 /// <summary>Push a risk alert to all dashboard clients.</summary>
    public async Task PushRiskAlertAsync(string terminalId, string level, string message, double? pct = null)
    {
        await BroadcastAsync(new
        {
            @event = "risk_alert",
            data = new { terminal = terminalId, level, message, pct }
        });
    }

 // ===================================================================
 // Helpers
 // ===================================================================

    private static readonly JsonSerializerOptions _jsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private static async Task SendToClientAsync(WebSocket ws, object data, CancellationToken ct)
    {
        if (ws.State != WebSocketState.Open) return;

        var json = JsonSerializer.Serialize(data, _jsonOpts);
        var bytes = Encoding.UTF8.GetBytes(json);
        await ws.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, ct);
    }

    private static async Task RespondJsonAsync(HttpListenerResponse response, object data)
    {
        response.StatusCode = 200;
        response.ContentType = "application/json; charset=utf-8";
        var json = JsonSerializer.Serialize(data, _jsonOpts);
        var bytes = Encoding.UTF8.GetBytes(json);
        response.ContentLength64 = bytes.Length;
        await response.OutputStream.WriteAsync(bytes);
        response.Close();
    }

    /// <summary>Check if any strategy on this terminal has reached its R-cap limit today.</summary>
    private bool IsRCapReached(string terminalId, TerminalProfile? profile, string brokerDate)
    {
        // Determine effective cap value
        double capValue;
        if (profile?.RCapOn == true)
            capValue = profile.RCapLimit > 0 ? profile.RCapLimit : _strategyMgr.GetEffectiveRCapForTerminal(terminalId);
        else if ((profile?.RCapLimit ?? 0) < 0)
            return false; // Explicitly disabled
        else
            capValue = _strategyMgr.GetEffectiveRCapForTerminal(terminalId);

        if (capValue <= 0) return false;

        var allR = _state.GetDailyRAll(terminalId, brokerDate);
        return allR.Any(r => r.RSum <= -capValue);
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

    private static string FormatDuration(TimeSpan ts)
    {
        if (ts.TotalDays >= 1) return $"{(int)ts.TotalDays}d {ts.Hours}h";
        if (ts.TotalHours >= 1) return $"{(int)ts.TotalHours}h {ts.Minutes}m";
        if (ts.TotalMinutes >= 1) return $"{(int)ts.TotalMinutes}m";
        return $"{(int)ts.TotalSeconds}s";
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
    }
}

// =====================================================================
// ProbeResult  â€” JSON response from probe_terminal.py
// =====================================================================

public class ProbeResult
{
    [JsonPropertyName("path")]
    public string Path { get; set; } = "";
    [JsonPropertyName("status")]
    public string Status { get; set; } = "";
    [JsonPropertyName("company")]
    public string? Company { get; set; }
    [JsonPropertyName("name")]
    public string? Name { get; set; }
    [JsonPropertyName("connected")]
    public bool Connected { get; set; }
    [JsonPropertyName("login")]
    public int? Login { get; set; }
    [JsonPropertyName("server")]
    public string? Server { get; set; }
    [JsonPropertyName("balance")]
    public double? Balance { get; set; }
    [JsonPropertyName("equity")]
    public double? Equity { get; set; }
    [JsonPropertyName("leverage")]
    public int? Leverage { get; set; }
    [JsonPropertyName("currency")]
    public string? Currency { get; set; }
    [JsonPropertyName("trade_mode")]
    public int? TradeMode { get; set; }
    [JsonPropertyName("margin_mode")]
    public int? MarginMode { get; set; }
    [JsonPropertyName("error")]
    public string? Error { get; set; }
}
