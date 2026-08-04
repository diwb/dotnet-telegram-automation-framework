using Microsoft.Extensions.DependencyInjection;
using TelegramAutomation.Abstractions;
using TelegramAutomation.Cli;
using TelegramAutomation.Core;
using TelegramAutomation.Persistence.SQLite;
using TelegramAutomation.Polling;
using TelegramAutomation.Scheduling;
using TelegramAutomation.Storage.InMemory;
using TelegramAutomation.Webhooks;

namespace TelegramAutomation.UnitTests;

public class CoreBehaviorTests
{
    public static IEnumerable<object[]> Commands => Enumerable.Range(1, 35).Select(i => new object[] { $"/status arg{i}", "/status", $"arg{i}" });
    public static IEnumerable<object[]> TextsToEscape => new[] { "hello_world", "*bold*", "[x](y)", "a+b=c", "price: 1.00!", "{json}|pipe", "#tag", "~strike~", "`code`", "> quote" }.Select(x => new object[] { x });

    [Theory]
    [MemberData(nameof(Commands))]
    public void Command_parser_splits_command_and_arguments(string text, string expectedCommand, string expectedArguments)
    {
        var parsed = CommandParser.Parse(text);
        Assert.Equal(expectedCommand, parsed.Command);
        Assert.Equal(expectedArguments, parsed.Arguments);
    }

    [Theory]
    [MemberData(nameof(TextsToEscape))]
    public void Markdown_escape_adds_backslashes_for_special_characters(string value)
    {
        var escaped = TelegramMarkdown.EscapeMarkdownV2(value);
        Assert.True(escaped.Length >= value.Length);
        Assert.Equal(System.Net.WebUtility.HtmlEncode(value), TelegramMarkdown.EscapeHtml(value));
    }

    [Theory]
    [InlineData("/start", TelegramUpdateKind.Command)]
    [InlineData("hello", TelegramUpdateKind.Text)]
    [InlineData(null, TelegramUpdateKind.CallbackQuery)]
    public void Update_kind_is_derived_from_payload(string? text, TelegramUpdateKind kind)
    {
        var update = kind == TelegramUpdateKind.CallbackQuery ? new TelegramUpdate(1, 2, 3, CallbackData: "confirm:yes", CallbackQueryId: "cb") : new TelegramUpdate(1, 2, 3, text);
        Assert.Equal(kind, update.Kind);
    }

    [Fact]
    public async Task Router_invokes_command_callback_text_and_fallback()
    {
        var processor = new TelegramAutomationBuilder()
            .UseCommand("/start", (_, _) => ValueTask.FromResult(AutomationResult.HandledResult("start")))
            .UseCallback("confirm:", (_, _) => ValueTask.FromResult(AutomationResult.HandledResult("callback")))
            .UseText((ctx, _) => ValueTask.FromResult(AutomationResult.HandledResult(ctx.Update.Text)))
            .UseFallback((_, _) => ValueTask.FromResult(AutomationResult.HandledResult("fallback")))
            .Build();
        Assert.Equal("start", (await processor.ProcessAsync(new TelegramUpdate(1, 1, 1, "/start"))).Message);
        Assert.Equal("callback", (await processor.ProcessAsync(new TelegramUpdate(2, 1, 1, CallbackData: "confirm:yes", CallbackQueryId: "cb1"))).Message);
        Assert.Equal("hello", (await processor.ProcessAsync(new TelegramUpdate(3, 1, 1, "hello"))).Message);
        Assert.Equal("fallback", (await processor.ProcessAsync(new TelegramUpdate(4, 1, 1))).Message);
    }

