using System.Text.Json.Serialization;

namespace Farmer.Core.Models;

public sealed class Manifest
{
    [JsonPropertyName("run_id")]
    public string RunId { get; set; } = string.Empty;

    /// <summary>
    /// Legacy flat list of paths the worker touched. Kept for backward
    /// compatibility with the Phase 5 fake worker and any consumer that
    /// only knows about files. Phase 6 workers should populate
    /// <see cref="Outputs"/> instead; this field is derived.
    /// </summary>
    [JsonPropertyName("files_changed")]
    public List<string> FilesChanged { get; set; } = [];

    /// <summary>
    /// Richer output description. A Phase 6 worker can produce files,
    /// directories, archives, binaries, or reports — anything the VM lets
    /// it create. Kind classification lets downstream tooling decide how to
    /// inspect each entry.
    /// </summary>
    [JsonPropertyName("outputs")]
    public List<OutputArtifact> Outputs { get; set; } = [];

    [JsonPropertyName("branch_name")]
    public string BranchName { get; set; } = string.Empty;

    [JsonPropertyName("commit_sha")]
    public string? CommitSha { get; set; }

    [JsonPropertyName("generated_at")]
    public DateTimeOffset GeneratedAt { get; set; } = DateTimeOffset.UtcNow;
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum OutputKind
{
    /// <summary>A single file (source code, config, document).</summary>
    File,
    /// <summary>A directory of files.</summary>
    Directory,
    /// <summary>A zip/tar/gzip archive.</summary>
    Archive,
    /// <summary>A compiled binary or executable.</summary>
    Binary,
    /// <summary>A report or summary artifact (markdown, json, txt).</summary>
    Report,
}

public sealed class OutputArtifact
{
    [JsonPropertyName("kind")]
    public OutputKind Kind { get; set; } = OutputKind.File;

    [JsonPropertyName("path")]
    public string Path { get; set; } = string.Empty;

    [JsonPropertyName("bytes")]
    public long Bytes { get; set; }

    [JsonPropertyName("description")]
    public string? Description { get; set; }
}

public sealed class Summary
{
    [JsonPropertyName("run_id")]
    public string RunId { get; set; } = string.Empty;

    [JsonPropertyName("description")]
    public string Description { get; set; } = string.Empty;

    [JsonPropertyName("issues")]
    public List<string> Issues { get; set; } = [];

    /// <summary>
    /// Legacy freeform retro blob. Preserved for Phase 5 compat; new code
    /// should read <see cref="WorkerRetro"/> instead which is just the same
    /// value under a clearer name.
    /// </summary>
    [JsonPropertyName("retro")]
    public string? Retro { get; set; }

    /// <summary>
    /// Claude's self-review of the run (what went well, what didn't, what it
    /// noticed). Written by the real Phase 6 worker, read by the
    /// retrospective agent as context.
    /// </summary>
    [JsonPropertyName("worker_retro")]
    public string? WorkerRetro { get; set; }

    [JsonPropertyName("generated_at")]
    public DateTimeOffset GeneratedAt { get; set; } = DateTimeOffset.UtcNow;
}
