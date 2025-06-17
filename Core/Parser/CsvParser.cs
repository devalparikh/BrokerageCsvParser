using System.Globalization;
using Core.BrokerageCsv;
using Core.Trade;
using CsvHelper;
using CsvHelper.Configuration;

namespace Core.Parser;

public static class CsvParser
{
    public static List<TradeActivity> Load<T>(string csvPath)
        where T : BrokerageCsvUtils
    {
        return T.LoadCsv(csvPath);
    }
}