# Phase 5 Build Log — What Got Built Tonight

This is the literal record of what was added/changed in Phase 5. Each section answers: **what file**, **why it exists**, **what it does**.

---

## 1. Telemetry Primitives (Step 0)

**Goal:** Prove OTel + Aspire visibility before touching anything else.

### `src/Farmer.Core/Telemetry/FarmerActivitySource.cs` (NEW)
- Static `ActivitySource("Farmer", "1.0.0")`.
- `StartRun(runId)` — opens a `workflow.run` root span tagged with `farmer.run_id`.
- `StartStage(runId, stageName)` — opens a `workflow.stage.{name}` child span.

### `src/Farmer.Core/Telemetry/FarmerMetrics.cs` (NEW)
- Static `Meter("Farmer", "1.0.0")`.
- Counters: `farmer.runs.started`, `farmer.runs.completed`, `farmer.runs.failed`, `farmer.vm.commands.executed`, `farmer.vm.commands.failed`.
- Histogram: `farmer.stage.duration` (ms).

### `src/Farmer.Host/Farmer.Host.csproj` (MODIFIED)
- Added 4 OTel NuGet packages (1.10.x): `OpenTelemetry.Extensions.Hosting`, `Exporter.OpenTelemetryProtocol`, `Exporter.Console`, `Instrumentation.AspNetCore`.

### Step 0 verification (deleted after)
- Added a temporary `/test-trace` endpoint that emitted a fake 7-stage workflow.
- Started Aspire Dashboard via `docker run … aspire-dashboard:9.0` on ports 18888 (UI) + 18889 (OTLP gRPC).
- Confirmed: root `workflow.run` span with 7 child stage spans, console + OTLP exporters working, structured logs correlated.
- **This was removed after Step 0; the production wiring lives in `Program.cs`.**

---

## 2. Externalized Config (Step 1)

**Goal:** Move runtime state to `D:\work\planning-runtime\`. Repo stays a pure engine.

### `src/Farmer.Core/Config/PathsSettings.cs` (NEW)
```csharp
public sealed class PathsSettings
{
    public string Root  { get; set; } = @"D:\work\planning-runtime";
    public string Data  { get; set; } = @"D:\work\planning-runtime\data";
    public string Runs  { get; set; } = @"D:\work\planning-runtime\runs";
    public string Inbox { get; set; } = @"D:\work\planning-runtime\inbox";
    public string Outbox{ get; set; } = @"D:\work\planning-runtime\outbox";
    public string Qa    { get; set; } = @"D:\work\planning-runtime\qa";

