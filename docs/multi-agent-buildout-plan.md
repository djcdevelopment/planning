# Multi-Agent Buildout — Master Plan (Phases 7 → 10)

Roadmap for the arc from "one real run locally" to "heterogeneous agents collaborating on the farm". Each phase has its own plan doc; this one sequences them, surfaces cross-cutting concerns, and names session boundaries.

Authoritative phase docs:
- [phase7-close-the-loop.md](./phase7-close-the-loop.md)
- [phase8-farm-online.md](./phase8-farm-online.md)
- [phase9-fan-out-fan-in.md](./phase9-fan-out-fan-in.md)
- [phase10-agent-to-agent.md](./phase10-agent-to-agent.md)

## Dependency chain + critical path

```
Phase 7 — Close the loop (local, 1 worker)
    ├─ Stream A: D:\ path fix          ┐
    ├─ Stream B: Azure OpenAI swap     ┤ parallel
    └─ Stream C: e2e run + retro       ┘ sequential after A+B

Phase 8 — Farm online (2 workers)      ← blocked by Phase 7 e2e green
    ├─ Stream A: vm-hub build          ← blocking
    ├─ Stream B: worker buildout       ┐ parallel after A
    └─ Stream C: Host wiring           ┘

Phase 9 — Fan-out / fan-in             ← blocked by Phase 8 parallel-trigger proof
    ├─ Stream A: shard contract        ← blocking
    ├─ Stream B: fan-out driver        ┐ parallel after A
    └─ Stream C: retro aggregator      ┘

Phase 10 — Agent-to-agent / MCP        ← blocked by Phase 9 aggregate-retro working
    ├─ Stream A: MCP scaffold + registry ← blocking
    ├─ Stream B: worker MCP server     ┐
    ├─ Stream C: live-query retro       ┤ partial parallel (C depends on B)
    └─ Stream D: first specialist      ┘ (optional)
```

**Critical path** (longest dependency chain): 7-C → 8-A → 8-C → 9-A → 9-B → 10-A → 10-B → 10-C.

## Wall-clock budget

| Phase | Parallel best-case | Serial worst-case | Realistic (w/ debug + retro) |
|---|---|---|---|
| 7 | 2.5 hr | 4 hr | **3-4 hr** (this session) |
| 8 | 2 hr | 3 hr | **4-5 hr** (VM provisioning eats wall time) |
| 9 | 4 hr | 6 hr | **5-6 hr** |
| 10 | 9 hr | 12 hr | **2 sessions, ~10-12 hr total** |

**Total for the arc:** ~25-30 engineering hours across 4-5 sessions. This is a multi-week buildout at normal pace, not a one-nighter.

## Session boundaries (recommended)

- **Session N (now):** Phase 7 end-to-end. Close the loop. Green-light Phase 8 at session close.
- **Session N+1:** Phase 8. Farm online. Two-worker parallel proof.
- **Session N+2:** Phase 9. Fan-out / fan-in.
- **Session N+3:** Phase 10 Streams A + B. MCP scaffold + worker servers.
- **Session N+4:** Phase 10 Streams C + D. Live-query retro + first specialist.

Do not merge phases into one session unless the prior one's retro validates the assumption. Phase discipline ("refine → plan → build, sequential and explicit") from the session retro applies at the phase scale too.

## Cross-cutting concerns

Things that evolve across every phase. Own them here so they don't silently drift.

### Auth posture

| Phase | Local dev | Farm (LAN) | Future (Azure) |
|---|---|---|---|
| 7 | API key (retired by 7-B) → Entra via `az login` (DefaultAzureCredential) | n/a | Managed Identity |
| 8 | Same | NKEY per worker on NATS; SSH key auth Host↔Worker | Same |
| 9 | Same | Same (shards travel on same NATS auth) | Same |
| 10 | Same | + mTLS on MCP endpoints (hub-issued certs) | + Managed Identity for MCP clients |

**Rule:** no new phase introduces a less-secure auth path than the one before it. Every phase lists its auth posture in its own plan's "open questions".

### Observability

- Phase 7: Jaeger + NATS monitoring + `cost-report.json` (already shipped in phase 5/6).
- Phase 8: add farm health panel — worker idle/busy, NATS queue depth, per-worker run throughput. Likely a small Grafana on vm-hub.
- Phase 9: shard-correlated traces (Jaeger already supports `parent_run_id` via span links). Per-shard cost visible in aggregate cost report.
- Phase 10: MCP call spans appear in Jaeger as child of worker stage spans. Registry health endpoint.

