# Phase Demo — Stream J status

Scope: reveal UI + feedback loop. Backend `/runs`, `/runs/{id}`, `/runs/{id}/file/{**path}`
endpoints + static-files handler for `/demo/`; new `demo/reveal.{html,css,js}` single-page
viewer; `docs/demo-reveal.md` operator one-pager.

## [RESEARCH-DONE]

Ground-truthing against the worktree + `planning-runtime/runs/`:

- **Run dir layout is stable** (`src/Farmer.Core/Layout/RunDirectoryLayout.cs`). `result.json` is
  the canonical status file; `review.json` exists only after `RetrospectiveStage` ran; `qa-retro.md`
  and `directive-suggestions.md` are the retro agent's persisted markdown outputs; `artifacts-index.json`
  is the Phase 7.5 Stream G output. Runs from before Stream G show `(no artifacts)` gracefully.
- **Existing `/runs/{runId}` endpoint** (Program.cs line 112 pre-change) was a thin passthrough to
  `IRunStore.GetRunStateAsync` returning `state.json`. It collided with Stream J's planned
  `/runs/{id}` route and had no callers in the codebase outside `dev-run.ps1`-style manual curls.
  Replaced; the raw `state.json` content is still reachable via `/runs/{id}/file/state.json`.
- **`IRunStore` is write-oriented** (SaveRunState, SaveReviewVerdict, etc.). For the reveal read
  side I built a standalone `RunsBrowserService` that scans the filesystem directly with lenient
  JSON deserialization. Rationale: the UI must keep working against runs whose schema pre-dates
  the current `RunStatus`/`WorkflowResult` shape — a strict deserializer throws, breaking the sidebar.
- **Events don't currently carry `trace_id`.** Searched `events.jsonl` across tonight's runs; no
  `trace_id` or `traceId` field. The Trace tab handles this with a notice + a link to Jaeger root;
  `TryReadTraceId` is forward-compatible so future pipeline changes that add `trace_id` to stage
  events light the iframe up with no UI code change.
- **Stream H collision map:** H edits CORS setup at the top of Program.cs and the `/trigger`
  handler body. My changes add one DI registration (`RunsBrowserService`), remove the legacy
  `/runs/{runId}` handler (a single short block between `/` and `/trigger`), and append a clearly
  commented block after `/trigger`. No overlap expected; legacy-endpoint removal is documented
  with a comment pointing callers at `/runs/{id}/file/state.json`.
- **Static files handler: `Microsoft.AspNetCore.StaticFiles` is already available** via the
  `Microsoft.NET.Sdk.Web` implicit `FrameworkReference`. No csproj change needed.
- **Tests:** Farmer.Tests (unit) has no reference to Farmer.Host; Farmer.Tests.Integration does.
  Service-layer tests landed in the Integration project alongside the existing `RetryDriverTests`
  to avoid adding a new `ProjectReference` to the unit test csproj.

## [DESIGN-READY]

Concrete deliverables:

**Backend (`src/Farmer.Host/`):**
- `Services/RunsBrowserService.cs` — read-side service. Exposes `ListRecent(take=20)`,
  `TryLoadSummary(id)`, `TryLoadDetail(id)`, `ListFiles(id)`, `ListArtifacts(id)`, and
  `TryResolveRunFile(id, relPath, out full)`. Tolerant JSON reader (snake_case + camelCase,
  skip unknown props, swallow parse errors). Security: run-id sanitization, rooted-path rejection,
  `..` rejection, `GetFullPath`-then-`StartsWith` enclosure check on the resolved candidate.
- `Program.cs` — one `AddSingleton<RunsBrowserService>()`, three `MapGet`s inside a
  clearly-commented Stream J block, and a `UseStaticFiles` for `/demo/*` with a parent-dir
  walk so `dotnet run` + `dotnet publish` both resolve the repo's `demo/` directory.

**Frontend (`demo/`):**
- `reveal.html` — semantic markup: topbar (auto-refresh toggle), sidebar (run list), main pane
  with summary header + 4 tabs (Trace / Artifacts / Retrospective / Directives), toast region.
- `reveal.css` — dark palette, grid layout (sidebar + main), responsive retro two-up that
  collapses under 1100px.