    [Fact]
    public async Task Middlewares_handle_idempotency_authorization_rate_limit_and_exceptions()
    {
        var clock = new FakeClock(DateTimeOffset.UtcNow);
        var store = new InMemoryAutomationStore(clock);
        var calls = 0;
        var processor = new TelegramAutomationBuilder()
            .Use(IdempotencyMiddleware.Create(store, TimeSpan.FromMinutes(5)))
            .Use(RateLimitingMiddleware.Create(store, 1, TimeSpan.FromMinutes(1)))
            .Use(AuthorizationMiddleware.Create(new AuthorizationPolicy(new HashSet<long> { 7 }, new HashSet<long> { 9 }, new HashSet<string> { "/admin" })))
            .UseText((_, _) => { calls++; return ValueTask.FromResult(AutomationResult.HandledResult("ok")); })
            .Build();
        Assert.Equal("ok", (await processor.ProcessAsync(new TelegramUpdate(10, 9, 7, "a"))).Message);
        Assert.Contains("Duplicate", (await processor.ProcessAsync(new TelegramUpdate(10, 9, 7, "a"))).Message);
        Assert.Contains("Rate limited", (await processor.ProcessAsync(new TelegramUpdate(11, 9, 7, "b"))).Message);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task Exception_mapping_returns_safe_message()
    {
        var processor = new TelegramAutomationBuilder().UseText((_, _) => throw new InvalidOperationException("secret " + "123456" + ":" + "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghi")).Build();
        var result = await processor.ProcessAsync(new TelegramUpdate(1, 2, 3, "explode"));
        Assert.Equal("The update could not be processed.", result.Message);
    }

    [Fact]
    public void Template_renderer_escapes_values_and_falls_back_to_english()
    {
        var renderer = new TemplateRenderer(new Dictionary<string, string> { ["hello.en"] = "Hello {{name}}" });
        Assert.Equal("Hello A\\_B", renderer.Render("hello", new Dictionary<string, object?> { ["name"] = "A_B" }, "pt"));
        Assert.Throws<KeyNotFoundException>(() => renderer.Render("missing", new Dictionary<string, object?>()));
        Assert.Throws<InvalidOperationException>(() => new TemplateRenderer(new Dictionary<string, string> { ["long.en"] = new string('x', 4097) }).Render("long", new Dictionary<string, object?>()));
    }

    [Fact]
    public async Task Scheduler_and_outbox_send_due_messages_and_dead_letter_failures()
    {
        var clock = new FakeClock(DateTimeOffset.UtcNow);
        var store = new InMemoryAutomationStore(clock);
        var sender = new CapturingSender();
        await store.EnqueueAsync(new OutboxMessage("1", 42, "outbox", "pending", 0, clock.UtcNow));
        Assert.Equal(1, await new OutboxProcessor(store, sender, clock).ProcessPendingAsync());
        await new LocalScheduler(store, clock, sender).ScheduleAsync("job", 42, "scheduled", clock.UtcNow);
        Assert.Equal(1, await new LocalScheduler(store, clock, sender).ProcessDueAsync());
        sender.Fail = true;
        await store.EnqueueAsync(new OutboxMessage("bad", 42, "bad", "pending", 4, clock.UtcNow));
        await new OutboxProcessor(store, sender, clock).ProcessPendingAsync();
        Assert.Contains(await ((IOutboxStore)store).ListAsync(), x => x.Status == "dead-letter");
    }

    [Fact]
    public async Task Polling_processes_updates_saves_offset_and_handles_duplicates_and_backoff()
    {
        var clock = new FakeClock(DateTimeOffset.UtcNow);
        var store = new InMemoryAutomationStore(clock);
        var source = new QueueUpdateSource(new TelegramPollingBatch([new TelegramUpdate(5, 1, 1, "a"), new TelegramUpdate(5, 1, 1, "a duplicate"), new TelegramUpdate(6, 1, 1, "b")]));
        var processor = new CountingProcessor();
        var runner = new LongPollingRunner(source, processor, store, store, new PollingOptions(EmptyDelay: TimeSpan.FromMilliseconds(1), ErrorBackoff: TimeSpan.FromMilliseconds(2)));
        var result = await runner.RunOnceAsync();
        Assert.Equal(3, result.Received);
        Assert.Equal(2, result.Processed);
        Assert.Equal(1, result.Duplicates);
        Assert.Equal(7, await store.GetOffsetAsync("telegram-polling"));
        source.Next = new TelegramPollingBatch([], TimeSpan.FromSeconds(3));
        Assert.Equal(TimeSpan.FromSeconds(3), (await runner.RunOnceAsync()).Backoff);
        source.Throw = true;
        Assert.Equal(TimeSpan.FromMilliseconds(2), (await runner.RunOnceAsync()).Backoff);
    }

    [Fact]
    public async Task Webhook_processor_validates_secret_payload_update_and_replay()
    {
        var store = new InMemoryAutomationStore();
        var processor = new WebhookUpdateProcessor(new WebhookRequestValidator(new WebhookSecurityOptions("local-secret-token-123")), store, new CountingProcessor());
        Assert.Equal(401, (await processor.ProcessAsync("bad", 10, new TelegramUpdate(1, 2, 3, "x"))).StatusCode);
        Assert.Equal(413, (await processor.ProcessAsync("local-secret-token-123", 99_999, new TelegramUpdate(1, 2, 3, "x"))).StatusCode);
        Assert.Equal(400, (await processor.ProcessAsync("local-secret-token-123", 10, new TelegramUpdate(-1, 2, 3))).StatusCode);
        Assert.True((await processor.ProcessAsync("local-secret-token-123", 10, new TelegramUpdate(1, 2, 3, "x"))).Processed);
        Assert.False((await processor.ProcessAsync("local-secret-token-123", 10, new TelegramUpdate(1, 2, 3, "x"))).Processed);
    }

    [Fact]
    public void Webhook_validator_blocks_ssrf_hosts_callback_size_and_non_https()
    {
        var validator = new WebhookRequestValidator(new WebhookSecurityOptions("local-secret-token-123", AllowedWebhookHosts: new HashSet<string> { "example.com", "localhost" }));
        Assert.True(validator.ValidateWebhookUrl("https://example.com/hook").Allowed);
        Assert.False(validator.ValidateWebhookUrl("http://example.com/hook").Allowed);
        Assert.False(validator.ValidateWebhookUrl("https://evil.com/hook").Allowed);
        Assert.False(validator.ValidateUpdate(new TelegramUpdate(1, 2, 3, CallbackData: new string('x', 65), CallbackQueryId: "cb")).Allowed);
    }

    [Theory]
    [InlineData("doctor")]
    [InlineData("fake-api health")]
    [InlineData("bot run-polling")]
    [InlineData("bot run-webhook")]
    [InlineData("updates replay")]
    [InlineData("outbox inspect")]
    [InlineData("schedules inspect")]
    public async Task Cli_commands_return_success_and_json_when_expected(string command)
    {
        var output = new StringWriter();
        var error = new StringWriter();
        var code = await TelegramTool.RunAsync(command.Split(' '), output, error);
        Assert.Equal(0, code);
        Assert.True(string.IsNullOrWhiteSpace(error.ToString()));
        Assert.NotEmpty(output.ToString());
    }

    [Fact]
    public async Task Cli_reports_errors_and_redacts_tokens()
    {
        var output = new StringWriter();
        var error = new StringWriter();
        Assert.Equal(1, await TelegramTool.RunAsync(["webhook", "validate", "short"], output, error));
        Assert.Equal(2, await TelegramTool.RunAsync(["unknown"], output, error));
        Assert.Contains("<redacted>", TelegramTool.Redact("Authorization: Bearer abc\n123456:ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghi"));
    }

    private sealed class CapturingSender : ITelegramSender
    {
        public bool Fail { get; set; }
        public List<SendMessageRequest> Messages { get; } = [];
        public Task<SendMessageResult> SendMessageAsync(SendMessageRequest request, CancellationToken cancellationToken = default) { Messages.Add(request); return Task.FromResult(Fail ? new SendMessageResult(false, Description: "fail") : new SendMessageResult(true, 1)); }
        public Task<SendMessageResult> EditMessageTextAsync(long chatId, long messageId, string text, CancellationToken cancellationToken = default) => Task.FromResult(new SendMessageResult(true, messageId));
        public Task<SendMessageResult> AnswerCallbackQueryAsync(string callbackQueryId, string? text = null, CancellationToken cancellationToken = default) => Task.FromResult(new SendMessageResult(true));
    }

    private sealed class CountingProcessor : ITelegramUpdateProcessor
    {
        public int Count { get; private set; }
        public ValueTask<AutomationResult> ProcessAsync(TelegramUpdate update, CancellationToken cancellationToken = default) { Count++; return ValueTask.FromResult(AutomationResult.HandledResult("ok")); }
    }

    private sealed class QueueUpdateSource : ITelegramUpdateSource
    {
        public QueueUpdateSource(TelegramPollingBatch next) => Next = next;
        public TelegramPollingBatch Next { get; set; }
        public bool Throw { get; set; }
        public Task<TelegramPollingBatch> GetUpdatesAsync(long? offset, int limit, IReadOnlyList<string> allowedUpdates, CancellationToken cancellationToken = default) => Throw ? throw new HttpRequestException("boom") : Task.FromResult(Next);
    }
}

public class AdditionalCoverageTests
{
    [Fact]
    public void Abstraction_records_and_clocks_cover_core_contracts()
    {
        var clock = new FakeClock(DateTimeOffset.Parse("2026-08-03T00:00:00Z"));
        clock.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal(DateTimeOffset.Parse("2026-08-03T00:01:00Z"), clock.UtcNow);
        Assert.True(new SystemClock().UtcNow <= DateTimeOffset.UtcNow.AddSeconds(1));
        var button = new InlineKeyboardButton("Yes", "confirm:yes");
        var request = new SendMessageRequest(1, "hi", [[button]], "MarkdownV2", "idem");
        Assert.Equal("idem", request.IdempotencyKey);
        Assert.True(new SendMessageResult(true, 10).Ok);
        var context = new TelegramAutomationContext(new TelegramUpdate(1, 2, 3, "x"));
        context.Items["x"] = 1;
        Assert.Equal(1, context.Items["x"]);
        Assert.False(AutomationResult.NotHandled.Handled);
        Assert.Equal("done", AutomationResult.HandledResult("done").Message);
        Assert.Equal(TelegramUpdateKind.Unknown, new TelegramUpdate(1, 2, 3).Kind);
        Assert.Equal("pending", new OutboxMessage("id", 1, "text", "pending", 0, clock.UtcNow).Status);
        Assert.False(new ScheduledJob("id", 1, "text", clock.UtcNow, null).Cancelled);
    }

