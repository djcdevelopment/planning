# ADR-006: OpenAI via MAF for the host-side retrospective agent

**Status:** Accepted (Phase 6)
**Date:** 2026-04-11
**Superseded by:** none

## Context

Phase 6 plans a host-side retrospective agent that reviews every completed run's output and produces a structured verdict + forward-looking directive suggestions. The user's stated framing is "use Microsoft Agent Framework as much as possible" — MAF is a competitive differentiator, the target audience is Azure/.NET developers who already live in the Microsoft stack, and learning MAF by doing is part of the point.

The obvious default was `Microsoft.Agents.AI.Anthropic` because (a) Farmer's VM worker already runs Claude CLI, (b) Claude Haiku is a good fit for cheap fast structured retrospectives, and (c) keeping the whole system on Anthropic feels consistent.

Reality intruded. Implementing the Anthropic path turned up:

1. **The preview package status.** `Microsoft.Agents.AI.Anthropic` is `1.0.0-preview.251125.1` as of April 2026 — still preview even after MAF v1.0 shipped.
2. **API mismatch with the research.** The research agent described `AsAIAgent(model, name, instructions)` on `AnthropicClient`. The actual method is `CreateAIAgent` on `IAnthropicClient`, and the `AnthropicClient` constructor takes `ClientOptions` not a bare string (two-character typo: `new AnthropicClient(apiKey)` should be `new AnthropicClient { APIKey = apiKey }`).
3. **No documented structured output path.** The MS Learn Anthropic docs don't show `ChatResponseFormat.Json` or any equivalent. The planned approach was a hand-rolled `TryParseVerdict` method with code-fence stripping and per-field validation — roughly 120 lines of fragile parser code.

At the point where the typo surfaced, the user said: *"if microsoft agent framework has better support for openAI remote API, so be it, i don't care. i just need the data to flow now"*.

A targeted probe against the OpenAI MAF docs turned up a much cleaner path:

- `Microsoft.Agents.AI.OpenAI` is **1.1.0 stable**, not preview. Same v1.1 line as `Microsoft.Agents.AI` core.
- **Typed structured output via `RunAsync<T>()`**: define a `RetrospectiveDto` POCO, call `agent.RunAsync<RetrospectiveDto>(userMessage)`, get an `AgentResponse<RetrospectiveDto>` where `.Result` is already a validated instance. The SDK enforces the schema. No hand-rolled parser.
- **Token usage is clean**: `response.Usage?.InputTokenCount` / `.OutputTokenCount`.
- **`gpt-4o-mini` is the Haiku equivalent** for cost and speed as of April 2026: $0.15/M input, $0.60/M output, native structured output support, 128K context.

## Decision

**Pivot the host-side retrospective agent to OpenAI via `Microsoft.Agents.AI.OpenAI` 1.1.0.** The worker on the VM stays Claude CLI in full dangerous mode (see [ADR-008](./adr-008-workers-full-dangerous-mode.md) and [ADR-009](./adr-009-hybrid-maf-host-cli-vm.md)) — that's a separate decision about VM sandbox autonomy, unrelated to which LLM the host-side agent calls.

The implementation:
```csharp
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using OpenAI;

var openAiClient = new OpenAIClient(apiKey);
var openAiChatClient = openAiClient.GetChatClient("gpt-4o-mini");
AIAgent agent = openAiChatClient.AsIChatClient().AsAIAgent(
    instructions: RetrospectivePrompt.SystemInstructions,
    name: "FarmerRetrospectiveAgent");

AgentResponse<RetrospectiveDto> response = await agent.RunAsync<RetrospectiveDto>(userMessage, ct);
var dto = response.Result;  // validated by the SDK against RetrospectiveDto's shape
```

`RetrospectiveDto` is a plain C# POCO with `JsonPropertyName` attributes matching the fields we want from the agent (verdict, risk_score, qa_retro_markdown, findings, suggestions, directive_suggestions). The SDK handles schema generation, enforcement, and parsing.

## Consequences

**Positive:**
- **~120 lines of hand-rolled parser code is deleted** before it's written. `MafRetrospectiveAgent.cs` ended up around 240 lines instead of the ~360 the Anthropic path would have needed.
- **Structured output is trustworthy.** No code-fence stripping, no tolerant parsing, no manual per-field validation. The SDK guarantees the shape.
- **Stable package line.** No preview packages in `Farmer.Agents` as of Phase 6. One less risk variable.
- **Better documentation.** The OpenAI MAF docs have three distinct structured-output patterns with full code samples. The Anthropic docs have none.
- **Token accounting is first-class** via `AgentResponse.Usage`, not something we have to reverse-engineer from response metadata.

