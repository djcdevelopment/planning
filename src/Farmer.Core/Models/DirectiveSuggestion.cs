using System.Text.Json.Serialization;

namespace Farmer.Core.Models;

/// <summary>
/// Scope of a <see cref="DirectiveSuggestion"/> — what part of a future run
/// it would apply to. The retrospective agent is descriptive about scope
/// but never prescriptive about timing: nothing is auto-applied in Phase 6.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum DirectiveScope
{
    /// <summary>Suggest a change to a numbered prompt file for this work request.</summary>
    Prompts,
    /// <summary>Suggest a change to the VM-side CLAUDE.md worker directive.</summary>
    ClaudeMd,
    /// <summary>Suggest a change to task-packet fields (feedback, worker_mode, etc.).</summary>
    TaskPacket,
}

/// <summary>
/// A forward-looking suggestion from the retrospective agent: "next time this
/// work_request_name runs, consider changing X to Y for reason Z". Phase 6
/// persists these as <c>directive-suggestions.md</c> alongside <c>qa-retro.md</c>
/// but does not automatically apply them. A human (or a Phase 7+ tool) decides
/// when and whether to act on them.
/// </summary>
public sealed class DirectiveSuggestion
{
    [JsonPropertyName("scope")]
    public DirectiveScope Scope { get; set; }

    /// <summary>
    /// What specifically the suggestion points at. For <see cref="DirectiveScope.Prompts"/>
    /// this is usually a filename like "1-SetupProject.md". For
    /// <see cref="DirectiveScope.ClaudeMd"/> it's a heading or section name. For
    /// <see cref="DirectiveScope.TaskPacket"/> it's the JSON path (e.g. "feedback").
    /// </summary>
    [JsonPropertyName("target")]
    public string Target { get; set; } = string.Empty;

    /// <summary>What's there today, for diff-ability. Optional.</summary>
    [JsonPropertyName("current_value")]
    public string? CurrentValue { get; set; }

    [JsonPropertyName("suggested_value")]
    public string SuggestedValue { get; set; } = string.Empty;

    /// <summary>Why the retrospective agent thinks this change would help.</summary>
    [JsonPropertyName("rationale")]
    public string Rationale { get; set; } = string.Empty;
}
