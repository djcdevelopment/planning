# Start NATS in the background. Idempotent: does nothing if one's already listening on 4222.
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$exe  = Join-Path $root 'tools\nats-server.exe'
$cfg  = Join-Path $root 'infra\nats.conf'
$log  = Join-Path $root 'logs\nats.log'

if (Get-NetTCPConnection -LocalPort 4222 -State Listen -ErrorAction SilentlyContinue) {
    Write-Host "nats-server already listening on 4222"
    return
}

Start-Process -FilePath $exe -ArgumentList @('-c', $cfg) `
    -RedirectStandardOutput $log -RedirectStandardError "$log.err" `
    -WindowStyle Hidden -PassThru | Out-Null

Start-Sleep -Seconds 2
if (Get-NetTCPConnection -LocalPort 4222 -State Listen -ErrorAction SilentlyContinue) {
    Write-Host "nats-server started; monitoring at http://localhost:8222"
} else {
    Write-Error "nats-server did not start - see $log.err"
}
