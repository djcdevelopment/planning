using Farmer.Agents;
using Farmer.Agents.Prompts;
using Farmer.Core.Config;
using Farmer.Core.Contracts;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Farmer.Tests.Agents;

/// <summary>
/// Phase 7.5 Stream E — the retrospective agent reads archived source
/// files from the run's <c>artifacts/</c> directory and includes them in
/// the LLM prompt, so it can reason from ground truth rather than just
/// the worker's self-reported manifest + summary.
///
/// We don't mock <see cref="Microsoft.Agents.AI.AIAgent"/> here — the
/// AIAgent surface is a moving target. Instead we pin two things:
///   (1) the pure prompt builder (<see cref="RetrospectivePrompt"/>)
///       renders the "Source files produced" section correctly for both
///       populated and empty cases, and the system instructions prefer
///       source evidence over the worker's self-report;
///   (2) the agent's internal <see cref="MafRetrospectiveAgent.LoadSourceFiles"/>
///       respects the <see cref="RetrospectiveSettings"/> caps and
///       degrades gracefully when <c>artifacts/</c> is missing or empty.
///
/// Together these cover the whole prompt-wiring path without spinning up
/// Azure OpenAI.
/// </summary>
public class MafRetrospectiveAgentTests
{
    private static RetrospectiveSettings DefaultSettings() => new()
    {
        MaxChangedFiles = 10,
        MaxFileBytes = 8192,
    };

    private static MafRetrospectiveAgent MakeAgentForLoadTest(RetrospectiveSettings settings)
    {
        // Use the artifact-loader-only test-seam ctor. Subclassing the
        // MAF AIAgent would require implementing five abstract members
        // whose signatures drift across releases — LoadSourceFiles is a
        // pure I/O path that never touches the AIAgent.
        return new MafRetrospectiveAgent(
            Options.Create(settings),
            NullLogger<MafRetrospectiveAgent>.Instance);
    }

    private static RetrospectiveContext MakeContext(string? artifactsDir) => new()
    {
        RunId = "run-test-e",
        ManifestJson = "{\"files_changed\":[]}",
        SummaryJson = null,
        ArtifactsDirectory = artifactsDir,
    };

