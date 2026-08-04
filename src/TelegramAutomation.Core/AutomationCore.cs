using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TelegramAutomation.Abstractions;

namespace TelegramAutomation.Core;

public sealed class TelegramAutomationBuilder
{
    private readonly List<RouteRegistration> _routes = [];
    private readonly List<Func<TelegramUpdateDelegate, TelegramUpdateDelegate>> _middleware = [];
    private TelegramUpdateDelegate _fallback = (_, _) => ValueTask.FromResult(AutomationResult.NotHandled);

    public TelegramAutomationBuilder()
    {
        Use(CorrelationMiddleware.Create());
        Use(ExceptionMappingMiddleware.Create(null));
        Use(TelemetryMiddleware.Create());
    }

    public TelegramAutomationBuilder UseCommand(string command, TelegramUpdateDelegate handler) { _routes.Add(new(RouteKind.Command, command, handler)); return this; }
    public TelegramAutomationBuilder UseCallback(string prefix, TelegramUpdateDelegate handler) { _routes.Add(new(RouteKind.CallbackPrefix, prefix, handler)); return this; }
    public TelegramAutomationBuilder UseText(TelegramUpdateDelegate handler) { _routes.Add(new(RouteKind.Text, string.Empty, handler)); return this; }
    public TelegramAutomationBuilder UseFallback(TelegramUpdateDelegate handler) { _fallback = handler; return this; }
    public TelegramAutomationBuilder Use(Func<TelegramUpdateDelegate, TelegramUpdateDelegate> middleware) { _middleware.Add(middleware); return this; }

    public ITelegramUpdateProcessor Build(IServiceProvider? services = null)
    {
        var router = new TelegramRouter(_routes, _fallback);
        TelegramUpdateDelegate terminal = router.RouteAsync;
        for (var index = _middleware.Count - 1; index >= 0; index--) terminal = _middleware[index](terminal);
        return new TelegramUpdateProcessor(terminal, services);
    }

    private sealed record RouteRegistration(RouteKind Kind, string Pattern, TelegramUpdateDelegate Handler);
    private enum RouteKind { Command, CallbackPrefix, Text }

    private sealed class TelegramRouter
    {
        private readonly IReadOnlyList<RouteRegistration> _routes;
        private readonly TelegramUpdateDelegate _fallback;
        public TelegramRouter(IReadOnlyList<RouteRegistration> routes, TelegramUpdateDelegate fallback) { _routes = routes; _fallback = fallback; }
        public ValueTask<AutomationResult> RouteAsync(TelegramAutomationContext context, CancellationToken cancellationToken)
        {
            foreach (var route in _routes)
            {
                if (Matches(route, context.Update)) { AutomationTelemetry.RecordHandlerMatched(route.Pattern); return route.Handler(context, cancellationToken); }
            }
            return _fallback(context, cancellationToken);
        }
        private static bool Matches(RouteRegistration route, TelegramUpdate update) => route.Kind switch
        {
            RouteKind.Command => update.Kind == TelegramUpdateKind.Command && CommandParser.Parse(update.Text!).Command.Equals(route.Pattern, StringComparison.OrdinalIgnoreCase),
            RouteKind.CallbackPrefix => update.Kind == TelegramUpdateKind.CallbackQuery && update.CallbackData?.StartsWith(route.Pattern, StringComparison.Ordinal) == true,
            RouteKind.Text => update.Kind == TelegramUpdateKind.Text,
            _ => false
        };
    }

    private sealed class TelegramUpdateProcessor : ITelegramUpdateProcessor
    {
        private readonly TelegramUpdateDelegate _pipeline;
        private readonly IServiceProvider? _services;
        public TelegramUpdateProcessor(TelegramUpdateDelegate pipeline, IServiceProvider? services) { _pipeline = pipeline; _services = services; }
        public ValueTask<AutomationResult> ProcessAsync(TelegramUpdate update, CancellationToken cancellationToken = default)
        {
            AutomationTelemetry.UpdatesReceived.Add(1);
            return _pipeline(new TelegramAutomationContext(update, _services), cancellationToken);
        }
    }
}

