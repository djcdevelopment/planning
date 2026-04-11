using Farmer.Core.Config;
using Farmer.Core.Contracts;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Farmer.Agents;

/// <summary>
/// The single entry point from <c>Farmer.Host</c> into MAF. Host code calls
/// <see cref="AddFarmerAgents"/> and never imports any <c>Microsoft.Agents.*</c>
/// or <c>OpenAI.*</c> types directly. That's the isolation contract: if MAF
/// or the OpenAI SDK churns, only this project changes.
/// </summary>
public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddFarmerAgents(
        this IServiceCollection services,
        IConfiguration config)
    {
        services.Configure<OpenAISettings>(config.GetSection(OpenAISettings.SectionName));
        services.Configure<RetrospectiveSettings>(config.GetSection(RetrospectiveSettings.SectionName));
        services.AddSingleton<IRetrospectiveAgent, MafRetrospectiveAgent>();
        return services;
    }
}
