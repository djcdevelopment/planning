namespace Farmer.Core.Config;

/// <summary>
/// Azure OpenAI connection settings for the Phase 6+ retrospective agent
/// (and any future MAF-based agents on the host side).
///
/// Transport pivot (Phase 7, 2026-04-22): moved from the public OpenAI
/// endpoint + API key to Azure OpenAI + Entra via
/// <c>DefaultAzureCredential</c>. The MAF binding is unchanged — still
/// <c>Microsoft.Agents.AI.OpenAI</c> 1.1.0. Only the underlying HTTP client
/// swapped to <c>AzureOpenAIClient</c>. See
/// <c>docs/adr/adr-006-openai-over-anthropic-maf.md</c> (Update 2026-04-22).
///
/// No API key. Auth is Entra: the developer signs in via
/// <c>Connect-AzAccount</c> (picked up by <c>AzurePowerShellCredential</c>)
/// or the Host runs under a managed identity; either way the
/// <c>DefaultAzureCredential</c> chain resolves a token. The principal
/// needs the <c>Cognitive Services OpenAI User</c> role on the Azure
/// OpenAI resource.
///
/// The VM-side worker is still Claude CLI in full dangerous mode — that's
/// a separate concern about VM isolation and autonomous work, unrelated
/// to the host-side retrospective agent's provider choice.
/// </summary>
public sealed class OpenAISettings
{
    public const string SectionName = "Farmer:OpenAI";

    /// <summary>
    /// The Azure OpenAI resource endpoint, e.g.
    /// <c>https://farmer-openai-dev.openai.azure.com/</c>. Required.
    /// When empty, the retrospective agent is skipped per
    /// <see cref="RetrospectiveSettings.FailureBehavior"/> (default AutoPass).
    /// </summary>
    public string Endpoint { get; set; } = string.Empty;

    /// <summary>
    /// The Azure OpenAI deployment name (not the raw model name). The
    /// deployment maps to a specific model + version in the portal — e.g.
    /// a deployment named <c>gpt-4.1-mini</c> backed by <c>gpt-4.1-mini</c>
    /// model version <c>2025-04-14</c>. Required.
    /// </summary>
    public string DeploymentName { get; set; } = string.Empty;

    public int MaxOutputTokens { get; set; } = 2048;

    public int TimeoutSeconds { get; set; } = 60;
}
