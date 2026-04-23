using System.Text.Json.Serialization;

namespace Farmer.Core.Models;

/// <summary>
/// A prompt carried inline on a /trigger payload instead of being read from
/// a pre-existing sample-plan directory on disk. Populated by clients that
/// have no shared filesystem with Farmer.Host (e.g. a phone-originated
/// request over the public cloudflared tunnel).
///
/// When <see cref="RunRequest.PromptsInline"/> is non-empty, LoadPromptsStage
/// uses these directly and skips the disk scan under
/// <c>{SamplePlansPath}/{WorkRequestName}/</c>. The list position drives the
/// resulting <see cref="PromptFile.Order"/> (1-indexed); the numeric-prefix
/// rule that governs on-disk prompts does not apply here because the client
/// chose the order explicitly.
/// </summary>
public sealed class InlinePrompt
{
    /// <summary>
    /// Filename for worker bookkeeping (e.g. <c>1-Build.md</c>). The worker's
    /// VM-side scripts key off filenames in logs, so something human-meaningful
    /// here is helpful but not enforced.
    /// </summary>
    [JsonPropertyName("filename")]
    public string Filename { get; set; } = string.Empty;

    /// <summary>
    /// Prompt body. Markdown by convention; worker doesn't parse it, just
    /// passes it to Claude CLI.
    /// </summary>
    [JsonPropertyName("content")]
    public string Content { get; set; } = string.Empty;
}
