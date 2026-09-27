using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

using Alpaca.Markets;

namespace CryptoTradingBots;

internal static class MomentumBot
{
    private static readonly string[] Symbols =
    {
        "BTC/USD","ETH/USD","DOGE/USD","SHIB/USD",
        "MATIC/USD","ALGO/USD","AVAX/USD","LINK/USD","SOL/USD"
    };
    private static readonly string[] SymbolsNS = Symbols.Select(s => s.Replace("/", "")).ToArray();

    public static async Task RunAsync()
    {
        // Initialize clients
        var dataClient = Environments.Live.GetAlpacaCryptoDataClient(
            new SecretKey(Secrets.KEY_ID, Secrets.SECRET_KEY));
        var tradingClient = Environments.Paper.GetAlpacaTradingClient(
            new SecretKey(Secrets.KEY_ID, Secrets.SECRET_KEY));
        var marketStreamingClient = Environments.Live.GetAlpacaCryptoStreamingClient(
            new SecretKey(Secrets.KEY_ID, Secrets.SECRET_KEY));
        await marketStreamingClient.ConnectAndAuthenticateAsync();

        await CloseAllPositions(tradingClient);

        async void OnBar(IBar bar)
        {
            Console.WriteLine($"Bar: {bar.Symbol} {bar.Close}");

            await CrossSectionalMomentum(dataClient, tradingClient);
        }

        foreach (string symbol in Symbols)
        {
            var sub = marketStreamingClient.GetDailyBarSubscription(symbol);
            sub.Received += OnBar;
            await marketStreamingClient.SubscribeAsync(sub);
        }

        await Task.Delay(-1);
    }

    private static async Task CloseAllPositions(IAlpacaTradingClient tradingClient)
    {
        var positions = await tradingClient.ListPositionsAsync();
        foreach (var pos in positions)
        {
            await tradingClient.PostOrderAsync(
                OrderSide.Sell.Market(
                    pos.Symbol,
                    OrderQuantity.Fractional(pos.AvailableQuantity)
                ).WithDuration(TimeInForce.Ioc)
            );
        }
    }

    private static async Task<decimal> CheckPosition(IAlpacaTradingClient tradingClient, string symbol)
    {
        var positions = await tradingClient.ListPositionsAsync();

        var pos = positions.FirstOrDefault(p => p.Symbol == symbol);
        return pos != null ? pos.Quantity : 0m;
    }

    private static async Task CrossSectionalMomentum(
        IAlpacaCryptoDataClient dataClient, IAlpacaTradingClient tradingClient)
    {
        try
        {
            var end = DateTime.UtcNow;
            var start = end.AddDays(-60);

            var priceData = new Dictionary<string, List<decimal>>();

            // 📊 Get historical data
            foreach (string symbol in Symbols)
            {
                var bars = (await dataClient.ListHistoricalBarsAsync(
                    new HistoricalCryptoBarsRequest(symbol, start, end, BarTimeFrame.Day))).Items;

                var closes = bars.Select(b => b.Close).ToList();
                priceData[symbol] = closes;
            }

            // 📈 Calculate returns
            var returns = new Dictionary<string, List<decimal>>();

            foreach (var kvp in priceData)
            {
                var prices = kvp.Value;
                var r = new List<decimal>();

                for (int i = 0; i < prices.Count - 1; i++)
                {
                    var ret = (prices[i + 1] / prices[i]) - 1;
                    r.Add(ret);
                }

                returns[kvp.Key] = r;
            }

            // 🚀 Momentum calculation (7-day lookback)
            var momentum = new Dictionary<string, decimal>();

            foreach (var kvp in returns)
            {
                var r = kvp.Value;

                if (r.Count < 7) continue;

                var recent = r[^2];     // shift(1)
                var past = r[^8];       // shift(7)

                var mom = (recent / past) - 1;
                momentum[kvp.Key] = mom;
            }

            // 🏆 Rank and pick best
            var ranked = momentum
                .OrderByDescending(x => x.Value)
                .ToList();

            var buySymbol = ranked.First().Key;

            Console.WriteLine($"Top Momentum: {buySymbol}");

            // 🧾 Account info
            var account = await tradingClient.GetAccountAsync();
            var buyingPower = account.TradableCash;

            var currentPosition = await CheckPosition(tradingClient, buySymbol);

            // 🔁 Find previously held symbol
            var positions = await tradingClient.ListPositionsAsync();
            var oldPosition = positions.FirstOrDefault();

            // 🟢 No positions → Buy
            if (!positions.Any())
            {
                await tradingClient.PostOrderAsync(
                    OrderSide.Buy.Market(
                        buySymbol,
                        OrderQuantity.Notional(buyingPower)
                    ).WithDuration(TimeInForce.Ioc)
                );

                Console.WriteLine($"BUY {buySymbol} | ${buyingPower}");
            }
            else if (oldPosition != null && oldPosition.Symbol != buySymbol)
            {
                // 🔴 Sell old
                await tradingClient.PostOrderAsync(
                    OrderSide.Sell.Market(
                        oldPosition.Symbol,
                        OrderQuantity.Fractional(oldPosition.AvailableQuantity)
                    ).WithDuration(TimeInForce.Ioc)
                );

                Console.WriteLine($"SELL {oldPosition.Symbol}");

                // 🟢 Buy new
                await tradingClient.PostOrderAsync(
                    OrderSide.Buy.Market(
                        buySymbol,
                        OrderQuantity.Notional(buyingPower)
                    ).WithDuration(TimeInForce.Ioc)
                );

                Console.WriteLine($"BUY {buySymbol} | ${buyingPower}");
            }

            Console.WriteLine("----------------------");
        }
        catch (Exception ex)
        {
            Console.WriteLine(ex.Message);
        }
    }
}
