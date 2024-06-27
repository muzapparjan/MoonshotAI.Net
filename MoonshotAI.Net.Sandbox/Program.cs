using MoonshotAI.Net.Sandbox;

if (args.Length < 1)
    throw new ArgumentException("API key is required");
var key = args[0];
//var task = Test1.RunAsync(key);
var task = Test2.RunAsync(key);
task.Wait();