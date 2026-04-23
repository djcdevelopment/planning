# Phase Demo v2 — Rearchitect desire-trace: .NET 8 + Vue + B2C

**Budget:** 10-12 hours wall clock.
**Goal:** replace the Python+static-HTML desire-trace at `steppe-trace.*.azurecontainerapps.io` with a production-shaped stack at `trace.steppeintegrations.com`:
- **.NET 8 ASP.NET Core** backend (API + static SPA host), containerized for Azure Container Apps
- **Vue 3 + Vite + TypeScript** SPA (chosen over React for smaller footprint — ~30 KB vs ~150 KB for equivalent UI)
- **Azure AD B2C** for identity — friend signs in, his runs are attributed, per-user history
- **Branded URL** at `trace.steppeintegrations.com` via ACA custom domain

Keep Farmer.Host local on the laptop (vm-golden still needs to be on-prem Hyper-V for this demo). The v2 intake + SPA run on Azure; they call through to Farmer via tunnel as before, but now **friend never sees the tunnel URL** (backend auto-discovers it) and **every call is authenticated** (JWT Bearer from B2C).

## Architecture

```
┌─────────────────────────────────────────┐
│ Phone browser                           │
│ trace.steppeintegrations.com            │
│ → B2C sign-in → Vue SPA                 │
└───────────────┬─────────────────────────┘
                │ HTTPS + Bearer JWT
                ▼
┌─────────────────────────────────────────┐
│ Azure Container Apps                    │
│ trace.steppeintegrations.com            │
│                                         │
│  .NET 8 ASP.NET Core                    │
│  ├─ Static SPA host (wwwroot/)          │
│  ├─ JwtBearer auth (B2C tenant)         │
│  ├─ /api/me        (claims)             │
│  ├─ /api/runs      (proxy → Farmer)     │
│  ├─ /api/runs/{id} (proxy)              │
│  ├─ /api/trigger   (proxy + user_id)    │
│  └─ /api/tunnel-url (current ephem url) │
│                                         │
│  Source of tunnel URL: Azure Blob       │
│  (published by start-tunnel.ps1)        │
└───────────────┬─────────────────────────┘
                │ HTTPS via cloudflared tunnel
                │ (ephemeral, auto-published)
                ▼
┌─────────────────────────────────────────┐
│ Laptop                                  │
│ cloudflared → Farmer.Host :5100         │
│ Farmer accepts X-Farmer-User-Id header  │
│ runs/{id}/request.json records user_id  │
│ vm-golden (unchanged)                   │
└─────────────────────────────────────────┘
```

## Non-goals (explicit, tempting but out-of-scope)

