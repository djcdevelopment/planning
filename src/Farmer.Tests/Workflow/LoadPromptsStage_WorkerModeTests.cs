using System.Text.Json;
using Farmer.Core.Config;
using Farmer.Core.Contracts;
using Farmer.Core.Models;
using Farmer.Core.Workflow;
using Farmer.Core.Workflow.Stages;
using Farmer.Tests.TestHelpers;
using Microsoft.Extensions.Options;
using Xunit;

namespace Farmer.Tests.Workflow;

/// <summary>
/// worker_mode plumbing: config default, per-request override, round-trip through
/// task-packet.json. worker.sh on the VM reads `.worker_mode` to decide whether to
/// invoke Claude CLI or produce canned fake output. Precedence:
///   state.RunRequest.WorkerMode (if set)  >  FarmerSettings.DefaultWorkerMode  >  "real"
/// </summary>
public class LoadPromptsStage_WorkerModeTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _samplePlansDir;
    private readonly InMemoryRunStore _store = new();

    public LoadPromptsStage_WorkerModeTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "farmer-test-" + Guid.NewGuid().ToString("N")[..8]);
        _samplePlansDir = Path.Combine(_tempDir, "sample-plans");
        Directory.CreateDirectory(_samplePlansDir);

        var dir = Path.Combine(_samplePlansDir, "demo-app");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "1-Setup.md"), "# setup");
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    private LoadPromptsStage MakeStage(string defaultWorkerMode)
    {
        var settings = Options.Create(new FarmerSettings
        {
            Paths = new PathsSettings { Data = _tempDir },
            DefaultWorkerMode = defaultWorkerMode,
        });
        return new LoadPromptsStage(settings, _store);
    }

    [Fact]
    public async Task Config_default_applied_when_request_has_no_override()
    {
        var stage = MakeStage(defaultWorkerMode: "fake");
        var state = new RunFlowState
        {
            RunId = "run-1",
            WorkRequestName = "demo-app",
            RunRequest = new RunRequest { RunId = "run-1", WorkRequestName = "demo-app" /* WorkerMode left null */ },
        };

        var result = await stage.ExecuteAsync(state);

        Assert.Equal(StageOutcome.Success, result.Outcome);
        Assert.Equal("fake", state.TaskPacket!.WorkerMode);
    }

    [Fact]
    public async Task Request_override_wins_over_config_default()
    {
        var stage = MakeStage(defaultWorkerMode: "real");
        var state = new RunFlowState
        {
            RunId = "run-2",
            WorkRequestName = "demo-app",
            RunRequest = new RunRequest { RunId = "run-2", WorkRequestName = "demo-app", WorkerMode = "fake" },
        };

        var result = await stage.ExecuteAsync(state);

        Assert.Equal(StageOutcome.Success, result.Outcome);
        Assert.Equal("fake", state.TaskPacket!.WorkerMode);
    }

    [Fact]
    public async Task Falls_back_to_real_when_both_request_and_config_are_missing()
    {
        var stage = MakeStage(defaultWorkerMode: "");  // simulate a missing/whitespace config value
        var state = new RunFlowState
        {
            RunId = "run-3",
            WorkRequestName = "demo-app",
            // No RunRequest at all — should still fall back safely.
        };

        var result = await stage.ExecuteAsync(state);

        Assert.Equal(StageOutcome.Success, result.Outcome);
        Assert.Equal("real", state.TaskPacket!.WorkerMode);
    }

    [Fact]
    public void TaskPacket_serializes_worker_mode_under_snake_case_key()
    {
        // Worker.sh reads the field via `jq -r '.worker_mode'` — the snake_case contract
        // must hold regardless of C# naming conventions.
        var packet = new TaskPacket
        {
            RunId = "run-4",
            WorkRequestName = "demo-app",
            WorkerMode = "fake",
            BranchName = "vm-golden-demo-app",
        };

        var json = JsonSerializer.Serialize(packet);
        using var doc = JsonDocument.Parse(json);

        Assert.True(doc.RootElement.TryGetProperty("worker_mode", out var prop),
            $"expected 'worker_mode' key in serialized JSON, got: {json}");
        Assert.Equal("fake", prop.GetString());
    }

}
