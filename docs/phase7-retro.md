# Phase 7 Retro — Close the Learning Loop

Supersedes [phase7-partial-retro.md](./phase7-partial-retro.md) (the mid-session snapshot when we were blocked on the K: mount wedge). This is the full close-of-phase record.

## Definition of done — all gates satisfied

- ✅ Host boots clean in Production environment with default config (Stream A)
- ✅ `MafRetrospectiveAgent` on Azure OpenAI via Entra (Stream B)
- ✅ Worker output readable without WinFsp/SSHFS-Win (Stream D)
- ✅ One real run's evidence archived in `planning-runtime/runs/run-20260423-083153-f1a957/`
- ✅ This retro exists
- ✅ Memory updated
- ✅ Decision on next phase: Phase 8 entry (farm online)

## What shipped

| Commit | Subject |
|---|---|
| `06a8caf` | fix(paths): retire stale D:\ defaults, point runtime root at C:\work\iso\planning-runtime |
| `9c226e3` | feat(agents): swap retrospective agent to Azure OpenAI + Entra |
| `9ef8ea3` | feat(collect): swap mapped-drive readback for SSH-based worker file reader |
| Various | Plan docs, retros, ADR-006 amendment, stream status files |

**Branches preserved:** `claude/phase7-stream-{a,b,d}-*` (merged via `--no-ff` merge commits for stream lineage).

**Test trajectory:** 133 → 143 (post-Streams A+B) → 157 (post-Stream D). 24 net new tests. 0 failed throughout.

**Azure resources:** `farmer-openai-dev` in `rg-farmer-dev` (East US 2), gpt-4.1-mini deployment, Cognitive Services OpenAI User role on Derek → Entra-only auth.

## The loop close, measured

Real-mode smoke trace `run-20260423-083153-f1a957`, 4m 10s wall-clock, 185 spans:

| Stage | Duration | What happened |
|---|---|---|
| CreateRun | 0.01s | Run dir materialized |
| LoadPrompts | 0.01s | Prompts loaded from repo |
| ReserveVm | 2.1s | vm-golden claimed via SSH |
| Deliver | 0.3s | SCP files to `/home/claude/projects/react-grid-component/` |
| Dispatch | 0.4s | Fire claude CLI over SSH |
| **Worker (on VM)** | **~3m 30s** | Real Claude CLI ran 3 prompts (ProjectSetup, GridComponent, AddTests) |
| Collect | 0.1s | **SSH-based readback** of manifest.json, summary.json, per-prompt timing |
| Retrospective | ~28s | **Azure OpenAI gpt-4.1-mini** reasoned over the output, emitted verdict + directive suggestions |

**Verdict:** Accept, risk_score 10/100. The retro agent noted all three prompts exited 0, identified the "minimal output" as likely idempotent no-op (previous runs had already produced the artifacts), and issued two directive suggestions for future prompt wording.

## What's fundamentally different now vs pre-Phase-7

1. **Credentials.** No OpenAI API key anywhere in the codebase or config. Entra-only, `DefaultAzureCredential` via `Connect-AzAccount`. Managed Identity ready on the day we move Host to Azure.
2. **Worker readback is protocol-independent of mapped drives.** `SshWorkerFileReader` works over any SSH-reachable worker, which is the shape Phase 8's multi-worker farm needs. K: is cosmetic now — you can delete the WinFsp/SSHFS-Win install and Farmer still works.
3. **Runtime paths correct in both Dev and Prod envs.** No more "works in Dev, crashes in Prod" surprise.
4. **Directive-suggestion threading is wired end-to-end.** PR #14's feature runs for real now — retro generates prompt rewrites, next run could consume them (not auto-applied, deliberately).

## What we learned (memory-worthy)

