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
    public void Renders_specific_directives_section_when_present()
    {
        var directives = new List<DirectiveSuggestion>
        {
            new()
            {
                Scope = DirectiveScope.Prompts,
                Target = "1-SetupProject.md",
                CurrentValue = "No strict mode toggle called out",
                SuggestedValue = "Explicitly enable tsconfig strict + noUncheckedIndexedAccess",
                Rationale = "Future runs keep missing strict-mode guard rails",
            },
            new()
            {
                Scope = DirectiveScope.ClaudeMd,
                Target = "Your Environment section",
                SuggestedValue = "Note the Node version pinned by the VM",
                Rationale = "Prompts that install packages should know the Node floor",
            },
        };

        var md = FeedbackBuilder.Render(
            Verdict(), priorAttempt: 1, priorRunId: "run-abc", directives: directives);

        Assert.Contains("## Specific directives", md);
        Assert.Contains("[Prompts -> 1-SetupProject.md]", md);
        Assert.Contains("Future runs keep missing strict-mode guard rails", md);
        Assert.Contains("Suggested: `Explicitly enable tsconfig strict + noUncheckedIndexedAccess`", md);
        Assert.Contains("Current: `No strict mode toggle called out`", md);
        Assert.Contains("[ClaudeMd -> Your Environment section]", md);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(new object[] { new string[0] })]
    public void Omits_directives_section_when_list_is_null_or_empty(object? _)
    {
        // Note: Theory data param just documents the two intents; we pass
        // both null and empty explicitly below regardless of the data item.
        var mdNull = FeedbackBuilder.Render(
            Verdict(findings: new() { "f" }), priorAttempt: 1, priorRunId: "run-abc", directives: null);
        var mdEmpty = FeedbackBuilder.Render(
            Verdict(findings: new() { "f" }), priorAttempt: 1, priorRunId: "run-abc",
            directives: Array.Empty<DirectiveSuggestion>());

        Assert.DoesNotContain("## Specific directives", mdNull);
        Assert.DoesNotContain("## Specific directives", mdEmpty);
        // But the Findings section still rendered -- confirming the directives
        // branch is the only thing that changed behavior.
        Assert.Contains("## Findings from the prior attempt", mdNull);
    }

    [Fact]
    public void Current_value_omitted_when_directive_has_null_current()
    {
        var directives = new List<DirectiveSuggestion>
        {
            new()
            {
                Scope = DirectiveScope.TaskPacket,
                Target = "feedback",
                CurrentValue = null,   // no current diff-point
                SuggestedValue = "Populate on retry",
                Rationale = "Enables downstream directive chaining",
            },
        };

        var md = FeedbackBuilder.Render(
            Verdict(), priorAttempt: 1, priorRunId: "run-abc", directives: directives);

        Assert.Contains("[TaskPacket -> feedback]", md);
        Assert.Contains("Suggested: `Populate on retry`", md);
        Assert.DoesNotContain("Current:", md);
    }

    [Fact]
    public void Handles_null_prior_run_id()
    {
        var md = FeedbackBuilder.Render(Verdict(), priorAttempt: 1, priorRunId: null);
        Assert.Contains("a prior attempt", md);
        Assert.DoesNotContain("run ``", md);
    }
}
