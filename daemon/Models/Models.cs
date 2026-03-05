using System.Text.Json.Serialization;

namespace Daemon.Models;

/// <summary>Account info from ACCOUNT_INFO command.</summary>
public class AccountInfo
{
    [JsonPropertyName("login")]     public int Login { get; set; }
    [JsonPropertyName("balance")]   public double Balance { get; set; }
    [JsonPropertyName("equity")]    public double Equity { get; set; }
    [JsonPropertyName("margin")]    public double Margin { get; set; }
    [JsonPropertyName("margin_free")] public double MarginFree { get; set; }
    [JsonPropertyName("margin_level")] public double MarginLevel { get; set; }
    [JsonPropertyName("profit")]    public double Profit { get; set; }
    [JsonPropertyName("currency")]  public string Currency { get; set; } = "USD";
    [JsonPropertyName("leverage")]  public int Leverage { get; set; }
    [JsonPropertyName("trade_mode")] public int TradeMode { get; set; }      // 0=demo, 2=real
    [JsonPropertyName("margin_mode")] public int MarginMode { get; set; }    // 0=netting, 2=hedge
    [JsonPropertyName("server_time")] public long ServerTime { get; set; }

    public bool IsHedge => MarginMode == 2;
    public string AccountType => MarginMode == 2 ? "hedge" : "netting";
}

/// <summary>Open position from GET_POSITIONS command.</summary>
public class Position
{
    [JsonPropertyName("ticket")]        public long Ticket { get; set; }
    [JsonPropertyName("symbol")]        public string Symbol { get; set; } = "";
    [JsonPropertyName("type")]          public string Type { get; set; } = "";      // "BUY"/"SELL"
    [JsonPropertyName("volume")]        public double Volume { get; set; }
    [JsonPropertyName("price_open")]    public double PriceOpen { get; set; }
    [JsonPropertyName("price_current")] public double PriceCurrent { get; set; }
    [JsonPropertyName("sl")]            public double SL { get; set; }
    [JsonPropertyName("tp")]            public double TP { get; set; }
    [JsonPropertyName("profit")]        public double Profit { get; set; }
    [JsonPropertyName("swap")]          public double Swap { get; set; }
    [JsonPropertyName("commission")]    public double Commission { get; set; }
    [JsonPropertyName("time")]          public long Time { get; set; }
    [JsonPropertyName("time_update")]   public long TimeUpdate { get; set; }
    [JsonPropertyName("magic")]         public int Magic { get; set; }
    [JsonPropertyName("comment")]       public string Comment { get; set; } = "";
    [JsonPropertyName("identifier")]    public long Identifier { get; set; }

    public bool IsBuy => Type == "BUY";
}

/// <summary>Instrument card from SYMBOL_INFO command.</summary>
public class InstrumentCard
{
    [JsonPropertyName("symbol")]                 public string Symbol { get; set; } = "";
    [JsonPropertyName("digits")]                 public int Digits { get; set; }
    [JsonPropertyName("point")]                  public double Point { get; set; }
    [JsonPropertyName("trade_tick_size")]         public double TradeTickSize { get; set; }
    [JsonPropertyName("trade_tick_value")]        public double TradeTickValue { get; set; }
    [JsonPropertyName("trade_tick_value_profit")] public double TradeTickValueProfit { get; set; }
    [JsonPropertyName("trade_tick_value_loss")]   public double TradeTickValueLoss { get; set; }
    [JsonPropertyName("trade_contract_size")]     public double TradeContractSize { get; set; }
    [JsonPropertyName("volume_min")]             public double VolumeMin { get; set; }
    [JsonPropertyName("volume_max")]             public double VolumeMax { get; set; }
    [JsonPropertyName("volume_step")]            public double VolumeStep { get; set; }
    [JsonPropertyName("margin_initial")]         public double MarginInitial { get; set; }
    [JsonPropertyName("margin_1lot")]            public double Margin1Lot { get; set; }
    [JsonPropertyName("spread")]                 public int Spread { get; set; }
    [JsonPropertyName("currency_base")]          public string CurrencyBase { get; set; } = "";
    [JsonPropertyName("currency_profit")]        public string CurrencyProfit { get; set; } = "";
    [JsonPropertyName("currency_margin")]        public string CurrencyMargin { get; set; } = "";
    [JsonPropertyName("trade_stops_level")]       public int TradeStopsLevel { get; set; }
    [JsonPropertyName("trade_freeze_level")]      public int TradeFreezeLevel { get; set; }
}

/// <summary>OHLCV bar from GET_RATES command.</summary>
public class Bar
{
    [JsonPropertyName("time")]   public long Time { get; set; }
    [JsonPropertyName("open")]   public double Open { get; set; }
    [JsonPropertyName("high")]   public double High { get; set; }
    [JsonPropertyName("low")]    public double Low { get; set; }
    [JsonPropertyName("close")]  public double Close { get; set; }
    [JsonPropertyName("volume")] public long Volume { get; set; }
}

