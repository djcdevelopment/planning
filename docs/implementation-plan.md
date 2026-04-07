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

**Goal:** Core workflow as a sequential pipeline. Each stage is an interface so it can be stubbed.

**What gets built:**
- `IWorkflowStage<TIn, TOut>` — generic stage interface with `ExecuteAsync`
- `RunWorkflow` — orchestrator that runs stages in sequence, passing `RunFlowState`
- 7 stages (all stubbed initially):
  - `CreateRunStage` — generates run_id, writes RunRequest
  - `LoadPromptsStage` — reads numbered prompt files from `data/sample-plans/`, builds TaskPacket
  - `ReserveVmStage` — claims a VM from the pool
  - `DeliverStage` — SCP uploads plan files + CLAUDE.md to VM's `~/projects/plans/`
  - `DispatchStage` — SSH triggers `claude --dangerously-skip-permissions` on VM
  - `CollectStage` — reads outputs from mapped drive, validates required files exist
  - `ReviewStage` — placeholder for QA (auto-accepts for now)
- `RunFlowState` — run_id, vm_id, attempt, status enum, timestamps, prompt list
- `WorkflowResult` — success/failure/retry with collected artifacts

**Key insight:** `Deliver` and `Dispatch` are separate stages because delivery is SCP (file copy) and dispatch is SSH (process start). The old plan combined them.

**Key files:**
```
Farmer.Core/
├── Workflow/IWorkflowStage.cs
├── Workflow/RunWorkflow.cs
├── Workflow/RunFlowState.cs
├── Workflow/WorkflowResult.cs
├── Workflow/Stages/CreateRunStage.cs
├── Workflow/Stages/LoadPromptsStage.cs
├── Workflow/Stages/ReserveVmStage.cs
├── Workflow/Stages/DeliverStage.cs
├── Workflow/Stages/DispatchStage.cs
├── Workflow/Stages/CollectStage.cs
└── Workflow/Stages/ReviewStage.cs
```

**Smoke test:** Integration test with all stubs — verifies 7 stages execute in order, FlowState transitions are correct, and WorkflowResult is populated.

**Resume point:** Workflow runs end-to-end with stubs. State transitions are validated.

---

## Phase 3: SSH + SCP Real Implementation + Shakedown

**Goal:** Replace SSH/SCP stubs with real implementations. Build the shakedown test to validate infrastructure.

**What gets built:**
- `SshService` real implementation:
  - `Execute(vmName, command)` — opens SSH connection, runs command, returns stdout/stderr
  - `ScpUpload(vmName, localPath, remotePath)` — uploads file via ScpClient
  - Connection pooling / reuse per VM
  - Timeout handling (configurable, default 30s for commands, 5min for dispatch)
- `MappedDriveReader` real implementation:
  - Reads from configured drive letter path
  - 500ms retry with backoff for SSHFS cache lag
  - File existence polling with timeout
- `VmManager` — pool of VMs with state tracking:
  - States: Available, Reserved, Busy, Draining, Error
  - `ReserveAsync()` — returns first available VM
  - `ReleaseAsync(vmId)` — returns VM to pool
  - Thread-safe (ConcurrentDictionary or lock)
- PowerShell shakedown script: `scripts/shakedown.ps1`
  - Tests all 7 categories x configured VMs
  - SSH connectivity, file write/read/delete, .comms round-trip, git status, mapped drive readable

**Key files:**
```
Farmer.Tools/
├── SshService.cs          (real Renci.SshNet implementation)
├── MappedDriveReader.cs   (real mapped drive reader with retry)
└── VmManager.cs

scripts/
└── shakedown.ps1

Farmer.Tests/
└── Integration/SshServiceTests.cs (requires VM connectivity)
```

**Smoke test:**
- `.\scripts\shakedown.ps1` — all 21 tests pass (7 categories x 3 VMs, or 7 x 1 for single-VM start)
- Integration test: SCP a test file to VM, wait 500ms, read it back via mapped drive, delete via SSH

**Resume point:** Can reliably write to VMs and read back. Infrastructure is validated.

---

## Phase 4: Middleware Layer

**Goal:** Cross-cutting concerns via middleware pattern wrapping each workflow stage.

**What gets built:**
- `IWorkflowMiddleware` — wraps stage execution with before/after hooks
- `LoggingMiddleware` — structured logging (Serilog) of stage entry/exit/duration/errors
- `CostTrackingMiddleware` — accumulates wall-clock time per stage, writes `cost-report.json` at end
- `HeartbeatMiddleware` — after each stage transition, writes `progress.md` to VM's `.comms/` via SSH
  - **Critical:** Must write via SSH, NOT via mapped drive (read-only!)
  - Format: machine-readable YAML front matter + human-readable markdown body
  - Contains: current_phase, progress_pct, started_at, updated_at, stages_completed[]
- Middleware pipeline builder: `workflow.Use<LoggingMiddleware>().Use<HeartbeatMiddleware>().Use<CostTrackingMiddleware>()`

