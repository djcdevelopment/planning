# Demo tunnel — phone-to-Farmer over Cloudflare

For the Phase Demo evening, a browser on the friend's phone needs to POST to
Farmer.Host running on your laptop. This is the minimum path to a public
HTTPS URL that forwards to `http://localhost:5100`, with no Cloudflare
account, DNS, or config required.

## Prerequisites

- **cloudflared** on PATH. If missing:
  ```powershell
  winget install Cloudflare.cloudflared
  ```
  Or grab the Windows .exe from
  [the cloudflared releases page](https://github.com/cloudflare/cloudflared/releases/latest)
  and put it on PATH.
- Farmer.Host bound to `http://localhost:5100` (the default — `.\scripts\dev-run.ps1`
  is your friend).
- CORS is already permissive (`AllowAnyOrigin` / `AllowAnyMethod` /
  `AllowAnyHeader`) on Farmer.Host's side, so browser-originated requests
  work without extra config. See `src/Farmer.Host/Program.cs`.

## One-shot start

```powershell
.\infra\start-tunnel.ps1
```

The script:

1. Checks `cloudflared` is on PATH (prints an install hint and exits if not).
2. Runs `cloudflared tunnel --url http://localhost:5100`.
3. Tees cloudflared's output to the console, parses the first
   `*.trycloudflare.com` URL it sees, and prints a single greppable line:
   ```
   TUNNEL_URL: https://<slug>.trycloudflare.com
   ```
4. Writes the same URL to `infra/.tunnel-url.txt` so orchestration scripts
   (or a desire-trace pickup step) can read it programmatically.
5. Stays running until you Ctrl+C. The tunnel closes when the process exits.

## What the URL looks like

```
https://recommendation-worker-comply-hassle.trycloudflare.com
```

Three/four dashed words + the `trycloudflare.com` apex. Cloudflare's
ephemeral tunnels pick a fresh slug every run — **the URL changes on
every restart**. Text the new URL to the friend if the tunnel drops and
you bring it back up.

## Smoke test

From a second terminal:

```powershell
# Replace with whatever TUNNEL_URL printed above
$tunnel = Get-Content .\infra\.tunnel-url.txt
curl.exe "$tunnel/health"
# Expected: { "status": "healthy", "timestamp": "..." }
```

The inline-prompts path, end-to-end, in one shot:

```powershell
curl.exe -X POST -H "Content-Type: application/json" "$tunnel/trigger" -d '{
  "prompts_inline": [
    { "filename": "1-Build.md", "content": "Write a Python function that prints hello world" }
  ],
  "worker_mode": "fake",
  "work_request_name": "live-demo"
}'
```

This requires no pre-existing `sample-plans/live-demo/` directory —
LoadPromptsStage uses the inline prompts directly. `work_request_name` is
still used for the run's display name in retrospective metadata.

## Persistent URL — named tunnel (for later, not tonight)

The ephemeral tunnel is fine for one evening of drinks. For a URL that
survives across laptop reboots, register a **named tunnel** bound to a
hostname on a Cloudflare-managed zone. The short version:

```bash
cloudflared tunnel login
cloudflared tunnel create farmer-demo
cloudflared tunnel route dns farmer-demo farmer.<yourzone>.com
cloudflared tunnel run --url http://localhost:5100 farmer-demo
```

Full walkthrough lives in
[Cloudflare's docs — Create a tunnel](https://developers.cloudflare.com/cloudflare-one/connections/connect-networks/get-started/create-remote-tunnel/).
No need to read it tonight.

## Troubleshooting

- **"cloudflared not found on PATH"** — install via `winget install Cloudflare.cloudflared`
  and reopen the terminal so the PATH refreshes.
- **The script prints a URL but curl times out** — Farmer.Host isn't
  actually listening on 5100. Start it with `.\scripts\dev-run.ps1` and
  retry.
- **Friend gets a CORS error** — shouldn't happen given the permissive
  dev policy; if it does, confirm the dev policy is in place
  (`app.UseCors("FarmerDevCors")` in `Program.cs`) and that the server was
  restarted after any config change.
- **Tunnel drops mid-demo** — kill the script with Ctrl+C, restart it,
  copy the new `TUNNEL_URL` and re-send it to the friend. The URL WILL be
  different each time.
- **Orphaned cloudflared process after Ctrl+C** — if Task Manager shows a
  lingering `cloudflared.exe`, kill it manually. The script tries to
  clean up in its `finally` block but Windows PS Ctrl+C semantics are
  imperfect.
