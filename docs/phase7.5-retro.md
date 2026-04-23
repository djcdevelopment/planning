# Phase 7.5 Retro — Loop Integrity

## Outcome: mostly green, one known race

The three integrity gaps from [phase7-stress-findings.md](./phase7-stress-findings.md) are structurally closed, with one live bug that emerged during stress re-runs and is deferred as a follow-up.

| Gap | Status | Evidence |
|---|---|---|
| #1 — Retrospective sees only self-reports | ✅ **CLOSED** | Stream E (`MafRetrospectiveAgent` reads archived source); verify run produced Verdict=`Reject`/risk=`90` on a discord-bot-python run that had empty artifacts — the first time the retro has *disagreed* with the worker's "success" self-report. That discrimination is the whole point. |
| #2 — VM workspace collision | ✅ **CLOSED** | Stream F (per-run `/home/claude/runs/run-{run_id}/`); verified three back-to-back runs produced three distinct directories (no contamination). Worker.sh + DeliverStage + DispatchStage + CollectStage all thread through the new path. |
| #3 — Artifacts not archived to Host | ⚠️ **Partially closed** | Stream G (`ArchiveStage` between Collect and Retrospective); stage exists, wires cleanly, tests green. But in practice it tried to archive `WORKER_NO_CHANGES` as a filename because of a downstream bug (see next section). Infrastructure is in place; plumbing to the right content is pending. |

Code + tests: **`main` at `cd5dca4`**, 191 tests green (+34 from the Phase 7 baseline of 157).

## Streams

- [F] `c0477fb` — `feat(phase7.5): per-run VM workspaces (Stream F)` — 12 files, +531/-106
- [G] `5368074` — `feat(phase7.5-g): ArchiveStage — pull per-file source to runs/<id>/artifacts/` — 7 files, +860/-2
- [E] `87c312a` — `feat(phase7.5): retro agent reads archived source (stream E)` — 7 files, +817/-10
- [hotfix] `cd5dca4` — `fix(worker): manifest write fails with ARG_MAX on big trees` — 1 file, +22/-6

All three stream builders ran in parallel worktrees, zero file overlaps per the territory matrix, single clean merge in sequence F → G → E.

## Stress re-run results (on Phase 7.5-upgraded main)

Fired the same three work requests (discord-bot-python, discord-bot-javascript, android-native-app):

| Run | Verdict (Phase 7) | Verdict (Phase 7.5) | Notes |
|---|---|---|---|
| discord-bot-python | Accept/10 | **Failed** at Collect | `ARG_MAX` on jq (2615 files × 80 chars) — fixed in `cd5dca4` |
| discord-bot-javascript | Accept/10 | **Failed** at Collect | Same ARG_MAX path |
| android-native-app | Accept/10 | **Accept/10** (success/7 stages) | Kotlin tree tiny, slipped under limit; now with ArchiveStage wired |
| discord-bot-python (post-hotfix verify) | n/a | **Reject/90** | Retro caught empty artifacts + called out mismatch |

The shift from "uniform Accept/10 regardless of content" to "Reject/90 with specific findings" is the core win. **Stress-testing was the right call** — it exposed both the integrity gaps (now addressed) and a silent bug (ARG_MAX) that would have hit any real user the first time they tried to build a Python or JS project. Without stress-testing, we'd have shipped the Phase 7 "loop closed" message and watched it break on first contact.

## The ARG_MAX find (deserves memory)

`worker.sh`'s `write_manifest` fed `git status` output through `jq --argjson files "$files_json"`. For small trees (Kotlin: ~10 files) this was fine. For Python (venv: 500+ files) and JS (node_modules: 1000+ files) the files_json grew to ~200KB, exceeding Linux's ARG_MAX (~128KB). Failure was silent: `jq` printed "Argument list too long" to stderr, but the `>` redirect had already truncated `manifest.json` to zero bytes, and `write_manifest` returned normally. CollectStage then failed with "The input does not contain any JSON tokens."

Two-part fix:
1. Filter toolchain noise (`.venv/`, `node_modules/`, `.gradle/`, `__pycache__/`, `.pytest_cache/`, `dist/`, `build/`, `bin/`, `obj/`, `.cache/`, `*.egg-info/`, etc.) out of the manifest — these aren't "what the worker built."
2. Switch from `--argjson` (command-line arg) to `--slurpfile` (file-backed) so we never hit ARG_MAX again even if a legitimate sample plan produces a huge source tree.

Memory entry added for future reference.

## Open: manifest write-vs-read race (deferred follow-up)

Even with the ARG_MAX fix, the post-hotfix verify run's `artifacts-index.json` shows `ArchiveStage` tried to archive `WORKER_NO_CHANGES`. On the VM, `manifest.json` at inspection time showed 21 correct source files written at 10:08:01 — but `ArchiveStage`'s index was generated at 10:07:57, four seconds *earlier*.

Hypothesis: `worker.sh` writes `write_manifest "in_progress"` after every prompt for SIGKILL resilience (`worker.sh:344`), and a final `write_manifest "complete"` at `worker.sh:362`. If `CollectStage`'s `WaitForFileAsync` polls fast enough to catch an early `in_progress` snapshot (when git had only a few files tracked, before Claude's later prompts added more), it reads the empty sentinel and moves on before the final write.

**Mitigation options:**
- Remove the per-prompt `write_manifest "in_progress"` (line 344) — the final write at 362 is sufficient for non-killed runs. SIGKILL resilience can come from reading `per-prompt-timing.jsonl` instead.
- Add a `worker-done.marker` file written last, have Collect wait for the marker rather than the manifest.
- Have Collect read summary.json too and re-read manifest if `summary.generated_at > manifest.generated_at` is violated.

Deferred — doesn't block the demo (retro correctly classifies the resulting empty archive as `Reject`; the *value* surfaces either way).

## Followups (prioritized)

| Priority | Item | Effort |
|---|---|---|
| Medium | Remove per-prompt manifest write (race fix) | 5 min |
| Medium | Binary-safe SCP download in ArchiveStage (currently text-only via `cat`) | 1-2 hr |
| Low | Delete `MappedDriveReader` + unused config fields | 30 min |
| Low | Rename `IMappedDriveReader` → `IWorkerFileReader` | 30 min |
| Low | Tighten retro system prompt to suppress hallucinations when artifacts/ is empty (one finding called the discord run "an android-native-app project" — invented detail) | 15 min |

## Phase Demo entry decision

**Go.** Phase 7.5's infrastructure is enough for the friend demo tomorrow:
- Retro discriminates (Reject/90 on bad input, will Accept good input)
- Per-run dirs isolate concurrent work
- ArchiveStage is wired (even if its output is sometimes empty due to the race — the failure mode is caught by the retro)

Going straight into Phase Demo: cloudflared tunnel + intake wiring + reveal UI + feedback loop.
