# setup-tunnel-blob.ps1 — Phase Demo v2 Stream 3
#
# One-time provisioning for the Azure Blob that receives ephemeral tunnel URLs
# from `infra/start-tunnel.ps1`. Idempotent — safe to re-run.
#
# What it does:
#   1. Creates (or reuses) a StandardLRS / StorageV2 storage account in the
#      given resource group.
#   2. Creates (or reuses) the container.
#   3. Mints a SAS URL for `current.json` with read+write permissions valid
#      for one year and prints it, along with the PowerShell one-liner to
#      persist it into your user-scoped environment.
#   4. Also writes the SAS to `infra/.tunnel-blob-url.txt` (gitignored) so
#      `start-tunnel.ps1` can be driven from a shared script that sources
#      the SAS from disk rather than asking the operator to export it.
#
# Usage:
#   .\infra\setup-tunnel-blob.ps1
#   .\infra\setup-tunnel-blob.ps1 -ResourceGroup rg-farmer-dev -StorageAccount myacct
#
# Requires the Azure CLI. We know it's installed at the Program Files path on
# this workstation; falls back to `az` on PATH for CI / other machines.

param(
    [string]$ResourceGroup = 'rg-farmer-dev',
    [string]$StorageAccount,
    [string]$Container = 'tunnel-state',
    [string]$BlobName = 'current.json',
    [string]$Location = 'eastus2',
    [int]$SasYears = 1
)

$ErrorActionPreference = 'Stop'

# --- Locate az.cmd ---------------------------------------------------------
$azFixedPath = 'C:\Program Files\Microsoft SDKs\Azure\CLI2\wbin\az.cmd'
if (Test-Path $azFixedPath) {
    $az = $azFixedPath
} else {
    $azCmd = Get-Command az -ErrorAction SilentlyContinue
    if (-not $azCmd) {
        Write-Host "ERROR: az CLI not found at $azFixedPath or on PATH." -ForegroundColor Red
        Write-Host "Install: https://learn.microsoft.com/cli/azure/install-azure-cli-windows"
        exit 1
    }
    $az = $azCmd.Source
}

function Invoke-Az {
    # Thin wrapper so we can capture JSON cleanly. $LASTEXITCODE propagates.
    param([Parameter(Mandatory, ValueFromRemainingArguments)][string[]]$Args)
    & $az @Args
    if ($LASTEXITCODE -ne 0) {
        throw "az $($Args -join ' ') failed with exit code $LASTEXITCODE"
    }
}

# --- Auto-generate storage account name if none supplied -------------------
# Storage account names are 3-24 chars, lowercase alphanumeric, globally unique.
# Default: `farmertunnel` + 8 hex chars sourced from the subscription id so
# repeat runs from the same sub land on the same name (idempotent).
if ([string]::IsNullOrWhiteSpace($StorageAccount)) {
    $subJson = Invoke-Az account show -o json
    $sub = $subJson | ConvertFrom-Json
    $hash = [System.BitConverter]::ToString(
        [System.Security.Cryptography.SHA256]::Create().ComputeHash(
            [System.Text.Encoding]::UTF8.GetBytes($sub.id))).Replace('-', '').ToLowerInvariant()
    $StorageAccount = "farmertunnel$($hash.Substring(0,8))"
    Write-Host "Auto-generated storage account name: $StorageAccount" -ForegroundColor DarkGray
}

if ($StorageAccount.Length -gt 24 -or $StorageAccount -notmatch '^[a-z0-9]+$') {
    Write-Host "ERROR: storage account name '$StorageAccount' is invalid (lowercase alnum, 3-24 chars)." -ForegroundColor Red
    exit 1
}

# --- Resource group --------------------------------------------------------
Write-Host "Ensuring resource group $ResourceGroup exists in $Location..." -ForegroundColor Cyan
Invoke-Az group create --name $ResourceGroup --location $Location -o none

# --- Storage account -------------------------------------------------------
Write-Host "Ensuring storage account $StorageAccount exists..." -ForegroundColor Cyan
$acctJson = & $az storage account show --name $StorageAccount --resource-group $ResourceGroup -o json 2>$null
if ($LASTEXITCODE -ne 0) {
    Invoke-Az storage account create `
        --name $StorageAccount `
        --resource-group $ResourceGroup `
        --location $Location `
        --sku Standard_LRS `
        --kind StorageV2 `
        --allow-blob-public-access false `
        -o none
    $acctJson = Invoke-Az storage account show --name $StorageAccount --resource-group $ResourceGroup -o json
}

# --- Storage key (for SAS generation) -------------------------------------
$keysJson = Invoke-Az storage account keys list `
    --account-name $StorageAccount `
    --resource-group $ResourceGroup `
    -o json
$keys = $keysJson | ConvertFrom-Json
$key = $keys[0].value

# --- Container -------------------------------------------------------------
Write-Host "Ensuring container $Container exists..." -ForegroundColor Cyan
Invoke-Az storage container create `
    --name $Container `
    --account-name $StorageAccount `
    --account-key $key `
    --public-access off `
    -o none

# --- SAS for current.json --------------------------------------------------
# rwc (read/write/create) on the blob itself so the tunnel script can replace
# it unconditionally. HTTPS-only. Expiry 1 year from today — rotate when the
# demo goes past the expiry.
$expiry = (Get-Date).ToUniversalTime().AddYears($SasYears).ToString('yyyy-MM-ddTHH:mm:ssZ')
Write-Host "Generating SAS (expires $expiry)..." -ForegroundColor Cyan

$sas = & $az storage blob generate-sas `
    --account-name $StorageAccount `
    --account-key $key `
    --container-name $Container `
    --name $BlobName `
    --permissions rcw `
    --expiry $expiry `
    --https-only `
    -o tsv
if ($LASTEXITCODE -ne 0) {
    throw "az storage blob generate-sas failed"
}
$sas = $sas.Trim()

$blobUrl = "https://$StorageAccount.blob.core.windows.net/$Container/$BlobName`?$sas"

# --- Persist + print -------------------------------------------------------
$sasFile = Join-Path $PSScriptRoot '.tunnel-blob-url.txt'
Set-Content -Path $sasFile -Value $blobUrl -Encoding utf8 -NoNewline
Write-Host ""
Write-Host "Wrote $sasFile" -ForegroundColor Green
Write-Host ""
Write-Host "SAS URL:" -ForegroundColor Cyan
Write-Host $blobUrl
Write-Host ""
Write-Host "Persist to your user environment (run in a PowerShell shell):" -ForegroundColor Cyan
Write-Host "  [Environment]::SetEnvironmentVariable(`"FARMER_TUNNEL_BLOB_URL`", `"$blobUrl`", `"User`")"
Write-Host ""
Write-Host "Then restart the shell and run .\infra\start-tunnel.ps1 -- it will auto-publish." -ForegroundColor DarkGray
