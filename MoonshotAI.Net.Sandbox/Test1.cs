namespace MoonshotAI.Net.Sandbox;

internal static class Test1
{
    public static async Task RunAsync(string key, CancellationToken cancellationToken = default)
    {
        var models = await Moonshot.ListModelIDsAsync(key, cancellationToken);
        Console.WriteLine("Models--------------------------------------------");
        foreach (var model in models)
            Console.WriteLine(model);
        Console.WriteLine("--------------------------------------------Models");

        var balance = await Moonshot.QueryBalanceAsync(key, cancellationToken);
        Console.WriteLine("Balance-------------------------------------------");
        Console.WriteLine($"Available: {balance.available_balance}");
        Console.WriteLine($"Voucher  : {balance.voucher_balance}");
        Console.WriteLine($"Cash     : {balance.cash_balance}");
        Console.WriteLine("-------------------------------------------Balance");

        Console.WriteLine("Token Count---------------------------------------");
        var testMessages = new Moonshot.Message[]
        {
            new("user", "Hello!"),
        };
        var tokenCount = await Moonshot.EstimateTokenCountAsync(key, models[^1], testMessages, cancellationToken);
        Console.WriteLine($"Count: {tokenCount}");
        Console.WriteLine("---------------------------------------Token Count");

        Console.WriteLine("Chat----------------------------------------------");
        var chatResponse = await Moonshot.ChatAsync(key, testMessages, models[^1], cancellationToken: cancellationToken);
        Console.WriteLine($"{testMessages[0].role}: {testMessages[0].content}");
        Console.WriteLine($"{chatResponse.role}: {chatResponse.content}");
        Console.WriteLine("----------------------------------------------Chat");
    }
}