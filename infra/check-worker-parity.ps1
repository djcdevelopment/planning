# Hash-compare src/Farmer.Worker/worker.sh against the deployed copy on the VM.
# Catches "I forgot to SCP the updated worker" before the first /trigger wastes a
# run chasing phantom behavior. Run directly, or via scripts/dev-run.ps1 which
# invokes this non-blocking at startup.
#
# Usage:
#   .\infra\check-worker-parity.ps1                    # check only
#   .\infra\check-worker-parity.ps1 -Deploy            # check + SCP local -> remote on drift
#   .\infra\check-worker-parity.ps1 -SshHost other-vm  # override defaults for a different VM
#
# Exit codes:
#   0 = hashes match (OK), or -Deploy fixed the drift
#   1 = drift detected and -Deploy not set
#   2 = couldn't reach remote (SSH failure, missing file, etc.)

param(
    [string]$SshHost    = 'vm-golden',
    [string]$SshUser    = 'claude',
    [string]$SshKeyPath = "$env:USERPROFILE\.ssh\id_ed25519",
    [string]$RemotePath = '/home/claude/projects/worker.sh',
    [switch]$Deploy,
    [switch]$Quiet                                      # suppress OK chatter; drift/fail still printed
)
# Default ErrorActionPreference (Continue) lets ssh's stderr surface without
# turning into a terminating error. We branch on $LASTEXITCODE explicitly below.

$root  = Split-Path -Parent $PSScriptRoot
$local = Join-Path $root 'src\Farmer.Worker\worker.sh'

if (-not (Test-Path $local)) {
    Write-Host "[worker-parity] FAIL: local worker.sh not found at $local" -ForegroundColor Red
    exit 2
}

# Normalize CRLF -> LF for comparison. Git history is LF; a Windows editor may
# leave CRLFs in the working copy, which would hash differently than the LF copy
# on the VM. We hash a normalized stream so drift reports reflect real content
# diffs, not line-ending noise.
$bytes = [System.IO.File]::ReadAllBytes($local)
$text  = [System.Text.Encoding]::UTF8.GetString($bytes) -replace "`r`n", "`n"
$normalizedBytes = [System.Text.Encoding]::UTF8.GetBytes($text)
$sha = [System.Security.Cryptography.SHA256]::Create()
$localHash = ([System.BitConverter]::ToString($sha.ComputeHash($normalizedBytes)) -replace '-','').ToLower()

# Remote hash via ssh sha256sum. Pipe stderr to a temp so a noisy warning doesn't
# break the hash parse.
$remoteOutput = & ssh -o BatchMode=yes -o StrictHostKeyChecking=accept-new `
    -i $SshKeyPath "$SshUser@$SshHost" "sha256sum $RemotePath 2>/dev/null" 2>$null
if ($LASTEXITCODE -ne 0 -or -not $remoteOutput) {
    Write-Host "[worker-parity] FAIL: couldn't read $SshUser@${SshHost}:$RemotePath (ssh exit $LASTEXITCODE)" -ForegroundColor Red
    exit 2
}
$remoteHash = ($remoteOutput -split '\s+')[0].ToLower()

if ($localHash -eq $remoteHash) {
    if (-not $Quiet) {
        Write-Host "[worker-parity] OK: $($localHash.Substring(0,12))... (local == $SshHost)" -ForegroundColor Green
    }
    exit 0
}

Write-Host "[worker-parity] DRIFT: local and $SshHost copies of worker.sh differ" -ForegroundColor Yellow
Write-Host "  local : $localHash"
Write-Host "  remote: $remoteHash"

if ($Deploy) {
    Write-Host "[worker-parity] -Deploy set; SCPing local -> $SshUser@${SshHost}:$RemotePath"
    & scp -o BatchMode=yes -o StrictHostKeyChecking=accept-new `
        -i $SshKeyPath $local "${SshUser}@${SshHost}:$RemotePath"
    if ($LASTEXITCODE -ne 0) {
        Write-Host "[worker-parity] FAIL: scp exit $LASTEXITCODE" -ForegroundColor Red
        exit 2
    }
    & ssh -o BatchMode=yes -i $SshKeyPath "$SshUser@$SshHost" "chmod +x $RemotePath"
    if ($LASTEXITCODE -ne 0) {
        Write-Host "[worker-parity] WARN: chmod +x exit $LASTEXITCODE (script copied, may not be executable)" -ForegroundColor Yellow
    }
    Write-Host "[worker-parity] deployed. Re-check:" -ForegroundColor Green
    & $PSCommandPath -SshHost $SshHost -SshUser $SshUser -SshKeyPath $SshKeyPath -RemotePath $RemotePath
    exit $LASTEXITCODE
}

Write-Host "[worker-parity] hint: re-run with -Deploy to copy the local version to the VM" -ForegroundColor Yellow
exit 1
