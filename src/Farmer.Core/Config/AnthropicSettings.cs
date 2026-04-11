namespace Farmer.Core.Config;

/// <summary>
/// Anthropic API connection settings for the Phase 6 retrospective agent (and
/// any future MAF-based agents). The API key is NEVER stored in appsettings.json —
/// set <c>ANTHROPIC_API_KEY</c> in the environment or use user-secrets.
/// </summary>
public sealed class AnthropicSettings
{
    public const string SectionName = "Farmer:Anthropic";

    /// <summary>
    /// Optional explicit key. If empty, <see cref="ResolveApiKey"/> falls
    /// through to the <c>ANTHROPIC_API_KEY</c> environment variable.
    /// Leaving this empty in appsettings.json is the intended default.
    /// </summary>
    public string ApiKey { get; set; } = string.Empty;

    /// <summary>Claude model name for the retrospective agent. Haiku is the
    /// right default: fast, cheap, strong at structured output.</summary>
    public string QaModel { get; set; } = "claude-haiku-4-5";

    public int MaxOutputTokens { get; set; } = 2048;

    public int TimeoutSeconds { get; set; } = 60;

    /// <summary>
    /// Returns the configured API key, falling back to the
    /// <c>ANTHROPIC_API_KEY</c> environment variable. Returns null if neither
    /// is set — callers should treat that as a configuration error.
    /// </summary>
    public string? ResolveApiKey() =>
        string.IsNullOrWhiteSpace(ApiKey)
            ? Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY")
            : ApiKey;
}