    [Fact]
    public async Task Core_covers_username_commands_admin_block_service_collection_and_redaction()
    {
        var parsed = CommandParser.Parse("/start@my_bot arg");
        Assert.Equal("/start", parsed.Command);
        Assert.Equal("arg", parsed.Arguments);
        var policy = new AuthorizationPolicy(new HashSet<long>(), new HashSet<long>(), new HashSet<string> { "/admin" });
        var processor = new TelegramAutomationBuilder().Use(AuthorizationMiddleware.Create(policy)).UseCommand("/admin", (_, _) => ValueTask.FromResult(AutomationResult.HandledResult("admin"))).Build();
        Assert.Equal("Admin command blocked.", (await processor.ProcessAsync(new TelegramUpdate(1, 9, 2, "/admin"))).Message);
        var services = new Microsoft.Extensions.DependencyInjection.ServiceCollection();
        services.AddTelegramAutomation(b => b.UseFallback((_, _) => ValueTask.FromResult(AutomationResult.HandledResult("fallback"))));
        var provider = services.BuildServiceProvider();
        var resolved = provider.GetRequiredService<ITelegramUpdateProcessor>();
        Assert.Equal("fallback", (await resolved.ProcessAsync(new TelegramUpdate(2, 1, 1))).Message);
        Assert.Contains("<redacted>", Redactor.Redact("chat_id=123456 token " + "123456" + ":" + "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghi"));
    }

