using System.Globalization;
using CsvHelper;
using CsvHelper.Configuration;
using CsvHelper.Configuration.Attributes;
using CsvHelper.TypeConversion;

namespace Core.BrokerageCsv;

public sealed class RobinhoodCsvRow
{
    [Name("Activity Date")]  public DateTime? ActivityDate { get; set; }

    [Name("Process Date")]   public DateTime? ProcessDate { get; set; }

    [Name("Settle Date")]    public DateTime? SettleDate  { get; set; }

    [Name("Instrument")]     public string?  Instrument   { get; set; }

    [Name("Description")]    public string?  Description  { get; set; }

    [Name("Trans Code")]     public string?  TransCode    { get; set; }

    [Name("Quantity"), TypeConverter(typeof(NullableDecimalConverter))]
    public decimal? Quantity { get; set; }

    [Name("Price"),    TypeConverter(typeof(NullableDecimalConverter))]
    public decimal? Price    { get; set; }

    [Name("Amount"),   TypeConverter(typeof(NullableDecimalConverter))]
    public decimal? Amount   { get; set; }
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

        bool negative = false;
        if (text.StartsWith("(") && text.EndsWith(")"))
        {
            negative = true;
            text = text[1..^1];              // drop the parentheses
        }

        if (decimal.TryParse(text, NumberStyles.Any, CultureInfo.InvariantCulture, out var val))
            return negative ? -val : val;

        // fall back to null on any strange format
        return null;
    }
}