- Moving Farmer.Host to Azure (vm-golden tether makes this a multi-day migration — deferred to Phase 11).
- Full user-management UI / profile editing / password reset in-app (use B2C's built-in flows).
- Admin panel / multi-tenant / RBAC beyond "signed-in vs not".
- Production-grade observability (App Insights on SPA + API is stretch; default ACA logs are enough for demo).
- SSR / PWA / offline — SPA with client-side routing is plenty.

## Streams (parallel, orthogonal territory)

### Stream 1 — .NET 8 backend scaffold (~3 hrs)

**Deliverable:** an ASP.NET Core 8 API that serves a Vue SPA, validates B2C JWTs, and proxies to the Farmer tunnel with the caller's identity attached.

**Territory (new repo or branch):**
- Preferred: new branch `v2-rearchitect` on `djcdevelopment/desire-trace`
- `src/SteppeTrace.Api/` — ASP.NET Core 8 web project
  - `Program.cs` — minimal API setup, CORS, static files, JwtBearer
  - `Endpoints/MeEndpoint.cs` — `GET /api/me` returns claims
  - `Endpoints/TriggerEndpoint.cs` — `POST /api/trigger` validates + forwards to Farmer tunnel
  - `Endpoints/RunsEndpoint.cs` — `GET /api/runs`, `GET /api/runs/{id}` proxy to Farmer
  - `Endpoints/TunnelUrlEndpoint.cs` — `GET /api/tunnel-url` returns the current ephemeral URL (read from Azure Blob or env var)
  - `Services/FarmerClient.cs` — `HttpClient`-based proxy with per-request user_id header
  - `Services/TunnelResolver.cs` — reads the current tunnel URL (blob poll or env var)
- `src/SteppeTrace.Api/wwwroot/` — built Vue SPA drops here during Docker build
- `Dockerfile` — multi-stage: (1) `node:20-alpine` builds SPA, (2) `mcr.microsoft.com/dotnet/sdk:8.0` builds API, (3) `mcr.microsoft.com/dotnet/aspnet:8.0` runtime image
- `.github/workflows/build-and-deploy.yml` — optional: CI that builds + pushes to ACR on main push (deferred)
- Tests: unit tests for endpoint logic using WebApplicationFactory.

**Config:**
- `appsettings.json`: B2C tenant + app id + authority URL placeholders
- `appsettings.Production.json` / env vars: actual values

### Stream 2 — Vue 3 SPA (~3 hrs)

**Territory:**
- `src/SteppeTrace.Web/` — Vite + Vue 3 + TypeScript project
  - `src/main.ts` — app mount, MSAL init
  - `src/auth.ts` — `@azure/msal-browser` wrapper
  - `src/api.ts` — typed API client (axios or fetch), auto-attaches Bearer
  - `src/views/SignIn.vue` — landing page, "Sign in" button that triggers B2C redirect
  - `src/views/Home.vue` — authenticated: intake form (mobile-first), user's run history sidebar
  - `src/views/RunDetail.vue` — verdict + directives + retry button
  - `src/components/IdeaForm.vue` — textarea + sensible placeholders, "Build for real" button
  - `src/router.ts` — Vue Router with auth guards
- `package.json` — deps: `vue@3`, `vue-router@4`, `@azure/msal-browser`, `vite`, `typescript`
- Styling: keep it light. Option A: plain CSS (~0 KB). Option B: Pico CSS (~10 KB, class-less). Rec: **Pico CSS** — gorgeous defaults, mobile-friendly, zero config.

**Features shipped:**
- B2C sign-in / sign-out (MSAL redirect flow)
- Token acquisition + refresh (silent)
- Idea textarea + optional audience/context inputs
- "Build for real" button → POST `/api/trigger` with auth header
- Run list (pulled from `/api/runs`) filtered by current user_id
- Tap a run → detail view (verdict, findings, directives, "Apply + re-run")
- Responsive mobile layout (single column, tap-friendly buttons, no accidental zoom on focus)

**Better error UX (addresses tonight's "Failed to fetch"):**
- All errors show: `{error.name}: {error.message}` + a "Copy diagnostic" button that dumps `userAgent, origin, tunnel_url, auth_state` for paste-back support

### Stream 3 — Farmer.Host identity + tunnel auto-publish (~2 hrs)

**Territory (planning repo):**
- `src/Farmer.Host/Program.cs` — add optional `X-Farmer-User-Id` header handling on `/trigger`
- `src/Farmer.Core/Models/RunRequest.cs` — add `UserId` field, passed through from trigger body → request.json
- `src/Farmer.Host/Services/RunDirectoryFactory.cs` — stamp user_id in run metadata
- `infra/start-tunnel.ps1` — after capturing the tunnel URL, `az storage blob upload` to a fixed container/blob (e.g., `tunnel-state/current.json` in a storage account on `rg-farmer-dev`)
- `infra/Azure.Setup.ps1` (new) — one-time script to provision the storage account + container + SAS URL if needed
- Runbook updated.

**Auth posture:** Farmer.Host does NOT validate B2C tokens directly. The ASP.NET API in front (Stream 1) is the boundary. Farmer trusts the `X-Farmer-User-Id` header because it only listens on the tunnel (not public internet). This is demo-grade; hardening is a follow-up.

### Stream 4 — B2C tenant + DNS + ACA custom domain (user-driven, ~1 hr of clicks)

**Must happen in parallel with builders; blocks integration.**

- **Azure AD B2C tenant:** create if you don't have one. Free tier ≤ 50k MAU — plenty.
  - Tenant name: pick short, e.g., `steppetrace.onmicrosoft.com`
  - Create user flow: "Sign up and sign in" (B2C_1_signupsignin)
  - Add identity providers you want (email/password is default; Google/MS optional)
  - App registration for the SPA:
    - Name: `trace-steppe-spa`
    - Supported account types: B2C users
    - Redirect URI type: **Single-page application (SPA)**
    - Redirect URIs: `http://localhost:5173/auth/callback`, `https://trace.steppeintegrations.com/auth/callback`
    - Implicit grant: enable access + id tokens

- **DNS records** (at your registrar, wherever `steppeintegrations.com` DNS is managed):
  - CNAME: `trace.steppeintegrations.com` → `steppe-trace.proudrock-473a42a9.westus2.azurecontainerapps.io` (temporary, points at v1 until v2 is ready; then swap)
  - Async: we'll deploy the v2 to a new ACA name (e.g., `steppe-trace-v2`), test it, then repoint the CNAME when green.
  - TXT: `asuid.trace.steppeintegrations.com` → `<verification_id from Azure portal>` (needed for ACA custom domain binding)

- **ACA custom domain:** in Azure portal, add `trace.steppeintegrations.com` to the container app → get verification token → add TXT → bind → Azure provisions managed cert (~15 min).

## Sequencing (10-hour budget)

```
Hour 0-0.5:  This plan committed + pushed. User kicks off: B2C tenant creation, DNS CNAME + TXT.
Hour 0.5-4:  Three builders run in parallel:
               - Stream 1: .NET 8 backend scaffold
               - Stream 2: Vue 3 SPA
               - Stream 3: Farmer identity + tunnel auto-publish
Hour 4-5.5:  Integration — wire SPA → API, API → Farmer, tunnel URL flowing
Hour 5.5-7:  ACA deploy + bind custom domain + deploy v2 container
Hour 7-9:    End-to-end smoke: sign in from phone, submit idea, run lands on laptop, reveal UI shows it
Hour 9-10:   Bug fixing from smoke findings
Hour 10-12:  Buffer — mobile UX polish, dress rehearsal with seed ideas, runbook update
```

## Risk register

| Risk | Mitigation |
|---|---|
| B2C SPA redirect URIs misconfigured → login loops | Test with localhost first; only add production URI after local flow works |
| ACA custom domain SSL cert provisioning delay | Bind early (Hour 2); cert usually < 15 min but can take longer. Keep old URL working as fallback until cutover |
| .NET + Vue Docker image size / cold start on ACA | Multi-stage build + chiseled Ubuntu base image; target < 200 MB. Scale-to-zero is OK for demo, ~1s cold start acceptable |
| Tunnel auto-publish path: laptop can't write to Azure Blob during start-tunnel.ps1 | Fallback: write to a public GitHub raw file in the desire-trace repo; backend fetches that URL |
| JWT validation on ASP.NET side fails (tenant config mismatch) | Keep anonymous fallback for the first demo if auth breaks — feature flag via env var `REQUIRE_AUTH=false` |
| Breaking the v1 during v2 build | v2 deploys to a separate ACA name (`steppe-trace-v2`), DNS stays on v1 until cutover. Zero-downtime swap. |
| M365 DNS (wherever steppeintegrations.com lives) doesn't support CNAME on subdomain | Very rare but possible. Fallback: use A record to Azure's anycast IP, or front-door |

## Your-action checklist (blocking items)

These must happen ASAP so builders aren't blocked at integration time:

1. **Create B2C tenant** (if none):
   - Azure Portal → Create a resource → Azure AD B2C → create new tenant
   - Directory name: `steppetrace` (yields `steppetrace.onmicrosoft.com`)
   - Associate the tenant to your current subscription

2. **B2C app registration + user flow** (once tenant is up):
   - In the B2C tenant: App registrations → New → SPA type
   - Record: **Client ID**, **Tenant ID**
   - User flows → New → Sign up and sign in (v2) → name it `B2C_1_signupsignin`
   - Identity providers: local account (email) is fine for demo

3. **DNS records** (at your domain registrar):
   - Find where `steppeintegrations.com` DNS is managed (look for authoritative NS records)
   - Add CNAME: `trace` → `steppe-trace.proudrock-473a42a9.westus2.azurecontainerapps.io`
   - We'll add a TXT record after I query Azure for the verification token (Hour ~2)

4. **Tell me:**
   - B2C tenant ID
   - B2C client ID (SPA app)
   - User flow name (probably `B2C_1_signupsignin`)
   - Which registrar holds steppeintegrations.com DNS

With those four things I can fully drive the rest.

## Success criteria (how we know we shipped)

- [ ] `https://trace.steppeintegrations.com/` loads the Vue SPA over HTTPS
- [ ] "Sign in" button triggers B2C redirect, returns with a valid JWT
- [ ] Signed-in user can submit an idea; run dispatches on laptop; run history shows it
- [ ] Different signed-in user sees only their own runs (basic tenant isolation)
- [ ] Tunnel URL auto-discovered — user never pastes
- [ ] Runbook for demo day updated to reflect new URL + new flow
- [ ] Phase-demo-v2-retro.md written after first real cross-device run

## Rollback plan

If at Hour 10 v2 isn't ready or broken in production:
- DNS CNAME `trace` is still pointing at the v1 ACA (we never moved it). v1 keeps working.
- v2 stays at `steppe-trace-v2.<hash>.azurecontainerapps.io` for continued debugging.
- Demo falls back to tonight's working setup (v1 + ephemeral tunnel URL pasted).

This is the safety net that makes the 10-hour sprint reasonable — worst case we land at the same place we're at tonight.