    public string SamplePlansPath => Path.Combine(Data, "sample-plans");
}
```

### `src/Farmer.Core/Config/TelemetrySettings.cs` (NEW)
```csharp
public sealed class TelemetrySettings
{
    public string ServiceName        { get; set; } = "Farmer";
    public string OtlpEndpoint       { get; set; } = "http://localhost:18889";
    public bool   EnableConsoleExporter { get; set; } = true;
    public bool   EnableOtlpExporter    { get; set; } = true;
}
```

### `src/Farmer.Core/Config/FarmerSettings.cs` (REPLACED)
- **Removed** flat `DataPath`, `SamplePlansPath`, `RunStorePath` properties.
- **Added** nested `Paths` and `Telemetry` sub-objects.
- VMs and SSH timeout settings unchanged.

### `src/Farmer.Host/appsettings.json` (REPLACED)
- All stale `D:\work\start\farmer\*` paths removed.
- New `Paths` and `Telemetry` sections matching the C# shape.
- VM list unchanged (claudefarm1/2/3).

### Ripple changes (forced by config rename)
- `src/Farmer.Tools/FileRunStore.cs` — `_settings.RunStorePath` → `_settings.Paths.Runs` (every save method).
- `src/Farmer.Core/Workflow/Stages/LoadPromptsStage.cs` — `_settings.SamplePlansPath` → `_settings.Paths.SamplePlansPath`.
- `src/Farmer.Tests/Workflow/LoadPromptsStageTests.cs` — test helper now creates a `_samplePlansDir` under `_tempDir/sample-plans/` to match the new layout.

---

## 3. Run Directory Layout (Step 2)

**Goal:** Each run is fully reconstructable from disk. No hidden state.

### `src/Farmer.Core/Layout/RunDirectoryLayout.cs` (MODIFIED)
- **Renamed** `RunStatusFile` → `RunStateFile` (returns `state.json`, was `status.json`).
- **Added** `RunEventsFile` → `events.jsonl`.
- **Added** `RunResultFile` → `result.json`.
- **Added** `RunLogsDir` → `logs/`.
- **Added** `RunArtifactsDir` → `artifacts/`.
- **Removed** `RunFinalStatusFile` (superseded by `result.json`).
- **`EnsureRunDirectory` now also creates `logs/` and `artifacts/` subdirs.**

**Final per-run file structure:**
```
runs/{RUN_ID}/
├── request.json      ← input (created by RunDirectoryFactory)
├── state.json        ← snapshot, updated after every stage
├── events.jsonl      ← append-only event log
├── result.json       ← terminal result (only on completion/failure)
├── task-packet.json  ← LoadPromptsStage output
├── logs/             ← VM command logs (future)
└── artifacts/        ← build outputs (future)
```

### `src/Farmer.Core/Contracts/IRunStore.cs` (MODIFIED)
- **Renamed** `SaveRunStatusAsync`/`GetRunStatusAsync` → `SaveRunStateAsync`/`GetRunStateAsync`.
- All consumers updated: `FileRunStore`, `CreateRunStage`, `CollectStage`, plus 3 test files with `InMemoryRunStore` doubles.

---

## 4. Eventing as Source of Truth (Steps 3-4)

**Goal:** Every stage transition is recorded once, atomically, in `events.jsonl` + `state.json`.

### `src/Farmer.Core/Models/RunEvent.cs` (NEW)
```csharp
public sealed class RunEvent
{
    public DateTimeOffset Timestamp { get; set; }
    public string RunId  { get; set; }   // snake_case in JSON
    public string Stage  { get; set; }
    public string Event  { get; set; }   // stage.started | stage.completed | stage.failed | stage.skipped
    public object? Data  { get; set; }
}
```

### `src/Farmer.Core/Middleware/EventingMiddleware.cs` (NEW)
- Implements `IWorkflowMiddleware`.
- **Skips silently when `state.RunDirectory` is null** — preserves existing in-memory test paths.
- Before `next()`: appends `stage.started` event, writes `state.json` snapshot.
- After `next()`: appends `stage.{completed|failed|skipped}` event, writes `state.json`.
- **Catches exceptions from inner middleware/stage**: writes `stage.failed` event with `{outcome:"Exception", error:msg}`, then re-throws so `RunWorkflow`'s exception handler still runs.
- `events.jsonl` written via `File.AppendAllTextAsync` (append-only, single writer per run).
- `state.json` written via temp+rename (atomic, same pattern as `FileRunStore`).

### `src/Farmer.Core/Workflow/RunFlowState.cs` (MODIFIED)
- **Added** `public string? RunDirectory { get; set; }` — set by `ExecuteFromDirectoryAsync`, read by `EventingMiddleware`. Stages ignore it. Defaults to null so existing in-memory tests are unaffected.

---

## 5. Telemetry Mirrors Eventing (Step 4 + Step 6)

**Goal:** Anti-drift rule. Telemetry uses the **same stage boundaries, names, and runId** as eventing — they cannot disagree.

### `src/Farmer.Core/Middleware/TelemetryMiddleware.cs` (NEW)
- Implements `IWorkflowMiddleware`.
- Wraps each stage in `FarmerActivitySource.StartStage(state.RunId, stage.Name)` — same `state.RunId` and `stage.Name` as `EventingMiddleware`.
- Records `FarmerMetrics.StageDuration` histogram tagged with `stage` + `outcome`.
- Sets `Activity.Status = Error` on `StageOutcome.Failure`.
- Outermost middleware in the chain (registered first) so its activity context wraps all inner work.

---

## 6. Directory Entry Point (Step 5)

**Goal:** Inbox-driven runs. Filesystem in, filesystem out. No HTTP required.

### `src/Farmer.Core/Workflow/RunWorkflow.cs` (MODIFIED)
- **Added** `ExecuteFromDirectoryAsync(string runDir, CancellationToken ct)`:
  1. Read `request.json` from `runDir`.
  2. Hydrate `RunFlowState` from the request, set `state.RunDirectory = runDir`.
  3. Reset stateful middleware: `_middleware.OfType<CostTrackingMiddleware>().FirstOrDefault()?.Reset()`.
  4. Open root `workflow.run` activity span via `FarmerActivitySource.StartRun(state.RunId)`.
  5. Increment `FarmerMetrics.RunsStarted`.
  6. Call existing `ExecuteAsync(state, ct)` — the tested core path.
  7. Increment `RunsCompleted` or `RunsFailed`.
  8. Write `result.json` (atomic temp+rename).
- **Existing `ExecuteAsync(RunFlowState, ct)` is untouched** — all 9 RunWorkflow tests still pass.

### `src/Farmer.Core/Workflow/Stages/CreateRunStage.cs` (MODIFIED)
- Skips ID generation when `state.RunId` is already populated (set by `ExecuteFromDirectoryAsync`).
- Existing test contract preserved (when `RunId` is empty, it generates).

---

## 7. Inbox Trigger (Step 7)

**Goal:** Thin background service. All real work lives in `RunDirectoryFactory`, not the watcher.

### `src/Farmer.Host/Services/RunDirectoryFactory.cs` (NEW)
- Reusable component, **not** buried in the watcher.
- `CreateFromInboxFileAsync(inboxFilePath, ct)`:
  1. Parse minimal `InboxTrigger { work_request_name, source }`.
  2. Generate `run_id = run-{yyyyMMdd-HHmmss}-{6char}`.
  3. Create `runs/{run_id}/` with `logs/` and `artifacts/` subdirs.
  4. Build full `RunRequest` (stamp run_id, task_id, attempt_id, timestamps).
  5. Write `request.json` to the run directory.
  6. Return `runDir`.
- Used by **both** `InboxWatcher` (file-based) and the manual `POST /trigger` HTTP endpoint.

### `src/Farmer.Host/Services/InboxWatcher.cs` (NEW)
- `BackgroundService` polling `Paths.Inbox` every 2 seconds for `*.json` files.
- Per file:
  1. Call `_runDirFactory.CreateFromInboxFileAsync(filePath)`.
  2. Delete the inbox file.
  3. Call `_workflow.ExecuteFromDirectoryAsync(runDir)`.
  4. Catch exceptions per-run; never crash the watcher.
- **Sequential** processing — one run at a time. Documented in code as Phase 5 compromise, not yet concurrency-safe.
- Bad inbox files (parse failures) get renamed to `*.bad` to avoid re-processing.

---

## 8. CostTracking Reset (Step 9)

### `src/Farmer.Core/Middleware/CostTrackingMiddleware.cs` (MODIFIED)
- **Added** `Reset()` — clears `_stageCosts` and resets `_totalStopwatch`.
- Documented as a Phase 5 compromise: this middleware is registered as singleton and accumulates state. `RunWorkflow.ExecuteFromDirectoryAsync` calls `Reset()` at the start of each run.
- **Not concurrency-safe** — acceptable for sequential `InboxWatcher` processing.

---

## 9. Complete DI Wiring (Step 8)

### `src/Farmer.Host/Program.cs` (REPLACED)

**Registrations in this exact order:**

```
Configuration:
  Configure<FarmerSettings> from "Farmer" section
  Read TelemetrySettings from "Farmer:Telemetry"

