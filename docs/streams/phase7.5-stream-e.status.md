# Phase 7.5 Stream E status — retro reads archived source

Branch: `claude/phase7.5-stream-e-retro-source`
Worktree: `C:\work\iso\planning-stream-e`

## Signals

- `[RESEARCH-DONE]` 2026-04-23

  Read `phase7.5-loop-integrity.md`, `phase7-stress-findings.md`, `CLAUDE.md`,
  `MafRetrospectiveAgent.cs`, `RetrospectiveStage.cs`, `IRetrospectiveAgent.cs`,
  `RetrospectivePrompt.cs`, and `RetrospectiveSettings.cs`.

  Existing surface — `RetrospectiveContext` already carries a `SampledOutputs`
  field + `ArtifactSnippet` type, but `RetrospectiveStage` currently sets it
  to `Array.Empty<ArtifactSnippet>()` and never populates it. The prompt
  builder already has a `=== SAMPLED OUTPUT FILES ===` branch that renders
  snippets if present. So the "wire up source files" mechanics are half-done;
  what's missing is the actual reading from `artifacts/`, the new prompt
  shape required by the plan, and the system-prompt language change.

  Baseline tests: 151/152 unit + 5/5 integration (one flaky telemetry test
  on the pre-existing main; not touched by this stream and passed after the
  package bump landed).

- `[DESIGN-READY]` 2026-04-23

  Target diff shape:

  1. `src/Farmer.Core/Contracts/IRetrospectiveAgent.cs`
     - Add `ArtifactsDirectory` to `RetrospectiveContext` (nullable string).
       Stage populates it from `state.RunDirectory`; agent reads it. Leave
       the existing `SampledOutputs` field in place for back-compat — prompt
       falls back to it if the agent didn't pre-load via the new parameter.

  2. `src/Farmer.Agents/Prompts/RetrospectivePrompt.cs`
     - Extend `BuildUserMessage(context)` to
       `BuildUserMessage(context, sourceFiles = null, totalSourceFiles = null)`.
     - Replace the old `=== SAMPLED OUTPUT FILES ===` section with the new
       `## Source files produced` section from the plan, including the
       `<file count>N files (showing M of N; ...)</file count>` header and
       per-file `### <path>` + ``` `<language>` ``` fenced blocks.
     - Empty branch renders `None captured for this run — the retrospective
       is reasoning from the manifest + summary only.`
     - Added `InferLanguageHint` for common extensions (cs/py/js/ts/md/yaml
       /go/rs/json/…). Unknown → bare fence.
     - Update `SystemInstructions` with a new rule block telling the agent
       to prefer source evidence over the worker's self-report and to flag
       contradictions explicitly. This is the load-bearing prompt change
       — without it, the source blocks do nothing.

  3. `src/Farmer.Agents/MafRetrospectiveAgent.cs`
     - New internal `LoadSourceFiles(context, out total)` — defensive
       enumeration of `context.ArtifactsDirectory` bounded by
       `MaxChangedFiles` + `MaxFileBytes`. Prefers `artifacts-index.json`
       (Stream G contract) when present, falls back to a sorted recursive
       directory walk. Per-file failures are swallowed with a warn log so
       one unreadable file doesn't starve the prompt.
     - `AnalyzeAsync` now calls `LoadSourceFiles` before `BuildUserMessage`
       and threads the loaded snippets + total into the prompt.
     - Added a second internal test-seam ctor (`settings`, `logger` only)
       for the load-path tests — subclassing MAF's `AIAgent` would require
       implementing five abstract members whose shapes drift with every
       release, and the load path doesn't touch the AIAgent anyway.
     - `[assembly: InternalsVisibleTo("Farmer.Tests")]`.

  4. `src/Farmer.Core/Workflow/Stages/RetrospectiveStage.cs`
     - Populate `ArtifactsDirectory = Path.Combine(state.RunDirectory,
       "artifacts")` when the state has a run directory (null in the
       in-memory test path, which is the correct "no artifacts" signal).

  5. `src/Farmer.Tests/Farmer.Tests.csproj`
     - Reference `Farmer.Agents` so the new tests can reach `MafRetrospectiveAgent`
       and `RetrospectivePrompt`.
     - Bump `Microsoft.Extensions.Logging.Abstractions` to `10.0.4`,
       `Microsoft.Extensions.Options` + `DependencyInjection` to `10.0.3`,
       add `Microsoft.Extensions.Logging` 10.0.3, drop unused
       `Microsoft.Extensions.Hosting`. Matches Farmer.Agents' 10.x graph
       — Farmer.Tests was on 8.x pins and produced NU1605 downgrade
       errors the moment the reference landed.

  6. `src/Farmer.Tests/Agents/MafRetrospectiveAgentTests.cs` (new, 11 tests)
     - `SystemInstructions_Prefer_Source_Over_Worker_SelfReport` — pins the
       prompt language that's load-bearing for the whole stream.
     - `BuildUserMessage_WithSourceFiles_RendersLabeledCodeBlocks` — the
       "populated" case from the plan gate; checks headers, language hint,
       actual content, truncation surfacing.
     - `BuildUserMessage_WithNoSourceFiles_DegradesGracefully` — "None
       captured" branch.
     - `LoadSourceFiles_PopulatedArtifactsDir_LoadsFiles` — happy path,
       relative paths + forward slashes.
     - `LoadSourceFiles_RespectsMaxChangedFiles_Cap`
     - `LoadSourceFiles_RespectsMaxFileBytes_Cap`
     - `LoadSourceFiles_EmptyArtifactsDirectory_ReturnsEmpty`
     - `LoadSourceFiles_NullArtifactsDirectory_ReturnsEmpty`
     - `LoadSourceFiles_MissingArtifactsDirectory_ReturnsEmpty`
     - `LoadSourceFiles_PrefersArtifactsIndexJson_When_Present` — validates
       the Stream-G index contract.
     - `LoadSourceFiles_MalformedIndex_FallsBackToDirectoryWalk` — safety.

