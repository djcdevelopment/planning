using System.Text;
using System.Text.Json;
using Farmer.Core.Contracts;

namespace Farmer.Agents.Prompts;

/// <summary>
/// The retrospective agent's prompt surface. Kept in one place so the
/// prompt and the JSON schema stay in sync — the agent is told exactly
/// what shape to return, and our parser (<c>MafRetrospectiveAgent</c>)
/// knows exactly what to expect.
/// </summary>
public static class RetrospectivePrompt
{
    /// <summary>
    /// System instructions for the retrospective agent. These are the
    /// agent's charter. The tone is deliberately framed as "post-mortem
    /// reviewer that feeds future runs" — NOT a gate, NOT a Retry trigger,
    /// NOT a judge of success or failure. Data is the product.
    /// </summary>
    public const string SystemInstructions = """
        You are the Farmer retrospective agent. You review the output of a
        worker run on a sandbox VM and produce a post-mortem — a descriptive
        analysis of what happened, plus forward-looking suggestions for how
        future runs of the same work request could be better instructed.

        IMPORTANT framing rules:

        1. You are not a gate. Your verdict does not pass or fail the run.
           Every run that reached you has already succeeded in the
           narrow sense that the pipeline finished. Your job is to extract
           signal from what the worker produced.

        2. Failures are data. A worker that produced corrupted output, a
           half-built feature, or nothing at all is just as valuable as a
           clean success — each tells us something about the prompts,
           the VM environment, or the work request's feasibility.

        3. You describe. You do not prescribe retries. If you think a run
           is bad, say so in findings and suggestions. Do not ask for a
           retry — there is no retry mechanism consuming your output.

        4. You look forward. Every suggestion should answer: "if the user
           ran the same work request name again tomorrow, what should be
           different?" That might mean rewriting a prompt, updating the
           VM's CLAUDE.md directive, or adjusting task-packet fields.

        5. You are concise. Findings and suggestions are one sentence each.
           The qa_retro markdown is at most a few short paragraphs. You are
           informing a human reader who has ten other runs to look at.

        Scoring rubric — risk_score is 0-100:
        - 0-20: worker produced expected output, no red flags
        - 21-40: minor notes, a few small improvements possible
        - 41-60: significant issues, some outputs may need rework
        - 61-80: major issues, outputs are probably not usable as-is
        - 81-100: the run is a write-off; the work request or prompts
                  need substantial rethinking before running again

        Verdict values — these are DESCRIPTIVE not PRESCRIPTIVE:
        - Accept: the run is fine, no directive changes needed
        - Retry: "if you ran this again it would probably go better with
                  the suggested changes" (still not a retry trigger —
                  it's shorthand for "improvable")
        - Reject: the run is not salvageable as-is; substantial rework
                  needed before running the same work request again

        You MUST return a single JSON object matching the schema in the
        user message. Do not wrap it in markdown code fences. Do not
        include commentary outside the JSON. If you cannot comply, return
        a JSON object with verdict="reject", risk_score=100, and a finding
        explaining the problem.
        """;

    /// <summary>
    /// Builds the user message: the JSON schema the agent must return,
    /// plus all the run artifacts as context. Kept plain-text for
    /// compatibility with <see cref="M:Microsoft.Extensions.AI.IChatClient"/>'s
    /// ChatMessage content model.
    /// </summary>
    public static string BuildUserMessage(RetrospectiveContext context)
    {
        var sb = new StringBuilder();

        sb.AppendLine("Return a JSON object exactly matching this schema:");
        sb.AppendLine();
        sb.AppendLine("""
            {
              "verdict": "accept" | "retry" | "reject",
              "risk_score": <integer 0-100>,
              "qa_retro_markdown": "<short markdown, a few paragraphs at most>",
              "findings": ["<finding 1>", "<finding 2>", ...],
              "suggestions": ["<suggestion 1>", "<suggestion 2>", ...],
              "directive_suggestions": [
                {
                  "scope": "prompts" | "claude_md" | "task_packet",
                  "target": "<filename, section name, or field name>",
                  "current_value": "<optional — the existing text>",
                  "suggested_value": "<what it should say instead>",
                  "rationale": "<why this change helps future runs>"
                }
              ]
            }
            """);
        sb.AppendLine();
        sb.AppendLine("=== RUN CONTEXT ===");
        sb.AppendLine();
        sb.AppendLine($"run_id: {context.RunId}");
        sb.AppendLine($"attempt: {context.Attempt}");
        sb.AppendLine($"work_request_name: {context.Request.WorkRequestName}");
        if (!string.IsNullOrEmpty(context.Request.ParentRunId))
            sb.AppendLine($"parent_run_id: {context.Request.ParentRunId}");
        sb.AppendLine();

        sb.AppendLine("=== TASK PACKET ===");
        sb.AppendLine(JsonSerializer.Serialize(context.TaskPacket, JsonOpts));
        sb.AppendLine();

        sb.AppendLine("=== WORKER MANIFEST ===");
        sb.AppendLine(context.ManifestJson);
        sb.AppendLine();

        if (!string.IsNullOrEmpty(context.SummaryJson))
        {
            sb.AppendLine("=== WORKER SUMMARY ===");
            sb.AppendLine(context.SummaryJson);
            sb.AppendLine();
        }

        if (!string.IsNullOrEmpty(context.WorkerRetroMarkdown))
        {
            sb.AppendLine("=== WORKER'S OWN RETRO (Claude's self-review) ===");
            sb.AppendLine(context.WorkerRetroMarkdown);
            sb.AppendLine();
        }

        if (!string.IsNullOrEmpty(context.ExecutionLogTail))
        {
            sb.AppendLine("=== EXECUTION LOG (last lines) ===");
            sb.AppendLine(context.ExecutionLogTail);
            sb.AppendLine();
        }

        if (context.SampledOutputs.Count > 0)
        {
            sb.AppendLine("=== SAMPLED OUTPUT FILES ===");
            foreach (var snippet in context.SampledOutputs)
            {
                sb.AppendLine($"--- {snippet.Path} ({snippet.TotalBytes} bytes{(snippet.Truncated ? ", truncated" : "")}) ---");
                sb.AppendLine(snippet.Content);
                sb.AppendLine();
            }
        }

        sb.AppendLine("=== END CONTEXT ===");
        sb.AppendLine();
        sb.AppendLine("Return only the JSON object specified above.");

        return sb.ToString();
    }

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
    };
}
