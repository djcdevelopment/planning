# Phase 10 — Agent-to-Agent (MCP surface + A2A)

**Depends on:** Phase 9 (fan-out proven, aggregate retrospective working).

**Goal:** agents stop being black boxes. Workers expose their capabilities and in-flight state as MCP tools; the retrospective agent can query a running worker; workers can consult specialist agents (design system, doc retrieval, policy checker) on the hub. This is where "multi-agent" stops meaning "parallel copies of the same agent" and starts meaning "heterogeneous agents collaborating".

This is also the phase where the MCP question from the Phase 6-retro era gets a real answer. We now have Phase 7-9 evidence telling us which queries are actually hot — MCP surface is designed against that evidence, not guessed.

## Motivating examples (the kind of thing that becomes possible)

1. **Live retrospective.** Retro agent queries `worker.get_current_plan_progress(run_id)` mid-run; if it sees a shard stuck on the same failure twice, it can inject a `feedback.md` file without waiting for the shard to finish. Closes the retry loop faster.
2. **Design-system consultation.** A UI-building worker calls `design_system.get_component_spec("data-grid")` on a specialist agent running on vm-hub. Avoids baking design system knowledge into every worker prompt.
3. **Cross-worker code review.** Worker A finishes a PR; worker B (code-review agent) is woken via `reviewer.review(pr_url)` MCP call. Farmer doesn't need to sequence this — the review agent is just another MCP tool.

## Non-goals

