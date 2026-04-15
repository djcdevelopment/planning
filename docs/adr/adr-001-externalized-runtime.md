# ADR-001: Externalized runtime directory

**Status:** Accepted (Phase 5)
**Date:** 2026-04-08
**Superseded by:** none

## Context

Phases 1–4 built Farmer as a self-contained .NET solution where runtime state (run folders, event logs, cost reports) lived under the repo root. That made per-phase end-to-end testing quick but coupled "the engine" to "state from specific test runs". Every time we cleaned up a test, we risked deleting something the next test needed. Every time we committed, we had to `.gitignore` ephemeral files. Every time we wanted to demo against a real VM, we had to reason about which files belonged to the repo and which were transient.

Phase 5's goal was "pipeline runs real workers against a real VM with visible Aspire traces". That goal forced the question: where does runtime state live?

## Decision

**All runtime state lives at `D:\work\planning-runtime\`, outside the repo.**

The repo (`D:\work\planning\`) is the engine: source code, tests, sample plan templates, scripts. The runtime directory is the state: per-run folders, inbox queue, outbox for downstream tools, QA archive. The two never share writable files.

Directory structure under `D:\work\planning-runtime\`:
```
data\sample-plans\      ← worker inputs (copied from repo)
inbox\                  ← trigger files dropped by external tools
runs\{run_id}\          ← immutable per-run directories
outbox\                 ← downstream consumption (Phase 7+)
qa\                     ← cross-run aggregates (Phase 7+)
```

Configuration lives in `FarmerSettings.Paths` with `Root`, `Data`, `Runs`, `Inbox`, `Outbox`, `Qa` all defaulting to subdirectories of `D:\work\planning-runtime\`. The repo's `appsettings.json` points here; tests override via in-memory options.

## Consequences

**Positive:**
- Repo stays clean. Never any ephemeral run files committed.
- External tools (QA aggregators, outbox consumers, test harnesses) can read the runtime directory without knowing anything about the engine repo.
- Restores / rebuilds / branch switches of the repo don't touch runtime state.
- The mental model is simple: repo = engine, runtime dir = state. Anyone reading the codebase can tell in ten seconds where anything lives.

**Negative:**
- Two directories to keep in sync — if you regenerate the runtime dir, you lose historical runs.
- New developers need to set up `planning-runtime\` before the first run. We document this in [README.md](../../README.md) and [docs/end-to-end-verification.md](../end-to-end-verification.md).
- Absolute paths in `appsettings.json` aren't portable. Acceptable for a Windows-only dev loop; when this ships elsewhere, we revisit.

## Related

- [ADR-002](./adr-002-file-first-entry.md) — the filesystem-as-source-of-truth philosophy that makes this directory meaningful
- [docs/phase5-pattern.md](../phase5-pattern.md) — the architectural invariants section talks about "filesystem is the source of truth"
