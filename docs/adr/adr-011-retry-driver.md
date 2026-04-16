# ADR-011: In-process retry driver with feedback injection

**Status:** Accepted (Phase 7)
**Date:** 2026-04-15
**Superseded by:** none

## Context

Phase 6 shipped the verdict producer: `MafRetrospectiveAgent` + `RetrospectiveStage` run as the 7th and final pipeline stage, producing a `ReviewVerdict` (Accept/Retry/Reject, risk_score, findings, suggestions) plus three artifact files per run ([ADR-007](./adr-007-qa-as-postmortem.md)). ADR-007 deliberately left the verdict *consumer* unbuilt: retrospective verdicts are descriptive metadata, not control signals. The pipeline always succeeds regardless of verdict value.

Phase 7 asks: what happens when the verdict says Retry?

Three options:

1. **Human-driven re-submit.** The human reads `qa-retro.md`, agrees with the findings, and manually POST's a new `/trigger` with improved prompts. No code change. This is what Phase 6 already supports — `parent_run_id` exists on `RunRequest` for chain linking, and the retrospective agent's context includes the attempt number.

2. **In-process retry driver.** The `/trigger` handler loops: run the workflow, check the verdict, and if the policy says retry, re-run with feedback from the prior attempt injected as a synthetic prompt. Bounded, synchronous, auditable. The caller sees the chain in the response.

3. **NATS-event-driven retry.** `RetrospectiveStage` emits a `farmer.run.retry_requested` event to the FARMER_RUNS stream. A `RetryCoordinator` hosted service subscribes, re-submits via a new `/trigger` call. Decoupled, async, but harder to debug and requires reasoning about event ordering + at-least-once semantics.

## Decision

**Option 2: in-process retry driver, opt-in per request.**

The `/trigger` handler delegates to `RetryDriver.RunAsync(triggerTempFile)`, which:
1. Runs the full 7-stage workflow for attempt N.
2. Reads `WorkflowResult.ReviewVerdict` (new: `ReviewVerdict` is now copied from `RunFlowState` into `WorkflowResult.FromState()`).
3. Checks the `RetryPolicy` from the request: `{ enabled, max_attempts, retry_on_verdicts }`.
4. If the verdict is in `retry_on_verdicts` and `attempts.Count < max_attempts`, builds feedback via `FeedbackBuilder.Render(verdict, ...)` and creates a new run directory with `parent_run_id` set and `Feedback` populated.
5. Loops to step 1.

Feedback injection uses the existing prompt infrastructure — no worker.sh change needed:
- `LoadPromptsStage` checks `RunRequest.Feedback`; if non-empty, prepends a synthetic `PromptFile { Order = 0, Filename = "0-feedback.md", Content = feedback }`.
- `worker.sh`'s `find ~/projects/plans -name '[0-9]*-*.md'` sorts `0-feedback.md` before `1-*.md` naturally.
- The VM's `CLAUDE.md` already tells Claude: *"If this is a retry run, the first prompt will include reviewer feedback at the top."*

Default behavior is unchanged: callers that don't include `retry_policy` in the POST body get a single-attempt response identical to Phase 6. The driver short-circuits immediately when `RetryPolicy` is null or `Enabled=false`.

## Consequences

**Positive:**
- **Opt-in, bounded, auditable.** Retry is never implicit. The request says how many times and on which verdicts. Each attempt is a separate run directory with its own `request.json`, `result.json`, `events.jsonl`. The chain is linked via `parent_run_id`.
- **Feedback is natural.** No special protocol — it's a prompt file that sorts first. Claude sees it as the first prompt of the session. Worker.sh doesn't change.
- **ADR-007 invariant preserved.** The retrospective is still post-mortem — it writes artifacts and produces a verdict. The retry driver is a *caller of the pipeline*, not a stage in it. If you remove the driver, the pipeline still works. If the verdict is null (AutoPass, API failure), the driver short-circuits.
- **Backward-compatible response shape.** Single-attempt calls return the same `WorkflowResult` as before. Multi-attempt calls return `{ attempts: [...], final: ... }` — the `final` key matches the old shape.

**Negative:**
- **Two Jaeger traces per retry chain.** Each `workflow.run` starts a new Activity with its own traceId. Debugging a chain means opening two+ URLs. Mitigated by the smoke script printing traceIds per attempt.
- **Two token spends per retry.** The VM worker runs Claude CLI again; the host's retrospective agent calls OpenAI again. Real retries cost real money. `max_attempts=2` is the default cap; callers must explicitly opt higher.
- **No automated pattern learning across chains.** The driver feeds forward findings from attempt N to attempt N+1, but doesn't aggregate across runs of the same `work_request_name`. Future work: directive-suggestion accumulation across chains.
- **RetryDriver integration test is deferred.** `WorkflowPipelineFactory` resolves stages by concrete type, making spy-stage injection via DI awkward. The driver's components (FeedbackBuilder, LoadPromptsStage feedback, RetryPolicy model) are individually unit-tested. Follow-up: extract `IWorkflowRunner` for testability.
- **Synchronous blocking of /trigger.** A retry chain with real Claude takes `N * 5-20 min`. The HTTP call blocks for the whole chain. Acceptable in dev; a production deployment would either (a) add a timeout or (b) switch to the NATS-event-driven variant (option 3, deferred).

## Future: event-driven retry (not built)

When Farmer grows a second process or a UI that wants to fire-and-forget retries, option 3 becomes worth the complexity. The implementation path:
1. Add a `farmer.run.retry_requested` subject to the NATS subject namespace.
2. `RetryDriver` emits this event instead of looping in-process.
3. A new `RetryCoordinator : IHostedService` subscribes, calls the driver for each event.
4. The `/trigger` response becomes 202 Accepted with a correlation ID; callers poll `/runs/{runId}` for completion.

This is additive — the in-process driver stays for CLI/smoke/CI use cases; the event-driven path is for async / multi-process scenarios.
