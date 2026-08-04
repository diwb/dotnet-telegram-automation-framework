using Microsoft.Extensions.DependencyInjection;
using TelegramAutomation.Core;

namespace TelegramAutomation.Observability;

public sealed record TelegramObservabilityOptions(bool EnableOpenTelemetry = false, string ServiceName = "TelegramAutomation");
public static class ObservabilityServiceCollectionExtensions
{
    public static IServiceCollection AddTelegramAutomationObservability(this IServiceCollection services, TelegramObservabilityOptions? options = null)
    {
        services.AddSingleton(options ?? new TelegramObservabilityOptions());
        _ = AutomationTelemetry.ActivitySource.Name;
        return services;
    }
}
