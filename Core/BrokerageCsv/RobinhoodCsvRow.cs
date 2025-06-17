using System.Globalization;
using Core.Trade;
using CsvHelper;
using CsvHelper.Configuration;
using CsvHelper.Configuration.Attributes;
using CsvHelper.TypeConversion;

namespace Core.BrokerageCsv;

internal sealed record RobinhoodCsvRow
{
    [Name("Activity Date")]
    public DateTime? ActivityDate { get; set; }

    [Name("Process Date")]
    public DateTime? ProcessDate { get; set; }

    [Name("Settle Date")]
    public DateTime? SettleDate { get; set; }

    [Name("Instrument")]
    public string? Instrument { get; set; }

    [Name("Description")] 
    public string? Description { get; set; }

    [Name("Trans Code")] 
    public string? TransCode { get; set; }

    [Name("Quantity")]
    [TypeConverter(typeof(NullableDecimalConverter))]
    public decimal? Quantity { get; set; }

    [Name("Price")]
    [TypeConverter(typeof(NullableDecimalConverter))]
    public decimal? Price { get; set; }

    [Name("Amount")]
    [TypeConverter(typeof(NullableDecimalConverter))]
    public decimal? Amount { get; set; }
}

public class RobinHoodCsvUtils : BrokerageCsvUtils
{
    public static List<TradeActivity> LoadCsv(string csvPath)
    {
        var csvConfiguration = new CsvConfiguration(CultureInfo.InvariantCulture)
        {
            HasHeaderRecord = true,
            IgnoreBlankLines = true,
            BadDataFound = null, // swallow rows that still look broken
            MissingFieldFound = null,
            Delimiter = ","
        };

        using var reader = new StreamReader(csvPath);
        using var csv = new CsvReader(reader, csvConfiguration);

        // register converter once, so any nullable decimal field re‑uses it
        csv.Context.TypeConverterCache.AddConverter<decimal?>(new NullableDecimalConverter());

        var rows = csv.GetRecords<RobinhoodCsvRow>().ToList();
        return rows
            .Where(r => r.ActivityDate != null) // skip rows with missing date
            .Select(ToTradeActivity!)
            .ToList();
    }

    private static TradeActivity ToTradeActivity(RobinhoodCsvRow r)
    {
        var activity = new TradeActivity
        {
            Date = r.ActivityDate!.Value,
            Symbol = r.Instrument,
            Type = MapActivityType(r.TransCode, r.Description),
            Quantity = r.Quantity,
            Price = r.Price,
            Amount = r.Amount,
            Notes = r.Description
        };

        if (TryParseOptionContract(r.Description, out var underlying, out var exp, out var strike, out var type))
        {
            activity.IsOption = true;
            activity.Underlying = underlying;
            activity.Expiration = exp;
            activity.StrikePrice = strike;
            activity.OptionType = type;
        }

        return activity;
    }

    private static bool TryParseOptionContract(
        string? description,
        out string? underlying,
        out DateTime? expiration,
        out decimal? strike,
        out OptionType? type)
    {
        underlying = null;
        expiration = null;
        strike = null;
        type = null;

        if (string.IsNullOrWhiteSpace(description))
            return false;

        var parts = description.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 4)
            return false;

        try
        {
            underlying = parts[0];

            if (!DateTime.TryParse(parts[1], CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
                return false;

            expiration = date;
            type = parts[2].Equals("Call", StringComparison.OrdinalIgnoreCase)
                ? OptionType.Call
                : parts[2].Equals("Put", StringComparison.OrdinalIgnoreCase)
                    ? OptionType.Put
                    : null;

            var strikeText = parts[3].Trim().TrimStart('$');
            strike = decimal.Parse(strikeText, NumberStyles.Any, CultureInfo.InvariantCulture);
            return type != null;
        }
        catch
        {
            return false;
        }
    }

    private static ActivityType MapActivityType(string? code, string? description)
    {
        code = code?.Trim().ToUpperInvariant();
        description = description?.ToLowerInvariant();

        return code switch
        {
            "BUY" => ActivityType.Buy,
            "SELL" => ActivityType.Sell,
            "STO" => ActivityType.STO,
            "BTC" => ActivityType.BTC,
            "OASGN" => ActivityType.Assignment,
            "EXP" => ActivityType.Expired,
            "DIV" => ActivityType.Dividend,
            "INT" => ActivityType.Interest,
            "XFER" => ActivityType.Transfer,
            _ when description?.Contains("assignment") == true => ActivityType.Assignment,
            _ when description?.Contains("expire") == true => ActivityType.Expired,
            _ when description?.Contains("dividend") == true => ActivityType.Dividend,
            _ => ActivityType.Unknown
        };
    }
}

public sealed class NullableDecimalConverter : DefaultTypeConverter
{
    public override object? ConvertFromString(string? text, IReaderRow row, MemberMapData mapData)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;

        text = text.Trim()
            .Replace("$", string.Empty)
            .Replace(",", string.Empty);

        var negative = false;
        if (text.StartsWith("(") && text.EndsWith(")"))
        {
            negative = true;
            text = text[1..^1]; // drop the parentheses
        }

        if (decimal.TryParse(text, NumberStyles.Any, CultureInfo.InvariantCulture, out var val))
            return negative ? -val : val;

        // fall back to null on any strange format
        return null;
    }
}