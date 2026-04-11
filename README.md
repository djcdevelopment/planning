# Farmer

A .NET 8 control plane that orchestrates Claude CLI workers on Hyper-V Ubuntu VMs, with a retrospective agent on the host (via Microsoft Agent Framework + OpenAI) that reviews every run's output. Filesystem-first runtime, OpenTelemetry throughout, inbox-triggered, designed for Azure/.NET developers learning agent orchestration.

**Status:** Phase 6 in progress (retrospective loop). Phase 5 shipped (externalized runtime, real SSH end-to-end verified). See [docs/](./docs/) and the branch list below.

---

## The pitch in one paragraph

Drop a JSON file into `D:\work\planning-runtime\inbox\`. A BackgroundService in `Farmer.Host` notices it, creates a new run directory under `runs\{run_id}\`, and walks the workflow: reserve a VM, SCP the prompt files to `~/projects/plans/` on the VM, SSH-execute `worker.sh`, wait for the worker's output on the mapped drive, and finally run a retrospective agent against the result. Every stage is an OpenTelemetry span, every run folder is immutable after completion, every failure is captured as data. If you ever want to know "what did Farmer ask, what did the worker do, what did the reviewer think" — it's all in the run directory, and it's also in Aspire.

## Architecture at a glance

```
inbox/*.json
    │
    ▼
InboxWatcher (host BackgroundService)
    │
    ▼
RunDirectoryFactory  →  runs/{run_id}/request.json
    │
    ▼
RunWorkflow — 7-stage pipeline
  CreateRun → LoadPrompts → ReserveVm → Deliver → Dispatch → Collect → Retrospective
                                           │           │         │          │
                                           │           │         │          └── IRetrospectiveAgent
                                           │           │         │              (MAF + OpenAI, host)
                                           │           │         │              writes qa-retro.md
                                           │           │         │              writes review.json
                                           │           │         │              writes directive-suggestions.md
                                           │           │         │
                                           │           │         └── reads output/manifest.json
                                           │           │             from mapped drive (O:)
                                           │           │
                                           │           └── bash ~/projects/worker.sh {run_id}
                                           │               (Claude CLI, full dangerous mode)
                                           │
                                           └── SCP prompts + task-packet.json
                                               to ~/projects/plans/
```

**Two runtimes, two providers:**
- **Host** (Windows .NET 8): Farmer.Host + Farmer.Core + Farmer.Agents. The retrospective agent uses Microsoft Agent Framework 1.1.0 + OpenAI provider (stable, `gpt-4o-mini` by default).
- **VM** (Hyper-V Ubuntu, e.g. `claudefarm2`): Claude CLI in full dangerous mode, no tool allowlist, sandboxed by the VM itself.

## Prerequisites

- .NET 8 SDK
- Docker (for Aspire Dashboard — traces/logs/metrics visualization)
- Hyper-V with at least one Ubuntu VM reachable over SSH
- SSHFS-Win (or equivalent) mapping the VM's `~/projects/` to a drive letter on the host (e.g., `O:\`)
- `OPENAI_API_KEY` environment variable for the retrospective agent
- An unencrypted SSH key at `~/.ssh/id_ed25519` (or configured via `Farmer:SshKeyPath`)

## Build and test

```powershell
cd D:\work\planning\src
dotnet build
dotnet test
```

**Expected:** 107+ tests green across `Farmer.Tests` (the count grows each phase). Build targets `net8.0`.

## Run the demo (Phase 5, verified)

Full runbook lives at [scripts/demo/README.md](./scripts/demo/README.md). The short version:

```powershell
# 1. Start Aspire Dashboard (OTLP receiver on 18889, UI on 18888)
docker run --rm -d --name aspire-dashboard `
  -p 18888:18888 -p 18889:18889 `
  -e DOTNET_DASHBOARD_UNSECURED_ALLOW_ANONYMOUS=true `
  mcr.microsoft.com/dotnet/aspire-dashboard:9.0

# 2. Start Farmer.Host
cd D:\work\planning\src\Farmer.Host
dotnet run

# 3. Drop a trigger into the inbox
copy D:\work\planning\scripts\demo\sample-request.json `
  D:\work\planning-runtime\inbox\hello.json

# 4. Open http://localhost:18888 and watch the workflow.run trace land
```

## Directory layout

```
D:\work\planning\            ← this repo (the engine)
├── src\
│   ├── Farmer.Core\         ← pure engine, no AI deps, stable .NET 8
│   ├── Farmer.Tools\        ← SSH/SCP/mapped drive implementations
│   ├── Farmer.Host\         ← ASP.NET Core host, DI, InboxWatcher
│   ├── Farmer.Agents\       ← NEW Phase 6: Microsoft Agent Framework surface
│   ├── Farmer.Worker\       ← VM-side worker scripts + CLAUDE.md
│   └── Farmer.Tests\        ← xUnit tests
├── docs\                    ← build logs, architecture docs, ADRs
│   └── adr\                 ← Architecture Decision Records (NEW)
├── data\sample-plans\       ← work request templates
├── scripts\demo\            ← local demo runbook + sample inbox file
└── README.md                ← you are here

D:\work\planning-runtime\    ← runtime state, NOT in git
├── inbox\                   ← drop trigger files here
├── runs\{run_id}\           ← one dir per run, immutable after completion
│   ├── request.json
│   ├── state.json
│   ├── events.jsonl
│   ├── result.json
│   ├── cost-report.json
│   ├── logs\
│   └── artifacts\
├── outbox\
├── qa\
└── data\sample-plans\       ← worker inputs (copied from repo)
```

## Branches

Phase work lives on feature branches; nothing has been merged to `main` yet beyond the initial scaffold and the stewardship notice.

| Branch | What's on it |
|---|---|
| `main` | Initial scaffold + license notice. Phase 5/6 work not yet merged. |
| `claude/phase5-externalized-runtime` | The architectural Phase 5 work (7 commits) |
| `claude/phase5-end-to-end-verification` | Phase 5 + real SSH end-to-end verified (5 more commits on top) |
| `claude/phase6-retrospective-loop` | **Current active branch.** Phase 6 foundations + `Farmer.Agents` + MAF OpenAI integration |
| `claude/phase5-otel-api` | Parallel Phase 5 work from another agent (different architecture, see ADR-005 for context) |

Merging any of these to `main` is deferred until Phase 6 ships end-to-end.

## Documentation

- **[docs/phase5-build-log.md](./docs/phase5-build-log.md)** — Literal record of every Phase 5 commit, including the retro on Bug 1 / Bug 2
- **[docs/phase5-pattern.md](./docs/phase5-pattern.md)** — Architectural invariants: filesystem as source of truth, eventing/telemetry agreement, stages don't know the runDir
- **[docs/phase5-testing-bridge.md](./docs/phase5-testing-bridge.md)** — Why real integration tests need a VM, the SSH bridge problem
- **[docs/end-to-end-verification.md](./docs/end-to-end-verification.md)** — The Phase 5/6 bridge fake worker and how to verify 7/7 green
- **[docs/adr/](./docs/adr/)** — Architecture Decision Records (the why of every load-bearing choice)
- **[docs/implementation-plan.md](./docs/implementation-plan.md)** — Original 7-phase plan from project start (partially superseded by per-phase docs)

## Observability

Every run gets:
- **Traces** — `workflow.run` (root) + `workflow.stage.*` (children) under the `Farmer` ActivitySource. Phase 6 adds `invoke_agent FarmerRetrospectiveAgent` + `chat gpt-4o-mini` under `Experimental.Microsoft.Agents.AI`.
- **Structured logs** — every log line has `RunId` and `StageName` as fields, correlated to the active span.
- **Metrics** — `farmer.runs.started/completed/failed`, `farmer.stage.duration`, Phase 6 adds `farmer.qa.risk_score` histogram and `farmer.qa.verdicts_total` counter tagged by verdict.

Open http://localhost:18888 with Aspire Dashboard running and every run shows up live.

## Working on this

If you're Claude (or another agent) picking this up mid-phase, read [CLAUDE.md](./CLAUDE.md) at the repo root first — it has the gotchas, the don't-touch list, and pointers to the active plan file.

If you're a human contributor: start with the relevant phase doc in `docs/`, then the most recent ADRs in `docs/adr/`, then the commit messages on the active branch. Farmer's commit messages are verbose on purpose — they tell you why, not just what.