- `reveal.js` — vanilla JS, ~350 lines. API base auto-detects `file://` vs HTTP origin, both
  overridable via `?api=` + `?jaeger=` query strings. 5s poll on `/runs` (toggle in UI).
  Directive cards split on `## 1.`-style headings with a single-card fallback.

**Docs (`docs/`):**
- `demo-reveal.md` — how to open it (both static-serve and file:// paths), table of what each
  tab shows, directive re-apply flow including the JSON payload shape, demo scripting tips.

**Tests:**
- `src/Farmer.Tests.Integration/RunsBrowserServiceTests.cs` — 14 tests covering:
  empty-root, skip-runs-missing-result, mtime-sort ordering, verdict population from review.json,
  full-detail retro+directives read, unknown-id-null, artifacts index preference over FS walk,
  artifacts FS-walk fallback, file resolution for run-root + nested artifact, path-traversal
  rejection with both slash directions + post-valid-segment traversal, rooted-path rejection,
  malicious run-id rejection, directory-listing rejection.

## [COMPLETE] ddf6078

Verification:

- `dotnet build src/Farmer.sln` — clean, **0 warnings, 0 errors**.
- `dotnet test src/Farmer.sln` — **205 green** (186 unit + 19 integration, 0 failed, 0 skipped).
  Baseline was 191 (186 + 5); net +14 from `RunsBrowserServiceTests`. Unit count unchanged.
- Service tests alone: **14/14 green** (`dotnet test --filter FullyQualifiedName~RunsBrowserService`).

Files touched (5 new + 1 modified):

- NEW `src/Farmer.Host/Services/RunsBrowserService.cs` — read-side service, ~300 lines.
- NEW `src/Farmer.Tests.Integration/RunsBrowserServiceTests.cs` — 14 tests.
- NEW `demo/reveal.html` — single-page viewer.
- NEW `demo/reveal.css` — dark palette + grid layout.
- NEW `demo/reveal.js` — vanilla-JS controller, fetch/render/poll.
- NEW `docs/demo-reveal.md` — one-pager.
- NEW `docs/streams/phase-demo-stream-j.status.md` — this file.
- MOD `src/Farmer.Host/Program.cs` — +1 DI registration, −1 legacy endpoint, +1 Stream J block
  (3 endpoints + static-files handler + FindDemoDirectory helper).

Surprises / notes for orchestrator:

- **Legacy `/runs/{runId}` removed.** It returned `state.json`, no callers in-tree, collided with
  `/runs/{id}`. Replaced with a pointer comment. `state.json` is still accessible via
  `/runs/{id}/file/state.json`. Call this out in the merge PR body so anyone with a curl
  bookmark knows where to look.
- **No `trace_id` in events.jsonl today.** Trace tab will show the fallback notice on tonight's
  runs. Not a bug — pipeline doesn't emit that field. The iframe will light up automatically
  whenever it starts to.
- **`prompts_inline` + `parent_run_id` on the re-run POST** are designed against Stream H's stated
  schema. If H's merge lags, the re-run still goes through but falls back to the disk lookup for
  `work_request_name` — directive content would be ignored in that path. Documented in
  `docs/demo-reveal.md` under "Caveats".
- **Static files handler walks up from `AppContext.BaseDirectory`** to find the repo's `demo/`.
  This works for `dotnet run` from `src/Farmer.Host/` and `dotnet publish` output layouts I tested
  against. If ops runs the Host from a standalone publish dir without the repo tree, `/demo/*`
  will 404 cleanly — `reveal.html` still works via `file:///` against the same API.
- **Tests deliberately live in `Farmer.Tests.Integration`**, not `Farmer.Tests`, to reuse the
  existing `ProjectReference` to Farmer.Host. No build infra was altered.
- **Path-traversal defense is belt-and-suspenders:** explicit `..` + rooted-path rejection before
  `Path.GetFullPath`, plus a `StartsWith(runDirFull + separator)` enclosure check after. The
  redundant layer catches `a/../../b`-style inputs that sneak through the first string scan.

Commit not pushed. Orchestrator owns the merge with Stream H.
