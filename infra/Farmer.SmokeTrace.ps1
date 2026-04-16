# Farmer.SmokeTrace.ps1 -- canonical one-shot smoke test for the /trigger pipeline.
#
# Starts NATS + Jaeger if not already up, POSTs a /trigger request, parses the
# JSON response, pulls the matching traceId from Jaeger, and prints the complete
# set of URLs + queries a human (or PR reviewer) needs to inspect the run.
#
# Usage:
#   .\infra\Farmer.SmokeTrace.ps1                           # fake mode, react-grid-component
#   .\infra\Farmer.SmokeTrace.ps1 -WorkerMode real          # real Claude CLI run (~5-20 min)
#   .\infra\Farmer.SmokeTrace.ps1 -WorkRequest api-endpoint # different sample plan
#   .\infra\Farmer.SmokeTrace.ps1 -FarmerHost http://localhost:5100
#
# Prereqs: Farmer.Host running on :5100 (scripts\dev-run.ps1). This script won't
# start the Host for you -- that process is expected to be long-lived and logged.

param(
    [ValidateSet('real', 'fake')][string]$WorkerMode = 'fake',
    [string]$WorkRequest = 'react-grid-component',
    [string]$FarmerHost = 'http://localhost:5100',
    [string]$JaegerHost = 'http://localhost:16686',
    [string]$NatsMonitor = 'http://localhost:8222'
)
$ErrorActionPreference = 'Stop'

function Section($title) { Write-Host ''; Write-Host "=== $title ===" -ForegroundColor Cyan }

# --- 0. Sanity: is Farmer.Host listening? -------------------------------------
Section 'Preflight'
try {
    $health = Invoke-RestMethod "$FarmerHost/health" -TimeoutSec 3
    Write-Host "  Farmer.Host: healthy ($($health.status))" -ForegroundColor Green
} catch {
    Write-Host "  Farmer.Host is not responding at $FarmerHost/health" -ForegroundColor Red
    Write-Host "  Start it first: .\scripts\dev-run.ps1" -ForegroundColor Yellow
    exit 1
}

# --- 1. POST /trigger ---------------------------------------------------------
Section 'POST /trigger'
$body = @{
    work_request_name = $WorkRequest
    source            = 'smoke-trace'
    worker_mode       = $WorkerMode
} | ConvertTo-Json -Compress

Write-Host "  body: $body"
$sw = [System.Diagnostics.Stopwatch]::StartNew()
$response = Invoke-RestMethod "$FarmerHost/trigger" -Method Post -ContentType 'application/json' -Body $body
$sw.Stop()

Write-Host ("  runId:            {0}" -f $response.runId)
Write-Host ("  success:          {0}" -f $response.success)
Write-Host ("  finalPhase:       {0}" -f $response.finalPhase)
Write-Host ("  stagesCompleted:  {0}" -f ($response.stagesCompleted -join ' > '))
Write-Host ("  durationSeconds:  {0}" -f $response.durationSeconds)
Write-Host ("  wall clock:       {0:N2}s" -f ($sw.ElapsedMilliseconds / 1000.0))
if ($response.error) {
    Write-Host ("  error:            {0}" -f $response.error) -ForegroundColor Red
}

# --- 2. Grab the traceId from Jaeger ------------------------------------------
Section 'Trace'
$traceId = $null
try {
    $traces = (Invoke-RestMethod "$JaegerHost/api/traces?service=Farmer&limit=10&lookback=5m" -TimeoutSec 3).data
    # Newest trace first; expect the POST /trigger span to be present.
    $trace = $traces |
        Where-Object { ($_.spans | Where-Object operationName -eq 'POST /trigger') } |
        Sort-Object -Property @{e={$_.spans[0].startTime}} -Descending |
        Select-Object -First 1
    if ($trace) {
        $traceId = $trace.traceID
        Write-Host ("  traceId:  {0}" -f $traceId)
        Write-Host ("  spans:    {0}" -f $trace.spans.Count)
    } else {
        Write-Host "  (no matching trace in Jaeger yet -- export is async; try refreshing the URL below in a few seconds)" -ForegroundColor Yellow
    }
} catch {
    Write-Host "  Jaeger unavailable at $JaegerHost ($($_.Exception.Message))" -ForegroundColor Yellow
}

# --- 3. NATS stream + bucket deltas -------------------------------------------
Section 'NATS'
try {
    $streams = (Invoke-RestMethod "$NatsMonitor/jsz?streams=true" -TimeoutSec 3).account_details[0].stream_detail
    $streams | Where-Object name -in 'FARMER_RUNS', 'OBJ_farmer-runs-out' |
        Select-Object name,
            @{n='msgs'; e={$_.state.messages}},
            @{n='bytes'; e={$_.state.bytes}} |
        Format-Table -AutoSize | Out-String | Write-Host
} catch {
    Write-Host "  NATS monitoring unavailable at $NatsMonitor" -ForegroundColor Yellow
}

# --- 4. Where to look next ----------------------------------------------------
Section 'Where to look'
if ($traceId) {
    Write-Host "  Jaeger waterfall:"
    Write-Host "    $JaegerHost/trace/$traceId"
}
Write-Host "  Jaeger service:     $JaegerHost/search?service=Farmer&lookback=15m"
Write-Host "  NATS overview:      $NatsMonitor/"
Write-Host "  NATS streams:       $NatsMonitor/jsz?streams=true"
Write-Host "  Filter FARMER_RUNS: nats sub 'farmer.events.run.$($response.runId).>'"
Write-Host "  Run dir on disk:    C:\work\iso\planning-runtime\runs\$($response.runId)\"
Write-Host "  ObjectStore key:    farmer-runs-out/$($response.runId)/"
Write-Host ''

if ($response.success) {
    Write-Host "  SUCCESS: $($response.stagesCompleted.Count)/7 stages green." -ForegroundColor Green
    exit 0
} else {
    Write-Host "  FAILURE at $($response.finalPhase)." -ForegroundColor Red
    exit 2
}
