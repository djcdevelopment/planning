# Download Jaeger v2 all-in-one for Windows. Run once after cloning.
# jaeger.exe is ~120MB uncompressed and lives outside git (see .gitignore).
param(
    [string]$Version = '2.17.0'
)
$ErrorActionPreference = 'Stop'
$toolsDir = $PSScriptRoot
$target   = Join-Path $toolsDir 'jaeger.exe'
if (Test-Path $target) {
    Write-Host "jaeger.exe already present at $target"
    return
}

$url = "https://github.com/jaegertracing/jaeger/releases/download/v$Version/jaeger-$Version-windows-amd64.tar.gz"
$tmp = New-TemporaryFile
Write-Host "downloading $url"
Invoke-WebRequest -Uri $url -OutFile $tmp.FullName -UseBasicParsing

$extractDir = Join-Path $toolsDir "_jaeger-extract"
if (Test-Path $extractDir) { Remove-Item $extractDir -Recurse -Force }
New-Item -ItemType Directory -Path $extractDir | Out-Null

tar -xzf $tmp.FullName -C $extractDir
$exe = Get-ChildItem $extractDir -Recurse -Filter 'jaeger.exe' | Select-Object -First 1
if (-not $exe) { throw "jaeger.exe not found in downloaded archive" }
Move-Item $exe.FullName $target
Remove-Item $extractDir -Recurse -Force
Remove-Item $tmp.FullName -Force

Write-Host "installed $target"
