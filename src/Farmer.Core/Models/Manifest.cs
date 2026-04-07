using System.Text.Json.Serialization;

namespace Farmer.Core.Models;

public sealed class Manifest
{
    [JsonPropertyName("run_id")]
    public string RunId { get; set; } = string.Empty;

    [JsonPropertyName("files_changed")]
    public List<string> FilesChanged { get; set; } = [];

    [JsonPropertyName("branch_name")]
    public string BranchName { get; set; } = string.Empty;

    [JsonPropertyName("commit_sha")]
    public string? CommitSha { get; set; }

    [JsonPropertyName("generated_at")]
    public DateTimeOffset GeneratedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class Summary
{
    [JsonPropertyName("run_id")]
    public string RunId { get; set; } = string.Empty;

    [JsonPropertyName("description")]
    public string Description { get; set; } = string.Empty;

    [JsonPropertyName("issues")]
    public List<string> Issues { get; set; } = [];

    [JsonPropertyName("retro")]
    public string? Retro { get; set; }

    [JsonPropertyName("generated_at")]
    public DateTimeOffset GeneratedAt { get; set; } = DateTimeOffset.UtcNow;
}
