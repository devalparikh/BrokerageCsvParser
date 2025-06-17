namespace Core.Trade;

public record TradeActivity
{
    public DateTime Date { get; set; }
    public string? Symbol { get; set; }
    public ActivityType Type { get; set; }
    public decimal? Quantity { get; set; }
    public decimal? Price { get; set; }
    public decimal? Amount { get; set; }
    public string? Notes { get; set; }

    // Option-specific fields
    public bool IsOption { get; set; }
    public string? Underlying { get; set; }
    public DateTime? Expiration { get; set; }
    public decimal? StrikePrice { get; set; }
    public OptionType? OptionType { get; set; }
}

public enum ActivityType
{
    Unknown,
    Buy,
    Sell,
    STO, // Sell to Open
    BTC, // Buy to Close
    Assignment,
    Expired,
    Dividend,
    Interest,
    Transfer,
    Other
}

public enum OptionType
{
    Call,
    Put
}