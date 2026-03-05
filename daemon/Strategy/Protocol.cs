using System.Text.Json;
using System.Text.Json.Serialization;

namespace Daemon.Strategy;

// =======================================================================
//  Strategy ↔ Daemon Protocol
//  Transport: TCP, newline-delimited JSON (same as Worker protocol)
//  Direction: Runner connects TO daemon's TCP listener
// =======================================================================

/// <summary>
/// Base envelope for all protocol messages.
/// "type" field determines the concrete message kind.
/// </summary>
public class ProtoMessage
{
    [JsonPropertyName("type")] public string Type { get; set; } = "";

    /// <summary>Raw JSON for type-specific payload.</summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; set; }
}

// -----------------------------------------------------------------------
//  Strategy → Daemon
// -----------------------------------------------------------------------

/// <summary>
/// HELLO — first message from strategy after TCP connect.
/// Declares what the strategy needs (symbols, timeframes, history depth).
/// </summary>
public class HelloMessage
{
    [JsonPropertyName("type")]     public string Type => "HELLO";
    [JsonPropertyName("strategy")] public string Strategy { get; set; } = "";
    [JsonPropertyName("version")]  public string Version { get; set; } = "1.0";

    [JsonPropertyName("requirements")]
    public StrategyRequirements Requirements { get; set; } = new();
}

public class StrategyRequirements
{
    /// <summary>List of canonical symbols the strategy trades.</summary>
    [JsonPropertyName("symbols")]
    public List<string> Symbols { get; set; } = new();

    /// <summary>Timeframe per symbol. Key = canonical symbol, Value = "M5","M30","H1","H4","D1".</summary>
    [JsonPropertyName("timeframes")]
    public Dictionary<string, string> Timeframes { get; set; } = new();

    /// <summary>Max history depth across all configs (max(N) + buffer). Used for initial load.</summary>
    [JsonPropertyName("history_bars")]
    public int HistoryBars { get; set; } = 300;

    /// <summary>R-cap: max daily R-loss for this strategy. null = not set by strategy.</summary>
    [JsonPropertyName("r_cap")]
    public double? RCap { get; set; }
}

/// <summary>
/// ACTIONS — strategy's response to a TICK.
/// Contains zero or more trade actions.
/// </summary>
public class ActionsMessage
{
    [JsonPropertyName("type")]    public string Type => "ACTIONS";
    [JsonPropertyName("actions")] public List<StrategyAction> Actions { get; set; } = new();
}

/// <summary>
/// GOODBYE — strategy is shutting down gracefully.
/// May include state to persist for next restart.
/// </summary>
public class GoodbyeMessage
{
    [JsonPropertyName("type")]   public string Type => "GOODBYE";
    [JsonPropertyName("state")]  public JsonElement? State { get; set; }
    [JsonPropertyName("reason")] public string Reason { get; set; } = "normal";
}

/// <summary>
/// HEARTBEAT — keep-alive ping from strategy.
/// Daemon responds with HEARTBEAT_ACK.
/// </summary>
public class HeartbeatMessage
{
    [JsonPropertyName("type")] public string Type => "HEARTBEAT";
    [JsonPropertyName("ts")]   public long Timestamp { get; set; }
}

// -----------------------------------------------------------------------
//  Daemon → Strategy
// -----------------------------------------------------------------------

/// <summary>
/// ACK — response to HELLO. Confirms strategy is registered.
/// May include previously saved state for restore.
/// </summary>
public class AckMessage
{
    [JsonPropertyName("type")]          public string Type => "ACK";
    [JsonPropertyName("status")]        public string Status { get; set; } = "ok";
    [JsonPropertyName("terminal_id")]   public string TerminalId { get; set; } = "";
    [JsonPropertyName("magic")]         public int Magic { get; set; }
    [JsonPropertyName("mode")]          public string Mode { get; set; } = "auto"; // auto/semi/monitor
    [JsonPropertyName("saved_state")]   public JsonElement? SavedState { get; set; }
    [JsonPropertyName("message")]       public string? Message { get; set; }
}

/// <summary>
/// TICK — periodic data push to strategy.
/// Contains bars for all symbols + current positions belonging to this strategy.
/// </summary>
public class TickMessage
{
    [JsonPropertyName("type")]       public string Type => "TICK";
    [JsonPropertyName("tick_id")]    public long TickId { get; set; }
    [JsonPropertyName("server_time")] public long ServerTime { get; set; }

