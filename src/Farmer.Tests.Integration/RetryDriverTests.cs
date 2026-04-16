using System.Text.Json;
using Farmer.Core.Config;
using Farmer.Core.Models;
using Farmer.Core.Workflow;
using Farmer.Host.Services;
using Farmer.Messaging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Farmer.Tests.Integration;

/// <summary>
/// Exercises <see cref="RetryDriver"/> end-to-end via the <see cref="IWorkflowRunner"/>
/// seam. No real VM, no real OpenAI. The <see cref="FakeWorkflowRunner"/> returns
/// prefab <see cref="WorkflowResult"/>s per attempt and writes the minimum set of
/// files the driver's artifact-upload path expects to find in the run dir.
///
/// Uses <see cref="NatsServerFixture"/> so <see cref="IRunArtifactStore"/> is real --
/// the driver's upload step is part of what we want to verify doesn't regress.
/// </summary>
public class RetryDriverTests : IClassFixture<NatsServerFixture>, IAsyncDisposable
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        WriteIndented = true,
    };

    private readonly NatsServerFixture _nats;
    private readonly string _runsDir;
    private readonly IRunArtifactStore _artifactStore;
    private readonly RunDirectoryFactory _dirFactory;

    public RetryDriverTests(NatsServerFixture nats)
    {
        _nats = nats;

        _runsDir = Path.Combine(Path.GetTempPath(), "farmer-retry-it-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_runsDir);

        var settings = Options.Create(new FarmerSettings
        {
            Paths = new PathsSettings { Runs = _runsDir, Root = _runsDir },
        });
        _dirFactory = new RunDirectoryFactory(settings);

        // Real NatsRunArtifactStore so uploads land in the fixture's NATS; reuses
        // the infra the driver actually calls in production.
        var natsSettings = Options.Create(new NatsSettings { Url = _nats.Url, Enabled = true });
        var provider = new NatsConnectionProvider(natsSettings, NullLogger<NatsConnectionProvider>.Instance);
        _artifactStore = new NatsRunArtifactStore(provider, NullLogger<NatsRunArtifactStore>.Instance);
    }

    public async ValueTask DisposeAsync()
    {
        try { if (Directory.Exists(_runsDir)) Directory.Delete(_runsDir, recursive: true); } catch { }
        await Task.CompletedTask;
    }

    [Fact]
    public async Task Retries_once_on_Retry_verdict_then_stops_on_Accept()
    {
        // Seed two attempts: first verdicts Retry with structured directives;
        // second verdicts Accept. We assert attempt 2's feedback carries both
        // the plain-string suggestions and the structured-directive section.
        var directives = new List<DirectiveSuggestion>
        {
            new()
            {
                Scope = DirectiveScope.Prompts,
                Target = "1-SetupProject.md",
                SuggestedValue = "Add vitest + react-testing-library install step",
                Rationale = "Tests were missing from prior attempt",
            },
        };
        var runner = new FakeWorkflowRunner(new[]
        {
            VerdictResult(Verdict.Retry, findings: new() { "Missing tests for DataGrid" }, directives: directives),
            VerdictResult(Verdict.Accept),
        });

        var driver = new RetryDriver(_dirFactory, runner, _artifactStore, NullLogger<RetryDriver>.Instance);

        var triggerFile = Path.Combine(_runsDir, "trigger.json");
        var trigger = new
        {
            work_request_name = "demo",
            source = "retry-driver-test",
            worker_mode = "fake",
            retry_policy = new { enabled = true, max_attempts = 2, retry_on_verdicts = new[] { "Retry" } },
        };
        await File.WriteAllTextAsync(triggerFile, JsonSerializer.Serialize(trigger, JsonOpts));

        var attempts = await driver.RunAsync(triggerFile);

        Assert.Equal(2, attempts.Count);
        Assert.Equal(Verdict.Retry, attempts[0].ReviewVerdict?.Verdict);
        Assert.Equal(Verdict.Accept, attempts[1].ReviewVerdict?.Verdict);

        // Attempt 2's request.json must have parent_run_id linking to attempt 1 and
        // feedback populated by FeedbackBuilder.
        var run2Dir = Path.Combine(_runsDir, attempts[1].RunId);
        var req2Json = await File.ReadAllTextAsync(Path.Combine(run2Dir, "request.json"));
        var req2 = JsonSerializer.Deserialize<RunRequest>(req2Json, JsonOpts);

        Assert.NotNull(req2);
        Assert.Equal(attempts[0].RunId, req2!.ParentRunId);
        Assert.NotNull(req2.Feedback);
        Assert.Contains("Missing tests for DataGrid", req2.Feedback!);
        // Structured directive tokens made it through RunFlowState -> WorkflowResult
        // -> FeedbackBuilder into the retry's 0-feedback.md equivalent.
        Assert.Contains("## Specific directives", req2.Feedback);
        Assert.Contains("[Prompts -> 1-SetupProject.md]", req2.Feedback);
        Assert.Contains("Tests were missing from prior attempt", req2.Feedback);
    }

    [Fact]
    public async Task Short_circuits_when_verdict_is_null()
    {
        // Simulates the AutoPass path: retrospective returned no verdict (no OpenAI key,
        // API error, etc). Driver must not loop -- retry requires a verdict to evaluate.
        var runner = new FakeWorkflowRunner(new[]
        {
            new WorkflowResult
            {
                RunId = "test-run-1",
                Success = true,
                FinalPhase = RunPhase.Complete,
                ReviewVerdict = null,
            },
        });

        var driver = new RetryDriver(_dirFactory, runner, _artifactStore, NullLogger<RetryDriver>.Instance);

        var triggerFile = Path.Combine(_runsDir, "trigger-short.json");
        var trigger = new
        {
            work_request_name = "demo",
            source = "retry-driver-test-short",
            worker_mode = "fake",
            retry_policy = new { enabled = true, max_attempts = 3, retry_on_verdicts = new[] { "Retry" } },
        };
        await File.WriteAllTextAsync(triggerFile, JsonSerializer.Serialize(trigger, JsonOpts));

        var attempts = await driver.RunAsync(triggerFile);

        Assert.Single(attempts);
        Assert.Null(attempts[0].ReviewVerdict);
    }

    // --- Helpers ---

    private static WorkflowResult VerdictResult(
        Verdict v,
        List<string>? findings = null,
        IReadOnlyList<DirectiveSuggestion>? directives = null) => new()
    {
        Success = true,
        FinalPhase = RunPhase.Complete,
        ReviewVerdict = new ReviewVerdict
        {
            Verdict = v,
            RiskScore = v == Verdict.Accept ? 10 : 55,
            Findings = findings ?? new List<string>(),
            Suggestions = new List<string>(),
        },
        DirectiveSuggestions = directives ?? Array.Empty<DirectiveSuggestion>(),
    };

    /// <summary>
    /// Test double <see cref="IWorkflowRunner"/>. Pops a prefab result per call and
    /// materializes the minimum files (request.json from driver, result.json from us)
    /// so the driver's downstream path -- read policy from request.json, upload artifacts
    /// in the run dir -- has something to work with.
    /// </summary>
    private sealed class FakeWorkflowRunner : IWorkflowRunner
    {
        private readonly Queue<WorkflowResult> _results;
        private int _callCount;

        public FakeWorkflowRunner(IEnumerable<WorkflowResult> results) => _results = new(results);

        public async Task<WorkflowResult> ExecuteFromDirectoryAsync(string runDir, CancellationToken ct = default)
        {
            _callCount++;
            if (_results.Count == 0)
                throw new InvalidOperationException($"FakeWorkflowRunner called {_callCount}x but queue is empty.");

            // Read the RunId that RunDirectoryFactory baked into request.json; reuse it
            // so attempts[i].RunId actually corresponds to the {runId}/ directory on disk.
            var reqJson = await File.ReadAllTextAsync(Path.Combine(runDir, "request.json"), ct);
            var req = JsonSerializer.Deserialize<RunRequest>(reqJson, JsonOpts)!;

            var result = _results.Dequeue();
            result.RunId = req.RunId;
            result.Attempt = req.AttemptId;
            if (result.ReviewVerdict is not null) result.ReviewVerdict.RunId = req.RunId;

            // Minimal on-disk footprint the driver's artifact-upload step expects.
            await File.WriteAllTextAsync(Path.Combine(runDir, "result.json"),
                JsonSerializer.Serialize(result, JsonOpts), ct);
            await File.WriteAllTextAsync(Path.Combine(runDir, "events.jsonl"),
                "{\"event\":\"fake\"}\n", ct);

            return result;
        }
    }
}
