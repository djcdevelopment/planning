# Phase 7 Stress-Test Findings — 3 Additional Work Requests

Ran post-retro stress tests against the Phase-7-closed pipeline on 2026-04-23, using three new work requests in addition to the original `react-grid-component`: `discord-bot-python`, `discord-bot-javascript`, `android-native-app`. All completed 7/7 green in real mode with Azure OpenAI retrospective firing. Results exposed three integrity gaps that block the pipeline from being production-ready, independent of Phase 8's farm-expansion work.

## Results at a glance

| Run | Work request | Duration | Verdict | Risk | VM project dir | Files produced |
|---|---|---|---|---|---|---|
| `run-20260423-083153-f1a957` | react-grid-component | 4m 10s | Accept | 10 | `/projects/react-grid-app/` | (pre-existing, idempotent) |
| `run-20260423-084831-796053` | discord-bot-python | 3m 14s | Accept | 10 | `/projects/discord-bot/` | Python files, tests |
| `run-20260423-085202-b71a9a` | discord-bot-javascript | 3m 58s | Accept | 10 | `/projects/discord-bot/` (same!) | JS files layered on Python |
| `run-20260423-085650-c34d12` | android-native-app | 3m 58s | Accept | 10 | `/projects/ClickCounter/` | Kotlin/Gradle skeleton |

**All four runs: identical verdict (Accept) and identical risk score (10).** The retrospective agent is not discriminating between runs of different complexity and varying completeness.

## Gap #1 — Retrospective integrity (most critical)

The retrospective agent reasons exclusively from the worker's self-report (`manifest.json` + `summary.json` — file list + narrative), not from the actual source code Claude produced. This is why every run gets `Accept/risk=10`: if the worker says "done" and lists files, the retro has no independent signal.

**Evidence:**
- JS-bot run: retro saw manifest listing JS files, produced clean `Accept`. Never noticed the VM dir also contained the Python bot's files from the prior run — because they weren't in the manifest.
- Discord-bot-python retro suggestion ("Encourage explicitly running tests and reporting their results in the final output to verify test coverage visually") hints that the agent is aware it can't verify tests ran; it's asking for better self-reports because it can't see the truth directly.
- Android run: retro suggested "explicitly document the absence and inability to run gradle build" — again, asking for richer narration because it has no direct visibility into the actual state.

**Root cause:** `CollectStage` reads only `manifest.json` and `summary.json` from the VM. `RetrospectiveStage` receives those two + the prompts. Source files never enter the agent's context.

**Fix shape:**
- Option A: have `CollectStage` pull a manifest-listed subset of source files (bounded — `FarmerSettings.Retrospective.MaxFileBytes` is already configured at 8192, but not used for this purpose yet).
- Option B: pass a file-tree snapshot (path-only) so the agent can at least compare against the manifest.
- Option C: let the retrospective agent invoke a tool to read specific files on demand (would need MCP or a callback). Highest-fidelity, most complex.

Rec: **A first** (bounded source-file read). Pre-sized context is cheap; MCP-style tool invocation is Phase 10 territory.

## Gap #2 — VM workspace collision + contamination

Claude on the VM chooses project-directory names by its own convention, not from `work_request_name`. Seen:
- `react-grid-component` → `/projects/react-grid-app/` (shortened)
- `discord-bot-python` → `/projects/discord-bot/`
- `discord-bot-javascript` → **same** `/projects/discord-bot/` (collision)
- `android-native-app` → `/projects/ClickCounter/` (picked from the prompt's explicit `rootProject.name`)

When the two Discord bots collided, the JS run did NOT wipe the Python code — it layered its `src/commands/*.js`, `package.json`, `tests/commands/*.test.js` directly alongside `src/bot/*.py`, `pyproject.toml`, `tests/test_commands.py`. Snapshot confirmed 33 files from both runs coexisting in `/projects/discord-bot/`.

**Consequences:**
- Second run's context is polluted by first run's files — Claude may (unpredictably) react to pre-existing artifacts.
- Phase 8's multi-worker farm can't rely on work-request names to isolate workspaces.
- Retry chains (PR #8 / ADR-011) technically work because they reuse the same dir deliberately, but that's the only case where collision is OK.

**Fix shape:** `DeliverStage` should create per-run directories on the VM, not per-work-request. Suggestion: `/home/claude/runs/{run_id}/` as the project root, with `run_id` tagged into the prompt context so Claude uses it. Requires a small worker.sh change + a Host-side path update. Old `/projects/{name}/` convention stays as a secondary-use slot if desired.

## Gap #3 — Artifacts not archived to Farmer

Farmer's per-run directory on the Host (`planning-runtime/runs/<run_id>/`) has `artifacts/` and `logs/` subdirectories, both **empty** on every run. The actual code Claude produced lives only on the VM and gets layered-into or persists-across runs.

**Consequences:**
- Post-mortem review requires SSHing to the VM — not self-contained.
- Phase 8 multi-worker means evidence is scattered across workers.
- No durable record of what Claude produced for any given run. Retry-feedback chains can reference the prior run's verdict (via `parent_run_id`) but not the prior run's actual code.

**Fix shape:** after `CollectStage` reads the manifest, SCP-pull the manifest-listed files into `runs/<run_id>/artifacts/`. Use `FarmerSettings.Retrospective.MaxChangedFiles` + `MaxFileBytes` for bounding. `SshWorkerFileReader` can do this; we already have the SSH path.

## Positive findings

Not everything gap-shaped. Three things worked well:

1. **Directive suggestions are useful.** Even with limited context, the retro agent produced concrete prompt-rewrite proposals across all runs. The Android retro nailed the "explicitly document inability to build" suggestion — a real improvement that a human reviewer would likely have proposed.
2. **Pipeline stability.** 4/4 real-mode runs completed 7/7 green without infrastructure errors. SSH readback (Stream D) held up across varied work types.
3. **Azure OpenAI retro latency is consistent.** 15-30s per run regardless of workload size. Cost-per-retro bounded.

## Phase 7.5 — scope proposal

Three focused streams, each small:

- **Stream E — Retrospective source visibility.** `CollectStage` + `RetrospectiveStage` pass bounded source content to the agent. Target: retro flags real issues in a deliberately-broken sample plan.
- **Stream F — Per-run VM workspace isolation.** `DeliverStage` uses `/home/claude/runs/{run_id}/` as project root; worker.sh prompt gets `WORK_DIR=<path>`; Claude's prompt context mentions the dir. Target: two back-to-back runs with same work request don't contaminate each other.
- **Stream G — Artifact archival to Host.** `CollectStage` SCP-pulls manifest-listed files into the run dir's `artifacts/`. Target: post-run review from the Host only, no SSH to VM needed.

All three can run in parallel (different files, different call sites). Total wall-clock: ~4-6 hrs if parallel, single session.

**Prerequisite before Phase 8:** Streams E + F + G should land first. They're all small, they all pay forward into Phase 8's multi-worker future, and they close the retrospective integrity gap that stress-testing exposed. Phase 8 would compound each of these problems (N workers × contamination, N workers × empty artifacts, N workers × retro blindness).

## Evidence permalinks

- Python bot run: http://localhost:16686/trace/b51b63b8673c42df9b9c19949cccf572
- JS bot run: http://localhost:16686/trace/3f973e5fe1b626c0cbe0820450fbb0d0
- Android run: http://localhost:16686/trace/8d82904b9fac98f8223e6d8265e35941
- Run dirs: `C:\work\iso\planning-runtime\runs\run-20260423-08{4831,5202,5650}-*\`