### Memory evolution

After each phase's retro:
- Capture surprises as `feedback_*` memory.
- Capture project-shape shifts (new invariants, new conventions) as `project_*`.
- Prune stale memory (do not accumulate — the session retro notes this is a recurring failure mode).

### Cost

- Phase 7 bakes Azure OpenAI per-call cost into `cost-report.json`. Baseline for everything downstream.
- Phase 9 warning: aggregate retrospective multiplies tokens per run by shard count. Make this explicit in cost report + don't default shard counts above 4.
- Phase 10 MCP calls to specialists add agent-on-agent token cost. Track `from_agent`/`to_agent` in cost rows.

## Human decision points (non-delegable)

Per the session retro's "always in the loop" list. Each phase surfaces its own decisions in its plan doc; these are the *cross-phase* ones:

1. **Before Phase 8:** is Phase 7 evidence *really* green, or are we rationalizing a partial result? User review of `docs/phase7-retro.md` required.
2. **Before Phase 9:** do we have enough workers for fan-out to matter, or do we need Phase 8.5 (a third worker)? 2 workers = 2× theoretical max; that's fine for v1 but the user should consciously choose before we commit shard plumbing.
3. **Before Phase 10:** is MCP the right call, or should we double down on NATS-only coordination and defer MCP indefinitely? Phase 9 retro should surface this clearly.
4. **After Phase 10:** production posture (Azure migration) or more specialists?

## Risk register (cross-phase)

| Risk | Phases affected | Mitigation |
|---|---|---|
| Azure OpenAI quota / deployment throttling | 7+ | Pin deployment name; monitor 429s; surface in cost report |
| Farm network flakiness (NAT, DHCP lease churn) | 8+ | Static IPs reserved in Hyper-V NAT; hosts file entries; document |
| NATS JetStream file-handle / disk accumulation | 8+ | Retention policy on `FARMER_RUNS` stream; monitor store size on vm-hub |
| MAF / MCP / A2A SDK churn (preview packages) | 7, 10 | Pin versions per phase; don't upgrade mid-phase |
| Memory drift (stale facts accumulating) | All | Consolidate-memory skill after each phase retro |
| Scope creep into production posture early | 8, 9, 10 | Explicit non-goal in each phase doc; "Azure Service Bus is Phase 11+, not now" |

## Gate signal protocol (used by all phases)

Inherited from the session retro's durable vocabulary. Stored as `docs/streams/phase{N}-stream-{letter}.status.md` per stream.

| Signal | Writer | Reader | Meaning |
|---|---|---|---|
| `[RESEARCH-DONE]` | builder | orchestrator | Read everything; plan shape ready |
| `[DESIGN-READY]` | builder | orchestrator | Design note posted; review before I code |
| `[DESIGN-ACK]` | orchestrator | builder | Approved; code away |
| `[BLOCKED: reason]` | builder | orchestrator | Need input; halting |
| `[COMPLETE] <sha>` | builder | orchestrator | Done; ready to merge |
| `[NEEDS-HUMAN]` | orchestrator | human | Above my pay grade; user decision |
| `[MERGED]` | orchestrator | human | Landed on main |

## Definition of shipped (whole arc)

The multi-agent buildout is "done" (for this definition of done) when:

- Farm topology: 1 hub + 2+ workers, idle-registered, auto-start with host.
- A single `/trigger` can fan out into ≥4 shards across ≥2 workers and complete faster than serial.
- Retrospective aggregates shard results into one verdict.
- Workers are queryable as MCP servers; at least one live-query case lands.
- At least one specialist agent on the hub, consulted by a worker mid-run.
- All auth on Entra/Managed Identity or mTLS; no bare API keys in source or config.
- `docs/phase{7-10}-retro.md` exist.
- Memory updated after each phase; stale entries pruned.

After that, the next meaningful arc is **Phase 11 — production posture** (Azure migration) or **Phase 12 — cross-session memory MCP** (shared context across Claude sessions), depending on what the user's roadmap demands.

## Current state

- Phase 7 plan: written, awaiting Azure OpenAI provisioning + PR #17 merge (merged as of this session).
- Phase 8/9/10 plans: drafted, not yet ack'd.
- Master plan (this doc): draft 1.
- Next action: user completes Azure OpenAI portal provisioning + `az login`; orchestrator dispatches Phase 7 streams A + B in parallel worktrees off `main`.
