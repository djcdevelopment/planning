using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Farmer.Core.Config;
using Farmer.Core.Contracts;
using Farmer.Core.Layout;
using Farmer.Core.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Farmer.Core.Workflow.Stages;

/// <summary>
/// Phase 7.5 Stream G: pulls per-file source content from the worker VM down to the
/// host-side run directory so retrospective + future readers have a durable record
/// of what the worker actually produced. Without this stage, Farmer's
/// <c>runs/&lt;id&gt;/artifacts/</c> is empty every run — the VM-side project directory
/// is layered/wiped, so by the time anything wants to reason about the code it's
/// already gone. See <c>docs/phase7-stress-findings.md</c> (Gap #3) for the evidence.
///
/// Contract:
/// - Runs after <see cref="CollectStage"/> (which validates the manifest exists) and
///   before <see cref="RetrospectiveStage"/> (which may read from artifacts/).
/// - Re-reads <c>output/manifest.json</c> from the VM; the manifest itself is the
///   source of truth for <c>files_changed</c>. <see cref="CollectStage"/> parses but
///   does not persist it to <see cref="RunFlowState"/>, so we re-read rather than
///   couple the two stages via shared state.
/// - For each file in <c>FilesChanged</c>, bounded by
///   <see cref="RetrospectiveSettings.MaxChangedFiles"/>: read via
///   <see cref="IMappedDriveReader.ReadFileAsync"/> (SSH <c>cat</c>), skip if the
///   decoded byte count exceeds <see cref="RetrospectiveSettings.MaxFileBytes"/>,
///   otherwise write to <c>{runDir}/artifacts/{relative path}</c>.
/// - Writes a small <c>artifacts-index.json</c> at the run-dir root recording what
///   was archived and what was skipped (with reason). Per ADR-007, data is the
///   product — skipped-with-reason is a first-class outcome.
/// - Per-file read errors are logged and recorded in the index; one bad file does
///   not fail the stage. The stage fails only if the manifest can't be loaded or
///   a host-side disk write throws (truly pathological).
///
/// Text-only v1: <see cref="IMappedDriveReader.ReadFileAsync"/> uses <c>cat</c>, so
/// binary files will land as UTF-8-decoded garbage. A follow-up will add SCP-based
/// binary download once a sample plan produces binaries. Most source files are text,
/// which is the population we care about for retrospective reasoning today.
/// </summary>
public sealed class ArchiveStage : IWorkflowStage
{
    private readonly IMappedDriveReader _reader;
    private readonly RetrospectiveSettings _retroSettings;
    private readonly ILogger<ArchiveStage> _logger;

