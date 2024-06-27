# MoonshotAI.Net

## Description

* (.Net) Unofficial MoonshotAI LLM SDK | 非官方月之暗面大模型SDK
* Reference: [MoonshotAI Doc](https://platform.moonshot.cn/docs/intro)

## Example

```c#
var models = await Moonshot.ListModelIDsAsync(key, cancellationToken);
var session = new Moonshot.Session(key, models[^1]);
session.OnMessageAdded += message => Console.WriteLine($"{message.role}: {message.content}");

await session.ChatAsync("你好啊！", cancellationToken);
await session.ChatAsync("你最近怎么样？", cancellationToken);
await session.ChatAsync("可以给我讲个笑话吗？", cancellationToken);
...
```

## Notice

**All Permissions related to MoonshotAI LLM & API belong to MoonshotAI**

**所有与月之暗面大模型及其API相关的内容本身许可均属于月之暗面**

**This is a free & open source third-party SDK**

**这是个完全免费且开源的第三方SDK**

**The library itself is under WTFPL license**

**这个仓库本身基于WTFPL协议**