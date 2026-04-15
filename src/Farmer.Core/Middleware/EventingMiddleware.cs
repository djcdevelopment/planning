using System.Text.Json;
using Farmer.Core.Contracts;
using Farmer.Core.Models;
using Farmer.Core.Workflow;

namespace Farmer.Core.Middleware;

/// <summary>
/// Writes events.jsonl (append-only) and state.json (snapshot) for each stage transition,
/// and mirrors each event to the configured <see cref="IRunEventPublisher"/> (NATS in
/// production, noop in tests). File writes are skipped when RunFlowState.RunDirectory is
/// null, preserving compatibility with in-memory test paths. Publisher calls fire either way.
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

    private readonly IRunEventPublisher _publisher;

    public EventingMiddleware(IRunEventPublisher? publisher = null)
    {
        _publisher = publisher ?? NoopRunEventPublisher.Instance;
    }

    public async Task<StageResult> InvokeAsync(
        IWorkflowStage stage, RunFlowState state,
        Func<Task<StageResult>> next, CancellationToken ct = default)
    {
        await EmitAsync(state, stage.Name, "stage.started", null, ct);
        if (state.RunDirectory is not null)
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
            await EmitAsync(state, stage.Name, "stage.failed",
                new { outcome = "Cancelled", error = "Operation cancelled" }, ct);
            if (state.RunDirectory is not null)
                await WriteStateSnapshotAsync(state);
            throw;
        }
        catch (Exception ex)
        {
            state.LastError = ex.Message;
            state.AdvanceTo(RunPhase.Failed);
            await EmitAsync(state, stage.Name, "stage.failed",
                new { outcome = "Exception", error = ex.Message }, ct);
            if (state.RunDirectory is not null)
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

        await EmitAsync(state, stage.Name, eventName,
            new { outcome = result.Outcome.ToString(), error = result.Error }, ct);
        if (state.RunDirectory is not null)
            await WriteStateSnapshotAsync(state);

        return result;
    }

    private async Task EmitAsync(RunFlowState state, string stage, string eventName, object? data, CancellationToken ct)
    {
        var evt = new RunEvent
        {
            RunId = state.RunId,
            Stage = stage,
            Event = eventName,
            Data = data
        };
        if (state.RunDirectory is not null)
        {
            var line = JsonSerializer.Serialize(evt, JsonOpts);
            var path = Path.Combine(state.RunDirectory, "events.jsonl");
            await File.AppendAllTextAsync(path, line + "\n", ct);
        }
        await _publisher.PublishAsync(evt, ct);
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