    private static readonly JsonSerializerOptions ManifestJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
    };

    private static readonly JsonSerializerOptions IndexJsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) }
    };

    public string Name => "Archive";
    public RunPhase Phase => RunPhase.Archiving;

    public ArchiveStage(
        IMappedDriveReader reader,
        IOptions<RetrospectiveSettings> retroSettings,
        ILogger<ArchiveStage> logger)
    {
        _reader = reader;
        _retroSettings = retroSettings.Value;
        _logger = logger;
    }

    public async Task<StageResult> ExecuteAsync(RunFlowState state, CancellationToken ct = default)
    {
        if (state.Vm is null)
            return StageResult.Failed(Name, "No VM assigned");

        var runDir = state.RunDirectory;
        if (string.IsNullOrWhiteSpace(runDir))
        {
            // No on-disk run directory (in-memory test path). Nothing to archive to.
            _logger.LogInformation(
                "ArchiveStage: RunDirectory is null, skipping (in-memory run path)");
            return StageResult.Skipped(Name, "No run directory — nothing to archive to");
        }

        var vm = state.Vm;

        // Re-read the manifest that CollectStage already validated. Coupling via
        // a side-channel in RunFlowState would be cleaner but is out of scope here:
        // Stream G owns ArchiveStage only; CollectStage belongs to Stream F.
        //
        // Resolve via ReaderPathForRunOutput (Stream F helper) so we read from the
        // per-run workspace, NOT the legacy shared /home/claude/projects/output/.
        // Without this, the reader base (RemoteProjectPath) made us read a stale
        // manifest from the old shared dir and every Archive index came back
        // WORKER_NO_CHANGES even when the per-run manifest on the VM had the right
        // file list. See phase7.5 retro + phase-demo dress rehearsal v3.
        Manifest? manifest;
        try
        {
            var manifestRel = RunDirectoryLayout.ReaderPathForRunOutput(vm, state.RunId, "manifest.json");
            var manifestJson = await _reader.ReadFileAsync(vm.Name, manifestRel, ct);
            manifest = JsonSerializer.Deserialize<Manifest>(manifestJson, ManifestJsonOptions);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "ArchiveStage: could not load manifest.json from {Vm}; writing empty index",
                vm.Name);
            await WriteIndexSafeAsync(runDir,
                new ArtifactsIndex { RunId = state.RunId }, ct);
            return StageResult.Succeeded(Name);
        }

        if (manifest is null || manifest.FilesChanged.Count == 0)
        {
            _logger.LogInformation(
                "ArchiveStage: manifest has no files_changed; writing empty index");
            await WriteIndexSafeAsync(runDir,
                new ArtifactsIndex { RunId = state.RunId }, ct);
            return StageResult.Succeeded(Name);
        }

        var artifactsRoot = Path.Combine(runDir, "artifacts");
        Directory.CreateDirectory(artifactsRoot);

        var cap = Math.Max(0, _retroSettings.MaxChangedFiles);
        var candidates = manifest.FilesChanged.Take(cap).ToList();
        var totalListed = manifest.FilesChanged.Count;
        if (totalListed > cap)
        {
            _logger.LogInformation(
                "ArchiveStage: manifest lists {Total} files; capping to {Cap} per MaxChangedFiles",
                totalListed, cap);
        }

        var index = new ArtifactsIndex
        {
            RunId = state.RunId,
            ManifestFilesCount = totalListed,
            MaxChangedFiles = cap,
            MaxFileBytes = _retroSettings.MaxFileBytes,
        };

        foreach (var relPath in candidates)
        {
            ct.ThrowIfCancellationRequested();

            var normalized = NormalizeRelativePath(relPath);
            if (normalized is null)
            {
                _logger.LogWarning(
                    "ArchiveStage: skipping unsafe path from manifest: {Path}", relPath);
                index.Entries.Add(new ArtifactEntry
                {
                    Path = relPath,
                    Status = ArtifactStatus.Skipped,
                    Reason = "unsafe-path",
                });
                continue;
            }

            string content;
            try
            {
                // Manifest paths are relative to the per-run workspace root
                // (worker.sh's PROJECT_ROOT = /home/claude/runs/run-<id>/).
                // The reader's base is vm.RemoteProjectPath (legacy shared
                // /home/claude/projects/), so we walk parent-relative to reach
                // the per-run root before joining the manifest entry.
                var readerPath = RunDirectoryLayout.ReaderPathForRunFile(vm, state.RunId, normalized);
                content = await _reader.ReadFileAsync(vm.Name, readerPath, ct);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    "ArchiveStage: read failed for {Path} on {Vm}: {Error}",
                    normalized, vm.Name, ex.Message);
                index.Entries.Add(new ArtifactEntry
                {
                    Path = normalized,
                    Status = ArtifactStatus.Skipped,
                    Reason = "read-error",
                    Detail = ex.Message,
                });
                continue;
            }

            // We read-then-measure because IMappedDriveReader has no stat primitive
            // and Stream G is forbidden from extending SshWorkerFileReader. For
            // MaxFileBytes=8192 (~8 KB/file × 10 files max) the over-fetch cost is
            // trivial and the code stays simple.
            var bytes = Encoding.UTF8.GetByteCount(content);
            if (bytes > _retroSettings.MaxFileBytes)
            {
                _logger.LogWarning(
                    "ArchiveStage: skipping {Path} — {Bytes} bytes exceeds MaxFileBytes ({Cap})",
                    normalized, bytes, _retroSettings.MaxFileBytes);
                index.Entries.Add(new ArtifactEntry
                {
                    Path = normalized,
                    Status = ArtifactStatus.Skipped,
                    Reason = "too-big",
                    Bytes = bytes,
                });
                continue;
            }

            var destPath = Path.Combine(artifactsRoot, normalized.Replace('/', Path.DirectorySeparatorChar));
            var destDir = Path.GetDirectoryName(destPath);
            if (!string.IsNullOrEmpty(destDir))
                Directory.CreateDirectory(destDir);

            // Overwrite rather than append — idempotent re-runs shouldn't double up.
            await File.WriteAllTextAsync(destPath, content, ct);

            index.Entries.Add(new ArtifactEntry
            {
                Path = normalized,
                Status = ArtifactStatus.Archived,
                Bytes = bytes,
            });
        }

        await WriteIndexSafeAsync(runDir, index, ct);

        var archivedCount = index.Entries.Count(e => e.Status == ArtifactStatus.Archived);
        var skippedCount = index.Entries.Count(e => e.Status == ArtifactStatus.Skipped);
        _logger.LogInformation(
            "ArchiveStage complete for {RunId}: {Archived} archived, {Skipped} skipped (of {Considered} considered, {Total} in manifest)",
            state.RunId, archivedCount, skippedCount, candidates.Count, totalListed);

        return StageResult.Succeeded(Name);
    }

    private async Task WriteIndexSafeAsync(string runDir, ArtifactsIndex index, CancellationToken ct)
    {
        // This IS load-bearing: if we can't persist the index, the caller (orchestrator
        // / retrospective) has no record of what happened. Let the exception surface —
        // the stage's contract says disk-write failures truly fail the run.
        var path = Path.Combine(runDir, "artifacts-index.json");
        var tmp = path + ".tmp";
        var json = JsonSerializer.Serialize(index, IndexJsonOptions);
        await File.WriteAllTextAsync(tmp, json, ct);
        File.Move(tmp, path, overwrite: true);
    }

    /// <summary>
    /// Defensive sanitization: reject any manifest entry that tries to escape the
    /// artifacts root via <c>..</c> or absolute paths. Normalizes backslash to
    /// forward-slash so Windows-style <see cref="Path.Combine"/> output from a
    /// worker doesn't confuse the SSH target. Returns <c>null</c> if the path
    /// should be refused outright.
    /// </summary>
    private static string? NormalizeRelativePath(string relPath)
    {
        if (string.IsNullOrWhiteSpace(relPath)) return null;
        var normalized = relPath.Replace('\\', '/').TrimStart('/');
        if (normalized.Length == 0) return null;
        if (Path.IsPathRooted(normalized)) return null;
        // Split and reject any segment == ".."
        foreach (var segment in normalized.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (segment == "..") return null;
        }
        return normalized;
    }
}