- Full Microsoft A2A protocol compliance (MAF's A2A spec is moving; target the subset that maps cleanly to MCP tools).
- MCP surface over memory / planning repo for external Claude sessions (separate problem — that's cross-session context sharing, not in-session A2A).
- Replacing NATS with MCP as the transport. NATS stays. MCP is RPC for agents; NATS is durable eventing.

## Architecture addition

```
        Host (laptop)
           │
           │  NATS (durable events, unchanged)
           ▼
       ┌─────────────────────────┐
       │     vm-hub              │
       │                         │
       │  NATS Server            │
       │  MCP Registry  ◄────────┼──── specialist agents register
       │  /comms/                │     (design-system, reviewer, policy-checker)
       └────┬────────────────────┘
            │ (MCP over HTTP/SSE on LAN)
       ┌────▼────────┐   ┌────▼────────┐
       │ vm-worker-01│   │ vm-worker-02│
       │ + MCP client│   │ + MCP client│
       │ + MCP server│   │ + MCP server│  ← workers are also MCP servers
       │   (expose   │   │   (expose   │     so retro/peer agents can query them
       │    state)   │   │    state)   │
       └─────────────┘   └─────────────┘
```

## Open questions (resolve before dispatch)

1. **MCP runtime on the .NET side.** Options: (a) `ModelContextProtocol.NET` reference SDK if it exists/stable at time of Phase 10; (b) roll a minimal HTTP+SSE JSON-RPC server. Rec: use the reference SDK if mature, else minimal roll. MCP tool surface is small; auth + transport are the hard parts.
2. **Worker MCP server auth.** The worker's MCP server is on a private LAN. Rec: mTLS via a hub-issued cert, or NKEY-bridged auth. Don't open workers to anything outside the farm subnet. This echoes the Phase 8 NATS auth decision and should share credentials infra.
3. **Which capabilities ship first?** Rec: start with read-only `worker.get_state(run_id)` + `worker.get_current_output()`. Write-side (`worker.inject_feedback`) is phase-10.5 — prove the read surface is load-bearing before opening write paths.
4. **Specialist agents — where do they live?** Rec: each specialist is its own small .NET project + Dockerfile, running as systemd service on vm-hub. `Farmer.Agents.DesignSystem`, `Farmer.Agents.CodeReviewer`. Register with the hub's MCP registry on startup.

## Streams

### Stream A — MCP server scaffold + hub registry

**Territory**
- `src/Farmer.Mcp/` (new project) — `IMcpServer`, `IMcpClient`, transport abstraction (HTTP+SSE).
- `src/Farmer.Mcp/Registry/McpRegistry.cs` — in-memory on hub; keyed by `{agent_id, tool_name}`. Agents POST to `/register` on startup.
- `scripts/hub-mcp-bootstrap.sh` — runs the registry as a systemd service on vm-hub :5200.
- New ADR: `docs/adr/adr-012-mcp-agent-surface.md` — decision record for why MCP over pure NATS here.

**Gate A**
- Agent registers → `curl http://vm-hub:5200/registry` lists it.
- One hello-world tool callable end-to-end from a Farmer.Host test.

### Stream B — Worker-side MCP server (read-only)

**Territory**
- `src/Farmer.Worker/McpExpose/` (new) — host a tiny MCP server inside the worker process, expose:
  - `worker.get_run_state(run_id)` — current stage, last event, elapsed ms.
  - `worker.get_recent_output(run_id, tail_lines)` — last N lines of Claude CLI stdout.
- Register worker on hub's MCP registry on process start.
- Update `vm-worker-bootstrap.sh` (from Phase 8) to open the worker's MCP port on LAN only.

**Gate B**
- From Farmer.Host: `await mcp.Call("worker-01", "get_run_state", runId)` returns a live state object for an in-flight run.

### Stream C — Retrospective goes live-query

**Territory**
- `src/Farmer.Agents/MafRetrospectiveAgent.cs` — add MCP client; during long-running shards, periodically poll `worker.get_run_state` to catch stuck-in-retry patterns.
- `src/Farmer.Agents/AggregateRetrospectiveAgent.cs` — same, aggregated across shards.
- Feedback surface: if retro decides to intervene, post a `feedback.md` via a hub `/comms/{run_id}/feedback.md` write (stays file-based — consistent with memory's filesystem-first preference) rather than an MCP write call. MCP is read-only in v1.

**Gate C**
- Inject a `WORKER_MODE=stuck-loop` (new test mode that deliberately retries same prompt 3x) → retro agent detects the loop via MCP polling → writes intervention feedback → worker picks up feedback → loop breaks.

### Stream D (optional, time-permitting) — First specialist agent

**Territory**
- `src/Farmer.Agents.DesignSystem/` (new project) — single MCP tool: `design_system.get_component_spec(name)`. Seed with 3-5 components from the existing sample plans.
- Systemd unit on vm-hub.
- Update one sample plan to consult the specialist during a run.

**Gate D**
- A worker run calls the design-system agent via MCP mid-plan. Full trace in Jaeger shows the MCP span as a child of the worker's stage span.

## Parallelization

- A is blocking for B, C, D.
- B and D can run in parallel after A.
- C depends on B.
- A ~4 hrs, B ~3 hrs, C ~2 hrs, D ~3 hrs. Total ~9 hrs wall-clock with parallelism; longest phase by far. Budget for a 2-session split.

## Risks + mitigations

- **MCP SDK instability.** Reference implementations are moving; pin versions aggressively. Don't upgrade mid-phase.
- **Latency cost of live queries.** Retro polling workers at 1 Hz across a 20-min run = 1,200 calls. Mitigation: adaptive poll interval (slow when state's not changing, fast on stage transitions which NATS tells us about anyway).
- **Auth debt.** If we punt mTLS to "next phase" it will rot. Budget the auth work inside Stream A; don't ship `0.0.0.0` binding on workers even on a private LAN.
- **Scope creep into A2A spec.** Microsoft's A2A is broader than MCP. Resist. MCP + NATS + files covers our cases; A2A-protocol-compliance is a separate marketing-driven phase.

## Exit definition

- Workers are queryable as MCP servers.
- Retrospective runs at least one live-query case end-to-end.
- At least one specialist agent on the hub (Stream D) OR a hard-documented deferral with rationale.
- `docs/phase10-retro.md` written.
- Decision point: is the next phase **production posture** (Azure Service Bus + App Service + Managed Identity everywhere) or **more specialists** (reviewer, policy, doc-retrieval)?

## Beyond Phase 10 (previewed, not planned)

- **Phase 11 — Production posture / Azure migration.** NATS → Azure Service Bus. Host → Container Apps. Workers → Azure Container Instances or stay on Hyper-V with hybrid connections. Everything on Managed Identity + Key Vault. This is where the memory note "Azure Service Bus is the production target if the pattern holds" gets cashed in.
- **Phase 12 — Cross-session memory.** MCP server exposing user/project memory so any Claude session can query structured context without `cat`. Different problem than in-session A2A.
