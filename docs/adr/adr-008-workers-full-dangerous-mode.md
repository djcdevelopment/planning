# ADR-008: Workers run Claude CLI in full dangerous mode on the VM sandbox

**Status:** Accepted (Phase 6)
**Date:** 2026-04-11
**Superseded by:** none

## Context

Phase 6 replaces the Phase 5 fake worker with a real `worker.sh` that invokes Claude CLI on the VM to do actual work per prompt file. Claude CLI has a long list of flags controlling what it's allowed to do:

- `--dangerously-skip-permissions` — don't prompt for any tool approval. Required for unattended / scripted runs.
- `--tools Read,Write,Edit,Bash,Glob,Grep,WebFetch,...` — allowlist of which tools Claude can use.
- `--max-turns N` — hard limit on agentic iterations.
- `--no-session-persistence` — don't save session state between invocations.

A conservative implementation of worker.sh would lock all of these down: restrictive tool allowlist, max-turns ~30, no WebFetch, no arbitrary Bash. That's the safest default, and it produces predictable output that's easy to QA.

The user pushed back on this hard. Two direct quotes:

> "I leave them 100% control on that VM and, you know, quite a good amount of space and no time limit. So if they want to download, you know, a couple different frameworks and build their own server, I ran this. I've run these tests manually before I started building the system, and that exactly happened. Like some of them packaged a zip file up, some of them gave me a directory, one of them fucking built an entire web server. And I think that in itself is good information."

> "when i ran my sample it was full --dangerous mode, that's 90% of the reason we're using VMs, full send. we'll keep a close eye on the output before letting large batch or unsupervised runs go"

The user's framing is that variance in worker output is *information*, not a bug. A worker that interprets "add an API endpoint" as "download a framework, scaffold a project, build a server, package it up" is giving us valuable data about how Claude thinks about that work request. Locking down the tool set removes that signal. The VM is the sandbox; that's literally why we use VMs.

## Decision

**Workers invoke Claude CLI in full dangerous mode with no tool allowlist and a very high max-turns ceiling.** The exact command line used by `worker.sh`:

```bash
claude -p "$(cat "$PROMPT_FILE")" \
  --dangerously-skip-permissions \
  --output-format stream-json \
  --verbose \
  --no-session-persistence \
  --max-turns 500
```

Explicitly:
- **`--dangerously-skip-permissions`** — yes, always.
- **No `--tools` flag** — Claude gets every tool the CLI supports (Read, Write, Edit, Bash, Glob, Grep, WebFetch, MCP tools if available, everything).
- **`--max-turns 500`** — effectively unlimited; the 30-minute `SshDispatchTimeoutMinutes` ceiling in `FarmerSettings` is the real upper bound.
- **`--no-session-persistence`** — each prompt is a fresh context. State that needs to persist across prompts should live on disk (git, files), not in Claude's session memory.
- **`--output-format stream-json` + `--verbose`** — capture every turn, tool call, and token for post-run telemetry.

The VM is the isolation boundary. A rogue worker can only damage files within `~/projects/` on the VM (which gets cleaned between runs by `IVmManager.CleanWorkspaceAsync`), can only talk to hosts the VM's network allows, and can only consume VM resources. The host machine is unaffected by anything the worker does.

What the worker *outputs* is captured richly: `Manifest.Outputs[]` classifies every produced artifact as `file | directory | archive | binary | report`, so a worker that packages a zip shows up as an `Archive` output, a worker that builds a server shows up as a `Binary` plus a set of `File`s, a worker that produces a directory tree shows up as a `Directory`. See [ADR-003](./adr-003-anti-drift-contract.md) and the Phase 6 `Manifest` model for the exact shape.

## Consequences

**Positive:**
- **Variance is captured, not suppressed.** The user's observation that workers sometimes produce zip files, directories, or entire servers becomes a feature: `Manifest.Outputs[]` accommodates all of them. The retrospective agent (ADR-007) gets to see all of it and comment.
- **Matches the user's stated philosophy.** "Full send" is a design principle, not a compromise.
- **Simpler worker.sh.** No tool allowlist logic, no per-prompt flag tweaking, no mode switching.
- **The sandbox is the VM.** Trust boundary is obvious: the host trusts nothing the worker produces until Collect copies it into the immutable run directory.

**Negative:**
- **Unsupervised large-batch runs are deferred.** The user explicitly said we monitor output manually for now. A worker that goes off the rails in a 500-turn session could run for 30 minutes, burn tokens, and produce nothing useful. We notice, we kill the VM, we iterate on prompts or CLAUDE.md.
- **Worker behavior is harder to predict.** A QA/debugging session may find that the same prompt produces wildly different outputs across runs. Feature, not bug — but new contributors may find it disorienting.
- **Security model depends on VM isolation.** If the VM isn't actually isolated (misconfigured network, shared filesystem, exposed services), a rogue worker could do real damage. We document this in the VM setup instructions.
- **Network requirements.** The worker needs outbound HTTP to fetch packages, download frameworks, etc. The VM must have internet access. Air-gapped deployments would need a different strategy.

## Related

- [ADR-009](./adr-009-hybrid-maf-host-cli-vm.md) — why the VM runs Claude CLI specifically, and why the host uses MAF/OpenAI
- [ADR-007](./adr-007-qa-as-postmortem.md) — how the retrospective agent reviews whatever the worker produced
- `src/Farmer.Worker/worker.sh` — the actual script (Phase 6 commit 4, TBD)
- `src/Farmer.Worker/CLAUDE.md` — the VM-side instructions for Claude
- `src/Farmer.Core/Models/Manifest.cs` — `Outputs[]` with the `OutputKind` classifier
