using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Daemon.Config;
using Daemon.Connector;

namespace Daemon.Strategy;

/// <summary>
/// Manages a single Python strategy process.
///
/// Lifecycle:
///   1. Open TCP listener on assigned port
///   2. Launch Python runner.py → runner connects to our listener
///   3. Receive HELLO → validate requirements → send ACK
///   4. Loop: send TICK → receive ACTIONS
///   5. On stop: send STOP → receive GOODBYE → kill process
///
/// The daemon is the TCP SERVER, the runner is the TCP CLIENT.
/// This mirrors the Worker protocol pattern (daemon controls lifecycle).
///
/// Protocol invariant (v2):
///   The DAEMON always initiates communication.  Runner only replies.
///   TICK → ACTIONS, HEARTBEAT → HEARTBEAT_ACK, STOP → GOODBYE.
///   Runner never sends unsolicited messages.
///   This eliminates TCP buffer desync from accumulated heartbeats.
/// </summary>
public class StrategyProcess : IDisposable
{
    private readonly StrategyAssignment _assignment;
    private readonly DaemonConfig _config;
    private readonly ILogger _log;
    private readonly string _tag;

    private Process? _process;
    private TcpListener? _listener;
    private TcpClient? _client;
    private StreamReader? _reader;
    private StreamWriter? _writer;
    private readonly SemaphoreSlim _sendLock = new(1, 1);

    private long _tickCounter;
    private DateTime _lastHeartbeat = DateTime.UtcNow;

    public string StrategyName => _assignment.Strategy;
    public string TerminalId => _assignment.Terminal;
    public int Magic => _assignment.Magic;
    public int Port { get; private set; }

    /// <summary>R-cap limit. Priority: daemon config override > strategy HELLO requirements.</summary>
    public double? RCap => _assignment.RCap ?? Requirements?.RCap;

    public bool IsRunning => _process != null && !_process.HasExited;
    public bool IsConnected => _client?.Connected == true;

    /// <summary>False until first tick completes. ENTER actions suppressed during warmup.</summary>
    public bool WarmupDone { get; set; }

    /// <summary>Strategy requirements, populated after HELLO handshake.</summary>
    public StrategyRequirements? Requirements { get; private set; }

    /// <summary>Strategy status for dashboard.</summary>
    public StrategyStatus Status { get; private set; } = StrategyStatus.Stopped;

    public StrategyProcess(StrategyAssignment assignment, DaemonConfig config, int port, ILogger logger)
    {
        _assignment = assignment;
        _config = config;
        Port = port;
        _log = logger;
        _tag = $"[Strategy:{assignment.Strategy}@{assignment.Terminal}]";
    }

