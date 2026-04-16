using System.Text.Json;
using Farmer.Core.Middleware;
using Farmer.Core.Models;
using Farmer.Core.Workflow;
using Farmer.Tests.TestHelpers;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Farmer.Tests.Workflow;

public class RunFromDirectoryTests : IDisposable
{
    private readonly string _tempDir;
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
    };

    public RunFromDirectoryTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "farmer-rundir-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
        Directory.CreateDirectory(Path.Combine(_tempDir, "logs"));
        Directory.CreateDirectory(Path.Combine(_tempDir, "artifacts"));
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    private void WriteRequest(string runId = "run-test-001", string workRequestName = "test-app")
    {
        var request = new RunRequest
        {
            RunId = runId,
            TaskId = "task-test-001",
            AttemptId = 1,
            WorkRequestName = workRequestName,
            PromptCount = 0,
            Source = "test"
        };
        var json = JsonSerializer.Serialize(request, JsonOpts);
        File.WriteAllText(Path.Combine(_tempDir, "request.json"), json);
    }

    private static IWorkflowStage SpyStage(string name, RunPhase phase)
    {
        return new LambdaStage(name, phase, _ => Task.FromResult(StageResult.Succeeded(name)));
    }

    [Fact]
    public async Task ProducesResultJson()
    {
        WriteRequest();
        var workflow = new RunWorkflow(
            new[] { SpyStage("A", RunPhase.Created) },
            NullLogger<RunWorkflow>.Instance);

        var result = await workflow.ExecuteFromDirectoryAsync(_tempDir);

        Assert.True(result.Success);
        Assert.True(File.Exists(Path.Combine(_tempDir, "result.json")));

        var resultJson = await File.ReadAllTextAsync(Path.Combine(_tempDir, "result.json"));
        var deserialized = JsonSerializer.Deserialize<WorkflowResult>(resultJson, JsonOpts);
        Assert.NotNull(deserialized);
        Assert.True(deserialized!.Success);
    }

    [Fact]
    public async Task EventingMiddlewareWritesEventsAndState()
    {
        WriteRequest();
        var stages = new IWorkflowStage[]
        {
            SpyStage("Stage1", RunPhase.Created),
            SpyStage("Stage2", RunPhase.Loading),
            SpyStage("Stage3", RunPhase.Reserving),
        };

        var workflow = new RunWorkflow(
            stages,
            NullLogger<RunWorkflow>.Instance,
            new IWorkflowMiddleware[] { new EventingMiddleware() });

        await workflow.ExecuteFromDirectoryAsync(_tempDir);

        // events.jsonl should have 2 lines per stage (started + completed) = 6 lines
        var eventsPath = Path.Combine(_tempDir, "events.jsonl");
        Assert.True(File.Exists(eventsPath));
        var lines = (await File.ReadAllTextAsync(eventsPath))
            .Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(6, lines.Length);

        // Verify first event is stage.started
        var firstEvent = JsonSerializer.Deserialize<RunEvent>(lines[0], JsonOpts);
        Assert.NotNull(firstEvent);
        Assert.Equal("stage.started", firstEvent!.Event);
        Assert.Equal("Stage1", firstEvent.Stage);

        // state.json should exist and reflect final state
        var statePath = Path.Combine(_tempDir, "state.json");
        Assert.True(File.Exists(statePath));
        var stateJson = await File.ReadAllTextAsync(statePath);
        Assert.Contains("run-test-001", stateJson);
    }

    [Fact]
    public async Task FailedStageStillProducesResultAndEvents()
    {
        WriteRequest();
        var stages = new IWorkflowStage[]
        {
            SpyStage("Good", RunPhase.Created),
            new LambdaStage("Bad", RunPhase.Loading,
                _ => Task.FromResult(StageResult.Failed("Bad", "something broke"))),
        };

        var workflow = new RunWorkflow(
            stages,
            NullLogger<RunWorkflow>.Instance,
            new IWorkflowMiddleware[] { new EventingMiddleware() });

        var result = await workflow.ExecuteFromDirectoryAsync(_tempDir);

        Assert.False(result.Success);
        Assert.True(File.Exists(Path.Combine(_tempDir, "result.json")));
        Assert.True(File.Exists(Path.Combine(_tempDir, "events.jsonl")));
        Assert.True(File.Exists(Path.Combine(_tempDir, "state.json")));

        // Events: Good.started, Good.completed, Bad.started, Bad.failed = 4
        var lines = (await File.ReadAllTextAsync(Path.Combine(_tempDir, "events.jsonl")))
            .Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(4, lines.Length);

        // Last event should be stage.failed
        var lastEvent = JsonSerializer.Deserialize<RunEvent>(lines[^1], JsonOpts);
        Assert.Equal("stage.failed", lastEvent!.Event);
    }

    /// <summary>
    /// Bug 1 + Bug 2 regression: on any failure, state.json, events.jsonl, and
    /// result.json must agree that the run is in the Failed phase. Previously
    /// state.json froze on the in-flight phase (e.g. "Delivering") while
    /// result.json correctly reported "Failed", and events.jsonl was missing
    /// the closing stage.failed event when the failure was an exception.
    /// </summary>
    [Fact]
    public async Task BugRegression_FailedRun_AllThreeFilesAgreeOnFailedPhase()
    {
        WriteRequest();
        var stages = new IWorkflowStage[]
        {
            SpyStage("First", RunPhase.Created),
            // Throws inside the middleware chain — exercises the catch path
            new LambdaStage("Exploder", RunPhase.Delivering,
                _ => throw new InvalidOperationException("kaboom")),
        };

        var workflow = new RunWorkflow(
            stages,
            NullLogger<RunWorkflow>.Instance,
            new IWorkflowMiddleware[] { new EventingMiddleware() });

        var result = await workflow.ExecuteFromDirectoryAsync(_tempDir);

        // result.json reports Failed
        Assert.False(result.Success);
        Assert.Equal(RunPhase.Failed, result.FinalPhase);

        // state.json must also report Failed (Bug 1)
        var stateJson = await File.ReadAllTextAsync(Path.Combine(_tempDir, "state.json"));
        var state = JsonSerializer.Deserialize<RunStatus>(stateJson, JsonOpts);
        Assert.NotNull(state);
        Assert.Equal(RunPhase.Failed, state!.Phase);
        Assert.Equal("kaboom", state.Error);

        // events.jsonl must contain a closing stage.failed event for the throwing stage (Bug 2)
        var lines = (await File.ReadAllTextAsync(Path.Combine(_tempDir, "events.jsonl")))
            .Split('\n', StringSplitOptions.RemoveEmptyEntries);
        var lastEvent = JsonSerializer.Deserialize<RunEvent>(lines[^1], JsonOpts);
        Assert.NotNull(lastEvent);
        Assert.Equal("Exploder", lastEvent!.Stage);
        Assert.Equal("stage.failed", lastEvent.Event);

        // result.json's RunId must match what's in events and state (no run-identity drift)
        var resultJson = await File.ReadAllTextAsync(Path.Combine(_tempDir, "result.json"));
        var resultDeserialized = JsonSerializer.Deserialize<WorkflowResult>(resultJson, JsonOpts);
        Assert.Equal(state.RunId, resultDeserialized!.RunId);
        Assert.Equal(state.RunId, lastEvent.RunId);
    }

    /// <summary>
    /// Success-path mirror of Bug 1: on a fully-successful run, state.json must
    /// show phase=Complete and stages_completed must include every stage.
    /// Previously state.json was "one step behind" because EventingMiddleware's
    /// last snapshot ran before RunWorkflow recorded the final stage and advanced
    /// to Complete. Fix: ExecuteFromDirectoryAsync writes a final authoritative
    /// state.json after the workflow returns.
    /// </summary>
    [Fact]
    public async Task BugRegression_SuccessfulRun_FinalStateJsonAgreesWithResult()
    {
        WriteRequest();
        var stages = new IWorkflowStage[]
        {
            SpyStage("Alpha", RunPhase.Created),
            SpyStage("Beta", RunPhase.Loading),
            SpyStage("Gamma", RunPhase.Reviewing),
        };

        var workflow = new RunWorkflow(
            stages,
            NullLogger<RunWorkflow>.Instance,
            new IWorkflowMiddleware[] { new EventingMiddleware() });

        var result = await workflow.ExecuteFromDirectoryAsync(_tempDir);

        Assert.True(result.Success);
        Assert.Equal(RunPhase.Complete, result.FinalPhase);
        Assert.Equal(3, result.StagesCompleted.Count);

        // state.json must show Complete + all 3 stages (not "Reviewing" + 2 stages)
        var stateJson = await File.ReadAllTextAsync(Path.Combine(_tempDir, "state.json"));
        var state = JsonSerializer.Deserialize<RunStatus>(stateJson, JsonOpts);
        Assert.NotNull(state);
        Assert.Equal(RunPhase.Complete, state!.Phase);
        Assert.Equal(3, state.StagesCompleted.Count);
        Assert.Contains("Alpha", state.StagesCompleted);
        Assert.Contains("Beta", state.StagesCompleted);
        Assert.Contains("Gamma", state.StagesCompleted);
        Assert.Null(state.Error);

        // result.json must agree
        var resultJson = await File.ReadAllTextAsync(Path.Combine(_tempDir, "result.json"));
        var resultDeserialized = JsonSerializer.Deserialize<WorkflowResult>(resultJson, JsonOpts);
        Assert.Equal(state.Phase, resultDeserialized!.FinalPhase);
        Assert.Equal(state.StagesCompleted.Count, resultDeserialized.StagesCompleted.Count);
    }

    /// <summary>
    /// Bug 1 regression for the StageOutcome.Failure path (no exception).
    /// </summary>
    [Fact]
    public async Task BugRegression_FailedStageResult_StateAgreesWithResult()
    {
        WriteRequest();
        var stages = new IWorkflowStage[]
        {
            SpyStage("Good", RunPhase.Created),
            new LambdaStage("Refuses", RunPhase.Delivering,
                _ => Task.FromResult(StageResult.Failed("Refuses", "nope"))),
        };

        var workflow = new RunWorkflow(
            stages,
            NullLogger<RunWorkflow>.Instance,
            new IWorkflowMiddleware[] { new EventingMiddleware() });

        var result = await workflow.ExecuteFromDirectoryAsync(_tempDir);

        Assert.False(result.Success);
        Assert.Equal(RunPhase.Failed, result.FinalPhase);

        var stateJson = await File.ReadAllTextAsync(Path.Combine(_tempDir, "state.json"));
        var state = JsonSerializer.Deserialize<RunStatus>(stateJson, JsonOpts);
        Assert.Equal(RunPhase.Failed, state!.Phase);
        Assert.Equal("nope", state.Error);
    }

    [Fact]
    public async Task LifecycleConsistency_EventsRecordAllTransitions()
    {
        WriteRequest();
        var stages = new IWorkflowStage[]
        {
            SpyStage("A", RunPhase.Created),
            SpyStage("B", RunPhase.Loading),
        };

        var workflow = new RunWorkflow(
            stages,
            NullLogger<RunWorkflow>.Instance,
            new IWorkflowMiddleware[] { new EventingMiddleware() });

        await workflow.ExecuteFromDirectoryAsync(_tempDir);

        // events.jsonl should have 4 lines: A.started, A.completed, B.started, B.completed
        var lines = (await File.ReadAllTextAsync(Path.Combine(_tempDir, "events.jsonl")))
            .Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(4, lines.Length);

        var events = lines.Select(l => JsonSerializer.Deserialize<RunEvent>(l, JsonOpts)!).ToList();
        Assert.Equal("stage.started", events[0].Event);
        Assert.Equal("A", events[0].Stage);
        Assert.Equal("stage.completed", events[1].Event);
        Assert.Equal("A", events[1].Stage);
        Assert.Equal("stage.started", events[2].Event);
        Assert.Equal("B", events[2].Stage);
        Assert.Equal("stage.completed", events[3].Event);
        Assert.Equal("B", events[3].Stage);

        // state.json should exist and contain the run_id
        var stateJson = await File.ReadAllTextAsync(Path.Combine(_tempDir, "state.json"));
        Assert.Contains("run-test-001", stateJson);

        // result.json should exist for terminal run
        Assert.True(File.Exists(Path.Combine(_tempDir, "result.json")));
    }

    [Fact]
    public async Task DeterministicOutputs_SameInputSameStructure()
    {
        // Run twice with same input, verify structural equivalence
        WriteRequest("run-det-001");
        var stages = new IWorkflowStage[] { SpyStage("Only", RunPhase.Created) };

        var wf1 = new RunWorkflow(stages, NullLogger<RunWorkflow>.Instance,
            new IWorkflowMiddleware[] { new EventingMiddleware() });
        await wf1.ExecuteFromDirectoryAsync(_tempDir);

        var events1 = (await File.ReadAllTextAsync(Path.Combine(_tempDir, "events.jsonl")))
            .Split('\n', StringSplitOptions.RemoveEmptyEntries);

        // Clean and re-run
        File.Delete(Path.Combine(_tempDir, "events.jsonl"));
        File.Delete(Path.Combine(_tempDir, "state.json"));
        File.Delete(Path.Combine(_tempDir, "result.json"));

        var wf2 = new RunWorkflow(stages, NullLogger<RunWorkflow>.Instance,
            new IWorkflowMiddleware[] { new EventingMiddleware() });
        await wf2.ExecuteFromDirectoryAsync(_tempDir);

        var events2 = (await File.ReadAllTextAsync(Path.Combine(_tempDir, "events.jsonl")))
            .Split('\n', StringSplitOptions.RemoveEmptyEntries);

        // Same number of events, same event types and stage names
        Assert.Equal(events1.Length, events2.Length);
        for (int i = 0; i < events1.Length; i++)
        {
            var e1 = JsonSerializer.Deserialize<RunEvent>(events1[i], JsonOpts)!;
            var e2 = JsonSerializer.Deserialize<RunEvent>(events2[i], JsonOpts)!;
            Assert.Equal(e1.Event, e2.Event);
            Assert.Equal(e1.Stage, e2.Stage);
            Assert.Equal(e1.RunId, e2.RunId);
        }
    }

    /// <summary>
}
