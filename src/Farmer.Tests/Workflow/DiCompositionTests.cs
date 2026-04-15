using Farmer.Core.Config;
using Farmer.Core.Contracts;
using Farmer.Core.Middleware;
using Farmer.Core.Models;
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

        // Stages — resolved by WorkflowPipelineFactory
        services.AddSingleton<CreateRunStage>();
        services.AddSingleton<LoadPromptsStage>();
        services.AddSingleton<ReserveVmStage>();
        services.AddSingleton<DeliverStage>();
        services.AddSingleton<DispatchStage>();
        services.AddSingleton<CollectStage>();
        services.AddSingleton<RetrospectiveStage>();

        // Retrospective agent — fake for DI test
        services.Configure<RetrospectiveSettings>(_ => { });
        services.AddSingleton<IRetrospectiveAgent, NoOpRetrospectiveAgent>();

        // Stateless middleware — resolved by WorkflowPipelineFactory.
        // CostTrackingMiddleware is NOT registered: factory creates it per-run.
        services.AddSingleton<TelemetryMiddleware>();
        services.AddSingleton<LoggingMiddleware>();
        services.AddSingleton<EventingMiddleware>();
        services.AddSingleton<HeartbeatMiddleware>();

        services.AddSingleton<WorkflowPipelineFactory>();

        return services.BuildServiceProvider();
    }

    [Fact]
    public void AllInfrastructureServicesResolve()
    {
        using var sp = BuildProvider();
        Assert.NotNull(sp.GetRequiredService<WorkflowPipelineFactory>());
        Assert.NotNull(sp.GetRequiredService<ISshService>());
        Assert.NotNull(sp.GetRequiredService<IMappedDriveReader>());
        Assert.NotNull(sp.GetRequiredService<IRunStore>());
        Assert.NotNull(sp.GetRequiredService<IVmManager>());
    }

    [Fact]
    public void Factory_BuildsWorkflowWithStagesInExactOrder()
    {
        using var sp = BuildProvider();
        var factory = sp.GetRequiredService<WorkflowPipelineFactory>();

        var (workflow, _) = factory.Create();

        // Inspect via reflection — _stages is private
        var stagesField = typeof(RunWorkflow).GetField("_stages",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        Assert.NotNull(stagesField);
        var stages = (IReadOnlyList<IWorkflowStage>)stagesField!.GetValue(workflow)!;

        Assert.Equal(7, stages.Count);
        Assert.Equal("CreateRun", stages[0].Name);
        Assert.Equal("LoadPrompts", stages[1].Name);
        Assert.Equal("ReserveVm", stages[2].Name);
        Assert.Equal("Deliver", stages[3].Name);
        Assert.Equal("Dispatch", stages[4].Name);
        Assert.Equal("Collect", stages[5].Name);
        Assert.Equal("Retrospective", stages[6].Name);
    }

    [Fact]
    public void Factory_BuildsWorkflowWithMiddlewareInOutermostFirstOrder()
    {
        using var sp = BuildProvider();
        var factory = sp.GetRequiredService<WorkflowPipelineFactory>();

        var (workflow, _) = factory.Create();

        var middlewareField = typeof(RunWorkflow).GetField("_middleware",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        Assert.NotNull(middlewareField);
        var middleware = (IReadOnlyList<IWorkflowMiddleware>)middlewareField!.GetValue(workflow)!;

        Assert.Equal(5, middleware.Count);
        Assert.IsType<TelemetryMiddleware>(middleware[0]);
        Assert.IsType<LoggingMiddleware>(middleware[1]);
        Assert.IsType<EventingMiddleware>(middleware[2]);
        Assert.IsType<CostTrackingMiddleware>(middleware[3]);
        Assert.IsType<HeartbeatMiddleware>(middleware[4]);
    }

    [Fact]
    public void Factory_ReturnsFreshCostTrackerPerCall()
    {
        using var sp = BuildProvider();
        var factory = sp.GetRequiredService<WorkflowPipelineFactory>();

        var (_, costTracker1) = factory.Create();
        var (_, costTracker2) = factory.Create();

        Assert.NotSame(costTracker1, costTracker2);
    }

    [Fact]
    public void Factory_ReturnsFreshWorkflowPerCall()
    {
        using var sp = BuildProvider();
        var factory = sp.GetRequiredService<WorkflowPipelineFactory>();

        var (workflow1, _) = factory.Create();
        var (workflow2, _) = factory.Create();

        Assert.NotSame(workflow1, workflow2);
    }

    /// <summary>No-op agent for DI composition tests. Never calls OpenAI.</summary>
    private sealed class NoOpRetrospectiveAgent : IRetrospectiveAgent
    {
        public Task<RetrospectiveResult> AnalyzeAsync(
            RetrospectiveContext context, CancellationToken ct = default)
        {
            return Task.FromResult(new RetrospectiveResult
            {
                Verdict = new ReviewVerdict
                {
                    RunId = context.RunId,
                    Verdict = Verdict.Accept,
                    RiskScore = 0,
                },
                QaRetroMarkdown = "No-op retrospective for DI composition test.",
            });
        }
    }
}
