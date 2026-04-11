namespace Farmer.Core.Config;

/// <summary>
/// OpenAI API connection settings for the Phase 6 retrospective agent (and
/// any future MAF-based agents on the host side).
///
/// Provider pivot: originally planned to use Anthropic via MAF's preview
/// Anthropic provider. Pivoted to OpenAI because (a) OpenAI's MAF package
/// <c>Microsoft.Agents.AI.OpenAI</c> is stable (1.1.0), not prerelease,
/// and (b) typed structured output via <c>RunAsync&lt;T&gt;()</c> is a
/// much cleaner path than hand-rolling JSON parsing.
///
/// Note: the VM-side worker is still Claude CLI in full dangerous mode —
/// that's a separate concern about VM isolation and autonomous work,
/// unrelated to the host-side retrospective agent's provider choice.
///
/// The API key is NEVER stored in appsettings.json — set
/// <c>OPENAI_API_KEY</c> in the environment or use user-secrets:
///   <c>dotnet user-secrets set Farmer:OpenAI:ApiKey sk-...</c>
/// </summary>
public sealed class OpenAISettings
{
    public const string SectionName = "Farmer:OpenAI";

    /// <summary>
    /// Optional explicit key. If empty, <see cref="ResolveApiKey"/> falls
    /// through to the <c>OPENAI_API_KEY</c> environment variable.
    /// Leaving this empty in appsettings.json is the intended default.
    /// </summary>
    public string ApiKey { get; set; } = string.Empty;

    /// <summary>
    /// OpenAI model name for the retrospective agent. <c>gpt-4o-mini</c> is
    /// the April 2026 cheap+fast choice: $0.15/M input, $0.60/M output,
    /// native structured output, 128K context window.
    /// </summary>
    public string QaModel { get; set; } = "gpt-4o-mini";

    public int MaxOutputTokens { get; set; } = 2048;

    public int TimeoutSeconds { get; set; } = 60;

    /// <summary>
    /// Returns the configured API key, falling back to the
    /// <c>OPENAI_API_KEY</c> environment variable. Returns null if neither
    /// is set — callers should treat that as a configuration error.
    /// </summary>
    public string? ResolveApiKey() =>
        string.IsNullOrWhiteSpace(ApiKey)
            ? Environment.GetEnvironmentVariable("OPENAI_API_KEY")
            : ApiKey;
}
