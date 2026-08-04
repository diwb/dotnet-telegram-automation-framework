using System.Collections.Concurrent;
using System.Text.Json;
using TelegramAutomation.Abstractions;

var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();
var state = FakeTelegramState.Instance;

app.MapGet("/health", () => Results.Ok(new { status = "healthy" }));
app.MapGet("/ready", () => Results.Ok(new { status = "ready" }));
app.MapGet("/bot{token}/getMe", (string token) => Results.Ok(new { ok = true, result = new { id = 700000001, is_bot = true, first_name = "Fake Automation Bot", username = "fake_automation_bot" } }));
app.MapPost("/bot{token}/getUpdates", (string token) => state.ConsumeFaultOr(() => Results.Ok(new { ok = true, result = state.DrainUpdates() })));
app.MapPost("/bot{token}/setWebhook", async (HttpRequest request, string token) =>
{
    var body = await JsonSerializer.DeserializeAsync<JsonElement>(request.Body);
    state.WebhookUrl = body.TryGetProperty("url", out var url) ? url.GetString() : null;
    state.WebhookSecret = body.TryGetProperty("secret_token", out var secret) ? secret.GetString() : state.WebhookSecret;
    return Results.Ok(new { ok = true, result = true, description = "Webhook was set" });
});
app.MapPost("/bot{token}/deleteWebhook", (string token) => { state.WebhookUrl = null; return Results.Ok(new { ok = true, result = true }); });
app.MapPost("/bot{token}/sendMessage", async (HttpRequest request, string token) =>
{
    var fault = state.ConsumeFault();
    if (fault is not null) return fault;
    var body = await JsonSerializer.DeserializeAsync<JsonElement>(request.Body);
    var chatId = body.GetProperty("chat_id").GetInt64();
    var text = body.GetProperty("text").GetString() ?? string.Empty;
    var keyboard = body.TryGetProperty("reply_markup", out var markup) ? markup.GetRawText() : null;
    var message = state.RecordMessage(chatId, text, keyboard);
    return Results.Ok(new { ok = true, result = new { message_id = message.MessageId, chat = new { id = chatId }, text, reply_markup = keyboard } });
});
app.MapPost("/bot{token}/editMessageText", () => state.ConsumeFaultOr(() => Results.Ok(new { ok = true, result = true })));
app.MapPost("/bot{token}/answerCallbackQuery", () => state.ConsumeFaultOr(() => Results.Ok(new { ok = true, result = true })));
app.MapPost("/bot{token}/getFile", () => Results.Ok(new { ok = true, result = new { file_id = "fake-file", file_unique_id = "fake-unique", file_size = 10, file_path = "documents/fake.txt" } }));
app.MapPost("/admin/enqueue-update", (TelegramUpdate update) => { state.Enqueue(update); return Results.Accepted(value: new { ok = true, queued = state.PendingUpdates }); });
app.MapPost("/admin/enqueue-malformed", () => { state.Enqueue(new TelegramUpdate(-1, 0, 0)); return Results.Accepted(value: new { ok = true }); });
app.MapPost("/admin/fault/rate-limit", (int? retryAfter) => { state.NextFault = Results.Json(new { ok = false, error_code = 429, description = "Too Many Requests", parameters = new { retry_after = retryAfter ?? 1 } }, statusCode: 429); return Results.Ok(new { ok = true }); });
app.MapPost("/admin/fault/retryable", () => { state.NextFault = Results.Json(new { ok = false, error_code = 500, description = "Internal Server Error" }, statusCode: 500); return Results.Ok(new { ok = true }); });
app.MapPost("/admin/fault/non-retryable", () => { state.NextFault = Results.Json(new { ok = false, error_code = 400, description = "Bad Request" }, statusCode: 400); return Results.Ok(new { ok = true }); });
app.MapPost("/admin/deliver-webhook", () => Results.Ok(new { ok = state.WebhookUrl is not null, webhook = state.WebhookUrl, delivered = state.WebhookUrl is not null ? state.PendingUpdates : 0 }));
app.MapPost("/admin/reset", () => { state.Reset(); return Results.Ok(new { ok = true }); });
app.MapGet("/admin/updates", () => Results.Ok(state.PeekUpdates()));
app.MapGet("/admin/messages", () => Results.Ok(state.Messages.OrderBy(x => x.MessageId)));

app.Run();

public sealed record FakeTelegramMessage(long MessageId, long ChatId, string Text, string? InlineKeyboardJson, DateTimeOffset SentAt);
public sealed class FakeTelegramState
{
    public static FakeTelegramState Instance { get; } = new();
    private readonly ConcurrentQueue<TelegramUpdate> _updates = new();
    private long _messageId = 1000;
    public string? WebhookUrl { get; set; }
    public string? WebhookSecret { get; set; }
    public IResult? NextFault { get; set; }
    public ConcurrentBag<FakeTelegramMessage> Messages { get; } = [];
    public int PendingUpdates => _updates.Count;
    public void Enqueue(TelegramUpdate update) => _updates.Enqueue(update);
    public IReadOnlyList<TelegramUpdate> PeekUpdates() => _updates.ToArray();
    public IReadOnlyList<TelegramUpdate> DrainUpdates() { var list = new List<TelegramUpdate>(); while (_updates.TryDequeue(out var update)) list.Add(update); return list; }
    public FakeTelegramMessage RecordMessage(long chatId, string text, string? inlineKeyboardJson = null) { var message = new FakeTelegramMessage(Interlocked.Increment(ref _messageId), chatId, text, inlineKeyboardJson, DateTimeOffset.UtcNow); Messages.Add(message); return message; }
    public IResult? ConsumeFault() { var fault = NextFault; NextFault = null; return fault; }
    public IResult ConsumeFaultOr(Func<IResult> next) => ConsumeFault() ?? next();
    public void Reset() { while (_updates.TryDequeue(out _)) { } while (Messages.TryTake(out _)) { } WebhookUrl = null; WebhookSecret = null; NextFault = null; }
}

public partial class Program { }
