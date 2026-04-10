using System.Text.Json;
using Farmer.Core.Models;
using Farmer.Core.Workflow;

namespace Farmer.Core.Middleware;

/// <summary>
/// Writes events.jsonl (append-only) and state.json (snapshot) for each stage transition.
/// Only active when RunFlowState.RunDirectory is set. Skips silently otherwise,
/// preserving compatibility with in-memory test paths.
///
/// Anti-drift contract: on any failure path, this middleware MUST advance
/// state.Phase to Failed and set state.LastError BEFORE writing the snapshot,
/// so state.json, events.jsonl, and result.json all agree on the final outcome.
/// RunWorkflow will re-apply the same transition after the middleware returns;
/// the operation is idempotent.
/// </summary>
public sealed class EventingMiddleware : IWorkflowMiddleware
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
    };

    private static readonly JsonSerializerOptions StateJsonOpts = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
    };

    public async Task<StageResult> InvokeAsync(
        IWorkflowStage stage, RunFlowState state,
        Func<Task<StageResult>> next, CancellationToken ct = default)
    {
        if (state.RunDirectory is null)
            return await next();

        await AppendEventAsync(state, stage.Name, "stage.started", null);
        await WriteStateSnapshotAsync(state);

        StageResult result;
        try
        {
            result = await next();
        }
        catch (OperationCanceledException)
        {
            state.LastError = "Operation cancelled";
            state.AdvanceTo(RunPhase.Failed);
            await AppendEventAsync(state, stage.Name, "stage.failed",
                new { outcome = "Cancelled", error = "Operation cancelled" });
            await WriteStateSnapshotAsync(state);
            throw;
        }
        catch (Exception ex)
        {
            state.LastError = ex.Message;
            state.AdvanceTo(RunPhase.Failed);
            await AppendEventAsync(state, stage.Name, "stage.failed",
                new { outcome = "Exception", error = ex.Message });
            await WriteStateSnapshotAsync(state);
            throw;
        }

        // Advance phase + record error BEFORE the snapshot so all three files agree.
        if (result.Outcome == StageOutcome.Failure)
        {
            state.LastError = result.Error;
            state.AdvanceTo(RunPhase.Failed);
        }

        var eventName = result.Outcome switch
        {
            StageOutcome.Success => "stage.completed",
            StageOutcome.Failure => "stage.failed",
            StageOutcome.Skip => "stage.skipped",
            _ => "stage.unknown"
        };

        await AppendEventAsync(state, stage.Name, eventName,
            new { outcome = result.Outcome.ToString(), error = result.Error });
        await WriteStateSnapshotAsync(state);

        return result;
    }

    private static async Task AppendEventAsync(RunFlowState state, string stage, string eventName, object? data)
    {
        var evt = new RunEvent
        {
            RunId = state.RunId,
            Stage = stage,
            Event = eventName,
            Data = data
        };
        var line = JsonSerializer.Serialize(evt, JsonOpts);
        var path = Path.Combine(state.RunDirectory!, "events.jsonl");
        await File.AppendAllTextAsync(path, line + "\n");
    }

    private static async Task WriteStateSnapshotAsync(RunFlowState state)
    {
        var json = JsonSerializer.Serialize(state.ToRunStatus(), StateJsonOpts);
        var path = Path.Combine(state.RunDirectory!, "state.json");
        var tmp = path + ".tmp";
        await File.WriteAllTextAsync(tmp, json);
        File.Move(tmp, path, overwrite: true);
    }
}
