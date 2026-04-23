# Phase 7.5 — Loop Integrity

**Why this exists:** stress-testing the Phase-7-closed pipeline with four real-mode runs revealed three structural gaps — all four runs got Accept/risk=10 regardless of workload. See [phase7-stress-findings.md](./phase7-stress-findings.md) for the evidence. These gaps compound with Phase 8's multi-worker expansion, so we close them first.

## The three streams (parallel)

Orthogonal territory — each stream owns a distinct set of files. No shared edits to high-traffic files like `CollectStage.cs`.

```
Stream F — per-run VM workspaces
  owns: DeliverStage.cs, DispatchStage.cs, CollectStage.cs, worker.sh, VmConfig.cs (path fields)

Stream G — artifact archival as NEW stage
  owns: Farmer.Core/Workflow/Stages/ArchiveStage.cs (NEW),
        Farmer.Core/Workflow/WorkflowPipelineFactory.cs (wire between Collect+Retro),
        stage-list constants if any

Stream E — retro reads archived source
  owns: MafRetrospectiveAgent.cs, RetrospectiveStage.cs (context passthrough only)
```

Stream E depends on Stream G's output at runtime (G populates `artifacts/`, E reads from it). If artifacts/ is empty, E degrades to today's behavior — safe to land in any order.

## Stream F — Per-run VM workspaces

**Problem:** Claude on the VM chooses the project dir name by its own convention (`discord-bot` for both the Python and JS bots, colliding). Host has no say.

**Fix:** Host establishes the VM project dir at `{VmConfig.RemoteRunsRoot}/run-{run_id}/` (default `/home/claude/runs/run-<id>/`). Worker.sh receives the path as `WORK_DIR` env var and uses it for cwd. The initial prompt context tells Claude which directory to use.

**Territory (owned):**
- `src/Farmer.Core/Workflow/Stages/DeliverStage.cs` — resolve destination path from run_id, SCP to `/home/claude/runs/run-<id>/` + subdirs (`plans/`, `output/`).
- `src/Farmer.Core/Workflow/Stages/DispatchStage.cs` — pass `WORK_DIR=/home/claude/runs/run-<id>` when invoking worker.sh.
- `src/Farmer.Core/Workflow/Stages/CollectStage.cs` — read from `/home/claude/runs/run-<id>/output/` via `SshWorkerFileReader`.
- `src/Farmer.Core/Config/VmConfig.cs` — add `RemoteRunsRoot` field (default `/home/claude/runs`). Leave `RemoteProjectPath` in place for back-compat (deprecated; follow-up cleanup).
- `src/Farmer.Worker/worker.sh` — read `WORK_DIR` env var; if unset, fall back to legacy `/home/claude/projects/...` path for pre-F runs.

**DO NOT TOUCH:** `MafRetrospectiveAgent.cs`, `RetrospectiveStage.cs`, `ArchiveStage.cs`, `WorkflowPipelineFactory.cs`, `IMappedDriveReader`, `SshWorkerFileReader`.

**Gate:**
- `dotnet test` — existing + new tests green.
- Integration gate (orchestrator re-runs smoke): two back-to-back `Farmer.SmokeTrace.ps1 -WorkerMode real` calls with the same `-WorkRequest` produce two separate `/home/claude/runs/run-<id>/` directories on the VM, and neither contaminates the other.

## Stream G — Artifact archival stage

**Problem:** Farmer's `runs/<id>/artifacts/` is empty every run. All code lives on the VM and gets layered/wiped.

**Fix:** new `ArchiveStage` runs between Collect and Retrospective. Reads the manifest from state (Collect already loaded it), then for each file under `FarmerSettings.Retrospective.MaxChangedFiles` + `MaxFileBytes` cap, pulls it down via SSH (text-safe `cat`) and writes to `runs/<run_id>/artifacts/<relative path>`.

**Territory (owned):**
- `src/Farmer.Core/Workflow/Stages/ArchiveStage.cs` — new stage class, implements `IWorkflowStage`. Name = "Archive", Phase = `RunPhase.Archiving` (new enum value in Models; see below).
- `src/Farmer.Core/Models/RunPhase.cs` — add `Archiving` enum value between `Collecting` and `Reviewing`. **Territory-safe because it's an enum addition, not an edit to logic.**
- `src/Farmer.Core/Workflow/WorkflowPipelineFactory.cs` — insert `ArchiveStage` in the stage list between `CollectStage` and `RetrospectiveStage`.
- `src/Farmer.Host/Program.cs` — DI registration for `ArchiveStage`.
- New tests: `src/Farmer.Tests/Workflow/ArchiveStageTests.cs`.

**DO NOT TOUCH:** `CollectStage.cs`, `RetrospectiveStage.cs`, `MafRetrospectiveAgent.cs`, `DeliverStage.cs`, `DispatchStage.cs`, `worker.sh`, `VmConfig.cs`.

**Gate:**
- `dotnet test` — existing + new tests green.
- Integration gate: after a real-mode smoke, `runs/<id>/artifacts/` contains the files listed in `manifest.json`, bounded by `MaxChangedFiles` × `MaxFileBytes`.

