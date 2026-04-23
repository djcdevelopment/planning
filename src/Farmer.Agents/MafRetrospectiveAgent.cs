using System.Diagnostics;
using System.Text.Json.Serialization;
using Azure.AI.OpenAI;
using Azure.Identity;
using Farmer.Agents.Prompts;
using Farmer.Core.Config;
using Farmer.Core.Contracts;
using Farmer.Core.Models;
using Farmer.Core.Telemetry;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Farmer.Agents;

/// <summary>
/// The Microsoft Agent Framework implementation of <see cref="IRetrospectiveAgent"/>.
/// Wraps an <see cref="AIAgent"/> built from the MAF OpenAI provider talking
/// to Azure OpenAI via <see cref="DefaultAzureCredential"/>.
///
/// This is the ONLY class in Farmer that imports <c>Microsoft.Agents.AI.*</c>,
/// <c>Microsoft.Extensions.AI.*</c>, <c>Azure.AI.OpenAI</c>, or
/// <c>Azure.Identity</c> types. If MAF, the OpenAI SDK, or the Azure SDKs
/// churn, this file is the blast radius.
///
/// Provider pivot (see ADR-006): originally targeted the preview MAF
/// Anthropic provider, pivoted to OpenAI because the stable 1.1.0 release
/// has native typed structured output via <c>AgentResponse&lt;T&gt;</c>.
/// That eliminates ~120 lines of hand-rolled JSON parsing.
///
/// Transport pivot (ADR-006 Update 2026-04-22): moved the underlying HTTP
/// client from the public OpenAI endpoint (+ API key) to Azure OpenAI
/// (+ Entra via <c>DefaultAzureCredential</c>). The MAF binding is still
/// <c>Microsoft.Agents.AI.OpenAI</c> because <c>AzureOpenAIClient</c>
/// shares a base class with <c>OpenAIClient</c> and flows through the same
/// chat-client shape.
///
/// The VM worker is still Claude CLI — that's a separate decision about
/// VM isolation and worker autonomy, unrelated to this class.
/// </summary>
public sealed class MafRetrospectiveAgent : IRetrospectiveAgent
{
    private readonly Lazy<AIAgent?> _agentLazy;
    private readonly OpenAISettings _openAi;
    private readonly RetrospectiveSettings _settings;
    private readonly ILogger<MafRetrospectiveAgent> _logger;

    /// <summary>
    /// Production constructor. Does NOT throw on missing configuration —
    /// the agent is built lazily when <see cref="AnalyzeAsync"/> is first
    /// called. If endpoint or deployment name are unset, the stage applies
    /// <see cref="RetrospectiveSettings.FailureBehavior"/> (default AutoPass)
    /// so the pipeline still completes. Per ADR-007: a failed retrospective
    /// is a lost learning opportunity, not a failed run.
    /// </summary>
    public MafRetrospectiveAgent(
        IOptions<OpenAISettings> openAi,
        IOptions<RetrospectiveSettings> settings,
        ILogger<MafRetrospectiveAgent> logger)
    {
        _openAi = openAi.Value;
        _settings = settings.Value;
        _logger = logger;
        _agentLazy = new Lazy<AIAgent?>(TryBuildAgent, LazyThreadSafetyMode.PublicationOnly);
    }

    /// <summary>
    /// Test seam: construct with a pre-built <see cref="AIAgent"/> so unit
    /// tests can inject a fake/mock agent without touching the OpenAI SDK.
    /// </summary>
    internal MafRetrospectiveAgent(
        AIAgent agent,
        IOptions<RetrospectiveSettings> settings,
        ILogger<MafRetrospectiveAgent> logger)
    {
        _agentLazy = new Lazy<AIAgent?>(() => agent);
        _openAi = new OpenAISettings();
        _settings = settings.Value;
        _logger = logger;
    }

