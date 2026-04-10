using Farmer.Core.Config;
using Farmer.Core.Workflow;
using Microsoft.Extensions.Options;

namespace Farmer.Host.Services;

/// <summary>
/// Background service that polls the inbox directory for trigger files and
/// starts workflow execution. Intentionally single-run sequential processing
/// for Phase 5 — not yet concurrency-safe.
/// </summary>
public sealed class InboxWatcher : BackgroundService
{
    private readonly FarmerSettings _settings;
    private readonly RunDirectoryFactory _runDirFactory;
    private readonly RunWorkflow _workflow;
    private readonly ILogger<InboxWatcher> _logger;

    public InboxWatcher(
        IOptions<FarmerSettings> settings,
        RunDirectoryFactory runDirFactory,
        RunWorkflow workflow,
        ILogger<InboxWatcher> logger)
    {
        _settings = settings.Value;
        _runDirFactory = runDirFactory;
        _workflow = workflow;
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

        try
        {
            var result = await _workflow.ExecuteFromDirectoryAsync(runDir, ct);
            _logger.LogInformation("Workflow {Outcome} for run {RunId}",
                result.Success ? "completed" : "failed", runId);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Workflow threw exception for run {RunId}", runId);
        }
    }
}
