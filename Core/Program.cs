using Core.BrokerageCsv;
using Core.Trade;
using CsvParser = Core.Parser.CsvParser;


var path =
    "/Users/devalparikh/Documents/Github/BrokerageCsvParser/Core/Data/071a529a-878c-544a-8363-1f7350bffe78.csv";
var trades = CsvParser.Load<RobinHoodCsvUtils>(path);
trades.Reverse();

var portfolio = new Portfolio();
// portfolio.LoadCash(trades);
portfolio.LoadShares(trades);
portfolio.LoadOptions(trades);

Console.WriteLine($"Shares Realized Pnl: {portfolio.SharePositions["NVDA"].RealizedPnL}");
Console.WriteLine($"Options Realized Pnl: {portfolio.OptionsPositions["NVDA"].RealizedPnL}");
Console.WriteLine(portfolio.SharePositions["NVDA"].RealizedPnL + portfolio.OptionsPositions["NVDA"].RealizedPnL);
Console.WriteLine(portfolio.RealizedPnL);

internal class Portfolio
{
    public Dictionary<string, OptionPosition> OptionsPositions = new();

    public decimal RealizedPnL;
    public Dictionary<string, SharePosition> SharePositions = new();

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
                RealizedPnL += SharePositions[trade.Symbol!].Sell(trade.Quantity!.Value, trade.Price!.Value);
        }
    }

    public void LoadOptions(List<TradeActivity> trades)
    {
        foreach (var trade in trades.Where(t => t.IsOption))
        {
            // “Symbol” already encodes underlying/expiry/strike/type in OCC format 
            // TODO make key unique by contract
            var key = trade.Symbol!;
            if (!OptionsPositions.ContainsKey(key))
                OptionsPositions[key] = new OptionPosition(key);

            var qty = trade.Quantity;
            var price = trade.Price; // price is premium per contract

            switch (trade.Type)
            {
                case ActivityType.STO: // Sell‑to‑Open
                    OptionsPositions[key].SellToOpen(trade);
                    break;

                case ActivityType.BTC: // Buy‑to‑Close
                    RealizedPnL += OptionsPositions[key]
                        .BuyToClose(qty!.Value, price!.Value);
                    break;

                case ActivityType.Expired: // Expired worthless
                    RealizedPnL += OptionsPositions[key].Expire();
                    break;

                case ActivityType.Assignment: // Assigned
                    RealizedPnL += OptionsPositions[key].Assign();
                    break;
            }
        }
    }
}

internal class SharePosition
{
    public decimal Quantity;
    public decimal RealizedPnL;
    public string Symbol;

    // private LinkedList<TradeActivity> taxLots = new LinkedList<TradeActivity>();
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
                lot.Notes += "Closed via Sell";
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
    private readonly LinkedList<TradeActivity> closedLots = new();

    private readonly LinkedList<TradeActivity> taxLots = new();
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
        taxLots.AddLast(trade);
    }

    public decimal BuyToClose(decimal contractsToClose, decimal debitPerContract)
    {
        if (contractsToClose <= 0) throw new ArgumentOutOfRangeException(nameof(contractsToClose));
        if (debitPerContract < 0) throw new ArgumentOutOfRangeException(nameof(debitPerContract));

        var realized = 0m;
        var node = taxLots.First;

        while (contractsToClose > 0 && node != null)
        {
            var lot = node.Value;
            var lotQty = Math.Min(contractsToClose, lot.Quantity!.Value);

            var lotCredit = lot.Price!.Value * lotQty * ContractMultiplier;
            var lotDebit = debitPerContract * lotQty * ContractMultiplier;

            realized += lotCredit - lotDebit;
            Quantity -= lotQty;
            TotalCredit -= lotCredit;

            var entireLotClosed = lotQty == lot.Quantity;
            if (entireLotClosed)
            {
                var next = node.Next;
                taxLots.Remove(node);
                closedLots.AddLast(lot);
                node = next;
            }
            else
            {
                lot.Quantity -= lotQty; // partial close
                node = node.Next;
            }

            contractsToClose -= lotQty;
        }

        RealizedPnL += realized;
        return realized;
    }

    public decimal Expire()
    {
        var realized = 0m;

        foreach (var lot in taxLots)
        {
            realized += lot.Price!.Value * lot.Quantity!.Value * ContractMultiplier;
            closedLots.AddLast(lot);
        }


        ResetPosition();
        RealizedPnL += realized;
        return realized;
    }

    public decimal Assign()
    {
        return Expire();
        // keep premium, position flattened
    }

    private void ResetPosition()
    {
        taxLots.Clear();
        Quantity = 0m;
        TotalCredit = 0m;
    }
}