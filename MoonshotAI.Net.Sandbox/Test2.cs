namespace MoonshotAI.Net.Sandbox;

internal static class Test2
{
    public static async Task RunAsync(string key, CancellationToken cancellationToken = default)
    {
        var models = await Moonshot.ListModelIDsAsync(key, cancellationToken);
        var session = new Moonshot.Session(key, models[^1]);
        session.OnMessageAdded += message => Console.WriteLine($"{message.role}: {message.content}");

        await session.ChatAsync("你好啊！", cancellationToken);
        await session.ChatAsync("你最近怎么样？", cancellationToken);
        await session.ChatAsync("可以给我讲个笑话吗？", cancellationToken);
        await session.ChatAsync("这个笑话不好笑！", cancellationToken);
        await session.ChatAsync("哈哈哈！", cancellationToken);
    }
}