using TelegramAutomation.Abstractions;

namespace TelegramAutomation.Scheduling;

public sealed class LocalScheduler
{
    private readonly IScheduleStore _store; private readonly IClock _clock; private readonly ITelegramSender _sender;
    public LocalScheduler(IScheduleStore store, IClock clock, ITelegramSender sender) { _store = store; _clock = clock; _sender = sender; }
    public Task ScheduleAsync(string id, long chatId, string text, DateTimeOffset dueAt, TimeSpan? recurrence = null, CancellationToken cancellationToken = default) => _store.UpsertAsync(new ScheduledJob(id, chatId, text, dueAt, recurrence), cancellationToken);
    public Task CancelAsync(string id, CancellationToken cancellationToken = default) => _store.CancelAsync(id, cancellationToken);
    public Task<IReadOnlyList<ScheduledJob>> ListAsync(CancellationToken cancellationToken = default) => _store.ListAsync(cancellationToken);
    public async Task<int> ProcessDueAsync(CancellationToken cancellationToken = default)
    {
        var processed = 0;
        foreach (var job in await _store.DueAsync(_clock.UtcNow, cancellationToken))
        {
            var result = await _sender.SendMessageAsync(new SendMessageRequest(job.ChatId, job.Text, IdempotencyKey: $"schedule:{job.Id}:{job.DueAt:O}"), cancellationToken);
            if (!result.Ok) continue;
            processed++;
            if (job.Recurrence is { } recurrence) await _store.UpsertAsync(job with { DueAt = job.DueAt.Add(recurrence) }, cancellationToken); else await _store.CancelAsync(job.Id, cancellationToken);
        }
        return processed;
    }
}

public sealed class OutboxProcessor
{
    private readonly IOutboxStore _store; private readonly ITelegramSender _sender; private readonly IClock _clock;
    public OutboxProcessor(IOutboxStore store, ITelegramSender sender, IClock clock) { _store = store; _sender = sender; _clock = clock; }
    public async Task<int> ProcessPendingAsync(int take = 50, CancellationToken cancellationToken = default)
    {
        var processed = 0;
        foreach (var message in await _store.DueAsync(_clock.UtcNow, take, cancellationToken))
        {
            var result = await _sender.SendMessageAsync(new SendMessageRequest(message.ChatId, message.Text, IdempotencyKey: message.IdempotencyKey ?? message.Id), cancellationToken);
            var attempts = message.Attempts + 1;
            if (result.Ok) { await _store.MarkAsync(message.Id, "sent", attempts, null, null, cancellationToken); processed++; continue; }
            var status = attempts >= 5 ? "dead-letter" : "pending";
            await _store.MarkAsync(message.Id, status, attempts, result.Description, _clock.UtcNow.Add(TimeSpan.FromSeconds(Math.Pow(2, attempts))), cancellationToken);
        }
        return processed;
    }
}
