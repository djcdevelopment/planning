# Dev launcher: uses appsettings.Development.json (C:\work\iso\planning-runtime paths + Jaeger OTLP).
$env:ASPNETCORE_ENVIRONMENT = 'Development'
$env:DOTNET_ENVIRONMENT     = 'Development'
$root = Split-Path -Parent $PSScriptRoot
Set-Location (Join-Path $root 'src\Farmer.Host')
dotnet run --no-build
