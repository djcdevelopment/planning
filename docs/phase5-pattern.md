# The Pattern We Built Towards

This is the architectural shape of Farmer after Phase 5. Read this to understand *why* the pieces are arranged the way they are, not just what got built.

---

## The One-Sentence Version

**Filesystem in, filesystem out, with telemetry and events as parallel observers of the same stage transitions.**

---

## The Core Loop

```
┌────────────────────────────────────────────────────────────────────┐
│                                                                    │
│  D:\work\planning-runtime\inbox\trigger.json                       │
│                  │                                                 │
│                  ▼                                                 │
│            InboxWatcher (poll, 2s)                                 │
│                  │                                                 │
│                  ▼                                                 │
│            RunDirectoryFactory                                     │
│            ├── parse minimal trigger                               │
│            ├── generate run_id                                     │
│            ├── create runs/{run_id}/{logs,artifacts}/              │
│            └── stamp request.json                                  │
│                  │                                                 │
│                  ▼                                                 │
│       RunWorkflow.ExecuteFromDirectoryAsync(runDir)                │
│            ├── read request.json                                   │
│            ├── hydrate RunFlowState (with RunDirectory set)        │
│            ├── reset CostTrackingMiddleware                        │
│            ├── start root activity span                            │
│            ├── increment runs.started counter                      │
│            ├── run middleware-wrapped stage pipeline ──┐           │
│            ├── increment runs.completed/failed         │           │
│            └── write result.json (atomic)              │           │
│                                                        │           │
│                                                        ▼           │
│              ┌─────────────────────────────────────────────┐       │
│              │  per-stage middleware chain (outermost first)│       │
│              │                                              │       │
│              │  TelemetryMiddleware (Activity span)         │       │
│              │  └─ LoggingMiddleware (structured log)       │       │
│              │     └─ EventingMiddleware (events.jsonl +    │       │
│              │        │                   state.json)       │       │
│              │        └─ CostTrackingMiddleware             │       │
│              │           └─ HeartbeatMiddleware (SSH)       │       │
│              │              └─ stage.ExecuteAsync(state)    │       │
│              └──────────────────────────────────────────────┘       │
│                                                                    │
│       D:\work\planning-runtime\runs\{run_id}\                      │
│       ├── request.json    ← input                                  │
│       ├── state.json      ← snapshot, updated per stage            │
│       ├── events.jsonl    ← append-only event log                  │
│       ├── result.json     ← terminal outcome                       │
│       ├── task-packet.json                                         │
│       ├── logs/                                                    │
│       └── artifacts/                                               │
│                                                                    │
└────────────────────────────────────────────────────────────────────┘

         Telemetry runs in parallel (same boundaries):

         OTLP gRPC :18889 ──► Aspire Dashboard :18888
              │                       │
              │                       ├── workflow.run span
              │                       │   ├── workflow.stage.CreateRun
              │                       │   ├── workflow.stage.LoadPrompts
              │                       │   └── ...
              │                       │
              │                       ├── correlated logs
              │                       │
              │                       └── farmer.runs.started/completed/failed
              │                           farmer.stage.duration
              ▼
         Console exporter (terminal output)
```

---

## The Three Invariants

These are the constraints we defended throughout the phase. If a future change violates one, we've drifted.

### Invariant 1: Filesystem is the source of truth

- Every fact about a run can be reconstructed from its `runs/{run_id}/` directory.
- No database, no in-memory state that survives a process restart, no shared mutable singletons (other than the documented Phase 5 compromise on `CostTrackingMiddleware`, which is reset per-run).
- The QA repo (a separate codebase) reads only from `runs/` and validates a run independently. It never needs the Farmer service to be running.
- `state.json` is always overwritten atomically (temp + rename).
- `events.jsonl` is append-only — never rewritten or compacted.
- `result.json` exists only for terminal runs (success or failure).

### Invariant 2: Eventing and telemetry tell the same story

The **anti-drift rule**: `EventingMiddleware` and `TelemetryMiddleware` operate on the same stage boundaries with the same identifiers.

