using Core.Trade;

namespace Core.BrokerageCsv;

public interface BrokerageCsvUtils
{
    public static abstract List<TradeActivity> LoadCsv(string csvPath);
}