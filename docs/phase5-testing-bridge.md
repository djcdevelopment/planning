# The Testing Bridge — What We Need to Get End-to-End

This document captures the *gap* between what we have today and a meaningful end-to-end test, why VMs are involved, and the minimum to move forward.

---

## Where We Are

**91 tests passing.** They cover:

| Category | Count | What they prove |
|---|---|---|
| Contract serialization | ~19 | DTOs round-trip JSON correctly |
| Workflow orchestrator (in-memory) | 9 | Stage ordering, state transitions, failure handling, cancellation, middleware chain |
| Individual stages (in-memory) | ~25 | Each of the 7 stages works in isolation with mocked dependencies |
| Middleware (in-memory) | ~15 | Logging, cost tracking, heartbeat, eventing all work in isolation |
| VmManager | ~8 | Pool reservation, state transitions, thread safety |
| RunDirectoryLayout | 10 | Path conventions for VM, host, run dir |
| **DI composition (NEW)** | 3 | All services resolve, stages and middleware in correct order |
| **RunFromDirectory (NEW)** | 5 | `ExecuteFromDirectoryAsync` writes events.jsonl, state.json, result.json correctly with **spy stages** |
| **Telemetry smoke (NEW)** | 3 | `ActivitySource` and `Meter` emit through middleware, root run span via directory entry |

**Plus a real run we observed manually:**
- Aspire Dashboard running, Farmer.Host running, inbox file processed.
- Pipeline ran 4 of 7 stages (`CreateRun → LoadPrompts → ReserveVm → Deliver`), failed at `Deliver` with `SshPassPhraseNullOrEmptyException`.
- All file artifacts produced correctly.
- All telemetry visible in console + Aspire.

## What's Missing

**A test that proves the real DI graph runs the real stages against the real `RunWorkflow.ExecuteFromDirectoryAsync`, end-to-end, producing the expected file artifacts.**

Every test we have either:
- Uses spy stages (proves the orchestrator/middleware/file-writing, not the real stages), or
- Uses real stages but with mocked `IRunStore` / no file I/O (proves stage logic, not the assembled system), or
- Was a manual observation (not repeatable in CI).

**There is no automated test that exercises the full pipeline.**

---

## Why VMs Are In The Picture

Three of the seven stages have an unavoidable dependency on a VM via SSH or SSHFS:

| Stage | Real dependency | What it does |
|---|---|---|
| `DeliverStage` | `ISshService.ExecuteAsync` (mkdir on VM) + `ScpUploadContentAsync` (upload prompts and task-packet) | Creates `~/projects/{plans,.comms,output}/` on the VM, uploads each prompt .md file, uploads `task-packet.json` |
| `DispatchStage` | `ISshService.ExecuteAsync` (run `bash worker.sh {run_id}`) | Triggers the VM-side worker script over SSH; blocks for up to 30 minutes |
| `CollectStage` | `IMappedDriveReader.WaitForFileAsync` + `ReadFileAsync` (read `output/manifest.json` from N:\) | Polls the SSHFS-mapped drive for the worker's output, deserializes the manifest |

Plus `HeartbeatMiddleware` (which wraps every stage) calls `ISshService.ScpUploadContentAsync` to write `.comms/progress.md` to the VM after every stage that has a reserved VM. It currently swallows failures and logs warnings — that's why our manual run got a warning at `ReserveVm` but kept going.

**The hard wall right now:** even if we wanted to run the real `DeliverStage`, the SSH key on this machine is encrypted with a passphrase, and `Renci.SshNet`'s `PrivateKeyFile` constructor we use in `SshService.cs:94` doesn't pass one in:

```csharp
return new SshClient(
    vm.SshHost, vm.SshUser,
    new PrivateKeyFile(Path.Combine(_userProfile, ".ssh", "id_rsa"))
);
```

So `Deliver` throws `SshPassPhraseNullOrEmptyException` before any VM work happens. **Even with a VM running and reachable, the current code would fail to authenticate.**

---

## Two Independent Problems

It helps to separate them because they have different solutions.

### Problem A: Testing the orchestration without needing VMs

**This is what we need for CI and fast iteration.**

The end-to-end test should prove:

