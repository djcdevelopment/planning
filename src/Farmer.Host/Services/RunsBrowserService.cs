using System.Text.Json;
using System.Text.Json.Serialization;
using Farmer.Core.Config;
using Farmer.Core.Layout;
using Microsoft.Extensions.Options;

namespace Farmer.Host.Services;

/// <summary>
/// Stream J (Phase Demo) — read-side service that powers the reveal UI. Scans
/// the run directory tree and composes summaries/details from the canonical
/// files (<c>result.json</c>, <c>review.json</c>, <c>request.json</c>,
/// <c>artifacts-index.json</c>, <c>events.jsonl</c>, <c>qa-retro.md</c>,
/// <c>directive-suggestions.md</c>). Stays independent of <see cref="IRunStore"/>
/// so the reveal endpoints can read historical runs whose files were written
/// by older schema versions without deserialization errors breaking the UI.
///
/// All filesystem access is sandboxed to <see cref="PathsSettings.Runs"/>;
/// <see cref="TryResolveRunFile"/> rejects absolute paths, path traversal
/// via <c>..</c>, and any resolved path that escapes the run dir.
/// </summary>
public sealed class RunsBrowserService
{
    private readonly FarmerSettings _settings;

    // Lenient reader: snake_case from FileRunStore, but also allow camelCase/PascalCase
    // that legacy runs may have written. Unknown properties are ignored so a schema
    // bump can't break the demo UI's read-side.
    private static readonly JsonSerializerOptions ReadOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public RunsBrowserService(IOptions<FarmerSettings> settings)
    {
        _settings = settings.Value;
    }

    /// <summary>
    /// Returns recent run summaries, newest first by directory mtime. Runs
    /// whose <c>result.json</c> can't be read are skipped (in-flight or
    /// broken runs shouldn't break the sidebar).
    /// </summary>
    public IReadOnlyList<RunSummary> ListRecent(int take = 20)
    {
        var runsDir = _settings.Paths.Runs;
        if (!Directory.Exists(runsDir))
            return Array.Empty<RunSummary>();

        var candidates = new DirectoryInfo(runsDir)
            .EnumerateDirectories()
            .OrderByDescending(d => d.LastWriteTimeUtc)
            .Take(Math.Max(take, 0))
            .ToList();

        var summaries = new List<RunSummary>(candidates.Count);
        foreach (var dir in candidates)
        {
            var summary = TryLoadSummary(dir.Name);
            if (summary is not null)
                summaries.Add(summary);
        }

        return summaries;
    }

    /// <summary>
    /// Loads a single run's summary from its <c>result.json</c>. Returns null
    /// when the result file is missing or unparseable (still-running or
    /// crashed before persisting the final result).
    /// </summary>
    public RunSummary? TryLoadSummary(string runId)
    {
        if (!IsSafeRunId(runId))
            return null;

        var runDir = RunDirectoryLayout.RunDir(_settings.Paths.Runs, runId);
        if (!Directory.Exists(runDir))
            return null;

        var resultPath = RunDirectoryLayout.RunResultFile(_settings.Paths.Runs, runId);
        if (!File.Exists(resultPath))
            return null;

        ResultDoc? result;
        try
        {
            var json = File.ReadAllText(resultPath);
            result = JsonSerializer.Deserialize<ResultDoc>(json, ReadOptions);
        }
        catch
        {
            return null;
        }
        if (result is null) return null;

        // work_request_name isn't on result.json -- grab it from request.json.
        // user_id likewise lives only on request.json; surface it so the UI can
        // filter run history per caller. Phase Demo v2 Stream 3.
        var requestPath = RunDirectoryLayout.RunRequestFile(_settings.Paths.Runs, runId);
        string? workRequestName = null;
        string? userId = null;
        if (File.Exists(requestPath))
        {
            try
            {
                var reqJson = File.ReadAllText(requestPath);
                var req = JsonSerializer.Deserialize<RequestDoc>(reqJson, ReadOptions);
                workRequestName = req?.WorkRequestName;
                userId = req?.UserId;
            }
            catch { /* optional */ }
        }

        // review.json is the authoritative copy of the verdict; result.json also
        // carries a denormalized copy, but prefer review.json in case a future
        // retry writes an updated verdict without rewriting result.json.
        ReviewDoc? review = null;
        var reviewPath = RunDirectoryLayout.RunReviewFile(_settings.Paths.Runs, runId);
        if (File.Exists(reviewPath))
        {
            try
            {
                var revJson = File.ReadAllText(reviewPath);
                review = JsonSerializer.Deserialize<ReviewDoc>(revJson, ReadOptions);
            }
            catch { /* optional */ }
        }

        return new RunSummary(
            RunId: result.RunId ?? runId,
            WorkRequestName: workRequestName,
            UserId: userId,
            FinalPhase: result.FinalPhase,
            Success: result.Success,
            Verdict: review?.Verdict ?? result.ReviewVerdict?.Verdict,
            RiskScore: review?.RiskScore ?? result.ReviewVerdict?.RiskScore,
            StartedAt: result.StartedAt,
            CompletedAt: result.CompletedAt,
            DurationSeconds: result.DurationSeconds);
    }

