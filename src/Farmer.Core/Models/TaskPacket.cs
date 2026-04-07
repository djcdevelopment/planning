using System.Text.Json.Serialization;

namespace Farmer.Core.Models;

public sealed class TaskPacket
{
    [JsonPropertyName("run_id")]
    public string RunId { get; set; } = string.Empty;

    [JsonPropertyName("work_request_name")]
    public string WorkRequestName { get; set; } = string.Empty;

    [JsonPropertyName("prompts")]
    public List<PromptFile> Prompts { get; set; } = [];

    [JsonPropertyName("branch_name")]
    public string BranchName { get; set; } = string.Empty;

    [JsonPropertyName("feedback")]
    public string? Feedback { get; set; }

    [JsonPropertyName("created_at")]
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class PromptFile
{
    [JsonPropertyName("order")]
    public int Order { get; set; }

    [JsonPropertyName("filename")]
    public string Filename { get; set; } = string.Empty;

    [JsonPropertyName("content")]
    public string Content { get; set; } = string.Empty;
}
