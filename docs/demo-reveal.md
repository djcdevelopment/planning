# Reveal UI — Phase Demo

One-pager on the demo viewer that pairs with the cloudflared tunnel from Stream H. This is the "laptop over drinks" side of the pitch: your friend submits a work request from his phone via `trace.steppeintegrations.com`, Farmer runs it, and you open this page to walk him through what happened.

## What it is

`demo/reveal.html` + `demo/reveal.css` + `demo/reveal.js` — a single-page vanilla-JS viewer for the Farmer run directory. No build step, no framework, no npm install.

## How to open it

Two equivalent options:

1. **Served by Farmer.Host.** Start the Host (`.\scripts\dev-run.ps1`) and hit [http://localhost:5100/demo/reveal.html](http://localhost:5100/demo/reveal.html). The Host's static-file handler serves the `demo/` directory automatically, and the page talks to the same origin — zero CORS friction.

2. **Opened via `file://`.** Double-click `demo/reveal.html` in Explorer, or open `file:///C:/work/iso/planning/demo/reveal.html`. The page auto-detects it's not on HTTP and falls back to `http://localhost:5100` for API calls. This requires Stream H's CORS allow-list (`*` in dev mode) to be in place.

Override the API base with `?api=http://other-host:5100`. Override the Jaeger URL (used in the Trace tab's iframe) with `?jaeger=http://...`.

## What each tab shows

| Tab | Source | Notes |
|---|---|---|
| **Trace** | `http://localhost:16686/trace/{trace_id}` iframe | `trace_id` is read from `events.jsonl`. The current pipeline doesn't embed trace_id in events, so this will usually show the "no trace_id found" notice with a link to Jaeger root. Future pipelines that add `trace_id` to events will light this tab up automatically. |
| **Artifacts** | `artifacts-index.json` if present, else filesystem walk of `artifacts/` | Each row is expand-to-read. Content is lazy-fetched via `GET /runs/{id}/file/artifacts/{path}` on first click. Skipped/error entries show their reason + detail from the index. |
| **Retrospective** | `qa-retro.md` + `review.json` side-by-side | The review card shows verdict + risk + findings + suggestions as structured HTML; the raw JSON sits underneath. |
| **Directives** | `directive-suggestions.md` split on `## 1.`, `## 2.`, ... headings | Each directive gets its own card with an **Apply + re-run** button. |

## Directive re-apply flow

Clicking **Apply + re-run** on a directive card:

1. POSTs to `/trigger` with:
   ```json
   {
     "work_request_name": "<same as the original run>",
     "worker_mode": "real",
     "prompts_inline": [
       { "filename": "0-feedback.md", "content": "<directive title + body>" },
       { "filename": "99-parent-note.md", "content": "<reference back to parent run>" }
     ],
     "parent_run_id": "<this run's id>"
   }
   ```
2. Stream H's `LoadPromptsStage` consumes `prompts_inline` and skips the disk sample-plan lookup for this run.
3. The sidebar polls `/runs` every 5s (toggleable via the auto-refresh checkbox in the top bar), so the new run appears within a cycle. For `worker_mode: real` the new run takes ~3–5 minutes to complete; the sidebar row shows verdict=n/a until then.

### Caveats

- If Stream H hasn't merged (`prompts_inline` not yet wired), the re-run will still go through but will fall back to the disk-based prompts for `work_request_name`. The directive content is ignored in that path. Check the target run's `request.json` to confirm.
- `parent_run_id` is a best-effort field — if the `TriggerRequest` schema hasn't been extended to accept it yet, it'll be dropped by the deserializer without failing the request.

## Backend endpoints used

All live in `src/Farmer.Host/Program.cs` in a commented Stream J block at the end:

```
GET  /runs                           -> RunSummary[] (last 20 by mtime, newest first)
GET  /runs/{id}                      -> RunDetail (summary + files + artifacts + retro + directives + trace_id)
GET  /runs/{id}/file/{**path}        -> file content (text/plain), sandboxed under runDir
     /demo/*                         -> static files from the repo's demo/ directory
```

The file endpoint rejects absolute paths, `..` traversal, and any resolved path that escapes the run directory. See `RunsBrowserServiceTests` in `Farmer.Tests.Integration` for the security assertions.

## Demo scripting tips

- Start with the **Trace** tab only if Jaeger has the trace; otherwise jump straight to **Retrospective** — it's the most visually compelling.
- The **Artifacts** tab looks best on runs where source was actually captured. The Phase 7.5 Stream G `ArchiveStage` populates these; runs from before that stage shipped will show `(no artifacts captured)`.
- The **Directives** tab is the money shot — demoing the close-the-loop pattern. If tonight's runs don't produce directives, synthesize one by editing `directive-suggestions.md` in a run dir manually before the demo.
