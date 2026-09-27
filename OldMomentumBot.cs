using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

using Alpaca.Markets;

namespace CryptoTradingBots;

/// <summary>
/// A momentum-based trading bot for ETH/USD using Alpaca's API.
/// </summary>
internal static class OldMomentumBot
{
    private const string SYMBOL = "ETH/USD";
    private const string SYMBOL_NS = "ETHUSD";

    private const int EMA_FAST_PERIOD = 9;
    private const int EMA_SLOW_PERIOD = 21;
    private const int RSI_PERIOD = 14;

    private const decimal NOTIONAL_PERCENT  = 0.99m; // 99%
    private const decimal STOP_LOSS_PERCENT = 0.01m; // 1%
    private const decimal ROUND_TRIP_FEE = 0.005m;   // 0.5%

    private static decimal emaFast;
    private static decimal emaSlow;
    private static decimal rsi;

    private static decimal previousClose;
    private static decimal currentClose;

    private static decimal currentPosition;
    private static decimal entryPrice;
    private static decimal stopLoss;

    private static readonly Queue<decimal> closes = new();
    private static readonly Queue<decimal> gains = new();
    private static readonly Queue<decimal> losses = new();

    private static int barsSinceLastTrade = 100;
    private const int MIN_BARS_BETWEEN_TRADES = 3;

    /// <summary>
    /// Main entry point for the bot. Connects to Alpaca, initializes indicators, and starts 
    /// streaming minute bars for ETH/USD.
    /// </summary>
    public static async Task RunAsync()
    {
        var dataClient = Environments.Live.GetAlpacaCryptoDataClient(
            new SecretKey(Secrets.KEY_ID, Secrets.SECRET_KEY));
        var tradingClient = Environments.Paper.GetAlpacaTradingClient(
            new SecretKey(Secrets.KEY_ID, Secrets.SECRET_KEY));
        var streamClient = Environments.Live.GetAlpacaCryptoStreamingClient(
            new SecretKey(Secrets.KEY_ID, Secrets.SECRET_KEY));

        await streamClient.ConnectAndAuthenticateAsync();

        Console.WriteLine($"Momentum bot running for {SYMBOL}");
        await Initialize(dataClient);

        var sub = streamClient.GetMinuteBarSubscription(SYMBOL);
        sub.Received += async bar =>
        {
            Console.WriteLine(
                $"\n{bar.TimeUtc} | Open: ${bar.Open} | High: ${bar.High} | Low: ${bar.Low} | " + 
                $"Close: ${bar.Close}");

            previousClose = currentClose;
            currentClose = bar.Close;

            UpdateIndicators(currentClose);
            await UpdatePosition(tradingClient);
            PrintStatistics();

            barsSinceLastTrade++;

            await HandleTrading(tradingClient);
        };

        await streamClient.SubscribeAsync(sub);

        Console.WriteLine($"Waiting for new {SYMBOL} bars...");
        await Task.Delay(-1);
    }

    /// <summary>
    /// Initializes the bot by fetching historical minute bars for ETH/USD and calculating the 
    /// initial EMA and RSI values. This ensures the bot starts with accurate indicator readings 
    /// before processing live data.
    /// </summary>
    /// <param name="client"></param>
    /// <returns></returns>
    private static async Task Initialize(IAlpacaCryptoDataClient client)
    {
        var end = DateTime.UtcNow;
        var start = end.AddMinutes(-100);

        var bars = (await client.ListHistoricalBarsAsync(
            new HistoricalCryptoBarsRequest(SYMBOL, start, end, BarTimeFrame.Minute))).Items;

        foreach (var bar in bars)
        {
            previousClose = currentClose;
            currentClose = bar.Close;
            UpdateIndicators(currentClose);
        }

        Console.WriteLine("Initialized indicators");
    }

    private static void UpdateIndicators(decimal close)
    {
        closes.Enqueue(close);
        if (closes.Count > 100) closes.Dequeue();

        // Exponential moving average
        if (emaFast == 0)
        {
            emaFast = close;
            emaSlow = close;
        }
        else
        {
            decimal kFast = 2m / (EMA_FAST_PERIOD + 1);
            decimal kSlow = 2m / (EMA_SLOW_PERIOD + 1);

            emaFast = close * kFast + emaFast * (1 - kFast);
            emaSlow = close * kSlow + emaSlow * (1 - kSlow);
        }

        // Relative Strength Index 
        if (previousClose != 0)
        {
            decimal change = close - previousClose;

            gains.Enqueue(Math.Max(change, 0));
            losses.Enqueue(Math.Max(-change, 0));

            if (gains.Count > RSI_PERIOD)
            {
                gains.Dequeue();
                losses.Dequeue();
            }

            decimal avgGain = gains.Any() ? gains.Average() : 0;
            decimal avgLoss = losses.Any() ? losses.Average() : 0;

            rsi = avgLoss == 0 ? 100 : 100 - (100 / (1 + (avgGain / avgLoss)));
        }
    }

