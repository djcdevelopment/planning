# Phase 7 — Close the Learning Loop

**Goal:** run one real plan end-to-end through Farmer on this machine, so evidence starts landing in `planning-runtime/runs/` and the next-session learning loop closes. Everything downstream (VM farm expansion, MCP read surface, NATS-event-driven retry) is deferred until we have one real run's evidence in hand.

## Why now

Three blockers have accumulated on top of a working repo:
1. **Leaked OpenAI key** — rotated before this session (✅ cleared).
2. **Stale `D:\` path default** — `Farmer.Host` crashes on startup in Production env because `PathsSettings.cs` defaults + `appsettings.json` still point at `D:\work\planning-runtime` (user runtime lives at `C:\work\iso\planning-runtime`). `appsettings.Development.json` is correct, which is why dev runs have been fine.
3. **OpenAI direct → Azure OpenAI swap** — already backlog'd in `CLAUDE.md`. Aligns with the Azure-native preference and retires an unrotatable public-OpenAI dependency.

Fixing 2 + 3 together clears the minimum surface needed to run real plans with Azure identity and capture evidence.

## Non-goals (deferred)

- VM farm hub + workers — single-machine e2e first; multi-node parallelism comes after we prove the pattern locally.
- MCP read surface over runtime/memory — need one real run's evidence to know which queries are hot before committing to a tool surface.
- NATS-event-driven `RetryCoordinator` — backlog item, not blocking.
- `prototype-nats/` rename — reboot-only, trivial, do whenever.

## Resolved prerequisites (2026-04-22 session)

- ✅ PR #17 merged to `main` (squash `01e7f2b`, branch deleted).
- ✅ Azure OpenAI resource provisioned + verified:
  - **Endpoint:** `https://farmer-openai-dev.openai.azure.com/`
  - **Deployment:** `gpt-4.1-mini` (model version `2025-04-14`)
  - **Resource group:** `rg-farmer-dev` (East US 2)
  - **Auth:** Entra / `DefaultAzureCredential` via `Connect-AzAccount` (`derek.ciula@gmail.com`, Pay-As-You-Go sub)
  - **Role:** `Cognitive Services OpenAI User` assigned to user's object ID on the account scope
  - **Probe:** Entra-token chat completion returns successfully
- ⚠️ **Model substitution:** original plan called for `gpt-4o-mini`; that version is soft-retired in the deployment API (catalog still lists it as GA but new deployments rejected). `gpt-4.1-mini` is the equivalent modern mini-tier — cheap, GA, default version, deprecation out to 2026-10-14.

## Streams

### Stream A — `D:` path fix (~20 min, low-risk)

