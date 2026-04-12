# Phase 6 Retrospective Verification — First Real QA Run

## Date: 2026-04-12

## What happened

Dropped `sample-request.json` (work request: `react-grid-component`) into the inbox on the `claude/phase6-retrospective-loop` branch with `OPENAI_API_KEY` set. The full 7-stage pipeline ran against claudefarm2 using the Phase 5 fake worker. For the first time, the `RetrospectiveStage` called a real OpenAI `gpt-4o-mini` model via MAF and produced three new artifacts in the run directory.

## Run ID

`run-20260412-115013-f6869c`

## Results

| Check | Result |
|---|---|
| Pipeline completed | `result.success: true`, `final_phase: Complete` |
| All 7 stages ran | 14 events in events.jsonl (7 x 2) |
| `review.json` exists | Yes — verdict: `Reject`, risk_score: 100 |
| `qa-retro.md` exists | Yes — identified the fake worker run correctly |
| `directive-suggestions.md` exists | Yes — 1 suggestion scoped to `1-SetupProject.md` |
| Pipeline did NOT gate on verdict | Correct — `success: true` despite `Reject` verdict (ADR-007) |
| `cost-report.json` has 7 stages | Yes |
| `state.json` phase | `Complete` |

## What the agent said

The retrospective agent correctly identified this as a fake calibration run:

**Findings:**
- "The worker did not make any real changes to the project files."
- "The execution was marked as a fake calibration run, indicating it did not fulfill its intended purpose."

**Suggestions:**
- "Future runs should ensure that the worker is correctly initialized to perform actual work."
- "Documentation should clarify the expectations for a fake worker run and when to use it."

**Directive suggestion:**
- Scope: `prompts`, Target: `1-SetupProject.md`
- Rationale: "This change will help clarify the execution expectations and enable the worker to deliver actual results."
- Suggested: "Ensure the worker is set to execute real tasks instead of simulating a run that does not produce outputs."

This is exactly the kind of learning output ADR-007 is designed to produce. The agent saw a meaningless run, said so, and suggested what to change. A human reading this would know immediately: "the prompts are fine, the worker was fake, swap in the real worker and re-run."

## Retrospective stage timing

- Retrospective started: 11:50:14.996 UTC
- Retrospective completed: 11:50:22.472 UTC
- **Duration: ~7.5 seconds** (round-trip to OpenAI gpt-4o-mini with full run context in the prompt)

## Run folder contents

```
run-20260412-115013-f6869c/
├── artifacts/
├── cost-report.json          (7 stages)
├── directive-suggestions.md  (NEW — from retrospective agent)
├── events.jsonl              (14 lines)
├── logs/
├── qa-retro.md               (NEW — from retrospective agent)
├── request.json
├── result.json               (success: true)
├── review.json               (NEW — from retrospective agent, verdict: Reject)
├── state.json                (phase: Complete)
└── task-packet.json
```

## What this proves

1. **MAF + OpenAI integration works end-to-end.** The `Farmer.Agents` isolation pattern holds: one project, stable packages, typed structured output via `RunAsync<RetrospectiveDto>`, token accounting, all flowing through Aspire.
2. **QA is a post-mortem, not a gate.** The agent returned `Reject` (correctly — it's a fake run). The pipeline still completed successfully. The run directory has every artifact. No data was lost. The verdict is metadata for learning.
3. **Directive suggestions are contextual.** The agent didn't just say "this was bad" — it pointed at a specific prompt file, quoted the current content, and suggested what should change. A human can action this without re-reading the entire run.
4. **The anti-drift invariant holds.** `events.jsonl` shows `Retrospective.stage.completed`. `state.json` shows `phase: Complete`. `result.json` shows `success: true`. All three agree.
5. **AutoPass fallback works when key is unset.** Tested separately: without `OPENAI_API_KEY`, the pipeline completes with all 7 stages green, just no retrospective artifacts. `RetrospectiveStage` returns `Succeeded` silently.

## What's next

This verification run uses the Phase 5 fake worker. The next major commit (real `worker.sh`) will produce actual code changes on the VM. When that lands, the retrospective agent will review real diffs instead of fake sentinels — and the directive suggestions should become much more interesting.
