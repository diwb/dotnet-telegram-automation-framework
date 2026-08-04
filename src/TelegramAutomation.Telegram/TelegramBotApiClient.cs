using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using TelegramAutomation.Abstractions;
using TelegramAutomation.Core;

namespace TelegramAutomation.Telegram;

public sealed class TelegramBotApiClient : ITelegramSender
{
    private readonly HttpClient _httpClient; private readonly string _botToken;
    public TelegramBotApiClient(HttpClient httpClient, string botToken) { _httpClient = httpClient; _botToken = botToken; }
    public Task<SendMessageResult> SendMessageAsync(SendMessageRequest request, CancellationToken cancellationToken = default)
    {
        AutomationTelemetry.TelegramApiCalls.Add(1);
        var payload = new { chat_id = request.ChatId, text = request.Text, parse_mode = request.ParseMode, reply_markup = request.InlineKeyboard is null ? null : new { inline_keyboard = request.InlineKeyboard } };
        return PostAsync("sendMessage", payload, cancellationToken);
    }
    public Task<SendMessageResult> EditMessageTextAsync(long chatId, long messageId, string text, CancellationToken cancellationToken = default) { AutomationTelemetry.TelegramApiCalls.Add(1); return PostAsync("editMessageText", new { chat_id = chatId, message_id = messageId, text }, cancellationToken); }
    public Task<SendMessageResult> AnswerCallbackQueryAsync(string callbackQueryId, string? text = null, CancellationToken cancellationToken = default) { AutomationTelemetry.TelegramApiCalls.Add(1); return PostAsync("answerCallbackQuery", new { callback_query_id = callbackQueryId, text }, cancellationToken); }
    public async Task<TelegramBotIdentity> GetMeAsync(CancellationToken cancellationToken = default)
    {
        var response = await _httpClient.GetFromJsonAsync<TelegramEnvelope<TelegramBotIdentity>>(Endpoint("getMe"), cancellationToken);
        return response?.Result ?? throw new InvalidOperationException("Telegram getMe returned an empty response.");
    }
    private async Task<SendMessageResult> PostAsync(string method, object payload, CancellationToken cancellationToken)
    {
        using var response = await _httpClient.PostAsJsonAsync(Endpoint(method), payload, cancellationToken);
        if (response.StatusCode == HttpStatusCode.TooManyRequests) AutomationTelemetry.Retries.Add(1);
        using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        var ok = document.RootElement.TryGetProperty("ok", out var okElement) && okElement.GetBoolean();
        var description = document.RootElement.TryGetProperty("description", out var descriptionElement) ? Redactor.Redact(descriptionElement.GetString() ?? string.Empty) : null;
        long? messageId = null;
        if (document.RootElement.TryGetProperty("result", out var result) && result.ValueKind == JsonValueKind.Object && result.TryGetProperty("message_id", out var messageElement)) messageId = messageElement.GetInt64();
        return new SendMessageResult(ok, messageId, response.StatusCode.ToString(), description);
    }
    private string Endpoint(string method) => $"bot{_botToken}/{method}";
    private sealed record TelegramEnvelope<T>(bool Ok, T Result);
}
public sealed record TelegramBotIdentity(long Id, bool IsBot, string FirstName, string Username);