    /// <summary>
    /// Bars per symbol. In full mode (is_delta=false): complete history window.
    /// In delta mode (is_delta=true): only new bars since the previous tick.
    /// Runner maintains the rolling buffer and passes the full window to on_bars().
    /// </summary>
    [JsonPropertyName("bars")]
    public Dictionary<string, List<BarData>> Bars { get; set; } = new();

    /// <summary>Current open positions belonging to this strategy (filtered by magic).</summary>
    [JsonPropertyName("positions")]
    public List<PositionData> Positions { get; set; } = new();

    /// <summary>
    /// Active pending (stop) orders for this strategy (filtered by magic).
    /// Strategy should check this before sending ENTER_PENDING to avoid duplicates.
    /// </summary>
    [JsonPropertyName("pending_orders")]
    public List<PendingOrderData> PendingOrders { get; set; } = new();

    /// <summary>Current account equity (for strategy info, not for risk calc).</summary>
    [JsonPropertyName("equity")]
    public double Equity { get; set; }

    /// <summary>
    /// Backtest optimization: when true, Bars contains only newly-added bars since the
    /// previous tick. Runner accumulates them in a rolling buffer and passes the full
    /// window to on_bars(). Always false (or absent) during live trading.
    /// </summary>
    [JsonPropertyName("is_delta")]
    public bool IsDelta { get; set; }
}

/// <summary>Bar data sent to strategy (lightweight, no JsonPropertyName clutter in core Bar).</summary>
public class BarData
{
    [JsonPropertyName("time")]   public long Time { get; set; }
    [JsonPropertyName("open")]   public double Open { get; set; }
    [JsonPropertyName("high")]   public double High { get; set; }
    [JsonPropertyName("low")]    public double Low { get; set; }
    [JsonPropertyName("close")]  public double Close { get; set; }
    [JsonPropertyName("volume")] public long Volume { get; set; }
}

/// <summary>Position data sent to strategy (simplified view).</summary>
public class PositionData
{
    [JsonPropertyName("ticket")]      public long Ticket { get; set; }
    [JsonPropertyName("symbol")]      public string Symbol { get; set; } = "";
    [JsonPropertyName("direction")]   public string Direction { get; set; } = ""; // "LONG"/"SHORT"
    [JsonPropertyName("volume")]      public double Volume { get; set; }
    [JsonPropertyName("price_open")]  public double PriceOpen { get; set; }
    [JsonPropertyName("sl")]          public double SL { get; set; }
    [JsonPropertyName("tp")]          public double TP { get; set; }
    [JsonPropertyName("profit")]      public double Profit { get; set; }
    [JsonPropertyName("open_time")]   public long OpenTime { get; set; }
    [JsonPropertyName("signal_data")] public string? SignalData { get; set; }
}

/// <summary>Pending (stop) order data sent to strategy in TICK.pending_orders.</summary>
public class PendingOrderData
{
    [JsonPropertyName("ticket")]        public long Ticket { get; set; }
    [JsonPropertyName("symbol")]        public string Symbol { get; set; } = "";
    [JsonPropertyName("direction")]     public string Direction { get; set; } = "";  // "LONG"/"SHORT"
    [JsonPropertyName("order_type")]    public string OrderType { get; set; } = "";  // "BUY_STOP"/"SELL_STOP"
    [JsonPropertyName("volume")]        public double Volume { get; set; }
    [JsonPropertyName("entry_price")]   public double EntryPrice { get; set; }
    [JsonPropertyName("sl")]            public double SL { get; set; }
    [JsonPropertyName("tp")]            public double TP { get; set; }
    /// <summary>Bars remaining until auto-cancel. -1 = GTC.</summary>
    [JsonPropertyName("bars_remaining")] public int BarsRemaining { get; set; }
    [JsonPropertyName("signal_data")]   public string? SignalData { get; set; }
}

/// <summary>
/// STOP — daemon tells strategy to shut down.
/// Strategy should save state and respond with GOODBYE.
/// </summary>
public class StopMessage
{
    [JsonPropertyName("type")]   public string Type => "STOP";
    [JsonPropertyName("reason")] public string Reason { get; set; } = "operator";
}

/// <summary>HEARTBEAT_ACK — response to strategy's HEARTBEAT.</summary>
public class HeartbeatAckMessage
{
    [JsonPropertyName("type")] public string Type => "HEARTBEAT_ACK";
    [JsonPropertyName("ts")]   public long Timestamp { get; set; }
}

/// <summary>
/// ERROR — daemon sends error to strategy (e.g. requirements mismatch).
/// </summary>
public class ErrorMessage
{
    [JsonPropertyName("type")]    public string Type => "ERROR";
    [JsonPropertyName("code")]    public string Code { get; set; } = "";
    [JsonPropertyName("message")] public string Message { get; set; } = "";
}

