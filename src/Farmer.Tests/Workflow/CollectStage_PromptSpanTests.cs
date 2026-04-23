using System.Diagnostics;
using System.Text.Json;
using Farmer.Core.Config;
using Farmer.Core.Contracts;
using Farmer.Core.Layout;
using Farmer.Core.Models;
using Farmer.Core.Telemetry;
using Farmer.Core.Workflow;
using Farmer.Core.Workflow.Stages;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Farmer.Tests.Workflow;

/// <summary>
/// Verifies CollectStage's SSH-boundary trace reconstruction: it reads
/// <c>output/per-prompt-timing.jsonl</c> (written by worker.sh) and emits
/// one back-dated <c>worker.prompt</c> span per entry via FarmerActivitySource.
/// </summary>
public class CollectStage_PromptSpanTests
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    private static VmConfig MakeVm() => new()
    {
        Name = "spy-vm",
        SshHost = "spy-vm",
        SshUser = "claude",
        MappedDriveLetter = "N",
        RemoteProjectPath = "/home/claude/projects",
        RemoteRunsRoot = "/home/claude/runs",
    };

    private static CollectStage MakeStage(MockMappedDriveReader reader) =>
        new(reader, new MockRunStore(), Options.Create(new FarmerSettings()), NullLogger<CollectStage>.Instance);

    /// <summary>
    /// Phase 7.5 Stream F: CollectStage looks up files under the per-run
    /// workspace, expressed to the reader as a parent-relative walk from
    /// <see cref="VmConfig.RemoteProjectPath"/>. Shared helper so each test
    /// seeds files at the exact key the stage will query.
    /// </summary>
    private static string ReaderKey(VmConfig vm, string runId, string file) =>
        RunDirectoryLayout.ReaderPathForRunOutput(vm, runId, file);

    /// <summary>Subscribes to FarmerActivitySource + returns captured activities; Dispose stops the listener.</summary>
    private sealed class SpanCapture : IDisposable
    {
        public List<Activity> Stopped { get; } = new();
        private readonly ActivityListener _listener;

        public SpanCapture()
        {
            _listener = new ActivityListener
            {
                ShouldListenTo = source => source.Name == FarmerActivitySource.Name,
                Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
                ActivityStopped = a => Stopped.Add(a),
            };
            ActivitySource.AddActivityListener(_listener);
        }

        public void Dispose() => _listener.Dispose();
    }

    private static string ValidManifestJson() => JsonSerializer.Serialize(
        new Manifest { RunId = "run-1", BranchName = "vm-golden-test", FilesChanged = ["src/App.tsx"] },
        JsonOptions);

    private static string TimingEntry(int idx, string filename, int exit, DateTimeOffset start, DateTimeOffset end,
        long stdoutBytes = 42, string mode = "fake")
    {
        var entry = new PromptTimingEntry
        {
            PromptIndex = idx,
            Filename = filename,
            Mode = mode,
            StartTs = start,
            EndTs = end,
            DurationMs = (long)(end - start).TotalMilliseconds,
            ExitCode = exit,
            StdoutBytes = stdoutBytes,
            StderrBytes = 0,
        };
        return JsonSerializer.Serialize(entry, JsonOptions);
    }

    [Fact]
    public async Task Emits_one_worker_prompt_span_per_timing_entry()
    {
        var vm = MakeVm();
        var reader = new MockMappedDriveReader();
        reader.SetFile(ReaderKey(vm, "run-1", "manifest.json"), ValidManifestJson());

        var start = DateTimeOffset.UtcNow.AddMinutes(-5);
        var jsonl = string.Join("\n", new[]
        {
            TimingEntry(1, "1-Setup.md",  exit: 0, start.AddSeconds(0),  start.AddSeconds(10)),
            TimingEntry(2, "2-Build.md",  exit: 0, start.AddSeconds(10), start.AddSeconds(30)),
            TimingEntry(3, "3-Tests.md",  exit: 1, start.AddSeconds(30), start.AddSeconds(45)),
        });
        reader.SetFile(ReaderKey(vm, "run-1", "per-prompt-timing.jsonl"), jsonl);

        using var capture = new SpanCapture();

        var result = await MakeStage(reader).ExecuteAsync(
            new RunFlowState { RunId = "run-1", Vm = vm });

        Assert.Equal(StageOutcome.Success, result.Outcome);

        var promptSpans = capture.Stopped.Where(a => a.OperationName == "worker.prompt").ToList();
        Assert.Equal(3, promptSpans.Count);

        Assert.All(promptSpans, s =>
        {
            Assert.Equal("run-1", s.GetTagItem("farmer.run_id"));
            Assert.Equal("fake",  s.GetTagItem("farmer.worker_mode"));
        });

        var indexes = promptSpans.Select(s => (int)s.GetTagItem("farmer.prompt_index")!).OrderBy(i => i).ToArray();
        Assert.Equal(new[] { 1, 2, 3 }, indexes);

        var failing = promptSpans.Single(s => (int)s.GetTagItem("farmer.prompt_index")! == 3);
        Assert.Equal(1, failing.GetTagItem("farmer.exit_code"));
        Assert.Equal(ActivityStatusCode.Error, failing.Status);
    }

    [Fact]
    public async Task Back_dated_span_start_and_duration_match_entries()
    {
        var vm = MakeVm();
        var reader = new MockMappedDriveReader();
        reader.SetFile(ReaderKey(vm, "run-ts", "manifest.json"), ValidManifestJson());

        var start = new DateTimeOffset(2026, 4, 16, 12, 0, 0, TimeSpan.Zero);
        var end   = start.AddSeconds(7);
        reader.SetFile(ReaderKey(vm, "run-ts", "per-prompt-timing.jsonl"), TimingEntry(1, "1-Setup.md", 0, start, end));

        using var capture = new SpanCapture();
        await MakeStage(reader).ExecuteAsync(new RunFlowState { RunId = "run-ts", Vm = vm });

        var span = capture.Stopped.Single(a => a.OperationName == "worker.prompt");
        Assert.Equal(start.UtcDateTime, span.StartTimeUtc);
        // Duration is computed from start/end; allow sub-ms rounding slack.
        Assert.True(Math.Abs((span.Duration - TimeSpan.FromSeconds(7)).TotalMilliseconds) < 2.0,
            $"Expected ~7s duration, got {span.Duration}");
    }

    [Fact]
    public async Task Missing_timing_file_is_silent_noop()
    {
        var vm = MakeVm();
        var reader = new MockMappedDriveReader();
        reader.SetFile(ReaderKey(vm, "run-miss", "manifest.json"), ValidManifestJson());
        // No timing file.

        using var capture = new SpanCapture();
        var result = await MakeStage(reader).ExecuteAsync(
            new RunFlowState { RunId = "run-miss", Vm = vm });

        Assert.Equal(StageOutcome.Success, result.Outcome);
        Assert.DoesNotContain(capture.Stopped, a => a.OperationName == "worker.prompt");
    }

    [Fact]
    public async Task Malformed_line_is_skipped_and_other_lines_processed()
    {
        var vm = MakeVm();
        var reader = new MockMappedDriveReader();
        reader.SetFile(ReaderKey(vm, "run-bad", "manifest.json"), ValidManifestJson());

        var start = DateTimeOffset.UtcNow.AddMinutes(-1);
        var jsonl = string.Join("\n", new[]
        {
            TimingEntry(1, "1-Setup.md", 0, start, start.AddSeconds(2)),
            "this is not JSON {{{",
            TimingEntry(2, "2-Build.md", 0, start.AddSeconds(2), start.AddSeconds(4)),
        });
        reader.SetFile(ReaderKey(vm, "run-bad", "per-prompt-timing.jsonl"), jsonl);

        using var capture = new SpanCapture();
        var result = await MakeStage(reader).ExecuteAsync(
            new RunFlowState { RunId = "run-bad", Vm = vm });

        Assert.Equal(StageOutcome.Success, result.Outcome);
        var promptSpans = capture.Stopped.Where(a => a.OperationName == "worker.prompt").ToList();
        Assert.Equal(2, promptSpans.Count);
    }

    // --- Mocks (kept local to this test file to avoid widening the TestHelpers surface for this one PR) ---

    private sealed class MockMappedDriveReader : IMappedDriveReader
    {
        private readonly Dictionary<string, string> _files = new();
        public void SetFile(string relativePath, string content) => _files[Normalize(relativePath)] = content;

        public Task<string> ReadFileAsync(string vmName, string relativePath, CancellationToken ct = default)
        {
            var key = Normalize(relativePath);
            if (!_files.TryGetValue(key, out var content))
                throw new FileNotFoundException($"Mock file not found: {relativePath}");
            return Task.FromResult(content);
        }

        public Task<bool> FileExistsAsync(string vmName, string relativePath, CancellationToken ct = default)
            => Task.FromResult(_files.ContainsKey(Normalize(relativePath)));

        public Task<string> WaitForFileAsync(string vmName, string relativePath, TimeSpan timeout, CancellationToken ct = default)
            => ReadFileAsync(vmName, relativePath, ct);

        public Task<IReadOnlyList<string>> ListFilesAsync(string vmName, string relativePath, string pattern = "*", CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<string>>(
                _files.Keys.Where(k => k.StartsWith(Normalize(relativePath))).ToList());

        private static string Normalize(string p) => p.Replace('\\', '/');
    }

    private sealed class MockRunStore : IRunStore
    {
        public Task SaveRunRequestAsync(RunRequest r, CancellationToken ct = default) => Task.CompletedTask;
        public Task<RunRequest?> GetRunRequestAsync(string id, CancellationToken ct = default) => Task.FromResult<RunRequest?>(null);
        public Task SaveTaskPacketAsync(TaskPacket p, CancellationToken ct = default) => Task.CompletedTask;
        public Task<TaskPacket?> GetTaskPacketAsync(string id, CancellationToken ct = default) => Task.FromResult<TaskPacket?>(null);
        public Task SaveRunStateAsync(RunStatus s, CancellationToken ct = default) => Task.CompletedTask;
        public Task<RunStatus?> GetRunStateAsync(string id, CancellationToken ct = default) => Task.FromResult<RunStatus?>(null);
        public Task SaveCostReportAsync(CostReport r, CancellationToken ct = default) => Task.CompletedTask;
        public Task SaveReviewVerdictAsync(ReviewVerdict v, CancellationToken ct = default) => Task.CompletedTask;
        public Task<IReadOnlyList<string>> ListRunIdsAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>());
    }
}