    /// <summary>
    /// Start the strategy: open TCP listener, launch Python, perform HELLO/ACK handshake.
    /// </summary>
    public async Task<bool> StartAsync(string? savedState, CancellationToken ct = default)
    {
        try
        {
            Status = StrategyStatus.Starting;
            _log.Info($"{_tag} Starting on port {Port}...");

            // 1. Open TCP listener
            _listener = new TcpListener(IPAddress.Loopback, Port);
            _listener.Start(1);
            _log.Info($"{_tag} TCP listener on 127.0.0.1:{Port}");

            // 2. Resolve strategy config path
            var strategyDir = Path.GetFullPath(_assignment.Strategy,
                Path.GetFullPath(_config.StrategyDir, AppContext.BaseDirectory));
            var configPath = _assignment.ConfigOverride
                ?? Path.Combine(strategyDir, "config.json");

            // 3. Launch Python runner
            var runnerPath = Path.GetFullPath(_config.RunnerScript, AppContext.BaseDirectory);
            var psi = new ProcessStartInfo
            {
                FileName = _config.PythonPath,
                Arguments = $"\"{runnerPath}\" --port {Port} --strategy \"{_assignment.Strategy}\" "
                          + $"--strategy-dir \"{Path.GetFullPath(_config.StrategyDir, AppContext.BaseDirectory)}\" "
                          + (File.Exists(configPath) ? $"--config \"{configPath}\"" : ""),
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WorkingDirectory = AppContext.BaseDirectory
            };

            _process = new Process { StartInfo = psi };
            _process.OutputDataReceived += (_, e) =>
            {
                if (e.Data != null) _log.Info($"{_tag} [stdout] {e.Data}");
            };
            _process.ErrorDataReceived += (_, e) =>
            {
                if (e.Data != null) _log.Warn($"{_tag} [stderr] {e.Data}");
            };

            _process.Start();
            _process.BeginOutputReadLine();
            _process.BeginErrorReadLine();
            _log.Info($"{_tag} Python process PID={_process.Id}");

            // 4. Wait for TCP connection from runner (timeout 15s)
            var acceptTask = _listener.AcceptTcpClientAsync(ct).AsTask();
            var timeoutTask = Task.Delay(TimeSpan.FromSeconds(15), ct);
            var completed = await Task.WhenAny(acceptTask, timeoutTask);

            if (completed == timeoutTask)
            {
                _log.Error($"{_tag} Runner did not connect within 15 seconds");
                await StopAsync(ct);
                return false;
            }

            _client = await acceptTask;
            var stream = _client.GetStream();
            _reader = new StreamReader(stream, Encoding.UTF8);
            _writer = new StreamWriter(stream, Encoding.UTF8) { AutoFlush = true };
            _log.Info($"{_tag} Runner connected");

            // 5. HELLO/ACK handshake
            var helloLine = await ReadLineWithTimeoutAsync(10, ct);
            if (helloLine == null)
            {
                _log.Error($"{_tag} No HELLO received within 10 seconds");
                await StopAsync(ct);
                return false;
            }

            var hello = ProtocolSerializer.Decode<HelloMessage>(helloLine);
            if (hello == null || hello.Type != "HELLO")
            {
                _log.Error($"{_tag} Expected HELLO, got: {helloLine.Truncate(200)}");
                await StopAsync(ct);
                return false;
            }

            Requirements = hello.Requirements;
            _log.Info($"{_tag} HELLO received: {Requirements.Symbols.Count} symbols, "
                    + $"history_bars={Requirements.HistoryBars}");

            // 6. Send ACK with saved state (if any)
            var ack = new AckMessage
            {
                TerminalId = _assignment.Terminal,
                Magic = _assignment.Magic,
                Mode = "auto", // Will be set from terminal profile
            };

            if (!string.IsNullOrEmpty(savedState))
            {
                try
                {
                    ack.SavedState = JsonDocument.Parse(savedState).RootElement;
                    _log.Info($"{_tag} Sending saved state in ACK");
                }
                catch { _log.Warn($"{_tag} Failed to parse saved state, sending ACK without it"); }
            }

            await SendAsync(ProtocolSerializer.Encode(ack), ct);
            _log.Info($"{_tag} ACK sent → strategy is RUNNING");

            Status = StrategyStatus.Running;
            _lastHeartbeat = DateTime.UtcNow;
            return true;
        }
        catch (OperationCanceledException)
        {
            Status = StrategyStatus.Stopped;
            return false;
        }
        catch (Exception ex)
        {
            _log.Error($"{_tag} Start failed: {ex.Message}");
            Status = StrategyStatus.Error;
            return false;
        }
    }

