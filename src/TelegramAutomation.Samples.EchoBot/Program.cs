using TelegramAutomation.Abstractions;
using TelegramAutomation.Core;
using TelegramAutomation.Storage.InMemory;

var store = new InMemoryAutomationStore();
var builder = new TelegramAutomationBuilder()
    .Use(IdempotencyMiddleware.Create(store, TimeSpan.FromMinutes(10)))
    .UseCommand("/start", (ctx, _) => ValueTask.FromResult(AutomationResult.HandledResult("Welcome to EchoBot.")))
    .UseCommand("/help", (ctx, _) => ValueTask.FromResult(AutomationResult.HandledResult("Send text and I will echo it safely.")))
    .UseCallback("confirm:", (ctx, _) => ValueTask.FromResult(AutomationResult.HandledResult("Callback confirmed.")))
    .UseText((ctx, _) => ValueTask.FromResult(AutomationResult.HandledResult(TelegramMarkdown.EscapeMarkdownV2(ctx.Update.Text ?? string.Empty))))
    .UseFallback((_, _) => ValueTask.FromResult(AutomationResult.HandledResult("Unsupported update.")));
var processor = builder.Build();
var result = await processor.ProcessAsync(new TelegramUpdate(1, 123, 456, args.FirstOrDefault() ?? "/start"));
Console.WriteLine(result.Message);
