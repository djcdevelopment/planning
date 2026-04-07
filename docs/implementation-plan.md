# Vertical Slice: Claude CLI Worker Orchestration System

## Context

Build a .NET host control plane on Windows 10 that orchestrates Claude CLI workers on Hyper-V Ubuntu VMs. Work requests (numbered prompt files like `1-SetupProject.md`) are submitted via an OpenAI-compatible API, delivered to VMs via SSH/SCP, and progress is monitored via read-only SSHFS mapped drives. This plan is designed to be picked up incrementally — each phase stands alone and can be committed/resumed independently.

**Infrastructure reality (hard-won lessons):**
- Mapped drives (N:, O:, P:) are **READ-ONLY** from Windows — all writes go through SSH/SCP
- Host reads VM state via mapped drives, writes to VMs via SSH/SCP
- `.comms/` directory on each VM is the progress communication channel
- Plan file naming: `1-slug.md` (NOT `[1]-slug.md` — PowerShell bracket issues)
- ~500ms SSHFS cache lag after SSH writes before visible on mapped drive
- SSH.NET (`Renci.SshNet`) for all SSH/SCP operations
- VM structure: `~/projects/` with `CLAUDE.md`, `.comms/progress.md`, `plans/`

**Phase gate rule:** Nothing moves to Phase N+1 until `dotnet build && dotnet test` is green on Windows. No exceptions.

**Lessons learned (Phase 1):**
- Every `.cs` file needs correct `using` statements — `using Xunit;`, `using Microsoft.Extensions.*`, etc.
- Classlib projects don't get `Microsoft.Extensions.*` for free — need explicit NuGet PackageReferences
- Nullable types from external libraries (SSH.NET `int?`) need explicit handling
- Text-based validation catches structure but not compilation — `dotnet build` is the only real gate
- Fix-push-test loop is the workflow: I push, you build, paste errors, I fix

**MS Agent Framework patterns used (all 3):**
1. **Workflow Orchestration** — sequential pipeline with FlowState
2. **Middleware** — logging, cost tracking, heartbeat as cross-cutting concerns
3. **OpenTelemetry** — distributed tracing per workflow stage

---

## Phase 1: Solution Skeleton + Shared-Drive Contract

**Goal:** .NET solution structure, contract models, SSH/SCP service, and the asymmetric read/write helpers.

**What gets built:**
- .NET 9 solution: `Farmer.Host` (ASP.NET Web API), `Farmer.Core` (domain + interfaces), `Farmer.Tools` (SSH, file I/O), `Farmer.Tests`
- Contract models: `RunRequest`, `TaskPacket`, `RunStatus`, `CostReport`, `ReviewVerdict`
- `ISshService` interface with `Execute(vmName, command)` and `ScpUpload(vmName, localPath, remotePath)`
- `SshService` implementation using `Renci.SshNet` (SshClient + ScpClient)
- `IMappedDriveReader` — reads files from N:/O:/P: mapped drives (read-only)
- `VmConfig` model: VM name, SSH host, SSH user, mapped drive letter, remote project path
- `appsettings.json` with VM pool configuration for all 3 VMs
- `RunDirectoryLayout` — knows the canonical paths for `.comms/`, `plans/`, outputs on both sides (VM path vs mapped drive path)
- CLAUDE.md template for VM workers

**Key files:**
```
src/
├── Farmer.sln
├── Farmer.Core/
│   ├── Models/RunRequest.cs, TaskPacket.cs, RunStatus.cs, CostReport.cs, ReviewVerdict.cs
│   ├── Contracts/ISshService.cs, IMappedDriveReader.cs, IVmManager.cs, IRunStore.cs
│   └── Config/VmConfig.cs, FarmerSettings.cs
├── Farmer.Tools/
│   ├── SshService.cs              (Renci.SshNet SSH + SCP)
│   ├── MappedDriveReader.cs       (File.ReadAllText on N:/O:/P:)
│   └── RunDirectoryLayout.cs
├── Farmer.Host/
│   └── Program.cs (minimal shell)
├── Farmer.Worker/
│   └── CLAUDE.md (template)
└── Farmer.Tests/
    ├── Models/ContractSerializationTests.cs
    └── Tools/RunDirectoryLayoutTests.cs
```

