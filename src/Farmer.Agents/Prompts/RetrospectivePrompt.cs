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

        6. Prefer source evidence over the worker's self-report. When the
           user message contains a "Source files produced" section, that
           is the ground truth about what the worker actually wrote. The
           manifest, summary, and worker-retro are the worker's claims —
           useful context, but treat them as claims to verify, not facts.
           If the source contradicts the worker's self-report (e.g., the
           worker claims a feature is complete but the source shows a
           stub, TODO, or missing logic), flag that contradiction
           explicitly in findings. That discrimination is the whole
           reason source is in front of you.

           If the "Source files produced" section reports that no source
           was captured, say so in your retro and note that your
           reasoning is limited to the manifest + summary.

        7. Do not speculate to fill a gap. When the "Source files produced"
           section says "None captured for this run," state in your
           findings that the source was not captured for this run. Do NOT
           invent claims about the code's contents, unrelated projects,
           run-ID mismatches, or prompt/worker misalignment — those are
           speculation without evidence. Treat "source not captured" as a
           caveat on your verdict, not as evidence of misrouting. An
           archival race or a worker that wrote nothing both look the same
           from your seat; say so plainly instead of guessing.

        8. Do not invent cross-references. Do not mention projects, run
           IDs, filenames, frameworks, or languages that do not appear
           verbatim in the manifest, summary, worker retro, execution log,
           or source-files section you received. If you cite something,
           quote it verbatim from one of those inputs. If you cannot
           quote it, do not claim it.

        9. Prefer neutral wording when evidence is thin. If the only
           issue is that source was not captured, choose "retry" with a
           moderate risk score (around 40-60) over "reject" at 80+. A
           missing-source run is ambiguous — it could be a real worker
           failure, or it could be an archival race — not definitely
           broken. Reserve high risk scores and "reject" for cases where
           the evidence you DO have shows a concrete problem.

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
    /// <param name="context">Run context — manifest, summary, worker retro, log tail.</param>
    /// <param name="sourceFiles">
    /// Source files produced by the worker, loaded by the agent from the
    /// run's <c>artifacts/</c> directory (Phase 7.5 Stream G populates it).
    /// Pass null or empty to render a "None captured for this run" block —
    /// the agent's system instructions know to degrade gracefully when
    /// source isn't available.
    /// </param>
    /// <param name="totalSourceFiles">
    /// Total number of source files available on disk before the
    /// <c>MaxChangedFiles</c> cap was applied. When null, the prompt
    /// reports <paramref name="sourceFiles"/>.Count as the total. Used
    /// to signal "showing M of N" when we're sampling.
    /// </param>
    public static string BuildUserMessage(
        RetrospectiveContext context,
        IReadOnlyList<ArtifactSnippet>? sourceFiles = null,
        int? totalSourceFiles = null)
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

        // Source files produced — the ground-truth section the agent is
        // instructed to prefer over the worker's self-report. Populated
        // from the archived artifacts/ dir (Stream G) at the agent level.
        // Fall back to the legacy SampledOutputs field if the caller
        // hasn't pre-loaded via the new `sourceFiles` parameter — keeps
        // back-compat with any future in-process caller that populates
        // the context directly.
        var files = sourceFiles ?? context.SampledOutputs;

        sb.AppendLine("## Source files produced");
        sb.AppendLine();
        if (files.Count == 0)
        {
            sb.AppendLine("None captured for this run — the retrospective is reasoning from the manifest + summary only.");
            sb.AppendLine();
        }
        else
        {
            var total = totalSourceFiles ?? files.Count;
            sb.AppendLine($"<file count>{total} files (showing {files.Count} of {total}; up to <MaxFileBytes> bytes each)</file count>");
            sb.AppendLine();
            foreach (var snippet in files)
            {
                var lang = InferLanguageHint(snippet.Path);
                sb.AppendLine($"### {snippet.Path}" +
                    (snippet.Truncated ? $" (truncated at {snippet.Content.Length} of {snippet.TotalBytes} bytes)" : ""));
                sb.Append("```");
                if (!string.IsNullOrEmpty(lang)) sb.Append(lang);
                sb.AppendLine();
                sb.AppendLine(snippet.Content);
                sb.AppendLine("```");
                sb.AppendLine();
            }
        }

        sb.AppendLine("=== END CONTEXT ===");
        sb.AppendLine();
        sb.AppendLine("Return only the JSON object specified above.");

        return sb.ToString();
    }

    /// <summary>
    /// Lightweight language hint from a file extension — helps the model
    /// treat the block as code (and prevents ``` inside the content from
    /// confusing it). Unknown extensions return empty so the fence stays
    /// bare.
    /// </summary>
    private static string InferLanguageHint(string relativePath)
    {
        if (string.IsNullOrEmpty(relativePath)) return string.Empty;
        var ext = Path.GetExtension(relativePath).TrimStart('.').ToLowerInvariant();
        return ext switch
        {
            "cs" => "csharp",
            "py" => "python",
            "js" or "mjs" or "cjs" => "javascript",
            "ts" => "typescript",
            "tsx" => "tsx",
            "jsx" => "jsx",
            "json" => "json",
            "xml" => "xml",
            "html" or "htm" => "html",
            "css" => "css",
            "md" or "markdown" => "markdown",
            "yml" or "yaml" => "yaml",
            "toml" => "toml",
            "sh" or "bash" => "bash",
            "ps1" or "psm1" or "psd1" => "powershell",
            "sql" => "sql",
            "go" => "go",
            "rs" => "rust",
            "java" => "java",
            "kt" or "kts" => "kotlin",
            "swift" => "swift",
            "rb" => "ruby",
            "php" => "php",
            "c" or "h" => "c",
            "cpp" or "cc" or "cxx" or "hpp" => "cpp",
            "dockerfile" => "dockerfile",
            "txt" or "log" => "",
            _ => "",
        };
    }

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
    };
}
