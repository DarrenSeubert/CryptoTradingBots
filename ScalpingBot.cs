using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

using Alpaca.Markets;

namespace CryptoTradingBots;

/// <summary>
/// Represents a scalping bot for managing crypto trading orders based on real-time market data.
/// </summary>
internal static class ScalpingBot
{
    private const string SYMBOL = "ETH/USD";
    private const string SYMBOL_NS = "ETHUSD";
    private const int HISTORICAL_DATA_MINS = 5; // 60
    private const decimal NOTIONAL_PERCENT = 0.80m;

    private const decimal MAKER_FEE = 0.0015m;
    private const decimal TAKER_FEE = 0.0025m;
    private const decimal MIN_PROFIT_BUFFER = 0.001m;

    private static decimal currentPrice;
    private static decimal buyPrice;
    private static decimal sellPrice;
    private static decimal spread;
    private static decimal totalFees;
    private static decimal currentPosition;

    private static readonly Queue<IBar> rollingBars = new();

    private static Guid? openOrderId = null;
    private static bool waitingForFill = false;

    /// <summary>
    /// Main entry point for the scalping bot. Initializes clients, fetches historical data, and 
    /// subscribes to live market data.
    /// </summary>
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

        // Fetch historical data to set initial buy/sell prices, spread, and fees
        await InitializeMarketData(dataClient);

        // Get minute bars subscription for the specified symbol
        var subscription = marketStreamingClient.GetMinuteBarSubscription(SYMBOL);

        // Handle incoming minute bars to update position and manage orders
        subscription.Received += async bar =>
        {
            Console.WriteLine("-------------------------------");
            Console.WriteLine(
                $"{bar.TimeUtc}: {bar.Symbol} Minute Bar | Open: {bar.Open}, High: {bar.High}, " +
                $"Low: {bar.Low}, Close: {bar.Close}"
            );

            UpdateRollingChannel(bar);
            PrintRollingBars();

            await UpdatePosition(tradingClient);

            // Manage orders dynamically
            IAccount account = await tradingClient.GetAccountAsync();
            await ManageOrders(tradingClient, account);
        };

        // Subscribe to the market data stream
        await marketStreamingClient.SubscribeAsync(subscription);