### Process learnings (captured as feedback memories)
- **Commit plan docs before spawning worktrees.** `git worktree add` branches from committed HEAD; uncommitted plans in main's working tree don't propagate to builders. Preventable rough edge, saw it on Streams A + B. Fixed the pattern for Stream D.
- **Azure OpenAI catalog ≠ deployment API.** Model catalog says `GenerallyAvailable`, deployment API rejects as soft-retired. Always fan through a fallback chain.
- **SecureString tokens break naive HTTP auth.** Newer Az modules return `Get-AzAccessToken.Token` as SecureString; direct interpolation sends literal `System.Security.SecureString` as the bearer. Unwrap via `NetworkCredential` or `ConvertFrom-SecureString -AsPlainText`.

### Infrastructure learnings
- **WinFsp.Np can desync from WinFsp.Launcher.** Registry has WinFsp.Np registered; service not actually loaded. `Restart-Service WinFsp.Launcher` and reboot both fail to restore it. Required a full WinFsp reinstall — which we skipped because we retired the dependency instead. Worth a memory entry so future-us doesn't re-chase this.
- **The SSHFS-Win mapped-drive pattern doesn't scale.** One drive letter per worker doesn't survive Phase 8+. Retiring it now (Stream D) was the right call even if K: had kept working.

### Codebase learnings
- **`ISshService` already existed.** Stream D's plan proposed introducing a new executor seam; builder found the existing one and reused it. Shows the value of builder judgment inside well-scoped stream prompts — they catch the spec being conservative.
- **Test count in CLAUDE.md was 24 behind reality.** Stale doc, non-regressive. Fixing in this retro's accompanying CLAUDE.md update.

## Open follow-ups (not blocking)

- **Delete `MappedDriveReader.cs`** + `VmConfig.MappedDriveLetter`/`MappedDrivePath` + `FarmerSettings.SshfsCacheLagMs`. One-commit dead-code cleanup.
- **Rename `IMappedDriveReader` → `IWorkerFileReader`.** Mechanical rename across ~10 files. Low risk.
- **`~/.ssh/config` `vmfarm_ed25519` reference.** Breaks command-line ssh outside Farmer.Host; one-line fix.
- **Empty `artifacts/` dir on idempotent runs.** Worker should always produce some proof-of-work artifact even when Claude finds the workspace already correct, OR DeliverStage should wipe artifacts/ too (it already wipes plans/ + output/ per PR #16). Pick one.
- **CLAUDE.md test count + Phase 7 shipped entry** — updated in the commit accompanying this retro.
- **Push-gated direct commit flow.** This session did direct local merges + a single `git push` at the end instead of PRs. Worked fine for a solo-author phase; reconsider PRs when we have multiple concurrent orchestrators/builders.

## Dashboard evidence permalinks

- Real-mode Jaeger trace: http://localhost:16686/trace/3a4b1e50821b8516d6892c8d753ade47
- Fake-mode Jaeger trace (earlier in session, loop-close proof): http://localhost:16686/trace/9ba4d76ef5c525f3e9f96841a0facf96
- Run directory (real-mode): `C:\work\iso\planning-runtime\runs\run-20260423-083153-f1a957\`
  - `review.json` — structured verdict (Accept, risk 10)
  - `qa-retro.md` — narrative retrospective
  - `directive-suggestions.md` — prompt-rewrite suggestions for next run

## Phase 8 entry decision

**Go.** Phase 7's Stream D already paid down the biggest debt that would have blocked Phase 8 (mapped-drive-per-worker). Next session opens on:

1. Reboot (optional, only if WinFsp corruption bothers you — otherwise never matters again)
2. Build `vm-hub` (192.168.144.10) + two workers (.11, .12)
3. First multi-worker parallel-trigger proof
4. See [phase8-farm-online.md](./phase8-farm-online.md) for the plan, [multi-agent-buildout-plan.md](./multi-agent-buildout-plan.md) for the sequencing + cross-cutting concerns

Estimated Phase 8 wall-clock: ~4-5 hours. Budget it as its own session.
