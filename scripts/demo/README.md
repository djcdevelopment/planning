# Phase 5 Local Demo — Aspire Dashboard + Externalized Runtime

## Prerequisites
- Docker running
- .NET 8 SDK
- Runtime directory exists: `D:\work\planning-runtime\` (with sample-plans)

## Quick Start

### 1. Start Aspire Dashboard
```powershell
docker run --rm -d --name aspire-dashboard `
  -p 18888:18888 -p 18889:18889 `
  -e DOTNET_DASHBOARD_UNSECURED_ALLOW_ANONYMOUS=true `
  mcr.microsoft.com/dotnet/aspire-dashboard:9.0
```
- Dashboard UI: http://localhost:18888
- OTLP endpoint: http://localhost:18889 (gRPC)

### 2. Start Farmer API
```powershell
cd D:\work\planning\src\Farmer.Host
dotnet run
```
- API: http://localhost:5100
- InboxWatcher starts polling `D:\work\planning-runtime\inbox\` every 2 seconds

### 3. Trigger a Run

**Option A — Drop file in inbox (primary path):**
```powershell
copy D:\work\planning\scripts\demo\sample-request.json D:\work\planning-runtime\inbox\
```

**Option B — HTTP trigger:**
```powershell
Invoke-RestMethod -Uri http://localhost:5100/trigger -Method Post `
  -Body '{"work_request_name":"react-grid-component","source":"manual"}' `
  -ContentType "application/json"
```

### 4. Verify Run Folder
```powershell
dir D:\work\planning-runtime\runs\run-*
```
Each run folder contains:
- `request.json` — the input request
- `state.json` — current lifecycle snapshot
- `events.jsonl` — append-only event log
- `result.json` — terminal result (success/failure)
- `logs\` — VM command logs
- `artifacts\` — build outputs

### 5. Inspect Aspire Dashboard
Open http://localhost:18888

**Expected traces:**
- Root span: `workflow.run` tagged with `farmer.run_id`
- Child spans: `workflow.stage.CreateRun`, `workflow.stage.LoadPrompts`, etc.

**Expected logs:**
- Structured log lines with `RunId` and `StageName` fields
- Correlated to trace context

**Expected metrics:**
- `farmer.runs.started` counter
- `farmer.runs.completed` / `farmer.runs.failed` counters
- `farmer.stage.duration` histogram by stage name

### 6. Inspect Events
```powershell
Get-Content D:\work\planning-runtime\runs\run-*\events.jsonl
```
Each line is a JSON event: `{timestamp, run_id, stage, event, data}`

### 7. Cleanup
```powershell
docker stop aspire-dashboard
```

## Registered Ports (portmap)
| Service | Port | Protocol |
|---------|------|----------|
| farmer/api | 5100 | http |
| farmer/aspire-dashboard | 18888 | http |
| farmer/aspire-otlp | 18889 | gRPC |
