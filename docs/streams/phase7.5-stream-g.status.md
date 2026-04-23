# Phase 7.5 — Stream G status

## [RESEARCH-DONE]

Scope verified against `docs/phase7.5-loop-integrity.md` and the existing codebase.
Findings:

- `src/Farmer.Core/Models/RunStatus.cs` holds the `RunPhase` enum (plan said
  `RunPhase.cs`; enum is co-located with `RunStatus`). `Archiving` slots cleanly
  between `Collecting` and `Reviewing` — pure addition, no existing switch
  statements in `src/` depend on a specific count or ordinal of enum values.
- `WorkflowPipelineFactory` resolves stages by type from DI, so `ArchiveStage`
  just needs one `AddSingleton<ArchiveStage>()` in `Program.cs` and one slot
  in the factory's stage array.
- `IMappedDriveReader.ReadFileAsync` is backed by `SshWorkerFileReader` (Phase 7
  Stream D) — `cat` under the hood, so v1 is text-only as the plan called out.
  The reader has no stat primitive; Stream G is forbidden from extending it.
  Trade-off chosen: read-then-measure (`Encoding.UTF8.GetByteCount`) and skip
  oversize. At `MaxFileBytes=8192` × `MaxFileBytes=10` the over-fetch cost is
  trivial.
- `CollectStage` parses the manifest but does NOT persist it to `RunFlowState`.
  The briefing said "Collect has already loaded + parsed it"; in practice
  ArchiveStage re-reads `output/manifest.json` via `IMappedDriveReader`, same
  pattern `RetrospectiveStage` uses for `manifest.json` / `summary.json`.
  Coupling via a shared state field would belong to Stream F's territory.
- `RetrospectiveSettings` is already registered via `AddFarmerAgents(...)` in
  `Program.cs`, so `IOptions<RetrospectiveSettings>` resolves for `ArchiveStage`
  with no extra wiring.
- `state.RunDirectory` is the correct target root — set by
  `RunWorkflow.ExecuteFromDirectoryAsync` and already used by
  `EventingMiddleware` + `RetrospectiveStage`.
- `DiCompositionTests.Factory_BuildsWorkflowWithStagesInExactOrder` asserts the
  exact stage count + names. It's a "traffic-light" test for the factory —
  insertion of Archive requires the assertion to flip to 8 stages. Matching
  update keeps the test load-bearing.

## [DESIGN-READY]

Concrete changes:

**New files:**
- `src/Farmer.Core/Workflow/Stages/ArchiveStage.cs` — implements
  `IWorkflowStage`. Name=`"Archive"`, Phase=`RunPhase.Archiving`. Reads
  manifest + per-file content via `IMappedDriveReader`, writes to
  `{RunDirectory}/artifacts/{relative path}` + `{RunDirectory}/artifacts-index.json`.
  Bounded by `RetrospectiveSettings.MaxChangedFiles` + `MaxFileBytes`.
  Per-file read/oversize/unsafe-path errors recorded in the index; stage
  only hard-fails if the VM has no manifest AND the index disk-write
  throws (it doesn't in practice).
- `src/Farmer.Tests/Workflow/ArchiveStageTests.cs` — 11 tests covering
  happy path, cap respect, skip-on-oversize, skip-on-read-error, empty
  manifest, missing manifest, no-VM, no-run-dir skip, path-traversal
  rejection, idempotent overwrite, and the Phase/Name contract.

**Edits (all surgical):**
- `src/Farmer.Core/Models/RunStatus.cs` — add `Archiving` enum value.
- `src/Farmer.Core/Workflow/WorkflowPipelineFactory.cs` — insert
  `GetRequiredService<ArchiveStage>()` between Collect and Retrospective.
- `src/Farmer.Host/Program.cs` — `AddSingleton<ArchiveStage>()` next to the
  other stages.
- `src/Farmer.Tests/Workflow/DiCompositionTests.cs` — register
  `ArchiveStage` in the test's service collection and extend the
  exact-order assertion from 7 → 8 stages.

**Index schema** (`artifacts-index.json` at run-dir root):
```json
{
  "run_id": "...",
  "manifest_files_count": 12,
  "max_changed_files": 10,
  "max_file_bytes": 8192,
  "generated_at": "2026-04-23T...",
  "entries": [
    { "path": "src/App.tsx", "status": "archived", "bytes": 612 },
    { "path": "vendor/huge.json", "status": "skipped", "reason": "too-big", "bytes": 524288 },
    { "path": "missing.txt", "status": "skipped", "reason": "read-error", "detail": "..." },
    { "path": "../escape", "status": "skipped", "reason": "unsafe-path" }
  ]
}
```

Per ADR-007 "data is the product" — skipped-with-reason is first-class output,
not a silent drop.

## [COMPLETE] <sha-after-commit>

Verification gates:

- `dotnet build src/Farmer.sln`: clean, **0 warnings, 0 errors**.
- `dotnet test src/Farmer.sln`: **168 green** (163 unit + 5 integration, 0 failed,
  0 skipped). Baseline was 157 (152 + 5); net +11 from ArchiveStageTests.
- ArchiveStageTests alone: **11/11 green**.
- DiCompositionTests (stage-order assertion updated): **5/5 green**.

Files touched (6):

- NEW `src/Farmer.Core/Workflow/Stages/ArchiveStage.cs`
- NEW `src/Farmer.Tests/Workflow/ArchiveStageTests.cs`
- NEW `docs/streams/phase7.5-stream-g.status.md`
- `src/Farmer.Core/Models/RunStatus.cs` — +1 enum value
- `src/Farmer.Core/Workflow/WorkflowPipelineFactory.cs` — +1 stage slot
- `src/Farmer.Host/Program.cs` — +1 DI registration
- `src/Farmer.Tests/Workflow/DiCompositionTests.cs` — register ArchiveStage,
  update exact-order assertion 7 → 8 stages

Surprises / notes for orchestrator:

- **Briefing said `RunPhase.cs` exists; actually it's inside `RunStatus.cs`.**
  Editing there instead. Pure enum addition, no call-site churn.
- **Briefing said "Collect has already loaded + parsed it" for the manifest,
  implying it's on `RunFlowState`.** CollectStage parses but does not persist.
  ArchiveStage re-reads via `IMappedDriveReader` — same idiom as
  RetrospectiveStage. Avoids editing CollectStage (Stream F territory).
- **Reader has no stat primitive; `SshWorkerFileReader` is off-limits.** Used
  read-then-measure for the size guard. At v1 caps (`MaxFileBytes=8192`) the
  over-fetch is negligible. If this becomes a cost problem, follow-up is
  adding a stat method to `IMappedDriveReader` (cross-stream work, not
  Stream G).
- **Extra defensive check added: path-traversal rejection** on manifest
  entries. A compromised worker emitting `"../../etc/passwd"` in
  `files_changed` would otherwise write outside the artifacts root.
  Covered by `RejectsPathTraversalInManifest` test.
- **DiCompositionTests update is the one cross-test "traffic-light" edit.**
  It's coupled to the factory's output shape, which Stream G changes; keeping
  it in sync is mechanical follow-on, not a territory violation.
- **Stream E depends on this stage's output at runtime** (artifacts/ +
  artifacts-index.json). Safe to land in any order per the plan; if E lands
  first, it degrades gracefully (artifacts/ empty → today's behavior).

Commit not pushed. Orchestrator owns the merge.
