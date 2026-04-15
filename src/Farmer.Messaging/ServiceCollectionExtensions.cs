using Farmer.Core.Contracts;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Farmer.Messaging;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers Farmer.Messaging services. Binds NatsSettings from the
    /// `Farmer:Messaging` config section. When Enabled=false (or unset) the
    /// registrations resolve to noop implementations so Farmer.Host still runs
    /// offline (in-memory tests, air-gapped dev).
    /// </summary>
    public static IServiceCollection AddFarmerMessaging(this IServiceCollection services, IConfiguration config)
    {
        services.Configure<NatsSettings>(config.GetSection(NatsSettings.SectionName));

        services.AddSingleton<NatsConnectionProvider>();
        services.AddSingleton<IRunEventPublisher>(sp =>
        {
            var settings = sp.GetRequiredService<IOptions<NatsSettings>>().Value;
            return settings.Enabled && !string.IsNullOrWhiteSpace(settings.Url)
                ? ActivatorUtilities.CreateInstance<NatsRunEventPublisher>(sp)
                : NoopRunEventPublisher.Instance;
        });
        services.AddSingleton<IRunArtifactStore>(sp =>
        {
            var settings = sp.GetRequiredService<IOptions<NatsSettings>>().Value;
            return settings.Enabled && !string.IsNullOrWhiteSpace(settings.Url)
                ? ActivatorUtilities.CreateInstance<NatsRunArtifactStore>(sp)
                : new NoopRunArtifactStore();
        });

        return services;
    }
}
