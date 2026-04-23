# Phase Demo — Stream H status

## [RESEARCH-DONE]

Scope verified against `docs/phase-demo-plan.md` and the existing codebase.
Findings:

- **No `TriggerRequest.cs` exists.** The `/trigger` schema is spread across
  two types: `InboxTrigger` (in `src/Farmer.Host/Services/RunDirectoryFactory.cs`,
  the minimal wire format clients POST) and `RunRequest` (in
  `src/Farmer.Core/Models/RunRequest.cs`, the full request persisted to
  `request.json` in the run dir). `RunDirectoryFactory.CreateFromInboxFileAsync`
  is the bridge: it deserializes `InboxTrigger` from the POST body, mints
  ids/timestamps, and writes a full `RunRequest` to `request.json`.
  `RunWorkflow.ExecuteFromDirectoryAsync` then reads it back and attaches it
  to `RunFlowState.RunRequest`.
- **`LoadPromptsStage` flow**: reads `state.WorkRequestName` →
  `SamplePlansPath/{WorkRequestName}/*.md` from disk. The feedback-injection
  pattern (for retries) already reads `state.RunRequest.Feedback` and prepends
  a synthetic `0-feedback.md` prompt. `prompts_inline` fits the same pattern
  — read from `state.RunRequest.PromptsInline`, build `PromptFile`s directly,
  skip the disk read.
- **CORS**: `Program.cs` has no `AddCors` / `UseCors` today. ASP.NET Core
  pattern: register a policy in services, call `app.UseCors(policyName)`
  before `app.Map*` endpoints.
- **Backward compat**: when `prompts_inline` is absent, current disk-read
  behavior must be untouched. `work_request_name` stays as the run's display
  name (used in `TaskPacket.WorkRequestName`, retrospective prompt, etc.);
  only the prompt *source* changes when `prompts_inline` wins.
- **Baseline tests**: 186 unit + 5 integration = 191 green pre-change.

## [DESIGN-READY]

Concrete changes:

**Edits:**
- `src/Farmer.Core/Models/RunRequest.cs` — add optional
  `List<InlinePrompt>? PromptsInline` with `[JsonPropertyName("prompts_inline")]`.
- NEW `src/Farmer.Core/Models/InlinePrompt.cs` — small record with
  `Filename` + `Content` snake-case JSON.
- `src/Farmer.Host/Services/RunDirectoryFactory.cs` — add
  `PromptsInline` on `InboxTrigger`, copy into the `RunRequest` written to
  `request.json`. Also stop failing on empty `work_request_name` when
  `prompts_inline` is populated — use a fallback display name.
- `src/Farmer.Core/Workflow/Stages/LoadPromptsStage.cs` — check
  `state.RunRequest?.PromptsInline` first; if non-empty, build prompts from
  it (assigning `Order` by position, 1-indexed; filename from the payload).
  Otherwise fall through to existing disk-scan path. Feedback injection
  applies either way.
- `src/Farmer.Host/Program.cs` — add a permissive dev CORS policy
  (`AllowAnyOrigin` / `AllowAnyMethod` / `AllowAnyHeader`), call
  `app.UseCors(...)` before endpoint mapping.

**New files:**
- `infra/start-tunnel.ps1` — wraps `cloudflared tunnel --url http://localhost:5100`.
  Pre-flight: checks `Get-Command cloudflared` and prints the winget install
  hint on miss. Launches cloudflared, streams its stdout/stderr, parses
  lines for the `*.trycloudflare.com` URL, writes `TUNNEL_URL: <url>` to
  stdout once, persists `infra/.tunnel-url.txt` for programmatic pickup,
  then tails the process until Ctrl+C.
- `docs/demo-tunnel.md` — one-pager.

**New tests:**
- `src/Farmer.Tests/Models/ContractSerializationTests.cs` — add
  `RunRequest_PromptsInline_RoundTrips` + `InlinePrompt_RoundTrips` +
  `InboxTrigger`-style shape test (covers the wire contract).
- `src/Farmer.Tests/Workflow/LoadPromptsStage_InlineTests.cs` — new file,
  tests for:
  1. Happy path: populated `PromptsInline` bypasses disk, assigns Order by
     position.
  2. Inline wins when both `work_request_name` and `PromptsInline` are
     provided — disk never touched even if the dir exists.
  3. Empty list behaves as absent → falls through to disk.
  4. Feedback injection still works alongside inline prompts.
  5. Stored TaskPacket carries the inline filenames.
