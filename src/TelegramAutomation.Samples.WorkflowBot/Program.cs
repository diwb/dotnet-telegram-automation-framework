using TelegramAutomation.Abstractions;
using TelegramAutomation.Scheduling;
using TelegramAutomation.Storage.InMemory;

var clock = new FakeClock(DateTimeOffset.Parse("2026-08-03T12:00:00Z"));
var store = new InMemoryAutomationStore(clock);
var sender = new ConsoleSender();
await store.SetAsync(new ConversationState(123, 456, "collecting-name", "{}", clock.UtcNow.AddMinutes(30)));
await store.EnqueueAsync(new OutboxMessage("welcome-1", 123, "Welcome from WorkflowBot", "pending", 0, clock.UtcNow));
var outbox = new OutboxProcessor(store, sender, clock);
await outbox.ProcessPendingAsync();
var scheduler = new LocalScheduler(store, clock, sender);
await scheduler.ScheduleAsync("reminder-1", 123, "Scheduled reminder", clock.UtcNow);
await scheduler.ProcessDueAsync();

sealed class ConsoleSender : ITelegramSender
{
    public Task<SendMessageResult> SendMessageAsync(SendMessageRequest request, CancellationToken cancellationToken = default) { Console.WriteLine(request.Text); return Task.FromResult(new SendMessageResult(true, 1)); }
    public Task<SendMessageResult> EditMessageTextAsync(long chatId, long messageId, string text, CancellationToken cancellationToken = default) => Task.FromResult(new SendMessageResult(true, messageId));
    public Task<SendMessageResult> AnswerCallbackQueryAsync(string callbackQueryId, string? text = null, CancellationToken cancellationToken = default) => Task.FromResult(new SendMessageResult(true));
}