**Key files:**
```
Farmer.Core/
├── Middleware/IWorkflowMiddleware.cs
├── Middleware/MiddlewarePipeline.cs
├── Middleware/LoggingMiddleware.cs
├── Middleware/CostTrackingMiddleware.cs
└── Middleware/HeartbeatMiddleware.cs
```

**Smoke test:** Run workflow with middleware, verify:
1. Console logs show structured stage timing
2. `.comms/progress.md` on VM (read via mapped drive) shows stage progression
3. Cost report JSON has timing entries for each stage

**Resume point:** Every workflow run produces structured logs, cost data, and remote progress updates.

---

## Phase 5: OpenTelemetry Instrumentation

**Goal:** Distributed tracing with per-stage spans, exportable to Aspire Dashboard or any OTLP collector.

**What gets built:**
- `FarmerActivitySource` — `ActivitySource("Farmer.Workflow")` singleton
- Root span: `workflow/{run_id}` with tags: run_id, vm_id, work_request_name
- Child spans per stage: `stage/{stage_name}` with duration + status
- Dispatch span specifically captures SSH round-trip time
- Custom metrics via `System.Diagnostics.Metrics`:
  - `farmer.runs.total` (counter)
  - `farmer.run.duration_seconds` (histogram)
  - `farmer.stage.duration_seconds` (histogram, tagged by stage name)
- `TelemetrySetup` — DI registration, OTLP exporter config, console exporter for dev
- Resource tags: service.name=Farmer, service.version, host.name

**Key files:**
```
Farmer.Host/
├── Telemetry/FarmerActivitySource.cs
├── Telemetry/FarmerMetrics.cs
└── Telemetry/TelemetrySetup.cs

Farmer.Core/
└── Middleware/TelemetryMiddleware.cs  (wraps stages in Activity spans)
```

**Smoke test:**
- Run workflow with console exporter — verify spans appear in stdout
- Optional: `docker run --rm -p 18888:18888 -p 4317:4317 mcr.microsoft.com/dotnet/aspire-dashboard` and verify trace waterfall in browser

**Resume point:** Full observability. Every run produces a trace waterfall.

---

## Phase 6: OpenAI-Compatible API + Prompt Loader

**Goal:** HTTP entry point that external tools can call to submit work requests.

**What gets built:**
- `POST /v1/chat/completions` — OpenAI-compatible endpoint:
  - Accepts standard ChatCompletion request format
  - Extracts work request name from message content (e.g., `"load:react-grid-component"`)
  - Triggers async workflow run, returns run_id in response
  - Streams progress updates via SSE if `stream: true` (stretch goal)
- `GET /v1/runs/{runId}/status` — polling endpoint for run status
- `GET /v1/runs/{runId}/result` — final result with artifacts
- `WorkRequestLoader`:
  - Reads from `data/sample-plans/{work-request-name}/`
  - Glob for `*.md` files, sort by numeric prefix (`1-`, `2-`, etc.)
  - Builds ordered `TaskPacket` with prompt content
  - Validates: at least 1 prompt, all files readable, no bracket naming
- Sample work requests (2-3 directories with example prompts)

**Key files:**
```
Farmer.Host/
├── Controllers/CompletionsController.cs
├── Controllers/RunsController.cs
├── Services/WorkRequestLoader.cs
├── OpenAI/ChatCompletionRequest.cs
├── OpenAI/ChatCompletionResponse.cs
└── OpenAI/RunStatusResponse.cs

data/
└── sample-plans/
    ├── react-grid-component/
    │   ├── 1-SetupProject.md
    │   ├── 2-BuildGridComponent.md
    │   └── 3-AddTests.md
    └── api-endpoint/
        ├── 1-DefineSchema.md
        └── 2-ImplementEndpoint.md
```

**Smoke test:**
```powershell
# Submit work request
$response = Invoke-RestMethod -Uri http://localhost:5000/v1/chat/completions -Method Post -Body '{"model":"claude-worker","messages":[{"role":"user","content":"load:react-grid-component"}]}' -ContentType "application/json"

# Poll for status
Invoke-RestMethod -Uri "http://localhost:5000/v1/runs/$($response.run_id)/status"
```

**Resume point:** External tools can submit work and poll status via standard HTTP.

---

## Phase 7: Worker Script + CLAUDE.md + End-to-End

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

## Phase 8: QA Inspection Agent + Retry Loop

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

- [ ] Phase 1: `dotnet build` succeeds, contract serialization tests pass
- [ ] Phase 2: Workflow integration test passes with stubs
- [ ] Phase 3: `shakedown.ps1` passes all tests, SCP round-trip works
- [ ] Phase 4: Structured logs appear, `.comms/progress.md` updated via SSH
- [ ] Phase 5: OTel spans visible in console exporter or Aspire Dashboard
- [ ] Phase 6: HTTP endpoint accepts request, returns run_id, status polling works
- [ ] Phase 7: Full end-to-end run with real Claude CLI on VM
- [ ] Phase 8: QA verdict written, retry loop works when triggered
