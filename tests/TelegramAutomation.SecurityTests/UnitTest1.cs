using TelegramAutomation.Abstractions;
using TelegramAutomation.Cli;
using TelegramAutomation.Core;
using TelegramAutomation.Storage.InMemory;
using TelegramAutomation.Webhooks;

namespace TelegramAutomation.SecurityTests;

public class SecurityBehaviorTests
{
    public static IEnumerable<object[]> TokenSamples => new[]
    {
        new object[] { "123456789" + ":" + "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghi" },
        new object[] { "9999999999" + ":" + "abcdefghijklmnopqrstuvwxyzABCDE12345" },
        new object[] { "token " + "111111" + ":" + "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghi" + " chat_id=123456789" }
    };

    [Theory]
    [MemberData(nameof(TokenSamples))]
    public void Redactor_removes_tokens_chat_ids_and_authorization_headers(string secret)
    {
        var redacted = TelegramTool.Redact("Authorization: Bearer abc123\n" + secret);
        Assert.DoesNotContain(":ABCDEFGHIJKLMNOPQRSTUVWXYZ", redacted);
        Assert.DoesNotContain("chat_id=123456789", redacted);
        Assert.DoesNotContain("Bearer abc123", redacted);
        Assert.Contains("<redacted>", redacted);
    }

    [Fact]
    public async Task Authorization_blocks_unallowed_user_and_admin_spoofing()
    {
        var processor = new TelegramAutomationBuilder()
            .Use(AuthorizationMiddleware.Create(new AuthorizationPolicy(new HashSet<long> { 1 }, new HashSet<long> { 10 }, new HashSet<string> { "/status" })))
            .UseCommand("/status", (_, _) => ValueTask.FromResult(AutomationResult.HandledResult("ok")))
            .Build();
        Assert.Equal("Unauthorized.", (await processor.ProcessAsync(new TelegramUpdate(1, 10, 2, "/status"))).Message);
        Assert.Equal("ok", (await processor.ProcessAsync(new TelegramUpdate(2, 10, 1, "/status"))).Message);
    }

    [Theory]
    [InlineData("<b>x</b>")]
    [InlineData("[click](http://127.0.0.1)")]
    [InlineData("../../etc/passwd")]
    [InlineData("$(rm -rf /)")]
    [InlineData("__import__('os')")]
    [InlineData("`code`")]
    [InlineData("a_b")]
    [InlineData("x*y")]
    [InlineData("{tenant:a}")]
    [InlineData("chat_id=999999")]
    public void Markdown_and_html_escape_handle_untrusted_input(string input)
    {
        Assert.True(TelegramMarkdown.EscapeMarkdownV2(input).Length >= input.Length);
        Assert.DoesNotContain("<", TelegramMarkdown.EscapeHtml(input));
    }

    [Fact]
    public void Webhook_security_blocks_secret_spoof_payload_size_callback_size_and_ssrf()
    {
        var validator = new WebhookRequestValidator(new WebhookSecurityOptions("local-secret-token-123", MaxPayloadBytes: 16, AllowedWebhookHosts: new HashSet<string> { "example.com", "localhost" }));
        Assert.False(validator.ValidateSecret("wrong").Allowed);
        Assert.True(validator.ValidateSecret("local-secret-token-123").Allowed);
        Assert.False(validator.ValidatePayload(17).Allowed);
        Assert.False(validator.ValidateUpdate(new TelegramUpdate(1, 2, 3, CallbackData: new string('x', 65), CallbackQueryId: "cb")).Allowed);
        Assert.False(validator.ValidateWebhookUrl("http://example.com/hook").Allowed);
        Assert.False(validator.ValidateWebhookUrl("https://evil.example/hook").Allowed);
        Assert.False(validator.ValidateWebhookUrl("https://10.0.0.5/hook").Allowed);
    }

    [Fact]
    public async Task Rate_limit_and_replay_protection_block_abuse()
    {
        var store = new InMemoryAutomationStore();
        Assert.True((await store.CheckAsync("u:1", 1, TimeSpan.FromMinutes(1))).Allowed);
        Assert.False((await store.CheckAsync("u:1", 1, TimeSpan.FromMinutes(1))).Allowed);
        Assert.True(await store.TryBeginAsync("webhook:1", TimeSpan.FromMinutes(1)));
        Assert.False(await store.TryBeginAsync("webhook:1", TimeSpan.FromMinutes(1)));
    }

    [Fact]
    public async Task Poison_update_is_mapped_to_safe_error()
    {
        var processor = new TelegramAutomationBuilder().UseText((_, _) => throw new InvalidOperationException("Authorization: Bearer secret")).Build();
        var result = await processor.ProcessAsync(new TelegramUpdate(1, 2, 3, "poison"));
        Assert.Equal("The update could not be processed.", result.Message);
    }
}
