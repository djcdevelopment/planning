# Session Retrospective — NATS Cutover + Phase 7 Sprint

## Date: 2026-04-15

## What shipped

Six PRs merged in one session, all to `main`, each build+test green in isolation, atomic commits throughout.

| PR | Title | Headline |
|---|---|---|
| #5 | NATS messaging cutover -- file-based inbox retired | `Farmer.Messaging` project, `InboxWatcher` deleted, every `/trigger` produces a single 100+ span Jaeger trace |
| #6 | ADR-010 + README + integration tests | Decision record for the cutover; new `Farmer.Tests.Integration` project with `NatsServerFixture` (spawns `nats-server.exe` per test class) |
| #7 | worker_mode contract + `Farmer.SmokeTrace.ps1` | `TaskPacket.WorkerMode` field with per-request/config precedence; one-command smoke script. Caught two bonus bugs: `InboxTrigger` dropped unknown JSON fields; `CreateRunStage` clobbered the parsed `RunRequest`. |
| #8 | Phase 7 retry driver | `RetryPolicy`, `FeedbackBuilder`, `RetryDriver`, `0-feedback.md` prompt prepend, `WorkflowResult.ReviewVerdict` |
| #9 | Cleanup: ADR-011, shared test helpers, Phase 7 docs | Extracted `InMemoryRunStore` from 4 nested copies; ADR-011; CLAUDE.md / README brought current |
| #10 | Release reserved VM in `RunWorkflow` finally block | Bug fix surfaced by Phase 7 demo; VM was never released; in-loop retries failed at attempt 2 |

**Test count progression:** 107 → 131 unit + 3 integration (24 net new tests, all from new behavior + shared helpers). 0 warnings throughout.

## How we got there

