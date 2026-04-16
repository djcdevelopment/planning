using Farmer.Core.Config;
using Farmer.Core.Contracts;
using Farmer.Core.Models;
using Farmer.Core.Workflow;
using Farmer.Core.Workflow.Stages;
using Microsoft.Extensions.Options;
using Xunit;

namespace Farmer.Tests.Workflow;

/// <summary>
/// Retry-feedback injection: when RunRequest.Feedback is populated, LoadPromptsStage
/// must prepend a synthetic 0-feedback.md prompt so Claude on the VM sees it as
/// prompt #0 (its CLAUDE.md expects that contract). TaskPacket.Feedback mirrors for
/// observability.
/// </summary>
public class LoadPromptsStage_FeedbackTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _samplePlansDir;
    private readonly InMemoryRunStore _store = new();

    public LoadPromptsStage_FeedbackTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "farmer-test-" + Guid.NewGuid().ToString("N")[..8]);
        _samplePlansDir = Path.Combine(_tempDir, "sample-plans");
        Directory.CreateDirectory(_samplePlansDir);

        var dir = Path.Combine(_samplePlansDir, "demo");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "1-Setup.md"), "# setup");
        File.WriteAllText(Path.Combine(dir, "2-Build.md"), "# build");
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    private LoadPromptsStage MakeStage()
    {
        var settings = Options.Create(new FarmerSettings { Paths = new PathsSettings { Data = _tempDir } });
        return new LoadPromptsStage(settings, _store);
    }

    [Fact]
    public async Task Feedback_prompt_prepended_when_RunRequest_has_feedback()
    {
        var stage = MakeStage();
        var feedback = "# Reviewer feedback\nFix the tests.";
        var state = new RunFlowState
        {
            RunId = "run-1",
            WorkRequestName = "demo",
            RunRequest = new RunRequest { RunId = "run-1", WorkRequestName = "demo", Feedback = feedback },
        };

        var result = await stage.ExecuteAsync(state);

        Assert.Equal(StageOutcome.Success, result.Outcome);
        Assert.Equal(3, state.TaskPacket!.Prompts.Count);
        Assert.Equal("0-feedback.md", state.TaskPacket.Prompts[0].Filename);
        Assert.Equal(0, state.TaskPacket.Prompts[0].Order);
        Assert.Equal(feedback, state.TaskPacket.Prompts[0].Content);
        Assert.Equal("1-Setup.md", state.TaskPacket.Prompts[1].Filename);
        Assert.Equal("2-Build.md", state.TaskPacket.Prompts[2].Filename);
    }

    [Fact]
    public async Task No_feedback_prompt_when_feedback_is_null()
    {
        var stage = MakeStage();
        var state = new RunFlowState
        {
            RunId = "run-2",
            WorkRequestName = "demo",
            RunRequest = new RunRequest { RunId = "run-2", WorkRequestName = "demo", Feedback = null },
        };

        var result = await stage.ExecuteAsync(state);

        Assert.Equal(StageOutcome.Success, result.Outcome);
        Assert.Equal(2, state.TaskPacket!.Prompts.Count);
        Assert.DoesNotContain(state.TaskPacket.Prompts, p => p.Filename == "0-feedback.md");
    }

    [Fact]
    public async Task No_feedback_prompt_when_feedback_is_whitespace()
    {
        var stage = MakeStage();
        var state = new RunFlowState
        {
            RunId = "run-3",
            WorkRequestName = "demo",
            RunRequest = new RunRequest { RunId = "run-3", WorkRequestName = "demo", Feedback = "   \n\t " },
        };

        var result = await stage.ExecuteAsync(state);

        Assert.Equal(StageOutcome.Success, result.Outcome);
        Assert.Equal(2, state.TaskPacket!.Prompts.Count);
        Assert.DoesNotContain(state.TaskPacket.Prompts, p => p.Filename == "0-feedback.md");
    }

    [Fact]
    public async Task Task_packet_Feedback_mirrors_RunRequest_Feedback_for_observability()
    {
        var stage = MakeStage();
        var feedback = "address the null-safety issue";
        var state = new RunFlowState
        {
            RunId = "run-4",
            WorkRequestName = "demo",
            RunRequest = new RunRequest { RunId = "run-4", WorkRequestName = "demo", Feedback = feedback },
        };

        await stage.ExecuteAsync(state);

        Assert.Equal(feedback, state.TaskPacket!.Feedback);
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
        public Task SaveRunStateAsync(RunStatus s, CancellationToken ct = default) { _statuses[s.RunId] = s; return Task.CompletedTask; }
        public Task<RunStatus?> GetRunStateAsync(string id, CancellationToken ct = default) { _statuses.TryGetValue(id, out var s); return Task.FromResult(s); }
        public Task SaveCostReportAsync(CostReport r, CancellationToken ct = default) => Task.CompletedTask;
        public Task SaveReviewVerdictAsync(ReviewVerdict v, CancellationToken ct = default) => Task.CompletedTask;
        public Task<IReadOnlyList<string>> ListRunIdsAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<string>>(_requests.Keys.ToList());
    }
}
