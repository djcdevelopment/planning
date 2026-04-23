# Phase Demo — Stream K status

Scope: tighten `RetrospectivePrompt.SystemInstructions` so the MAF retrospective agent
stops confabulating explanations ("unrelated Android project", "run_id mismatch", etc.)
when the `artifacts/` directory is empty or sparse. System-prompt change only — no
behavior change in the stages, agent, or settings.

## [RESEARCH-DONE]

Ground-truthing the hallucination pattern + prompt surface:

- **Three observed hallucinations** in today's rehearsal runs, all sharing the shape
  "worker-claimed success + sparse Source files produced section → retro invents a
  misrouting story":
  - `run-20260423-104056-38a14a`: "worker manifest references a different run_id
    and unrelated prompts" — false.
  - `run-20260423-085202-b71a9a`: "unrelated android-native-app project" — no such
    reference in inputs.
  - `run-20260423-103031-7f0162`: "worker manifest and summary relate to an unrelated
    Android app project" — neither did.
- **Prompt lives in `RetrospectivePrompt.cs`** (`SystemInstructions` const). Stream E's
  rule #6 ("Prefer source evidence over the worker's self-report") is the closest
  existing clause; the new anti-hallucination guidance is folded in at the same level
  so the overall tone stays consistent. The block already tells the agent what to do
  when source is present; the gap is what to do when it *isn't*.
- **`BuildUserMessage` already emits "None captured for this run"** for empty
  `sourceFiles` (line 189). No wiring change needed — the agent sees a stable sentinel
  string, and the new system-instruction clause teaches it how to respond to that
  specific sentinel.
- **Tests pin the existing rule** (`SystemInstructions_Prefer_Source_Over_Worker_SelfReport`).
  New test asserts the anti-hallucination / no-fabrication language appears in the
  charter so future edits don't silently drop it.

## [DESIGN-READY]

Concrete deliverables:

- `src/Farmer.Agents/Prompts/RetrospectivePrompt.cs` — extend the existing rule #6
  block (source-as-ground-truth) with three new directives, phrased in the same
  tone as the surrounding charter:
  1. When "Source files produced" says "None captured for this run," state that in
     findings rather than speculating about contents / unrelated projects / run-id
     mismatch / prompt-worker misalignment.
  2. Do not reference projects, run IDs, or filenames that don't appear in the
     inputs. Quote verbatim from the manifest/summary/source-files section if you
     cite something.
  3. When missing source is the only concern, prefer `retry` with a moderate risk
     score over `reject` with a 90+ score — missing source is ambiguous, not
     evidence of a write-off.
- `src/Farmer.Tests/Agents/MafRetrospectiveAgentTests.cs` — one new `[Fact]`
  (`SystemInstructions_Guard_Against_Speculation_On_Empty_Source`) asserting the
  three new directives land in `SystemInstructions`. Additive; does not alter any
  existing test.

## [COMPLETE] <sha>

Verification:

- `dotnet build src/Farmer.sln` — clean, **0 warnings, 0 errors**.
- `dotnet test src/Farmer.sln` — **221 green** (199 unit + 22 integration,
  0 failed, 0 skipped). `MafRetrospectiveAgentTests` grew from 11 → 12 with
  the additive assertion; all existing tests unchanged.

Files touched (1 MOD + 1 MOD + 1 NEW):

- MOD `src/Farmer.Agents/Prompts/RetrospectivePrompt.cs` — three-paragraph extension
  of rule #6; no other rules, scoring rubric, schema, or `BuildUserMessage` code
  changed.
- MOD `src/Farmer.Tests/Agents/MafRetrospectiveAgentTests.cs` — one additive
  assertion that the new anti-speculation language is present.
- NEW `docs/streams/retro-hallucination-fix.status.md` — this file.

Commit not pushed, not merged. Orchestrator owns the merge + rehearsal.