1. The real `RunDirectoryFactory` parses an inbox file and creates the correct directory.
2. The real `RunWorkflow.ExecuteFromDirectoryAsync` reads `request.json` and hydrates state correctly.
3. The real `EventingMiddleware` writes the expected `events.jsonl` lines and `state.json` snapshots.
4. The real `TelemetryMiddleware` emits the expected activities and metrics.
5. `result.json` is written with the correct outcome.
6. The full middleware chain (Telemetry → Logging → Eventing → CostTracking → Heartbeat) works in the assembled order.
7. Eventing and telemetry agree on stage names and run_id (anti-drift invariant).

**It should NOT need to prove SSH works.** That's a separate test.

**The minimum bridge:** stub `ISshService` and `IMappedDriveReader` with in-process fakes that always succeed. The real stages call the fakes. Everything else is real. This is exactly what the (interrupted) `EndToEndInboxTests.cs` was about to do.

### Problem B: Proving the real SSH path actually works

**This is what we need before Phase 6 (worker.sh + real Claude CLI).**

This requires:
- A reachable SSH host (real VM, container, or even `localhost` with sshd).
- An unencrypted private key (or passphrase support added to `SshService`).
- A target directory structure on the host that matches what the stages expect.
- Optionally, a writable `~/projects/output/` so `CollectStage` can read a fake manifest written by the test.

This is a separate, slower, environment-specific test. It belongs in a different test class with `[Trait("category","integration")]` so CI can skip it by default.

---

## What We Need For The Real End-to-End (Problem A)

**Goal:** A single test that exercises the assembled system, no VMs, no SSH, no real Claude CLI.

### Required pieces

| Need | Status | Notes |
|---|---|---|
| Temp runtime directory with proper subdirs | ✅ already in test harness | `Path.GetTempPath()` + cleanup in `Dispose` |
| Sample plans on disk | ✅ trivial | Write `1-Setup.md`, `2-Build.md` into temp data dir |
| Inbox trigger file | ✅ trivial | Write minimal JSON |
| Real `RunDirectoryFactory` | ✅ exists | Used as-is |
| Real `FileRunStore` | ✅ exists | Pointed at temp runs dir via `IOptions<FarmerSettings>` |
| Real `VmManager` | ✅ exists | Configured with one fake VM in settings |
| Real `CreateRunStage`, `LoadPromptsStage`, `ReserveVmStage`, `ReviewStage` | ✅ exist | No SSH dependency |
| **Stub `ISshService`** | ❌ needs writing | Returns success for all SSH/SCP calls — DeliverStage and DispatchStage and HeartbeatMiddleware go through it |
| **Stub `IMappedDriveReader`** | ❌ needs writing | Returns a synthetic `manifest.json` for `CollectStage` to consume, OR a stub stage replaces it |
| Real `DeliverStage`, `DispatchStage`, `CollectStage` | ⚠️ depend on stubs | Need stubs above to be wired in DI |
| Real `EventingMiddleware`, `TelemetryMiddleware`, `LoggingMiddleware`, `CostTrackingMiddleware` | ✅ exist | Wired into the test's RunWorkflow |
| Real `HeartbeatMiddleware` | ⚠️ depends on stub `ISshService` | Will be exercised since at least one VM is reserved |
| Real `RunWorkflow.ExecuteFromDirectoryAsync` | ✅ exists | Used as-is |

### What the test asserts

**Pipeline behavior:**
- `result.Success == true`
- `result.FinalPhase == Complete`
- All 7 stages completed (`StagesCompleted.Count == 7`)

**File artifacts:**
- `request.json` exists with the correct `work_request_name`
- `task-packet.json` exists with 2 prompts (matching the sample plans on disk)
- `state.json` exists, `phase == "Complete"`, `stages_completed` has all 7
- `events.jsonl` has exactly 14 lines (7 stages × 2 events each: started + completed)
- `result.json` exists, `success == true`
- `logs/` and `artifacts/` directories exist

**Anti-drift invariant:**
- For every `stage.completed` event in `events.jsonl`, the same stage name appears in `state.stages_completed`
- All events share the same `run_id`, matching `request.run_id`
- The stage names in events match `["CreateRun", "LoadPrompts", "ReserveVm", "Deliver", "Dispatch", "Collect", "Review"]`

