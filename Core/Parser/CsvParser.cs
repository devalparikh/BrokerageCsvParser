using Core.BrokerageCsv;
using Core.Trade;

namespace Core.Parser;

public static class CsvParser
{
    public static List<TradeActivity> Load<T>(string csvPath)
        where T : BrokerageCsvUtils
    {
        return T.LoadCsv(csvPath);
    }
}