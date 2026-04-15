# Start Jaeger v2 all-in-one. Idempotent: no-op if 4317 already listening.
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$exe  = Join-Path $root 'tools\jaeger.exe'
$cfg  = Join-Path $root 'infra\jaeger.yaml'
$log  = Join-Path $root 'logs\jaeger.log'

if (Get-NetTCPConnection -LocalPort 4317 -State Listen -ErrorAction SilentlyContinue) {
    Write-Host "Jaeger already listening on OTLP gRPC 4317"
    return
}

# jaeger.exe is too large for git (~120MB). Fetch on first use.
if (-not (Test-Path $exe)) {
    Write-Host "jaeger.exe not present; running tools\download-jaeger.ps1 ..."
    & (Join-Path $root 'tools\download-jaeger.ps1')
}

Start-Process -FilePath $exe -ArgumentList @('--config', "file:$cfg") `
    -RedirectStandardOutput $log -RedirectStandardError "$log.err" `
    -WindowStyle Hidden -PassThru | Out-Null

Start-Sleep -Seconds 3
if (Get-NetTCPConnection -LocalPort 4317 -State Listen -ErrorAction SilentlyContinue) {
    Write-Host "Jaeger OTLP gRPC listening on :4317"
    Write-Host "Jaeger OTLP HTTP listening on :4318"
    Write-Host "Jaeger UI at http://localhost:16686"
} else {
    Write-Error "Jaeger did not start - see $log.err"
}
