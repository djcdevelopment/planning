# ADR-003: Anti-drift contract between events.jsonl, state.json, and result.json

**Status:** Accepted (Phase 5)
**Date:** 2026-04-09
**Superseded by:** none

## Context

Phase 5 introduced three separate files per run that all describe the run's state from slightly different angles:

- **`events.jsonl`** — append-only log, one JSON object per stage transition. Historical record.
- **`state.json`** — latest-snapshot, overwritten atomically after each stage. Current view.
- **`result.json`** — written once when the pipeline completes (or fails). Terminal outcome.

The architectural claim is that filesystem is the source of truth. That claim only holds if the files agree. In early Phase 5 testing, the first real failure (SSH passphrase blowing up at Deliver) surfaced **Bug 1**: `state.json` showed `phase: "Delivering"` while `result.json` showed `final_phase: "Failed"`. Same run, two files, two contradictory stories. The filesystem-is-truth claim collapsed the moment we looked at a failure.

The root cause: `EventingMiddleware` wrote the state snapshot in its catch block *before* anyone advanced `state.Phase` to `Failed`. The phase transition happened later in `RunWorkflow`, after the exception bubbled past the middleware. The snapshot froze the in-flight phase forever.

A parallel bug (**Bug 2**): the success path had the same "one step behind" drift. On a clean run, `state.json` showed the second-to-last stage as "in progress" and stages_completed missing the final stage, while `result.json` correctly reported `final_phase: "Complete"` with all 7 stages.

## Decision

**For every completed run — success or failure — `events.jsonl`, `state.json`, and `result.json` must agree on the final phase and stages_completed list.** This is an invariant, pinned by regression tests that can never be disabled.

Implementation:
1. `EventingMiddleware` catch blocks set `state.LastError` and `state.AdvanceTo(RunPhase.Failed)` **before** writing the snapshot. Same for `StageOutcome.Failure` from the middleware's normal return path.
2. `RunWorkflow.ExecuteFromDirectoryAsync` writes a **final** authoritative `state.json` after the pipeline returns, alongside the existing `result.json` write. This closes the success-path drift — by the time the final write happens, `state.Phase` and `state.StagesCompleted` reflect reality.
3. Two regression tests pin the invariant:
   - `BugRegression_FailedRun_AllThreeFilesAgreeOnFailedPhase` (exception path + file check)
   - `BugRegression_SuccessfulRun_FinalStateJsonAgreesWithResult` (clean completion)
4. Both tests assert the full tuple: phase, stages_completed, error field. If any future change reintroduces drift, these tests fail.

## Consequences

**Positive:**
- The filesystem-as-source-of-truth claim holds on every run, including failures.
- Any consumer reading the run directory can trust the three files consistently.
- The regression tests make this a bug that can't silently come back.
- The invariant is documented in code comments at the exact write-sites (`EventingMiddleware.cs`, `RunWorkflow.cs`), so future readers don't have to chase this ADR to understand why.

**Negative:**
- The middleware's exception-path catch block has side effects beyond pure logging (mutating `state`). Slightly worse SRP, but the alternative is worse (drift).
- Two writes of `state.json` per run (one from middleware after the last stage, one from `ExecuteFromDirectoryAsync` at the end). The second is always a superset of the first; the cost is one extra atomic write per run. Acceptable.

## Related

- [docs/phase5-build-log.md §12](../phase5-build-log.md) — the real-world-verification section that documented Bug 1 + Bug 2 with receipts
- [docs/phase5-pattern.md](../phase5-pattern.md) — "Invariant 2: Eventing and telemetry tell the same story"
- `src/Farmer.Core/Middleware/EventingMiddleware.cs` — the catch block + the anti-drift contract comment at the top
- `src/Farmer.Core/Workflow/RunWorkflow.cs` — the final state.json write in `ExecuteFromDirectoryAsync`
- `src/Farmer.Tests/Workflow/RunFromDirectoryTests.cs` — the regression tests
