using Farmer.Core.Telemetry;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace Farmer.Host.Telemetry;

public static class TelemetrySetup
{
    public static IServiceCollection AddFarmerTelemetry(this IServiceCollection services)
    {
        services.AddOpenTelemetry()
            .ConfigureResource(resource => resource
                .AddService(
                    serviceName: "Farmer",
                    serviceVersion: "0.1.0"))
            .WithTracing(builder =>
            {
                builder
                    .AddSource(FarmerDiagnostics.ActivitySourceName)
                    .AddAspNetCoreInstrumentation()
                    .AddConsoleExporter()
                    .AddOtlpExporter();
            });

        return services;
    }
}