## Stream E — Retro reads archived source

**Problem:** Retrospective agent reasons from manifest + summary only. Never sees actual source — can't discriminate quality.

**Fix:** `MafRetrospectiveAgent` reads the per-run artifacts/ directory (populated by G's ArchiveStage), loads each file bounded by `MaxChangedFiles` + `MaxFileBytes`, and includes them in the LLM prompt as labeled code blocks. `RetrospectiveStage` passes the run-directory path through to the agent.

**Defensive:** if artifacts/ is empty (e.g., Stream G hasn't landed yet, or a specific run produced no files), degrade gracefully — don't fail, don't add empty blocks. Log a warning.

**Territory (owned):**
- `src/Farmer.Agents/MafRetrospectiveAgent.cs` — read artifacts/, format source content into prompt. Respect `MaxChangedFiles` + `MaxFileBytes` caps already in `RetrospectiveSettings`.
- `src/Farmer.Core/Workflow/Stages/RetrospectiveStage.cs` — pass run directory path to agent context (if not already available).
- `src/Farmer.Core/Contracts/IRetrospectiveAgent.cs` — extend method signature if needed to accept source content / run dir. Keep back-compat if possible.
- `src/Farmer.Core/Config/OpenAISettings.cs` or `RetrospectiveSettings.cs` — config field if a new switch is needed (rec: use existing `MaxChangedFiles` + `MaxFileBytes`).
- Tests: `src/Farmer.Tests/Agents/MafRetrospectiveAgentTests.cs` — assert source appears in the prompt when artifacts/ has content; absent when empty.

**DO NOT TOUCH:** `CollectStage.cs`, `DeliverStage.cs`, `DispatchStage.cs`, `ArchiveStage.cs`, `WorkflowPipelineFactory.cs`, `worker.sh`, `VmConfig.cs`, `IMappedDriveReader`, `SshWorkerFileReader`.

**Gate:**
- `dotnet test` — existing + new tests green.
- Integration gate: after all three streams merged, re-run `android-native-app` (the run that should have flagged "build not attempted but claimed success"). Retro verdict should now cite actual file contents (not just the summary) and risk score should differ from the current blanket 10.

## Territory matrix

| File | Stream F | Stream G | Stream E |
|---|---|---|---|
| `DeliverStage.cs` | ✅ owns | — | — |
| `DispatchStage.cs` | ✅ owns | — | — |
| `CollectStage.cs` | ✅ owns | — | — |
| `worker.sh` | ✅ owns | — | — |
| `VmConfig.cs` | ✅ owns | — | — |
| `ArchiveStage.cs` | — | ✅ owns (NEW) | — |
| `WorkflowPipelineFactory.cs` | — | ✅ owns | — |
| `RunPhase.cs` | — | ✅ owns (enum addition) | — |
| `Program.cs` DI | — | ✅ owns (one line) | — |
| `MafRetrospectiveAgent.cs` | — | — | ✅ owns |
| `RetrospectiveStage.cs` | — | — | ✅ owns |
| `IRetrospectiveAgent.cs` | — | — | ✅ owns |

Zero overlap. Three builders run truly parallel.

## Gate protocol

Each stream writes `docs/streams/phase7.5-stream-{e,f,g}.status.md` with `[RESEARCH-DONE]`, `[DESIGN-READY]`, `[BLOCKED]`, `[COMPLETE] <sha>` per the established pattern.

## Merge + validation

Orchestrator merges in order: **F → G → E** (not because of strict dependency, but because worker.sh needs to deploy before new-style runs work, and E wants G's artifacts/ to be populated for the validation run to show impact).

After all three merge, re-fire the three stress work requests (discord-bot-python, discord-bot-javascript, android-native-app) and compare verdicts against the pre-Phase-7.5 baseline. Expected:
- No more `/projects/discord-bot/` collision — two distinct `runs/run-<id>/` dirs on VM.
- `runs/<id>/artifacts/` populated on all three.
- Retro verdicts cite file contents + plausibly differ across runs (different risk scores, specific findings).

## Non-goals

- Deleting `MappedDriveReader` + `VmConfig.MappedDriveLetter` + `FarmerSettings.SshfsCacheLagMs` + `VmConfig.RemoteProjectPath` — dead code cleanup, later.
- Renaming `IMappedDriveReader` → `IWorkerFileReader` — mechanical, later.
- Binary-safe artifact download (images, compiled outputs) — v1 is text-only via `cat`. Upgrade to SCP-based binary download when a sample plan produces binaries.
- Retrospective agent using MCP to pull additional files on demand — Phase 10 territory.

## Exit definition

Phase 7.5 done when:
- Streams F, G, E merged to `main` with `--no-ff`.
- `dotnet test` green on merged main.
- Re-ran stress tests show (a) no VM dir collision, (b) artifacts populated, (c) retro verdicts with real content-grounded reasoning.
- `phase7.5-retro.md` written.
- Phase 8 entry decision: go / no-go.