Started the day with the file-based inbox dead (network drives broken on the primary dev box). Decided to migrate Farmer to this machine wholesale and cut over to message-based coordination at the same time. The NATS prototype at `C:\work\iso\prototype-nats\` was the evidence basis -- pattern was already proven cross-machine with Azure OpenAI; the question was just "does it slot into Farmer cleanly?"

Sequence:
1. **Cloned Farmer here.** Bumped net8 -> net9 because the prototype was net9 and there was no net8 runtime on this box. 107 tests stayed green.
2. **Built `Farmer.Messaging`** as a new project. `IRunEventPublisher` interface in `Farmer.Core`, NATS impl in `Farmer.Messaging`, noop default for tests.
3. **Wired into `EventingMiddleware`.** Existing middleware now publishes each stage event via NATS alongside the `events.jsonl` write. Anti-drift contract preserved.
4. **Deleted `InboxWatcher`.** `/trigger` is the only ingress. Run dirs mirror to NATS ObjectStore post-workflow.
5. **First green E2E on vm-golden** -- single 7/7 trace, fake worker mode. (Required fixing the `worker_mode` plumbing along the way -- two latent bugs in `InboxTrigger` and `CreateRunStage` came out of that.)
6. **First green E2E with real Claude** -- `claude login` on the VM, real `worker_mode=real`, Claude built a Vite+React+TypeScript project, retrospective AutoPassed (no OpenAI key set yet).
7. **Phase 7 retry driver.** `RetryPolicy` opt-in per request, `FeedbackBuilder` renders prior verdict as markdown, `LoadPromptsStage` prepends as `0-feedback.md`. Driver loops until policy says stop.
8. **First green E2E with real OpenAI retrospective.** Verdict `Accept` returned. Forced a 2-attempt chain (`retry_on_verdicts: ["Accept", "Retry"]`) to exercise the loop -- which surfaced the VM release bug.
9. **VM release fix.** Wrap `RunWorkflow.ExecuteAsync` stages in try/finally; release the reserved VM in the finally block. 5 new tests, lifted `LambdaStage` to a shared TestHelper while there.

## Run IDs preserved (the receipts)

| Run | What it proved |
|---|---|
| `run-20260415-234022-6bbdb3` | NATS cutover end-to-end, fake worker, 7/7, 130 spans, traceId `1f356fa5d45d4b0f7bab39774c43c087` |
| `run-20260415-234803-209180` | Real Claude CLI session, 368s, produced a working React+TS scaffold, traceId `49d0201f678accfad0737cd3f68c7c1a` |
| `run-20260416-035009-64fa6e` | First real-OpenAI retrospective: verdict `Accept`, risk 10, real findings + suggestions in JSON |
| `run-20260416-035020-0bbe7b` | Retry chain attempt 2 -- `parent_run_id` linking confirmed in `request.json`, full `feedback` markdown threaded through, but failed at ReserveVm (the bug) |
| `run-20260416-052747-28a095` + `run-20260416-052753-c001d1` | Post-fix retry chain, both attempts 7/7 green |

## Decisions worth surfacing

- **In-process retry driver, not NATS-event-driven.** ADR-011 documents the trade. The event-driven variant is additive and deferred until there's a second process that benefits from it.
- **`worker_mode` is in `TaskPacket`, not env-only.** Phase 7 needed to flip mode per request without sed'ing files on the VM. Precedence: per-request > config default > "real".
- **NATS lives in the Farmer repo's `tools/`, downloaded for Jaeger.** `nats-server.exe` is 17MB (under GitHub's file limit); `jaeger.exe` is 123MB and fetched via `tools/download-jaeger.ps1` on first run.
- **Aspire AppHost deferred.** It pulls toward net9 for the host (yak shave today, irrelevant tomorrow) and competes with Jaeger as an OTLP sink. Standalone Jaeger does the job for one-process Farmer.
- **OpenAI provider unchanged.** ADR-006 still holds: MAF + OpenAI gpt-4o-mini for retrospectives. Azure OpenAI is proven (in the prototype) but swapping during the cutover would conflate variables.
- **Three-file anti-drift invariant ([ADR-003](./adr/adr-003-anti-drift-contract.md)) preserved.** EventingMiddleware still writes `events.jsonl` + `state.json`; the new NATS publish is additive, not authoritative.

## Real surprises (good and bad)

**Good:**
- The NATS.Net consumer-side trace propagation gotcha (already known from the prototype) ported over cleanly with a one-line fix per consumer (`msg.Headers?.GetActivityContext() ?? default` → parent ActivityContext).
- The `0-feedback.md` mechanism works without any VM-side change. `worker.sh`'s find-pattern naturally sorts `0-` before `1-`, and `CLAUDE.md` on the VM already promised Claude that retry runs would have feedback as the first prompt. Phase 7 only had to deliver on the promise.
- Six PRs in one session, every one of them landing green, with smoke runs in between -- the discipline of `git stash --keep-index --include-untracked` for atomic commits paid off.

**Bad / surprises:**
- **`IVmManager.ReleaseAsync` was never called by anything.** Phase 5/6 masked it because every dev iteration restarted the host. Phase 7 retry surfaced it on the first 2-attempt chain. Fix was small; the lesson is "exercise lifecycle in the same process, not just across restarts."
- **`InboxTrigger` silently dropped unknown JSON fields.** When I added `worker_mode` to `RunRequest` in PR #7's first commit, smoke tests still ran but the field never made it past `/trigger`. Took a debugging detour to find that `InboxTrigger` (the request-body parser) had a closed schema. Same pattern bit a second time as `CreateRunStage` was overwriting the parsed `RunRequest`.
- **PowerShell `(F)` parens in icacls strings.** Silently failed to grant access; combined with `/inheritance:r` left the SSH key unreadable. Saved as a memory: always use `${env:VAR}` braces when compounding with literal suffixes.
- **OpenSSH probes every key in `~/.ssh/`.** A redundant `vmfarm_ed25519` with bad ACLs aborted SSH auth before the targeted `id_ed25519` was tried. Fixing the targeted key wasn't enough -- had to either fix or delete the sibling.
- **`jaeger.exe` is 123 MB.** Above GitHub's 100 MB hard limit. Hit "remote rejected" on first push attempt; switched to a download script.

## What's NOT done (named, not blocked)

- **`IWorkflowRunner` extraction** -- `RetryDriver` doesn't have an integration test because `WorkflowPipelineFactory` resolves stages by concrete type. Spy stages can't wire through it. Estimated 30 min when someone wants it.
- **`DirectiveSuggestion[]` threading** -- `FeedbackBuilder` only uses `ReviewVerdict.Suggestions` (string list). Structured suggestions from `RetrospectiveResult.DirectiveSuggestions` would need `RunFlowState` + `WorkflowResult` plumbing.
- **NATS-event-driven retry** -- the architectural variant of Phase 7. Subscribe to `farmer.run.retry_requested`, async retry from a `RetryCoordinator` hosted service. Worth it when Farmer has multiple processes or needs fire-and-forget triggers.
- **Engineer a real `Retry` verdict** -- every retry test today used verdict `Accept` (forced via `retry_on_verdicts: ["Accept"]`). The retrospective is too lenient on fake-mode output. Crafting a `worker.sh` failure mode that produces a real `Retry` would be a satisfying close-out demo.
- **Trace context across the SSH boundary** -- `workflow.stage.Dispatch` is a single flat span for the whole Claude CLI session. Propagating `traceparent` via an env file passed to `ssh` would give sub-span visibility.
- **`prototype-nats/` directory rename** -- Windows file handles blocked the rename to `prototype-nats-archived/`; `ARCHIVED.md` was dropped in place. Reboot will clear the lock.
- **OpenAI key in this session's chat transcript.** Rotate when convenient.

## How to pick up where this left off

1. Read [README.md](../README.md) -- already updated for current architecture.
2. Read [CLAUDE.md](../CLAUDE.md) -- session-level gotchas, current ports, build+test+run commands.
3. Read [ADR-010](./adr/adr-010-nats-messaging-cutover.md) (NATS cutover) and [ADR-011](./adr/adr-011-retry-driver.md) (retry driver) -- newest decisions, both load-bearing.
4. `git log --oneline -20 main` -- what shipped today.
5. `.\infra\Farmer.SmokeTrace.ps1` -- one-command pulse check; expects NATS + Jaeger + Farmer.Host running.

The "What's NOT done" list above is the working backlog.
