# ADR-007: QA is a post-mortem, never a gate

**Status:** Accepted (Phase 6)
**Date:** 2026-04-11
**Superseded by:** none

## Context

The original Phase 6 sketch in `docs/implementation-plan.md` (written months earlier) described QA as a gating step: the retrospective agent reviews the worker's output, returns a verdict, and if the verdict is "Retry" the pipeline automatically re-runs the work request with injected feedback. `FarmerSettings.MaxRetries` defaults to 2, so each work request would potentially become up to 3 full runs.

When we got to Phase 6 planning, the user's framing reframed this completely. Two load-bearing quotes:

> "I thought QA would run after builds completed, more of a post-mortem on quality that can impact the next set of planning prompts or the VM CLI's builder's .md 'directive'"

> "that is still data, which means that is still success"

The implication: a "bad" run isn't something the system should try to fix automatically. A bad run is the same as a good run in the sense that it produces data we can learn from. The QA agent's job is to extract that learning, not to decide whether the current run "passes". Retry is a human-driven mechanism (re-drop the inbox file if you want another attempt) or a future Phase 7+ feature (automated re-runs based on accumulated pattern data).

## Decision

**QA is a post-mortem. It runs as the last stage of the workflow, writes its artifacts, and never gates pipeline success.** Specifically:

1. **`RetrospectiveStage` is the 7th and final pipeline stage.** It reads the run directory (locally, after `CollectStage` has copied everything in), calls `IRetrospectiveAgent.AnalyzeAsync`, writes the outputs, and returns `StageResult.Succeeded`. It never returns `Failure` unless the agent infrastructure itself collapsed **and** `RetrospectiveSettings.FailureBehavior == Fail`.
2. **`result.json.success` is determined by the pipeline, not by the verdict.** If the workflow reaches `RetrospectiveStage`, `result.success = true` regardless of what the verdict says. Verdict lives in `review.json` as metadata.
3. **No `RetryCoordinator`.** No automatic re-runs. `FarmerSettings.MaxRetries` stays defined but unused by Phase 6 (reserved for a future phase that opts in per work-request).
4. **The agent's verdict enum is `Accept | Retry | Reject`** — but the names are **descriptive, not prescriptive**. "Retry" means "if you ran this again it would probably go better with the suggestions". "Reject" means "this is not salvageable as-is". Neither triggers automatic action. They feed the human reader of `qa-retro.md` and the downstream consumer of `directive-suggestions.md`.
5. **Directive suggestions are the learning channel.** The retrospective agent produces `directive-suggestions.md` with entries scoped to `Prompts | ClaudeMd | TaskPacket`. A future phase (manual for now) applies them to the next run's inputs. Phase 6 writes them; nothing reads them automatically.

Failure modes for the retrospective stage itself:
- **OpenAI API unreachable** → retry up to `MaxAgentCallRetries + 1` total (default 3). If still failing, apply `FailureBehavior`.
- **`FailureBehavior.AutoPass`** (default) → stage succeeds, no `review.json` written, no metrics recorded. The run is complete, just without a learning artifact.
- **`FailureBehavior.Fail`** → stage fails, `result.success = false`. Only for environments where retrospectives are load-bearing for a downstream consumer.

## Consequences

**Positive:**
- **Simpler code.** No retry loop, no feedback injection path in Phase 6. `RetryCoordinator` doesn't exist. `TaskPacket.Feedback` exists but isn't populated by anything.
- **"Data is the product" holds.** Every completed run has the same `result.success = true` shape. Failures (in the sense of bad worker output) are data, not errors.
- **Clear separation of concerns.** Pipeline success answers "did the machinery work?". Verdict answers "was the output any good?". Two independent questions, two independent files.
- **Retrospective failure doesn't fail the run.** A broken OpenAI API key doesn't cascade — the pipeline still produces all 7 stages of artifacts and marks the run complete. We just lose the learning for that run.

**Negative:**
- **No automatic recovery.** If the worker produces garbage, the human has to notice and re-drop the inbox file. For experimental / manual-review workflows, that's fine. For unattended production batches (not yet a goal), it's not.
- **A future retry loop will need to bolt onto this.** Phase 7+ may add opt-in retry based on `task-packet.json.retry_on_reject` or similar. The infrastructure for that retry lives nowhere in Phase 6, so adding it later will touch `InboxWatcher`, `RunDirectoryFactory`, and possibly introduce a `RetryCoordinator`. Acceptable cost given how cheap the Phase 6 simplification is.

## Related

- [ADR-005](./adr-005-farmer-agents-blast-radius.md) — where the retrospective agent lives
- [ADR-006](./adr-006-openai-over-anthropic-maf.md) — which LLM the retrospective agent calls
- `src/Farmer.Core/Contracts/IRetrospectiveAgent.cs` — the contract, note the "never throws on infra failure" rule
- `src/Farmer.Core/Config/RetrospectiveSettings.cs` — `FailureBehavior.AutoPass` default