// -----------------------------------------------------------------------
//  Strategy Actions (inside ACTIONS message)
// -----------------------------------------------------------------------

/// <summary>
/// A single action from strategy. The daemon handles risk check + execution.
/// Strategy only provides signal — never lot sizes, never margin calculations.
/// </summary>
public class StrategyAction
{
    /// <summary>"ENTER", "ENTER_PENDING", "EXIT", "MODIFY_SL"</summary>
    [JsonPropertyName("action")] public string Action { get; set; } = "";

    // --- ENTER / ENTER_PENDING fields ---
    [JsonPropertyName("symbol")]    public string? Symbol { get; set; }
    [JsonPropertyName("direction")] public string? Direction { get; set; }   // "LONG" / "SHORT"
    [JsonPropertyName("sl_price")]  public double? SlPrice { get; set; }
    [JsonPropertyName("tp_price")]  public double? TpPrice { get; set; }

    // --- ENTER_PENDING specific ---
    /// <summary>
    /// Pending order trigger price.
    /// LONG + entry_price > current_price  → BUY_STOP
    /// SHORT + entry_price &lt; current_price → SELL_STOP
    /// </summary>
    [JsonPropertyName("entry_price")]  public double? EntryPrice { get; set; }

    /// <summary>
    /// How many bars the pending order lives before auto-cancel.
    /// Counted from the bar the signal was generated on.
    /// null or 0 = no expiry (GTC).
    /// </summary>
    [JsonPropertyName("expiry_bars")] public int? ExpiryBars { get; set; }

    // --- EXIT / MODIFY_SL fields ---
    [JsonPropertyName("ticket")]    public long? Ticket { get; set; }

    // --- MODIFY_SL fields ---
    [JsonPropertyName("new_sl")]    public double? NewSl { get; set; }

    // --- Optional metadata ---
    [JsonPropertyName("comment")]   public string? Comment { get; set; }
    [JsonPropertyName("signal_data")] public string? SignalData { get; set; }
}

// -----------------------------------------------------------------------
//  Protocol Serializer — encode/decode newline-delimited JSON
// -----------------------------------------------------------------------

public static class ProtocolSerializer
{
    private static readonly JsonSerializerOptions _opts = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    /// <summary>Serialize a message to a newline-terminated JSON string.</summary>
    public static string Encode<T>(T message) =>
        JsonSerializer.Serialize(message, _opts) + "\n";

    /// <summary>Deserialize a JSON line into a typed message.</summary>
    public static T? Decode<T>(string line) =>
        JsonSerializer.Deserialize<T>(line.Trim(), _opts);

    /// <summary>Read the "type" field from a raw JSON line without full deserialization.</summary>
    public static string? ReadType(string line)
    {
        try
        {
            using var doc = JsonDocument.Parse(line);
            if (doc.RootElement.TryGetProperty("type", out var typeProp))
                return typeProp.GetString();
        }
        catch { }
        return null;
    }

    /// <summary>Deserialize raw JSON line into specific message type based on "type" field.</summary>
    public static object? DecodeAny(string line)
    {
        var type = ReadType(line);
        return type switch
        {
            "HELLO"         => Decode<HelloMessage>(line),
            "ACTIONS"       => Decode<ActionsMessage>(line),
            "GOODBYE"       => Decode<GoodbyeMessage>(line),
            "HEARTBEAT"     => Decode<HeartbeatMessage>(line),
            "ACK"           => Decode<AckMessage>(line),
            "TICK"          => Decode<TickMessage>(line),
            "STOP"          => Decode<StopMessage>(line),
            "HEARTBEAT_ACK" => Decode<HeartbeatAckMessage>(line),
            "ERROR"         => Decode<ErrorMessage>(line),
            _ => null
        };
    }

    /// <summary>Convert core Bar model to protocol BarData.</summary>
    public static BarData ToBarData(Models.Bar bar) => new()
    {
        Time = bar.Time, Open = bar.Open, High = bar.High,
        Low = bar.Low, Close = bar.Close, Volume = bar.Volume
    };

    /// <summary>Convert core Position to protocol PositionData.</summary>
    public static PositionData ToPositionData(Models.Position pos) => new()
    {
        Ticket = pos.Ticket, Symbol = pos.Symbol,
        Direction = pos.IsBuy ? "LONG" : "SHORT",
        Volume = pos.Volume, PriceOpen = pos.PriceOpen,
        SL = pos.SL, TP = pos.TP, Profit = pos.Profit,
        OpenTime = pos.Time
    };
}
