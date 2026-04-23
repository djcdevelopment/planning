using System.Diagnostics;
using System.Text.Json;
using Farmer.Core.Config;
using Farmer.Core.Contracts;
using Farmer.Core.Layout;
using Farmer.Core.Models;
using Farmer.Core.Telemetry;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Farmer.Core.Workflow.Stages;

public sealed class CollectStage : IWorkflowStage
{
    private readonly IMappedDriveReader _reader;
    private readonly IRunStore _runStore;
    private readonly FarmerSettings _settings;
    private readonly ILogger<CollectStage> _logger;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
    };

    public string Name => "Collect";
    public RunPhase Phase => RunPhase.Collecting;

    public CollectStage(
        IMappedDriveReader reader,
        IRunStore runStore,
        IOptions<FarmerSettings> settings,
        ILogger<CollectStage> logger)
    {
        _reader = reader;
        _runStore = runStore;
        _settings = settings.Value;
        _logger = logger;
    }

    public async Task<StageResult> ExecuteAsync(RunFlowState state, CancellationToken ct = default)
    {
        if (state.Vm is null)
            return StageResult.Failed(Name, "No VM assigned");

        var vm = state.Vm;
        var timeout = TimeSpan.FromMinutes(2);

        // Phase 7.5 Stream F: read from the per-run workspace at
        // {RemoteRunsRoot}/run-{run_id}/output/ (DeliverStage mkdir'd it,
        // worker.sh populated it). The reader's base path is still
        // {RemoteProjectPath}, so ReaderPathForRunOutput expresses the run
        // output dir as a parent-relative walk that POSIX cat / test -f /
        // ls -1 collapse at read time.
        var manifestRel = RunDirectoryLayout.ReaderPathForRunOutput(vm, state.RunId, "manifest.json");
        var summaryRel  = RunDirectoryLayout.ReaderPathForRunOutput(vm, state.RunId, "summary.json");

        _logger.LogInformation("Waiting for manifest.json on {Vm} run workspace (run_id={RunId})",
            vm.Name, state.RunId);

        string manifestJson;
        try
        {
            manifestJson = await _reader.WaitForFileAsync(vm.Name, manifestRel, timeout, ct);
        }
        catch (TimeoutException)
        {
            return StageResult.Failed(Name,
                $"Timed out waiting for manifest.json in run workspace on {vm.Name}");
        }

        // Parse manifest
        Manifest? manifest;
        try
        {
            manifest = JsonSerializer.Deserialize<Manifest>(manifestJson, JsonOptions);
        }
        catch (JsonException ex)
        {
            return StageResult.Failed(Name, $"Failed to parse manifest.json: {ex.Message}");
        }

        if (manifest is null || manifest.FilesChanged.Count == 0)
            return StageResult.Failed(Name, "manifest.json is empty or has no files_changed");

        // Read summary (optional — don't fail if missing)
        Summary? summary = null;
        if (await _reader.FileExistsAsync(vm.Name, summaryRel, ct))
        {
            try
            {
                var summaryJson = await _reader.ReadFileAsync(vm.Name, summaryRel, ct);
                summary = JsonSerializer.Deserialize<Summary>(summaryJson, JsonOptions);
            }
            catch (Exception ex)
            {
                _logger.LogWarning("Failed to read/parse summary.json: {Error}", ex.Message);
            }
        }

        _logger.LogInformation("Collected from {Vm}: {FileCount} files changed, summary={HasSummary}",
            vm.Name, manifest.FilesChanged.Count, summary is not null);

        // Reconstruct per-prompt timing spans from worker.sh's JSONL log.
        // Best-effort: a missing or malformed log doesn't fail the stage.
        await EmitPromptSpansAsync(vm, state.RunId, ct);

        // Persist to run store — manifest doesn't have its own store method,
        // so we update the run status with collection info
        var status = state.ToRunStatus();
        status.Phase = RunPhase.Collecting;
        await _runStore.SaveRunStateAsync(status, ct);

        return StageResult.Succeeded(Name);
    }

    /// <summary>
    /// Reads <c>output/per-prompt-timing.jsonl</c> from the per-run workspace
    /// and emits one back-dated <c>worker.prompt</c> span per line. worker.sh
    /// writes the file append-only; each line carries ISO-8601 UTC start/end
    /// timestamps so Jaeger renders the spans inside
    /// <c>workflow.stage.Dispatch</c>'s time window. See docs/adr/* for the
    /// "filesystem first" rationale over emitting OTLP from bash.
    /// </summary>
    private async Task EmitPromptSpansAsync(VmConfig vm, string runId, CancellationToken ct)
    {
        var timingRel = RunDirectoryLayout.ReaderPathForRunOutput(vm, runId, "per-prompt-timing.jsonl");
        if (!await _reader.FileExistsAsync(vm.Name, timingRel, ct))
        {
            _logger.LogInformation("No per-prompt-timing.jsonl on {Vm}; skipping span reconstruction.", vm.Name);
            return;
        }

        string jsonl;
        try
        {
            jsonl = await _reader.ReadFileAsync(vm.Name, timingRel, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Failed to read per-prompt-timing.jsonl: {Error}", ex.Message);
            return;
        }

        var lines = jsonl.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        int emitted = 0;
        foreach (var line in lines)
        {
            PromptTimingEntry? entry;
            try
            {
                entry = JsonSerializer.Deserialize<PromptTimingEntry>(line, JsonOptions);
            }
            catch (JsonException ex)
            {
                _logger.LogWarning("Skipping malformed per-prompt-timing line: {Error} | {Line}", ex.Message, line);
                continue;
            }
            if (entry is null) continue;

            var tags = new ActivityTagsCollection
            {
                { "farmer.run_id",          runId },
                { "farmer.prompt_index",    entry.PromptIndex },
                { "farmer.prompt_filename", entry.Filename },
                { "farmer.worker_mode",     entry.Mode },
                { "farmer.exit_code",       entry.ExitCode },
                { "farmer.stdout_bytes",    entry.StdoutBytes },
                { "farmer.stderr_bytes",    entry.StderrBytes },
                { "farmer.duration_ms",     entry.DurationMs },
            };

            // Positional args disambiguate between two overloads that both
            // match the named-args set; this one is (name, kind, parentCtx, tags, links, startTime).
            using var activity = FarmerActivitySource.Source.StartActivity(
                "worker.prompt",
                ActivityKind.Internal,
                default(ActivityContext),
                tags,
                (IEnumerable<ActivityLink>?)null,
                entry.StartTs);
            activity?.SetEndTime(entry.EndTs.UtcDateTime);
            if (entry.ExitCode != 0)
                activity?.SetStatus(ActivityStatusCode.Error, $"exit_code={entry.ExitCode}");

            emitted++;
        }

        _logger.LogInformation("Emitted {Count} worker.prompt spans for {RunId}", emitted, runId);
    }
}
