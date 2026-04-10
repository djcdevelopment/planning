using Farmer.Core.Config;
using Farmer.Core.Contracts;
using Farmer.Core.Middleware;
using Farmer.Core.Workflow;
using Farmer.Core.Workflow.Stages;
using Farmer.Tools;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace Farmer.Tests.Workflow;

public class DiCompositionTests
{
    private ServiceProvider BuildProvider()
    {
        var services = new ServiceCollection();

        services.AddLogging();
        services.Configure<FarmerSettings>(o =>
        {
            o.Paths = new PathsSettings
            {
                Root = Path.GetTempPath(),
                Data = Path.GetTempPath(),
                Runs = Path.Combine(Path.GetTempPath(), "farmer-test-runs"),
                Inbox = Path.Combine(Path.GetTempPath(), "farmer-test-inbox"),
            };
            o.Vms =
            [
                new VmConfig { Name = "test-vm", SshHost = "localhost", MappedDriveLetter = "Z" }
            ];
        });

        // Infrastructure
        services.AddSingleton<ISshService, SshService>();
        services.AddSingleton<IMappedDriveReader, MappedDriveReader>();
        services.AddSingleton<IRunStore, FileRunStore>();
        services.AddSingleton<IVmManager, VmManager>();

        // Stages (explicit order)
        services.AddSingleton<CreateRunStage>();
        services.AddSingleton<LoadPromptsStage>();
        services.AddSingleton<ReserveVmStage>();
        services.AddSingleton<DeliverStage>();
        services.AddSingleton<DispatchStage>();
        services.AddSingleton<CollectStage>();
        services.AddSingleton<ReviewStage>();

        services.AddSingleton<IEnumerable<IWorkflowStage>>(sp => new IWorkflowStage[]
        {
            sp.GetRequiredService<CreateRunStage>(),
            sp.GetRequiredService<LoadPromptsStage>(),
            sp.GetRequiredService<ReserveVmStage>(),
            sp.GetRequiredService<DeliverStage>(),
            sp.GetRequiredService<DispatchStage>(),
            sp.GetRequiredService<CollectStage>(),
            sp.GetRequiredService<ReviewStage>(),
        });

        // Middleware
        services.AddSingleton<TelemetryMiddleware>();
        services.AddSingleton<LoggingMiddleware>();
        services.AddSingleton<EventingMiddleware>();
        services.AddSingleton<CostTrackingMiddleware>();
        services.AddSingleton<HeartbeatMiddleware>();

        services.AddSingleton<IEnumerable<IWorkflowMiddleware>>(sp => new IWorkflowMiddleware[]
        {
            sp.GetRequiredService<TelemetryMiddleware>(),
            sp.GetRequiredService<LoggingMiddleware>(),
            sp.GetRequiredService<EventingMiddleware>(),
            sp.GetRequiredService<CostTrackingMiddleware>(),
            sp.GetRequiredService<HeartbeatMiddleware>(),
        });

        services.AddSingleton<RunWorkflow>();

        return services.BuildServiceProvider();
    }

    [Fact]
    public void AllServicesResolve()
    {
        using var sp = BuildProvider();
        Assert.NotNull(sp.GetRequiredService<RunWorkflow>());
        Assert.NotNull(sp.GetRequiredService<ISshService>());
        Assert.NotNull(sp.GetRequiredService<IMappedDriveReader>());
        Assert.NotNull(sp.GetRequiredService<IRunStore>());
        Assert.NotNull(sp.GetRequiredService<IVmManager>());
    }

    [Fact]
    public void StagesResolveInExactOrder()
    {
        using var sp = BuildProvider();
        var stages = sp.GetRequiredService<IEnumerable<IWorkflowStage>>().ToList();

        Assert.Equal(7, stages.Count);
        Assert.Equal("CreateRun", stages[0].Name);
        Assert.Equal("LoadPrompts", stages[1].Name);
        Assert.Equal("ReserveVm", stages[2].Name);
        Assert.Equal("Deliver", stages[3].Name);
        Assert.Equal("Dispatch", stages[4].Name);
        Assert.Equal("Collect", stages[5].Name);
        Assert.Equal("Review", stages[6].Name);
    }

    [Fact]
    public void MiddlewareResolvesInExpectedOrder()
    {
        using var sp = BuildProvider();
        var middleware = sp.GetRequiredService<IEnumerable<IWorkflowMiddleware>>().ToList();

        Assert.Equal(5, middleware.Count);
        Assert.IsType<TelemetryMiddleware>(middleware[0]);
        Assert.IsType<LoggingMiddleware>(middleware[1]);
        Assert.IsType<EventingMiddleware>(middleware[2]);
        Assert.IsType<CostTrackingMiddleware>(middleware[3]);
        Assert.IsType<HeartbeatMiddleware>(middleware[4]);
    }
}
