# Demo Runbook — Friend meetup, 2026-04-24

One-page checklist for tomorrow. Follow top to bottom.

## Morning-of (before you leave the house)

**Wake the stack:**
```powershell
# 0. Cloudflared is installed at C:\Program Files (x86)\cloudflared\cloudflared.exe.
#    Open a FRESH PowerShell window (PATH gets picked up on first launch).
#    If 'cloudflared --version' errors, the install didn't take — rerun:
#    winget install -e --id Cloudflare.cloudflared

# 1. Verify vm-golden is up (Hyper-V). It auto-starts with host, but confirm:
Test-Connection vm-golden -Count 2 -Quiet   # should return True

# 2. Start infra (idempotent; no-ops if already listening)
cd C:\work\iso\planning
.\infra\start-nats.ps1
.\infra\start-jaeger.ps1

# 3. Start Farmer.Host (foreground in its own window; leave running all day)
.\scripts\dev-run.ps1 -SkipWorkerCheck

# 4. In a SEPARATE window: start the tunnel. It prints the public URL.
.\infra\start-tunnel.ps1
# Output includes a line: `TUNNEL_URL: https://<something>.trycloudflare.com`
# That URL is ALSO written to infra\.tunnel-url.txt for copy-paste.
```

**Smoke-test the intake** (from the same laptop, paste your tunnel URL):
```powershell
$tunnel = Get-Content .\infra\.tunnel-url.txt
$body = @{
  work_request_name = "pre-flight-test"
  worker_mode = "fake"
  prompts_inline = @(
    @{ filename = "1-Test.md"; content = "Write a Python hello world script." }
  )
} | ConvertTo-Json -Depth 5
$resp = Invoke-RestMethod "$tunnel/trigger" -Method Post -ContentType "application/json" -Body $body
$resp.runId   # should print a run id and success=True
```

**Update the desire-trace live site (if you want the button on trace.steppeintegrations.com):**
```powershell
cd C:\work\iso\desire-trace
az containerapp up --name steppe-trace --resource-group steppe-rg --source .
# ~2-3 min. The new 'Build for real' button appears on the live URL once it finishes.
# If you skip this, the button only exists in your local clone (still usable by
# running `python serve.py` locally — see below).
```

**Alternative if you skip the Azure redeploy:** your friend uses **desire-trace running locally** on your laptop — you open a browser tab on the laptop, navigate to the local page yourself, paste the tunnel URL into the "Build for real" box, type his idea, hit the button. Friend watches reveal unfold. Less magical but zero deploy risk.

## Text your friend the URL

**Two URLs to send:**
1. The desire-trace page (Azure-hosted, has the Build for real button):
   `https://steppe-trace.proudrock-473a42a9.westus2.azurecontainerapps.io/`
2. The tunnel URL (for the "Farmer URL" input on that page):
   `https://<something>.trycloudflare.com` — copy from `infra\.tunnel-url.txt`

**Tell him:** "Paste the tunnel URL into the 'Farmer URL' field, type what you want built into the big box, click Build for real. I'll show you the result when we meet."

## At drinks

Nothing. The laptop is doing the work. Your friend's submissions accumulate in `planning-runtime\runs\` and you'll see them all at reveal time.

**If he asks "is it working?"** — open the reveal UI on your phone browser:
`https://<tunnel>/demo/reveal.html`
You'll see his runs land in the left sidebar as they complete.

## At reveal (laptop comes out)

1. Open **reveal UI** locally: `http://localhost:5100/demo/reveal.html`
2. Left sidebar shows recent runs, newest first. Click his latest.
3. Main pane has four tabs:
   - **Trace** — Jaeger waterfall (if not showing, paste the trace_id from events.jsonl into Jaeger manually)
   - **Artifacts** — the actual source files Claude produced, browsable
   - **Retrospective** — `qa-retro.md` narrative + `review.json` verdict (Accept/Retry/Reject with risk score + findings)
   - **Directives** — rewrite suggestions from the retro agent