public static class TelegramAutomationServiceCollectionExtensions
{
    public static IServiceCollection AddTelegramAutomation(this IServiceCollection services, Action<TelegramAutomationBuilder>? configure = null)
    {
        services.AddSingleton<IClock, SystemClock>();
        services.AddSingleton(provider =>
        {
            var builder = new TelegramAutomationBuilder();
            configure?.Invoke(builder);
            return builder.Build(provider);
        });
        return services;
    }
}

public sealed record ParsedCommand(string Command, string Arguments);
public static class CommandParser
{
    public static ParsedCommand Parse(string text)
    {
        var trimmed = text.Trim();
        var firstSpace = trimmed.IndexOf(' ', StringComparison.Ordinal);
        if (firstSpace < 0) return new ParsedCommand(trimmed.Split('@')[0], string.Empty);
        return new ParsedCommand(trimmed[..firstSpace].Split('@')[0], trimmed[(firstSpace + 1)..].Trim());
    }
}

public static partial class Redactor
{
    public static string Redact(string value) => TelegramChatIdRegex().Replace(TelegramBotTokenRegex().Replace(value, "$1:<redacted>"), "chat_id=<redacted>");
    [GeneratedRegex(@"\b(\d{6,12}):[A-Za-z0-9_-]{30,}\b")] private static partial Regex TelegramBotTokenRegex();
    [GeneratedRegex(@"chat_id=-?\d{5,}")] private static partial Regex TelegramChatIdRegex();
}

public static class TelegramMarkdown
{
    private static readonly char[] MarkdownV2 = ['_', '*', '[', ']', '(', ')', '~', '`', '>', '#', '+', '-', '=', '|', '{', '}', '.', '!'];
    public static string EscapeMarkdownV2(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (var c in value) { if (MarkdownV2.Contains(c)) builder.Append('\\'); builder.Append(c); }
        return builder.ToString();
    }
    public static string EscapeHtml(string value) => WebUtility.HtmlEncode(value);
}

public sealed class TemplateRenderer
{
    private readonly IReadOnlyDictionary<string, string> _templates;
    public TemplateRenderer(IReadOnlyDictionary<string, string> templates) => _templates = templates;
    public string Render(string key, IReadOnlyDictionary<string, object?> model, string language = "en")
    {
        if (!_templates.TryGetValue($"{key}.{language}", out var template) && !_templates.TryGetValue($"{key}.en", out template)) throw new KeyNotFoundException($"Template '{key}' was not found.");
        foreach (var pair in model) template = template.Replace("{{" + pair.Key + "}}", TelegramMarkdown.EscapeMarkdownV2(Convert.ToString(pair.Value) ?? string.Empty), StringComparison.Ordinal);
        if (template.Length > 4096) throw new InvalidOperationException("Rendered Telegram message exceeds 4096 characters.");
        return template;
    }
}

public sealed record AuthorizationPolicy(IReadOnlySet<long> AllowedUsers, IReadOnlySet<long> AllowedChats, IReadOnlySet<string> AdminCommands);
public static class AuthorizationMiddleware
{
    public static Func<TelegramUpdateDelegate, TelegramUpdateDelegate> Create(AuthorizationPolicy policy) => next => async (context, cancellationToken) =>
    {
        var userAllowed = policy.AllowedUsers.Count == 0 || policy.AllowedUsers.Contains(context.Update.UserId);
        var chatAllowed = policy.AllowedChats.Count == 0 || policy.AllowedChats.Contains(context.Update.ChatId);
        if (!userAllowed || !chatAllowed) return AutomationResult.HandledResult("Unauthorized.");
        if (context.Update.Kind == TelegramUpdateKind.Command)
        {
            var command = CommandParser.Parse(context.Update.Text!).Command;
            if (policy.AdminCommands.Contains(command) && !policy.AllowedUsers.Contains(context.Update.UserId)) return AutomationResult.HandledResult("Admin command blocked.");
        }
        return await next(context, cancellationToken);
    };
}

