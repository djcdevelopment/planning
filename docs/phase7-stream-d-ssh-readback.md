# Phase 7 Stream D — SSH-based worker readback

**Why:** the existing `IMappedDriveReader` + `MappedDriveReader` implementation reads worker output by mounting the VM's filesystem as a Windows drive letter via WinFsp/SSHFS-Win. That stack is corrupt on this host (WinFsp.Np not loaded; net use returns error 67; reboot and service restart don't fix). More importantly, the mapped-drive pattern doesn't scale — Phase 8's two-worker farm would need two drive letters, Phase 9's fan-out N+. SSH readback is the right pattern going forward; implementing it now closes Phase 7's loop AND pays down the technical debt before it multiplies.

## Goal

Replace the mapped-drive backend behind `IMappedDriveReader` with SSH-based reads, preserving interface semantics so call sites (`CollectStage`, `RetrospectiveStage`) don't change. After this lands, `Farmer.SmokeTrace.ps1` should complete 7/7 green — including the Retrospective stage calling Azure OpenAI.

## Scope

**In:** new `SshWorkerFileReader : IMappedDriveReader`, DI swap, unit tests.

**Out of scope (follow-ups):**
- Renaming `IMappedDriveReader` → `IWorkerFileReader` (mechanical but touches ~10 files; leave for a cleanup commit).
- Deleting `MappedDriveReader.cs` + `VmConfig.MappedDriveLetter` + `FarmerSettings.SshfsCacheLagMs` — leave in place; harmless dead code for one commit.
- Persistent SSH connection pooling. Open-close per operation is fine for Collect's ~3 reads.

## Design

`SshWorkerFileReader` uses `Renci.SshNet` (already a dependency — see Deliver/Dispatch). One `SshClient` per operation, close cleanly. Authenticates via the same `id_ed25519` path used by Deliver.

**Per-method mapping:**

| Interface method | SSH implementation |
|---|---|
| `ReadFileAsync(vm, path)` | `SshClient.RunCommand("cat -- <quoted abs path>")`, return `stdout`. Throw if exit != 0. |
| `FileExistsAsync(vm, path)` | `SshClient.RunCommand("test -f <path> && echo 1 || echo 0")`, parse output. |
| `WaitForFileAsync(vm, path, timeout)` | Poll `FileExistsAsync` at `ProgressPollIntervalMs` until deadline; on hit, `ReadFileAsync`. No SSHFS cache lag delay — SSH reads are consistent. |
| `ListFilesAsync(vm, path, pattern)` | `SshClient.RunCommand("ls -1 <path>/<pattern>")`, split on newlines. Empty if directory missing (exit != 0). |

**Path resolution:** use `vm.RemoteProjectPath` (absolute, per CLAUDE.md SCP gotcha — does NOT expand `~`) as the base, join with the relative path from the caller. So `CollectStage`'s `Path.Combine("output", "manifest.json")` becomes `/home/claude/projects/{work_request_name}/output/manifest.json` on the VM.

**Path quoting:** single-quote all paths passed to `RunCommand` to avoid shell expansion surprises. Escape any `'` inside paths by `'\''` (standard bash trick). Workers don't create paths with quotes, but defend anyway.

**Connection info:** pulled from `VmConfig` — `SshHost`, `SshUser`, from `FarmerSettings.SshKeyPath` (default `id_ed25519`). Match the pattern `SshVmManager` / `DeliverStage` already use for their SSH clients.

**Timeout:** per-command SSH timeout from `FarmerSettings.SshCommandTimeoutSeconds` (existing field, default 30).

## Territory

- **New:** `src/Farmer.Tools/SshWorkerFileReader.cs`
- **New (tests):** `src/Farmer.Tests/Tools/SshWorkerFileReaderTests.cs` — use a fake `ISshCommandExecutor` or constructor injection so we can test the path-resolution and poll-loop logic without a real VM. If the existing codebase doesn't have an SSH-executor seam, add a minimal one rather than running real SSH in unit tests.
- **Modified:** `src/Farmer.Host/Program.cs` — swap `AddSingleton<IMappedDriveReader, MappedDriveReader>()` → `AddSingleton<IMappedDriveReader, SshWorkerFileReader>()`.
- **Modified (if needed):** `src/Farmer.Tests/Workflow/DiCompositionTests.cs` — update the DI assertion if it checks the concrete type. If it just checks interface resolution, unchanged.

## Do NOT touch

- `src/Farmer.Tools/MappedDriveReader.cs` — leave in place as dead code (deletion is a follow-up commit).
- `src/Farmer.Core/Contracts/IMappedDriveReader.cs` — interface stays. Rename is a follow-up.
- `src/Farmer.Core/Config/VmConfig.cs` `MappedDriveLetter` / `MappedDrivePath` fields — leave (harmless unused).
- `src/Farmer.Core/Config/FarmerSettings.cs` `SshfsCacheLagMs` — leave (harmless unused).
- Call sites (`CollectStage`, `RetrospectiveStage`) — interface stays, no caller changes needed.

## Gates

1. `dotnet build src\Farmer.sln` — 0 warnings, 0 errors.
2. `dotnet test src\Farmer.sln` — 143+ green (existing baseline preserved; new tests additive).
3. Integration gate (orchestrator runs, not builder): `.\infra\Farmer.SmokeTrace.ps1` fake mode → 7/7 green. Retrospective stage fires. Jaeger trace includes a `workflow.stage.Retrospective` span with child span for the Azure OpenAI call.

## Status file

`docs/streams/phase7-stream-d.status.md` in the builder's worktree. Same gate vocabulary as A + B.

## Completion

Single atomic commit suggested: `feat(collect): swap mapped-drive readback for SSH-based worker file reader`. Builder may split if they find a natural seam.

Do NOT push. Orchestrator merges to main, re-fires smoke trace.
