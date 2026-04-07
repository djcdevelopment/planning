# shakedown.ps1 — Validate all VM connectivity and file operations
# Run from repo root: .\scripts\shakedown.ps1
# Tests 7 categories per VM. All must pass before running workloads.

param(
    [string[]]$VmNames = @("claudefarm1", "claudefarm2", "claudefarm3"),
    [string]$SshUser = "claude"
)

$ErrorActionPreference = "Continue"
$passed = 0
$failed = 0
$total = 0

# Drive letter mapping
$driveMap = @{
    "claudefarm1" = "N"
    "claudefarm2" = "O"
    "claudefarm3" = "P"
}

function Test-Check {
    param([string]$Name, [scriptblock]$Action)
    $script:total++
    try {
        $result = & $Action
        if ($result -eq $true -or $null -eq $result) {
            Write-Host "  [PASS] $Name" -ForegroundColor Green
            $script:passed++
        } else {
            Write-Host "  [FAIL] $Name : $result" -ForegroundColor Red
            $script:failed++
        }
    } catch {
        Write-Host "  [FAIL] $Name : $($_.Exception.Message)" -ForegroundColor Red
        $script:failed++
    }
}

foreach ($vm in $VmNames) {
    $drive = $driveMap[$vm]
    if (-not $drive) {
        Write-Host "`nSkipping $vm (no drive mapping)" -ForegroundColor Yellow
        continue
    }

    Write-Host "`n=== $vm (drive $drive`:) ===" -ForegroundColor Cyan

    # 1. SSH connectivity
    Test-Check "SSH connectivity" {
        $output = ssh "${SshUser}@${vm}" "echo hello" 2>&1
        if ($LASTEXITCODE -ne 0) { return "SSH failed: $output" }
        if ($output -notmatch "hello") { return "Unexpected output: $output" }
        $true
    }

    # 2. CLAUDE.md exists
    Test-Check "CLAUDE.md exists on VM" {
        $output = ssh "${SshUser}@${vm}" "test -f ~/projects/CLAUDE.md && echo exists" 2>&1
        if ($output -notmatch "exists") { return "CLAUDE.md not found" }
        $true
    }

    # 3. SSH file write/read/delete
    Test-Check "SSH file write + read + delete" {
        $testFile = "~/projects/.shakedown-test-$(Get-Random).txt"
        ssh "${SshUser}@${vm}" "echo 'shakedown-ok' > $testFile" 2>&1
        $content = ssh "${SshUser}@${vm}" "cat $testFile" 2>&1
        ssh "${SshUser}@${vm}" "rm -f $testFile" 2>&1
        if ($content -notmatch "shakedown-ok") { return "Write/read failed: $content" }
        $true
    }

    # 4. .comms write (SSH) + read (mapped drive)
    Test-Check ".comms round-trip (SSH write, drive read)" {
        $token = "shakedown-$(Get-Random)"
        ssh "${SshUser}@${vm}" "mkdir -p ~/projects/.comms && echo '$token' > ~/projects/.comms/shakedown.txt" 2>&1
        Start-Sleep -Milliseconds 500  # SSHFS cache lag
        $drivePath = "${drive}:\projects\.comms\shakedown.txt"
        if (-not (Test-Path -LiteralPath $drivePath)) { return "File not visible on mapped drive" }
        $content = Get-Content -LiteralPath $drivePath -Raw
        ssh "${SshUser}@${vm}" "rm -f ~/projects/.comms/shakedown.txt" 2>&1
        if ($content -notmatch $token) { return "Content mismatch: expected $token" }
        $true
    }

    # 5. Plan file cross-visibility (write SSH, read drive)
    Test-Check "Plan file cross-visibility" {
        $token = "plan-test-$(Get-Random)"
        ssh "${SshUser}@${vm}" "mkdir -p ~/projects/plans && echo '$token' > ~/projects/plans/shakedown-plan.md" 2>&1
        Start-Sleep -Milliseconds 500
        $drivePath = "${drive}:\projects\plans\shakedown-plan.md"
        if (-not (Test-Path -LiteralPath $drivePath)) { return "Plan file not visible on drive" }
        $content = Get-Content -LiteralPath $drivePath -Raw
        ssh "${SshUser}@${vm}" "rm -f ~/projects/plans/shakedown-plan.md" 2>&1
        if ($content -notmatch $token) { return "Content mismatch" }
        $true
    }

    # 6. Git repo status
    Test-Check "Git accessible on VM" {
        $output = ssh "${SshUser}@${vm}" "cd ~/projects && git status --short 2>&1 || echo 'git-error'" 2>&1
        if ($output -match "git-error" -or $output -match "fatal") { return "Git not working: $output" }
        $true
    }

    # 7. Mapped drive readable
    Test-Check "Mapped drive readable" {
        $drivePath = "${drive}:\projects"
        if (-not (Test-Path -LiteralPath $drivePath)) { return "Drive ${drive}: not mounted or projects dir missing" }
        $items = Get-ChildItem -LiteralPath $drivePath -ErrorAction Stop
        $true
    }
}

# Summary
Write-Host "`n$('=' * 50)" -ForegroundColor White
Write-Host "  Results: $passed passed, $failed failed (out of $total)" -ForegroundColor $(if ($failed -gt 0) { "Red" } else { "Green" })
Write-Host "$('=' * 50)" -ForegroundColor White

if ($failed -gt 0) {
    Write-Host "`nFAILED — fix issues before running workloads." -ForegroundColor Red
    exit 1
} else {
    Write-Host "`nALL PASSED — infrastructure ready." -ForegroundColor Green
}
