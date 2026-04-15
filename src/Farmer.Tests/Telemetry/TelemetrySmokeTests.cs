using System.Diagnostics;
using Farmer.Core.Middleware;
using Farmer.Core.Models;
using Farmer.Core.Telemetry;
using Farmer.Core.Workflow;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Farmer.Tests.Telemetry;

public class TelemetrySmokeTests
{
    [Fact]
    public async Task TelemetryMiddleware_EmitsStageActivities()
    {
        var captured = new List<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = s => s.Name == FarmerActivitySource.Name,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStarted = a => captured.Add(a),
        };
        ActivitySource.AddActivityListener(listener);

        var stages = new IWorkflowStage[]
        {
            new SpyStage("StageA", RunPhase.Created),
            new SpyStage("StageB", RunPhase.Loading),
        };

        var workflow = new RunWorkflow(
            stages,
            NullLogger<RunWorkflow>.Instance,
            new IWorkflowMiddleware[] { new TelemetryMiddleware() });

        var state = new RunFlowState { RunId = "run-telemetry-test", WorkRequestName = "test" };
        await workflow.ExecuteAsync(state);

        // Should have captured activities for both stages
        var stageActivities = captured
            .Where(a => a.DisplayName.StartsWith("workflow.stage."))
            .ToList();
        Assert.Equal(2, stageActivities.Count);
        Assert.Contains(stageActivities, a => a.DisplayName == "workflow.stage.StageA");
        Assert.Contains(stageActivities, a => a.DisplayName == "workflow.stage.StageB");

        // Verify tags
        foreach (var activity in stageActivities)
        {
            Assert.Equal("run-telemetry-test", activity.GetTagItem("farmer.run_id")?.ToString());
        }
    }

    [Fact]
    public async Task ExecuteFromDirectory_EmitsRootRunActivity()
    {
        var captured = new List<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = s => s.Name == FarmerActivitySource.Name,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStarted = a => captured.Add(a),
        };
        ActivitySource.AddActivityListener(listener);

        // Create temp run directory with request.json
        var tempDir = Path.Combine(Path.GetTempPath(), "farmer-otel-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(tempDir);
        Directory.CreateDirectory(Path.Combine(tempDir, "logs"));
        Directory.CreateDirectory(Path.Combine(tempDir, "artifacts"));

        try
        {
            var request = new RunRequest
            {
                RunId = "run-otel-001",
                TaskId = "task-otel-001",
                WorkRequestName = "test",
                Source = "test"
            };
            var json = System.Text.Json.JsonSerializer.Serialize(request, new System.Text.Json.JsonSerializerOptions
            {
                PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.SnakeCaseLower,
                WriteIndented = true
            });
            await File.WriteAllTextAsync(Path.Combine(tempDir, "request.json"), json);

            var workflow = new RunWorkflow(
                new IWorkflowStage[] { new SpyStage("Only", RunPhase.Created) },
                NullLogger<RunWorkflow>.Instance);

            await workflow.ExecuteFromDirectoryAsync(tempDir);

            // Should have root run activity for this specific run
            var runActivities = captured
                .Where(a => a.DisplayName == "workflow.run" &&
                       a.GetTagItem("farmer.run_id")?.ToString() == "run-otel-001")
                .ToList();
            Assert.Single(runActivities);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void Metrics_AreCreated()
    {
        // Verify metric instruments exist and can record without error
        FarmerMetrics.RunsStarted.Add(1);
        FarmerMetrics.RunsCompleted.Add(1);
        FarmerMetrics.RunsFailed.Add(1);
        FarmerMetrics.StageDuration.Record(42.0,
            new KeyValuePair<string, object?>("stage", "test"),
            new KeyValuePair<string, object?>("outcome", "Success"));
        FarmerMetrics.VmCommandsExecuted.Add(1);
        FarmerMetrics.VmCommandsFailed.Add(1);
        // If we get here without exception, instruments are valid
    }

    private sealed class SpyStage : IWorkflowStage
    {
        public string Name { get; }
        public RunPhase Phase { get; }

        public SpyStage(string name, RunPhase phase) { Name = name; Phase = phase; }

        public Task<StageResult> ExecuteAsync(RunFlowState state, CancellationToken ct = default)
            => Task.FromResult(StageResult.Succeeded(Name));
    }
}
