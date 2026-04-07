using System.Diagnostics;
using Farmer.Core.Middleware;
using Farmer.Core.Models;
using Farmer.Core.Telemetry;
using Farmer.Core.Workflow;
using Xunit;

namespace Farmer.Tests.Middleware;

public class TelemetryMiddlewareTests : IDisposable
{
    private readonly List<Activity> _startedActivities = [];
    private readonly List<Activity> _stoppedActivities = [];
    private readonly ActivityListener _listener;

    public TelemetryMiddlewareTests()
    {
        _listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == FarmerDiagnostics.ActivitySourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStarted = a => _startedActivities.Add(a),
            ActivityStopped = a => _stoppedActivities.Add(a),
        };
        ActivitySource.AddActivityListener(_listener);
    }

    public void Dispose()
    {
        _listener.Dispose();
    }

    [Fact]
    public async Task CreatesActivityWithCorrectName()
    {
        var middleware = new TelemetryMiddleware();
        var stage = new FakeStage("LoadPrompts", RunPhase.Loading);
        var state = new RunFlowState { RunId = "run-test-1" };

        await middleware.InvokeAsync(stage, state,
            () => Task.FromResult(StageResult.Succeeded("LoadPrompts")));

        Assert.Single(_startedActivities);
        Assert.Equal("stage.LoadPrompts", _startedActivities[0].OperationName);
    }

    [Fact]
    public async Task SetsRunIdAndStageNameTags()
    {
        var middleware = new TelemetryMiddleware();
        var stage = new FakeStage("Deliver", RunPhase.Delivering);
        var state = new RunFlowState { RunId = "run-abc-123" };

        await middleware.InvokeAsync(stage, state,
            () => Task.FromResult(StageResult.Succeeded("Deliver")));

        var activity = Assert.Single(_stoppedActivities);
        Assert.Equal("run-abc-123", activity.GetTagItem("farmer.run_id"));
        Assert.Equal("Deliver", activity.GetTagItem("farmer.stage_name"));
        Assert.Equal("Delivering", activity.GetTagItem("farmer.stage_phase"));
        Assert.Equal("Success", activity.GetTagItem("farmer.stage_outcome"));
    }

    [Fact]
    public async Task SetsErrorStatusOnFailure()
    {
        var middleware = new TelemetryMiddleware();
        var stage = new FakeStage("Dispatch", RunPhase.Dispatching);
        var state = new RunFlowState { RunId = "run-fail-1" };

        var result = await middleware.InvokeAsync(stage, state,
            () => Task.FromResult(StageResult.Failed("Dispatch", "SSH timeout")));

        var activity = Assert.Single(_stoppedActivities);
        Assert.Equal(ActivityStatusCode.Error, activity.Status);
        Assert.Equal("SSH timeout", activity.StatusDescription);
        Assert.Equal("Failure", activity.GetTagItem("farmer.stage_outcome"));
        Assert.Equal(StageOutcome.Failure, result.Outcome);
    }

    [Fact]
    public async Task CallsNextDelegate()
    {
        var middleware = new TelemetryMiddleware();
        var stage = new FakeStage("Test", RunPhase.Loading);
        var state = new RunFlowState();
        var nextCalled = false;

        await middleware.InvokeAsync(stage, state, () =>
        {
            nextCalled = true;
            return Task.FromResult(StageResult.Succeeded("Test"));
        });

        Assert.True(nextCalled);
    }

    [Fact]
    public async Task RecordsDurationTag()
    {
        var middleware = new TelemetryMiddleware();
        var stage = new FakeStage("Slow", RunPhase.Executing);
        var state = new RunFlowState { RunId = "run-dur-1" };

        await middleware.InvokeAsync(stage, state, async () =>
        {
            await Task.Delay(10);
            return StageResult.Succeeded("Slow");
        });

        var activity = Assert.Single(_stoppedActivities);
        var durationMs = (double)activity.GetTagItem("farmer.stage_duration_ms")!;
        Assert.True(durationMs >= 0);
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
