using Farmer.Core.Models;
using Farmer.Core.Workflow;

namespace Farmer.Tests.TestHelpers;

/// <summary>
/// Test-only IWorkflowStage that wraps a lambda. Lifted from a private nested class
/// in RunFromDirectoryTests so multiple test files can share the helper without
/// re-declaring it.
/// </summary>
public sealed class LambdaStage : IWorkflowStage
{
    private readonly Func<RunFlowState, Task<StageResult>> _execute;
    public string Name { get; }
    public RunPhase Phase { get; }

    public LambdaStage(string name, RunPhase phase, Func<RunFlowState, Task<StageResult>> execute)
    {
        Name = name;
        Phase = phase;
        _execute = execute;
    }

    public Task<StageResult> ExecuteAsync(RunFlowState state, CancellationToken ct = default)
        => _execute(state);
}
