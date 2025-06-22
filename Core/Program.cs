using Core.BrokerageCsv;
using Core.Trade;
using CsvParser = Core.Parser.CsvParser;


var path =
    "/Users/devalparikh/Documents/Github/BrokerageCsvParser/Core/Data/071a529a-878c-544a-8363-1f7350bffe78.csv";

var path2 = 
    "/Users/devalparikh/Documents/Github/BrokerageCsvParser/Core/Data/67006d3a-af7b-50b5-8028-1e9355e1b345.csv";

var trades = CsvParser.Load<RobinHoodCsvUtils>(path);
trades.Reverse();

var portfolio = new Portfolio();
// portfolio.LoadCash(trades);
portfolio.LoadShares(trades);
portfolio.LoadOptions(trades);

// Console.WriteLine($"Shares NVDA Realized Pnl: {portfolio.SharePositions["NVDA"].RealizedPnL}");
// Console.WriteLine($"Shares Overall Realized Pnl: {portfolio.SharePositions["NVDA"].RealizedPnL}");

var trades2 = CsvParser.Load<RobinHoodCsvUtils>(path2);
trades2.Reverse();
portfolio.LoadShares(trades2);
portfolio.LoadOptions(trades2);

Console.WriteLine($"trade - {portfolio.TotalRealizedPnL}");
Console.WriteLine($"total shares realized - {portfolio.SharesRealizedPnL}");
Console.WriteLine($"total options realized - {portfolio.OptionsRealizedPnL}");


internal class Portfolio
{
    public Dictionary<string, SharePosition> SharePositions = new();
    public Dictionary<string, OptionPosition> OptionsPositions = new();
 
    public decimal SharesRealizedPnL;
    public decimal OptionsRealizedPnL;
    public decimal TotalRealizedPnL;
    
    public void LoadShares(List<TradeActivity> trades)
    {
        foreach (var trade in trades)
        {
            if (!SharePositions.ContainsKey(trade.Symbol!))
                SharePositions[trade.Symbol!] = new SharePosition(trade.Symbol!);

            // Includes orders and assignments
            if (trade.Type == ActivityType.Buy)
                SharePositions[trade.Symbol!].Buy(trade);
            if (trade.Type == ActivityType.Sell)
            {
                var pnL = SharePositions[trade.Symbol!].Sell(trade.Quantity!.Value, trade.Price!.Value);
                SharesRealizedPnL += pnL;
                TotalRealizedPnL += pnL;
            }
        }
    }

    public void LoadOptions(List<TradeActivity> trades)
    {
        foreach (var trade in trades.Where(t => t.IsOption))
        {
            // TODO make key unique by contract
            // “key” should encodes underlying/expiry/strike/type
            // var key = trade.Symbol!;
            var key = trade.Symbol! + "_" + trade.Expiration! + "_" + trade.StrikePrice + "_" + trade.OptionType;
            if (!OptionsPositions.ContainsKey(key))
            {
                OptionsPositions[key] = new OptionPosition(key);
            }
            
            var qty = trade.Quantity;
            var price = trade.Price; // price is premium per contract

            var realizedPnL = 0m;
            switch (trade.Type)
            {
                case ActivityType.STO: // Sell‑to‑Open
                    OptionsPositions[key].SellToOpen(trade);
                    break;

                case ActivityType.BTC: // Buy‑to‑Close
                    realizedPnL += OptionsPositions[key]
                        .BuyToClose(qty!.Value, price!.Value);
                    break;

                case ActivityType.Expired: // Expired worthless
                    realizedPnL += OptionsPositions[key].Expire();
                    break;

                case ActivityType.Assignment: // Assigned
                    realizedPnL += OptionsPositions[key].Assign();
                    break;
            }

            OptionsRealizedPnL += realizedPnL;
            TotalRealizedPnL += realizedPnL;
        }
    }
}

internal class SharePosition
{
    public decimal Quantity;
    public decimal RealizedPnL;
    public string Symbol;

    private readonly PriorityQueue<TradeActivity, DateTime> taxLots = new();
    public decimal TotalCost;

    public SharePosition(string symbol)
    {
        Symbol = symbol;
    }

    public decimal CostBasis => Quantity == 0 ? 0 : TotalCost / Quantity;

    public void Buy(TradeActivity trade)
    {
        var pricePerShare = trade.Price!.Value;
        var sharesPurchased = trade.Quantity!.Value;
        TotalCost += pricePerShare * sharesPurchased;
        Quantity += sharesPurchased;

        taxLots.Enqueue(trade, trade.Date);
    }