- `src/Farmer.Tests/Host/RunDirectoryFactoryInlineTests.cs` — new file,
  verifies `prompts_inline` is persisted to `request.json` and survives the
  inbox→request transcription.

## [COMPLETE] <sha-after-commit>

Verification gates:

- `dotnet build src/Farmer.sln`: clean, **0 warnings, 0 errors**.
- `dotnet test src/Farmer.sln`: **206 green** (198 unit + 8 integration,
  0 failed, 0 skipped). Baseline was 191 (186 + 5); net +15, all additive
  (no existing tests edited).
- PowerShell parse of `infra/start-tunnel.ps1`: OK.

Files touched (10):

- NEW `src/Farmer.Core/Models/InlinePrompt.cs`
- `src/Farmer.Core/Models/RunRequest.cs` — +`PromptsInline`
- `src/Farmer.Core/Workflow/Stages/LoadPromptsStage.cs` — inline fast path
- `src/Farmer.Host/Services/RunDirectoryFactory.cs` — carry `prompts_inline`
  through the inbox→request transcription + fallback `work_request_name`
- `src/Farmer.Host/Program.cs` — +CORS dev policy (`AllowAnyOrigin` /
  `AllowAnyMethod` / `AllowAnyHeader`), wired before endpoint mapping
- `src/Farmer.Tests/Models/ContractSerializationTests.cs` — +5 tests
- NEW `src/Farmer.Tests/Workflow/LoadPromptsStage_InlineTests.cs` — 8 tests
- NEW `src/Farmer.Tests.Integration/RunDirectoryFactory_InlinePromptsTests.cs` — 3 tests
- NEW `infra/start-tunnel.ps1`
- NEW `docs/demo-tunnel.md`
- NEW `docs/streams/phase-demo-stream-h.status.md`

Surprises / notes for orchestrator:

- **No `TriggerRequest.cs`.** Plan said "`src/Farmer.Core/Contracts/TriggerRequest.cs`
  (or equivalent existing trigger schema)" — the equivalent is split between
  `RunRequest` (Farmer.Core/Models) and `InboxTrigger` (Farmer.Host/Services,
  inside `RunDirectoryFactory.cs`). Added `PromptsInline` to both; the wire
  contract is on `InboxTrigger`, the persisted shape is on `RunRequest`,
  and the flow funnels through `RunDirectoryFactory.CreateFromInboxFileAsync`.
- **`/trigger` handler didn't need edits.** The briefing said "prompts_inline
  wiring in the /trigger handler" — in practice the handler does the wiring
  by default. Body -> tempfile -> `RetryDriver.RunAsync` -> `RunDirectoryFactory`
  -> `request.json` -> `RunFlowState.RunRequest` -> `LoadPromptsStage`. The
  new `PromptsInline` property rides through unchanged. CORS is the only
  Program.cs edit.
- **Fallback `work_request_name`.** When `prompts_inline` is populated but
  `work_request_name` is absent, `RunDirectoryFactory` now synthesizes
  `"inline-request"` instead of carrying an empty string. Prevents a hostile
  landmine where a phone client sends only `prompts_inline` and downstream
  stages (retrospective prompt builder, worker branch name) try to interpolate
  an empty name. Covered by a test.
- **Missing filename fallback.** `LoadPromptsStage` synthesizes
  `{i+1}-inline.md` when an `InlinePrompt` arrives with an empty filename.
  Keeps worker-side log lines meaningful if a sloppy client omits it.
  Covered by a test.
- **Integration test location.** `RunDirectoryFactory` lives in `Farmer.Host`
  which is only referenced by `Farmer.Tests.Integration`, so the factory
  test went there. No NATS fixture needed (the factory is pure disk).
- **Script behavior**: `start-tunnel.ps1` parses stderr too — cloudflared
  prints its URL banner there on Windows. Regex is permissive so future
  cosmetic changes to cloudflared's banner don't break pickup. Orphan-process
  caveat documented in the troubleshooting section of `docs/demo-tunnel.md`.

Commit not pushed. Orchestrator owns the merge.