    /// <summary>
    /// Send TICK to strategy, receive ACTIONS response.
    /// Drains any stale/pending messages from socket before sending.
    /// Returns null if communication failed.
    /// </summary>
    public async Task<List<StrategyAction>?> SendTickAsync(TickMessage tick, CancellationToken ct = default)
    {
        if (Status != StrategyStatus.Running || !IsConnected)
            return null;

        try
        {
            // Drain any pending messages (legacy heartbeats from old runners, etc.)
            int drained = await DrainPendingAsync(ct);
            if (drained > 0)
                _log.Info($"{_tag} Drained {drained} pending message(s) before TICK");

            tick.TickId = Interlocked.Increment(ref _tickCounter);
            var json = ProtocolSerializer.Encode(tick);

            await SendAsync(json, ct);

            // Wait for ACTIONS response (timeout 30s — strategy may need time for heavy computation)
            // Loop to skip any unexpected messages (defensive)
            const int maxAttempts = 5;
            for (int attempt = 0; attempt < maxAttempts; attempt++)
            {
                var responseLine = await ReadLineWithTimeoutAsync(30, ct);
                if (responseLine == null)
                {
                    _log.Warn($"{_tag} No ACTIONS response within 30 seconds");
                    return null;
                }

                var msgType = ProtocolSerializer.ReadType(responseLine);

                if (msgType == "ACTIONS")
                {
                    var actions = ProtocolSerializer.Decode<ActionsMessage>(responseLine);
                    _lastHeartbeat = DateTime.UtcNow;
                    return actions?.Actions ?? new List<StrategyAction>();
                }

                if (msgType == "HEARTBEAT")
                {
                    // Legacy runner sent an unsolicited heartbeat — ACK and retry
                    _lastHeartbeat = DateTime.UtcNow;
                    await SendAsync(ProtocolSerializer.Encode(
                        new HeartbeatAckMessage { Timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds() }), ct);
                    _log.Info($"{_tag} Drained legacy HEARTBEAT (attempt {attempt + 1})");
                    continue;
                }

                if (msgType == "HEARTBEAT_ACK")
                {
                    // Runner sent ACK to our heartbeat — skip and retry
                    _lastHeartbeat = DateTime.UtcNow;
                    _log.Info($"{_tag} Skipped stale HEARTBEAT_ACK (attempt {attempt + 1})");
                    continue;
                }

                _log.Warn($"{_tag} Unexpected response type: {msgType} (attempt {attempt + 1})");
            }

            _log.Error($"{_tag} Failed to receive ACTIONS after {maxAttempts} attempts (protocol desync)");
            return null;
        }
        catch (Exception ex)
        {
            _log.Warn($"{_tag} SendTick error: {ex.Message}");
            Status = StrategyStatus.Error;
            return null;
        }
    }