**Smoke test:**
- Unit tests: JSON roundtrip for all contract models
- Unit test: `RunDirectoryLayout` generates correct VM-side and host-side paths
- Integration test (optional, needs VM): SSH connect + execute `echo hello` + read result via mapped drive

**Resume point:** Solution builds, models serialize, layout paths are correct.

---

## Phase 2: Workflow Pipeline + State Machine

**Goal:** Core workflow as a sequential pipeline. Each stage is an interface so it can be stubbed. Must compile and pass all tests before moving on.

**Lessons from Phase 1:**
- Every `.cs` file MUST have correct `using` statements (missed `using Xunit;`, `using Microsoft.Extensions.*`)
- Every project MUST have correct NuGet PackageReferences for all usings (missed Logging/Options in classlib)
- Text-based validation is insufficient — `dotnet build && dotnet test` is the only real gate
- Nullable types from external libraries need explicit handling (SSH.NET `int?`)

**What gets built:**
- `IWorkflowStage` — non-generic interface: `Task<StageResult> ExecuteAsync(RunFlowState state, CancellationToken ct)`
- `StageResult` — Success/Failure/Skip with optional error message
- `RunWorkflow` — orchestrator that runs stages in sequence, updating `RunFlowState` between stages
- 7 stage implementations (all stubbed — just update state + return Success):
  - `CreateRunStage` — generates run_id, writes RunRequest via `IRunStore`
  - `LoadPromptsStage` — reads numbered prompt files, builds TaskPacket
  - `ReserveVmStage` — claims a VM via `IVmManager`
  - `DeliverStage` — SCP uploads via `ISshService` (stub: no-op)
  - `DispatchStage` — SSH triggers worker (stub: no-op)
  - `CollectStage` — reads outputs via `IMappedDriveReader` (stub: no-op)
  - `ReviewStage` — placeholder (stub: auto-accept)
- `RunFlowState` — references existing models: run_id, vm_id (from VmConfig.Name), attempt, RunPhase, timestamps, prompt list, WorkflowResult
- `WorkflowResult` — success/failure/retry with collected artifacts

**Key insight:** `Deliver` and `Dispatch` are separate stages because delivery is SCP (file copy) and dispatch is SSH (process start).

**Key files:**
```
Farmer.Core/
├── Workflow/IWorkflowStage.cs        (interface + StageResult)
├── Workflow/RunWorkflow.cs           (orchestrator)
├── Workflow/RunFlowState.cs          (shared state)
├── Workflow/WorkflowResult.cs        (final outcome)
├── Workflow/Stages/CreateRunStage.cs
├── Workflow/Stages/LoadPromptsStage.cs
├── Workflow/Stages/ReserveVmStage.cs
├── Workflow/Stages/DeliverStage.cs
├── Workflow/Stages/DispatchStage.cs
├── Workflow/Stages/CollectStage.cs
└── Workflow/Stages/ReviewStage.cs

Farmer.Tests/
└── Workflow/
    ├── RunWorkflowTests.cs           (orchestrator: stage ordering, state transitions)
    ├── CreateRunStageTests.cs        (writes RunRequest, generates run_id)
    └── LoadPromptsStageTests.cs      (reads prompt files, orders by prefix)
```

**Dependencies on existing code:**
- `RunFlowState` uses: `RunPhase` enum (from Models/RunStatus.cs), `VmConfig` (from Config/VmConfig.cs)
- `CreateRunStage` calls: `IRunStore.SaveRunRequestAsync()`, `IRunStore.SaveRunStatusAsync()`
- `LoadPromptsStage` reads files from `FarmerSettings.SamplePlansPath`
- Stages reference `RunDirectoryLayout` for path resolution
- No new NuGet packages needed — only Farmer.Core internal types

