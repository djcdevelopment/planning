using System.Diagnostics;
using Farmer.Core.Workflow;
using Microsoft.Extensions.Logging;

namespace Farmer.Core.Middleware;

public sealed class LoggingMiddleware : IWorkflowMiddleware
{
    private readonly ILogger<LoggingMiddleware> _logger;

    public LoggingMiddleware(ILogger<LoggingMiddleware> logger)
    {
        _logger = logger;
    }

    public async Task<StageResult> InvokeAsync(
        IWorkflowStage stage,
        RunFlowState state,
        Func<Task<StageResult>> next,
        CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();

        _logger.LogInformation("[Middleware] Stage [{StageName}] executing (run: {RunId})",
            stage.Name, state.RunId);

        var result = await next();

        sw.Stop();

        _logger.LogInformation(
            "[Middleware] Stage [{StageName}] completed: {Outcome} in {Duration:F1}ms",
            stage.Name, result.Outcome, sw.Elapsed.TotalMilliseconds);

        if (result.Outcome == StageOutcome.Failure)
        {
            _logger.LogWarning("[Middleware] Stage [{StageName}] error: {Error}",
                stage.Name, result.Error);
        }

        return result;
    }
}