    private static async Task UpdatePosition(IAlpacaTradingClient client)
    {
        try
        {
            var pos = await client.GetPositionAsync(SYMBOL_NS);
            currentPosition = pos.Quantity;
            entryPrice = pos.AverageEntryPrice;
            stopLoss = entryPrice * (1 - STOP_LOSS_PERCENT);
            Console.WriteLine($"* Position: {currentPosition} @ {entryPrice:F2}");
        }
        catch
        {
            currentPosition = 0;
            entryPrice = 0;
            stopLoss = 0;
            Console.WriteLine("* No position");
        }
    }

    private static async Task HandleTrading(IAlpacaTradingClient client)
    {
        if (currentPosition == 0 && barsSinceLastTrade >= MIN_BARS_BETWEEN_TRADES)
        {
            // ================= ENTRY =================
            bool trendUp = emaFast > emaSlow;
            bool pullback = previousClose < emaFast;
            bool bounce = currentClose > previousClose;
            bool momentum = rsi > 50;

            decimal trendStrength = (emaFast - emaSlow) / currentClose;
            decimal minMoveRequired = ROUND_TRIP_FEE * 1.5m;

            bool trendStrongEnough = trendStrength > minMoveRequired;

            if (trendUp && pullback && bounce && momentum && trendStrongEnough)
            {
                Console.WriteLine("* BUY SIGNAL");

                var account = await client.GetAccountAsync();

                decimal investAmount = Math.Round(account.TradableCash * NOTIONAL_PERCENT, 2);

                var order = await client.PostOrderAsync(
                    OrderSide.Buy.Market(
                        SYMBOL,
                        OrderQuantity.Notional(investAmount)
                    ).WithDuration(TimeInForce.Ioc)
                );

                entryPrice = order.AverageFillPrice ?? currentClose;
                stopLoss = entryPrice * (1 - STOP_LOSS_PERCENT);
                barsSinceLastTrade = 0;

                decimal quantity = order.Quantity ?? Math.Round(investAmount / entryPrice, 6);
                Console.WriteLine($"* BOUGHT {quantity} @ {entryPrice} | Stop: {stopLoss}");
            }
            else
            {
                Console.WriteLine("* Buy conditions not met:");
                Console.WriteLine(
                    $"* TrendUp (EMA9 > EMA21): {trendUp} | " +
                    $"Pullback (previousClose < EMA9): {pullback} | " +
                    $"Bounce (currentClose > previousClose): {bounce} | " +
                    $"Momentum (RSI > 50): {momentum} | " +
                    $"TrendStrength ({trendStrength:P2} > {minMoveRequired:P2}): {trendStrongEnough}"
                );
            }
        }
        else if (currentPosition > 0)
        {
            // ================= EXIT =================
            bool trendBroken = emaFast < emaSlow;
            bool momentumLost = rsi < 50;
            bool stopHit = currentClose <= stopLoss;

            if (trendBroken || momentumLost || stopHit)
            {
                Console.WriteLine("* SELL SIGNAL");

                var pos = await client.GetPositionAsync(SYMBOL_NS);

                await client.PostOrderAsync(
                    OrderSide.Sell.Market(
                        SYMBOL,
                        OrderQuantity.Fractional(pos.AvailableQuantity)
                    ).WithDuration(TimeInForce.Ioc)
                );

                decimal grossReturn = (currentClose / entryPrice) - 1;
                decimal netReturn = grossReturn - ROUND_TRIP_FEE;
                decimal grossProfit = (currentClose - entryPrice) * pos.Quantity;
                decimal netProfit = netReturn * entryPrice * pos.Quantity;
                Console.WriteLine(
                    $"* SOLD @ {currentClose} | " + 
                    $"Gross Profit: ${grossProfit:F2} | " + 
                    $"Net Profit: ${netProfit:F2} | " + 
                    $"Gross Return: {grossReturn * 100:F2}% | " +
                    $"Net Return: {netReturn * 100:F2}%"
                );

                entryPrice = 0;
                stopLoss = 0;
                barsSinceLastTrade = 0;
            }
            else
            {
                Console.WriteLine("* Sell conditions not met:");
            }

            Console.WriteLine(
                $"* TrendBroken (EMA9 < EMA21): {trendBroken} | " +
                $"MomentumLost (RSI < 50): {momentumLost} | " +
                $"StopHit (currentClose <= stopLoss): {stopHit}"
            );
        }
    }

    private static void PrintStatistics()
    {
        Console.WriteLine(
            $"* EMA9: {emaFast:F2} | EMA21: {emaSlow:F2} | RSI: {rsi:F2} | "+ 
            $"Stop Loss: {(stopLoss > 0 ? stopLoss.ToString("F2") : "N/A")}"
        );
    }
}