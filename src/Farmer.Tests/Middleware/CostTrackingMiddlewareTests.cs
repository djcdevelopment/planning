using Farmer.Core.Middleware;
using Farmer.Core.Models;
using Farmer.Core.Workflow;
using Xunit;

namespace Farmer.Tests.Middleware;

public class CostTrackingMiddlewareTests
{
    [Fact]
    public async Task TracksStageTimings()
    {
        var mw = new CostTrackingMiddleware();
        var stage = new FakeStage("StageA", RunPhase.Loading);
        var state = new RunFlowState { RunId = "run-1" };

        await mw.InvokeAsync(stage, state, () => Task.FromResult(StageResult.Succeeded("StageA")));

        var report = mw.GetReport("run-1");

        Assert.Equal("run-1", report.RunId);
        Assert.Single(report.Stages);
        Assert.Equal("StageA", report.Stages[0].StageName);
        Assert.True(report.Stages[0].DurationSeconds >= 0);
    }

    [Fact]
    public async Task AccumulatesMultipleStages()
    {
        var mw = new CostTrackingMiddleware();
        var state = new RunFlowState { RunId = "run-1" };

        await mw.InvokeAsync(new FakeStage("A", RunPhase.Loading), state,
            () => Task.FromResult(StageResult.Succeeded("A")));
        await mw.InvokeAsync(new FakeStage("B", RunPhase.Delivering), state,
            () => Task.FromResult(StageResult.Succeeded("B")));
        await mw.InvokeAsync(new FakeStage("C", RunPhase.Collecting), state,
            () => Task.FromResult(StageResult.Succeeded("C")));

        var report = mw.GetReport("run-1");

        Assert.Equal(3, report.Stages.Count);
        Assert.Equal("A", report.Stages[0].StageName);
        Assert.Equal("B", report.Stages[1].StageName);
        Assert.Equal("C", report.Stages[2].StageName);
        Assert.True(report.TotalDurationSeconds >= 0);
    }

    [Fact]
    public async Task ReportHasTimestamps()
    {
        var mw = new CostTrackingMiddleware();
        var state = new RunFlowState { RunId = "run-1" };

        await mw.InvokeAsync(new FakeStage("A", RunPhase.Loading), state,
            () => Task.FromResult(StageResult.Succeeded("A")));

        var report = mw.GetReport("run-1");

        Assert.True(report.Stages[0].CompletedAt >= report.Stages[0].StartedAt);
        Assert.True(report.GeneratedAt > DateTimeOffset.MinValue);
    }

    [Fact]
    public async Task StillTracksOnFailure()
    {
        var mw = new CostTrackingMiddleware();
        var state = new RunFlowState { RunId = "run-1" };

        await mw.InvokeAsync(new FakeStage("FailStage", RunPhase.Loading), state,
            () => Task.FromResult(StageResult.Failed("FailStage", "error")));

        var report = mw.GetReport("run-1");

        Assert.Single(report.Stages);
        Assert.Equal("FailStage", report.Stages[0].StageName);
    }

    [Fact]
    public async Task CallsNext()
    {
        var mw = new CostTrackingMiddleware();
        var state = new RunFlowState();
        var called = false;

        await mw.InvokeAsync(new FakeStage("S", RunPhase.Loading), state, () =>
        {
            called = true;
            return Task.FromResult(StageResult.Succeeded("S"));
        });

        Assert.True(called);
    }

    // --- Helpers ---

    private sealed class FakeStage : IWorkflowStage
    {
        public string Name { get; }
        public RunPhase Phase { get; }
        public FakeStage(string name, RunPhase phase) { Name = name; Phase = phase; }
        public Task<StageResult> ExecuteAsync(RunFlowState state, CancellationToken ct = default)
            => Task.FromResult(StageResult.Succeeded(Name));
    }
}