    public decimal Sell(decimal quantityToSell, decimal salePricePerShare)
    {
        if (quantityToSell <= 0) throw new ArgumentOutOfRangeException(nameof(quantityToSell));
        if (salePricePerShare <= 0) throw new ArgumentOutOfRangeException(nameof(salePricePerShare));

        // Robinhood free stock doesn't export 
        //if (quantityToSell > Quantity) throw new InvalidOperationException("Not enough shares.");

        decimal realizedPnL = 0;
        while (quantityToSell > 0 && taxLots.Count > 0)
        {
            var lot = taxLots.Dequeue();

            // max you can sell for this lot - is the lot itself
            var lotSellQty = Math.Min(quantityToSell, lot.Quantity!.Value);
            var lotCostBasis = lot.Price!.Value;
            var proceeds = salePricePerShare * lotSellQty;
            var cost = lotCostBasis * lotSellQty;
            realizedPnL += proceeds - cost;

            Quantity -= lotSellQty;
            TotalCost -= cost;

            var isEntireLotSold = lotSellQty == lot.Quantity;
            if (!isEntireLotSold)
            {
                // Partially consume the front lot
                lot.Quantity -= lotSellQty;
                lot.Notes += " Closed via Sell";
                taxLots.Enqueue(lot, lot.Date);
            }

            quantityToSell -= lotSellQty;
        }

        RealizedPnL += realizedPnL;
        return realizedPnL;
    }
}

internal class OptionPosition
{
    private const int ContractMultiplier = 100; // 1 contract = 100 shares
    private readonly PriorityQueue<TradeActivity, DateTime> taxLots = new();
    private readonly PriorityQueue<TradeActivity, DateTime> closedLots = new();
    public decimal Quantity; // open SHORT contracts
    public decimal RealizedPnL;

    public string Symbol;
    public decimal TotalCredit; // running premium collected

    public OptionPosition(string symbol)
    {
        Symbol = symbol;
    }


    public void SellToOpen(TradeActivity trade)
    {
        var credit = trade.Price!.Value * trade.Quantity!.Value * ContractMultiplier;
        TotalCredit += credit;
        Quantity += trade.Quantity!.Value;
        taxLots.Enqueue(trade, trade.Date);
    }

    public decimal BuyToClose(decimal contractsToClose, decimal debitPerContract)
    {
        if (contractsToClose <= 0) throw new ArgumentOutOfRangeException(nameof(contractsToClose));
        if (debitPerContract < 0) throw new ArgumentOutOfRangeException(nameof(debitPerContract));

        var realized = 0m;

        while (contractsToClose > 0 && taxLots.Count > 0)
        {
            var lot = taxLots.Dequeue();
            var lotQty = Math.Min(contractsToClose, lot.Quantity!.Value);

            var lotCredit = lot.Price!.Value * lotQty * ContractMultiplier;
            var lotDebit = debitPerContract * lotQty * ContractMultiplier;

            realized += lotCredit - lotDebit;
            Quantity -= lotQty;
            TotalCredit -= lotCredit;

            var entireLotClosed = lotQty == lot.Quantity;
            if (entireLotClosed)
            {
                lot.Type = ActivityType.BTC;
                closedLots.Enqueue(lot, lot.Date);
            }
            else
            {
                // partial lot close
                lot.Quantity -= lotQty;
                lot.Notes += " Closed via BTC";
                taxLots.Enqueue(lot, lot.Date);
            }

            contractsToClose -= lotQty;
        }

        RealizedPnL += realized;
        return realized;
    }

    public decimal Expire()
    {
        return CloseLots(ActivityType.Expired);
    }

    public decimal Assign()
    {
        return CloseLots(ActivityType.Assignment);
        // keep premium, position flattened
    }

    private decimal CloseLots(ActivityType activityType)
    {
        var realized = 0m;

        while (taxLots.Count > 0)
        {
            var lot = taxLots.Dequeue();
            lot.Type = activityType;
            realized += lot.Price!.Value * lot.Quantity!.Value * ContractMultiplier;
            closedLots.Enqueue(lot, lot.Date);
        }


        ResetPosition();
        RealizedPnL += realized;
        return realized;
    }

    private void ResetPosition()
    {
        taxLots.Clear();
        Quantity = 0m;
        TotalCredit = 0m;
    }
}