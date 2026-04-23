# Phase Demo v2 — Stream 3 status (Farmer.Host identity + tunnel auto-publish)

Scope: plumb `user_id` through `/trigger` → `request.json` → the run-browser
service, and auto-publish the ephemeral cloudflared URL to an Azure Blob from
`start-tunnel.ps1`. Territory covered by the Stream 3 section of
`docs/phase-demo-v2-rearchitect.md`.

## [RESEARCH-DONE]

Ground-truthing against the worktree at commit `4e30d61` (branched from `main`):

- **`RunRequest` already carries rich metadata** (retry policy, inline
  prompts, feedback, parent run id). Adding `UserId` as a nullable string
  follows the existing pattern — JSON `user_id`, persisted verbatim into
  `request.json`.
- **`InboxTrigger` in `RunDirectoryFactory.cs` is the wire-contract shim.**
  Every field surfaced to `/trigger` bodies goes through it, so `user_id`
  needed a mirrored property plus an extra line in the `RunRequest` copy
  block. No behaviour change for legacy triggers that omit the field.
- **`/trigger` handler reads the body as a single string and writes it to a
  temp file** before calling `RetryDriver.RunAsync`. No existing
  deserialization happens in `Program.cs` itself. The cleanest header/body
  merge sits between the `ReadToEndAsync` and the `WriteAllTextAsync` — a
  string-in / string-out mutation that only touches the body when the
  header actually adds new information. I extracted that mutation into a
  static helper (`Farmer.Host.Services.TriggerBodyEnricher`) so it could be
  unit-tested without a `WebApplicationFactory`.
- **`RunsBrowserService.TryLoadSummary` already reaches into `request.json`
  for `work_request_name`**; threading `user_id` alongside it is a
  one-line delta on the request-doc shape. The `RunSummary` record gained
  a `UserId` slot (record-field addition); `TryLoadDetail` returns the
  same summary so detail responses pick it up for free.
- **`start-tunnel.ps1` parses the tunnel URL inside an event handler
  closure.** The blob-upload logic goes in the same closure, right after
  the `.tunnel-url.txt` write, gated on `$env:FARMER_TUNNEL_BLOB_URL`. No
  new PowerShell modules required — `Invoke-RestMethod` with PUT +
  `x-ms-blob-type: BlockBlob` is enough.
- **az CLI is at `C:\Program Files\Microsoft SDKs\Azure\CLI2\wbin\az.cmd`**
  per the memory index; the provisioning script falls back to `az` on
  PATH so CI / fresh machines still work.
- **Baseline tests: 221 green** (199 unit + 22 integration), matching the
  count documented in the plan.

## [DESIGN-READY]

Concrete deliverables:

**Farmer.Core + Farmer.Host:**
- `src/Farmer.Core/Models/RunRequest.cs` — add `UserId` (nullable string,
  `user_id` on the wire).
- `src/Farmer.Host/Services/RunDirectoryFactory.cs` — mirror `user_id` on
  `InboxTrigger`, copy it into `RunRequest` at persist time.
- `src/Farmer.Host/Services/TriggerBodyEnricher.cs` (NEW) — static helper
  that splices `X-Farmer-User-Id` into the body's `user_id` field only
  when the body doesn't already carry one (body > header precedence).
- `src/Farmer.Host/Program.cs` — read the header, call the enricher, hand
  the mutated body to the existing temp-file path. No other behaviour
  change.
- `src/Farmer.Host/Services/RunsBrowserService.cs` — add `UserId` to
  `RunSummary`, read from `request.json`, surface on both
  `ListRecent()` and `TryLoadDetail()`.

**Tests:**
- `src/Farmer.Tests.Integration/TriggerBodyEnricherTests.cs` (NEW) — 8
  tests covering: header fills an empty body field, body wins when both
  set, empty/whitespace header is a no-op, blank body-side user_id is
  treated as missing and the header fills in, malformed JSON returned
  untouched, non-object JSON returned untouched, other fields preserved
  round-trip.
- `src/Farmer.Tests.Integration/RunDirectoryFactory_InlinePromptsTests.cs` —
  +1 test asserting `user_id` on the trigger body is persisted verbatim
  to `request.json`, +1 assertion on the existing legacy-shape test that
  `UserId` stays null when not supplied.
