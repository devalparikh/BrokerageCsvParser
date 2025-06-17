using Core.BrokerageCsv;
using Core.Trade;
using CsvParser = Core.Parser.CsvParser;


string path =
    "/Users/devalparikh/Documents/Github/BrokerageCsvParser/Core/Data/071a529a-878c-544a-8363-1f7350bffe78.csv";
List<TradeActivity> trades = CsvParser.Load<RobinHoodCsvUtils>(path);
trades.Reverse();

Portfolio portfolio = new Portfolio();
// portfolio.LoadCash(trades);
portfolio.LoadShares(trades);
portfolio.LoadOptions(trades);

Console.WriteLine($"Shares Realized Pnl: {portfolio.SharePositions["NVDA"].RealizedPnL}");
Console.WriteLine($"Options Realized Pnl: {portfolio.OptionsPositions["NVDA"].RealizedPnL}");
Console.WriteLine(portfolio.SharePositions["NVDA"].RealizedPnL + portfolio.OptionsPositions["NVDA"].RealizedPnL);
Console.WriteLine(portfolio.RealizedPnL);

class Portfolio
{
    public Dictionary<string, SharePosition> SharePositions = new Dictionary<string, SharePosition>();
    public Dictionary<string, OptionPosition> OptionsPositions = new Dictionary<string, OptionPosition>();

    public decimal RealizedPnL = 0;

    public void LoadShares(List<TradeActivity> trades)
    {
        foreach (TradeActivity trade in trades)
        {
            if (!SharePositions.ContainsKey(trade.Symbol!))
            {
                SharePositions[trade.Symbol!] = new SharePosition(trade.Symbol!);
            }

            // Includes orders and assignments
            if (trade.Type == ActivityType.Buy)
                SharePositions[trade.Symbol!].Buy(trade);
            if (trade.Type == ActivityType.Sell)
                RealizedPnL += SharePositions[trade.Symbol!].Sell(trade.Quantity!.Value, trade.Price!.Value);
        }
    }

    public void LoadOptions(List<TradeActivity> trades)
    {
        foreach (TradeActivity trade in trades.Where(t => t.IsOption))
        {
            // “Symbol” already encodes underlying/expiry/strike/type in OCC format 
            // TODO make key unique by contract
            string key = trade.Symbol!;
            if (!OptionsPositions.ContainsKey(key))
                OptionsPositions[key] = new OptionPosition(key);

            decimal? qty = trade.Quantity;
            decimal? price = trade.Price; // price is premium per contract

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

class SharePosition
{
    public string Symbol;
    public decimal Quantity = 0;
    public decimal TotalCost = 0;
    public decimal CostBasis => Quantity == 0 ? 0 : TotalCost / Quantity;
    public decimal RealizedPnL = 0;

    // private LinkedList<TradeActivity> taxLots = new LinkedList<TradeActivity>();
    private PriorityQueue<TradeActivity, DateTime> taxLots = new();
    
    public SharePosition(string symbol)
    {
        Symbol = symbol;
    }

    public void Buy(TradeActivity trade)
    {
        decimal pricePerShare = trade.Price!.Value;
        decimal sharesPurchased = trade.Quantity!.Value;
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
            decimal lotSellQty = Math.Min(quantityToSell, lot.Quantity!.Value);
            decimal lotCostBasis = lot.Price!.Value;
            decimal proceeds = salePricePerShare * lotSellQty;
            decimal cost = lotCostBasis * lotSellQty;
            realizedPnL += proceeds - cost;

            Quantity -= lotSellQty;
            TotalCost -= cost;

            bool isEntireLotSold = lotSellQty == lot.Quantity;
            if (!isEntireLotSold)
            {
                // Partially consume the front lot
                lot.Quantity -= lotSellQty;
                lot.Notes += "Closed via Sell";
                taxLots.Enqueue(lot, lot.Date);
            }
            else
            {
                
            }

            quantityToSell -= lotSellQty;
        }

        RealizedPnL += realizedPnL;
        return realizedPnL;
    }
}

class OptionPosition
{
    private const int ContractMultiplier = 100; // 1 contract = 100 shares

    public string Symbol;
    public decimal Quantity = 0m; // open SHORT contracts
    public decimal TotalCredit = 0m; // running premium collected
    public decimal RealizedPnL = 0m;

    private readonly LinkedList<TradeActivity> taxLots = new();
    private readonly LinkedList<TradeActivity> closedLots = new();

    public OptionPosition(string symbol) => Symbol = symbol;


    public void SellToOpen(TradeActivity trade)
    {
        decimal credit = trade.Price!.Value * trade.Quantity!.Value * ContractMultiplier;
        TotalCredit += credit;
        Quantity += trade.Quantity!.Value;
        taxLots.AddLast(trade);
    }

    public decimal BuyToClose(decimal contractsToClose, decimal debitPerContract)
    {
        if (contractsToClose <= 0) throw new ArgumentOutOfRangeException(nameof(contractsToClose));
        if (debitPerContract < 0) throw new ArgumentOutOfRangeException(nameof(debitPerContract));

        decimal realized = 0m;
        var node = taxLots.First;

        while (contractsToClose > 0 && node != null)
        {
            var lot = node.Value;
            decimal lotQty = Math.Min(contractsToClose, lot.Quantity!.Value);

            decimal lotCredit = lot.Price!.Value * lotQty * ContractMultiplier;
            decimal lotDebit = debitPerContract * lotQty * ContractMultiplier;

            realized += lotCredit - lotDebit;
            Quantity -= lotQty;
            TotalCredit -= lotCredit;

            bool entireLotClosed = lotQty == lot.Quantity;
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
        decimal realized = 0m;

        foreach (var lot in taxLots)
        {
            realized += lot.Price!.Value * lot.Quantity!.Value * ContractMultiplier;
            closedLots.AddLast(lot); 
        }
            

        ResetPosition();
        RealizedPnL += realized;
        return realized;
    }

    public decimal Assign() => Expire(); // keep premium, position flattened

    private void ResetPosition()
    {
        taxLots.Clear();
        Quantity = 0m;
        TotalCredit = 0m;
    }
}