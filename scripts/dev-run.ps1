# Dev launcher: uses appsettings.Development.json (C:\work\iso\planning-runtime paths + Jaeger OTLP).
# Pre-flight: checks worker.sh parity against vm-golden. Warns on drift; non-blocking so
# you can still boot Farmer.Host offline. Pass -SkipWorkerCheck to bypass entirely.
param(
    [switch]$SkipWorkerCheck
)
$env:ASPNETCORE_ENVIRONMENT = 'Development'
$env:DOTNET_ENVIRONMENT     = 'Development'
$root = Split-Path -Parent $PSScriptRoot

if (-not $SkipWorkerCheck) {
    $parity = Join-Path $root 'infra\check-worker-parity.ps1'
    if (Test-Path $parity) {
        & $parity -Quiet
        if ($LASTEXITCODE -ne 0) {
            Write-Host "[dev-run] worker.sh parity check non-zero; continuing anyway (use -SkipWorkerCheck to suppress)." -ForegroundColor Yellow
        }
    }
}

Set-Location (Join-Path $root 'src\Farmer.Host')
dotnet run --no-build
