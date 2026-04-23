# Phase 9 — Fan-Out / Fan-In

**Depends on:** Phase 8 (farm online, 2+ workers idle, parallel triggers proven).

**Goal:** one `/trigger` request can shard a plan across multiple workers, and the retrospective stage aggregates the partial results into a single verdict. Phase 8 proved *N concurrent runs*; Phase 9 proves *one run of N parts*. This is where Farmer starts actually beating sequential execution on wall-clock for multi-component builds.

## The shape

```
Before:  trigger  → [Plan] → worker → result → retro → verdict
After:   trigger  → [Plan] → shard(K) → worker₁..K (parallel) → join → retro(aggregate) → verdict
```

## Non-goals

- Dynamic shard count based on plan content (out of scope — caller specifies `shard_count` on trigger, defaults to 1).
- Cross-shard communication during execution (Phase 10).
- Persistent checkpointing for resume across failures (nice-to-have; skip unless Phase 8 retro flags it).

## Open questions (resolve before dispatch)

1. **What does "shard a plan" actually mean?** Two choices:
   - **a. Caller-sharded.** Trigger body carries `shards: [{ work_request_name, scope }, ...]`; Host just runs N `RunWorkflow`s in parallel. Simple, dumb, composable.
   - **b. Agent-sharded.** Host runs a `PlanningStage` agent that reads the work request and emits shards itself. More powerful, more failure modes.
   
   Rec: **a first**. Caller-sharded gets us the fan-out plumbing cheaply; agent-sharded can be added as a preprocessor later without changing the workflow. YAGNI on agent sharding until we have demand.

2. **Join semantics — all-or-nothing vs partial?** If 3 of 4 shards succeed, is that a retry-on-the-failed-shard scenario, or a whole-run-failure? Rec: **all-or-nothing v1**. Retrospective sees only fully-completed shard sets. Failed shard fails the parent run; retry policy from Phase 7 applies at the parent level. Partial-success handling is its own phase.

3. **Retrospective aggregation.** Does retro get each shard's full context, or a summary? Rec: pass **full context per shard** + explicit `shard_id` + `parent_run_id`. Agent can decide granularity. Cost concern: a 4-shard run quadruples retro tokens — document this and make it visible in `cost-report.json`.

## Streams

### Stream A — Shard contract in Core

**Territory**
- `src/Farmer.Core/Contracts/RunShard.cs` (new) — `{ shard_id, parent_run_id, scope, work_request_name }`.
- `src/Farmer.Core/Contracts/ShardedRunRequest.cs` (new) — extends `/trigger` schema with optional `shards[]`. If absent, legacy single-run behavior.
- `src/Farmer.Core/Contracts/AggregatedResult.cs` (new) — `{ parent_run_id, shards: ShardResult[], aggregate_verdict }`.
- Update JSON contracts in `docs/adr/adr-003-anti-drift-contract.md` — three-file invariant now has a fourth file (`shards.jsonl` in the parent run dir).

**Gate A**
- Unit tests for contract serialization + back-compat (old single-shard trigger still parses).

### Stream B — Fan-out driver in Host

**Territory**
- `src/Farmer.Host/ShardDispatcher.cs` (new) — reads `ShardedRunRequest`, mints parent_run_id, publishes N `RunShard` messages to a new `FARMER_SHARDS` JetStream subject, tracks completion via a consumer on `farmer.events.shard.>` subjects.
- `src/Farmer.Host/ShardJoin.cs` (new) — barrier: waits for all shard completions (or first failure), builds `AggregatedResult`.
- `src/Farmer.Messaging/NatsSubjects.cs` — new subject namespace `farmer.rpc.shard.*` + `farmer.events.shard.*`.
- `src/Farmer.Worker/` — worker stays dumb. Each shard is a normal run with a parent pointer in metadata. Worker has no new code.

**Gate B**
- Trigger with 4 shards → 4 parallel runs → all complete → `AggregatedResult` materialized in parent run dir.
- If shards=1 (or absent), behavior is bit-identical to Phase 8.

### Stream C — Retrospective aggregator

**Territory**
- `src/Farmer.Agents/AggregateRetrospectiveAgent.cs` (new, sibling of `MafRetrospectiveAgent`) — consumes `AggregatedResult`, emits a single verdict.
- Prompt engineering: show the agent each shard's result with clear shard boundaries; explicit instruction to judge the *whole* (did the shards compose? are there inter-shard contradictions?).
- Update `RunWorkflow` — if parent run, use `AggregateRetrospectiveAgent`; else `MafRetrospectiveAgent`. Branching by presence of `shards` metadata.

**Gate C**
- 4-shard run produces one aggregate verdict with per-shard findings visible.
- Cost report shows per-shard + aggregate retro costs separately.

## Parallelization

- A is blocking for B and C.
- B and C can run parallel after A.
- Estimated: A ~1 hr, B ~3 hrs, C ~2 hrs → ~4 hrs wall-clock with B+C parallel.

## Risks + mitigations

- **Straggler shards.** One slow shard blocks the whole run. Mitigation v1: timeout per shard → whole-run failure. v2 (out of scope): cancel siblings on first failure.
- **Retry ambiguity at parent level.** Phase 7 retry driver retries the whole run. With shards, retrying means re-running all shards. Document that explicitly; shard-level retry is Phase 9.5 or later.
- **NATS subject explosion.** `farmer.events.shard.{parent_id}.{shard_id}.{stage}` can get busy. Monitor; subject sharding within JetStream is cheap but observability tooling needs to keep up.

## Exit definition

- One trigger fans out into N=4 parallel shards on 2 workers.
- Aggregated retrospective produces one verdict.
- Wall-clock beats serial-execution baseline by at least 2× on the 4-shard case (2-worker farm, so 2× is the theoretical max, accept ≥1.7× for overhead).
- `docs/phase9-retro.md` written.
- Decision point: is this enough parallelism, or do we need more workers? (Informs Phase 10 / beyond.)
