# Phase 8 — Farm Online

**Depends on:** Phase 7 (one real run completes locally, evidence archived).

**Goal:** retire the single-laptop topology. Stand up `vm-hub` + two workers so a single `/trigger` request can land on any available worker, and two triggers in flight at the same time land on two different workers. This is the first phase where "multi-agent" stops meaning "sequential retries" and starts meaning "concurrent execution".

## Why now (conditions that must hold after Phase 7)

- Host boots cleanly, Azure OpenAI verified, one real run's evidence in `planning-runtime/runs/`.
- We have a known-good single-worker baseline to regression-compare against.

If any of those don't hold, Phase 8 is premature — the farm will amplify the Phase 7 bug, not work around it.

## Topology delta

| Before (today) | After (Phase 8) |
|---|---|
| Host laptop runs NATS locally on `:4222` | NATS moves to `vm-hub` (`192.168.144.10:4222`) |
| One worker: `vm-golden` (reference image only) | Two workers: `vm-worker-01` (.11), `vm-worker-02` (.12), both cloned from or autoinstalled parallel to golden |
| Runs serialized behind one worker | `FARMER_RUNS` JetStream consumer group — workers compete for messages |
| `.comms` directory nonexistent | `/home/claude/comms/` on hub, sshfs-mounted read-write by both workers |

`vm-golden` stays as the reference/fallback. We do not destroy it.

## Non-goals

- Fan-out of a single plan across multiple workers (that's Phase 9).
- Workers coordinating with each other directly (that's Phase 10).
- Migrating NATS to Azure Service Bus (later; validate the pattern on-prem first).
- Auto-scaling / dynamic worker provisioning. Two static workers is enough to prove the model.

## Open questions (resolve before dispatch)

1. **Clone vs autoinstall for workers.** RESUME.md says autoinstall is probably cleaner (no machine-id/host-key/cloud-init-state scrubbing). Rec: autoinstall from the same subiquity + cloud-init recipe that worked for golden, parameterized by hostname + static IP + `/comms` fstab entry.
2. **NATS auth posture on the hub.** Options: open on LAN (farm is behind NAT, low risk), NKEY per worker, or Entra via NATS-Azure bridge. Rec: NKEY per worker — cheap, auditable, sets the habit before Azure Service Bus migration.
3. **What runs on vm-hub besides NATS?** Jaeger? Host itself? Rec: hub hosts NATS + `/comms` only. Host stays on the laptop for now (easier to iterate). Jaeger stays on laptop. Keeps hub minimal; we can migrate the host service to the hub or to Azure later without retouching worker config.

## Streams

### Stream A — vm-hub build

**Territory**
- `scripts/vm-hub-bootstrap.sh` (new) — autoinstall post-boot: install NATS as systemd service with JetStream enabled + store at `/var/lib/nats/jetstream`, create `/home/claude/comms/` with correct perms, open :4222/:8222 in ufw for LAN.
- `infra/nats-cluster.conf` (new) — NATS config: listen on 0.0.0.0:4222, enable JS, set store_dir, NKEY accounts for workers.
- `scripts/02-create-vm-hub.ps1` (new) — mirrors `02-create-golden.ps1` but static IP .10 + hostname `vm-hub`.
- Update `C:\work\iso\RESUME.md` with hub build log.

**Gate A**
- NATS reachable from laptop: `.\infra\check-nats.ps1 -Host vm-hub` returns JS status green.
- `/home/claude/comms/` writable via SSH from laptop.
- `vm-hub` auto-starts with host.

### Stream B — worker buildout (parameterized)

**Territory**
- `scripts/vm-worker-bootstrap.sh` (new, parameterized) — sshfs-mount hub:/comms → /comms on boot (fstab), install Claude CLI identically to golden, register as NATS consumer in `farmer-workers` durable group.
- `scripts/02-create-vm-worker.ps1 -Number 1|2` (new) — one script, two invocations.
- Update `FarmerSettings.Workers[]` schema to list workers by hostname, not single `SshHost`.
- Update `WorkerPool` / `IVmManager` to reserve any-free-worker instead of hardcoded single.

**Gate B**
- Both workers show `idle` in `WorkerPool.Status`.
- SSH key auth works from Host to both (no passphrase prompt — see SSH-key gotcha in CLAUDE.md).

### Stream C — Host wiring

**Territory**
- `src/Farmer.Host/appsettings.json` — `Farmer:Nats:Url` → `nats://vm-hub:4222`; `Farmer:Workers[]` populated.
- `src/Farmer.Core/VmManagement/WorkerPool.cs` (rework or replace `SingleVmManager`).
- No changes to `RunWorkflow`, `RetrospectiveStage`, or retry driver — work assignment is below their abstraction layer.

**Gate C**
- Parallel trigger test: two `curl` invocations fire within 100 ms of each other → two runs land on two different workers → both complete → evidence for both runs in `planning-runtime/runs/`.
- Jaeger shows both traces independently; no cross-run span leakage.

## Parallelization

- Stream A blocks B + C (workers need hub's NATS to register).
- B and C can run in parallel after A finishes.
- A is mostly VM provisioning (wall-clock bound, ~30 min).
- B is bash + PowerShell + a small FarmerSettings schema change (~45 min per worker, but both workers share the same script so it's really one unit of work).
- C is C# (~1 hr including tests).

Total wall-clock: ~2 hrs if B + C are parallel after A. ~3 hrs serial.

## Risks + mitigations

- **sshfs auto-mount race.** Worker boots before hub's NATS ready → systemd retry. Mitigation: add `Requires=` + `After=` on the NATS-dependent services; `/comms` mount has its own retry loop via `_netdev,nofail`.
- **NATS cluster split-brain.** We're running single-node NATS, not a cluster — so this can't happen yet. Note the assumption; revisit if we add a second hub.
- **Worker key exposure on hub's `/comms`.** Workers mount hub's comms dir with their own key. Don't store Claude auth tokens there; those stay in worker `/home/claude/`.

## Exit definition

- Two workers idle + registered with hub's NATS.
- Two parallel runs land on two workers, both complete.
- Evidence archived for both.
- `docs/phase8-retro.md` written.
- Memory updated with any farm-specific gotchas.
