# Architecture Decision Records

Load-bearing design choices that shaped Farmer, in the format of lightweight ADRs. Each record is short, explains the problem the decision solved, the decision itself, and the consequences. New sessions picking up mid-phase should read these before touching anything non-trivial — they encode the *why* that commit messages and docs only hint at.

Format: loosely MADR. Each ADR has `Status`, `Context`, `Decision`, `Consequences`. No fancy tooling, no numbering tooling, just markdown.

## Index

| # | Status | Title |
|---|---|---|
| [ADR-001](./adr-001-externalized-runtime.md) | Accepted (Phase 5) | Externalized runtime directory — runtime state lives outside the repo |
| [ADR-002](./adr-002-file-first-entry.md) | Accepted (Phase 5) | Filesystem is the source of truth; InboxWatcher is the primary entry path |
| [ADR-003](./adr-003-anti-drift-contract.md) | Accepted (Phase 5) | events.jsonl, state.json, and result.json must agree for every completed run |
| [ADR-004](./adr-004-workflow-pipeline-factory.md) | Accepted (Phase 5) | WorkflowPipelineFactory with per-run fresh middleware, no singleton state |
| [ADR-005](./adr-005-farmer-agents-blast-radius.md) | Accepted (Phase 6) | Farmer.Agents as the isolated blast radius for MAF + LLM SDK dependencies |
| [ADR-006](./adr-006-openai-over-anthropic-maf.md) | Accepted (Phase 6) | OpenAI via MAF for the host-side retrospective agent (stable, typed structured output) |
| [ADR-007](./adr-007-qa-as-postmortem.md) | Accepted (Phase 6) | QA runs as a post-mortem, never as a gate. Verdict is metadata. |
| [ADR-008](./adr-008-workers-full-dangerous-mode.md) | Accepted (Phase 6) | Workers run Claude CLI in full dangerous mode on the VM sandbox |
| [ADR-009](./adr-009-hybrid-maf-host-cli-vm.md) | Accepted (Phase 6) | Hybrid architecture: MAF for host-side agents, Claude CLI for VM-side workers |

## When to add a new ADR

Add a new ADR when you make a decision that:
- Affects code organization across projects
- Picks one third-party dependency over another
- Changes a contract that other components rely on
- Involves a trade-off between two defensible options (not "the obvious choice")

Do NOT add an ADR for:
- Refactorings that don't change behavior
- Bug fixes
- Dependency version bumps (unless the version jump changes contracts)
- "How should I write this method" discussions

If in doubt: write it. ADRs are cheap, the cost of forgetting a load-bearing decision is high.