Infrastructure (singletons):
  ISshService          → SshService
  IMappedDriveReader   → MappedDriveReader
  IRunStore            → FileRunStore
  IVmManager           → VmManager           (was missing pre-Phase 5)

Stages (singletons + explicit ordered IEnumerable factory):
  CreateRunStage, LoadPromptsStage, ReserveVmStage,
  DeliverStage, DispatchStage, CollectStage, ReviewStage

Middleware (singletons + explicit ordered IEnumerable factory):
  TelemetryMiddleware  (outermost - wraps activity span)
  LoggingMiddleware    (logs within span context)
  EventingMiddleware   (writes events.jsonl + state.json)
  CostTrackingMiddleware
  HeartbeatMiddleware  (innermost - writes to VM via SSH)

Workflow:
  RunWorkflow (singleton)

Background:
  RunDirectoryFactory (singleton)
  InboxWatcher (hosted)

OpenTelemetry:
  Tracing  → AddSource("Farmer") + AspNetCore + conditional OTLP/Console exporters
  Metrics  → AddMeter("Farmer") + conditional OTLP/Console exporters
  Resource → service.name from TelemetrySettings.ServiceName, version 0.1.0

Endpoints:
  GET  /health
  GET  /                       (service info)
  GET  /runs/{runId}           (returns IRunStore.GetRunStateAsync)
  POST /trigger                (manual inbox-equivalent for HTTP path)

