using Farmer.Core.Models;
using Farmer.Core.Workflow;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Farmer.Tests.Workflow;

public class RunWorkflowTests
{
    private readonly ILogger<RunWorkflow> _logger = NullLogger<RunWorkflow>.Instance;

    private static RunFlowState MakeState(string workRequest = "test-request") => new()
    {
        WorkRequestName = workRequest,
        Attempt = 1
    };

    [Fact]
    public async Task AllStages_ExecuteInOrder()
    {
        var executionOrder = new List<string>();

        var stages = new IWorkflowStage[]
        {
            new SpyStage("A", RunPhase.Created, executionOrder),
            new SpyStage("B", RunPhase.Loading, executionOrder),
            new SpyStage("C", RunPhase.Reserving, executionOrder),
        };

        var workflow = new RunWorkflow(stages, _logger);
        var state = MakeState();

        var result = await workflow.ExecuteAsync(state);

        Assert.True(result.Success);
        Assert.Equal(new[] { "A", "B", "C" }, executionOrder);
    }

    [Fact]
    public async Task AllStages_RecordedInStagesCompleted()
    {
        var stages = new IWorkflowStage[]
        {
            new SpyStage("A", RunPhase.Created, new List<string>()),
            new SpyStage("B", RunPhase.Loading, new List<string>()),
            new SpyStage("C", RunPhase.Reserving, new List<string>()),
        };

        var workflow = new RunWorkflow(stages, _logger);
        var state = MakeState();

        var result = await workflow.ExecuteAsync(state);

        Assert.Equal(new[] { "A", "B", "C" }, state.StagesCompleted);
        Assert.Equal(new[] { "A", "B", "C" }, result.StagesCompleted);
    }

    [Fact]
    public async Task PhaseTransitions_Correctly()
    {
        var phasesObserved = new List<RunPhase>();

        var stages = new IWorkflowStage[]
        {
            new PhaseCapturingStage("S1", RunPhase.Loading, phasesObserved),
            new PhaseCapturingStage("S2", RunPhase.Delivering, phasesObserved),
            new PhaseCapturingStage("S3", RunPhase.Collecting, phasesObserved),
        };

        var workflow = new RunWorkflow(stages, _logger);
        var state = MakeState();

        await workflow.ExecuteAsync(state);

        Assert.Equal(new[] { RunPhase.Loading, RunPhase.Delivering, RunPhase.Collecting }, phasesObserved);
        Assert.Equal(RunPhase.Complete, state.Phase);
    }

    [Fact]
    public async Task FailedStage_StopsPipeline()
    {
        var executionOrder = new List<string>();

        var stages = new IWorkflowStage[]
        {
            new SpyStage("A", RunPhase.Created, executionOrder),
            new FailingStage("B", RunPhase.Loading, "something broke"),
            new SpyStage("C", RunPhase.Reserving, executionOrder),
        };

        var workflow = new RunWorkflow(stages, _logger);
        var state = MakeState();

        var result = await workflow.ExecuteAsync(state);

        Assert.False(result.Success);
        Assert.Equal("something broke", result.Error);
        Assert.Equal(RunPhase.Failed, state.Phase);
        Assert.Equal(new[] { "A" }, executionOrder); // C never ran
        Assert.Single(state.StagesCompleted); // Only A completed
    }

    [Fact]
    public async Task Exception_InStage_TreatedAsFailure()
    {
        var stages = new IWorkflowStage[]
        {
            new ThrowingStage("A", RunPhase.Loading),
        };

        var workflow = new RunWorkflow(stages, _logger);
        var state = MakeState();

        var result = await workflow.ExecuteAsync(state);

        Assert.False(result.Success);
        Assert.Equal(RunPhase.Failed, state.Phase);
        Assert.Contains("boom", result.Error);
    }