| Boundary | EventingMiddleware writes | TelemetryMiddleware emits |
|---|---|---|
| Stage start | `events.jsonl` line: `{event:"stage.started", stage:"X", run_id:"Y"}` | `Activity` span `workflow.stage.X` started, tagged `farmer.run_id=Y` |
| Stage success | `events.jsonl` line: `{event:"stage.completed", ...}` | Activity ends with `Status.Ok`, histogram records duration with `outcome=Success` |
| Stage failure | `events.jsonl` line: `{event:"stage.failed", ...}` | Activity ends with `Status.Error`, histogram records duration with `outcome=Failure` |
| Stage skip | `events.jsonl` line: `{event:"stage.skipped", ...}` | Histogram records with `outcome=Skip` |

**They cannot disagree because they read the same `stage.Name` and `state.RunId`.** If a future stage emits one but not the other, that's a test failure waiting to happen. The integration test in section 4 below should pin this.

### Invariant 3: Stages don't know about the run directory

- `IWorkflowStage.ExecuteAsync(RunFlowState state, ct)` is the only contract.
- Stages mutate `state` and call `IRunStore` — they never read or write `state.RunDirectory` directly.
- All filesystem coupling lives in:
  - `RunWorkflow.ExecuteFromDirectoryAsync` (reads `request.json`, writes `result.json`)
  - `EventingMiddleware` (writes `events.jsonl`, `state.json`)
  - `RunDirectoryFactory` (creates the directory)
  - `FileRunStore` (writes the per-run side files via `IRunStore`)
- This means stages remain unit-testable with in-memory `IRunStore` doubles, no temp directories required. 79 of 91 tests still use the in-memory path.

---

## The Two Entry Points

### Primary: file-based (the real path)

```
inbox/*.json  →  InboxWatcher  →  RunDirectoryFactory  →  RunWorkflow.ExecuteFromDirectoryAsync(runDir)
```

This is the path that matters. Everything is on disk; the service just reacts.

### Secondary: HTTP (for testing convenience)

```
POST /trigger {body}  →  RunDirectoryFactory  →  RunWorkflow.ExecuteFromDirectoryAsync(runDir)
```

Same `RunDirectoryFactory` — that's why it was extracted. The HTTP path is sugar for "drop a file in the inbox" without needing filesystem access.

There is also `GET /runs/{runId}` which reads `state.json` via `IRunStore` — purely for inspection.

---

## Middleware Ordering (Why It Matters)

Registration order = execution order, outermost first.

```
TelemetryMiddleware    ← starts the Activity span FIRST
LoggingMiddleware      ← logs are inside the span context (correlated)
EventingMiddleware     ← writes events while the span is open
CostTrackingMiddleware ← measures inside everything else
HeartbeatMiddleware    ← SSH writes to VM, innermost
                       └── stage.ExecuteAsync()
```

**Why this order:**
- `TelemetryMiddleware` outermost so the Activity is alive for every log line emitted by inner middleware → automatic trace/log correlation in Aspire.
- `EventingMiddleware` after `LoggingMiddleware` so the file events are written *after* the log lines (chronological consistency in tools that show both).
- `HeartbeatMiddleware` innermost because its SSH write is the slowest, most likely to fail, and its failure should be isolated (it currently swallows errors and logs warnings).

---

## State Lifecycle

A run progresses through these phases. They map 1:1 to `RunPhase` enum values.

```
Created  →  Loading  →  Reserving  →  Delivering  →  Dispatching  →  Collecting  →  Reviewing  →  Complete
                                                                                                ↘
                                                                                                  Failed
```

- `state.json` always reflects the **current** phase (overwritten atomically per stage).
- `events.jsonl` contains the **history** of phases.
- `result.json` is written **once**, at the end, with the final outcome.

The integration test should assert:
1. Last `event` in `events.jsonl` agrees with the `phase` in `state.json`.
2. `result.json` exists if and only if `state.json` shows a terminal phase (`Complete` or `Failed`).
3. `state.json.stages_completed` is a superset of every stage that has a `stage.completed` event.

---

## Where Things Live