    /// <summary>
    /// Send daemon-initiated HEARTBEAT to runner, expect HEARTBEAT_ACK.
    /// Used by Scheduler between candles to keep the connection alive
    /// and detect dead runners early.
    /// Returns true if runner responded.
    /// </summary>
    public async Task<bool> SendHeartbeatAsync(CancellationToken ct = default)
    {
        if (Status != StrategyStatus.Running || !IsConnected)
            return false;

        try
        {
            await SendAsync(ProtocolSerializer.Encode(
                new HeartbeatMessage { Timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds() }), ct);

            var responseLine = await ReadLineWithTimeoutAsync(10, ct);
            if (responseLine == null)
            {
                _log.Warn($"{_tag} No HEARTBEAT_ACK within 10 seconds");
                return false;
            }

            var msgType = ProtocolSerializer.ReadType(responseLine);
            if (msgType == "HEARTBEAT_ACK")
            {
                _lastHeartbeat = DateTime.UtcNow;
                return true;
            }

            // Legacy runner might have sent its own HEARTBEAT first
            if (msgType == "HEARTBEAT")
            {
                _lastHeartbeat = DateTime.UtcNow;
                await SendAsync(ProtocolSerializer.Encode(
                    new HeartbeatAckMessage { Timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds() }), ct);
                // Try reading our ACK again
                responseLine = await ReadLineWithTimeoutAsync(5, ct);
                if (responseLine != null && ProtocolSerializer.ReadType(responseLine) == "HEARTBEAT_ACK")
                {
                    _lastHeartbeat = DateTime.UtcNow;
                    return true;
                }
            }

            _log.Warn($"{_tag} Unexpected heartbeat response: {msgType}");
            return false;
        }
        catch (Exception ex)
        {
            _log.Warn($"{_tag} Heartbeat error: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Drain all pending messages from the socket without blocking.
    /// Uses NetworkStream.DataAvailable to check for buffered data —
    /// NEVER cancels a ReadLineAsync mid-flight (which corrupts StreamReader state).
    /// </summary>
    private async Task<int> DrainPendingAsync(CancellationToken ct)
    {
        if (_client == null) return 0;
        var stream = _client.GetStream();
        int count = 0;

        // Only read if there's data already sitting in the TCP buffer
        while (stream.DataAvailable)
        {
            var line = await ReadLineWithTimeoutAsync(5, ct);
            if (line == null)
                break;

            count++;
            var msgType = ProtocolSerializer.ReadType(line);

            if (msgType == "HEARTBEAT")
            {
                await SendAsync(ProtocolSerializer.Encode(
                    new HeartbeatAckMessage { Timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds() }), ct);
            }

            if (count >= 50)
            {
                _log.Warn($"{_tag} Drained 50+ messages — possible protocol issue");
                break;
            }
        }
        return count;
    }

    /// <summary>
    /// Process incoming heartbeat from strategy (called from background reader if needed).
    /// </summary>
    public async Task HandleHeartbeatAsync(CancellationToken ct = default)
    {
        _lastHeartbeat = DateTime.UtcNow;
        await SendAsync(ProtocolSerializer.Encode(
            new HeartbeatAckMessage { Timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds() }), ct);
    }

    /// <summary>Check if strategy heartbeat is stale (no communication for too long).</summary>
    public bool IsHeartbeatStale(TimeSpan threshold) =>
        DateTime.UtcNow - _lastHeartbeat > threshold;

    /// <summary>
    /// Stop the strategy gracefully: send STOP, wait for GOODBYE, kill process.
    /// </summary>
    public async Task StopAsync(CancellationToken ct = default)
    {
        if (Status == StrategyStatus.Stopped)
            return;

        _log.Info($"{_tag} Stopping...");
        Status = StrategyStatus.Stopping;
        string? savedState = null;

        try
        {
            // Send STOP if connected
            if (IsConnected)
            {
                // Drain pending first to clear the pipe
                await DrainPendingAsync(ct);

                await SendAsync(ProtocolSerializer.Encode(
                    new StopMessage { Reason = "operator" }), ct);

                // Wait for GOODBYE (timeout 5s)
                var line = await ReadLineWithTimeoutAsync(5, ct);
                if (line != null)
                {
                    var goodbye = ProtocolSerializer.Decode<GoodbyeMessage>(line);
                    if (goodbye?.State != null)
                    {
                        savedState = goodbye.State.Value.GetRawText();
                        _log.Info($"{_tag} Received GOODBYE with state ({savedState.Length} bytes)");
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _log.Warn($"{_tag} Error during graceful stop: {ex.Message}");
        }

        // Cleanup TCP
        _reader?.Dispose();
        _writer?.Dispose();
        _client?.Dispose();
        _listener?.Stop();
        _reader = null;
        _writer = null;
        _client = null;
        _listener = null;

        // Kill process if still running
        if (_process != null && !_process.HasExited)
        {
            try
            {
                _process.Kill(entireProcessTree: true);
                await _process.WaitForExitAsync(ct).WaitAsync(TimeSpan.FromSeconds(5), ct);
            }
            catch { }
        }
        _process?.Dispose();
        _process = null;

        Status = StrategyStatus.Stopped;
        _log.Info($"{_tag} Stopped");

        // Fire event with saved state (caller can persist it)
        OnStopped?.Invoke(this, savedState);
    }

    /// <summary>Event fired when strategy stops. string? = saved state JSON.</summary>
    public event Action<StrategyProcess, string?>? OnStopped;

    // -----------------------------------------------------------------------
    //  Helpers
    // -----------------------------------------------------------------------

    private async Task SendAsync(string data, CancellationToken ct)
    {
        await _sendLock.WaitAsync(ct);
        try
        {
            if (_writer != null)
                await _writer.WriteAsync(data.AsMemory(), ct);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    private async Task<string?> ReadLineWithTimeoutAsync(int timeoutSeconds, CancellationToken ct)
    {
        if (_reader == null) return null;
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
            return await _reader.ReadLineAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (IOException)
        {
            return null;
        }
    }

    public void Dispose()
    {
        _reader?.Dispose();
        _writer?.Dispose();
        _client?.Dispose();
        _listener?.Stop();
        if (_process != null && !_process.HasExited)
        {
            try { _process.Kill(entireProcessTree: true); } catch { }
        }
        _process?.Dispose();
        _sendLock.Dispose();
    }
}

public enum StrategyStatus
{
    Stopped,
    Starting,
    Running,
    Stopping,
    Error
}

/// <summary>Extension method for string truncation in logs.</summary>
internal static class StringExtensions
{
    public static string Truncate(this string s, int maxLen) =>
        s.Length <= maxLen ? s : s[..maxLen] + "...";
}