    [Fact]
    public async Task SkippedStage_DoesNotStopPipeline()
    {
        var executionOrder = new List<string>();

        var stages = new IWorkflowStage[]
        {
            new SpyStage("A", RunPhase.Created, executionOrder),
            new SkippingStage("B", RunPhase.Loading),
            new SpyStage("C", RunPhase.Reserving, executionOrder),
        };

        var workflow = new RunWorkflow(stages, _logger);
        var state = MakeState();

        var result = await workflow.ExecuteAsync(state);

        Assert.True(result.Success);
        Assert.Equal(new[] { "A", "C" }, executionOrder);
        // B was skipped, not in StagesCompleted
        Assert.DoesNotContain("B", state.StagesCompleted);
    }

    [Fact]
    public async Task EmptyPipeline_Succeeds()
    {
        var workflow = new RunWorkflow(Array.Empty<IWorkflowStage>(), _logger);
        var state = MakeState();

        var result = await workflow.ExecuteAsync(state);

        Assert.True(result.Success);
        Assert.Equal(RunPhase.Complete, state.Phase);
    }

    [Fact]
    public async Task Result_ContainsDuration()
    {
        var stages = new IWorkflowStage[]
        {
            new SpyStage("A", RunPhase.Created, new List<string>()),
        };

        var workflow = new RunWorkflow(stages, _logger);
        var state = MakeState();

        var result = await workflow.ExecuteAsync(state);

        Assert.True(result.DurationSeconds >= 0);
        Assert.True(result.CompletedAt >= result.StartedAt);
    }

    [Fact]
    public async Task Cancellation_PropagatesThrough()
    {
        var cts = new CancellationTokenSource();
        cts.Cancel();

        var stages = new IWorkflowStage[]
        {
            new SpyStage("A", RunPhase.Created, new List<string>()),
        };

        var workflow = new RunWorkflow(stages, _logger);
        var state = MakeState();

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => workflow.ExecuteAsync(state, cts.Token));
    }

    // --- Test doubles ---

    private sealed class SpyStage : IWorkflowStage
    {
        private readonly List<string> _log;
        public string Name { get; }
        public RunPhase Phase { get; }

        public SpyStage(string name, RunPhase phase, List<string> log)
        {
            Name = name;
            Phase = phase;
            _log = log;
        }

        public Task<StageResult> ExecuteAsync(RunFlowState state, CancellationToken ct = default)
        {
            _log.Add(Name);
            return Task.FromResult(StageResult.Succeeded(Name));
        }
    }

    private sealed class PhaseCapturingStage : IWorkflowStage
    {
        private readonly List<RunPhase> _phases;
        public string Name { get; }
        public RunPhase Phase { get; }

        public PhaseCapturingStage(string name, RunPhase phase, List<RunPhase> phases)
        {
            Name = name;
            Phase = phase;
            _phases = phases;
        }

        public Task<StageResult> ExecuteAsync(RunFlowState state, CancellationToken ct = default)
        {
            _phases.Add(state.Phase);
            return Task.FromResult(StageResult.Succeeded(Name));
        }
    }

    private sealed class FailingStage : IWorkflowStage
    {
        private readonly string _error;
        public string Name { get; }
        public RunPhase Phase { get; }

        public FailingStage(string name, RunPhase phase, string error)
        {
            Name = name;
            Phase = phase;
            _error = error;
        }

        public Task<StageResult> ExecuteAsync(RunFlowState state, CancellationToken ct = default)
        {
            return Task.FromResult(StageResult.Failed(Name, _error));
        }
    }

    private sealed class ThrowingStage : IWorkflowStage
    {
        public string Name { get; }
        public RunPhase Phase { get; }

        public ThrowingStage(string name, RunPhase phase)
        {
            Name = name;
            Phase = phase;
        }

        public Task<StageResult> ExecuteAsync(RunFlowState state, CancellationToken ct = default)
        {
            throw new InvalidOperationException("boom");
        }
    }

    private sealed class SkippingStage : IWorkflowStage
    {
        public string Name { get; }
        public RunPhase Phase { get; }

        public SkippingStage(string name, RunPhase phase)
        {
            Name = name;
            Phase = phase;
        }

        public Task<StageResult> ExecuteAsync(RunFlowState state, CancellationToken ct = default)
        {
            return Task.FromResult(StageResult.Skipped(Name, "not needed"));
        }
    }
}