        Console.WriteLine("Connected to live crypto trades. Waiting for bars...");
        await Task.Delay(-1);
    }

    /// <summary>
    /// Fetches historical bars to initialize buy/sell prices, spread, and fees. Uses a look-back
    /// period defined by HISTORICAL_DATA_MINS.
    /// </summary>
    /// <param name="client"></param>
    private static async Task InitializeMarketData(IAlpacaCryptoDataClient client)
    {
        DateTime end = DateTime.UtcNow;
        DateTime start = end.AddMinutes(-HISTORICAL_DATA_MINS);

        var bars = (await client.ListHistoricalBarsAsync(
            new HistoricalCryptoBarsRequest(SYMBOL, start, end, BarTimeFrame.Minute))).Items;

        foreach (var bar in bars)
            rollingBars.Enqueue(bar);

        RecalculateChannel();

        currentPrice = bars[^1].Close;

        // Initially assume worst case fees
        totalFees = (buyPrice * TAKER_FEE) + (sellPrice * TAKER_FEE);

        Console.WriteLine($"Historical data for {SYMBOL} fetched.");
        Console.WriteLine(
            $"Current Price: {currentPrice}, Buy: {buyPrice}, Sell: {sellPrice}, Spread: {spread}"
        );
    }

    /// <summary>
    /// Maintain rolling 10 minute bar window
    /// </summary>
    private static void UpdateRollingChannel(IBar newBar)
    {
        rollingBars.Enqueue(newBar);

        if (rollingBars.Count > HISTORICAL_DATA_MINS)
            rollingBars.Dequeue();

        currentPrice = newBar.Close;

        RecalculateChannel();
    }

    /// <summary>
    /// Utility function to print the current rolling bars for debugging purposes
    /// </summary>
    private static void PrintRollingBars()
    {
    Console.WriteLine("----- Rolling 10-min Bars -----");
    int i = 1;
    foreach (var bar in rollingBars)
    {
        Console.WriteLine(
            $"Bar {i}: Time={bar.TimeUtc}, Open={bar.Open}, High={bar.High}, Low={bar.Low}, Close={bar.Close}"
        );
        i++;
    }
    Console.WriteLine("-------------------------------");
}

    /// <summary>
    /// Recalculate the rolling price channel
    /// </summary>
    private static void RecalculateChannel()
    {
        buyPrice = rollingBars.Min(x => x.Low) * 1.002m;
        sellPrice = rollingBars.Max(x => x.High) * 0.998m;

        spread = sellPrice - buyPrice;
    }

    private static async Task UpdatePosition(IAlpacaTradingClient client)
    {
        try
        {
            var pos = await client.GetPositionAsync(SYMBOL_NS);
            currentPosition = pos.Quantity;
        }
        catch (RestClientErrorException ex) when (ex.Message.Contains("position does not exist"))
        {
            currentPosition = 0;
        }

        Console.WriteLine($"Current Position: {currentPosition}");
    }

    /// <summary>
    /// Dynamically calculates total fees based on whether orders are likely taker or maker
    /// </summary>
    private static void UpdateFees()
    {
        bool buyLikelyTaker = currentPrice >= buyPrice * 0.999m; // Close to buy price
        bool sellLikelyTaker = currentPrice <= sellPrice * 1.001m; // Close to sell price

        decimal buyFee = buyLikelyTaker ? TAKER_FEE : MAKER_FEE;
        decimal sellFee = sellLikelyTaker ? TAKER_FEE : MAKER_FEE;

        totalFees = (buyPrice * buyFee) + (sellPrice * sellFee);
        Console.WriteLine(
            $"Fees updated | BuyFee: {buyFee}, SellFee: {sellFee}, TotalFees: {totalFees}"
        );
    }

    /// <summary>
    /// Manages order placement and cancellation based on current market conditions, position, and 
    /// order status
    /// </summary>
    /// <param name="client"></param>
    /// <param name="account"></param>
    private static async Task ManageOrders(IAlpacaTradingClient client, IAccount account)
    {
        UpdateFees();

        decimal minProfit = totalFees + (buyPrice * MIN_PROFIT_BUFFER);
        if (spread <= minProfit)
        {
            Console.WriteLine($"Spread ({spread}) too small to cover fees and profit buffer " + 
                $"{minProfit}. No trades will be placed."
            );
            Console.WriteLine("Spread too small to trade.");
            return;
        }

        if (waitingForFill && openOrderId != null)
        {
            var orderStatus = (await client.GetOrderAsync(openOrderId.Value)).OrderStatus;
            if (orderStatus == OrderStatus.Filled || orderStatus == OrderStatus.Canceled)
            {
                waitingForFill = false;
                openOrderId = null;
            }
            return;
        }

        if (currentPosition <= 0)
            await PlaceBuy(client, account);
        else
            await PlaceSell(client);
    }

    /// <summary>
    /// Places a buy limit order if conditions are favorable and we are not already in a position
    /// </summary>
    /// <param name="client"></param>
    /// <param name="account"></param>
    private static async Task PlaceBuy(IAlpacaTradingClient client, IAccount account)
    {
        if (currentPrice <= buyPrice) return;

        decimal cash = account.TradableCash;
        var order = await client.PostOrderAsync(
            OrderSide.Buy.Limit(
                SYMBOL,
                OrderQuantity.Notional(cash * NOTIONAL_PERCENT),
                buyPrice)
            .WithDuration(TimeInForce.Gtc));

        openOrderId = order.OrderId;
        waitingForFill = true;

        Console.WriteLine($"BUY LIMIT placed at {buyPrice}");
    }

    /// <summary>
    /// Places a sell limit order if conditions are favorable and we currently hold a position
    /// </summary>
    /// <param name="client"></param>
    private static async Task PlaceSell(IAlpacaTradingClient client)
    {
        if (currentPrice >= sellPrice) return;

        var pos = await client.GetPositionAsync(SYMBOL_NS);
        var order = await client.PostOrderAsync(
            OrderSide.Sell.Limit(
                SYMBOL,
                OrderQuantity.Fractional(pos.AvailableQuantity),
                sellPrice)
            .WithDuration(TimeInForce.Gtc));

        openOrderId = order.OrderId;
        waitingForFill = true;

        Console.WriteLine($"SELL LIMIT placed at {sellPrice}");
    }
}