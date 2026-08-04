using System.Net.Http.Json;
using TelegramAutomation.Abstractions;
using TelegramAutomation.Core;

namespace TelegramAutomation.Polling;

public sealed record PollingOptions(string ConsumerName = "telegram-polling", int Limit = 100, TimeSpan EmptyDelay = default, TimeSpan ErrorBackoff = default, IReadOnlyList<string>? AllowedUpdates = null)
{
    public TimeSpan EffectiveEmptyDelay => EmptyDelay == default ? TimeSpan.FromMilliseconds(250) : EmptyDelay;
    public TimeSpan EffectiveErrorBackoff => ErrorBackoff == default ? TimeSpan.FromSeconds(1) : ErrorBackoff;
    public IReadOnlyList<string> EffectiveAllowedUpdates => AllowedUpdates ?? ["message", "callback_query"];
}

public sealed record PollingRunResult(int Received, int Processed, int Duplicates, long? SavedOffset, TimeSpan? Backoff);

public sealed class LongPollingRunner
{
    private readonly ITelegramUpdateSource _source;
    private readonly ITelegramUpdateProcessor _processor;
    private readonly ICheckpointStore _checkpointStore;
    private readonly IIdempotencyStore _idempotencyStore;
    private readonly PollingOptions _options;

    public LongPollingRunner(ITelegramUpdateSource source, ITelegramUpdateProcessor processor, ICheckpointStore checkpointStore, IIdempotencyStore idempotencyStore, PollingOptions? options = null)
    {
        _source = source;
        _processor = processor;
        _checkpointStore = checkpointStore;
        _idempotencyStore = idempotencyStore;
        _options = options ?? new PollingOptions();
    }

    public async Task<PollingRunResult> RunOnceAsync(CancellationToken cancellationToken = default)
    {
        var offset = await _checkpointStore.GetOffsetAsync(_options.ConsumerName, cancellationToken);
        try
        {
            var batch = await _source.GetUpdatesAsync(offset, _options.Limit, _options.EffectiveAllowedUpdates, cancellationToken);
            if (batch.RetryAfter is { } retryAfter)
            {
                AutomationTelemetry.Retries.Add(1);
                return new PollingRunResult(0, 0, 0, offset, retryAfter);
            }

            var processed = 0;
            var duplicates = 0;
            long? savedOffset = offset;
            foreach (var update in batch.Updates.OrderBy(x => x.UpdateId))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!await _idempotencyStore.TryBeginAsync($"update:{update.UpdateId}", TimeSpan.FromHours(24), cancellationToken))
                {
                    duplicates++;
                    savedOffset = Math.Max(savedOffset ?? 0, update.UpdateId + 1);
                    continue;
                }

                await _processor.ProcessAsync(update, cancellationToken);
                processed++;
                savedOffset = Math.Max(savedOffset ?? 0, update.UpdateId + 1);
                await _checkpointStore.SaveOffsetAsync(_options.ConsumerName, savedOffset.Value, cancellationToken);
            }

            return new PollingRunResult(batch.Updates.Count, processed, duplicates, savedOffset, batch.Updates.Count == 0 ? _options.EffectiveEmptyDelay : null);
        }
        catch (OperationCanceledException) { throw; }
        catch
        {
            AutomationTelemetry.Retries.Add(1);
            return new PollingRunResult(0, 0, 0, offset, _options.EffectiveErrorBackoff);
        }
    }

    public async Task RunUntilCancelledAsync(Func<TimeSpan, CancellationToken, Task>? delay = null, CancellationToken cancellationToken = default)
    {
        delay ??= Task.Delay;
        while (!cancellationToken.IsCancellationRequested)
        {
            var result = await RunOnceAsync(cancellationToken);
            if (result.Backoff is { } backoff && backoff > TimeSpan.Zero)
            {
                await delay(backoff, cancellationToken);
            }
        }
    }
}

public sealed class HttpTelegramUpdateSource : ITelegramUpdateSource
{
    private readonly HttpClient _httpClient;
    private readonly string _botToken;

    public HttpTelegramUpdateSource(HttpClient httpClient, string botToken)
    {
        _httpClient = httpClient;
        _botToken = botToken;
    }

    public async Task<TelegramPollingBatch> GetUpdatesAsync(long? offset, int limit, IReadOnlyList<string> allowedUpdates, CancellationToken cancellationToken = default)
    {
        var response = await _httpClient.PostAsJsonAsync($"bot{_botToken}/getUpdates", new { offset, limit, allowed_updates = allowedUpdates }, cancellationToken);
        if ((int)response.StatusCode == 429)
        {
            return new TelegramPollingBatch([], TimeSpan.FromSeconds(1));
        }
        response.EnsureSuccessStatusCode();
        var envelope = await response.Content.ReadFromJsonAsync<GetUpdatesEnvelope>(cancellationToken: cancellationToken);
        return new TelegramPollingBatch(envelope?.Result ?? []);
    }

    private sealed record GetUpdatesEnvelope(bool Ok, IReadOnlyList<TelegramUpdate> Result);
}

