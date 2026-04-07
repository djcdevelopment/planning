using Farmer.Core.Config;
using Farmer.Core.Contracts;
using Farmer.Core.Middleware;
using Farmer.Core.Models;
using Farmer.Core.Workflow;
using Farmer.Core.Workflow.Stages;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Farmer.Tests.Workflow;

public class WorkflowPipelineFactoryTests
{
    private static IServiceProvider BuildServiceProvider()
    {
        var services = new ServiceCollection();

        // Configuration
        var settings = new FarmerSettings
        {
            SamplePlansPath = Path.GetTempPath(),
            RunStorePath = Path.Combine(Path.GetTempPath(), "farmer-test-runs"),
            Vms = [new VmConfig { Name = "testvm", SshHost = "testvm", SshUser = "test", MappedDriveLetter = "T", RemoteProjectPath = "~/projects" }]
        };
        services.AddSingleton<IOptions<FarmerSettings>>(Options.Create(settings));

        // Mock infrastructure
        services.AddSingleton<ISshService, StubSshService>();
        services.AddSingleton<IMappedDriveReader, StubMappedDriveReader>();
        services.AddSingleton<IRunStore, StubRunStore>();
        services.AddSingleton<IVmManager, StubVmManager>();

        // Stages (concrete types, transient)
        services.AddTransient<CreateRunStage>();
        services.AddTransient<LoadPromptsStage>();
        services.AddTransient<ReserveVmStage>();
        services.AddTransient<DeliverStage>();
        services.AddTransient<DispatchStage>();
        services.AddTransient<CollectStage>();
        services.AddTransient<ReviewStage>();

        // Stateless middleware
        services.AddSingleton<LoggingMiddleware>();
        services.AddSingleton<TelemetryMiddleware>();
        services.AddSingleton<HeartbeatMiddleware>();

        // Logging
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));

        // Factory
        services.AddSingleton<WorkflowPipelineFactory>();

        return services.BuildServiceProvider();
    }

    [Fact]
    public void Create_ReturnsWorkflowAndCostTracker()
    {
        var sp = BuildServiceProvider();
        var factory = sp.GetRequiredService<WorkflowPipelineFactory>();

        var (workflow, costTracker) = factory.Create();

        Assert.NotNull(workflow);
        Assert.NotNull(costTracker);
    }

    [Fact]
    public void Create_ReturnsFreshCostTrackerEachTime()
    {
        var sp = BuildServiceProvider();
        var factory = sp.GetRequiredService<WorkflowPipelineFactory>();

        var (_, costTracker1) = factory.Create();
        var (_, costTracker2) = factory.Create();

        Assert.NotSame(costTracker1, costTracker2);
    }

    [Fact]
    public async Task Create_WorkflowExecutesSuccessfully()
    {
        var sp = BuildServiceProvider();
        var factory = sp.GetRequiredService<WorkflowPipelineFactory>();
        var (workflow, costTracker) = factory.Create();

        var state = new RunFlowState { WorkRequestName = "test-request" };

        // Workflow will run through all stages — some may fail due to stubs,
        // but the factory wiring itself should not throw
        var result = await workflow.ExecuteAsync(state);

        // CreateRun should have set the RunId
        Assert.False(string.IsNullOrEmpty(state.RunId));
        Assert.True(state.StagesCompleted.Count > 0);
    }

    [Fact]
    public void Create_CostTrackerReportStartsEmpty()
    {
        var sp = BuildServiceProvider();
        var factory = sp.GetRequiredService<WorkflowPipelineFactory>();
        var (_, costTracker) = factory.Create();

        var report = costTracker.GetReport("test-run");
        Assert.Equal("test-run", report.RunId);
        Assert.Empty(report.Stages);
    }

    // --- Stubs ---

    private sealed class StubSshService : ISshService
    {
        public Task<SshResult> ExecuteAsync(string vmName, string command, TimeSpan? timeout = null, CancellationToken ct = default)
            => Task.FromResult(new SshResult { ExitCode = 0 });

        public Task ScpUploadAsync(string vmName, string localPath, string remotePath, CancellationToken ct = default)
            => Task.CompletedTask;

        public Task ScpUploadContentAsync(string vmName, string content, string remotePath, CancellationToken ct = default)
            => Task.CompletedTask;
    }

    private sealed class StubMappedDriveReader : IMappedDriveReader
    {
        public Task<string> ReadFileAsync(string vmName, string relativePath, CancellationToken ct = default)
            => Task.FromResult("{}");

        public Task<bool> FileExistsAsync(string vmName, string relativePath, CancellationToken ct = default)
            => Task.FromResult(false);

        public Task<string> WaitForFileAsync(string vmName, string relativePath, TimeSpan timeout, CancellationToken ct = default)
            => Task.FromResult("{}");

        public Task<IReadOnlyList<string>> ListFilesAsync(string vmName, string relativePath, string pattern = "*", CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<string>>([]);
    }

    private sealed class StubRunStore : IRunStore
    {
        private readonly Dictionary<string, object> _data = new();

        public Task SaveRunRequestAsync(RunRequest request, CancellationToken ct = default)
        { _data[$"req-{request.RunId}"] = request; return Task.CompletedTask; }

        public Task<RunRequest?> GetRunRequestAsync(string runId, CancellationToken ct = default)
            => Task.FromResult(_data.TryGetValue($"req-{runId}", out var v) ? (RunRequest)v : null);

        public Task SaveTaskPacketAsync(TaskPacket packet, CancellationToken ct = default)
        { _data[$"pkt-{packet.RunId}"] = packet; return Task.CompletedTask; }

        public Task<TaskPacket?> GetTaskPacketAsync(string runId, CancellationToken ct = default)
            => Task.FromResult(_data.TryGetValue($"pkt-{runId}", out var v) ? (TaskPacket)v : null);

        public Task SaveRunStatusAsync(RunStatus status, CancellationToken ct = default)
        { _data[$"sts-{status.RunId}"] = status; return Task.CompletedTask; }

        public Task<RunStatus?> GetRunStatusAsync(string runId, CancellationToken ct = default)
            => Task.FromResult(_data.TryGetValue($"sts-{runId}", out var v) ? (RunStatus)v : null);

        public Task SaveCostReportAsync(CostReport report, CancellationToken ct = default)
            => Task.CompletedTask;

        public Task SaveReviewVerdictAsync(ReviewVerdict verdict, CancellationToken ct = default)
            => Task.CompletedTask;

        public Task<IReadOnlyList<string>> ListRunIdsAsync(CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<string>>([]);
    }

    private sealed class StubVmManager : IVmManager
    {
        public Task<VmConfig?> ReserveAsync(CancellationToken ct = default)
            => Task.FromResult<VmConfig?>(new VmConfig
            {
                Name = "testvm",
                SshHost = "testvm",
                SshUser = "test",
                MappedDriveLetter = "T",
                RemoteProjectPath = "~/projects"
            });

        public Task ReleaseAsync(string vmName, CancellationToken ct = default)
            => Task.CompletedTask;

        public Task MarkBusyAsync(string vmName, CancellationToken ct = default)
            => Task.CompletedTask;

        public Task MarkErrorAsync(string vmName, string reason, CancellationToken ct = default)
            => Task.CompletedTask;

        public VmState GetState(string vmName) => VmState.Available;
        public IReadOnlyList<VmConfig> GetAllVms() => [];
    }
}
