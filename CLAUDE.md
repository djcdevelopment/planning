# CLAUDE.md — repo-root handoff for Claude sessions

If you're an AI session (Claude Code, another agent, future-me) picking up this project, read this first. It's the fast path to understanding where we are without reading the entire conversation history that produced the current state.

## What this project is (one paragraph)

**Farmer** is a .NET 8 control plane that orchestrates Claude CLI workers on Hyper-V Ubuntu VMs, with a Microsoft Agent Framework (MAF) retrospective agent on the host that reviews every run. Inbox-triggered, file-first, OTel-instrumented throughout. Target audience: Azure/.NET developers learning agent orchestration. The competitive differentiator is using MAF "as much as possible" while keeping workers autonomous on VMs. See [README.md](./README.md) for the full pitch and architecture diagram.

## Current phase state (as of last session)

- **Phase 5** shipped: externalized runtime, file-first `InboxWatcher`, OTel in Aspire, 96 tests green, real end-to-end verified against `claudefarm2` with a fake worker. Two feature branches pushed (`claude/phase5-externalized-runtime` and `claude/phase5-end-to-end-verification`), neither merged to main yet.
- **Phase 6** shipped on `claude/phase6-retrospective-loop`:
  - Real `worker.sh` runs Claude CLI in full dangerous mode on VM
  - `RetrospectiveStage` calls real OpenAI `gpt-4o-mini` via MAF to review every run
  - Three new artifacts per run: `review.json` + `qa-retro.md` + `directive-suggestions.md`
  - First real end-to-end verified: Claude built a TypeScript API on claudefarm2, OpenAI accepted with risk_score=15
  - 7 commits on the branch, 107 tests green
  - See [docs/phase6-retro-verification.md](./docs/phase6-retro-verification.md) for the first retrospective run

Check `git log --oneline claude/phase5-end-to-end-verification..HEAD` on the active branch to see exactly what's committed.

## The plan file for the active session

Plans live at `C:\Users\derek\.claude\plans\goofy-knitting-valley.md` (outside the repo — session artifact, not tracked in git). It's the working plan for Phase 6 including the OpenAI pivot notes. If the file is stale relative to committed reality (commits landed that aren't in the plan, or vice versa), the commit log is authoritative.

## Runtime directory (NOT in git)

