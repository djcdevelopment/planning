# start-tunnel.ps1 — Phase Demo ingress
#
# Stands up an ephemeral Cloudflare Tunnel pointing at Farmer.Host on
# http://localhost:5100. No Cloudflare account or DNS required — the
# `--url` form of `cloudflared tunnel` provisions a throwaway
# *.trycloudflare.com hostname for the lifetime of the process.
#
# Output contract:
#   - On success, prints a single line of the form:
#       TUNNEL_URL: https://<slug>.trycloudflare.com
#     This line is greppable; the demo-side page (desire-trace) consumes it.
#   - Also persists the URL to infra/.tunnel-url.txt for programmatic pickup.
#   - Remaining cloudflared output is forwarded to the console so you can
#     watch connection health. Ctrl+C terminates the tunnel.
#
# This is NOT a persistent tunnel. The URL changes on every run. For a
# stable hostname, switch to a named tunnel bound to a real hostname on a
# Cloudflare-managed zone; see docs/demo-tunnel.md for the one-liner upgrade
# path.

$ErrorActionPreference = 'Stop'

# 1) Pre-flight: cloudflared on PATH, or in the standard winget install dir?
$cloudflaredCmd = Get-Command cloudflared -ErrorAction SilentlyContinue
if (-not $cloudflaredCmd) {
    # Fresh winget install puts cloudflared at "%ProgramFiles% (x86)\cloudflared\"
    # but PATH doesn't refresh until a new shell. Fall back to the install dir.
    $fallback = Join-Path ${env:ProgramFiles(x86)} 'cloudflared\cloudflared.exe'
    if (-not (Test-Path $fallback)) {
        $fallback = Join-Path $env:ProgramFiles 'cloudflared\cloudflared.exe'
    }
    if (Test-Path $fallback) {
        $cloudflaredPath = $fallback
        Write-Host "Using cloudflared at $fallback (PATH not refreshed yet)" -ForegroundColor DarkGray
    } else {
        Write-Host ""
        Write-Host "ERROR: cloudflared not found on PATH or in Program Files." -ForegroundColor Red
        Write-Host ""
        Write-Host "Install with winget:"
        Write-Host "  winget install Cloudflare.cloudflared"
        Write-Host ""
        Write-Host "Or download the Windows .exe from:"
        Write-Host "  https://github.com/cloudflare/cloudflared/releases/latest"
        Write-Host ""
        exit 1
    }
} else {
    $cloudflaredPath = $cloudflaredCmd.Source
}

$root = Split-Path -Parent $PSScriptRoot
$urlFile = Join-Path $PSScriptRoot '.tunnel-url.txt'

# Clear any stale URL file from a previous run so consumers don't pick up
# a dead hostname before the new one is written.
if (Test-Path $urlFile) { Remove-Item $urlFile -Force }

Write-Host "Starting cloudflared tunnel -> http://localhost:5100 ..."
Write-Host "(Ctrl+C to stop)"
Write-Host ""

# cloudflared writes its startup banner + tunnel URL to stderr. Use
# Start-Process with redirected streams so we can parse + tee the URL
# without losing interactivity on Ctrl+C.
#
# The URL pattern: cloudflared prints a banner line like:
#   |  https://some-slug-here.trycloudflare.com               |
# (possibly with ANSI escapes / box-drawing). The regex is permissive.
$tunnelUrlRegex = [regex]'https://[a-z0-9-]+\.trycloudflare\.com'

$pinfo = New-Object System.Diagnostics.ProcessStartInfo
$pinfo.FileName = $cloudflaredPath
$pinfo.Arguments = 'tunnel --url http://localhost:5100'
$pinfo.UseShellExecute = $false
$pinfo.RedirectStandardOutput = $true
$pinfo.RedirectStandardError = $true
$pinfo.CreateNoWindow = $false

$proc = New-Object System.Diagnostics.Process
$proc.StartInfo = $pinfo

$foundUrl = $false

# Event handlers fire on cloudflared's background threads; be careful with
# shared state. $script:foundUrl is the only mutation and is set at most
# once per line, so we don't need a lock for the demo use case.
$handleLine = {
    param($line)
    if ([string]::IsNullOrWhiteSpace($line)) { return }
    Write-Host $line
    if (-not $script:foundUrl) {
        $m = $tunnelUrlRegex.Match($line)
        if ($m.Success) {
            $script:foundUrl = $true
            $url = $m.Value
            Write-Host ""
            Write-Host "TUNNEL_URL: $url" -ForegroundColor Green
            Write-Host ""
            try {
                Set-Content -Path $urlFile -Value $url -Encoding utf8 -NoNewline
                Write-Host "Wrote $urlFile"
            } catch {
                Write-Warning "Failed to write $urlFile : $_"
            }

            # Phase Demo v2 Stream 3: auto-publish the ephemeral URL to Azure
            # Blob so the Azure-hosted front-end can pick it up without the
            # user ever seeing / pasting it. Skipped silently when the
            # storage SAS isn't configured — local development still works.
            $blobSas = $env:FARMER_TUNNEL_BLOB_URL
            if ([string]::IsNullOrWhiteSpace($blobSas)) {
                Write-Host "Skipping blob upload -- FARMER_TUNNEL_BLOB_URL not set" -ForegroundColor DarkGray
            } else {
                try {
                    $payload = [ordered]@{
                        url        = $url
                        updated_at = (Get-Date).ToUniversalTime().ToString('o')
                    } | ConvertTo-Json -Compress
                    $headers = @{
                        'x-ms-blob-type' = 'BlockBlob'
                    }
                    Invoke-RestMethod -Method Put -Uri $blobSas -Headers $headers `
                        -ContentType 'application/json' -Body $payload | Out-Null
                    Write-Host "Published tunnel URL to Azure Blob" -ForegroundColor Green
                } catch {
                    Write-Warning "Failed to publish tunnel URL to blob: $_"
                }
            }
        }
    }
}

$stdoutHandler = Register-ObjectEvent -InputObject $proc -EventName OutputDataReceived -Action {
    & $handleLine $EventArgs.Data
}
$stderrHandler = Register-ObjectEvent -InputObject $proc -EventName ErrorDataReceived -Action {
    & $handleLine $EventArgs.Data
}

try {
    [void]$proc.Start()
    $proc.BeginOutputReadLine()
    $proc.BeginErrorReadLine()

    # Tail until the process exits (or Ctrl+C kills the PS host, which will
    # orphan cloudflared on the default PS console -- acceptable for demo.
    # For a cleaner story, ship a trap handler; Windows PS 5.1's Ctrl+C
    # semantics make that more trouble than it's worth here).
    while (-not $proc.HasExited) {
        Start-Sleep -Milliseconds 250
    }

    Write-Host ""
    Write-Host "cloudflared exited with code $($proc.ExitCode)"
    exit $proc.ExitCode
}
finally {
    if ($stdoutHandler) { Unregister-Event -SourceIdentifier $stdoutHandler.Name -ErrorAction SilentlyContinue }
    if ($stderrHandler) { Unregister-Event -SourceIdentifier $stderrHandler.Name -ErrorAction SilentlyContinue }
    if ($proc -and -not $proc.HasExited) {
        try { $proc.Kill() } catch { }
    }
}
