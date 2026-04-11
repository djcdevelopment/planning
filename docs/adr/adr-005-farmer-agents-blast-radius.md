# ADR-005: Farmer.Agents as the isolated blast radius for AI SDK dependencies

**Status:** Accepted (Phase 6)
**Date:** 2026-04-11
**Superseded by:** none

## Context

Phase 6 introduces the retrospective agent — a host-side LLM call that reviews each completed run and produces `qa-retro.md` + `review.json` + `directive-suggestions.md`. The agent is built on the Microsoft Agent Framework (MAF), which is itself built on `Microsoft.Extensions.AI` abstractions, which wraps provider-specific SDKs (OpenAI, Anthropic, Azure Foundry, etc.).

This is the first time Farmer pulls AI SDKs into its dependency graph. MAF v1.0 shipped April 3, 2026 — brand new. The provider-specific MAF packages are a mix of stable (`Microsoft.Agents.AI.OpenAI` 1.1.0) and preview (`Microsoft.Agents.AI.Anthropic` 1.0.0-preview.251125.1). The underlying provider SDKs are mature individually but churn against MAF's abstractions in minor ways.

`Farmer.Core` until this point has been: `Microsoft.Extensions.Logging.Abstractions` and `Microsoft.Extensions.Options`. Both 8.x stable. Zero AI dependencies. The whole codebase compiles cleanly on the .NET 8 GA line with no preview packages.

If we drop MAF dependencies into `Farmer.Core` directly, two things happen:
1. Core's NuGet graph grows by dozens of transitive packages on the 10.x line
2. Any future MAF churn forces a rebuild / test re-run of every project that references Core

## Decision

**Create `Farmer.Agents` as a new project whose sole purpose is to be the blast radius for every AI SDK dependency.**

Rules:
- `Farmer.Agents` is the **only** project in the solution that imports types from `Microsoft.Agents.AI.*`, `Microsoft.Extensions.AI.*`, `OpenAI`, or any future provider SDK.
- `Farmer.Core` owns the `IRetrospectiveAgent` **interface** (plus its POCO context/result types). No MAF types in its signatures.
- `Farmer.Host` references `Farmer.Agents` via a single extension method: `services.AddFarmerAgents(config)`. Host code never sees MAF types directly.
- If the MAF API or any provider SDK makes breaking changes, the blast radius is this one project. Every other project still compiles.

Practical layout:
```
src/
├── Farmer.Core/
│   ├── Contracts/IRetrospectiveAgent.cs      ← interface lives here
│   ├── Config/OpenAISettings.cs              ← config POCO lives here
│   └── Config/RetrospectiveSettings.cs
├── Farmer.Agents/                            ← new project
│   ├── Farmer.Agents.csproj                  ← MAF + OpenAI + MS.Extensions.AI packages
│   ├── MafRetrospectiveAgent.cs              ← IRetrospectiveAgent implementation
│   ├── Prompts/RetrospectivePrompt.cs
│   └── ServiceCollectionExtensions.cs        ← AddFarmerAgents
└── Farmer.Host/
    └── Program.cs                            ← calls AddFarmerAgents(config), no MAF types
```

## Consequences

**Positive:**
- `Farmer.Core` stays pure .NET 8 stable. Its NuGet graph is unchanged.
- If MAF's Anthropic provider (currently preview) matures and we want to add it alongside OpenAI, that's a single-project change.
- The `IRetrospectiveAgent` interface is provider-agnostic. Swapping OpenAI for Anthropic, or adding a fallback chain, or adding a second agent, all happen inside `Farmer.Agents`.
- Testing: the interface lives in Core, so tests can use a `FakeRetrospectiveAgent` without touching the MAF project at all. MAF-integration tests (when we need them) live in `Farmer.Agents.Tests` or use the `internal` test-seam constructor on `MafRetrospectiveAgent`.

**Negative:**
- One extra project in the solution. One extra `ProjectReference` from Host.
- A boundary contract has to be maintained: code review has to catch any `using Microsoft.Agents.AI;` that accidentally lands in Core or Host. A linter rule could enforce this later if discipline slips.

## Related

- [ADR-006](./adr-006-openai-over-anthropic-maf.md) — the specific provider pivot this project isolates us from
- `src/Farmer.Agents/Farmer.Agents.csproj` — the project file with pinned MAF packages
- `src/Farmer.Agents/ServiceCollectionExtensions.cs` — the single entry point Host uses
- `src/Farmer.Core/Contracts/IRetrospectiveAgent.cs` — the contract that keeps Core clean
