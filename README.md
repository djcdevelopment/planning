# Farmer

A .NET 9 control plane that orchestrates Claude CLI workers on Hyper-V Ubuntu VMs, with a retrospective agent on the host (via Microsoft Agent Framework + OpenAI) that reviews every run's output. NATS JetStream + ObjectStore for coordination, OpenTelemetry throughout, HTTP-triggered, designed for Azure/.NET developers learning agent orchestration.

**Status:** Phase 6 shipped (real worker + MAF retrospective). **NATS cutover shipped 2026-04-15** (PR #5) — file-based inbox retired, messaging-first. See [ADR-010](./docs/adr/adr-010-nats-messaging-cutover.md), [docs/](./docs/), and the branch list below.

---

## The pitch in one paragraph

POST a run request to `/trigger` on Farmer.Host. The request runs the 7-stage workflow synchronously: reserve a VM, SCP the prompt files to `~/projects/plans/` on the VM, SSH-execute `worker.sh`, wait for the worker's output on the mapped drive, run a retrospective agent against the result, mirror the whole run directory to NATS ObjectStore. Every stage publishes a `RunEvent` to the durable `FARMER_RUNS` JetStream stream; every stage is an OpenTelemetry span under one traceId from HTTP ingress through retrospective; every run folder is immutable after completion. If you ever want to know "what did Farmer ask, what did the worker do, what did the reviewer think" — it's in the run directory on disk, in the `farmer-runs-out` ObjectStore bucket over the wire, and as a 100+ span waterfall in Jaeger.

## Architecture at a glance

```
POST /trigger  ─────────────┐
                            ▼
                   RunDirectoryFactory → runs/{run_id}/request.json
                            │
                            ▼
                   RunWorkflow — 7-stage pipeline
                   CreateRun → LoadPrompts → ReserveVm → Deliver → Dispatch → Collect → Retrospective
                            │                               │         │         │           │
                            │                               │         │         │           └── IRetrospectiveAgent
                            │                               │         │         │               (MAF + OpenAI, host)
                            │                               │         │         │               writes qa-retro.md
                            │                               │         │         │               writes review.json
                            │                               │         │         │               writes directive-suggestions.md
                            │                               │         │         │
                            │                               │         │         └── reads output/manifest.json
                            │                               │         │             from mapped drive (K:)
                            │                               │         │
                            │                               │         └── bash ~/projects/worker.sh {run_id}
                            │                               │             (Claude CLI, full dangerous mode)
                            │                               │
                            │                               └── SCP prompts + task-packet.json
                            │                                   to ~/projects/plans/
                            │
                            │  EventingMiddleware + /trigger handler also:
                            ▼
                   NATS  ──────┬──────→  FARMER_RUNS stream
                               │         farmer.events.run.{runId}.{stage}.{status}
                               │         (one message per stage transition)
                               │
                               └──────→  farmer-runs-out ObjectStore bucket
                                         {runId}/manifest.json, events.jsonl, state.json, result.json, ...
```

**Three runtimes:**
- **Host** (Windows .NET 9): Farmer.Host + Farmer.Core + Farmer.Agents + Farmer.Messaging. The retrospective agent uses Microsoft Agent Framework + OpenAI provider (stable, `gpt-4o-mini` by default).
- **NATS** (same box or LAN-reachable): `nats-server.exe` from `tools/`, JetStream file-backed, runs the `FARMER_RUNS` stream + `farmer-runs-out` bucket. Started via `infra/start-nats.ps1`.
- **VM** (Hyper-V Ubuntu, e.g. `vm-golden`): Claude CLI in full dangerous mode, no tool allowlist, sandboxed by the VM itself.

Tracing target: Jaeger v2 (`tools/jaeger.exe`, downloaded via `tools/download-jaeger.ps1` on first use). Started via `infra/start-jaeger.ps1`. UI on http://localhost:16686 — every `/trigger` call produces one clickable traceId spanning ~100-130 spans.

## Prerequisites

- .NET 9 SDK
- Hyper-V with at least one Ubuntu VM reachable over SSH (`vm-golden` in the shipped config)
- SSHFS-Win (or equivalent) mapping the VM's `~/projects/` to a drive letter on the host (`K:` by default)
- `OPENAI_API_KEY` environment variable for the retrospective agent (optional: `Farmer:Retrospective:FailureBehavior=AutoPass` is the default, so missing key just skips the LLM call rather than failing the stage)
- An SSH key at `~/.ssh/id_ed25519` (must have ACL allowing ONLY the running user — OpenSSH on Windows silently falls back to password auth if `CodexSandboxUsers` or similar inherited ACEs are present; fix with `icacls $key /reset; icacls $key /inheritance:r; icacls $key /grant:r "${env:USERNAME}:F"`)

`nats-server.exe` is checked in at `tools/` (17 MB). `jaeger.exe` is larger than GitHub's file limit; `infra/start-jaeger.ps1` auto-invokes `tools/download-jaeger.ps1` on first run.

## Build and test

```powershell
cd C:\work\iso\planning

# First-time setup: wire up the pre-commit secret scanner (idempotent).
# Points git at .githooks/ so `infra/check-staged-secrets.ps1` runs on every commit.
.\scripts\install-githooks.ps1

dotnet build src\Farmer.sln
dotnet test src\Farmer.sln
```

**Expected:** 133 tests green on `net9.0` (128 unit + 5 integration).

## Run the demo (NATS cutover, verified)

```powershell
# 1. Start NATS + Jaeger (idempotent; no-ops if already listening)
.\infra\start-nats.ps1
.\infra\start-jaeger.ps1    # fetches jaeger.exe on first run, ~2 minutes

# 2. Start Farmer.Host
.\scripts\dev-run.ps1        # uses appsettings.Development.json (C:\work\iso\planning-runtime paths, NATS + OTLP localhost)

# 3. In a second window — trigger a run
cd C:\work\iso\planning
curl.exe -X POST -H "Content-Type: application/json" `
  --data-binary "@scripts\demo\sample-request.json" `
  http://localhost:5100/trigger

# 4. Observe:
#    - Jaeger UI → http://localhost:16686 → Service "Farmer" → latest trace
#    - NATS streams → http://localhost:8222/jsz?streams=true
#    - Quick CLI summary of the latest trace:
.\scripts\_waterfall.ps1
```

Expected `/trigger` response shape: `{ "runId": "...", "success": true, "finalPhase": "Complete", "stagesCompleted": ["CreateRun","LoadPrompts","ReserveVm","Deliver","Dispatch","Collect","Retrospective"], ... }`. Duration is ~2s with a fake worker, ~5-20 min with a real Claude CLI session.

### Retry (Phase 7)

```powershell
# Real-Retry demo: fake-bad produces BUILD FAILED on attempt 1, clean output on
# the retry (detected via task-packet.feedback). gpt-4o-mini verdicts Reject on
# attempt 1 and Accept on attempt 2 -- the loop fires on a real verdict, not a
# contrived retry_on_verdicts: ["Accept"] config.
curl.exe -X POST -H "Content-Type: application/json" -d '{
  "work_request_name": "react-grid-component",
  "worker_mode": "fake-bad",
  "retry_policy": { "enabled": true, "max_attempts": 2, "retry_on_verdicts": ["Retry","Reject"] }
}' http://localhost:5100/trigger
```

When the retrospective verdict is in `retry_on_verdicts`, the driver loops: each retry gets a synthetic `0-feedback.md` prompt with the prior attempt's findings injected. The chain is linked via `parent_run_id`. Without an OpenAI key, retrospective AutoPasses (null verdict, no retry); with a key, the verdict drives the loop. See [ADR-011](./docs/adr/adr-011-retry-driver.md) and [docs/retry-demo-2026-04-16.md](./docs/retry-demo-2026-04-16.md) for the full receipt.

## Directory layout

```
C:\work\iso\planning\        ← this repo (the engine)
├── src\
│   ├── Farmer.Core\         ← pure engine, no AI/transport deps
│   ├── Farmer.Tools\        ← SSH/SCP/mapped drive implementations
│   ├── Farmer.Host\         ← ASP.NET Core host, DI, /trigger endpoint
│   ├── Farmer.Agents\       ← Microsoft Agent Framework surface (retrospective)
│   ├── Farmer.Messaging\    ← NATS JetStream + ObjectStore; DI-injected event publisher + artifact store
│   ├── Farmer.Worker\       ← VM-side worker scripts + CLAUDE.md
│   └── Farmer.Tests\        ← xUnit tests (133 on net9, incl. 5 integration)
├── docs\
│   └── adr\                 ← Architecture Decision Records (ADR-010 NATS cutover, ADR-011 retry driver)
├── infra\                   ← nats.conf, jaeger.yaml, start-*.ps1
├── tools\                   ← nats-server.exe (in-repo), jaeger.exe (downloaded)
├── scripts\                 ← dev-run.ps1, _waterfall.ps1, demo runbook
├── data\sample-plans\       ← work request templates
└── README.md                ← you are here

C:\work\iso\planning-runtime\  ← runtime state, NOT in git (configurable, see Farmer:Paths)
├── runs\{run_id}\           ← one dir per run, immutable after completion
│   ├── request.json
│   ├── state.json
│   ├── events.jsonl
│   ├── result.json
│   ├── cost-report.json
│   ├── logs\
│   └── artifacts\
├── nats\                    ← JetStream store_dir (FARMER_RUNS stream + farmer-runs-out bucket on disk)
├── data\sample-plans\       ← worker inputs (copied from repo)
├── outbox\
└── qa\
```

## Branches

`main` is the source of truth; feature branches open PRs, merge, and get deleted on the way in.

| PR | Title | Status |
|---|---|---|
| #2 | Phase 6 retrospective loop | merged |
| #3 | Phase 5 externalized runtime | merged |
| #4 | Phase 5 end-to-end verification | merged |
| #5 | NATS messaging cutover -- file-based inbox retired | merged 2026-04-15 |
| #6 | ADR-010 + README + integration tests | merged 2026-04-15 |
| #7 | worker_mode contract + Farmer.SmokeTrace.ps1 | merged 2026-04-15 |
| #8 | Phase 7: opt-in retry driver with feedback injection | merged 2026-04-15 |
| #9 | Cleanup: ADR-011, shared test helpers, Phase 7 docs | merged 2026-04-15 |
| #10 | Release reserved VM in RunWorkflow finally block | merged 2026-04-15 |
| #11 | docs: session retrospective 2026-04-15 | merged 2026-04-15 |
| #12 | IWorkflowRunner seam + real-Retry demo (fake-bad mode) | merged 2026-04-16 |

`claude/phase5-otel-api` was a parallel Phase 5 implementation from a different agent; we cherry-picked `WorkflowPipelineFactory` (see [ADR-004](./docs/adr/adr-004-workflow-pipeline-factory.md)). Don't merge the rest — it's a different architecture.

## Architecture Diagrams

Visual overviews of the system (SVG, open in any browser):

- **[Data Flow](./docs/diagrams/data-flow.svg)** — inbox trigger through 7-stage pipeline to immutable run directory, with VM boundary and transport layer
- **[Learning Loop](./docs/diagrams/learning-loop.svg)** — the two LLM call sites (Claude CLI on VM, OpenAI on host), what each sees, what each produces, and how directive suggestions feed forward to future runs
- **[Tech Stack](./docs/diagrams/tech-stack.svg)** — layered architecture from Aspire Dashboard through .NET host through transport to VM worker, with scaling path notes
- **[Artifact Map](./docs/diagrams/artifact-map.svg)** — every file in a completed run directory, who writes it, and who reads it

## Documentation

- **[docs/phase5-build-log.md](./docs/phase5-build-log.md)** — Literal record of every Phase 5 commit, including the retro on Bug 1 / Bug 2
- **[docs/phase5-pattern.md](./docs/phase5-pattern.md)** — Architectural invariants: filesystem as source of truth, eventing/telemetry agreement, stages don't know the runDir
- **[docs/phase5-testing-bridge.md](./docs/phase5-testing-bridge.md)** — Why real integration tests need a VM, the SSH bridge problem
- **[docs/end-to-end-verification.md](./docs/end-to-end-verification.md)** — The Phase 5/6 bridge fake worker and how to verify 7/7 green
- **[docs/adr/](./docs/adr/)** — Architecture Decision Records (the why of every load-bearing choice)
- **[docs/implementation-plan.md](./docs/implementation-plan.md)** — Original 7-phase plan from project start (partially superseded by per-phase docs)

## Observability

Every run gets:
- **Traces** — `POST /trigger` (root) → `workflow.run` → `workflow.stage.*` under the `Farmer` ActivitySource, joined by `NATS.Net` client spans (`farmer.events publish`, `$JS.API publish`, `$O.farmer-runs-out publish`) and MAF's `Experimental.Microsoft.Agents.AI` spans for the retrospective. A real-Claude run produces ~130 spans under a single traceId from HTTP ingress to artifact upload.
- **Structured logs** — every log line has `RunId` and `StageName` as fields, correlated to the active span.
- **Metrics** — `farmer.runs.started/completed/failed`, `farmer.stage.duration`, `farmer.qa.risk_score` histogram, `farmer.qa.verdicts_total` counter tagged by verdict.
- **JetStream replay** — `FARMER_RUNS` durably holds 24h of stage events; `nats sub 'farmer.events.run.>'` watches every run in real time without touching Farmer.Host.
- **ObjectStore** — `farmer-runs-out` bucket holds every run's artifacts keyed by `{runId}/{filename}`. Accessible from any NATS client on the LAN, no mapped drive needed.

Open http://localhost:16686 (Jaeger) and http://localhost:8222 (NATS monitoring) to see both live.

## Working on this

If you're Claude (or another agent) picking this up mid-phase, read [CLAUDE.md](./CLAUDE.md) at the repo root first — it has the gotchas, the don't-touch list, and pointers to the active plan file.

If you're a human contributor: start with the relevant phase doc in `docs/`, then the most recent ADRs in `docs/adr/`, then the commit messages on the active branch. Farmer's commit messages are verbose on purpose — they tell you why, not just what.
