using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using TelegramAutomation.Abstractions;
using TelegramAutomation.Persistence.SQLite;
using TelegramAutomation.Polling;
using TelegramAutomation.Storage.InMemory;

namespace TelegramAutomation.IntegrationTests;

public class IntegrationBehaviorTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;
    public IntegrationBehaviorTests(WebApplicationFactory<Program> factory) => _factory = factory;

    [Fact]
    public async Task Fake_api_getme_updates_send_message_and_inline_keyboard_work_over_http()
    {
        var client = _factory.CreateClient();
        await client.PostAsync("/admin/reset", null);
        var me = await client.GetAsync("/botfake-token/getMe");
        Assert.True(me.IsSuccessStatusCode);
        await client.PostAsJsonAsync("/admin/enqueue-update", new TelegramUpdate(1, 10, 20, "/start"));
        var updates = await client.PostAsJsonAsync("/botfake-token/getUpdates", new { offset = 0, limit = 10, allowed_updates = new[] { "message" } });
        Assert.Contains("/start", await updates.Content.ReadAsStringAsync());
        var send = await client.PostAsJsonAsync("/botfake-token/sendMessage", new { chat_id = 10, text = "hello", reply_markup = new { inline_keyboard = new[] { new[] { new { text = "OK", callback_data = "confirm:ok" } } } } });
        Assert.True(send.IsSuccessStatusCode);
        var messages = await client.GetStringAsync("/admin/messages");
        Assert.Contains("confirm:ok", messages);
    }

    [Fact]
    public async Task Fake_api_supports_callbacks_webhook_configuration_rate_limits_and_errors()
    {
        var client = _factory.CreateClient();
        await client.PostAsync("/admin/reset", null);
        await client.PostAsJsonAsync("/admin/enqueue-update", new TelegramUpdate(2, 10, 20, CallbackData: "confirm:yes", CallbackQueryId: "cb-1"));
        Assert.Contains("confirm:yes", await (await client.PostAsJsonAsync("/botfake-token/getUpdates", new { })).Content.ReadAsStringAsync());
        Assert.True((await client.PostAsJsonAsync("/botfake-token/setWebhook", new { url = "https://example.com/hook", secret_token = "local-secret-token-123" })).IsSuccessStatusCode);
        Assert.Contains("delivered", await (await client.PostAsync("/admin/deliver-webhook", null)).Content.ReadAsStringAsync());
        await client.PostAsync("/admin/fault/rate-limit?retryAfter=2", null);
        Assert.Equal((HttpStatusCode)429, (await client.PostAsJsonAsync("/botfake-token/sendMessage", new { chat_id = 10, text = "x" })).StatusCode);
        await client.PostAsync("/admin/fault/retryable", null);
        Assert.Equal(HttpStatusCode.InternalServerError, (await client.PostAsJsonAsync("/botfake-token/editMessageText", new { chat_id = 10, message_id = 1, text = "x" })).StatusCode);
        await client.PostAsync("/admin/fault/non-retryable", null);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/botfake-token/answerCallbackQuery", new { callback_query_id = "cb" })).StatusCode);
    }

    [Fact]
    public async Task Http_polling_source_reads_updates_and_handles_rate_limit()
    {
        var client = _factory.CreateClient();
        await client.PostAsync("/admin/reset", null);
        await client.PostAsJsonAsync("/admin/enqueue-update", new TelegramUpdate(9, 10, 20, "hello"));
        var source = new HttpTelegramUpdateSource(client, "fake-token");
        var batch = await source.GetUpdatesAsync(0, 10, ["message"]);
        Assert.Single(batch.Updates);
        await client.PostAsync("/admin/fault/rate-limit?retryAfter=1", null);
        Assert.NotNull((await source.GetUpdatesAsync(0, 10, ["message"])).RetryAfter);
    }

    [Fact]
    public async Task Sqlite_persists_idempotency_conversation_outbox_schedules_and_offsets_after_restart()
    {
        var path = Path.Combine(Path.GetTempPath(), $"telegram-{Guid.NewGuid():N}.sqlite");
        var connection = $"Data Source={path}";
        var clock = new FakeClock(DateTimeOffset.Parse("2026-08-03T12:00:00Z"));
        var store = new SqliteAutomationStore(connection, clock);
        Assert.True(await store.TryBeginAsync("update:1", TimeSpan.FromMinutes(5)));
        Assert.False(await store.TryBeginAsync("update:1", TimeSpan.FromMinutes(5)));
        await store.SetAsync(new ConversationState(10, 20, "collecting", "{\"name\":\"A\"}", clock.UtcNow.AddMinutes(10)));
        await store.EnqueueAsync(new OutboxMessage("m1", 10, "pending", "pending", 0, clock.UtcNow));
        await store.MarkAsync("m1", "sent", 1, null, null);
        await store.UpsertAsync(new ScheduledJob("s1", 10, "due", clock.UtcNow, TimeSpan.FromMinutes(5)));
        await store.SaveOffsetAsync("polling", 42);

        var restarted = new SqliteAutomationStore(connection, clock);
        Assert.Equal("collecting", (await restarted.GetAsync(10, 20))!.State);
        Assert.Equal("sent", (await ((IOutboxStore)restarted).ListAsync()).Single().Status);
        Assert.Single(await restarted.DueAsync(clock.UtcNow));
        Assert.Equal(42, await restarted.GetOffsetAsync("polling"));
        await restarted.CancelAsync("s1");
        Assert.True((await ((IScheduleStore)restarted).ListAsync()).Single().Cancelled);
    }

    [Fact]
    public async Task Sqlite_handles_basic_concurrent_idempotency_race()
    {
        var path = Path.Combine(Path.GetTempPath(), $"telegram-race-{Guid.NewGuid():N}.sqlite");
        var store = new SqliteAutomationStore($"Data Source={path}");
        var results = await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => store.TryBeginAsync("same", TimeSpan.FromMinutes(1))));
        Assert.Equal(1, results.Count(x => x));
    }

    [Fact]
    public async Task Cli_process_returns_zero_for_doctor()
    {
        var project = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "TelegramAutomation.Cli", "TelegramAutomation.Cli.csproj"));
        var start = new ProcessStartInfo("dotnet", $"run --project \"{project}\" -- doctor") { RedirectStandardOutput = true, RedirectStandardError = true };
        using var process = Process.Start(start)!;
        var output = await process.StandardOutput.ReadToEndAsync();
        var error = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        Assert.Equal(0, process.ExitCode);
        Assert.Contains("ok", output);
        Assert.DoesNotContain("token", error, StringComparison.OrdinalIgnoreCase);
    }
}
