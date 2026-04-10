using System.Diagnostics;
using Farmer.Core.Models;
using Farmer.Core.Workflow;

namespace Farmer.Core.Middleware;

public sealed class CostTrackingMiddleware : IWorkflowMiddleware
{
    private readonly List<StageCost> _stageCosts = [];
    private readonly Stopwatch _totalStopwatch = new();

    public async Task<StageResult> InvokeAsync(
        IWorkflowStage stage,
        RunFlowState state,
        Func<Task<StageResult>> next,
        CancellationToken ct = default)
    {
        if (!_totalStopwatch.IsRunning)
            _totalStopwatch.Start();

        var startedAt = DateTimeOffset.UtcNow;
        var sw = Stopwatch.StartNew();

        var result = await next();

        sw.Stop();

        _stageCosts.Add(new StageCost
        {
            StageName = stage.Name,
            DurationSeconds = sw.Elapsed.TotalSeconds,
            StartedAt = startedAt,
            CompletedAt = DateTimeOffset.UtcNow
        });

        return result;
    }

    /// <summary>
    /// Phase 5 compromise: reset state between runs when registered as singleton.
    /// Not concurrency-safe — acceptable for sequential InboxWatcher processing.
    /// </summary>
    public void Reset()
    {
        _stageCosts.Clear();
        _totalStopwatch.Reset();
    }

    public CostReport GetReport(string runId)
    {
        _totalStopwatch.Stop();

        return new CostReport
        {
            RunId = runId,
            TotalDurationSeconds = _totalStopwatch.Elapsed.TotalSeconds,
            Stages = new List<StageCost>(_stageCosts),
            GeneratedAt = DateTimeOffset.UtcNow
        };
    }
}