URL: http://localhost:5100  (registered in portmap as farmer/api)
```

**Stage and middleware ordering uses an explicit factory lambda** (`sp => new IWorkflowStage[] { ... }`) rather than relying on DI registration order — this is robust and the order is visible in one place.

---

## 10. Tests (Step 10)

**Existing 79 tests continue to pass — none were broken.** All renames (status→state) propagated cleanly through 4 test files.

### NEW: `src/Farmer.Tests/Workflow/DiCompositionTests.cs`
- 3 tests:
  - `AllServicesResolve` — DI container builds, all 5 infrastructure services resolve.
  - `StagesResolveInExactOrder` — `IEnumerable<IWorkflowStage>` returns 7 stages in pipeline order.
  - `MiddlewareResolvesInExpectedOrder` — `IEnumerable<IWorkflowMiddleware>` returns 5 middleware, Telemetry first (outermost).

### NEW: `src/Farmer.Tests/Workflow/RunFromDirectoryTests.cs`
- 5 tests using a `LambdaStage` test double (no real VMs):
  - `ProducesResultJson` — happy path, `result.json` exists and deserializes.
  - `EventingMiddlewareWritesEventsAndState` — 3 stages produce 6 event lines (started + completed each), state.json exists.
  - `FailedStageStillProducesResultAndEvents` — Bad stage fails, `result.json` written, last event is `stage.failed`.
  - `LifecycleConsistency_EventsRecordAllTransitions` — events for both stages in correct order, last is `stage.completed`.
  - `DeterministicOutputs_SameInputSameStructure` — same input twice → same event sequence (ignoring timestamps).

### NEW: `src/Farmer.Tests/Telemetry/TelemetrySmokeTests.cs`
- 3 tests using `ActivityListener`:
  - `TelemetryMiddleware_EmitsStageActivities` — verifies child stage spans with `farmer.run_id` tag.
  - `ExecuteFromDirectory_EmitsRootRunActivity` — verifies root `workflow.run` span filtered by run_id.
  - `Metrics_AreCreated` — exercises every metric instrument without exception.

### `src/Farmer.Tests/Farmer.Tests.csproj` (MODIFIED)
- Added `Microsoft.Extensions.DependencyInjection` 8.0.1
- Added `Microsoft.Extensions.Hosting` 8.0.1

**Test count: 79 → 91 (12 new tests, 0 broken).**

---

## 11. Demo Assets

### `D:\work\planning-runtime\` (created on disk)
```
data/sample-plans/
  api-endpoint/         (copied from repo)
  react-grid-component/ (copied from repo)