4. **The closing-the-loop moment:** pick a directive card, click **"Apply + re-run"**. A new run fires with that directive threaded in as `0-feedback.md`. ~4 min later it lands in the sidebar with a `parent_run_id` link to the original. Show him the comparison.

## Fallbacks (read this; it's the difference between a live demo and an awkward one)

| Symptom | Fix |
|---|---|
| Tunnel URL 404s / times out | Ctrl+C the tunnel window, re-run `.\infra\start-tunnel.ps1` — new ephemeral URL. Text it to friend. Previous runs still in reveal UI. |
| Laptop went to sleep | `Settings → System → Power → Screen & sleep → "Never" on battery + plugged in`. Cloudflared also dies on sleep; restart tunnel after wake. |
| Farmer.Host crashed | Check dev-run window. If dead, `.\scripts\dev-run.ps1 -SkipWorkerCheck`. Past runs preserved on disk. |
| vm-golden stopped | Hyper-V Manager → right-click vm-golden → Start. Then re-trigger any failed runs. |
| Friend's run says Reject/risk=90 with "no source captured" | **Known issue:** manifest race (worker.sh writes partial + final; Collect sometimes catches the partial). Retro correctly flags it. This is actually demonstrable value — "look how the retro catches a broken build" — lean into it. |
| Friend's run takes too long | Real mode is 3-10 min depending on toolchain. He sees "dispatched" immediately; the run continues even if the tunnel drops. Past runs remain queryable via `/runs`. |
| Azure OpenAI quota error | Unlikely at one-friend volume but: retry via the re-run button. If persistent, check [Azure portal → farmer-openai-dev → Metrics](https://portal.azure.com). |
| `az login` expired | `Connect-AzAccount -Subscription c1d3fee5-3b54-4f6b-bff2-974dc34053cc` then restart Farmer.Host to refresh DefaultAzureCredential. |

## Seed ideas (if your friend asks what to try)

- "A CLI tool that diffs two SQL schemas"
- "A Slack bot that summarizes PRs when you @-mention it"
- "An iOS-style pull-to-refresh on a plain HTML list"
- "A tiny HTTP server in Rust that proxies and rate-limits one endpoint"
- "A browser extension that highlights tracking pixels on any page"

Sized so real mode finishes in 3-8 min each. Avoid "build me Uber" — Claude will get creative with tool choices and the retro will be less interesting than a focused task.

## Architecture cheat sheet (if he asks how it works)

```
[his phone]         [Azure]                  [your laptop, tethered]
 types idea  ─HTTPS─▶ desire-trace     ─HTTPS─▶ cloudflared tunnel
                     "Build for real"           │
                     button                     ▼
                                         Farmer.Host (dotnet, :5100)
                                                │
                                                ├─▶ SSH ──▶ vm-golden (Hyper-V Ubuntu)
                                                │             └─▶ Claude CLI (real worker)
                                                │
                                                └─▶ HTTPS ─▶ Azure OpenAI (retrospective agent)
                                                             via DefaultAzureCredential (Entra)
```

- **File-based everything** — all evidence in `planning-runtime\runs\<id>\`, nothing in a DB
- **Retrospective is a different model than the worker** — Claude CLI builds; `gpt-4.1-mini` on Azure reviews
- **NATS JetStream** is the event bus between stages; **Jaeger** captures the trace
- **The demo loop:** idea → Farmer → evidence → retro → directive → re-run. This is Phase 7 through 7.5 + Phase Demo.

## Post-mortem

After he leaves: open [docs/phase-demo-retro.md](./phase-demo-retro.md) (not yet written — you'll write it fresh from what you saw). Capture:
- What ideas he tried
- Which retros surprised you
- What Claude got wrong that you noticed
- What your friend said when he saw the reveal UI

That retro feeds the next iteration. The feedback loop you're demoing is literal: his feedback becomes the next improvement's prompt.