**Smoke test (MUST pass before Phase 3):**
```
dotnet build   — 0 errors, 0 warnings
dotnet test    — all tests green including:
  - RunWorkflowTests: 7 stages execute in order
  - RunWorkflowTests: FlowState.Phase transitions correctly per stage
  - RunWorkflowTests: stage failure stops pipeline, sets Failed state
  - CreateRunStageTests: generates unique run_id, persists RunRequest
  - LoadPromptsStageTests: reads sample-plans dir, orders by numeric prefix
  - LoadPromptsStageTests: fails gracefully on missing/empty directory
```

**Resume point:** Workflow runs end-to-end with stubs. State transitions are validated. All tests green.

---

## Phase 3: VmManager + Wire Real Stages + Shakedown

**Goal:** SshService and MappedDriveReader already exist from Phase 1. This phase adds VmManager, wires real stage implementations (replacing stubs), and validates with a shakedown script.

**Architectural issue to resolve first:**
- `RunDirectoryLayout` is in `Farmer.Tools` (static class) but stages are in `Farmer.Core`
- Stages need path resolution to know where to SCP files, what to read from mapped drive
- **Solution:** Move `RunDirectoryLayout` to `Farmer.Core` (it's pure path logic with no external deps)
- This avoids circular dependency (Core → Tools → Core)

**What gets built:**
- Move `RunDirectoryLayout` from `Farmer.Tools` → `Farmer.Core` (namespace: `Farmer.Core.Layout`)
- `VmManager` — implements `IVmManager`, pool of VMs with state tracking:
  - States: Available, Reserved, Busy, Draining, Error
  - `ReserveAsync()` — returns first available VM
  - `ReleaseAsync(vmId)` — returns VM to pool
  - Thread-safe (ConcurrentDictionary or lock)
- Wire real implementations into workflow stages (inject interfaces via constructor):
  - `DeliverStage` — inject `ISshService`, `IRunStore`:
    - Ensures `.comms/` and `plans/` dirs exist on VM (SSH mkdir -p)
    - SCPs each prompt .md file to VM's `~/projects/plans/`
    - SCPs task-packet.json to VM
    - SCPs CLAUDE.md to VM
  - `DispatchStage` — inject `ISshService`, `FarmerSettings`:
    - SSH: `cd ~/projects && claude --dangerously-skip-permissions -p "$(cat plans/task-packet.json)"`
    - Uses dispatch timeout (default 30min from FarmerSettings)
    - Checks SshResult.Success, returns Failure with stderr on error
  - `CollectStage` — inject `IMappedDriveReader`, `IRunStore`:
    - Waits for `output/manifest.json` on mapped drive (with timeout)
    - Reads + deserializes `Manifest` and `Summary` JSON
    - Validates required fields (FilesChanged not empty, Description not empty)
    - Persists collected artifacts to RunStore
  - `ReserveVmStage` — already wired (calls `IVmManager.ReserveAsync()`)
- PowerShell shakedown script: `scripts/shakedown.ps1`
  - Tests all 7 categories x configured VMs
  - SSH connectivity, file write/read/delete, .comms round-trip, git status, mapped drive readable
- Unit tests for VmManager + real stage logic (using test doubles for ISshService/IMappedDriveReader)

**Key files:**
```
Farmer.Core/
├── Layout/RunDirectoryLayout.cs  (moved from Farmer.Tools)
└── Workflow/Stages/
    ├── DeliverStage.cs    (real: calls ISshService)
    ├── DispatchStage.cs   (real: calls ISshService)
    └── CollectStage.cs    (real: calls IMappedDriveReader + deserializes JSON)

Farmer.Tools/
├── VmManager.cs
└── RunDirectoryLayout.cs  (deleted — moved to Core)

scripts/
└── shakedown.ps1

Farmer.Tests/
├── Tools/VmManagerTests.cs         (state machine: reserve, release, concurrent, error)
├── Workflow/DeliverStageTests.cs   (mock ISshService, verify SCP calls)
├── Workflow/DispatchStageTests.cs  (mock ISshService, verify SSH command)
├── Workflow/CollectStageTests.cs   (mock IMappedDriveReader, verify file reads + parsing)
└── Integration/SshServiceTests.cs  (requires VM connectivity — skipped in CI)
```

**Smoke test:**
- `dotnet build && dotnet test` — all green including:
  - VmManager: reserve/release cycle, no-VMs-available, thread safety, error recovery
  - DeliverStage: verifies correct SCP calls made with right paths
  - DispatchStage: verifies SSH command format, timeout handling, failure propagation
  - CollectStage: verifies file wait, JSON deserialization, validation of required fields
  - All existing 38 tests still pass (no regressions)
- `.\scripts\shakedown.ps1` (on Windows with VMs) — 7 categories x N VMs all pass

**Resume point:** VmManager works, all stages have real implementations with tested logic. Shakedown validates infra.

---

## Phase 4: Middleware Layer

**Goal:** Cross-cutting concerns via middleware pattern wrapping each workflow stage.

**Lessons applied:**
- No new NuGet packages needed — use `Microsoft.Extensions.Logging` (not Serilog)
- Middleware parameter must be optional in `RunWorkflow` constructor to avoid breaking existing 10 tests
- HeartbeatMiddleware must skip when `state.Vm` is null (before ReserveVmStage)
- CostTrackingMiddleware accumulates during run, needs explicit flush at workflow end

**What gets built:**
- `IWorkflowMiddleware` — wraps stage execution:
  ```csharp
  Task<StageResult> InvokeAsync(IWorkflowStage stage, RunFlowState state,
      Func<Task<StageResult>> next, CancellationToken ct);
  ```
- Modify `RunWorkflow` constructor to accept optional `IEnumerable<IWorkflowMiddleware>`:
  - Default to empty list (no middleware) — preserves existing test compatibility
  - Each middleware wraps `stage.ExecuteAsync` via the `next` delegate pattern
- `LoggingMiddleware` — uses `ILogger`, logs stage name + duration + outcome per stage
- `CostTrackingMiddleware`:
  - Tracks `Stopwatch` per stage, accumulates `List<StageCost>`
  - Exposes `GetReport(runId)` method that returns `CostReport`
  - `RunWorkflow` calls this after pipeline completes to persist via `IRunStore`
- `HeartbeatMiddleware`:
  - After each stage completes, writes `progress.md` to VM via `ISshService.ScpUploadContentAsync()`
  - **Skips** when `state.Vm` is null (early stages before VM reservation)
  - Format: YAML front matter + markdown body
  - Uses `RunDirectoryLayout.VmProgressFile()` for path

**Key files:**
```
Farmer.Core/
├── Middleware/IWorkflowMiddleware.cs
├── Middleware/LoggingMiddleware.cs
├── Middleware/CostTrackingMiddleware.cs
└── Middleware/HeartbeatMiddleware.cs

Farmer.Core/Workflow/
└── RunWorkflow.cs  (modified: accept optional middleware list, wrap stage calls)

Farmer.Tests/
├── Middleware/LoggingMiddlewareTests.cs
├── Middleware/CostTrackingMiddlewareTests.cs
└── Middleware/HeartbeatMiddlewareTests.cs
```

**Smoke test (MUST pass before Phase 5):**
```
dotnet build && dotnet test — all green including:
  - All existing 66 tests pass (no constructor breakage)
  - LoggingMiddleware: logs stage name and duration
  - CostTrackingMiddleware: accumulates timing per stage, GetReport returns valid CostReport
  - CostTrackingMiddleware: duration > 0 for stages that take time
  - HeartbeatMiddleware: calls ISshService with correct progress.md path and content
  - HeartbeatMiddleware: skips when state.Vm is null
  - RunWorkflow with middleware: middleware wraps in correct order
```

**Resume point:** Every workflow run can produce structured logs, cost data, and remote progress updates. All middleware is testable in isolation.

---

## Phase 5: OpenTelemetry + OpenAI-Compatible API (Combined)

**Goal:** Wire up the full entry point so you can submit a work request via HTTP, watch it execute through the workflow with all middleware, and see OTel traces + logs + cost reports. Combines old Phases 5+6 because OTel instrumentation isn't visible without a running host.

**Lessons applied:**
- OTel plumbing alone produces nothing visible — need a running host + API to exercise it
- DI wiring is where missing packages and constructor mismatches surface — test early
- WorkRequestLoader duplicates LoadPromptsStage logic — reuse or share
- Sample plans already exist in `data/sample-plans/` from Phase 1

**What gets built:**

### OTel instrumentation:
- `TelemetryMiddleware` — implements `IWorkflowMiddleware`, wraps each stage in an `Activity` span
  - Tags: stage_name, run_id, outcome, duration
  - Child of root `workflow/{run_id}` span
- `FarmerActivitySource` — `ActivitySource("Farmer.Workflow")` singleton
- `FarmerMetrics` — custom metrics: `farmer.runs.total` (counter), `farmer.stage.duration_seconds` (histogram)
- `TelemetrySetup` — DI registration in `Program.cs`:
  - OpenTelemetry SDK with OTLP exporter (for Aspire Dashboard)
  - Console exporter (for immediate terminal visibility)
  - Resource: service.name=Farmer, service.version=0.1.0

### OpenAI-compatible API:
- `POST /v1/chat/completions` — accepts OpenAI ChatCompletion format:
  - Extracts work request name from message content (e.g., `"load:react-grid-component"`)
  - Constructs full stage pipeline with DI
  - Runs workflow async, returns run_id immediately
- `GET /v1/runs/{runId}/status` — returns current RunStatus from RunStore
- `GET /v1/runs/{runId}/result` — returns WorkflowResult + CostReport
- Full DI wiring in `Program.cs`:
  - Register all services: ISshService, IMappedDriveReader, IVmManager, IRunStore
  - Register all stages in pipeline order
  - Register all middleware: Logging, CostTracking, Heartbeat, Telemetry
  - Configure FarmerSettings from appsettings.json

### NuGet packages needed (Farmer.Host):
- `OpenTelemetry.Extensions.Hosting`
- `OpenTelemetry.Exporter.Console`
- `OpenTelemetry.Exporter.OpenTelemetryProtocol`

**Key files:**
```
Farmer.Core/
└── Middleware/TelemetryMiddleware.cs

Farmer.Host/
├── Program.cs                          (full DI wiring + OTel setup)
├── Telemetry/FarmerActivitySource.cs
├── Telemetry/FarmerMetrics.cs
├── Telemetry/TelemetrySetup.cs
├── Controllers/CompletionsController.cs
├── Controllers/RunsController.cs
├── OpenAI/ChatCompletionRequest.cs
├── OpenAI/ChatCompletionResponse.cs
└── appsettings.json                    (updated with OTel config)

Farmer.Tests/
├── Middleware/TelemetryMiddlewareTests.cs
└── Controllers/CompletionsControllerTests.cs (optional — integration)
```

**Smoke test — the "see everything light up" moment:**
```powershell
# 1. Optional: start Aspire Dashboard for trace UI
docker run --rm -p 18888:18888 -p 4317:4317 mcr.microsoft.com/dotnet/aspire-dashboard

# 2. Start host
cd src/Farmer.Host
dotnet run

# 3. Submit work request (stubs will execute instantly)
$r = Invoke-RestMethod -Uri http://localhost:5000/v1/chat/completions -Method Post `
  -Body '{"model":"claude-worker","messages":[{"role":"user","content":"load:react-grid-component"}]}' `
  -ContentType "application/json"

# 4. Poll status
Invoke-RestMethod -Uri "http://localhost:5000/v1/runs/$($r.run_id)/status"

# 5. Check console output — should show:
#    - Structured log lines per stage (LoggingMiddleware)
#    - OTel span output (ConsoleExporter)
#    - Cost report written

# 6. Check Aspire Dashboard at http://localhost:18888 — trace waterfall
```

**Resume point:** Full observability pipeline working. Submit via HTTP, see traces, logs, and cost data.

---

## Phase 6: Worker Script + CLAUDE.md + End-to-End

**Goal:** The VM-side worker script and CLAUDE.md that make Claude CLI actually do the work. Wire everything together for the first real end-to-end run.

**What gets built:**
- `worker.sh` — main entry point triggered via SSH:
  ```
  1. Receive run_id as argument
  2. Read task-packet.json from ~/projects/plans/
  3. Write "started" to .comms/progress.md
  4. Create feature branch: {vm-name}-{work-request-name}
  5. For each prompt file (sorted by numeric prefix):
     a. Update .comms/progress.md with current prompt #
     b. Feed prompt content to: claude --dangerously-skip-permissions -p "$(cat prompt.md)"
     c. Log output to execution-log.txt
  6. Write .comms/progress.md = "building-complete"
  7. Run retro: claude --dangerously-skip-permissions -p "Review what you just built..."
  8. Git add, commit, push feature branch
  9. Write output/manifest.json (list of files changed)
  10. Write output/summary.json (what was built, any issues)
  11. Write .comms/progress.md = "complete"
  12. Clean workspace (remove plan files, reset working directory)
  ```
- `CLAUDE.md` template — instructs the Claude CLI:
  - How to read prompts from `plans/` directory
  - How to update `.comms/progress.md` with status
  - Branch naming convention
  - Output artifact requirements
  - Self-review/retro instructions
- `DeliverStage` (real) — SCPs plan files + CLAUDE.md to VM
- `DispatchStage` (real) — SSHs and runs `bash ~/projects/worker.sh {run_id}`
- `CollectStage` (real) — reads outputs from mapped drive, validates manifest
- `ProgressMonitor` — polls mapped drive `.comms/progress.md` with configurable interval, fires events on state change

**Key files:**
```
Farmer.Worker/
├── worker.sh
└── CLAUDE.md

Farmer.Tools/
└── ProgressMonitor.cs

Farmer.Core/Workflow/Stages/
├── DeliverStage.cs    (real SCP implementation)
├── DispatchStage.cs   (real SSH implementation)
└── CollectStage.cs    (real mapped-drive read)
```

**Smoke test — the big one:**
1. Start Farmer.Host
2. `POST /v1/chat/completions` with `load:react-grid-component`
3. Watch `N:\projects\.comms\progress.md` update in real-time
4. Feature branch appears on git remote
5. `GET /v1/runs/{runId}/status` shows `complete`
6. Aspire Dashboard shows full trace waterfall
7. Console logs show structured stage timing

**Resume point:** Full vertical slice working end-to-end with 1 VM.

---

## Phase 7: QA Inspection Agent + Retry Loop

**Goal:** Host-side QA scoring using OpenAI API, with retry capability.

**What gets built:**
- `QaInspectionAgent` — runs on host, calls OpenAI API:
  1. Reads `output/summary.json` + `output/manifest.json` from mapped drive
  2. Reads git diff of feature branch
  3. Sends structured scoring prompt to OpenAI (GPT-4o)
  4. Parses response into `ReviewVerdict`
- `ReviewVerdict`: verdict (accept/retry/reject), risk_score (0-100), findings[], suggestions[]
- `ScoringPromptBuilder` — constructs the QA prompt with: original requirements, build output, diff, checklist
- Retry loop in `ReviewStage`:
  - If `retry` and `attempt < max_retries`: inject feedback into new TaskPacket, re-deliver, re-dispatch
  - Feedback file: `plans/0-feedback.md` (prepended before original prompts)
- `review.json` written to run directory (via SSH to VM)
- `final-status.json` with overall outcome

**Key files:**
```
Farmer.Core/
├── Review/QaInspectionAgent.cs
├── Review/ScoringPromptBuilder.cs
├── Review/ReviewVerdict.cs
└── Review/RetryPolicy.cs

Farmer.Host/
└── Services/OpenAiClientFactory.cs
```

**Smoke test:**
1. End-to-end run completes
2. `review.json` exists on mapped drive with structured verdict
3. If verdict is `retry`, second attempt runs automatically with feedback
4. OTel trace shows QA span + retry spans if applicable
5. `final-status.json` reflects final verdict

**Resume point:** Complete system with QA gate. Ready for multi-VM scaling.

---

## Data Flow Summary (with asymmetric read/write)

```
Host writes to VM:  SSH/SCP  ──────────────────────────────────────────┐
                                                                       │
  ┌─────────────┐    SCP: plans/*.md + CLAUDE.md    ┌─────────────────▼──┐
  │  Farmer.Host │ ──────────────────────────────── │  claudefarm1 (VM)   │
  │  (Windows)   │    SSH: bash worker.sh {run_id}  │  ~/projects/        │
  │              │ ──────────────────────────────── │    plans/            │
  │              │                                   │    .comms/progress.md│
  │              │    Read: N:\projects\.comms\*     │    output/           │
  │              │ ◄──────────────────────────────── │    CLAUDE.md         │
  └─────────────┘    (SSHFS mapped drive, READ-ONLY) └────────────────────┘
                                                                       │
Host reads from VM: Mapped drive (N:) ─────────────────────────────────┘
```

---

## Key Design Decisions

| Decision | Choice | Why |
|----------|--------|-----|
| Write to VM | SSH/SCP only | Mapped drives are read-only (SSHFS limitation) |
| Read from VM | Mapped drive (N:/O:/P:) | Direct file access, no SSH overhead for reads |
| SSHFS lag | 500ms retry in MappedDriveReader | Known cache delay after SSH writes |
| Plan naming | `1-slug.md` (no brackets) | PowerShell interprets `[N]` as wildcards |
| SSH library | Renci.SshNet | Proven in previous build, handles SCP + SSH |
| Heartbeat | Write to `.comms/progress.md` via SSH | Can't write via mapped drive; host reads via mapped drive |
| Feature branch | `{vm-name}-{work-request-name}` | Clean isolation per work request |
| Prompt ordering | Numeric prefix sort (`1-`, `2-`, ...) | Simple, explicit, no ambiguity |
| QA agent | Host-side OpenAI API | No VM needed; designed to migrate to VM later |
| Workflow stages | 7 stages with interface abstraction | Each independently testable + stubbable |
| Middleware | Separate from stages | Cross-cutting concerns don't pollute business logic |
| Scaling | VmManager with pool abstraction | Start 1, add VMs by config change |

---

## Verification Checklist (per phase)

- [x] Phase 1: `dotnet build` succeeds, contract serialization tests pass (19 tests)
- [x] Phase 2: Workflow integration test passes with stubs (38 tests)
- [x] Phase 3: VmManager + real stages + shakedown script (66 tests)
- [x] Phase 4: Middleware layer — logging, cost tracking, heartbeat (79 tests)
- [ ] Phase 5: OTel + API — submit via HTTP, see traces in Aspire Dashboard
- [ ] Phase 6: Worker script + CLAUDE.md — full end-to-end with real Claude CLI on VM
- [ ] Phase 7: QA inspection agent — verdict scoring + retry loop
