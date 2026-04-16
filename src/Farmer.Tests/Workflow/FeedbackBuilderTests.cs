using Farmer.Core.Models;
using Farmer.Core.Workflow;
using Xunit;

namespace Farmer.Tests.Workflow;

public class FeedbackBuilderTests
{
    private static ReviewVerdict Verdict(
        Verdict v = Farmer.Core.Models.Verdict.Retry,
        int risk = 42,
        List<string>? findings = null,
        List<string>? suggestions = null) => new()
    {
        RunId = "run-xyz",
        Verdict = v,
        RiskScore = risk,
        Findings = findings ?? new List<string>(),
        Suggestions = suggestions ?? new List<string>(),
    };

    [Fact]
    public void Renders_header_with_attempt_counter_and_prior_run_id()
    {
        var md = FeedbackBuilder.Render(Verdict(), priorAttempt: 1, priorRunId: "run-abc");
        Assert.Contains("attempt **2**", md);
        Assert.Contains("Attempt 1", md);
        Assert.Contains("run `run-abc`", md);
        Assert.Contains("verdict **Retry**", md);
        Assert.Contains("42/100", md);
    }

    [Fact]
    public void Renders_findings_as_bullets()
    {
        var md = FeedbackBuilder.Render(
            Verdict(findings: new() { "Missing tests for DataGrid", "No null-safety on onRowClick" }),
            priorAttempt: 1, priorRunId: "run-abc");

        Assert.Contains("## Findings from the prior attempt", md);
        Assert.Contains("- Missing tests for DataGrid", md);
        Assert.Contains("- No null-safety on onRowClick", md);
    }

    [Fact]
    public void Renders_suggestions_section_when_present()
    {
        var md = FeedbackBuilder.Render(
            Verdict(suggestions: new() { "Add sorting assertions" }),
            priorAttempt: 1, priorRunId: "run-abc");

        Assert.Contains("## Suggestions from the reviewer", md);
        Assert.Contains("- Add sorting assertions", md);
    }

    [Fact]
    public void Omits_empty_sections_cleanly()
    {
        var md = FeedbackBuilder.Render(Verdict(), priorAttempt: 1, priorRunId: "run-abc");
        Assert.DoesNotContain("## Findings", md);
        Assert.DoesNotContain("## Suggestions", md);
        Assert.Contains("proceed with the numbered prompts", md);
    }

    [Fact]
    public void Handles_null_prior_run_id()
    {
        var md = FeedbackBuilder.Render(Verdict(), priorAttempt: 1, priorRunId: null);
        Assert.Contains("a prior attempt", md);
        Assert.DoesNotContain("run ``", md);
    }
}
