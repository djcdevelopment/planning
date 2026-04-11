# End-to-End Verification with the Fake Worker

## What this is

A calibration step that bridges Phase 5 (orchestration is built and tested in isolation) and Phase 6 (orchestration drives a real Claude CLI worker on the VM). It uses a deliberately fake `worker.sh` that performs no actual work but produces enough output for `CollectStage` to succeed, allowing the entire 7-stage pipeline to run end-to-end against real infrastructure for the first time.

## Why we need it

After Phase 5, the 95 tests cover every component in isolation but no test exercises the assembled pipeline against real SSH, real SCP, and a real mapped drive. The dashboard has always shown red at the Deliver or Dispatch stage. Without this bridge, we don't know whether:

- `Renci.SshNet` actually authenticates against the real VM
- `ScpUploadContentAsync` actually puts files where the next stage expects them
- The ~500ms SSHFS cache lag retry actually fires under real conditions
- `MappedDriveReader.WaitForFileAsync` correctly polls the mapped drive
- The atomic JSON deserialization in `CollectStage` handles real-world whitespace, encoding, and line endings

The fake worker lets us answer all of these without writing a real worker. It also gives us a baseline of what an all-green dashboard run looks like, so future regressions stand out clearly instead of hiding among the existing red stages.

## What the fake worker does (and doesn't)

Lives at `src/Farmer.Worker/worker.sh`. Tracked in version control with LF line endings enforced via `.gitattributes`. Manually uploaded to `~/projects/worker.sh` on the target VM via `scp` — see "How to install" below.

**Does:**
- Writes `~/projects/.comms/progress.md` with `phase: complete` and a YAML-frontmatter marker `fake_worker: true`
- Writes `~/projects/output/manifest.json` with `files_changed: ["FAKE_WORKER_NO_REAL_CHANGES"]`
- Writes `~/projects/output/summary.json` with a description that says "Fake worker calibration run"
- Exits 0

**Does not:**
- Read any prompt files
- Run Claude CLI
- Touch any source code
- Create any git branches
- Make any commits
- Push anything anywhere

A run that uses this worker leaves **zero footprint** outside the target VM's `~/projects/` directory.

## How to identify a fake-worker run

Three independent markers, in case any one of them gets lost:

1. **In the manifest:** `~/projects/output/manifest.json` (also visible via the mapped drive, e.g. `O:\projects\output\manifest.json`) contains the literal string `"FAKE_WORKER_NO_REAL_CHANGES"` in `files_changed`. No real worker will ever produce this value.
2. **In the heartbeat:** `~/projects/.comms/progress.md` has `fake_worker: true` in its YAML front matter.
3. **In the summary:** `~/projects/output/summary.json` has a `description` field that begins with "Fake worker calibration run".

If you see any of these in a run folder, you are looking at a calibration run, not real work.

## How to install on a target VM

Run once per VM, manually, from a PowerShell prompt on the Windows host. This project runs on a Windows 10 machine — use PowerShell, not Bash.

```powershell
scp D:\work\planning\src\Farmer.Worker\worker.sh claude@claudefarm2:~/projects/worker.sh
ssh claude@claudefarm2 "chmod +x ~/projects/worker.sh"
```

Verify it landed and is executable:

```powershell
ssh claude@claudefarm2 "test -x ~/projects/worker.sh && echo ready"
```

Expected output: `ready`

This is intentionally manual and one-time. Phase 6 will replace `worker.sh` with the real version, and at that point `DeliverStage` should be modified to upload it as part of every run (so the version on the VM tracks the version in the repo automatically).

## How to run the verification

Prerequisites:
- Aspire Dashboard running: http://localhost:18888
- Farmer.Host binds 5100 cleanly (no other dotnet.exe holding the port)
- `appsettings.json` contains only the target VM (e.g., claudefarm2) in its `Vms` array
- `SshKeyPath` in settings points at an unencrypted key (default `id_ed25519`)
- The fake `worker.sh` has been installed on the target VM (previous section)

From a PowerShell prompt:

```powershell
# 1. Clean previous runs so the verification run is the only one in the folder
Remove-Item -Recurse -Force D:\work\planning-runtime\runs\*

# 2. Start the service (binds http://localhost:5100, starts InboxWatcher)
cd D:\work\planning\src\Farmer.Host
dotnet run

# 3. In a second PowerShell window, drop the inbox trigger
Copy-Item D:\work\planning\scripts\demo\sample-request.json D:\work\planning-runtime\inbox\verify.json

# 4. Wait a few seconds, then stop the service with Ctrl+C in the first window
```

## Verification checklist

A successful end-to-end run satisfies all of the following. Any failure means stop and diagnose before continuing.

- [ ] `dotnet build && dotnet test` both green, 95 tests passing
- [ ] `ssh claude@claudefarm2 "test -x ~/projects/worker.sh && echo ready"` returns `ready`
- [ ] A new run directory appears at `D:\work\planning-runtime\runs\run-*\` after the inbox file is dropped
- [ ] The run directory contains all 8 expected artifacts:
  - `request.json`
  - `state.json`
  - `events.jsonl`
  - `result.json`
  - `task-packet.json`
  - `cost-report.json`
  - `logs\` (directory)
  - `artifacts\` (directory)
- [ ] `events.jsonl` has exactly 14 lines — seven stages × two events each (stage.started + stage.completed)
- [ ] The final event is `Review.stage.completed`
- [ ] `state.json` shows `phase: "Complete"` with no `error` field
- [ ] `result.json` shows `success: true`, `final_phase: "Complete"`, and all 7 stages in `stages_completed`
- [ ] `cost-report.json` has 7 stage entries with non-zero durations
- [ ] The mapped drive `O:\projects\output\manifest.json` contains `FAKE_WORKER_NO_REAL_CHANGES`
- [ ] The Aspire Dashboard `workflow.run` waterfall for this run shows **7 green child stage spans**, root span green

## When to delete the fake worker

When Phase 6 lands. The real `worker.sh` replaces this file at the same path. The documentation in this file is then moved to `docs/archive/` or deleted along with the fake worker.sh entry in `src/Farmer.Worker/`. Until then, this file and the fake worker stay tracked together as a unit.