public static class IdempotencyMiddleware
{
    public static Func<TelegramUpdateDelegate, TelegramUpdateDelegate> Create(IIdempotencyStore store, TimeSpan ttl) => next => async (context, cancellationToken) =>
    {
        var key = context.Update.CallbackQueryId is { Length: > 0 } callbackId ? $"callback:{callbackId}" : $"update:{context.Update.UpdateId}";
        if (!await store.TryBeginAsync(key, ttl, cancellationToken)) { AutomationTelemetry.DuplicateUpdates.Add(1); return AutomationResult.HandledResult("Duplicate update ignored."); }
        return await next(context, cancellationToken);
    };
}

public static class RateLimitingMiddleware
{
    public static Func<TelegramUpdateDelegate, TelegramUpdateDelegate> Create(IRateLimitStore store, int limit, TimeSpan window) => next => async (context, cancellationToken) =>
    {
        var decision = await store.CheckAsync($"chat:{context.Update.ChatId}", limit, window, cancellationToken);
        if (!decision.Allowed) { AutomationTelemetry.RateLimited.Add(1); return AutomationResult.HandledResult($"Rate limited. Retry after {decision.RetryAfter.TotalSeconds:N0}s."); }
        return await next(context, cancellationToken);
    };
}

public static class CorrelationMiddleware { public static Func<TelegramUpdateDelegate, TelegramUpdateDelegate> Create() => next => (context, cancellationToken) => { context.CorrelationId = $"tg-{context.Update.UpdateId}-{Guid.NewGuid():N}"; return next(context, cancellationToken); }; }
public static class ExceptionMappingMiddleware
{
    public static Func<TelegramUpdateDelegate, TelegramUpdateDelegate> Create(ILoggerFactory? loggerFactory) => next => async (context, cancellationToken) =>
    {
        try { return await next(context, cancellationToken); }
        catch (Exception ex) when (ex is not OperationCanceledException) { loggerFactory?.CreateLogger("TelegramAutomation").LogError(ex, "Handler failed for {CorrelationId}", context.CorrelationId); AutomationTelemetry.HandlerFailures.Add(1); return AutomationResult.HandledResult("The update could not be processed."); }
    };
}
public static class TelemetryMiddleware
{
    public static Func<TelegramUpdateDelegate, TelegramUpdateDelegate> Create() => next => async (context, cancellationToken) =>
    {
        using var activity = AutomationTelemetry.ActivitySource.StartActivity("telegram.update.process");
        activity?.SetTag("telegram.update_id", context.Update.UpdateId);
        activity?.SetTag("telegram.chat_type", context.Update.ChatType);
        var stopwatch = Stopwatch.StartNew();
        var result = await next(context, cancellationToken);
        stopwatch.Stop();
        AutomationTelemetry.HandlerDuration.Record(stopwatch.Elapsed.TotalMilliseconds);
        AutomationTelemetry.UpdatesProcessed.Add(1);
        return result;
    };
}

public static class AutomationTelemetry
{
    public static readonly ActivitySource ActivitySource = new("TelegramAutomation");
    public static readonly Meter Meter = new("TelegramAutomation");
    public static readonly Counter<long> UpdatesReceived = Meter.CreateCounter<long>("telegram_updates_received");
    public static readonly Counter<long> UpdatesProcessed = Meter.CreateCounter<long>("telegram_updates_processed");
    public static readonly Counter<long> HandlerFailures = Meter.CreateCounter<long>("telegram_handler_failures");
    public static readonly Counter<long> DuplicateUpdates = Meter.CreateCounter<long>("telegram_duplicate_updates");
    public static readonly Counter<long> TelegramApiCalls = Meter.CreateCounter<long>("telegram_api_calls");
    public static readonly Counter<long> Retries = Meter.CreateCounter<long>("telegram_retries");
    public static readonly Counter<long> RateLimited = Meter.CreateCounter<long>("telegram_rate_limited");
    public static readonly Histogram<double> HandlerDuration = Meter.CreateHistogram<double>("telegram_handler_duration_ms");
    public static void RecordHandlerMatched(string route) => Activity.Current?.SetTag("telegram.route", route);
}