- `src/Farmer.Tests.Integration/RunsBrowserServiceTests.cs` — +2 tests
  covering `ListRecent` + `TryLoadDetail` surfacing `user_id` from
  `request.json`; helper extended with an optional `userId` parameter.

**Tunnel infra:**
- `infra/start-tunnel.ps1` — extend the URL-captured handler with an
  `Invoke-RestMethod -Method Put` block gated on `$env:FARMER_TUNNEL_BLOB_URL`.
  Skips with a single log line when unset. Warns on failure; never aborts
  the tunnel.
- `infra/setup-tunnel-blob.ps1` (NEW) — idempotent provisioning: ensures
  resource group + StandardLRS StorageV2 account + container; generates
  a 1-year SAS for `current.json` with read/create/write permissions;
  writes to `infra/.tunnel-blob-url.txt` and prints a persist-to-env
  one-liner.
- `.gitignore` — add `infra/.tunnel-blob-url.txt`.
- `docs/demo-tunnel.md` — paragraph + command under a new
  "Auto-publishing the URL to Azure Blob" section.

## [COMPLETE]

Verification:

- `dotnet build src/Farmer.sln` — **0 warnings, 0 errors**.
- `dotnet test src/Farmer.sln` — **232 green** (199 unit + 33 integration,
  0 failed, 0 skipped). Baseline 221 preserved; +11 net from the new
  tests (8 enricher + 1 factory + 2 browser).
- `setup-tunnel-blob.ps1` and `start-tunnel.ps1` both parse clean under
  `[System.Management.Automation.PSParser]::Tokenize(...)`.
- Not run in this session: `setup-tunnel-blob.ps1` end-to-end against a
  live Azure sub, and a `start-tunnel.ps1` upload with a real SAS.
  Orchestrator owns both per the stream spec.

Files touched:

- NEW `src/Farmer.Host/Services/TriggerBodyEnricher.cs`
- NEW `src/Farmer.Tests.Integration/TriggerBodyEnricherTests.cs`
- NEW `infra/setup-tunnel-blob.ps1`
- NEW `docs/streams/v2-stream-3-farmer-identity.status.md` — this file
- MOD `src/Farmer.Core/Models/RunRequest.cs` — +`UserId` prop
- MOD `src/Farmer.Host/Services/RunDirectoryFactory.cs` — +`UserId` on
  `InboxTrigger`, copy through to `RunRequest`
- MOD `src/Farmer.Host/Program.cs` — call the enricher before the temp
  file write; no other logic change
- MOD `src/Farmer.Host/Services/RunsBrowserService.cs` — +`UserId` on
  `RunSummary` record + `RequestDoc` internal shape
- MOD `src/Farmer.Tests.Integration/RunDirectoryFactory_InlinePromptsTests.cs` —
  +user_id persistence test, +null assertion on legacy-shape test
- MOD `src/Farmer.Tests.Integration/RunsBrowserServiceTests.cs` — +2
  tests + optional helper parameter
- MOD `infra/start-tunnel.ps1` — blob-upload block inside URL-captured
  handler
- MOD `.gitignore` — `infra/.tunnel-blob-url.txt`
- MOD `docs/demo-tunnel.md` — auto-publish section

Surprises / notes for orchestrator:

- **Merge precedence: body > header.** If the JSON body carries
  `user_id`, the `X-Farmer-User-Id` header is silently ignored. The
  enricher test `Merge_BodyWins_WhenBothPresent` pins this.
  Documented inline in the trigger handler so Stream 1 (.NET 8 proxy)
  knows which field to populate if it ever wants to keep the body +
  header both flowing.
- **Whitespace/empty body-side `user_id` is treated as missing** and the
  header fills it in. Simpler for demo clients; the enricher test
  `Merge_BlankUserIdInBody_TreatedAsMissing_HeaderFillsIn` pins this.
- **Storage account name is auto-generated from a SHA-256 of the
  subscription id.** Idempotent across runs from the same sub.
  Overridable via `-StorageAccount`.
- **SAS permissions: `rcw`** (read + create + write) against the blob
  itself. Tight enough that a leak can't enumerate other blobs;
  sufficient for `start-tunnel.ps1` to replace `current.json` on every
  restart.
- **Tests use `System.Text.Json.Nodes` in assertions.** Already referenced
  transitively by the Integration project; no csproj change.

Commit(s) not pushed. Orchestrator owns the merge.
