# Phase Demo — Friend-tomorrow shakedown

**Goal:** friend types a rough idea on his phone → Farmer builds + reviews it → we pull up the laptop over drinks and show the trace + artifacts + retro + iterate button.

**Target: demo-ready tonight. Dress rehearsal before sleep. Runbook committed so you wake up to a checklist.**

## Topology

```
[friend's phone]                  [Azure]                     [laptop]
 trace.steppe...     ─HTTPS─▶    desire-trace UI    ─HTTPS─▶  cloudflared tunnel
                                 (hosts "Build      ─JSON──▶  Farmer.Host :5100
                                  for real" button)             │
                                                                ├─▶ vm-golden (Claude CLI)
                                                                └─▶ Azure OpenAI (retro)

 [you, over drinks]
 opens demo/reveal.html on laptop
   → lists recent runs, iframes Jaeger trace, browses artifacts/,
     shows qa-retro.md + review.json + directive-suggestions.md,
     "Apply this directive and re-run" button
```

## Two parallel streams + one inline edit

### Stream H — Farmer intake (tunnel + CORS + inline prompts)

**Territory**
- `src/Farmer.Host/Program.cs` — enable CORS for `*.trycloudflare.com` + `*.azurecontainerapps.io` + `https://trace.steppeintegrations.com` origins. Dev-mode: allow any.
- `src/Farmer.Core/Contracts/TriggerRequest.cs` (or equivalent existing trigger schema) — add optional `prompts_inline` field: `{ filename: string, content: string }[]`. When present, skips the disk sample-plan lookup and uses these prompts directly. Falls through to `work_request_name` for backward compat.
- `src/Farmer.Core/Workflow/Stages/LoadPromptsStage.cs` (or wherever prompts are loaded) — check `prompts_inline` first; if populated use it, else fall through to existing disk path.
- New: `infra/start-tunnel.ps1` — starts cloudflared, outputs the public URL, keeps running until Ctrl+C. Use the quick ephemeral `cloudflared tunnel --url http://localhost:5100` form (no account needed).
- New: `docs/demo-tunnel.md` — one-pager: run this, copy this URL, paste it into desire-trace's config.
- Tests: TriggerRequest deserialization, LoadPromptsStage inline path, CORS middleware.

**Gate**: `dotnet test` green. Manual: `curl -X POST http://localhost:5100/trigger -d '{"prompts_inline":[{"filename":"1.md","content":"build hello world"}],"worker_mode":"fake"}'` completes 7/7.

### Stream J — Reveal UI + feedback loop

**Territory**
- New: `demo/reveal.html` — single static HTML page, vanilla JS, no framework. Lists recent runs from `planning-runtime/runs/` (needs a local HTTP endpoint that exposes the run list — add a simple GET `/runs` to Farmer.Host serving run dir listings).
- New: in `src/Farmer.Host/Program.cs` — add `GET /runs` endpoint returning JSON array of recent run summaries (`{run_id, work_request_name, success, verdict, risk_score, completed_at}` for last N runs). Also `GET /runs/{id}/artifact/{*path}` for reading artifact content.
- New: `demo/reveal.css` (or inline) — dark theme, mobile-safe widths for peeking on phone if needed.
- New: `demo/reveal.js` (or inline) — fetch runs, render cards, embed Jaeger iframe, render markdown files as-is (use a tiny 3-line markdown shim or just `<pre>`).
- In `reveal.html`: for each run, "Apply directive and re-run" button — POSTs `/trigger` with the same prompts + an additional `0-feedback.md` prompt containing the selected directive.
- `docs/demo-reveal.md` — how to open reveal.html (just `file:///...` or `http://localhost:5100/demo/reveal.html` if Farmer serves it).

**Gate**: open reveal.html, see at least one run from tonight's stress runs, click through to Jaeger trace, click artifact filename → see content, click directive → trigger a new run → new run appears in the list after 4 min.

**Territory DO NOT touch**: `src/Farmer.Core/Workflow/Stages/*` (Stream H owns LoadPrompts; all others unchanged), `src/Farmer.Agents/**`, `infra/start-tunnel.ps1` (Stream H).

### Inline (orchestrator does) — desire-trace "Build for real" button

- In `C:\work\iso\desire-trace\index.html`: add a "Build for real" button next to the existing trace-generation UI. On click, POST the user's input to a configurable endpoint (defaults to the cloudflared URL set via a `FARMER_URL` env var or data attribute).
- In `C:\work\iso\desire-trace\serve.py`: pass `FARMER_URL` through to the page (render as a `<meta>` tag).
- Separate commit in the `desire-trace` repo, separate push.

## Territory matrix

| File | Stream H | Stream J | Inline |
|---|---|---|---|
| `src/Farmer.Host/Program.cs` | ✅ (CORS + prompts_inline wiring) | ✅ (`/runs` + `/runs/.../artifact` endpoints) | — |
| `src/Farmer.Core/Contracts/TriggerRequest.cs` | ✅ | — | — |
| `src/Farmer.Core/Workflow/Stages/LoadPromptsStage.cs` | ✅ | — | — |
| `infra/start-tunnel.ps1` | ✅ (NEW) | — | — |
| `demo/reveal.html` / `.js` / `.css` | — | ✅ (NEW) | — |
| `docs/demo-tunnel.md` | ✅ (NEW) | — | — |
| `docs/demo-reveal.md` | — | ✅ (NEW) | — |
| `C:\work\iso\desire-trace\index.html` | — | — | ✅ |
| `C:\work\iso\desire-trace\serve.py` | — | — | ✅ |

**Program.cs collision**: H and J both edit Program.cs (different sections: H adds CORS + is the owner of `LoadPromptsStage` change; J adds `/runs` + `/runs/.../artifact` endpoints). Both add code, no overlap. Orchestrator merges in order H → J; expected clean.

## Runbook (will be docs/demo-runbook.md)

What's in the runbook:
1. Morning-of checklist (start infra, start Host, start tunnel, test one sample run)
2. Exact URL to give friend via text/paste
3. What to do during drinks (nothing — laptop runs itself)
4. What to do at reveal time (open reveal.html, walk him through latest run)
5. If directive looks interesting, click "Apply + re-run", wait 4 min, show him the improved result
6. Fallback: tunnel drops → restart tunnel, URL changes, text friend new URL
7. Fallback: laptop sleeps → System > Power > set to "never sleep while plugged in"
8. Fallback: Farmer crashes → `.\scripts\dev-run.ps1 -SkipWorkerCheck` restarts it

## Non-goals (explicitly)

- Full Azure cloud deploy of Farmer.Host (too heavy for tomorrow; vm-golden is on your laptop anyway).
- Multi-user support (one friend at a time is enough).
- Auth on the tunnel (friend's trusted; no PII in transit).
- Polished production UI on reveal.html (vanilla HTML + dark CSS is enough).
- Persistent cloudflared tunnel (named) — ephemeral is fine for one session.
- Desire-trace workspace discipline integration (keep desire-trace as-is, just add the button).

## Exit definition

Phase Demo is ready when:
- [ ] Tunnel script works end-to-end (you can hit Farmer from your phone over cellular)
- [ ] Friend's mock phone submission through desire-trace lands a Farmer run
- [ ] reveal.html shows the run with all four views (trace, artifacts, retro, directives)
- [ ] "Apply + re-run" works: new run appears in the list with a parent_run_id link
- [ ] docs/demo-runbook.md exists with a morning-of checklist
- [ ] All committed + pushed