```
src/Farmer.Core/                    ← Engine, no I/O concerns beyond file events
├── Config/
│   ├── FarmerSettings.cs           ← Root config
│   ├── PathsSettings.cs            ← Runtime root + subdirs
│   ├── TelemetrySettings.cs        ← OTel exporter switches
│   └── VmConfig.cs                 ← Per-VM SSH/drive info
├── Contracts/                      ← Interfaces only
│   ├── ISshService.cs
│   ├── IMappedDriveReader.cs
│   ├── IRunStore.cs
│   └── IVmManager.cs
├── Models/                         ← DTOs (snake_case JSON)
│   ├── RunRequest.cs
│   ├── RunStatus.cs
│   ├── RunEvent.cs                 ← NEW (Phase 5)
│   ├── TaskPacket.cs
│   ├── CostReport.cs
│   └── ReviewVerdict.cs
├── Layout/
│   └── RunDirectoryLayout.cs       ← Path conventions for VM, host, run dir
├── Workflow/
│   ├── RunWorkflow.cs              ← Has both ExecuteAsync(state) and ExecuteFromDirectoryAsync(runDir)
│   ├── RunFlowState.cs             ← Mutable state passed through pipeline
│   ├── WorkflowResult.cs           ← Terminal outcome DTO
│   ├── IWorkflowStage.cs           ← Stage interface + StageResult
│   └── Stages/
│       ├── CreateRunStage.cs
│       ├── LoadPromptsStage.cs
│       ├── ReserveVmStage.cs
│       ├── DeliverStage.cs         ← needs ISshService
│       ├── DispatchStage.cs        ← needs ISshService
│       ├── CollectStage.cs         ← needs IMappedDriveReader
│       └── ReviewStage.cs          ← stub
├── Middleware/
│   ├── IWorkflowMiddleware.cs
│   ├── LoggingMiddleware.cs
│   ├── EventingMiddleware.cs       ← NEW (Phase 5)
│   ├── TelemetryMiddleware.cs      ← NEW (Phase 5)
│   ├── CostTrackingMiddleware.cs   ← Reset() added (Phase 5)
│   └── HeartbeatMiddleware.cs
└── Telemetry/                      ← NEW (Phase 5)
    ├── FarmerActivitySource.cs
    └── FarmerMetrics.cs

src/Farmer.Tools/                   ← Implementations of contracts (filesystem, SSH, etc.)
├── SshService.cs                   ← Renci.SshNet wrapper
├── MappedDriveReader.cs            ← N:/O:/P: drive reader with SSHFS retry
├── VmManager.cs                    ← Pool with state machine
└── FileRunStore.cs                 ← Now uses Paths.Runs

src/Farmer.Host/                    ← Composition root
├── Program.cs                      ← Full DI + OTel + endpoints
├── appsettings.json                ← Runtime paths + telemetry config
└── Services/                       ← NEW (Phase 5)
    ├── RunDirectoryFactory.cs
    └── InboxWatcher.cs

src/Farmer.Worker/
└── CLAUDE.md                       ← VM-side instructions (Phase 6 territory)
```

---

## Phase 6 Updates

Phase 6 shipped the first two items below and reframed the third. The Phase 5 invariants held throughout — none were broken. For the full Phase 6 architecture, see [docs/phase6-retro-verification.md](./phase6-retro-verification.md) and the [ADRs](./adr/README.md) (especially ADR-005 through ADR-009).

1. **`worker.sh`** — **Done (Phase 6).** Real Claude CLI in full dangerous mode. Runs per prompt, writes `manifest.json` + `summary.json` + `worker-retro.md`. `ReviewStage` renamed to `RetrospectiveStage`.
2. **Retrospective agent** — **Done (Phase 6).** `MafRetrospectiveAgent` using Microsoft Agent Framework + OpenAI `gpt-4o-mini`. Writes `qa-retro.md` + `review.json` + `directive-suggestions.md`. See [ADR-006](./adr/adr-006-openai-over-anthropic-maf.md) for the provider pivot and [ADR-007](./adr/adr-007-qa-as-postmortem.md) for the post-mortem-not-gate framing.
3. **Retry loop** — **Deferred.** QA is a post-mortem, not a gate (ADR-007). No retry loop in Phase 6. Verdict is metadata. Manual re-run is the retry mechanism.
4. **`outbox/` writer** — deferred to Phase 7+.
5. **Concurrency** — deferred. `WorkflowPipelineFactory` (ADR-004) already makes per-run isolation possible by construction.