    /// <summary>
    /// Full metadata for a single run: summary + paths + artifact listing +
    /// retro/directives markdown content + trace_id (best-effort from
    /// events.jsonl).
    /// </summary>
    public RunDetail? TryLoadDetail(string runId)
    {
        var summary = TryLoadSummary(runId);
        if (summary is null) return null;

        var runsRoot = _settings.Paths.Runs;
        var runDir = RunDirectoryLayout.RunDir(runsRoot, runId);

        var retroPath = Path.Combine(runDir, "qa-retro.md");
        var directivesPath = Path.Combine(runDir, "directive-suggestions.md");

        string? ReadIfExists(string p) => File.Exists(p) ? SafeReadText(p) : null;

        var artifacts = ListArtifacts(runId);
        var files = ListFiles(runId);
        var traceId = TryReadTraceId(runId);

        return new RunDetail(
            Summary: summary,
            RunDir: runDir,
            Files: files,
            Artifacts: artifacts,
            QaRetroMarkdown: ReadIfExists(retroPath),
            DirectivesMarkdown: ReadIfExists(directivesPath),
            TraceId: traceId);
    }

    /// <summary>
    /// Lists files inside the run dir (one level) plus a flag indicating
    /// which ones are directories. Used by the UI to render a simple file list.
    /// </summary>
    public IReadOnlyList<FileEntry> ListFiles(string runId)
    {
        if (!IsSafeRunId(runId)) return Array.Empty<FileEntry>();
        var runDir = RunDirectoryLayout.RunDir(_settings.Paths.Runs, runId);
        if (!Directory.Exists(runDir)) return Array.Empty<FileEntry>();

        var entries = new List<FileEntry>();
        foreach (var path in Directory.EnumerateFileSystemEntries(runDir))
        {
            var name = Path.GetFileName(path);
            var isDir = Directory.Exists(path);
            long? size = isDir ? null : SafeFileSize(path);
            entries.Add(new FileEntry(name, isDir, size));
        }
        return entries
            .OrderByDescending(e => e.IsDirectory)
            .ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// Returns artifact entries from <c>artifacts-index.json</c> when present,
    /// otherwise a filesystem walk of <c>artifacts/</c>. The index is preferred
    /// because it carries the reason/detail for skipped files.
    /// </summary>
    public ArtifactsInfo ListArtifacts(string runId)
    {
        if (!IsSafeRunId(runId))
            return new ArtifactsInfo(Source: "none", Entries: Array.Empty<ArtifactEntry>());

        var runsRoot = _settings.Paths.Runs;
        var runDir = RunDirectoryLayout.RunDir(runsRoot, runId);
        if (!Directory.Exists(runDir))
            return new ArtifactsInfo(Source: "none", Entries: Array.Empty<ArtifactEntry>());

        var indexPath = Path.Combine(runDir, "artifacts-index.json");
        if (File.Exists(indexPath))
        {
            try
            {
                var json = File.ReadAllText(indexPath);
                var doc = JsonSerializer.Deserialize<ArtifactsIndexDoc>(json, ReadOptions);
                if (doc?.Entries is not null)
                {
                    var entries = doc.Entries
                        .Select(e => new ArtifactEntry(
                            Path: e.Path ?? "(unnamed)",
                            Status: e.Status,
                            Bytes: e.Bytes,
                            Reason: e.Reason,
                            Detail: e.Detail))
                        .ToList();
                    return new ArtifactsInfo(Source: "index", Entries: entries);
                }
            }
            catch { /* fall through to FS walk */ }
        }

        var artifactsDir = RunDirectoryLayout.RunArtifactsDir(runsRoot, runId);
        if (!Directory.Exists(artifactsDir))
            return new ArtifactsInfo(Source: "filesystem", Entries: Array.Empty<ArtifactEntry>());

        var walked = new List<ArtifactEntry>();
        foreach (var file in Directory.EnumerateFiles(artifactsDir, "*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(artifactsDir, file).Replace('\\', '/');
            walked.Add(new ArtifactEntry(
                Path: rel,
                Status: "captured",
                Bytes: SafeFileSize(file),
                Reason: null,
                Detail: null));
        }
        walked.Sort((a, b) => StringComparer.OrdinalIgnoreCase.Compare(a.Path, b.Path));
        return new ArtifactsInfo(Source: "filesystem", Entries: walked);
    }

    /// <summary>
    /// Attempts to resolve <c>{runDir}/{relativePath}</c> to an absolute path
    /// safely scoped under the run directory. Returns false for:
    /// <list type="bullet">
    ///   <item>unknown run ids (not a subdir)</item>
    ///   <item>rooted paths (<c>C:\...</c>, <c>/etc/...</c>)</item>
    ///   <item>any path that normalizes outside the run dir (<c>..</c> escapes)</item>
    /// </list>
    /// Directory listings are not served — <paramref name="fullPath"/> is set
    /// only when the resolved target is an existing file.
    /// </summary>
    public bool TryResolveRunFile(string runId, string relativePath, out string fullPath)
    {
        fullPath = string.Empty;
        if (!IsSafeRunId(runId)) return false;
        if (string.IsNullOrWhiteSpace(relativePath)) return false;

        // Reject absolute + UNC + explicit traversal before the filesystem even sees it.
        if (Path.IsPathRooted(relativePath)) return false;
        if (relativePath.Contains("..", StringComparison.Ordinal)) return false;

        var runDir = RunDirectoryLayout.RunDir(_settings.Paths.Runs, runId);
        if (!Directory.Exists(runDir)) return false;

        var runDirFull = Path.GetFullPath(runDir);
        var candidate = Path.GetFullPath(Path.Combine(runDir, relativePath));

        var withSep = runDirFull.EndsWith(Path.DirectorySeparatorChar)
            ? runDirFull
            : runDirFull + Path.DirectorySeparatorChar;
        if (!candidate.StartsWith(withSep, StringComparison.OrdinalIgnoreCase))
            return false;

        if (!File.Exists(candidate))
            return false;

        fullPath = candidate;
        return true;
    }

    // --- helpers ---

    private static bool IsSafeRunId(string runId)
    {
        if (string.IsNullOrWhiteSpace(runId)) return false;
        // Run ids are of the form run-YYYYMMDD-HHMMSS-xxxxxx. Be permissive but
        // block separators that would let a caller construct a sibling path.
        if (runId.Contains('/') || runId.Contains('\\') || runId.Contains("..", StringComparison.Ordinal))
            return false;
        if (runId.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) return false;
        return true;
    }

    private static long? SafeFileSize(string path)
    {
        try { return new FileInfo(path).Length; }
        catch { return null; }
    }

    private static string? SafeReadText(string path)
    {
        try { return File.ReadAllText(path); }
        catch { return null; }
    }

    private string? TryReadTraceId(string runId)
    {
        var eventsPath = RunDirectoryLayout.RunEventsFile(_settings.Paths.Runs, runId);
        if (!File.Exists(eventsPath)) return null;
        try
        {
            using var stream = File.OpenRead(eventsPath);
            using var reader = new StreamReader(stream);
            string? line;
            int scanned = 0;
            // Events don't currently carry trace_id, but keep this forward-
            // compatible: if a future pipeline version writes one, we pick it up.
            while ((line = reader.ReadLine()) is not null && scanned < 50)
            {
                scanned++;
                if (!line.Contains("trace", StringComparison.OrdinalIgnoreCase)) continue;
                try
                {
                    using var doc = JsonDocument.Parse(line);
                    if (doc.RootElement.TryGetProperty("trace_id", out var tid) && tid.ValueKind == JsonValueKind.String)
                        return tid.GetString();
                    if (doc.RootElement.TryGetProperty("traceId", out var tid2) && tid2.ValueKind == JsonValueKind.String)
                        return tid2.GetString();
                }
                catch { /* skip malformed lines */ }
            }
        }
        catch { /* best-effort */ }
        return null;
    }

    // --- DTOs ---

    public sealed record RunSummary(
        string RunId,
        string? WorkRequestName,
        string? UserId,
        string? FinalPhase,
        bool Success,
        string? Verdict,
        int? RiskScore,
        DateTimeOffset? StartedAt,
        DateTimeOffset? CompletedAt,
        double? DurationSeconds);

    public sealed record RunDetail(
        RunSummary Summary,
        string RunDir,
        IReadOnlyList<FileEntry> Files,
        ArtifactsInfo Artifacts,
        string? QaRetroMarkdown,
        string? DirectivesMarkdown,
        string? TraceId);

    public sealed record FileEntry(string Name, bool IsDirectory, long? Bytes);

    public sealed record ArtifactsInfo(string Source, IReadOnlyList<ArtifactEntry> Entries);

    public sealed record ArtifactEntry(
        string Path,
        string? Status,
        long? Bytes,
        string? Reason,
        string? Detail);

    // --- internal JSON shapes for tolerant deserialization ---

    private sealed class ResultDoc
    {
        [JsonPropertyName("run_id")] public string? RunId { get; set; }
        [JsonPropertyName("success")] public bool Success { get; set; }
        [JsonPropertyName("final_phase")] public string? FinalPhase { get; set; }
        [JsonPropertyName("started_at")] public DateTimeOffset? StartedAt { get; set; }
        [JsonPropertyName("completed_at")] public DateTimeOffset? CompletedAt { get; set; }
        [JsonPropertyName("duration_seconds")] public double? DurationSeconds { get; set; }
        [JsonPropertyName("review_verdict")] public ReviewDoc? ReviewVerdict { get; set; }
    }

    private sealed class ReviewDoc
    {
        [JsonPropertyName("verdict")] public string? Verdict { get; set; }
        [JsonPropertyName("risk_score")] public int? RiskScore { get; set; }
    }

    private sealed class RequestDoc
    {
        [JsonPropertyName("work_request_name")] public string? WorkRequestName { get; set; }
        [JsonPropertyName("user_id")] public string? UserId { get; set; }
    }

    private sealed class ArtifactsIndexDoc
    {
        [JsonPropertyName("entries")] public List<ArtifactsIndexEntryDoc>? Entries { get; set; }
    }

    private sealed class ArtifactsIndexEntryDoc
    {
        [JsonPropertyName("path")] public string? Path { get; set; }
        [JsonPropertyName("status")] public string? Status { get; set; }
        [JsonPropertyName("bytes")] public long? Bytes { get; set; }
        [JsonPropertyName("reason")] public string? Reason { get; set; }
        [JsonPropertyName("detail")] public string? Detail { get; set; }
    }
}
