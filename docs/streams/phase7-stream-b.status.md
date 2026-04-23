# Phase 7 Stream B status — Azure OpenAI swap

Branch: `claude/phase7-stream-b-azure-openai`
Worktree: `C:\work\iso\planning-stream-b`

## Signals

- `[RESEARCH-DONE]` 2026-04-22

  Read CLAUDE.md, ADR-006, `MafRetrospectiveAgent.cs`, `OpenAISettings.cs`,
  `Farmer.Agents.csproj`, both `appsettings.json` files, and
  `ServiceCollectionExtensions.cs`. Callers of `ApiKey` / `QaModel` /
  `ResolveApiKey` across the tree:

  - `src/Farmer.Agents/MafRetrospectiveAgent.cs` (will rewrite)
  - `src/Farmer.Tests/Models/Phase6ModelTests.cs` three tests to replace:
    - `OpenAISettings_ResolveApiKey_PrefersExplicitValue`
    - `OpenAISettings_ResolveApiKey_FallsBackToEnvironment`
    - `OpenAISettings_HasGpt4oMiniAsDefaultQaModel`
  - `src/Farmer.Host/appsettings.json` `Farmer:OpenAI` block (will update)

  No `phase7-close-the-loop.md` actually exists in this worktree — the
  prompt's Resolved prerequisites values are the authoritative source
  (endpoint, deployment, auth). Proceeding with those.

  No `docs/streams/` exists yet — created by this file.

- `[DESIGN-READY]` 2026-04-22

  Target diff shape:

  1. `src/Farmer.Core/Config/OpenAISettings.cs`
     - Remove `ApiKey`, `QaModel`, `ResolveApiKey()`.
     - Add `Endpoint` (string, empty default), `DeploymentName` (string,
       empty default). Keep `MaxOutputTokens` + `TimeoutSeconds` (harmless,
       still advisory).
     - Rewrite xmldoc for Entra / `DefaultAzureCredential` story.

  2. `src/Farmer.Agents/Farmer.Agents.csproj`
     - `dotnet add package Azure.AI.OpenAI` (latest stable)
     - `dotnet add package Azure.Identity` (latest stable)
     - `Microsoft.Agents.AI.OpenAI` stays at 1.1.0.

  3. `src/Farmer.Agents/MafRetrospectiveAgent.cs`
     - `TryBuildAgent()` builds `AzureOpenAIClient(endpoint, DefaultAzureCredential)`
       then `.GetChatClient(deployment)` → `AsIChatClient().AsAIAgent(...)`.
     - Skip-agent condition becomes missing endpoint or deployment name
       (no dual-mode fallback). Log+return null as before; stage still
       AutoPasses per ADR-007.
     - Drop the `OpenAI` using, add `Azure.AI.OpenAI` + `Azure.Identity`.

  4. `src/Farmer.Host/appsettings.json` `Farmer:OpenAI` block:
     ```json
     "OpenAI": {
       "Endpoint": "https://farmer-openai-dev.openai.azure.com/",
       "DeploymentName": "gpt-4.1-mini",
       "MaxOutputTokens": 2048,
       "TimeoutSeconds": 60
     }
     ```

  5. `src/Farmer.Host/appsettings.Development.json`
     - Add matching `Farmer:OpenAI` block with the same endpoint/deployment
       (dev and prod both hit `farmer-openai-dev` — the resource name.)

  6. `src/Farmer.Tests/Models/Phase6ModelTests.cs`
     - Replace the three tests above with tests for the new shape:
       - Endpoint default is empty.
       - DeploymentName default is empty.
     - Keep `RetrospectiveSettings_*` tests untouched.

  7. `docs/adr/adr-006-openai-over-anthropic-maf.md`
     - Append "### Update 2026-04-22 — deployment path moved to Azure OpenAI".
     - Original decision (MAF + OpenAI SDK over MAF + Anthropic preview)
       stands. What changed: the underlying HTTP transport is now
       `AzureOpenAIClient` w/ `DefaultAzureCredential` instead of
       `OpenAIClient` w/ API key. MAF + OpenAI-SDK still the binding.
       This is additive — no API key, Entra only, Entra role already
       assigned.

  Commit plan: two commits for easy review.
  1. config + NuGet + appsettings + ADR update + tests (green on its own
     because the agent still compiles against old API until commit 2).
  2. agent rewrite.

  Actually, commit 1 would NOT be green — removing `ApiKey`/`QaModel`/
  `ResolveApiKey` from `OpenAISettings` breaks `MafRetrospectiveAgent`'s
  compile. So: single atomic commit. Revisiting plan: squashed commit.

- Build: `dotnet build src/Farmer.sln` — 0 warnings, 0 errors.
- Tests: `dotnet test src/Farmer.sln` — 143 green (138 unit + 5 integration).
  Baseline count was 138 unit (CLAUDE.md's "128" was stale — pre-stash test
  run on the untouched HEAD also returned 138). Net change 0: removed 3
  tests (`ResolveApiKey_PrefersExplicitValue`, `ResolveApiKey_FallsBackToEnvironment`,
  `HasGpt4oMiniAsDefaultQaModel`), added 3 tests (`EndpointDefaultsToEmpty`,
  `DeploymentNameDefaultsToEmpty`, `CanBindEndpointAndDeployment`).
- Runtime smoke: not executed.

- `[COMPLETE]` <pending commit sha, filled in by commit step>.