inbox/
runs/
outbox/
qa/
```

### `scripts/demo/sample-request.json` (NEW)
Minimal inbox trigger:
```json
{ "work_request_name": "react-grid-component", "source": "inbox" }
```

### `scripts/demo/README.md` (NEW)
Full local runbook: start Aspire Dashboard, start Farmer.Host, drop inbox file, verify run folder, inspect dashboard. Documents the three registered ports.

### Portmap registrations (existing system at `D:\work\start\portmap`)
- `farmer/api` = 5100
- `farmer/aspire-dashboard` = 18888
- `farmer/aspire-otlp` = 18889

---

## 12. Real-World Verification (what we actually saw)

We ran a real inbox-triggered workflow against `react-grid-component`. Honest scorecard: **57% of stages green, 2 latent bugs surfaced, 1 config blocker.** Details below — celebrate nothing until §13 is empty.

1. Aspire Dashboard running in Docker (`mcr.microsoft.com/dotnet/aspire-dashboard:9.0`).
2. `dotnet run` started Farmer.Host on port 5100.
3. `InboxWatcher` logged: `InboxWatcher started, watching D:\work\planning-runtime\inbox`.
4. Copied `sample-request.json` → inbox.
5. Watcher picked it up: `Processing inbox file: test-run.json`.
6. `RunDirectoryFactory` created `runs/run-20260409-063402-aa4026/` (and a second run `run-20260409-073450-14fe44/` with the same failure shape).
7. **Pipeline ran 3 of 7 stages cleanly, then died at stage 4:**
   - ✅ `CreateRun` — generated IDs, persisted RunRequest
   - ✅ `LoadPrompts` — loaded 3 markdown files
   - ✅ `ReserveVm` — reserved `claudefarm1`
   - ❌ `Deliver` — **threw `SshPassPhraseNullOrEmptyException`** (config issue, not code)
8. Workflow ended with `success: false`, `final_phase: "Failed"`.
9. Files were produced (`request.json`, `state.json`, `events.jsonl`, `result.json`, `task-packet.json`, `logs/`, `artifacts/`) — but two of them are wrong on failure cases. See §13.
10. **OTel traces and console exporter both fired** for every stage span and the root run span. The telemetry observer is the only one that didn't lie.

---

## 13. Known Bugs & Open Issues (the things to fix before celebrating)

**Found during retro on 2026-04-09 by inspecting the actual run dirs.** These are real, not speculative — every one has receipts in `runs/run-20260409-073450-14fe44/`.

### Bug 1 — `state.json` and `result.json` disagree on failure (ARCH)
- **Symptom:** `state.json` reports `phase: "Delivering"`, `result.json` reports `final_phase: "Failed"`. Both files describe the same run.
- **Root cause:** `EventingMiddleware.cs:47-53` writes the state snapshot in its catch block *before* anyone advances `state.Phase` to `Failed`. Phase transition happens later in `RunWorkflow.cs:124`, after the exception has bubbled up past the middleware. Result: the snapshot freezes the in-flight phase forever.
- **Why this matters:** The architectural premise of Phase 5 is "filesystem + telemetry are parallel observers of the same transitions, they can never disagree." This bug refutes that premise on the very first failure case.
- **Fix:** In `EventingMiddleware`'s catch block, set `state.Phase = Failed` (or call the equivalent) *before* `WriteStateSnapshotAsync`. Add a regression test that asserts post-failure agreement across all three files.

### Bug 2 — `events.jsonl` is missing the `stage.failed` closing event for `Deliver` (ARCH)
- **Symptom:** `events.jsonl` ends with `{"stage":"Deliver","event":"stage.started"}` and no closure. `result.json` knows the SSH error. The "durable source of truth for stage transitions" is incomplete on the only transition that mattered.
- **Root cause:** Unconfirmed. `EventingMiddleware`'s catch block *should* append a `stage.failed` event before re-throwing — but the line is absent from the file. Either there's a flush/lifecycle bug, the exception escapes through a path that bypasses the middleware (e.g., DI resolution), or something is racing the file write.
- **Fix:** Reproduce with a deliberately-throwing stage in a test, confirm whether the catch path is actually hit, then patch. Add the same regression test as Bug 1.

### Smell 1 — `CostTrackingMiddleware.Reset()` is a singleton in denial
- `Reset()` exists because the middleware is registered as singleton and accumulates per-run state. The comment in `CostTrackingMiddleware.cs:39-42` literally says "not concurrency-safe."
- **Fix:** Make it `Scoped` in `Program.cs`, create a per-run scope in `RunWorkflow.ExecuteFromDirectoryAsync`, delete `Reset()`, delete the comment, delete the corresponding note in memory.

### Smell 2 — Middleware order anti-drift rule has no test
- `TelemetryMiddleware` is supposed to be outermost; `EventingMiddleware` and `TelemetryMiddleware` must use the same stage names + run_id. This is enforced by a comment in §5 of this doc, not by code.
- **Fix:** Add an integration test that asserts `_middleware.Select(m => m.GetType()).ToArray()` matches a hard-coded expected ordering. ~8 lines.

### Smell 3 — SSH key validation is a runtime exception, not a startup check
- The `SshPassPhraseNullOrEmptyException` killed stage 4 of a real run. It should have killed app startup with a clear banner.
- **Fix:** On host boot, attempt to load each VM's configured key. Fail fast with a descriptive error if any key is encrypted-but-unconfigured.

### Smell 4 — `scripts/demo/` is untracked
- The only proof Phase 5 works end-to-end is not in version control. `git clean -fd` would erase the demo.
- **Fix:** `git add scripts/demo/` as part of the Phase 5 commit.

### Smell 5 — 34 uncommitted files in working tree
- All Phase 5 work sits unstaged. No bisect possible. No partial rollback possible.
- **Fix:** Split into 5-6 atomic commits (telemetry primitives → externalized config → run dir layout → eventing → directory entry point → inbox watcher + demo), each one buildable and test-green on its own.

---

## 14. Next Session Pickup (do these in order)

1. **Fix Bug 1** (state/result drift) + add regression test. ~30 min.
2. **Reproduce and fix Bug 2** (missing failure event) + extend the same regression test. ~30 min.
3. **Convert CostTrackingMiddleware to scoped**, delete Reset, delete comments, delete memory note. ~10 min.
4. **Add SSH key validation as startup self-check.** Fail to start, not fail at stage 4. ~20 min.
5. **Add middleware-order test.** ~5 min.
6. **Split working tree into atomic commits**, including `scripts/demo/`. ~20 min.
7. **Re-run the inbox demo** with a working SSH key. Expect: 7/7 stages green, all three durable files agree, OTel traces show the full run.
8. **Only then** is Phase 5 done. Move on to Phase 6.

---

## What We Did NOT Build (intentional, deferred)

- **No `Reset()` plumbing for HeartbeatMiddleware** — it's stateless, doesn't need it.
- **No real VM-side `worker.sh` or end-to-end Claude CLI execution** — that's Phase 6.
- **No QA inspection agent** — Phase 7.
- **No concurrency for InboxWatcher** — sequential is fine for now, documented in code.
- **No retry policy** — single attempt, fail = fail.
- **No `outbox/` or `qa/` writers** — the directories exist; nothing writes to them yet.
- **No structured `logs/vm.log` writer** — VM logs go to console for now.
