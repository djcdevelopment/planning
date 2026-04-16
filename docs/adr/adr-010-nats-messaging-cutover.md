# ADR-010: NATS messaging replaces the file-based inbox

**Status:** Accepted
**Date:** 2026-04-15
**Superseded by:** none

## Context

Through Phase 6, the control plane was coordinated through a filesystem inbox: `InboxWatcher` polled `D:\work\planning-runtime\inbox\*.json` every 2s, writing each run's lifecycle into `runs/{run_id}/{request,state,result}.json` + `events.jsonl` with a strict three-file anti-drift invariant ([ADR-003](./adr-003-anti-drift-contract.md)). The `POST /trigger` endpoint was a thin adapter that wrote the incoming body to the inbox dir and let `InboxWatcher` pick it up. This was fine when Farmer.Host, the runtime dir, and the VMs all lived on one physical machine.

That assumption broke for two reasons:

1. **Host relocation.** Farmer.Host needed to move off the primary dev box (the user wanted to free up resources for unrelated work and stop the host from competing with their IDE/games). The new host has no `D:\` drive and the old runtime dir is on a share that's no longer reachable. The file-based inbox was effectively dead.
2. **Observability.** The file-based model had no real-time signal: "how's my run going?" meant tailing `events.jsonl` with a mapped drive. There was no queryable event stream, no central dashboard, no single-traceId waterfall across stages. Every stage wrote its own span into OTel but the spans were console-exported only — the Aspire dashboard was set up but the traces never landed (wrong OTLP port, covered in the parallel prototype).

We had two alternatives:

- **Rebuild the file-based path against a new path on the new host.** Cheap, but just moves the problem: next time the host moves or a second orchestrator is needed, we're back here. And it doesn't solve the observability problem.
- **Cut over to message-based IPC.** A proper queue gives at-least-once semantics, a replayable event log, cross-process observability, and a natural path to multi-replica Farmer.Host or multi-VM fan-out down the line.

The NATS prototype at `C:\work\iso\prototype-nats\` was built specifically as the evidence basis for this decision. That prototype shipped a working NATS + JetStream + ObjectStore + MAF + Azure OpenAI loop with ~57 spans per job in Jaeger under a single traceId, including a non-obvious trace-context gotcha that we needed to find before committing to the pattern (consumer-side handlers in fire-and-forget tasks lose `Activity.Current`; the fix is `msg.Headers?.GetActivityContext() ?? default` passed as the parent to `StartActivity` with `ActivityKind.Consumer`).

## Decision

**Retire `InboxWatcher` and the `D:\work\planning-runtime\inbox\` polling loop. Replace with NATS as the coordination fabric. HTTP `/trigger` stays as the synchronous entry point; every stage transition publishes a `RunEvent` to a JetStream stream; every run's artifacts land in a NATS ObjectStore bucket.**

Concrete shape shipped in PR #5:

- **Stream `FARMER_RUNS`** — wildcard-captures `farmer.events.run.>`, file-backed, 24h retention. One message per `stage.started`/`stage.completed`/`stage.failed` transition. Subject: `farmer.events.run.{runId}.{stage}.{status}`.
- **Bucket `farmer-runs-out`** — ObjectStore. Key layout: `{runId}/{filename}`. Host mirrors the completed run directory into the bucket post-workflow; `events.jsonl`, `state.json`, `result.json`, `review.json`, etc. all upload.
- **New project `src/Farmer.Messaging/`** owns all NATS integration. Multi-targeted net8.0;net9.0 (though we bumped all of Farmer to net9 in the same PR). `NatsConnectionProvider` lazily ensures the stream + bucket on first use. `NatsRunEventPublisher` + `NatsRunArtifactStore` are the DI-injected seams. Noop variants register automatically when `Farmer:Messaging:Enabled=false` or the URL is missing, so unit tests and offline dev still work.
- **`IRunEventPublisher` contract lives in `Farmer.Core`** so `EventingMiddleware` can consume it without a downward dependency on NATS. Middleware mirrors every stage event to the publisher alongside the existing `events.jsonl` writes.
- **OTel source `NATS.Net` is added to the tracer provider** in Farmer.Host so the NATS client's own spans (`inbox subscribe`, `$JS.API publish`, etc.) join the same trace as the workflow stages.
- **`InboxWatcher` deleted.** `D:\work\planning-runtime\inbox\*.json` polling is gone. `/trigger` is the only HTTP ingress path; a future ADR will document adding a `NatsRunListener` for a pure-NATS ingress path (not part of this cutover).
- **Anti-drift contract preserved.** `events.jsonl`, `state.json`, `result.json` all still agree on the final phase. NATS publishing is additive, never authoritative — if NATS is down, the file writes still happen and the run completes.

## Consequences

**Positive:**

- **Host portability.** The control plane can now live anywhere that reaches NATS over TCP. Farmer.Host on the new box talks to `nats://127.0.0.1:4222` (co-located) or `nats://lan-host:4222` (remote) with the same config key. The broken `D:\` drive was the forcing function but multi-host is the capability we unlocked.
- **One traceId end-to-end.** A single `/trigger` now produces 100+ spans under one traceId in Jaeger: HTTP ingress → `workflow.run` → 7× `workflow.stage.*` → `farmer.events publish` × N → `$JS.API publish` × N → `$O.farmer-runs-out publish` × N. Click one URL, see the whole run.
- **Replayable event log.** `FARMER_RUNS` is a durable JetStream — a new consumer can replay the last 24h of events, recompute metrics, rebuild the UI, whatever.
- **Artifact access without mapped drives.** ObjectStore reads work from any client on the LAN; no SMB/SSHFS/K: required to inspect a run's output from a second machine.
- **Same-shape path to production.** When we swap NATS for Azure Service Bus (planned, see [project_nats_prototype memory](../../README.md#future)), most of `Farmer.Messaging` becomes two provider implementations of the same interfaces. No business logic change.

**Negative:**

- **Ingress is still file-aware.** `/trigger` writes a temp file and calls `RunDirectoryFactory.CreateFromInboxFileAsync` to stand up the run dir. That file-writing lives on *local* disk now (no network share), but it's still an artifact of the old model. A future cleanup should let the pipeline accept a `RunRequest` in-memory.
- **`tools/jaeger.exe` doesn't fit in git.** 123 MB > GitHub's 100 MB file limit. We check in `tools/nats-server.exe` (17 MB) but fetch Jaeger on demand via `tools/download-jaeger.ps1`, invoked automatically by `infra/start-jaeger.ps1`. This works but adds one onboarding step.
- **Trace context doesn't cross the SSH boundary.** `workflow.stage.Dispatch` wraps the whole Claude CLI session as a single flat span (5-20 minutes in real mode). We don't instrument inside `worker.sh`. Propagating `traceparent` through SCP'd env files is doable but deferred — the current span tells us duration + outcome, which is enough for now.
- **Retrospective provider is unchanged.** Still OpenAI gpt-4o-mini via MAF ([ADR-006](./adr-006-openai-over-anthropic-maf.md)). The prototype proved Azure OpenAI works end-to-end but swapping during a transport cutover conflates variables (did the output shift because of the new bus, or the new model?). A separate ADR will document the Azure swap when we make it.
- **Two runtime artifacts to install**: `tools/nats-server.exe` (in-repo) and `tools/jaeger.exe` (downloaded). One binary per observability/transport dependency. Production migration to Azure Service Bus + Application Insights eliminates both.

## Verification

PR #5 demonstrates both fake- and real-worker paths end-to-end on `vm-golden`:

| Run | runId | Duration | Stages | Spans | Jaeger trace |
|---|---|---|---|---|---|
| Fake worker | `run-20260415-234022-6bbdb3` | 1.6s | 7/7 | 130 | `1f356fa5d45d4b0f7bab39774c43c087` |
| Real Claude | `run-20260415-234803-209180` | 368s (~6m) | 7/7 | 130 | `49d0201f678accfad0737cd3f68c7c1a` |

The real-Claude run produced a working Vite + React + TypeScript project with `@tanstack/react-table`, ESLint, Prettier, `DataGrid.tsx`, and `useGridData.ts` hook — three prompts, three `exit=0`, ObjectStore got 9 artifact files per run.

Smoke test: `scripts/dev-run.ps1` (starts Farmer.Host with `appsettings.Development.json` overrides) + `curl.exe -X POST -H "Content-Type: application/json" --data-binary "@scripts\demo\sample-request.json" http://localhost:5100/trigger` → `scripts/_waterfall.ps1` prints the latest trace waterfall.
