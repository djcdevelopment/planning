using System.Text;
using Farmer.Core.Models;

namespace Farmer.Core.Workflow;

/// <summary>
/// Renders a prior attempt's <see cref="ReviewVerdict"/> (and, when available, the
/// retrospective's structured <see cref="DirectiveSuggestion"/> list) as a Markdown
/// document that the retry driver writes into a new run's plans dir as
/// <c>0-feedback.md</c>. The VM's CLAUDE.md already tells Claude that the first
/// prompt on a retry run is reviewer feedback, so this output is consumed as-is --
/// no VM-side change needed.
/// </summary>
public static class FeedbackBuilder
{
    public static string Render(
        ReviewVerdict verdict,
        int priorAttempt,
        string? priorRunId,
        IReadOnlyList<DirectiveSuggestion>? directives = null)
    {
        var sb = new StringBuilder();

        sb.AppendLine("# Reviewer Feedback for this Retry");
        sb.AppendLine();
        var who = string.IsNullOrWhiteSpace(priorRunId) ? "a prior attempt" : $"run `{priorRunId}`";
        sb.AppendLine($"This is attempt **{priorAttempt + 1}**. Attempt {priorAttempt} ({who}) produced verdict **{verdict.Verdict}** with risk score {verdict.RiskScore}/100. Before continuing to your regular prompts, address the issues below.");
        sb.AppendLine();

        if (verdict.Findings is { Count: > 0 })
        {
            sb.AppendLine("## Findings from the prior attempt");
            sb.AppendLine();
            foreach (var f in verdict.Findings)
                sb.AppendLine($"- {f}");
            sb.AppendLine();
        }

        if (verdict.Suggestions is { Count: > 0 })
        {
            sb.AppendLine("## Suggestions from the reviewer");
            sb.AppendLine();
            foreach (var s in verdict.Suggestions)
                sb.AppendLine($"- {s}");
            sb.AppendLine();
        }

        if (directives is { Count: > 0 })
        {
            sb.AppendLine("## Specific directives");
            sb.AppendLine();
            foreach (var d in directives)
            {
                var target = string.IsNullOrWhiteSpace(d.Target) ? "(unspecified)" : d.Target;
                sb.AppendLine($"- **[{d.Scope} -> {target}]** {d.Rationale}");
                if (!string.IsNullOrWhiteSpace(d.SuggestedValue))
                    sb.AppendLine($"  - Suggested: `{d.SuggestedValue}`");
                if (!string.IsNullOrWhiteSpace(d.CurrentValue))
                    sb.AppendLine($"  - Current: `{d.CurrentValue}`");
            }
            sb.AppendLine();
        }

        sb.AppendLine("---");
        sb.AppendLine();
        sb.AppendLine("After addressing the points above, proceed with the numbered prompts that follow.");

        return sb.ToString();
    }
}
