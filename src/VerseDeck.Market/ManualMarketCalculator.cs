namespace VerseDeck.Market;

public sealed record ManualMarketRun(string Commodity, string BuyLocation, string SellLocation, decimal Scu, decimal BuyPricePerScu, decimal SellPricePerScu, decimal ExtraCosts)
{
    public decimal Investment => Scu * BuyPricePerScu + ExtraCosts;
    public decimal FinalSale => Scu * SellPricePerScu;
    public decimal Profit => FinalSale - Investment;
}

public interface ICommunityMarketService
{
    bool IsEnabled { get; }
}