**Negative:**
- **Anthropic bias in the codebase.** We're using Claude on the VM worker and OpenAI on the host. Some readers will assume we should use one or the other everywhere. ADR-008 and ADR-009 document why the split is intentional.
- **OpenAI API spend** for every retrospective. Small per-run (Haiku-class usage) but it adds up if Farmer runs a lot. We monitor via `farmer.qa.agent_call_failures_total` and raw token counts in the cost report.
- **Account for which API key is which.** `OPENAI_API_KEY` for the retrospective agent, SSH key for the VM worker, no API key at all for the VM's Claude CLI (that's authed separately per VM). Three credential systems. Documented in [README.md](../../README.md) prerequisites.

## Related

- [ADR-005](./adr-005-farmer-agents-blast-radius.md) — why the pivot is cheap (one project changed)
- [ADR-009](./adr-009-hybrid-maf-host-cli-vm.md) — why the host uses MAF/OpenAI while the VM uses Claude CLI
- `src/Farmer.Agents/MafRetrospectiveAgent.cs` — the implementation
- `src/Farmer.Core/Config/OpenAISettings.cs` — the config POCO (renamed from `AnthropicSettings`)
- Commit message on `e6655b2 Phase 6: Farmer.Agents project + IRetrospectiveAgent + MAF OpenAI provider` — the commit where the pivot landed

## Update 2026-04-22 — deployment path moved to Azure OpenAI + Entra

**Status:** Additive. The original decision (MAF + OpenAI-SDK over MAF + Anthropic-preview) still stands. Only the underlying HTTP transport changed.

### What changed

The retrospective agent's HTTP client moved from the public OpenAI endpoint (+ `OPENAI_API_KEY`) to Azure OpenAI (+ Entra via `DefaultAzureCredential`). Concretely:

```csharp
// before (Phase 6)
var openAiClient = new OpenAIClient(apiKey);
var chatClient = openAiClient.GetChatClient("gpt-4o-mini");

// after (Phase 7)
var azureClient = new AzureOpenAIClient(new Uri(endpoint), new DefaultAzureCredential());
var chatClient = azureClient.GetChatClient("gpt-4.1-mini");
```

`AzureOpenAIClient` (from `Azure.AI.OpenAI`) shares a base class with `OpenAIClient` and the returned chat client flows through `AsIChatClient().AsAIAgent(...)` unchanged. **The MAF binding is still `Microsoft.Agents.AI.OpenAI` 1.1.0.** Typed structured output via `RunAsync<T>()` is untouched — the whole value prop of the original pivot (no hand-rolled JSON parser) is preserved.

### Why this is additive, not superseding

The question ADR-006 answered — "which MAF provider library do we use on the host?" — still has the same answer: `Microsoft.Agents.AI.OpenAI`. We did not reopen Anthropic vs. OpenAI. We swapped _transports_ underneath a fixed provider binding. The pros and cons in the original Consequences section still apply.

What this update does reshuffle:

- **No more OpenAI API key.** The "three credential systems" note in the Negative section drops to two: Entra on the host, SSH on the VM (Claude CLI on the VM is authed separately per VM, untouched). The PR #16 OpenAI-key leak incident that triggered the Phase 7 secret scanner is now mechanically impossible for this code path — there is no key to leak.
- **No more per-token spend on a credit-card-billed OpenAI account.** Spend moves to the Azure subscription; cost report token accounting still works (Azure OpenAI returns the same `Usage` shape).
- **Different failure modes.** Missing API key is replaced by "missing Entra token" or "missing role assignment" or "wrong tenant." The skip path (AutoPass per ADR-007) still applies — retrospectives are postmortem, never gating.

### Config shape change

`OpenAISettings` lost `ApiKey` and `QaModel`, gained `Endpoint` and `DeploymentName`. `ResolveApiKey()` is gone. `Farmer:OpenAI` in `appsettings.json`:

```jsonc
// before
{ "ApiKey": "", "QaModel": "gpt-4o-mini", ... }
// after
{
  "Endpoint": "https://farmer-openai-dev.openai.azure.com/",
  "DeploymentName": "gpt-4.1-mini",
  ...
}
```

### Prerequisites

- Azure OpenAI resource provisioned. For Farmer development: `farmer-openai-dev` in the dev subscription, with a deployment named `gpt-4.1-mini` backed by `gpt-4.1-mini` model version `2025-04-14`.
- The Host process's identity (developer's Entra user locally; managed identity in Azure) has the **Cognitive Services OpenAI User** role on the resource.
- Developer signed in via `Connect-AzAccount` so `AzurePowerShellCredential` in the `DefaultAzureCredential` chain resolves a token. In Azure, managed identity wins instead.

### Related

- `src/Farmer.Agents/MafRetrospectiveAgent.cs` — the `TryBuildAgent` rewrite.
- `src/Farmer.Core/Config/OpenAISettings.cs` — the new config shape.
- `docs/phase7-close-the-loop.md` — the Phase 7 plan that this work closes.
