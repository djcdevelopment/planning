# Phase 7 Retro — Partial (session 2026-04-22)

Phase 7 code landed on `main`; loop-closing e2e run blocked on a K: mount wedge that needs host-level reset. Captured here so next session picks up clean.

## What shipped

- **Stream A (D:\ path retirement)** — `PathsSettings.cs` defaults + `appsettings.json` prod paths flipped from `D:\work\planning-runtime\*` to `C:\work\iso\planning-runtime\*`. 3 files, 15 adds / 15 deletes. Atomic commit `06a8caf`.
- **Stream B (Azure OpenAI + Entra)** — `MafRetrospectiveAgent` now builds via `new AzureOpenAIClient(endpoint, DefaultAzureCredential())`. `ApiKey` / `QaModel` config retired; `Endpoint` + `DeploymentName` added. `Azure.AI.OpenAI 2.1.0` + `Azure.Identity 1.21.0` added (keeping `Microsoft.Agents.AI.OpenAI 1.1.0` — the MAF binding is unchanged). ADR-006 amended in place (not superseded). 8 files, commits `9c226e3` + `f64009f`.
- **Plan docs** — phase 7-10 + master buildout plan committed (`b230bcf`).
- **Merges** — both streams `--no-ff` merged to `main` (merge commits `f527c17`, `9e972ff`); worktrees cleaned.
- **Final state** — `dotnet build` 0/0, `dotnet test` 143 green (138 unit + 5 integration). `main` at `5adf6bd`.

## What's validated

- ✅ **Stream A in production behavior** — `dev-run.ps1` boots clean, `/health` returns 200 with no `DirectoryNotFoundException`. The runtime root default is now correct.
- ✅ **Stream B in code** — build + unit tests green; config binding covered by new tests (`EndpointDefaultsToEmpty`, `DeploymentNameDefaultsToEmpty`, `CanBindEndpointAndDeployment`).
- ✅ **Azure OpenAI infrastructure** — `farmer-openai-dev` provisioned in `rg-farmer-dev` (East US 2), `gpt-4.1-mini` deployed (Standard, 10K TPM), `Cognitive Services OpenAI User` role assigned to Derek on the account scope, probe call round-tripped via `DefaultAzureCredential`.

## What's NOT validated (blocked on K: mount)

- ❌ **Retrospective stage end-to-end** — the fake smoke trace failed at `Deliver/Dispatch` → `output/manifest.json` readback, so the retrospective never fired. Stream B's runtime path (Host → Azure → retro verdict) is unexercised against a real run.

## Blocking issue

**`net use K:` returns error 67 ("network name cannot be found")** even after removing the stale mount. WinFsp Launcher service is running. Per RESUME.md this is the documented wedge that needs either:
- `Restart-Service WinFsp.Launcher -Force` in **elevated** PS + retry mount (couldn't execute from this session — non-admin), or
- a host reboot ("the reliable reset" per RESUME.md).

Next session should open with the mount restored, then re-fire `infra/Farmer.SmokeTrace.ps1` (fake then real).

## Surprises worth remembering

1. **`gpt-4o-mini` is soft-retired at the deployment API** (since 2026-03-31) even though the Azure catalog still lists it as `GenerallyAvailable`. Deployment attempts fail with "has been deprecated since". `gpt-4.1-mini` (2025-04-14) is the modern mini-tier default. Pattern: never trust the catalog alone; try the deployment and have a fallback chain.
2. **`Get-AzAccessToken` returns a `SecureString` token in newer Az modules.** Naive interpolation into an `Authorization: Bearer $tok.Token` header yields "Bearer System.Security.SecureString" — silent 401s. Unwrap via `[System.Net.NetworkCredential]::new('', $tok.Token).Password` or `ConvertFrom-SecureString -AsPlainText`.
3. **Plan docs uncommitted in `main`'s working tree did NOT propagate to newly-created worktrees.** `git worktree add ... main` branches from the committed HEAD, not the working-tree state. Sub-agents couldn't read `docs/phase7-close-the-loop.md`; they worked from the kickoff prompts, which were self-contained enough. For future phases: commit plan artifacts to `main` before spawning builders.
4. **Test baseline is 143 (138 unit + 5 integration), not 133.** `CLAUDE.md` has the stale count from an earlier phase retro; not a regression. Worth updating in a follow-up doc commit.
5. **`~/.ssh/config` still references the deleted `vmfarm_ed25519` key** with `IdentitiesOnly yes`, which breaks command-line `ssh claude@vm-golden` outside Farmer.Host. One-line fix (change `IdentityFile` to `~/.ssh/id_ed25519` or delete the block), doesn't affect Farmer.Host because Renci.SshNet uses its own explicit `id_ed25519` path.

## Next session entry

1. Reboot or admin-PS restart WinFsp.Launcher → remount K: with `\\sshfs.k\claude@vm-golden` (note: `sshfs.k`, not `sshfs`).
2. `.\scripts\dev-run.ps1 -SkipWorkerCheck` → Host up.
3. `.\infra\Farmer.SmokeTrace.ps1` (fake mode) — expect 7/7 green this time.
4. `.\infra\Farmer.SmokeTrace.ps1 -WorkerMode real` — the loop-closing run; retro fires against Azure OpenAI.
5. Capture evidence in `phase7-retro.md` (full). Memory update with anything surprising from the real run.
6. Decide Phase 8 entry: build `vm-hub` + two workers, or hold for other reasons.

## Open follow-ups (not blocking Phase 8)

- Update `CLAUDE.md` test count 133 → 143.
- Fix `~/.ssh/config` `vmfarm_ed25519` reference.
- `prototype-nats/` directory rename (needs reboot to release file handles — natural pair with the K: mount reboot if chosen).
- Push `main` to `origin` — local-only so far.