**Telemetry parity (optional in this test, can be a separate test):**
- An `ActivityListener` captures activities during the run
- Captured activities include exactly 1 root `workflow.run` and 7 child `workflow.stage.*`
- Each captured activity has the same `farmer.run_id` as the events file

### What blocks this test today

**Nothing major.** The fakes are ~30 lines of code each. The test was about to be written when you stopped me. The only design decision is:

- **Should we stub the SSH service via DI**, so the test builds the same DI graph as production but with `ISshService` swapped, OR
- **Should we instantiate `RunWorkflow` directly** in the test with explicit stage list, like `RunFromDirectoryTests.cs` already does?

**Recommendation: build the real DI graph with stubs.** This is the only way to prove DI composition + real stages + real middleware actually compose. The test is more verbose but proves more.

---

## What We Need For The Real SSH Path (Problem B — Phase 6 territory)

We do NOT need this to finish Phase 5. But here's the picture so it's clear what comes next.

### Option 1: Real Hyper-V VMs

What you currently have. Three VMs (`claudefarm1/2/3`), SSHFS-mapped to `N:\`, `O:\`, `P:\`.

**Blocker:** SSH key has a passphrase. Two ways to resolve:
- Generate an unencrypted key for the Farmer service account and authorize it on the VMs
- Add passphrase support to `SshService` (read from config, secrets, or env var)

**Once SSH works:** there's still a chicken-and-egg with `worker.sh` — `DispatchStage` runs `bash worker.sh {run_id}` but `worker.sh` doesn't exist on the VMs yet. That's Phase 6's first deliverable.

### Option 2: Local Linux container with SSH

Spin up a docker container running sshd. Generate a key pair just for it. Mount a volume so you can read what the "worker" wrote. This is the cleanest CI path because it's reproducible.

**Pros:** Reproducible, no hardware dependency, can run in CI later.
**Cons:** Need to write the docker setup; SSHFS mapping doesn't apply (would need a different read path or mount the volume on Windows directly).

### Option 3: Localhost OpenSSH server on Windows

Windows 10 ships with optional OpenSSH server. Enable it, set up keys, point the config at `localhost`. Closest to production behavior on this machine but uses Windows SSH (different from Ubuntu).

### Recommendation for the SSH bridge

For Phase 5: **Option A only** (the in-process stub). It's enough.

For Phase 6: **Option 2** (docker container). It's reproducible, isolates the SSH setup from your development machine, and gives you a clean place to test `worker.sh`. The Hyper-V VMs become your "production-like" environment for end-to-end demos.

---

## The Minimum To Get Moving

**To finish Phase 5 with confidence:**

1. **Write the in-process end-to-end test** described above. ~150 lines, needs only stub `ISshService` and `IMappedDriveReader`. This is what was almost-written when you stopped me.
2. **Add an explicit anti-drift test**: capture activities via `ActivityListener` AND read `events.jsonl`, assert the stage name lists match exactly.
3. **Decide what to do about the encrypted SSH key.** For Phase 5, the answer is: nothing. Document it as a known blocker for the manual demo path. For Phase 6, it has to be solved.

**That's it for Phase 5.** Aspire visibility is proven (we saw it). DI is wired. File artifacts work. The 91 unit tests cover every component. The integration test closes the loop.

**To unlock Phase 6:**

1. SSH bridge solved (Option 2 recommended).
2. `worker.sh` written and uploadable.
3. End-to-end test extended to actually run a real stage against the docker container.

---

## Specific Decisions I Need From You

1. **Stub vs real SSH for the integration test.** I'm assuming stubs (Problem A only). Confirm or override.
2. **Where the integration test lives.** I had it at `Farmer.Tests/Integration/EndToEndInboxTests.cs`. Same project as unit tests, or a separate `Farmer.IntegrationTests` project?
3. **Do you want me to wire the test to build the *real* DI graph** (`new ServiceCollection().Configure<FarmerSettings>(...).AddSingleton<ISshService, FakeSsh>()...`) or use **direct instantiation** like the existing `RunFromDirectoryTests`?
4. **Anti-drift test as part of the e2e test, or separate?** I'd vote part of the same test — easier to read, one fixture, one run.
5. **The encrypted SSH key.** Ignore for Phase 5? Or do you want to fix `SshService` to support passphrases now (small change, ~10 lines)?

Once you answer those, the test is ~30 minutes of writing.