**Root cause**
- `src/Farmer.Core/Config/PathsSettings.cs:5-10` — defaults hardcoded to `D:\work\planning-runtime\*`.
- `src/Farmer.Host/appsettings.json:11-16` — `Farmer:Paths` section hardcoded to `D:\`.
- `src/Farmer.Host/appsettings.Development.json:9-14` — correct `C:\work\iso\planning-runtime` (this is why dev has worked).
- Crash site: `Directory.CreateDirectory(...)` on the `Inbox` path during startup. `InboxWatcher` was retired in PR #5 (NATS cutover per ADR-010), so this is both a stale default AND likely dead-code.

**Fix**
- Change `PathsSettings.cs` defaults from `D:\work\planning-runtime\*` → `C:\work\iso\planning-runtime\*` (match reality + Development config).
- Update `src/Farmer.Host/appsettings.json` `Farmer:Paths` section to match.
- Audit whether `Inbox` dir is still created anywhere on startup (post-NATS-cutover); if dead, remove the creation call. If still load-bearing for something, leave it but ensure it points at C:.

**Territory (owned files)**
- `src/Farmer.Core/Config/PathsSettings.cs`
- `src/Farmer.Host/appsettings.json` (only the `Farmer:Paths` JSON section)
- Possibly: `src/Farmer.Host/InboxWatcher.cs` or whatever owns the dead `CreateDirectory(Inbox)` call (delete if retired).

**Gate A**
- `dotnet test src\Farmer.sln` green (133 tests baseline).
- `.\scripts\dev-run.ps1` boots without `DirectoryNotFoundException`.
- Host starts in Production env too (`$env:ASPNETCORE_ENVIRONMENT='Production'; .\scripts\dev-run.ps1` — verifies the default path change took effect).

### Stream B — Azure OpenAI swap (~1.5 hr)

**Goal:** retire `new OpenAIClient(apiKey)` in `MafRetrospectiveAgent`; replace with `new AzureOpenAIClient(endpoint, DefaultAzureCredential)` and a deployment name. Entra auth via `az login` locally; Managed Identity path available for later Azure-hosted runs.

**Constraint:** keep `Microsoft.Agents.AI.OpenAI 1.1.0` (per ADR-006 + ADR-009 — the MAF+OpenAI hybrid is deliberate). Azure OpenAI is API-compatible; the package's `OpenAIClient`-shaped surface accepts an `AzureOpenAIClient` via the Azure SDK interop. Validate this is still true at MAF 1.1.0.

**Territory (owned files)**
- `src/Farmer.Agents/Farmer.Agents.csproj` — add `Azure.AI.OpenAI` + `Azure.Identity` NuGets.
- `src/Farmer.Agents/MafRetrospectiveAgent.cs` — rewrite `TryBuildAgent()` (~290 LOC file; net change ~60 LOC).
- `src/Farmer.Core/Config/OpenAISettings.cs` — add `Endpoint` + `DeploymentName` fields. Optional: `AuthMode` enum (`ApiKey | Entra`) for a soft-launch dual-path; rec **skip** — YAGNI, Entra-only.
- `src/Farmer.Host/appsettings.json` + `appsettings.Development.json` — new keys under `Farmer:OpenAI`.
- `docs/adr/adr-006-openai-over-anthropic-maf.md` — amend in place with an "Update 2026-04-22" block (don't supersede; the OpenAI-over-Anthropic decision still stands, only the deployment path changes).

**Gate B**
- `dotnet test` green.
- Smoke test: boot Host, trigger one run with `worker_mode=fake` → retrospective stage calls Azure endpoint → completes with verdict. Proves Entra auth + endpoint wiring.

**Collision note with Stream A:** both streams edit `appsettings.json`, but different JSON sections (`Farmer:Paths` vs `Farmer:OpenAI`). Orchestrator merges; JSON merge is trivial. No lockfile conflict risk.

### Stream C — e2e run + retro (sequential, after A + B merge)

1. `.\scripts\dev-run.ps1` — confirm Host boots clean (validates A).
2. `.\infra\Farmer.SmokeTrace.ps1 -WorkerMode fake` — fast 7/7-green sanity (validates pipeline wiring).
3. `.\infra\Farmer.SmokeTrace.ps1 -WorkerMode real` — real Claude CLI on a VM + Azure OpenAI retrospective (~5-20 min). **This is the loop-closing run.**
4. Inspect evidence: `planning-runtime/runs/{latest}/cost-report.json`, `events.jsonl`, Jaeger trace at `localhost:16686`, retro output quality.
5. Write `docs/phase7-retro.md` — what was surprising, what broke, what's worth capturing in memory.
6. Update user/project memory with anything memory-worthy.

**Gate C**
- Full run completes (verdict: Accept or Retry — both are valid outcomes, data is the product per ADR-007).
- All three evidence channels populated (cost-report, events.jsonl, Jaeger trace).
- Retro doc written + at least one memory update landed.

## Gate protocol (borrowed from session retro)

Each stream's builder writes a `phase7-stream-{a,b}.status.md` under `docs/streams/`:

| Signal | Writer | Meaning |
|---|---|---|
| `[RESEARCH-DONE]` | builder | Read everything; plan shape ready. |
| `[DESIGN-READY]` | builder | Design note posted; review before I code. |
| `[DESIGN-ACK]` | orchestrator | Approved; code away. |
| `[BLOCKED: reason]` | builder | Need input; halting. |
| `[COMPLETE] <sha>` | builder | Done; ready to merge. |
| `[NEEDS-HUMAN]` | orchestrator | Above my pay grade; user decision. |

Orchestrator merges on `[COMPLETE]`. User owns merge-to-`main`.

## Territory matrix

| File/dir | Stream A | Stream B | Stream C |
|---|---|---|---|
| `src/Farmer.Core/Config/PathsSettings.cs` | ✅ owns | — | — |
| `src/Farmer.Core/Config/OpenAISettings.cs` | — | ✅ owns | — |
| `src/Farmer.Agents/**` | — | ✅ owns | — |
| `src/Farmer.Host/appsettings.json` — `Farmer:Paths` | ✅ owns | — | — |
| `src/Farmer.Host/appsettings.json` — `Farmer:OpenAI` | — | ✅ owns | — |
| `src/Farmer.Host/appsettings.Development.json` | ✅ reads | ✅ owns | — |
| `src/Farmer.Host/InboxWatcher.cs` (if it still exists) | ✅ owns (delete if dead) | — | — |
| `docs/adr/adr-006-*.md` | — | ✅ owns | — |
| `docs/phase7-retro.md` | — | — | ✅ owns |
| `docs/streams/phase7-stream-a.status.md` | ✅ owns | — | — |
| `docs/streams/phase7-stream-b.status.md` | — | ✅ owns | — |

No overlap. Safe to parallelize A + B.

## Exit definition

Phase 7 is complete when:
- Host boots clean in Production environment with default config.
- MafRetrospectiveAgent is on Azure OpenAI via Entra.
- One real run's evidence is archived in `planning-runtime/runs/`.
- `docs/phase7-retro.md` exists.
- Memory is updated with at least one insight from the run.
- At least one decision made about what comes next (VM farm | MCP | NATS retry).