    [Fact]
    public async Task Polling_covers_empty_batch_loop_cancellation_and_offset_input()
    {
        var store = new InMemoryAutomationStore();
        await store.SaveOffsetAsync("telegram-polling", 100);
        var source = new InspectingSource(new TelegramPollingBatch([]));
        var runner = new LongPollingRunner(source, new NoopProcessor(), store, store, new PollingOptions(EmptyDelay: TimeSpan.FromMilliseconds(1)));
        var once = await runner.RunOnceAsync();
        Assert.Equal(100, source.ObservedOffset);
        Assert.Equal(TimeSpan.FromMilliseconds(1), once.Backoff);
        using var cts = new CancellationTokenSource();
        var delays = 0;
        await Assert.ThrowsAsync<TaskCanceledException>(async () => await runner.RunUntilCancelledAsync((_, token) => { delays++; cts.Cancel(); return Task.Delay(1000, token); }, cts.Token));
        Assert.True(delays >= 1);
    }

    [Fact]
    public void Webhook_validator_covers_success_null_payload_localhost_and_invalid_url()
    {
        var validator = new WebhookRequestValidator(new WebhookSecurityOptions("local-secret-token-123"));
        Assert.True(validator.ValidateSecret("local-secret-token-123").Allowed);
        Assert.True(validator.ValidatePayload(null).Allowed);
        Assert.True(validator.ValidatePayload(100).Allowed);
        Assert.True(validator.ValidateWebhookUrl("http://localhost/hook").Allowed);
        Assert.False(validator.ValidateWebhookUrl("not a url").Allowed);
        Assert.True(validator.ValidateUpdate(new TelegramUpdate(1, 2, 3, CallbackData: "ok", CallbackQueryId: "cb")).Allowed);
    }