// --- Artifact index model (internal to this stage; consumers read as JSON) ---

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ArtifactStatus
{
    Archived,
    Skipped,
}

public sealed class ArtifactEntry
{
    [JsonPropertyName("path")]
    public string Path { get; set; } = string.Empty;

    [JsonPropertyName("status")]
    public ArtifactStatus Status { get; set; }

    [JsonPropertyName("bytes")]
    public int? Bytes { get; set; }

    /// <summary>
    /// Short reason code when <see cref="Status"/> is <see cref="ArtifactStatus.Skipped"/>:
    /// "too-big", "read-error", "unsafe-path". Null otherwise.
    /// </summary>
    [JsonPropertyName("reason")]
    public string? Reason { get; set; }

    /// <summary>Human-readable detail (exception message, etc.) when available.</summary>
    [JsonPropertyName("detail")]
    public string? Detail { get; set; }
}

public sealed class ArtifactsIndex
{
    [JsonPropertyName("run_id")]
    public string RunId { get; set; } = string.Empty;

    [JsonPropertyName("manifest_files_count")]
    public int ManifestFilesCount { get; set; }

    [JsonPropertyName("max_changed_files")]
    public int MaxChangedFiles { get; set; }

    [JsonPropertyName("max_file_bytes")]
    public int MaxFileBytes { get; set; }

    [JsonPropertyName("generated_at")]
    public DateTimeOffset GeneratedAt { get; set; } = DateTimeOffset.UtcNow;

    [JsonPropertyName("entries")]
    public List<ArtifactEntry> Entries { get; set; } = [];
}
