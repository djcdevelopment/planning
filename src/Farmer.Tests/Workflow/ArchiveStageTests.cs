using System.Text;
using System.Text.Json;
using Farmer.Core.Config;
using Farmer.Core.Contracts;
using Farmer.Core.Models;
using Farmer.Core.Workflow;
using Farmer.Core.Workflow.Stages;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Farmer.Tests.Workflow;

/// <summary>
/// Phase 7.5 Stream G — ArchiveStage unit tests. Covers the happy path, the two
/// skip modes (oversize, read-error), empty manifests, and the disk-relative
/// output layout. The stage's job is to turn Gap #3 from
/// <c>docs/phase7-stress-findings.md</c> (artifacts/ always empty) into a
/// populated directory plus a machine-readable index, so the retro agent and
/// future consumers have real source to reason about.
/// </summary>
public class ArchiveStageTests : IDisposable
{
    private readonly string _runRoot;
    private readonly string _runDir;

    private static readonly JsonSerializerOptions SnakeCaseOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    public ArchiveStageTests()
    {
        _runRoot = Path.Combine(Path.GetTempPath(), "farmer-archive-tests", Guid.NewGuid().ToString("N"));
        _runDir = Path.Combine(_runRoot, "run-under-test");
        Directory.CreateDirectory(_runDir);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_runRoot)) Directory.Delete(_runRoot, recursive: true);
        }
        catch
        {
            // best-effort cleanup; never fail a test on it
        }
    }

    private static VmConfig MakeVm() => new()
    {
        Name = "claudefarm1",
        SshHost = "claudefarm1",
        SshUser = "claude",
        MappedDriveLetter = "N",
        RemoteProjectPath = "/home/claude/projects",
    };

    private static RetrospectiveSettings DefaultRetroSettings(
        int maxFiles = 10, int maxBytes = 8192) => new()
    {
        MaxChangedFiles = maxFiles,
        MaxFileBytes = maxBytes,
    };

    private static ArchiveStage MakeStage(
        MockMappedDriveReader reader,
        RetrospectiveSettings? settings = null)
    {
        return new ArchiveStage(
            reader,
            Options.Create(settings ?? DefaultRetroSettings()),
            NullLogger<ArchiveStage>.Instance);
    }

    private static string ManifestJson(params string[] filesChanged)
    {
        var manifest = new Manifest
        {
            RunId = "run-under-test",
            FilesChanged = filesChanged.ToList(),
        };
        return JsonSerializer.Serialize(manifest, SnakeCaseOptions);
    }

    [Fact]
    public async Task HappyPath_ArchivesAllFilesUnderCap()
    {
        var reader = new MockMappedDriveReader();
        reader.SetFile("output/manifest.json", ManifestJson("src/App.tsx", "package.json"));
        reader.SetFile("src/App.tsx", "export default () => <div/>;");
        reader.SetFile("package.json", "{ \"name\": \"app\" }");

        var stage = MakeStage(reader);
        var state = new RunFlowState
        {
            RunId = "run-under-test",
            Vm = MakeVm(),
            RunDirectory = _runDir,
        };

        var result = await stage.ExecuteAsync(state);

        Assert.Equal(StageOutcome.Success, result.Outcome);

        var appPath = Path.Combine(_runDir, "artifacts", "src", "App.tsx");
        var pkgPath = Path.Combine(_runDir, "artifacts", "package.json");
        Assert.True(File.Exists(appPath), $"expected archived file at {appPath}");
        Assert.True(File.Exists(pkgPath), $"expected archived file at {pkgPath}");
        Assert.Equal("export default () => <div/>;", await File.ReadAllTextAsync(appPath));
        Assert.Equal("{ \"name\": \"app\" }", await File.ReadAllTextAsync(pkgPath));

        var index = ReadIndex();
        Assert.Equal("run-under-test", index.RunId);
        Assert.Equal(2, index.ManifestFilesCount);
        Assert.Equal(2, index.Entries.Count);
        Assert.All(index.Entries, e => Assert.Equal(ArtifactStatus.Archived, e.Status));
    }

    [Fact]
    public async Task RespectsMaxChangedFilesCap()
    {
        var reader = new MockMappedDriveReader();
        reader.SetFile("output/manifest.json", ManifestJson("a.txt", "b.txt", "c.txt", "d.txt"));
        reader.SetFile("a.txt", "a");
        reader.SetFile("b.txt", "b");
        reader.SetFile("c.txt", "c");
        reader.SetFile("d.txt", "d");

        var stage = MakeStage(reader, DefaultRetroSettings(maxFiles: 2));
        var state = new RunFlowState
        {
            RunId = "run-under-test",
            Vm = MakeVm(),
            RunDirectory = _runDir,
        };

        var result = await stage.ExecuteAsync(state);

        Assert.Equal(StageOutcome.Success, result.Outcome);

        // Only the first 2 should be archived; c.txt / d.txt should NOT exist on disk
        // and should NOT appear in the index.
        Assert.True(File.Exists(Path.Combine(_runDir, "artifacts", "a.txt")));
        Assert.True(File.Exists(Path.Combine(_runDir, "artifacts", "b.txt")));
        Assert.False(File.Exists(Path.Combine(_runDir, "artifacts", "c.txt")));
        Assert.False(File.Exists(Path.Combine(_runDir, "artifacts", "d.txt")));

        var index = ReadIndex();
        Assert.Equal(4, index.ManifestFilesCount);
        Assert.Equal(2, index.MaxChangedFiles);
        Assert.Equal(2, index.Entries.Count);
    }

    [Fact]
    public async Task SkipsFilesOverMaxFileBytes()
    {
        var reader = new MockMappedDriveReader();
        reader.SetFile("output/manifest.json", ManifestJson("small.txt", "big.txt"));
        reader.SetFile("small.txt", new string('x', 100));
        reader.SetFile("big.txt", new string('x', 1000));

        var stage = MakeStage(reader, DefaultRetroSettings(maxFiles: 10, maxBytes: 500));
        var state = new RunFlowState
        {
            RunId = "run-under-test",
            Vm = MakeVm(),
            RunDirectory = _runDir,
        };

        var result = await stage.ExecuteAsync(state);

        Assert.Equal(StageOutcome.Success, result.Outcome);
        Assert.True(File.Exists(Path.Combine(_runDir, "artifacts", "small.txt")));
        Assert.False(File.Exists(Path.Combine(_runDir, "artifacts", "big.txt")));

        var index = ReadIndex();
        Assert.Equal(2, index.Entries.Count);
        var small = index.Entries.Single(e => e.Path == "small.txt");
        var big = index.Entries.Single(e => e.Path == "big.txt");
        Assert.Equal(ArtifactStatus.Archived, small.Status);
        Assert.Equal(ArtifactStatus.Skipped, big.Status);
        Assert.Equal("too-big", big.Reason);
        Assert.Equal(1000, big.Bytes);
    }

    [Fact]
    public async Task SkipsFilesThatFailToRead()
    {
        var reader = new MockMappedDriveReader();
        reader.SetFile("output/manifest.json", ManifestJson("ok.txt", "missing.txt"));
        reader.SetFile("ok.txt", "ok");
        // missing.txt deliberately not set — ReadFileAsync will throw FileNotFoundException

        var stage = MakeStage(reader);
        var state = new RunFlowState
        {
            RunId = "run-under-test",
            Vm = MakeVm(),
            RunDirectory = _runDir,
        };

        var result = await stage.ExecuteAsync(state);

        Assert.Equal(StageOutcome.Success, result.Outcome);
        Assert.True(File.Exists(Path.Combine(_runDir, "artifacts", "ok.txt")));
        Assert.False(File.Exists(Path.Combine(_runDir, "artifacts", "missing.txt")));

        var index = ReadIndex();
        Assert.Equal(2, index.Entries.Count);
        var okEntry = index.Entries.Single(e => e.Path == "ok.txt");
        var missing = index.Entries.Single(e => e.Path == "missing.txt");
        Assert.Equal(ArtifactStatus.Archived, okEntry.Status);
        Assert.Equal(ArtifactStatus.Skipped, missing.Status);
        Assert.Equal("read-error", missing.Reason);
        Assert.False(string.IsNullOrEmpty(missing.Detail));
    }

    [Fact]
    public async Task EmptyManifest_SucceedsWithEmptyIndex()
    {
        var reader = new MockMappedDriveReader();
        reader.SetFile("output/manifest.json", ManifestJson(/* no files */));

        var stage = MakeStage(reader);
        var state = new RunFlowState
        {
            RunId = "run-under-test",
            Vm = MakeVm(),
            RunDirectory = _runDir,
        };

        var result = await stage.ExecuteAsync(state);

        Assert.Equal(StageOutcome.Success, result.Outcome);
        var index = ReadIndex();
        Assert.Empty(index.Entries);
        Assert.Equal(0, index.ManifestFilesCount);
    }

    [Fact]
    public async Task MissingManifest_SucceedsWithEmptyIndex()
    {
        // CollectStage failing would have already aborted the run, but defense in depth:
        // if somehow Archive runs without a manifest on the VM, we log + degrade instead
        // of crashing the pipeline.
        var reader = new MockMappedDriveReader();
        var stage = MakeStage(reader);
        var state = new RunFlowState
        {
            RunId = "run-under-test",
            Vm = MakeVm(),
            RunDirectory = _runDir,
        };

        var result = await stage.ExecuteAsync(state);

        Assert.Equal(StageOutcome.Success, result.Outcome);
        var indexPath = Path.Combine(_runDir, "artifacts-index.json");
        Assert.True(File.Exists(indexPath));
    }

    [Fact]
    public async Task FailsWhenNoVm()
    {
        var reader = new MockMappedDriveReader();
        var stage = MakeStage(reader);
        var state = new RunFlowState { RunId = "x", Vm = null, RunDirectory = _runDir };

        var result = await stage.ExecuteAsync(state);

        Assert.Equal(StageOutcome.Failure, result.Outcome);
        Assert.Contains("No VM", result.Error);
    }

    [Fact]
    public async Task SkippedWhenNoRunDirectory()
    {
        // The in-memory pipeline path (no on-disk run dir) has nothing to archive to.
        // EventingMiddleware skips its file writes the same way. Match that contract.
        var reader = new MockMappedDriveReader();
        var stage = MakeStage(reader);
        var state = new RunFlowState { RunId = "x", Vm = MakeVm(), RunDirectory = null };

        var result = await stage.ExecuteAsync(state);

        Assert.Equal(StageOutcome.Skip, result.Outcome);
    }

    [Fact]
    public async Task RejectsPathTraversalInManifest()
    {
        // Defense: a compromised or buggy worker could try to escape the artifacts
        // root via "../something". Reject with a recorded "unsafe-path" reason.
        var reader = new MockMappedDriveReader();
        reader.SetFile("output/manifest.json",
            ManifestJson("../escape.txt", "good.txt"));
        reader.SetFile("good.txt", "ok");
        reader.SetFile("../escape.txt", "pwned"); // never actually read

        var stage = MakeStage(reader);
        var state = new RunFlowState
        {
            RunId = "run-under-test",
            Vm = MakeVm(),
            RunDirectory = _runDir,
        };

        var result = await stage.ExecuteAsync(state);

        Assert.Equal(StageOutcome.Success, result.Outcome);
        var index = ReadIndex();
        var escape = index.Entries.Single(e => e.Path == "../escape.txt");
        Assert.Equal(ArtifactStatus.Skipped, escape.Status);
        Assert.Equal("unsafe-path", escape.Reason);

        // Sanity: no "escape.txt" (or any file outside artifacts/) was written
        var stray = Path.Combine(_runRoot, "escape.txt");
        Assert.False(File.Exists(stray));
    }

    [Fact]
    public async Task OverwritesExistingArtifactFiles()
    {
        // Idempotency: if a prior attempt left a stale artifact at the same path,
        // we overwrite (not append). Matches the ADR-007 "runs are immutable after
        // complete but stages within a run may re-execute" contract.
        var reader = new MockMappedDriveReader();
        reader.SetFile("output/manifest.json", ManifestJson("foo.txt"));
        reader.SetFile("foo.txt", "new content");

        var artifactsDir = Path.Combine(_runDir, "artifacts");
        Directory.CreateDirectory(artifactsDir);
        await File.WriteAllTextAsync(Path.Combine(artifactsDir, "foo.txt"), "stale content");

        var stage = MakeStage(reader);
        var state = new RunFlowState
        {
            RunId = "run-under-test",
            Vm = MakeVm(),
            RunDirectory = _runDir,
        };

        var result = await stage.ExecuteAsync(state);

        Assert.Equal(StageOutcome.Success, result.Outcome);
        Assert.Equal("new content",
            await File.ReadAllTextAsync(Path.Combine(artifactsDir, "foo.txt")));
    }

    [Fact]
    public async Task PhaseIsArchiving()
    {
        var reader = new MockMappedDriveReader();
        var stage = MakeStage(reader);
        Assert.Equal(RunPhase.Archiving, stage.Phase);
        Assert.Equal("Archive", stage.Name);
        await Task.CompletedTask;
    }

    // --- Helpers ---

    private ArtifactsIndex ReadIndex()
    {
        var path = Path.Combine(_runDir, "artifacts-index.json");
        Assert.True(File.Exists(path), $"expected artifacts-index.json at {path}");
        var json = File.ReadAllText(path);
        return JsonSerializer.Deserialize<ArtifactsIndex>(json, SnakeCaseOptions)!;
    }

    // --- Test doubles ---

    /// <summary>
    /// Minimal in-memory <see cref="IMappedDriveReader"/>. Mirrors the shape of
    /// <c>CollectStageTests.MockMappedDriveReader</c> so callers can match patterns
    /// across the suite without coupling the classes directly.
    /// </summary>
    private sealed class MockMappedDriveReader : IMappedDriveReader
    {
        private readonly Dictionary<string, string> _files = new();

        public void SetFile(string relativePath, string content)
        {
            _files[Normalize(relativePath)] = content;
        }

        public Task<string> ReadFileAsync(string vmName, string relativePath, CancellationToken ct = default)
        {
            var key = Normalize(relativePath);
            if (!_files.TryGetValue(key, out var content))
                throw new FileNotFoundException($"Mock file not found: {relativePath}", relativePath);
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
}
