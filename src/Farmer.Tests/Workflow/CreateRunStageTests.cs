using Farmer.Core.Contracts;
using Farmer.Core.Models;
using Farmer.Core.Workflow;
using Farmer.Core.Workflow.Stages;
using Farmer.Tests.TestHelpers;
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

        var status = await store.GetRunStateAsync(state.RunId);
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

}
