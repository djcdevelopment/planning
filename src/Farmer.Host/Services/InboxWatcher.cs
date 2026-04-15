using Farmer.Core.Config;
using Farmer.Core.Contracts;
using Farmer.Core.Workflow;
using Microsoft.Extensions.Options;

namespace Farmer.Host.Services;

/// <summary>
/// Background service that polls the inbox directory for trigger files and
/// starts workflow execution. Intentionally single-run sequential processing
/// for Phase 5 — not yet concurrency-safe.
///
/// Builds a fresh workflow + cost tracker per run via WorkflowPipelineFactory,
/// then persists the cost report after completion.
/// </summary>
public sealed class InboxWatcher : BackgroundService
{
    private readonly FarmerSettings _settings;
    private readonly RunDirectoryFactory _runDirFactory;
    private readonly WorkflowPipelineFactory _pipelineFactory;
    private readonly IRunStore _runStore;
    private readonly IVmManager _vmManager;
    private readonly ILogger<InboxWatcher> _logger;

    public InboxWatcher(
        IOptions<FarmerSettings> settings,
        RunDirectoryFactory runDirFactory,
        WorkflowPipelineFactory pipelineFactory,
        IRunStore runStore,
        IVmManager vmManager,
        ILogger<InboxWatcher> logger)
    {
        _settings = settings.Value;
        _runDirFactory = runDirFactory;
        _pipelineFactory = pipelineFactory;
        _runStore = runStore;
        _vmManager = vmManager;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var inboxPath = _settings.Paths.Inbox;
        Directory.CreateDirectory(inboxPath);
        Directory.CreateDirectory(_settings.Paths.Runs);

        _logger.LogInformation("InboxWatcher started, watching {Inbox}", inboxPath);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var files = Directory.GetFiles(inboxPath, "*.json");
                foreach (var file in files)
                {
                    if (stoppingToken.IsCancellationRequested) break;
                    await ProcessInboxFileAsync(file, stoppingToken);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "Error scanning inbox");
            }

            await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);
        }
    }

    private async Task ProcessInboxFileAsync(string filePath, CancellationToken ct)
    {
        var fileName = Path.GetFileName(filePath);
        _logger.LogInformation("Processing inbox file: {File}", fileName);

        string runDir;
        try
        {
            runDir = await _runDirFactory.CreateFromInboxFileAsync(filePath, ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to create run directory from inbox file {File}", fileName);
            // Move bad file to avoid re-processing
            var badPath = filePath + ".bad";
            File.Move(filePath, badPath, overwrite: true);
            return;
        }

        // Delete inbox file after successfully creating run directory
        File.Delete(filePath);

        var runId = Path.GetFileName(runDir);
        _logger.LogInformation("Starting workflow for run {RunId} from inbox file {File}", runId, fileName);

        string? reservedVm = null;
        try
        {
            // Fresh workflow + cost tracker per run — no shared state across runs.
            var (workflow, costTracker) = _pipelineFactory.Create();
            var result = await workflow.ExecuteFromDirectoryAsync(runDir, ct);

            // Track which VM was reserved so we can release it in finally
            var stateJson = await File.ReadAllTextAsync(
                Path.Combine(runDir, "state.json"), ct);
            var vmId = System.Text.Json.JsonDocument.Parse(stateJson)
                .RootElement.TryGetProperty("vm_id", out var v) ? v.GetString() : null;
            reservedVm = vmId;

            var costReport = costTracker.GetReport(result.RunId);
            await _runStore.SaveCostReportAsync(costReport, ct);

            _logger.LogInformation("Workflow {Outcome} for run {RunId}",
                result.Success ? "completed" : "failed", runId);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Workflow threw exception for run {RunId}", runId);

            // Try to find the VM from state.json even on exception
            try
            {
                var statePath = Path.Combine(runDir, "state.json");
                if (File.Exists(statePath))
                {
                    var stateJson = await File.ReadAllTextAsync(statePath, ct);
                    reservedVm = System.Text.Json.JsonDocument.Parse(stateJson)
                        .RootElement.TryGetProperty("vm_id", out var v) ? v.GetString() : null;
                }
            }
            catch { /* best effort */ }
        }
        finally
        {
            // Release the VM back to the pool so the next run can use it
            if (!string.IsNullOrEmpty(reservedVm))
            {
                try
                {
                    await _vmManager.ReleaseAsync(reservedVm, ct);
                    _logger.LogInformation("Released VM {VmName} after run {RunId}", reservedVm, runId);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to release VM {VmName} after run {RunId}", reservedVm, runId);
                }
            }
        }
    }
}
