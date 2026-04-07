using Farmer.Core.Contracts;
using Farmer.Core.Models;
using Farmer.Core.Workflow;
using Farmer.Core.Workflow.Stages;
using Xunit;

namespace Farmer.Tests.Workflow;

public class CreateRunStageTests
{
    [Fact]
    public async Task GeneratesUniqueRunId()
    {
        var store = new InMemoryRunStore();
        var stage = new CreateRunStage(store);

        var state1 = new RunFlowState { WorkRequestName = "test" };
        var state2 = new RunFlowState { WorkRequestName = "test" };

        await stage.ExecuteAsync(state1);
        await stage.ExecuteAsync(state2);

        Assert.NotEmpty(state1.RunId);
        Assert.NotEmpty(state2.RunId);
        Assert.NotEqual(state1.RunId, state2.RunId);
    }

    [Fact]
    public async Task PersistsRunRequest()
    {
        var store = new InMemoryRunStore();
        var stage = new CreateRunStage(store);
        var state = new RunFlowState { WorkRequestName = "react-grid", Attempt = 1 };

        var result = await stage.ExecuteAsync(state);

        Assert.Equal(StageOutcome.Success, result.Outcome);
        Assert.NotNull(state.RunRequest);
        Assert.Equal("react-grid", state.RunRequest!.WorkRequestName);
        Assert.Equal(1, state.RunRequest.AttemptId);

        var saved = await store.GetRunRequestAsync(state.RunId);
        Assert.NotNull(saved);
        Assert.Equal(state.RunId, saved!.RunId);
    }

    [Fact]
    public async Task PersistsRunStatus()
    {
        var store = new InMemoryRunStore();
        var stage = new CreateRunStage(store);
        var state = new RunFlowState { WorkRequestName = "test" };

        await stage.ExecuteAsync(state);

        var status = await store.GetRunStatusAsync(state.RunId);
        Assert.NotNull(status);
        Assert.Equal(state.RunId, status!.RunId);
    }

    [Fact]
    public async Task SetsTaskIdOnState()
    {
        var store = new InMemoryRunStore();
        var stage = new CreateRunStage(store);
        var state = new RunFlowState { WorkRequestName = "test" };

        await stage.ExecuteAsync(state);

        Assert.NotEmpty(state.TaskId);
        Assert.StartsWith("task-", state.TaskId);
    }

    // --- In-memory test double for IRunStore ---

    private sealed class InMemoryRunStore : IRunStore
    {
        private readonly Dictionary<string, RunRequest> _requests = new();
        private readonly Dictionary<string, TaskPacket> _packets = new();
        private readonly Dictionary<string, RunStatus> _statuses = new();
        private readonly Dictionary<string, CostReport> _costs = new();
        private readonly Dictionary<string, ReviewVerdict> _verdicts = new();

        public Task SaveRunRequestAsync(RunRequest request, CancellationToken ct = default)
        {
            _requests[request.RunId] = request;
            return Task.CompletedTask;
        }

        public Task<RunRequest?> GetRunRequestAsync(string runId, CancellationToken ct = default)
        {
            _requests.TryGetValue(runId, out var r);
            return Task.FromResult(r);
        }

        public Task SaveTaskPacketAsync(TaskPacket packet, CancellationToken ct = default)
        {
            _packets[packet.RunId] = packet;
            return Task.CompletedTask;
        }

        public Task<TaskPacket?> GetTaskPacketAsync(string runId, CancellationToken ct = default)
        {
            _packets.TryGetValue(runId, out var p);
            return Task.FromResult(p);
        }

        public Task SaveRunStatusAsync(RunStatus status, CancellationToken ct = default)
        {
            _statuses[status.RunId] = status;
            return Task.CompletedTask;
        }

        public Task<RunStatus?> GetRunStatusAsync(string runId, CancellationToken ct = default)
        {
            _statuses.TryGetValue(runId, out var s);
            return Task.FromResult(s);
        }

        public Task SaveCostReportAsync(CostReport report, CancellationToken ct = default)
        {
            _costs[report.RunId] = report;
            return Task.CompletedTask;
        }

        public Task SaveReviewVerdictAsync(ReviewVerdict verdict, CancellationToken ct = default)
        {
            _verdicts[verdict.RunId] = verdict;
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<string>> ListRunIdsAsync(CancellationToken ct = default)
        {
            return Task.FromResult<IReadOnlyList<string>>(_requests.Keys.ToList());
        }
    }
}
