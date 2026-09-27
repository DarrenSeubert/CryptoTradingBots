using System;
using System.Threading.Tasks;

namespace CryptoTradingBots;

class Program
{
    static async Task Main(string[] args)
    {
        try
        {
            await MomentumBot.RunAsync();
            // await ScalpingBot.RunAsync();
        }
        catch (Exception e)
        {
            Console.Error.WriteLine(e);
        }
    }
}