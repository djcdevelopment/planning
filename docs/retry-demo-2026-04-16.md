# Retry loop fires on a real verdict — 2026-04-16 demo

Companion to [docs/session-retro-2026-04-15.md](./session-retro-2026-04-15.md). Yesterday's Phase 7 retry demos all used `retry_on_verdicts: ["Accept"]` because fake-mode output was clean enough that gpt-4o-mini verdicted `Accept` every time. Today: `worker.sh` gains a `fake-bad` mode that produces adversarial output on the first attempt and clean output on the retry (triggered by the presence of `task-packet.json.feedback`). The retrospective now has realistic failure signal to flag, and the loop fires on a verdict the agent actually assigned.

## Demo command

```powershell
curl.exe -X POST -H "Content-Type: application/json" -d '{
  "work_request_name": "react-grid-component",
  "worker_mode": "fake-bad",
  "retry_policy": { "enabled": true, "max_attempts": 2,
                    "retry_on_verdicts": ["Retry", "Reject"] }
}' http://localhost:5100/trigger
```

`retry_on_verdicts` includes `Reject` deliberately: the synthetic failure is catastrophic enough (`BUILD FAILED`, all prompts exit=1, `WORKER_NO_CHANGES` in manifest) that the retrospective logically verdicts `Reject`, not `Retry`. Either is valid grounds for a retry in this demo.

## Results

### Attempt 1 — `run-20260416-085840-f946f1`

- Duration: 9.53s
- Mode dispatch: `fake-bad` branch, no feedback → `run_claude_fake_bad`
- Prompt outputs: 3 × `exit=1`, explicit `BUILD FAILED` / `npm ERR!` messages
- Retrospective (`gpt-4o-mini`):
  - **Verdict: Reject**, risk_score 100
  - Findings: *"All prompts failed, resulting in 0% success rate."* / *"The worker was in 'fake-bad' mode, which may have influenced the failure outcomes."*
  - Suggestions: *"Reassess the prompts for clarity and completeness in instructions."* / *"Check the environment configuration to ensure compatibility with task requirements."*

### Driver action

- `verdict=Reject` is in `retry_on_verdicts=["Retry","Reject"]` → retry fires
- `FeedbackBuilder.Render` emits markdown with attempt counter, prior run ID, findings, and suggestions
- New run directory created with `attempt_id=2`, `parent_run_id="run-20260416-085840-f946f1"`, `feedback` = rendered markdown

### Attempt 2 — `run-20260416-085849-edfe3a`

- Duration: 5.23s
- Mode dispatch: `fake-bad` branch, **feedback present** → falls through to `run_claude_fake` (clean output)
- Prompt outputs: 3 × `exit=0`, no `SUMMARY_ISSUES`
- Retrospective (`gpt-4o-mini`):
  - **Verdict: Accept**, risk_score 20
  - Findings: *"All prompts succeeded, showing a 100% success rate **this time**."* ← the agent explicitly noticed the improvement
  - Suggestions: *"Consider re-evaluating the worker's mode configuration for future runs to optimize performance."* / *"Maintain clarity and completeness in prompts to avoid confusion in subsequent tasks."*

### Response shape

```json
{
  "attempts": [ <r1>, <r2> ],
  "final": <r2>
}
```

Total wall clock: ~15s for both attempts + two real OpenAI calls + NATS publishes + ObjectStore uploads.

## Why this is the closing argument for Phase 7

- **Real verdicts from the real model, not a contrived config flag.** The retry chain was driven by the agent disagreeing with the output, not by us forcing `Accept` into `retry_on_verdicts`.
- **Feedback injection demonstrably changed behavior.** Attempt 1 failed; attempt 2 succeeded because `task-packet.json.feedback` was populated and `worker.sh` branched on its presence.
- **Quality explicitly improved between attempts.** Reject (risk 100) → Accept (risk 20). The agent even narrated the improvement in natural language.

## Mechanics to carry forward

- **`WORKER_MODE=fake-bad`** is permanent infrastructure now -- useful for any future retry-loop testing without burning Claude credits.
- **`retry_on_verdicts`** should include both `Retry` and `Reject` in production config, since the line between "address feedback" and "stop trying" is context-dependent. Farmer's default is `["Retry"]` only; callers who want Reject-retry opt in.
- **The retrospective agent will verdict `Reject` on catastrophic failures** -- it's not lenient. This is a signal that the retry policy's `retry_on_verdicts` list is a real policy knob, not a UI nicety.

## Known limitations (documented, not blocked on)

- **Chain of 2 is the demo ceiling.** Fake-bad's feedback-aware branch only has two states (bad → good). A real three-attempt chain would need worker.sh to vary output further based on attempt count, or the retry loop to terminate earlier.
- **Feedback markdown is human-targeted, not machine-structured.** `FeedbackBuilder` renders ReviewVerdict.Findings + Suggestions (string lists). `DirectiveSuggestion[]` (structured scope + target + rationale) isn't threaded yet — backlog item.
- **Each attempt is a separate Jaeger trace.** The retry chain's two attempts have distinct traceIds. ADR-011 documents this as a known limitation; outer-span wrapping is deferred.

## Traces + NATS evidence

- Jaeger: two entries under service `Farmer`, traceIds from the response's `attempts[i]` (check `/trace/{traceId}` for either)
- NATS FARMER_RUNS stream gained ~28 messages (14 per attempt × 2 attempts)
- OBJ_farmer-runs-out bucket gained entries for `run-20260416-085840-f946f1/*` and `run-20260416-085849-edfe3a/*`
- Cost reports: `C:\work\iso\planning-runtime\runs\<runId>\cost-report.json` exist for both attempts (verifies the regression fix from the companion commit)