/// <summary>Historical deal from HISTORY_DEALS command.</summary>
public class Deal
{
    [JsonPropertyName("ticket")]      public long Ticket { get; set; }
    [JsonPropertyName("order")]       public long Order { get; set; }
    [JsonPropertyName("symbol")]      public string Symbol { get; set; } = "";
    [JsonPropertyName("type")]        public int Type { get; set; }
    [JsonPropertyName("volume")]      public double Volume { get; set; }
    [JsonPropertyName("price")]       public double Price { get; set; }
    [JsonPropertyName("profit")]      public double Profit { get; set; }
    [JsonPropertyName("swap")]        public double Swap { get; set; }
    [JsonPropertyName("commission")]  public double Commission { get; set; }
    [JsonPropertyName("time")]        public long Time { get; set; }
    [JsonPropertyName("magic")]       public int Magic { get; set; }
    [JsonPropertyName("comment")]     public string Comment { get; set; } = "";
    [JsonPropertyName("position_id")] public long PositionId { get; set; }
    [JsonPropertyName("entry")]       public int Entry { get; set; }  // 0=IN, 1=OUT, 2=INOUT, 3=OUT_BY

    public double TotalPnl => Profit + Swap + Commission;
}

/// <summary>Order execution result from ORDER_SEND command.</summary>
public class OrderResult
{
    [JsonPropertyName("order")]   public long Order { get; set; }
    [JsonPropertyName("deal")]    public long Deal { get; set; }
    [JsonPropertyName("price")]   public double Price { get; set; }
    [JsonPropertyName("volume")]  public double Volume { get; set; }
    [JsonPropertyName("comment")] public string Comment { get; set; } = "";
}

/// <summary>Worker response envelope.</summary>
public class WorkerResponse
{
    [JsonPropertyName("id")]        public int? Id { get; set; }
    [JsonPropertyName("status")]    public string Status { get; set; } = "";
    [JsonPropertyName("data")]      public System.Text.Json.JsonElement? Data { get; set; }
    [JsonPropertyName("message")]   public string? Message { get; set; }
    [JsonPropertyName("code")]      public int? Code { get; set; }
    [JsonPropertyName("retryable")] public bool Retryable { get; set; }

    public bool IsOk => Status == "ok";
}

/// <summary>Symbol info returned by SYMBOLS_GET command (for auto-mapping).</summary>
public class BrokerSymbol
{
    [JsonPropertyName("name")]            public string Name { get; set; } = "";
    [JsonPropertyName("visible")]         public bool Visible { get; set; }
    [JsonPropertyName("path")]            public string Path { get; set; } = "";
    [JsonPropertyName("currency_base")]   public string CurrencyBase { get; set; } = "";
    [JsonPropertyName("currency_profit")] public string CurrencyProfit { get; set; } = "";
}

/// <summary>Result of CALC_POSITIONS_MARGIN: per-position margin + PnL with own/total split.</summary>
public class PositionsMarginResult
{
    [JsonPropertyName("own_margin")]   public double OwnMargin { get; set; }
    [JsonPropertyName("own_profit")]   public double OwnProfit { get; set; }
    [JsonPropertyName("total_margin")] public double TotalMargin { get; set; }
    [JsonPropertyName("total_profit")] public double TotalProfit { get; set; }
    [JsonPropertyName("positions")]    public List<PositionMarginEntry> Positions { get; set; } = new();
}

public class PositionMarginEntry
{
    [JsonPropertyName("ticket")]     public long Ticket { get; set; }
    [JsonPropertyName("symbol")]     public string Symbol { get; set; } = "";
    [JsonPropertyName("magic")]      public int Magic { get; set; }
    [JsonPropertyName("type")]       public string Type { get; set; } = "";
    [JsonPropertyName("volume")]     public double Volume { get; set; }
    [JsonPropertyName("margin")]     public double Margin { get; set; }
    [JsonPropertyName("profit")]     public double Profit { get; set; }
    [JsonPropertyName("swap")]       public double Swap { get; set; }
    [JsonPropertyName("price_open")] public double PriceOpen { get; set; }
}

/// <summary>Pending order from ORDERS_GET command.</summary>
public class BrokerPendingOrder
{
    [JsonPropertyName("ticket")]        public long Ticket { get; set; }
    [JsonPropertyName("symbol")]        public string Symbol { get; set; } = "";
    [JsonPropertyName("type")]          public int Type { get; set; }   // MT5 order type int
    [JsonPropertyName("volume")]        public double Volume { get; set; }
    [JsonPropertyName("price_open")]    public double PriceOpen { get; set; }
    [JsonPropertyName("sl")]            public double SL { get; set; }
    [JsonPropertyName("tp")]            public double TP { get; set; }
    [JsonPropertyName("price_current")] public double PriceCurrent { get; set; }
    [JsonPropertyName("time_setup")]    public long TimeSetup { get; set; }
    [JsonPropertyName("magic")]         public int Magic { get; set; }
    [JsonPropertyName("comment")]       public string Comment { get; set; } = "";

    // MT5 order type constants: 2=BUY_LIMIT, 3=SELL_LIMIT, 4=BUY_STOP, 5=SELL_STOP
    public bool IsBuyStop  => Type == 4;
    public bool IsSellStop => Type == 5;
}
