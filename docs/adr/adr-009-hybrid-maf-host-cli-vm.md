# ADR-009: Hybrid architecture — MAF for host-side agents, Claude CLI for VM-side workers

**Status:** Accepted (Phase 6)
**Date:** 2026-04-11
**Superseded by:** none

## Context

Phase 6 has two distinct places where "an LLM does something":

1. **The VM worker.** Reads prompt files, does whatever Claude thinks the prompts want, writes manifest + artifacts. Runs on an Ubuntu VM. Needs full filesystem access, arbitrary Bash, WebFetch, git, package managers, the ability to build and run code. Autonomous. Produces side effects on disk. Output can be anything from "a single file changed" to "a full compiled server binary".

2. **The host retrospective agent.** Reads the worker's output files, produces a structured verdict + retro + directive suggestions. Runs in-process inside `Farmer.Host` on the Windows control plane. No filesystem side effects beyond writing the review artifacts. Completely stateless per call. Output is a JSON shape that matches a known C# type.

These are two completely different problems. The first needs an autonomous agent with broad tool access. The second needs a constrained, typed, structured-output call with good observability.

There is a tempting simplification where we make them the same: either run everything through Claude CLI (one provider, one codepath) or run everything through MAF (one abstraction, one set of OTel spans). Both are wrong for different reasons.

**Can't run the worker through MAF** — MAF's core abstraction is `IChatClient`, which is an in-process chat call. Giving it a "run arbitrary bash" tool is possible but everything happens in the host's .NET process. The user's whole point of using VMs is sandbox isolation and the ability to download/build/run whatever (see [ADR-008](./adr-008-workers-full-dangerous-mode.md)). Moving that into the host process collapses the isolation.

**Can't run the retrospective through Claude CLI** — Claude CLI is a CLI. Invoking it from the host for every retrospective means shelling out to a subprocess, parsing its stdout, hoping the stream-json format hasn't changed, and building our own OTel spans around an external process. That's exactly the plumbing MAF was built to eliminate. Plus we'd miss out on typed structured output (`RunAsync<T>`).

## Decision

**Hybrid: MAF + OpenAI (stable) for host-side agents, Claude CLI (full dangerous mode) for VM-side workers.**

| | Host (Windows .NET process) | VM (Ubuntu sandbox) |
|---|---|---|
| What runs | `Farmer.Host`, `Farmer.Agents`, `Farmer.Core` | `worker.sh` + Claude CLI |
| LLM provider | OpenAI via MAF (see [ADR-006](./adr-006-openai-over-anthropic-maf.md)) | Claude via `claude` binary directly |
| Interface | `AIAgent` + `IChatClient` + typed `RunAsync<T>` | shell subprocess + stream-json |
| Isolation | Same process as the orchestrator | VM sandbox |
| Filesystem access | Writes to `runs/{run_id}/` only | Full write access to `~/projects/` |
| Tool access | None (agent just reads + writes JSON) | Everything Claude CLI can do |
| OTel | Native via MAF's `Experimental.Microsoft.Agents.AI` | Captured into `execution-log.txt`, forwarded via eventing |

The retrospective agent is a `ChatClientAgent` built from MAF's `AsAIAgent` extension. The worker is a bash script that invokes `claude -p "..." --dangerously-skip-permissions --output-format stream-json --verbose --no-session-persistence --max-turns 500`. They share nothing. They communicate through files (request.json in, manifest/summary/artifacts out).

## Consequences

**Positive:**
- **Each problem gets the right tool.** Autonomous work on a VM is what Claude CLI was built for. Structured reviewer calls from .NET are what MAF was built for. Forcing either tool outside its lane would be worse.
- **Isolation is real.** A rogue worker can't touch host state. A misbehaving retrospective can't crash the worker. The two layers are separated by the VM boundary and the file-based contract.
- **Observability is actually unified** via OpenTelemetry, despite the provider split. MAF emits its own spans under `Experimental.Microsoft.Agents.AI`; our `FarmerActivitySource` wraps them with `workflow.stage.Retrospective`. The worker's per-prompt telemetry lands in `execution-log.txt` and `output/telemetry.json`, and future phases can replay that into the same Aspire trace backend. See [docs/phase5-pattern.md](../phase5-pattern.md) on the anti-drift rule.
- **Two providers ≠ two code paths.** `IRetrospectiveAgent` is the only MAF contract. `ISshService.ExecuteAsync` + `IMappedDriveReader` is the only Claude-CLI contract. They share nothing because they model different things.

**Negative:**
- **Two credential systems.** `OPENAI_API_KEY` for the host agent, SSH key for host-to-VM, Claude CLI's own auth on the VM. Three credentials. Documented in README prerequisites.
- **Two token spends.** Every run pays for both Claude CLI usage (worker) and OpenAI usage (retrospective). Monitored via worker-side `output/telemetry.json` and host-side `cost-report.json` (Phase 6 expands this to include QA token usage).
- **Cognitive load.** A reader has to understand why the project is MAF-forward but also uses Claude CLI. ADRs 006, 008, and this one try to answer that in writing. The short version: the VM is a sandbox, the host is an orchestrator, they're different problems.
- **We can't pretend to be "all Microsoft" or "all Anthropic".** Both marketing angles lose. The honest angle is "MAF where MAF fits, Claude CLI where Claude CLI fits" — which is the competitive differentiator this project is actually built around.

## Related

- [ADR-005](./adr-005-farmer-agents-blast-radius.md) — the project boundary that makes this hybrid possible
- [ADR-006](./adr-006-openai-over-anthropic-maf.md) — why OpenAI on the host specifically
- [ADR-007](./adr-007-qa-as-postmortem.md) — how the retrospective agent is used
- [ADR-008](./adr-008-workers-full-dangerous-mode.md) — why the VM worker runs the way it does
- `src/Farmer.Core/Workflow/Stages/DispatchStage.cs` — the host side of the host→VM handoff
- `src/Farmer.Worker/worker.sh` — the VM side (Phase 6 commit 4, TBD)
- `src/Farmer.Agents/MafRetrospectiveAgent.cs` — the host-side agent
