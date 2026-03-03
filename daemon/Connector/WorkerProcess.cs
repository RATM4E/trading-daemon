using System.Diagnostics;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Daemon.Config;
using Daemon.Models;

namespace Daemon.Connector;

/// <summary>
/// Manages a single MT5 worker process: spawn, TCP communication, shutdown.
/// One WorkerProcess per MT5 terminal.
/// </summary>
public class WorkerProcess : IDisposable
{
    private readonly TerminalConfig _config;
    private readonly string _pythonPath;
    private readonly string _workerScript;
    private readonly ILogger _log;

    private Process? _process;
    private TcpClient? _tcp;
    private StreamReader? _reader;
    private StreamWriter? _writer;
    private int _nextId;
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private bool _disposed;

    public string TerminalId => _config.Id;
    public int Port => _config.Port;
    public bool IsRunning => _process is { HasExited: false };
    public bool IsConnected => _tcp?.Connected == true;

    public WorkerProcess(TerminalConfig config, string pythonPath, string workerScript, ILogger logger)
    {
        _config = config;
        _pythonPath = pythonPath;
        _workerScript = workerScript;
        _log = logger;
    }

    /// <summary>Start the Python worker process and connect via TCP.</summary>
    public async Task StartAsync(CancellationToken ct = default)
    {
        _log.Info($"[{TerminalId}] Starting worker on port {Port}...");

        // Build arguments
        var args = new StringBuilder();
        args.Append($"--port {Port}");
        args.Append($" --terminal-path \"{_config.TerminalPath}\"");
        if (_config.Login.HasValue)
            args.Append($" --login {_config.Login}");
        if (!string.IsNullOrEmpty(_config.Password))
            args.Append($" --password \"{_config.Password}\"");
        if (!string.IsNullOrEmpty(_config.Server))
            args.Append($" --server \"{_config.Server}\"");

        // Start process
        var psi = new ProcessStartInfo
        {
            FileName = _pythonPath,
            Arguments = $"\"{_workerScript}\" {args}",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };

        _process = Process.Start(psi);
        if (_process == null)
            throw new Exception($"[{TerminalId}] Failed to start worker process");

        // Forward worker stdout/stderr to our log
        _process.OutputDataReceived += (_, e) =>
        {
            if (e.Data != null) _log.Info($"[{TerminalId}/py] {e.Data}");
        };
        _process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data != null) _log.Warn($"[{TerminalId}/py] {e.Data}");
        };
        _process.BeginOutputReadLine();
        _process.BeginErrorReadLine();

        _log.Info($"[{TerminalId}] Worker PID={_process.Id}, waiting for TCP ready...");

        // Wait for TCP to become available (worker needs time to init MT5)
        await WaitForTcpAsync(ct);

        _log.Info($"[{TerminalId}] TCP connected to worker");
    }

    private async Task WaitForTcpAsync(CancellationToken ct)
    {
        const int maxRetries = 30;     // 30 * 1s = 30 sec max wait
        const int retryDelayMs = 1000;

        for (int i = 0; i < maxRetries; i++)
        {
            ct.ThrowIfCancellationRequested();

            if (_process?.HasExited == true)
                throw new Exception($"[{TerminalId}] Worker process exited during startup (exit code {_process.ExitCode})");

            try
            {
                _tcp = new TcpClient();
                await _tcp.ConnectAsync("127.0.0.1", Port, ct);

                var stream = _tcp.GetStream();
                stream.ReadTimeout = 10_000;
                stream.WriteTimeout = 5_000;

                _reader = new StreamReader(stream, Encoding.UTF8);
                _writer = new StreamWriter(stream, Encoding.UTF8) { AutoFlush = true };

                return; // Connected!
            }
            catch (SocketException)
            {
                _tcp?.Dispose();
                _tcp = null;
                await Task.Delay(retryDelayMs, ct);
            }
        }

        throw new TimeoutException($"[{TerminalId}] Could not connect to worker on port {Port} after {maxRetries}s");
    }

    /// <summary>Send a command and receive the response.</summary>
    public async Task<WorkerResponse> SendCommandAsync(string cmd, Dictionary<string, object>? parameters = null,
                                                        CancellationToken ct = default)
    {
        if (!IsConnected || _writer == null || _reader == null)
            throw new InvalidOperationException($"[{TerminalId}] Not connected to worker");

        await _sendLock.WaitAsync(ct);
        try
        {
            var id = Interlocked.Increment(ref _nextId);

            // Build request
            var request = new Dictionary<string, object> { ["cmd"] = cmd, ["id"] = id };
            if (parameters != null)
            {
                foreach (var kv in parameters)
                    request[kv.Key] = kv.Value;
            }

            var json = JsonSerializer.Serialize(request);
            await _writer.WriteLineAsync(json);

            // Read response line
            var responseLine = await _reader.ReadLineAsync(ct);
            if (responseLine == null)
                throw new IOException($"[{TerminalId}] Worker closed connection");

            var response = JsonSerializer.Deserialize<WorkerResponse>(responseLine)
                ?? throw new Exception($"[{TerminalId}] Failed to parse response");

            return response;
        }
        finally
        {
            _sendLock.Release();
        }
    }

    /// <summary>Check if worker is alive.</summary>
    public async Task<bool> HeartbeatAsync(CancellationToken ct = default)
    {
        try
        {
            var resp = await SendCommandAsync("HEARTBEAT", ct: ct);
            return resp.IsOk;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Get account info.</summary>
    public async Task<AccountInfo?> GetAccountInfoAsync(CancellationToken ct = default)
    {
        var resp = await SendCommandAsync("ACCOUNT_INFO", ct: ct);
        if (!resp.IsOk || resp.Data == null) return null;
        return resp.Data.Value.Deserialize<AccountInfo>();
    }

    /// <summary>Get open positions.</summary>
    public async Task<List<Position>> GetPositionsAsync(CancellationToken ct = default)
    {
        var resp = await SendCommandAsync("GET_POSITIONS", ct: ct);
        if (!resp.IsOk || resp.Data == null) return new List<Position>();
        return resp.Data.Value.Deserialize<List<Position>>() ?? new List<Position>();
    }

    /// <summary>Get OHLCV bars.</summary>
    public async Task<List<Bar>> GetRatesAsync(string symbol, string timeframe, int count = 300,
                                                CancellationToken ct = default)
    {
        var parameters = new Dictionary<string, object>
        {
            ["symbol"] = symbol,
            ["timeframe"] = timeframe,
            ["count"] = count,
        };
        var resp = await SendCommandAsync("GET_RATES", parameters, ct);
        if (!resp.IsOk || resp.Data == null) return new List<Bar>();
        return resp.Data.Value.Deserialize<List<Bar>>() ?? new List<Bar>();
    }

    /// <summary>Get instrument card.</summary>
    public async Task<InstrumentCard?> GetSymbolInfoAsync(string symbol, CancellationToken ct = default)
    {
        var parameters = new Dictionary<string, object> { ["symbol"] = symbol };
        var resp = await SendCommandAsync("SYMBOL_INFO", parameters, ct);
        if (!resp.IsOk || resp.Data == null) return null;
        return resp.Data.Value.Deserialize<InstrumentCard>();
    }

    /// <summary>Calculate effective leverage per asset class.</summary>
    public async Task<Dictionary<string, int>> CalcLeverageAsync(Dictionary<string, string> classSymbols,
                                                                   CancellationToken ct = default)
    {
        var parameters = new Dictionary<string, object> { ["symbols"] = classSymbols };
        var resp = await SendCommandAsync("CALC_LEVERAGE", parameters, ct);
        if (!resp.IsOk || resp.Data == null) return new Dictionary<string, int>();

        var result = new Dictionary<string, int>();

        // New format: { "leverage": {"FX": 100, ...}, "details": {...}, "account_leverage": 100 }
        if (resp.Data.Value.TryGetProperty("leverage", out var levProp))
        {
            foreach (var prop in levProp.EnumerateObject())
            {
                if (prop.Value.TryGetInt32(out var lev))
                    result[prop.Name] = lev;
            }
        }
        else
        {
            // Fallback: old flat format {"FX": 100, ...}
            foreach (var prop in resp.Data.Value.EnumerateObject())
            {
                if (prop.Value.TryGetInt32(out var lev))
                    result[prop.Name] = lev;
            }
        }

        return result;
    }

    /// <summary>Calculate profit/loss for a hypothetical trade via MT5 OrderCalcProfit.
    /// Returns profit in account currency, or null if calculation failed.</summary>
    public async Task<double?> CalcProfitAsync(string symbol, string action, double volume,
                                                double priceOpen, double priceClose,
                                                CancellationToken ct = default)
    {
        var parameters = new Dictionary<string, object>
        {
            ["symbol"] = symbol,
            ["action"] = action,
            ["volume"] = volume,
            ["price_open"] = priceOpen,
            ["price_close"] = priceClose
        };
        var resp = await SendCommandAsync("CALC_PROFIT", parameters, ct);
        if (!resp.IsOk || resp.Data == null) return null;

        if (resp.Data.Value.TryGetProperty("profit", out var profitProp))
            return profitProp.GetDouble();

        return null;
    }

    /// <summary>Calculate margin for a hypothetical trade via MT5 OrderCalcMargin.
    /// Returns margin in account currency, or null on failure.</summary>
    public async Task<double?> CalcMarginAsync(string symbol, string action, double volume,
                                                double price, CancellationToken ct = default)
    {
        var parameters = new Dictionary<string, object>
        {
            ["symbol"] = symbol,
            ["action"] = action,
            ["volume"] = volume,
            ["price"] = price
        };
        var resp = await SendCommandAsync("CALC_MARGIN", parameters, ct);
        if (!resp.IsOk || resp.Data == null) return null;

        if (resp.Data.Value.TryGetProperty("margin", out var marginProp))
            return marginProp.GetDouble();

        return null;
    }

    /// <summary>Calculate per-position margin filtered by magic numbers.
    /// Returns own/total margin split for multi-strategy account sharing.</summary>
    public async Task<PositionsMarginResult?> CalcPositionsMarginAsync(
        List<int>? magics = null, CancellationToken ct = default)
    {
        var parameters = new Dictionary<string, object>();
        if (magics != null && magics.Count > 0)
            parameters["magics"] = magics;

        var resp = await SendCommandAsync("CALC_POSITIONS_MARGIN", parameters, ct);
        if (!resp.IsOk || resp.Data == null) return null;

        return JsonSerializer.Deserialize<PositionsMarginResult>(
            resp.Data.Value.GetRawText(), _jsonOpt);
    }

    private static readonly JsonSerializerOptions _jsonOpt = new()
    {
        PropertyNameCaseInsensitive = true
    };

    /// <summary>Check which canonical symbols are available on the terminal.</summary>
    public async Task<(Dictionary<string, string> Resolved, List<string> Missing)>
        CheckSymbolsAsync(List<string> symbols, CancellationToken ct = default)
    {
        var parameters = new Dictionary<string, object> { ["symbols"] = symbols };
        var resp = await SendCommandAsync("CHECK_SYMBOLS", parameters, ct);

        var resolved = new Dictionary<string, string>();
        var missing = new List<string>();

        if (!resp.IsOk || resp.Data == null)
            return (resolved, symbols); // treat all as missing on error

        if (resp.Data.Value.TryGetProperty("resolved", out var resProp))
        {
            foreach (var prop in resProp.EnumerateObject())
                resolved[prop.Name] = prop.Value.GetString() ?? prop.Name;
        }

        if (resp.Data.Value.TryGetProperty("missing", out var misProp))
        {
            foreach (var item in misProp.EnumerateArray())
            {
                var s = item.GetString();
                if (s != null) missing.Add(s);
            }
        }

        return (resolved, missing);
    }

    /// <summary>Send a trade order.</summary>
    public async Task<WorkerResponse> SendOrderAsync(Dictionary<string, object> request,
                                                      CancellationToken ct = default)
    {
        var parameters = new Dictionary<string, object> { ["request"] = request };
        return await SendCommandAsync("ORDER_SEND", parameters, ct);
    }

    /// <summary>Send SHUTDOWN and wait for process to exit.</summary>
    public async Task StopAsync(CancellationToken ct = default)
    {
        _log.Info($"[{TerminalId}] Stopping worker...");

        // Try graceful shutdown
        try
        {
            if (IsConnected)
            {
                await SendCommandAsync("SHUTDOWN", ct: ct);
            }
        }
        catch (Exception ex)
        {
            _log.Warn($"[{TerminalId}] SHUTDOWN command failed: {ex.Message}");
        }

        // Wait for process exit
        if (_process != null && !_process.HasExited)
        {
            try
            {
                using var timeoutCts = new CancellationTokenSource(5000);
                await _process.WaitForExitAsync(timeoutCts.Token);
                _log.Info($"[{TerminalId}] Worker exited cleanly");
            }
            catch (OperationCanceledException)
            {
                _log.Warn($"[{TerminalId}] Worker didn't exit in 5s, killing...");
                try { _process.Kill(entireProcessTree: true); } catch { }
            }
        }

        CleanupConnection();
    }

    private void CleanupConnection()
    {
        _reader?.Dispose();
        _writer?.Dispose();
        _tcp?.Dispose();
        _reader = null;
        _writer = null;
        _tcp = null;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        CleanupConnection();

        if (_process is { HasExited: false })
        {
            try { _process.Kill(entireProcessTree: true); } catch { }
        }
        _process?.Dispose();
        _sendLock.Dispose();
    }
}

/// <summary>Simple logger interface (will be replaced with proper logging later).</summary>
public interface ILogger
{
    void Info(string message);
    void Warn(string message);
    void Error(string message);
}

/// <summary>Console logger for development.</summary>
public class ConsoleLogger : ILogger
{
    public void Info(string message) => Console.WriteLine($"{DateTime.Now:HH:mm:ss} [INF] {message}");
    public void Warn(string message) => Console.WriteLine($"{DateTime.Now:HH:mm:ss} [WRN] {message}");
    public void Error(string message) => Console.WriteLine($"{DateTime.Now:HH:mm:ss} [ERR] {message}");
}
