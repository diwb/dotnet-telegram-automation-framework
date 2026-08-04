using TelegramAutomation.Abstractions;
using TelegramAutomation.Core;
using TelegramAutomation.Storage.InMemory;

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.ConfigureKestrel(options => options.Limits.MaxRequestBodySize = 64 * 1024);
var store = new InMemoryAutomationStore();
var processor = new TelegramAutomationBuilder()
    .Use(IdempotencyMiddleware.Create(store, TimeSpan.FromMinutes(10)))
    .UseCommand("/start", (ctx, _) => ValueTask.FromResult(AutomationResult.HandledResult("Webhook host ready.")))
    .UseText((ctx, _) => ValueTask.FromResult(AutomationResult.HandledResult("queued")))
    .UseFallback((_, _) => ValueTask.FromResult(AutomationResult.HandledResult("ignored")))
    .Build();
var app = builder.Build();
app.MapGet("/health", () => Results.Ok(new { status = "healthy" }));
app.MapGet("/ready", () => Results.Ok(new { status = "ready" }));
app.MapPost("/telegram/webhook", async (HttpRequest request, TelegramUpdate update) =>
{
    var expected = app.Configuration["Telegram:WebhookSecret"] ?? "local-secret-token-123";
    if (!request.Headers.TryGetValue("X-Telegram-Bot-Api-Secret-Token", out var actual) || actual != expected) return Results.Unauthorized();
    var result = await processor.ProcessAsync(update);
    return Results.Ok(new { ok = result.Handled });
});
app.Run();
public partial class Program { }
