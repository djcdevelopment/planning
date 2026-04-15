# ADR-002: File-first primary entry path via InboxWatcher

**Status:** Accepted (Phase 5)
**Date:** 2026-04-08
**Superseded by:** none

## Context

A separate agent working in parallel built a Phase 5 variant (`origin/claude/phase5-otel-api`) where the primary entry path was `POST /v1/chat/completions` — OpenAI-compatible HTTP, with an in-process `BackgroundWorkflowRunner` tracking runs in a `ConcurrentDictionary<runId, Task>`. That approach has real advantages: external tooling that already speaks OpenAI can talk to Farmer with zero code changes.

But it came with a cost: run state was in-memory. A process restart loses every in-flight run. A crash leaves no forensic trail. Two processes can't share the same run queue. Scaling horizontally requires a real state store.

Phase 5's user framing was explicit: "filesystem remains the source of truth even when telemetry is present". The user's `planning-runtime` directory idea (see [ADR-001](./adr-001-externalized-runtime.md)) makes no sense if the primary execution path doesn't actually use it.

## Decision

**The primary entry path is `InboxWatcher`, a `BackgroundService` that polls `D:\work\planning-runtime\inbox\` every 2 seconds.** When it sees a new JSON file, it calls `RunDirectoryFactory.CreateFromInboxFileAsync` to stamp a `request.json` into a new `runs\{run_id}\` directory, then invokes `RunWorkflow.ExecuteFromDirectoryAsync(runDir)`.

HTTP is a *secondary* convenience entry. `POST /trigger` does the same thing: accepts JSON, writes it to a temp file, calls the same factory, calls the same workflow. Zero in-memory run state.

Each run's complete state lives in its directory: `request.json`, `state.json`, `events.jsonl`, `result.json`, `cost-report.json`, `logs/`, `artifacts/`. Any consumer that wants to know anything about any run reads files. Any process restart picks up exactly where the filesystem says we were.

## Consequences

**Positive:**
- Crash recovery is trivial: re-process the inbox. In-flight runs that crashed mid-execution leave a partial run directory (the events.jsonl tells you where they stopped) that a human can inspect.
- The QA agent (Phase 6) reads from the run directory, not from live process state. Same for any future consumers.
- Multiple host processes could theoretically process the same inbox (not yet implemented, but not blocked by the architecture).
- "Where did this data come from?" is always answerable with a file path.

**Negative:**
- 2-second polling latency. Acceptable for the current "drop a file and wait" use case; unacceptable if Farmer ever needs to react to events in <1s. When that day comes, we add a `FileSystemWatcher` or switch to a real queue.
- `InboxWatcher` processes runs sequentially. No concurrency in Phase 5/6. When we add multi-VM dispatch, we'll need a proper worker pool with file-based locking or a claim protocol.

## Related

- [ADR-001](./adr-001-externalized-runtime.md) — where the inbox directory lives
- [ADR-003](./adr-003-anti-drift-contract.md) — what files every run must produce and how they stay consistent
- `src/Farmer.Host/Services/InboxWatcher.cs` — the implementation
- `src/Farmer.Host/Services/RunDirectoryFactory.cs` — the reusable component that both entry paths share
