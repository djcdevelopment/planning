using System.Text.Json;
using Farmer.Core.Config;
using Farmer.Core.Contracts;
using Farmer.Core.Models;
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

        // Wait for manifest.json to appear on mapped drive
        _logger.LogInformation("Waiting for manifest.json on {Vm} mapped drive", vm.Name);

        string manifestJson;
        try
        {
            manifestJson = await _reader.WaitForFileAsync(vm.Name,
                Path.Combine("output", "manifest.json"), timeout, ct);
        }
        catch (TimeoutException)
        {
            return StageResult.Failed(Name,
                $"Timed out waiting for output/manifest.json on {vm.Name} mapped drive");
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
        if (await _reader.FileExistsAsync(vm.Name, Path.Combine("output", "summary.json"), ct))
        {
            try
            {
                var summaryJson = await _reader.ReadFileAsync(vm.Name,
                    Path.Combine("output", "summary.json"), ct);
                summary = JsonSerializer.Deserialize<Summary>(summaryJson, JsonOptions);
            }
            catch (Exception ex)
            {
                _logger.LogWarning("Failed to read/parse summary.json: {Error}", ex.Message);
            }
        }

        _logger.LogInformation("Collected from {Vm}: {FileCount} files changed, summary={HasSummary}",
            vm.Name, manifest.FilesChanged.Count, summary is not null);

        // Persist to run store — manifest doesn't have its own store method,
        // so we update the run status with collection info
        var status = state.ToRunStatus();
        status.Phase = RunPhase.Collecting;
        await _runStore.SaveRunStateAsync(status, ct);

        return StageResult.Succeeded(Name);
    }
}
