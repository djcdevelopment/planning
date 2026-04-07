using Farmer.Core.Config;
using Farmer.Core.Contracts;
using Farmer.Core.Models;
using Farmer.Core.Workflow;
using Farmer.Core.Workflow.Stages;
using Microsoft.Extensions.Options;
using Xunit;

namespace Farmer.Tests.Workflow;

public class LoadPromptsStageTests : IDisposable
{
    private readonly string _tempDir;
    private readonly InMemoryRunStore _store = new();

    public LoadPromptsStageTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "farmer-test-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    private LoadPromptsStage MakeStage()
    {
        var settings = Options.Create(new FarmerSettings { SamplePlansPath = _tempDir });
        return new LoadPromptsStage(settings, _store);
    }

    private void CreatePlanDir(string name, params string[] files)
    {
        var dir = Path.Combine(_tempDir, name);
        Directory.CreateDirectory(dir);
        foreach (var file in files)
        {
            File.WriteAllText(Path.Combine(dir, file), $"# Content of {file}");
        }
    }

    [Fact]
    public async Task LoadsPromptsInNumericOrder()
    {
        CreatePlanDir("my-app", "3-Tests.md", "1-Setup.md", "2-Build.md");

        var stage = MakeStage();
        var state = new RunFlowState { RunId = "run-1", WorkRequestName = "my-app" };

        var result = await stage.ExecuteAsync(state);

        Assert.Equal(StageOutcome.Success, result.Outcome);
        Assert.NotNull(state.TaskPacket);
        Assert.Equal(3, state.TaskPacket!.Prompts.Count);
        Assert.Equal("1-Setup.md", state.TaskPacket.Prompts[0].Filename);
        Assert.Equal("2-Build.md", state.TaskPacket.Prompts[1].Filename);
        Assert.Equal("3-Tests.md", state.TaskPacket.Prompts[2].Filename);
        Assert.Equal(1, state.TaskPacket.Prompts[0].Order);
        Assert.Equal(2, state.TaskPacket.Prompts[1].Order);
        Assert.Equal(3, state.TaskPacket.Prompts[2].Order);
    }

    [Fact]
    public async Task ReadsPromptContent()
    {
        CreatePlanDir("my-app", "1-Setup.md");

        var stage = MakeStage();
        var state = new RunFlowState { RunId = "run-1", WorkRequestName = "my-app" };

        await stage.ExecuteAsync(state);

        Assert.Contains("Content of 1-Setup.md", state.TaskPacket!.Prompts[0].Content);
    }

    [Fact]
    public async Task FailsOnMissingDirectory()
    {
        var stage = MakeStage();
        var state = new RunFlowState { RunId = "run-1", WorkRequestName = "nonexistent" };

        var result = await stage.ExecuteAsync(state);

        Assert.Equal(StageOutcome.Failure, result.Outcome);
        Assert.Contains("not found", result.Error);
    }

    [Fact]
    public async Task FailsOnEmptyDirectory()
    {
        CreatePlanDir("empty-app"); // no files

        var stage = MakeStage();
        var state = new RunFlowState { RunId = "run-1", WorkRequestName = "empty-app" };

        var result = await stage.ExecuteAsync(state);

        Assert.Equal(StageOutcome.Failure, result.Outcome);
        Assert.Contains("No numbered prompt files", result.Error);
    }

    [Fact]
    public async Task IgnoresNonNumberedFiles()
    {
        CreatePlanDir("my-app", "1-Setup.md", "README.md", "notes.txt");

        var stage = MakeStage();
        var state = new RunFlowState { RunId = "run-1", WorkRequestName = "my-app" };

        await stage.ExecuteAsync(state);

        Assert.Single(state.TaskPacket!.Prompts);
        Assert.Equal("1-Setup.md", state.TaskPacket.Prompts[0].Filename);
    }

    [Fact]
    public async Task SetsBranchName()
    {
        CreatePlanDir("my-app", "1-Setup.md");

        var stage = MakeStage();
        var state = new RunFlowState { RunId = "run-1", WorkRequestName = "my-app" };

        await stage.ExecuteAsync(state);

        Assert.Equal("local-my-app", state.TaskPacket!.BranchName);
    }

    [Fact]
    public async Task SetsBranchNameWithVm()
    {
        CreatePlanDir("my-app", "1-Setup.md");

        var stage = MakeStage();
        var state = new RunFlowState
        {
            RunId = "run-1",
            WorkRequestName = "my-app",
            Vm = new VmConfig { Name = "claudefarm1" }
        };

        await stage.ExecuteAsync(state);

        Assert.Equal("claudefarm1-my-app", state.TaskPacket!.BranchName);
    }

    [Fact]
    public async Task PersistsTaskPacket()
    {
        CreatePlanDir("my-app", "1-Setup.md");

        var stage = MakeStage();
        var state = new RunFlowState { RunId = "run-1", WorkRequestName = "my-app" };

        await stage.ExecuteAsync(state);

        var saved = await _store.GetTaskPacketAsync("run-1");
        Assert.NotNull(saved);
        Assert.Equal("my-app", saved!.WorkRequestName);
    }

    // --- In-memory test double ---

    private sealed class InMemoryRunStore : IRunStore
    {
        private readonly Dictionary<string, RunRequest> _requests = new();
        private readonly Dictionary<string, TaskPacket> _packets = new();
        private readonly Dictionary<string, RunStatus> _statuses = new();

        public Task SaveRunRequestAsync(RunRequest r, CancellationToken ct = default) { _requests[r.RunId] = r; return Task.CompletedTask; }
        public Task<RunRequest?> GetRunRequestAsync(string id, CancellationToken ct = default) { _requests.TryGetValue(id, out var r); return Task.FromResult(r); }
        public Task SaveTaskPacketAsync(TaskPacket p, CancellationToken ct = default) { _packets[p.RunId] = p; return Task.CompletedTask; }
        public Task<TaskPacket?> GetTaskPacketAsync(string id, CancellationToken ct = default) { _packets.TryGetValue(id, out var p); return Task.FromResult(p); }
        public Task SaveRunStatusAsync(RunStatus s, CancellationToken ct = default) { _statuses[s.RunId] = s; return Task.CompletedTask; }
        public Task<RunStatus?> GetRunStatusAsync(string id, CancellationToken ct = default) { _statuses.TryGetValue(id, out var s); return Task.FromResult(s); }
        public Task SaveCostReportAsync(CostReport r, CancellationToken ct = default) => Task.CompletedTask;
        public Task SaveReviewVerdictAsync(ReviewVerdict v, CancellationToken ct = default) => Task.CompletedTask;
        public Task<IReadOnlyList<string>> ListRunIdsAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<string>>(_requests.Keys.ToList());
    }
}