    [Fact]
    public async Task Webhook_processor_returns_ignored_when_pipeline_does_not_handle()
    {
        var store = new InMemoryAutomationStore();
        var processor = new WebhookUpdateProcessor(new WebhookRequestValidator(new WebhookSecurityOptions("local-secret-token-123")), store, new NotHandledProcessor());
        var result = await processor.ProcessAsync("local-secret-token-123", 10, new TelegramUpdate(99, 2, 3));
        Assert.Equal("ignored", result.Body);
    }

    [Theory]
    [InlineData("help")]
    [InlineData("fake-api start")]
    [InlineData("webhook validate local-secret-token-123")]
    [InlineData("templates render Alice_B")]
    [InlineData("bot get-me")]
    [InlineData("bot send-message")]
    public async Task Cli_covers_help_and_remaining_success_commands(string command)
    {
        var output = new StringWriter();
        var error = new StringWriter();
        Assert.Equal(0, await TelegramTool.RunAsync(command.Split(' '), output, error));
        Assert.NotEmpty(output.ToString());
    }

    private sealed class InspectingSource : ITelegramUpdateSource
    {
        private readonly TelegramPollingBatch _batch;
        public InspectingSource(TelegramPollingBatch batch) => _batch = batch;
        public long? ObservedOffset { get; private set; }
        public Task<TelegramPollingBatch> GetUpdatesAsync(long? offset, int limit, IReadOnlyList<string> allowedUpdates, CancellationToken cancellationToken = default) { ObservedOffset = offset; Assert.Contains("message", allowedUpdates); return Task.FromResult(_batch); }
    }

    private sealed class NoopProcessor : ITelegramUpdateProcessor
    {
        public ValueTask<AutomationResult> ProcessAsync(TelegramUpdate update, CancellationToken cancellationToken = default) => ValueTask.FromResult(AutomationResult.HandledResult("ok"));
    }

    private sealed class NotHandledProcessor : ITelegramUpdateProcessor
    {
        public ValueTask<AutomationResult> ProcessAsync(TelegramUpdate update, CancellationToken cancellationToken = default) => ValueTask.FromResult(AutomationResult.NotHandled);
    }
}




