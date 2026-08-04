using System.Collections.Concurrent;

namespace TelegramAutomation.Abstractions;

public enum TelegramUpdateKind { Unknown, Command, Text, CallbackQuery, Photo, Document }

public sealed record TelegramUpdate(long UpdateId, long ChatId, long UserId, string? Text = null, string? CallbackData = null, string? CallbackQueryId = null, string ChatType = "private", DateTimeOffset? ReceivedAt = null)
{
    public TelegramUpdateKind Kind
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(CallbackData)) return TelegramUpdateKind.CallbackQuery;
            if (!string.IsNullOrWhiteSpace(Text) && Text.StartsWith("/", StringComparison.Ordinal)) return TelegramUpdateKind.Command;
            if (!string.IsNullOrWhiteSpace(Text)) return TelegramUpdateKind.Text;
            return TelegramUpdateKind.Unknown;
        }
    }
}

public sealed class TelegramAutomationContext
{
    public TelegramAutomationContext(TelegramUpdate update, IServiceProvider? services = null) { Update = update; Services = services; }
    public TelegramUpdate Update { get; }
    public IServiceProvider? Services { get; }
    public string CorrelationId { get; set; } = Guid.NewGuid().ToString("N");
    public ConcurrentDictionary<string, object?> Items { get; } = new();
}

public sealed record AutomationResult(bool Handled, string? Message = null)
{
    public static AutomationResult NotHandled { get; } = new(false);
    public static AutomationResult HandledResult(string? message = null) => new(true, message);
}

public delegate ValueTask<AutomationResult> TelegramUpdateDelegate(TelegramAutomationContext context, CancellationToken cancellationToken);

public interface ITelegramUpdateProcessor { ValueTask<AutomationResult> ProcessAsync(TelegramUpdate update, CancellationToken cancellationToken = default); }

public sealed record InlineKeyboardButton(string Text, string CallbackData);
public sealed record SendMessageRequest(long ChatId, string Text, IReadOnlyList<IReadOnlyList<InlineKeyboardButton>>? InlineKeyboard = null, string? ParseMode = null, string? IdempotencyKey = null);
public sealed record SendMessageResult(bool Ok, long? MessageId = null, string? ErrorCode = null, string? Description = null);

public interface ITelegramSender
{
    Task<SendMessageResult> SendMessageAsync(SendMessageRequest request, CancellationToken cancellationToken = default);
    Task<SendMessageResult> EditMessageTextAsync(long chatId, long messageId, string text, CancellationToken cancellationToken = default);
    Task<SendMessageResult> AnswerCallbackQueryAsync(string callbackQueryId, string? text = null, CancellationToken cancellationToken = default);
}

public interface IClock { DateTimeOffset UtcNow { get; } }
public sealed class SystemClock : IClock { public DateTimeOffset UtcNow => DateTimeOffset.UtcNow; }
public sealed class FakeClock : IClock { public FakeClock(DateTimeOffset initial) => UtcNow = initial; public DateTimeOffset UtcNow { get; private set; } public void Advance(TimeSpan value) => UtcNow = UtcNow.Add(value); }

public sealed record ConversationState(long ChatId, long UserId, string State, string DataJson, DateTimeOffset ExpiresAt);
public interface IConversationStateStore { Task<ConversationState?> GetAsync(long chatId, long userId, CancellationToken cancellationToken = default); Task SetAsync(ConversationState state, CancellationToken cancellationToken = default); Task ClearAsync(long chatId, long userId, CancellationToken cancellationToken = default); }
public interface IIdempotencyStore { Task<bool> TryBeginAsync(string key, TimeSpan ttl, CancellationToken cancellationToken = default); }
public sealed record OutboxMessage(string Id, long ChatId, string Text, string Status, int Attempts, DateTimeOffset DueAt, string? IdempotencyKey = null, string? LastError = null);
public interface IOutboxStore { Task EnqueueAsync(OutboxMessage message, CancellationToken cancellationToken = default); Task<IReadOnlyList<OutboxMessage>> DueAsync(DateTimeOffset now, int take, CancellationToken cancellationToken = default); Task MarkAsync(string id, string status, int attempts, string? lastError, DateTimeOffset? nextDueAt, CancellationToken cancellationToken = default); Task<IReadOnlyList<OutboxMessage>> ListAsync(CancellationToken cancellationToken = default); }
public sealed record ScheduledJob(string Id, long ChatId, string Text, DateTimeOffset DueAt, TimeSpan? Recurrence, bool Cancelled = false);
public interface IScheduleStore { Task UpsertAsync(ScheduledJob job, CancellationToken cancellationToken = default); Task<IReadOnlyList<ScheduledJob>> DueAsync(DateTimeOffset now, CancellationToken cancellationToken = default); Task<IReadOnlyList<ScheduledJob>> ListAsync(CancellationToken cancellationToken = default); Task CancelAsync(string id, CancellationToken cancellationToken = default); }
public sealed record RateLimitDecision(bool Allowed, TimeSpan RetryAfter) { public static RateLimitDecision AllowedNow { get; } = new(true, TimeSpan.Zero); }
public interface IRateLimitStore { Task<RateLimitDecision> CheckAsync(string key, int limit, TimeSpan window, CancellationToken cancellationToken = default); }

public interface ICheckpointStore
{
    Task<long?> GetOffsetAsync(string consumer, CancellationToken cancellationToken = default);
    Task SaveOffsetAsync(string consumer, long offset, CancellationToken cancellationToken = default);
}

public sealed record TelegramPollingBatch(IReadOnlyList<TelegramUpdate> Updates, TimeSpan? RetryAfter = null);

public interface ITelegramUpdateSource
{
    Task<TelegramPollingBatch> GetUpdatesAsync(long? offset, int limit, IReadOnlyList<string> allowedUpdates, CancellationToken cancellationToken = default);
}
