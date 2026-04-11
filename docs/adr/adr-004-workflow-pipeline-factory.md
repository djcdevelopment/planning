# ADR-004: WorkflowPipelineFactory with per-run fresh middleware

**Status:** Accepted (Phase 5, late)
**Date:** 2026-04-09
**Superseded by:** none

## Context

In early Phase 5, `CostTrackingMiddleware` was registered as a DI singleton and accumulated state (a `List<StageCost>` and a `Stopwatch`) across every stage of every run. That's fine for a single run, but `InboxWatcher` processes runs sequentially, and the singleton's state leaked between runs. The initial fix was a `Reset()` method that `RunWorkflow.ExecuteFromDirectoryAsync` called at the start of each run. It worked but it was a hack — a comment in the code literally said "Phase 5 compromise: not concurrency-safe".

When Phase 5 closed, we looked at the parallel `origin/claude/phase5-otel-api` branch (a different agent had built their own Phase 5 with a different architecture). Their design used a `WorkflowPipelineFactory` that built a fresh `RunWorkflow` **and** a fresh `CostTrackingMiddleware` per call, returning both as a tuple. That's the right answer: per-run isolation by construction, not by discipline.

## Decision

**Introduce `WorkflowPipelineFactory` as the only way to build a `RunWorkflow` instance.** The factory:

1. Resolves all 7 stages and all 3 stateless middlewares from DI (they're singletons — stages have no per-run state, most middleware doesn't either).
2. Constructs a **fresh `CostTrackingMiddleware`** inline with `new CostTrackingMiddleware()`.
3. Builds the middleware list in the correct outermost-first order: `Telemetry → Logging → Eventing → [fresh CostTracking] → Heartbeat`.
4. Returns a tuple: `(RunWorkflow, CostTrackingMiddleware)` so the caller (`InboxWatcher` or `/trigger` endpoint) can call `costTracker.GetReport(runId)` after the pipeline completes and persist it.

`RunWorkflow` is no longer a DI singleton. `CostTrackingMiddleware` is no longer registered in DI at all. The factory is the singleton that knows how to assemble them.

The factory pattern was cherry-picked from `origin/claude/phase5-otel-api` with attribution in the commit message. Their version had a slightly different middleware list (no `EventingMiddleware`, different ordering) — we adapted it to ours.

## Consequences

**Positive:**
- **Concurrency-safe by construction.** Multiple runs can be processed in parallel (not yet wired up in InboxWatcher, but no longer blocked at the architectural level) because each call to `Create()` returns a fresh `CostTrackingMiddleware`.
- **No more `Reset()`** on middleware. The comment admitting concurrency issues is gone. The DI registration is smaller.
- **Cost reports are now persisted per run.** The `GetReport(runId)` call + `IRunStore.SaveCostReportAsync` happens in the calling code, naturally. Previously nothing called GetReport because the middleware was hidden inside the `RunWorkflow.ExecuteAsync` call.
- The factory is testable: `DiCompositionTests.Factory_ReturnsFreshCostTrackerPerCall` pins the invariant.

**Negative:**
- One more type (`WorkflowPipelineFactory`) and one more DI registration (`AddSingleton<WorkflowPipelineFactory>`).
- Callers now write `var (workflow, costTracker) = factory.Create();` instead of just resolving `RunWorkflow` from DI. Slightly more ceremony. Acceptable — the ceremony makes the per-run isolation obvious.

## Related

- `src/Farmer.Core/Workflow/WorkflowPipelineFactory.cs` — the factory itself
- `src/Farmer.Core/Middleware/CostTrackingMiddleware.cs` — the now-`Reset()`-free class
- `src/Farmer.Host/Services/InboxWatcher.cs` — the caller that uses the factory per run
- `origin/claude/phase5-otel-api` — the parallel branch this was cherry-picked from