    private AIAgent? TryBuildAgent()
    {
        if (string.IsNullOrWhiteSpace(_openAi.Endpoint) ||
            string.IsNullOrWhiteSpace(_openAi.DeploymentName))
        {
            _logger.LogWarning(
                "Azure OpenAI endpoint or deployment name not configured. " +
                "Retrospectives will be skipped. " +
                "Set Farmer:OpenAI:Endpoint and Farmer:OpenAI:DeploymentName.");
            return null;
        }

        // Entra auth: DefaultAzureCredential walks its chain (env vars →
        // managed identity → Azure CLI → Azure PowerShell → Visual Studio).
        // Locally the developer has `Connect-AzAccount` signed in and the
        // AzurePowerShellCredential step wins. The principal needs the
        // "Cognitive Services OpenAI User" role on the resource.
        var azureClient = new AzureOpenAIClient(
            new Uri(_openAi.Endpoint),
            new DefaultAzureCredential());
        var chatClient = azureClient.GetChatClient(_openAi.DeploymentName);
        return chatClient.AsIChatClient().AsAIAgent(
            instructions: RetrospectivePrompt.SystemInstructions,
            name: "FarmerRetrospectiveAgent");
    }

    public async Task<RetrospectiveResult> AnalyzeAsync(
        RetrospectiveContext context,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        var agent = _agentLazy.Value;
        if (agent is null)
        {
            return new RetrospectiveResult
            {
                Error = "Azure OpenAI endpoint or deployment name not configured. " +
                        "Set Farmer:OpenAI:Endpoint and Farmer:OpenAI:DeploymentName.",
                AgentCallAttempts = 0,
            };
        }

        using var activity = FarmerActivitySource.StartAgentReview(
            context.RunId, "FarmerRetrospectiveAgent");

        var userMessage = RetrospectivePrompt.BuildUserMessage(context);
        var maxAttempts = _settings.MaxAgentCallRetries + 1;
        var attemptsMade = 0;
        var inputTokens = 0;
        var outputTokens = 0;
        string? lastError = null;

        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            attemptsMade = attempt;
            ct.ThrowIfCancellationRequested();

            try
            {
                var sw = Stopwatch.StartNew();

                // Typed structured output — the SDK enforces the schema from
                // RetrospectiveDto's shape. No hand-rolled JSON parser, no
                // code-fence stripping, no per-field validation.
                var response = await agent.RunAsync<RetrospectiveDto>(
                    userMessage,
                    cancellationToken: ct);

                sw.Stop();

                if (response.Usage is not null)
                {
                    inputTokens += (int)(response.Usage.InputTokenCount ?? 0);
                    outputTokens += (int)(response.Usage.OutputTokenCount ?? 0);
                }

                _logger.LogInformation(
                    "Retrospective agent attempt {Attempt}/{Max} for run {RunId} " +
                    "took {DurationMs}ms, {InTokens}/{OutTokens} tokens",
                    attempt, maxAttempts, context.RunId, sw.ElapsedMilliseconds,
                    inputTokens, outputTokens);

                var dto = response.Result;
                if (dto is null)
                {
                    lastError = "agent returned null structured result";
                    continue;
                }

                return new RetrospectiveResult
                {
                    Verdict = BuildVerdict(context.RunId, dto),
                    QaRetroMarkdown = dto.QaRetroMarkdown,
                    DirectiveSuggestions = BuildDirectiveSuggestions(dto.DirectiveSuggestions),
                    AgentCallAttempts = attemptsMade,
                    InputTokens = inputTokens,
                    OutputTokens = outputTokens,
                };
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                lastError = $"call failed: {ex.Message}";
                _logger.LogWarning(ex,
                    "Retrospective agent call failed on attempt {Attempt}/{Max}",
                    attempt, maxAttempts);

                FarmerMetrics.QaAgentCallFailures.Add(1,
                    new KeyValuePair<string, object?>("farmer.qa.failure_reason", ex.GetType().Name));
            }

            if (attempt < maxAttempts)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(100 * attempt), ct);
            }
        }

        return new RetrospectiveResult
        {
            Error = lastError ?? "unknown failure",
            AgentCallAttempts = attemptsMade,
            InputTokens = inputTokens,
            OutputTokens = outputTokens,
        };
    }

    private static ReviewVerdict BuildVerdict(string runId, RetrospectiveDto dto)
    {
        var verdict = new ReviewVerdict
        {
            RunId = runId,
            Verdict = ParseVerdictEnum(dto.Verdict),
            RiskScore = Math.Clamp(dto.RiskScore, 0, 100),
            ReviewedAt = DateTimeOffset.UtcNow,
        };
        foreach (var f in dto.Findings ?? Enumerable.Empty<string>())
            if (!string.IsNullOrWhiteSpace(f)) verdict.Findings.Add(f);
        foreach (var s in dto.Suggestions ?? Enumerable.Empty<string>())
            if (!string.IsNullOrWhiteSpace(s)) verdict.Suggestions.Add(s);
        return verdict;
    }

    private static IReadOnlyList<DirectiveSuggestion> BuildDirectiveSuggestions(
        IReadOnlyList<DirectiveSuggestionDto>? raw)
    {
        if (raw is null || raw.Count == 0) return Array.Empty<DirectiveSuggestion>();
        var list = new List<DirectiveSuggestion>(raw.Count);
        foreach (var d in raw)
        {
            if (string.IsNullOrWhiteSpace(d.SuggestedValue)) continue;
            list.Add(new DirectiveSuggestion
            {
                Scope = ParseScopeEnum(d.Scope),
                Target = d.Target ?? string.Empty,
                CurrentValue = d.CurrentValue,
                SuggestedValue = d.SuggestedValue,
                Rationale = d.Rationale ?? string.Empty,
            });
        }
        return list;
    }

    private static Verdict ParseVerdictEnum(string? raw) =>
        (raw ?? "").Trim().ToLowerInvariant() switch
        {
            "accept" => Verdict.Accept,
            "retry" => Verdict.Retry,
            "reject" => Verdict.Reject,
            _ => Verdict.Accept, // safe default — post-mortem never gates
        };

    private static DirectiveScope ParseScopeEnum(string? raw) =>
        (raw ?? "").Trim().ToLowerInvariant() switch
        {
            "prompts" => DirectiveScope.Prompts,
            "claude_md" => DirectiveScope.ClaudeMd,
            "task_packet" => DirectiveScope.TaskPacket,
            _ => DirectiveScope.Prompts,
        };

    /// <summary>
    /// DTO that defines the JSON schema the OpenAI agent must return.
    /// The MAF SDK enforces this schema via <c>RunAsync&lt;T&gt;</c> — we
    /// never parse freeform text.
    /// </summary>
    public sealed class RetrospectiveDto
    {
        [JsonPropertyName("verdict")]
        public string Verdict { get; set; } = "accept";

        [JsonPropertyName("risk_score")]
        public int RiskScore { get; set; }

        [JsonPropertyName("qa_retro_markdown")]
        public string? QaRetroMarkdown { get; set; }

        [JsonPropertyName("findings")]
        public List<string>? Findings { get; set; }

        [JsonPropertyName("suggestions")]
        public List<string>? Suggestions { get; set; }

        [JsonPropertyName("directive_suggestions")]
        public List<DirectiveSuggestionDto>? DirectiveSuggestions { get; set; }
    }

    public sealed class DirectiveSuggestionDto
    {
        [JsonPropertyName("scope")]
        public string? Scope { get; set; }

        [JsonPropertyName("target")]
        public string? Target { get; set; }

        [JsonPropertyName("current_value")]
        public string? CurrentValue { get; set; }

        [JsonPropertyName("suggested_value")]
        public string SuggestedValue { get; set; } = string.Empty;

        [JsonPropertyName("rationale")]
        public string? Rationale { get; set; }
    }
}