Runtime state lives at `D:\work\planning-runtime\`, deliberately outside the repo. See [ADR-001](./docs/adr/adr-001-externalized-runtime.md) for why. Structure:

```
D:\work\planning-runtime\
├── data\sample-plans\     ← worker inputs (copied from repo data/sample-plans/)
├── inbox\                 ← drop *.json here to trigger a run
├── runs\{run_id}\         ← immutable after completion, one dir per run
├── outbox\
├── qa\
```

A successful Phase 5 verification run is preserved at `runs\run-20260411-030920-a2b2e2\` — don't delete it, it's the reference for "what a 7/7 green run looks like".

## Registered ports (portmap at D:\work\start\portmap)

| Service | Port | Status |
|---|---|---|
| `farmer/api` | 5100 | dotnet, HTTP |
| `farmer/aspire-dashboard` | 18888 | docker, HTTP |
| `farmer/aspire-otlp` | 18889 | docker, gRPC OTLP |

Docker keeps the Aspire Dashboard container running across sessions. If you see `aspire-dashboard Up X days` in `docker ps`, leave it alone.

## Common gotchas

- **SSH key path.** `FarmerSettings.SshKeyPath` defaults to `id_ed25519`. The legacy `id_rsa` is encrypted with a passphrase on this machine, and `Renci.SshNet.PrivateKeyFile` can't talk to `ssh-agent`. Don't "fix" this by changing to `id_rsa` — it will fail at `Deliver` stage with `SshPassPhraseNullOrEmptyException`.
- **SSH uses absolute paths for SCP destinations.** `Renci.SshNet`'s `ScpClient` does NOT expand `~`. The VM config uses `/home/claude/projects` as `RemoteProjectPath`, not `~/projects`. See [commit `a3c3c5b`](../git) on the verification branch for history.
- **Mapped drive is read-only from Windows.** Writes to VM always go through SSH/SCP. Reads come through the mapped drive (`O:\projects\` for `claudefarm2`). There's a ~500ms SSHFS cache lag that `MappedDriveReader` handles with a retry loop.
- **`CollectStage` rejects empty `files_changed`**. Phase 5 fake workers used sentinel strings (`FAKE_WORKER_NO_REAL_CHANGES`) to satisfy this. Phase 6 workers will populate `Manifest.Outputs[]` AND leave `files_changed` non-empty for back-compat. See [ADR-003](./docs/adr/adr-003-anti-drift-contract.md) and `CollectStage.cs`.
- **Three-file anti-drift invariant.** `events.jsonl`, `state.json`, and `result.json` must agree on the final phase and stages_completed list. Two regression tests (`BugRegression_*`) pin this. See [ADR-003](./docs/adr/adr-003-anti-drift-contract.md) for the full history of why.
- **Middleware ordering matters.** `WorkflowPipelineFactory` builds the middleware list in this exact order: `Telemetry → Logging → Eventing → [fresh CostTracking] → Heartbeat`. Don't reorder. `CostTrackingMiddleware` is `new`'d inline per-run (see [ADR-004](./docs/adr/adr-004-workflow-pipeline-factory.md)) and is NOT in DI.
- **MAF + OpenAI, not MAF + Anthropic.** The host retrospective agent uses `Microsoft.Agents.AI.OpenAI 1.1.0` (stable), not the preview Anthropic package. The VM worker still uses Claude CLI. This is a deliberate hybrid — see [ADR-006](./docs/adr/adr-006-openai-over-anthropic-maf.md) and [ADR-009](./docs/adr/adr-009-hybrid-maf-host-cli-vm.md).

## Do-not-touch list

- **`.claude/worktrees/charming-moore/`** — another agent's idle worktree. User explicitly said "leave it alone, I'll look at it later". Don't nuke it, don't commit into it, don't switch into it.
- **`origin/claude/phase5-otel-api`** — a parallel Phase 5 implementation from a different agent. We cherry-picked `WorkflowPipelineFactory` from it (see [ADR-004](./docs/adr/adr-004-workflow-pipeline-factory.md)). Don't merge the rest of that branch without a plan — it's a different architecture than ours.
- **Any pushed feature branches** — do not rebase, do not force-push. If you need to change history on a pushed branch, stop and ask the user.
- **`main` branch's README and license** — if you update the README on main, do it via a proper commit flow, not a direct push.

## Build + test + run

```powershell
cd D:\work\planning\src
dotnet build                # expect clean
dotnet test                 # expect 107+ green (varies per phase)

cd Farmer.Host
dotnet run                  # binds http://localhost:5100, starts InboxWatcher

# in another window:
copy D:\work\planning\scripts\demo\sample-request.json `
  D:\work\planning-runtime\inbox\hello.json
# then watch http://localhost:18888 for traces
```

## Key read order if you're picking this up cold

1. [README.md](./README.md) — overall architecture and status
2. This file — session-level gotchas
3. [docs/adr/README.md](./docs/adr/README.md) — load-bearing design decisions
4. [docs/phase5-pattern.md](./docs/phase5-pattern.md) — Phase 5 invariants (filesystem as truth, anti-drift)
5. [docs/phase5-build-log.md](./docs/phase5-build-log.md) — what Phase 5 actually shipped, including the retro on Bugs 1 + 2
6. `git log --oneline` on the active branch — source of truth for what's committed
7. The plan file at `C:\Users\derek\.claude\plans\goofy-knitting-valley.md` — current working plan if it exists and isn't stale

## User collaboration notes

- The user wants hard, specific critique with receipts. Lead with issues, not wins. Roasts must include fixes. (See `C:\Users\derek\.claude\projects\D--work-planning\memory\feedback_critique_style.md` if available.)
- The user prefers atomic commits that each build + test green in isolation. Use `git stash --keep-index --include-untracked` to validate each commit's staged state before committing.
- The user pushed back on tool-allowlist conservatism for the VM worker — full dangerous mode is the design, not a compromise. See [ADR-008](./docs/adr/adr-008-workers-full-dangerous-mode.md).
- The user's target audience is Azure/.NET devs learning MAF. Code comments should lean toward tutorial-style when the code is demonstrating a new MAF pattern.
- "Data is the product." Failures are captured as data, not treated as terminal errors. See [ADR-007](./docs/adr/adr-007-qa-as-postmortem.md).
