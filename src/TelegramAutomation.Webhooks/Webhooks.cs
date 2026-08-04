using System.Text.RegularExpressions;
using TelegramAutomation.Abstractions;
using TelegramAutomation.Core;

namespace TelegramAutomation.Webhooks;

public sealed record WebhookSecurityOptions(string SecretToken, long MaxPayloadBytes = 64 * 1024, int MaxCallbackDataBytes = 64, IReadOnlySet<string>? AllowedWebhookHosts = null)
{
    public IReadOnlySet<string> EffectiveAllowedWebhookHosts => AllowedWebhookHosts ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "localhost", "127.0.0.1" };
}

public sealed record WebhookValidationResult(bool Allowed, string? Reason = null)
{
    public static WebhookValidationResult Success { get; } = new(true);
    public static WebhookValidationResult Deny(string reason) => new(false, reason);
}

public sealed class WebhookRequestValidator
{
    private static readonly Regex PrivateAddress = new(@"^(10\.|172\.(1[6-9]|2\d|3[0-1])\.|192\.168\.)", RegexOptions.Compiled);
    private readonly WebhookSecurityOptions _options;
    public WebhookRequestValidator(WebhookSecurityOptions options) => _options = options;

    public WebhookValidationResult ValidateSecret(string? provided) =>
        FixedTimeEquals(provided ?? string.Empty, _options.SecretToken) ? WebhookValidationResult.Success : WebhookValidationResult.Deny("invalid-secret");

    public WebhookValidationResult ValidatePayload(long? contentLength) =>
        contentLength is not null && contentLength > _options.MaxPayloadBytes ? WebhookValidationResult.Deny("payload-too-large") : WebhookValidationResult.Success;

    public WebhookValidationResult ValidateUpdate(TelegramUpdate update)
    {
        if (update.CallbackData is { } data && System.Text.Encoding.UTF8.GetByteCount(data) > _options.MaxCallbackDataBytes) return WebhookValidationResult.Deny("callback-data-too-large");
        if (update.UpdateId < 0) return WebhookValidationResult.Deny("malformed-update");
        return WebhookValidationResult.Success;
    }

    public WebhookValidationResult ValidateWebhookUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return WebhookValidationResult.Deny("invalid-url");
        if (uri.Scheme != Uri.UriSchemeHttps && uri.Host is not "localhost" and not "127.0.0.1") return WebhookValidationResult.Deny("https-required");
        if (!_options.EffectiveAllowedWebhookHosts.Contains(uri.Host)) return WebhookValidationResult.Deny("host-not-allowed");
        if (PrivateAddress.IsMatch(uri.Host)) return WebhookValidationResult.Deny("private-host-blocked");
        return WebhookValidationResult.Success;
    }

    private static bool FixedTimeEquals(string left, string right)
    {
        var leftBytes = System.Text.Encoding.UTF8.GetBytes(left);
        var rightBytes = System.Text.Encoding.UTF8.GetBytes(right);
        return System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(leftBytes, rightBytes);
    }

}

public sealed record WebhookProcessResult(int StatusCode, string Body, bool Processed);

public sealed class WebhookUpdateProcessor
{
    private readonly WebhookRequestValidator _validator;
    private readonly IIdempotencyStore _idempotencyStore;
    private readonly ITelegramUpdateProcessor _processor;

    public WebhookUpdateProcessor(WebhookRequestValidator validator, IIdempotencyStore idempotencyStore, ITelegramUpdateProcessor processor)
    {
        _validator = validator;
        _idempotencyStore = idempotencyStore;
        _processor = processor;
    }

    public async Task<WebhookProcessResult> ProcessAsync(string? secret, long? contentLength, TelegramUpdate update, CancellationToken cancellationToken = default)
    {
        var secretResult = _validator.ValidateSecret(secret);
        if (!secretResult.Allowed) return new WebhookProcessResult(401, secretResult.Reason!, false);
        var payloadResult = _validator.ValidatePayload(contentLength);
        if (!payloadResult.Allowed) return new WebhookProcessResult(413, payloadResult.Reason!, false);
        var updateResult = _validator.ValidateUpdate(update);
        if (!updateResult.Allowed) return new WebhookProcessResult(400, updateResult.Reason!, false);
        if (!await _idempotencyStore.TryBeginAsync($"webhook:{update.UpdateId}", TimeSpan.FromHours(24), cancellationToken)) return new WebhookProcessResult(200, "duplicate", false);
        var result = await _processor.ProcessAsync(update, cancellationToken);
        return new WebhookProcessResult(200, result.Handled ? "ok" : "ignored", true);
    }
}