- `[COMPLETE]` <sha>

  Verification gates:

  - `dotnet build src/Farmer.sln` — clean, 0 warnings, 0 errors.
  - `dotnet test src/Farmer.sln` — **168 green** (163 unit + 5 integration,
    0 failed, 0 skipped). Baseline was 151 unit passing / 1 flaky; 11 new
    unit tests additive in `MafRetrospectiveAgentTests`. The flaky
    telemetry test passed too after the Extensions 10.x bump (not
    investigated — orthogonal).
  - Sanity-grep: `ArtifactsDirectory` referenced only in
    `IRetrospectiveAgent.cs` (definition), `MafRetrospectiveAgent.cs`
    (consumer), `RetrospectiveStage.cs` (producer), and the new tests.

  Files touched (7):

  - `src/Farmer.Core/Contracts/IRetrospectiveAgent.cs` (+18 lines, field + xmldoc)
  - `src/Farmer.Agents/Prompts/RetrospectivePrompt.cs` (+70 / -8, new section + lang hint + prompt update)
  - `src/Farmer.Agents/MafRetrospectiveAgent.cs` (+160 / -2, loader + test ctor + InternalsVisibleTo)
  - `src/Farmer.Core/Workflow/Stages/RetrospectiveStage.cs` (+9, artifacts-dir wiring)
  - `src/Farmer.Tests/Farmer.Tests.csproj` (+7 / -4, package graph align)
  - `src/Farmer.Tests/Agents/MafRetrospectiveAgentTests.cs` (new, ~280 lines, 11 tests)
  - `docs/streams/phase7.5-stream-e.status.md` (new, this file)

  Surprises:

  - `SampledOutputs` + `ArtifactSnippet` already existed on
    `RetrospectiveContext`. The ground was half-prepared — just never
    populated and the prompt format was the pre-plan shape. Kept the
    field in place and added `sourceFiles` as an explicit parameter to
    `BuildUserMessage` so the agent can inject pre-loaded snippets without
    mutating the init-only context.
  - Test-seam ctor surprise: `Microsoft.Agents.AI.AIAgent` is abstract
    with five abstract members whose shapes (`RunCoreAsync`,
    `RunCoreStreamingAsync`, `CreateSessionCoreAsync`,
    `SerializeSessionCoreAsync`, `DeserializeSessionCoreAsync`) would
    couple the test to a MAF-internal surface. Added a narrower internal
    ctor that skips the AIAgent entirely — the artifact-loader path is
    pure I/O and doesn't need it.
  - Package graph pain: pulling `Farmer.Agents` into `Farmer.Tests`
    dragged the whole MAF 10.x Extensions chain in. Had to bump the test
    project's pins and drop `Microsoft.Extensions.Hosting` (unused).

  Commit not pushed. Orchestrator owns the merge after F + G land.
