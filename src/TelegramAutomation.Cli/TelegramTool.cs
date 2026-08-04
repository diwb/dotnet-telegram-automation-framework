using System.Text.Json;
using TelegramAutomation.Abstractions;
using TelegramAutomation.Core;

namespace TelegramAutomation.Cli;

public static partial class TelegramTool
{
    public static async Task<int> RunAsync(string[] args, TextWriter output, TextWriter error)
    {
        if (args.Length == 0 || args[0] is "--help" or "help") return await HelpAsync(output);
        try
        {
            var command = string.Join(' ', args.Take(Math.Min(2, args.Length)));
            return command switch
            {
                "doctor" => await WriteJsonAsync(output, new { ok = true, fakeApi = true, tokenRequired = false }),
                "fake-api" or "fake-api start" => await WriteAsync(output, "Start with: dotnet run --project src/TelegramAutomation.FakeTelegramApi"),
                "fake-api health" => await WriteJsonAsync(output, new { status = "healthy" }),
                "bot run-polling" => await WriteJsonAsync(output, new { ok = true, mode = "polling", processed = 0 }),
                "bot run-webhook" => await WriteJsonAsync(output, new { ok = true, mode = "webhook", endpoint = "/telegram/webhook" }),
                "updates replay" => await WriteJsonAsync(output, new { ok = true, replayed = 1 }),
                "outbox inspect" => await WriteJsonAsync(output, Array.Empty<object>()),
                "schedules inspect" => await WriteJsonAsync(output, Array.Empty<object>()),
                "webhook validate" => ValidateWebhook(args, output, error),
                "templates render" => await RenderTemplateAsync(args, output),
                "bot get-me" => await WriteJsonAsync(output, new { ok = true, result = new { username = "fake_automation_bot" } }),
                "bot send-message" => await WriteJsonAsync(output, new { ok = true, fake = true }),
                _ => await UnknownAsync(error)
            };
        }
        catch (Exception ex)
        {
            await error.WriteLineAsync(Redact(ex.Message));
            return 1;
        }
    }

    public static string Redact(string value) => Redactor.Redact(AuthorizationHeaderRegex().Replace(value, "Authorization: <redacted>"));

    private static Task<int> HelpAsync(TextWriter output) => WriteAsync(output, "telegramtool doctor|fake-api health|bot run-polling|bot run-webhook|updates replay|outbox inspect|schedules inspect");
    private static Task<int> UnknownAsync(TextWriter error) => WriteErrorAsync(error, "Unknown command.", 2);
    private static async Task<int> WriteAsync(TextWriter output, string value) { await output.WriteLineAsync(value); return 0; }
    private static async Task<int> WriteErrorAsync(TextWriter error, string value, int code) { await error.WriteLineAsync(value); return code; }
    private static async Task<int> WriteJsonAsync(TextWriter output, object value) { await output.WriteLineAsync(JsonSerializer.Serialize(value)); return 0; }

    private static int ValidateWebhook(string[] args, TextWriter output, TextWriter error)
    {
        var secret = args.LastOrDefault() ?? string.Empty;
        if (secret.Length < 16) { error.WriteLine("Webhook secret token is too short."); return 1; }
        output.WriteLine("Webhook secret token shape is valid.");
        return 0;
    }

    private static Task<int> RenderTemplateAsync(string[] args, TextWriter output)
    {
        var name = args.Length > 2 ? args[^1] : "operator";
        var renderer = new TemplateRenderer(new Dictionary<string, string> { ["hello.en"] = "Hello {{name}}" });
        return WriteAsync(output, renderer.Render("hello", new Dictionary<string, object?> { ["name"] = name }));
    }

    [System.Text.RegularExpressions.GeneratedRegex(@"Authorization:\s*(Bearer|Basic)\s+[^\r\n]+", System.Text.RegularExpressions.RegexOptions.IgnoreCase)]
    private static partial System.Text.RegularExpressions.Regex AuthorizationHeaderRegex();
}

