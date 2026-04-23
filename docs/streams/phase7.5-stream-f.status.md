# Phase 7.5 — Stream F status

## [RESEARCH-DONE]

Scope verified. Findings:

- `VmConfig` today carries one shared project path (`RemoteProjectPath`, default `~/projects`). All runs land in the same directory — the collision that `phase7-stress-findings.md` documented for two `discord-bot` work requests on the same VM.
- `DeliverStage` does `rm -rf {plans,output} && mkdir -p {plans,comms,output}` under `vm.RemoteProjectPath`. That wipe is defensive against prior-run contamination and goes away for free once each run gets a fresh, uniquely-named workspace.
- `DispatchStage` runs `cd {RemoteProjectPath} && bash worker.sh <run_id>`. `worker.sh` is deployed at `{RemoteProjectPath}/worker.sh` (per `check-worker-parity.ps1`), so we must keep `cd`-ing there to launch the script even when the workspace moves.
- `worker.sh` reads `PROJECT_ROOT="${HOME}/projects"` hard-coded. No env-var override today. Needs to honor `WORK_DIR` with a legacy fallback for pre-F deployments.
- `CollectStage` uses `IMappedDriveReader` (now backed by `SshWorkerFileReader`) with relative paths. Reader's `ResolveRemotePath` joins `vm.RemoteProjectPath` + the relative path and `TrimStart('/')`s the relative, so an absolute path can't reach it.
- `SshWorkerFileReader.cs` is in the DO-NOT-TOUCH list. Path adjustment has to happen at call sites.

Surprises:

- Run ids in this codebase already carry the `run-` prefix (e.g. `run-20260423-083153-f1a957`). The Stream F brief describes the layout as `run-<run_id>/` but that would double-prefix to `run-run-...`. Went with `{run_id}` verbatim so the on-disk name matches what tracing already calls the run.
- `worker.sh`'s `write_manifest` shells `git status` under `$PROJECT_ROOT` to compute `files_changed`. A fresh per-run workspace isn't a git repo, so `files_changed` would fall through to the `WORKER_NO_CHANGES` sentinel and `CollectStage` would fail its non-empty check. Added a guarded `git init` in `worker.sh` right after the workspace `mkdir -p`.
- `HeartbeatMiddleware` writes `progress.md` under `{RemoteProjectPath}/.comms/` — separate from worker.sh's own `worker-progress.md` under `.comms/` of the run workspace. Left the heartbeat writer alone (out of owned territory and nothing reads `progress.md` back today).

## [DESIGN-READY]

Concrete diff shape:

**Modified — `src/Farmer.Core/Config/VmConfig.cs`**
- Add `RemoteRunsRoot` string, default `/home/claude/runs`.
- Leave `RemoteProjectPath` in place with a back-compat XML doc comment (no `[Obsolete]`; worker.sh still lives under it and DispatchStage still `cd`s there).

**Modified — `src/Farmer.Core/Layout/RunDirectoryLayout.cs`**
- Add VM-side per-run helpers: `VmRunRoot`, `VmRunPlansDir`, `VmRunOutputDir`, `VmRunCommsDir`, `VmRunPlanFile`, `VmRunTaskPacket`.
- Add `ReaderPathForRunOutput(vm, runId, file)` that computes a parent-relative walk from `RemoteProjectPath` to `RemoteRunsRoot/{run_id}/output/{file}`. Callers hand the result to `IMappedDriveReader` so `SshWorkerFileReader.ResolveRemotePath` produces a working absolute POSIX path the kernel collapses at `open(2)` time.

**Modified — `src/Farmer.Core/Workflow/Stages/DeliverStage.cs`**
- Replace `rm -rf + mkdir -p` under `RemoteProjectPath` with `mkdir -p` under the per-run workspace. No wipe needed.
- SCP prompts + task-packet to the per-run `plans/` dir.

**Modified — `src/Farmer.Core/Workflow/Stages/DispatchStage.cs`**
- Still `cd {RemoteProjectPath}` (worker.sh lives there). Prefix the invocation with `WORK_DIR={RemoteRunsRoot}/{run_id}` as a bash single-command env override.

**Modified — `src/Farmer.Core/Workflow/Stages/CollectStage.cs`**
- Compute reader-relative paths via `RunDirectoryLayout.ReaderPathForRunOutput` for manifest, summary, and per-prompt-timing.jsonl.
- No change to the reader interface or impl.

**Modified — `src/Farmer.Worker/worker.sh`**
- `PROJECT_ROOT="${WORK_DIR:-${HOME}/projects}"` — WORK_DIR-first with legacy fallback.
- `git init --quiet` the workspace if `.git` is absent so `write_manifest`'s git-backed enumeration still works.

**New tests / updates**
- `DeliverStageTests`: 5 new/updated tests covering per-run path uploads, no-rm-rf assertion, and two-runs-two-dirs collision check. Old wipe-coverage tests retired along with the rm-rf.
- `DispatchStageTests`: 2 new tests for `WORK_DIR=` prefix placement and `cd` target.
- `CollectStageTests`: refactored to seed reader files at the actual query key (via `ReaderPathForRunOutput`); 1 new test asserts reads target the run_id.
- `CollectStage_PromptSpanTests`: updated to use the per-run reader key helper.
- `RunDirectoryLayoutTests`: 7 new tests covering the new per-run helpers + the reader-path walk (including nested project roots, backslash normalization, and an end-to-end collapse assertion).

No touch to `SshWorkerFileReader.cs`, `IMappedDriveReader.cs`, `HeartbeatMiddleware.cs`, `MafRetrospectiveAgent.cs`, `RetrospectiveStage.cs`, `ArchiveStage.cs` (doesn't exist), `WorkflowPipelineFactory.cs`, `RunPhase.cs`.

## [COMPLETE] <sha>

Verification gates:

- `dotnet build src/Farmer.sln`: clean, 0 warnings, 0 errors.
- `dotnet test src/Farmer.sln`: **164 unit + 5 integration = 169 green** (baseline 157 unit preserved; 7 net new tests covering per-run path behavior).
- Worker parity: `check-worker-parity.ps1` will report DRIFT until orchestrator deploys with `-Deploy`; expected per the stream brief.

Files touched (8):

- `src/Farmer.Core/Config/VmConfig.cs` (added `RemoteRunsRoot`, XML-doc clarifying `RemoteProjectPath` legacy status)
- `src/Farmer.Core/Layout/RunDirectoryLayout.cs` (7 new per-run helpers + `ReaderPathForRunOutput`)
- `src/Farmer.Core/Workflow/Stages/DeliverStage.cs` (per-run mkdir + SCP)
- `src/Farmer.Core/Workflow/Stages/DispatchStage.cs` (WORK_DIR env prefix)
- `src/Farmer.Core/Workflow/Stages/CollectStage.cs` (reader paths via ReaderPathForRunOutput)
- `src/Farmer.Worker/worker.sh` (WORK_DIR honored, workspace git-init)
- `src/Farmer.Tests/Tools/RunDirectoryLayoutTests.cs` (+7 tests)
- `src/Farmer.Tests/Workflow/DeliverStageTests.cs`, `DispatchStageTests.cs`, `CollectStageTests.cs`, `CollectStage_PromptSpanTests.cs` (test reshape for per-run workspace)

Commit not pushed, not merged.
