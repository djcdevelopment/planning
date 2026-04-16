namespace Farmer.Core.Workflow;

/// <summary>
/// Test seam for running a full 7-stage workflow against an on-disk run directory.
/// Production implementation wraps <see cref="WorkflowPipelineFactory"/>; tests
/// supply fakes that return prefabricated <see cref="WorkflowResult"/>s without
/// wiring the entire stage + middleware DI graph.
/// Introduced so <c>RetryDriver</c> can be exercised end-to-end in integration tests.
/// </summary>
public interface IWorkflowRunner
{
    Task<WorkflowResult> ExecuteFromDirectoryAsync(string runDir, CancellationToken ct = default);
}