    [Fact]
    public void SystemInstructions_Prefer_Source_Over_Worker_SelfReport()
    {
        // The prompt's charter must tell the model to treat source as
        // ground truth and the worker's retro/manifest/summary as claims.
        // Without this, the model tends to echo the worker's self-report
        // (which is what produced the blanket Accept/risk=10 on the four
        // stress runs — see phase7-stress-findings.md).
        var sys = RetrospectivePrompt.SystemInstructions;

        Assert.Contains("Prefer source evidence", sys, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Source files produced", sys, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("contradicts the worker's self-report", sys, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SystemInstructions_Guard_Against_Speculation_On_Empty_Source()
    {
        // Phase Demo Stream K — three live rehearsal runs (104056-38a14a,
        // 085202-b71a9a, 103031-7f0162) had the retro invent explanations
        // ("unrelated Android project", "run_id mismatch", "prompt/worker
        // misalignment") when the Source files produced section was empty.
        // The charter now explicitly forbids that pattern; pin the exact
        // anti-hallucination phrases so future prompt edits can't silently
        // drop them.
        var sys = RetrospectivePrompt.SystemInstructions;

        // Rule #7 — name the "None captured" sentinel so the agent links
        // its response behavior to the specific user-message cue.
        Assert.Contains("None captured for this run", sys, StringComparison.Ordinal);
        Assert.Contains("speculation without evidence", sys, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("caveat on your verdict", sys, StringComparison.OrdinalIgnoreCase);

        // Rule #8 — no invented cross-references.
        Assert.Contains("Do not invent cross-references", sys, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("quote it verbatim", sys, StringComparison.OrdinalIgnoreCase);

        // Rule #9 — prefer retry + moderate risk over reject + 80+ when
        // the only problem is missing source.
        Assert.Contains("neutral wording when evidence is thin", sys, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("ambiguous", sys, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void BuildUserMessage_WithSourceFiles_RendersLabeledCodeBlocks()
    {
        var ctx = MakeContext(artifactsDir: null);
        var snippets = new ArtifactSnippet[]
        {
            new()
            {
                Path = "src/bot.py",
                Content = "import discord\n# TODO: actually implement the bot\n",
                TotalBytes = 48,
                Truncated = false,
            },
            new()
            {
                Path = "README.md",
                Content = "# discord-bot\n\nA discord bot.\n",
                TotalBytes = 2048,
                Truncated = true,
            },
        };

        var prompt = RetrospectivePrompt.BuildUserMessage(ctx, snippets, totalSourceFiles: 2);

        Assert.Contains("## Source files produced", prompt);
        // File count + sampling hint present so the model knows what it's seeing.
        Assert.Contains("2 files", prompt);

        // Labeled headers per file.
        Assert.Contains("### src/bot.py", prompt);
        Assert.Contains("### README.md", prompt);

        // Language hint from extension — python for .py, markdown for .md.
        Assert.Contains("```python", prompt);
        Assert.Contains("```markdown", prompt);

        // Actual content made it through.
        Assert.Contains("# TODO: actually implement the bot", prompt);
        Assert.Contains("A discord bot.", prompt);

        // Truncation is surfaced on the header line so the model doesn't
        // over-commit to a finding about "missing" content.
        Assert.Contains("truncated", prompt, StringComparison.OrdinalIgnoreCase);

        // The fallback "None captured" string must NOT appear when we have
        // files — otherwise we'd be telling the agent to ignore the source.
        Assert.DoesNotContain("None captured for this run", prompt);
    }

    [Fact]
    public void BuildUserMessage_WithNoSourceFiles_DegradesGracefully()
    {
        var ctx = MakeContext(artifactsDir: null);

        var prompt = RetrospectivePrompt.BuildUserMessage(ctx, sourceFiles: null);

        Assert.Contains("## Source files produced", prompt);
        Assert.Contains("None captured for this run", prompt);
        Assert.Contains("manifest + summary only", prompt);
        // No stray empty code fence.
        Assert.DoesNotContain("```\n```", prompt);
    }

    [Fact]
    public void LoadSourceFiles_PopulatedArtifactsDir_LoadsFiles()
    {
        using var tmp = new TempDir();
        var artifactsDir = Path.Combine(tmp.Path, "artifacts");
        Directory.CreateDirectory(Path.Combine(artifactsDir, "src"));
        File.WriteAllText(
            Path.Combine(artifactsDir, "src", "bot.py"),
            "print('hello')\n");
        File.WriteAllText(
            Path.Combine(artifactsDir, "README.md"),
            "# bot\n");

        var agent = MakeAgentForLoadTest(DefaultSettings());
        var ctx = MakeContext(artifactsDir);

        var loaded = agent.LoadSourceFiles(ctx, out var total);

        Assert.Equal(2, total);
        Assert.Equal(2, loaded.Count);

        // Paths are relative to artifacts/ and use forward slashes on
        // every platform — the LLM prompt should be host-agnostic.
        var paths = loaded.Select(s => s.Path).OrderBy(p => p).ToArray();
        Assert.Equal(new[] { "README.md", "src/bot.py" }, paths);

        // Content loaded verbatim (no BOM, no trailing weirdness).
        var bot = loaded.Single(s => s.Path == "src/bot.py");
        Assert.Equal("print('hello')\n", bot.Content);
        Assert.False(bot.Truncated);
    }

    [Fact]
    public void LoadSourceFiles_RespectsMaxChangedFiles_Cap()
    {
        using var tmp = new TempDir();
        var artifactsDir = Path.Combine(tmp.Path, "artifacts");
        Directory.CreateDirectory(artifactsDir);
        for (var i = 0; i < 5; i++)
            File.WriteAllText(Path.Combine(artifactsDir, $"f{i}.txt"), $"file {i}");

        var settings = DefaultSettings();
        settings.MaxChangedFiles = 2;
        var agent = MakeAgentForLoadTest(settings);

        var loaded = agent.LoadSourceFiles(MakeContext(artifactsDir), out var total);

        Assert.Equal(5, total); // total on disk unchanged
        Assert.Equal(2, loaded.Count); // cap enforced
    }

    [Fact]
    public void LoadSourceFiles_RespectsMaxFileBytes_Cap()
    {
        using var tmp = new TempDir();
        var artifactsDir = Path.Combine(tmp.Path, "artifacts");
        Directory.CreateDirectory(artifactsDir);
        var big = new string('x', 5_000);
        File.WriteAllText(Path.Combine(artifactsDir, "big.txt"), big);

        var settings = DefaultSettings();
        settings.MaxFileBytes = 1024;
        var agent = MakeAgentForLoadTest(settings);

        var loaded = agent.LoadSourceFiles(MakeContext(artifactsDir), out _);

        var snippet = Assert.Single(loaded);
        Assert.True(snippet.Truncated);
        Assert.Equal(1024, snippet.Content.Length);
        Assert.Equal(5_000, snippet.TotalBytes);
    }

    [Fact]
    public void LoadSourceFiles_EmptyArtifactsDirectory_ReturnsEmpty()
    {
        // Load-bearing defensive path: Stream G may not have landed yet,
        // or a specific run may have produced no files. The agent must
        // degrade — not throw, not add a stub snippet, not add an empty
        // block.
        using var tmp = new TempDir();
        var artifactsDir = Path.Combine(tmp.Path, "artifacts");
        Directory.CreateDirectory(artifactsDir); // empty

        var agent = MakeAgentForLoadTest(DefaultSettings());
        var loaded = agent.LoadSourceFiles(MakeContext(artifactsDir), out var total);

        Assert.Empty(loaded);
        Assert.Equal(0, total);
    }

    [Fact]
    public void LoadSourceFiles_NullArtifactsDirectory_ReturnsEmpty()
    {
        var agent = MakeAgentForLoadTest(DefaultSettings());
        var loaded = agent.LoadSourceFiles(MakeContext(artifactsDir: null), out var total);

        Assert.Empty(loaded);
        Assert.Equal(0, total);
    }

    [Fact]
    public void LoadSourceFiles_MissingArtifactsDirectory_ReturnsEmpty()
    {
        var agent = MakeAgentForLoadTest(DefaultSettings());
        var loaded = agent.LoadSourceFiles(
            MakeContext(Path.Combine(Path.GetTempPath(), $"farmer-nonexistent-{Guid.NewGuid():N}")),
            out var total);

        Assert.Empty(loaded);
        Assert.Equal(0, total);
    }

    [Fact]
    public void LoadSourceFiles_PrefersArtifactsIndexJson_When_Present()
    {
        using var tmp = new TempDir();
        var artifactsDir = Path.Combine(tmp.Path, "artifacts");
        Directory.CreateDirectory(artifactsDir);

        File.WriteAllText(Path.Combine(artifactsDir, "a.txt"), "a");
        File.WriteAllText(Path.Combine(artifactsDir, "b.txt"), "b");
        File.WriteAllText(Path.Combine(artifactsDir, "c.txt"), "c");

        // Index names only b and a (in that order) — c should be skipped,
        // and order should follow the index.
        File.WriteAllText(
            Path.Combine(artifactsDir, "artifacts-index.json"),
            "[\"b.txt\", \"a.txt\"]");

        var agent = MakeAgentForLoadTest(DefaultSettings());
        var loaded = agent.LoadSourceFiles(MakeContext(artifactsDir), out var total);

        Assert.Equal(2, total);
        Assert.Equal(new[] { "b.txt", "a.txt" }, loaded.Select(s => s.Path).ToArray());
    }

    [Fact]
    public void LoadSourceFiles_MalformedIndex_FallsBackToDirectoryWalk()
    {
        using var tmp = new TempDir();
        var artifactsDir = Path.Combine(tmp.Path, "artifacts");
        Directory.CreateDirectory(artifactsDir);
        File.WriteAllText(Path.Combine(artifactsDir, "a.txt"), "a");
        File.WriteAllText(Path.Combine(artifactsDir, "b.txt"), "b");
        File.WriteAllText(
            Path.Combine(artifactsDir, "artifacts-index.json"),
            "not json at all {{");

        var agent = MakeAgentForLoadTest(DefaultSettings());
        var loaded = agent.LoadSourceFiles(MakeContext(artifactsDir), out var total);

        // The index itself should never show up as a source-file entry
        // (we filter artifacts-index.json by name on the fallback path).
        Assert.Equal(2, total);
        Assert.DoesNotContain(loaded, s => s.Path.EndsWith("artifacts-index.json"));
    }

    // ---- helpers ----

    /// <summary>Self-deleting temp directory for file-io tests.</summary>
    private sealed class TempDir : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            $"farmer-stream-e-{Guid.NewGuid():N}");

        public TempDir() => Directory.CreateDirectory(Path);

        public void Dispose()
        {
            try { if (Directory.Exists(Path)) Directory.Delete(Path, recursive: true); }
            catch { /* best effort */ }
        }
    }

}
