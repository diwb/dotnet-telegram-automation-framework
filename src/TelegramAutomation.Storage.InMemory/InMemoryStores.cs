using System.Collections.Concurrent;
using TelegramAutomation.Abstractions;

namespace TelegramAutomation.Storage.InMemory;

public sealed class InMemoryAutomationStore : IIdempotencyStore, IConversationStateStore, IOutboxStore, IScheduleStore, IRateLimitStore, ICheckpointStore
{
    private readonly IClock _clock;
    private readonly ConcurrentDictionary<string, DateTimeOffset> _idempotency = new();
    private readonly ConcurrentDictionary<string, ConversationState> _conversations = new();
    private readonly ConcurrentDictionary<string, OutboxMessage> _outbox = new();
    private readonly ConcurrentDictionary<string, ScheduledJob> _jobs = new();
    private readonly ConcurrentDictionary<string, Queue<DateTimeOffset>> _rate = new();
    private readonly ConcurrentDictionary<string, long> _checkpoints = new();
    public InMemoryAutomationStore(IClock? clock = null) => _clock = clock ?? new SystemClock();
    public Task<bool> TryBeginAsync(string key, TimeSpan ttl, CancellationToken cancellationToken = default) { CleanupIdempotency(); return Task.FromResult(_idempotency.TryAdd(key, _clock.UtcNow.Add(ttl))); }
    public Task<ConversationState?> GetAsync(long chatId, long userId, CancellationToken cancellationToken = default) { var key = $"{chatId}:{userId}"; if (!_conversations.TryGetValue(key, out var state) || state.ExpiresAt <= _clock.UtcNow) { _conversations.TryRemove(key, out _); return Task.FromResult<ConversationState?>(null); } return Task.FromResult<ConversationState?>(state); }
    public Task SetAsync(ConversationState state, CancellationToken cancellationToken = default) { _conversations[$"{state.ChatId}:{state.UserId}"] = state; return Task.CompletedTask; }
    public Task ClearAsync(long chatId, long userId, CancellationToken cancellationToken = default) { _conversations.TryRemove($"{chatId}:{userId}", out _); return Task.CompletedTask; }
    public Task EnqueueAsync(OutboxMessage message, CancellationToken cancellationToken = default) { _outbox[message.Id] = message; return Task.CompletedTask; }
    public Task<IReadOnlyList<OutboxMessage>> DueAsync(DateTimeOffset now, int take, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<OutboxMessage>>(_outbox.Values.Where(x => x.Status == "pending" && x.DueAt <= now).OrderBy(x => x.DueAt).Take(take).ToArray());
    public Task MarkAsync(string id, string status, int attempts, string? lastError, DateTimeOffset? nextDueAt, CancellationToken cancellationToken = default) { if (_outbox.TryGetValue(id, out var existing)) _outbox[id] = existing with { Status = status, Attempts = attempts, LastError = lastError, DueAt = nextDueAt ?? existing.DueAt }; return Task.CompletedTask; }
    Task<IReadOnlyList<OutboxMessage>> IOutboxStore.ListAsync(CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<OutboxMessage>>(_outbox.Values.OrderBy(x => x.DueAt).ToArray());
    public Task UpsertAsync(ScheduledJob job, CancellationToken cancellationToken = default) { _jobs[job.Id] = job; return Task.CompletedTask; }
    public Task<IReadOnlyList<ScheduledJob>> DueAsync(DateTimeOffset now, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<ScheduledJob>>(_jobs.Values.Where(x => !x.Cancelled && x.DueAt <= now).OrderBy(x => x.DueAt).ToArray());
    Task<IReadOnlyList<ScheduledJob>> IScheduleStore.ListAsync(CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<ScheduledJob>>(_jobs.Values.OrderBy(x => x.DueAt).ToArray());
    public Task CancelAsync(string id, CancellationToken cancellationToken = default) { if (_jobs.TryGetValue(id, out var existing)) _jobs[id] = existing with { Cancelled = true }; return Task.CompletedTask; }
    public Task<RateLimitDecision> CheckAsync(string key, int limit, TimeSpan window, CancellationToken cancellationToken = default)
    {
        var queue = _rate.GetOrAdd(key, _ => new Queue<DateTimeOffset>());
        lock (queue)
        {
            while (queue.Count > 0 && queue.Peek() <= _clock.UtcNow.Subtract(window)) queue.Dequeue();
            if (queue.Count >= limit) return Task.FromResult(new RateLimitDecision(false, queue.Peek().Add(window) - _clock.UtcNow));
            queue.Enqueue(_clock.UtcNow); return Task.FromResult(RateLimitDecision.AllowedNow);
        }
    }
    public Task<long?> GetOffsetAsync(string consumer, CancellationToken cancellationToken = default) =>
        Task.FromResult(_checkpoints.TryGetValue(consumer, out var offset) ? offset : (long?)null);

    public Task SaveOffsetAsync(string consumer, long offset, CancellationToken cancellationToken = default)
    {
        _checkpoints[consumer] = offset;
        return Task.CompletedTask;
    }
    private void CleanupIdempotency() { foreach (var pair in _idempotency.Where(pair => pair.Value <= _clock.UtcNow)) _idempotency.TryRemove(pair.Key, out _); }
}


