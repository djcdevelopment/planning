# Pre-commit secret scanner. Runs against staged content (default), an arbitrary
# unified diff (-Diff), or a built-in fixture set (-Test).
#
# Local hook entry: .githooks/pre-commit invokes this script.
# CI second-line: .github/workflows/secrets-scan.yml runs this with -Diff against the PR diff.
#
# Exit codes:
#   0 - clean (or self-test all passed)
#   1 - secret(s) detected in staged content
#   2 - usage / setup error (couldn't read diff, etc.)

[CmdletBinding(DefaultParameterSetName = 'Staged')]
param(
    [Parameter(ParameterSetName = 'Diff', Mandatory)][string]$Diff,
    [Parameter(ParameterSetName = 'Test', Mandatory)][switch]$Test
)

# --- Pattern catalog ---
# Each entry: a friendly name + a .NET regex. Patterns are intentionally specific
# (require the prefix literal) so we don't false-positive on git SHAs etc. The
# 40+ char lengths match real-world key formats; tighter is safer than looser
# because the failure mode of a false positive is "blocked commit, dev annoyed",
# while a false negative is "credential on GitHub."
$Patterns = @(
    @{ Name = 'OpenAI project key';   Regex = 'sk-proj-[A-Za-z0-9_-]{40,}' }
    @{ Name = 'Anthropic key';        Regex = 'sk-ant-[A-Za-z0-9_-]{40,}' }
    @{ Name = 'OpenAI legacy';        Regex = 'sk-(?!proj-|ant-)[A-Za-z0-9]{40,}' }
    @{ Name = 'GitHub token';         Regex = 'gh[pousr]_[A-Za-z0-9]{36,}' }
    @{ Name = 'Slack token';          Regex = 'xox[baprs]-[A-Za-z0-9-]{20,}' }
    @{ Name = 'AWS access key';       Regex = 'AKIA[0-9A-Z]{16}' }
    @{ Name = 'Private key block';    Regex = '-----BEGIN [A-Z ]+PRIVATE KEY-----' }
)

# --- Allowlist ---
# Files matching these patterns are skipped wholesale (templates, lockfiles,
# the scanner's own test fixtures). Lines containing the literal token
# `secret-scan: ignore` are skipped per-line so docs / comments can show
# example keys without tripping the scan.
function Test-AllowlistedPath([string]$path) {
    if (-not $path) { return $false }
    $lower = $path.ToLowerInvariant()
    if ($lower -like '*.example' -or $lower -like '*.template' -or $lower -like '*.lock') { return $true }
    if ($lower -like 'infra/secret-scan-test-fixtures/*' -or $lower -like 'infra\secret-scan-test-fixtures\*') { return $true }
    return $false
}
function Test-AllowlistedLine([string]$line) {
    return $line -match 'secret-scan:\s*ignore'
}

# --- Diff scanner ---
# Walks a unified diff. Tracks the current file via `+++ b/<path>` headers.
# Tracks the current new-file line number by parsing `@@ -a,b +c,d @@` hunk headers
# and incrementing on each '+' or ' ' (context) line. Emits a finding per match.
function Scan-Diff([string]$diffText) {
    $findings = @()
    $currentFile = $null
    $currentLine = 0
    $skipFile = $false

    foreach ($rawLine in ($diffText -split "`n")) {
        $line = $rawLine.TrimEnd("`r")

        if ($line -match '^\+\+\+ b/(.+)$') {
            $currentFile = $Matches[1]
            $skipFile = (Test-AllowlistedPath $currentFile)
            $currentLine = 0
            continue
        }
        if ($line -match '^---') { continue }
        if ($line -match '^@@ -\d+(?:,\d+)? \+(\d+)') {
            $currentLine = [int]$Matches[1] - 1
            continue
        }
        if ($skipFile) { continue }

        # Track line number through context (' ') and added ('+') lines; deletions ('-') don't advance.
        if ($line.StartsWith(' ') -or ($line.StartsWith('+') -and -not $line.StartsWith('+++'))) {
            $currentLine++
        } else {
            continue
        }

        # Only scan added lines; existing content shouldn't trigger a NEW commit's scan.
        if (-not $line.StartsWith('+')) { continue }
        if (Test-AllowlistedLine $line) { continue }

        $payload = $line.Substring(1)  # strip the '+' marker
        foreach ($p in $Patterns) {
            $matches = [regex]::Matches($payload, $p.Regex)
            foreach ($m in $matches) {
                $redacted = if ($m.Value.Length -gt 12) { $m.Value.Substring(0, 12) + '...' } else { $m.Value }
                $findings += [PSCustomObject]@{
                    File    = $currentFile
                    Line    = $currentLine
                    Pattern = $p.Name
                    Match   = $redacted
                }
            }
        }
    }

    # NOTE: always call this as `@(Scan-Diff ...)` -- PowerShell unwraps
    # single-element and empty arrays on function return.
    return $findings
}

# --- Self-test mode ---
if ($PSCmdlet.ParameterSetName -eq 'Test') {
    $fixturesDir = Join-Path $PSScriptRoot 'secret-scan-test-fixtures'
    if (-not (Test-Path $fixturesDir)) {
        Write-Host "[secret-scan] FAIL: fixtures dir not found at $fixturesDir" -ForegroundColor Red
        exit 2
    }

    $expected = @{
        'positive-openai.txt'      = 1
        'positive-anthropic.txt'   = 1
        'positive-github.txt'      = 1
        'positive-private-key.txt' = 1
        'negative-not-a-secret.txt' = 0
        'negative-allowlist.txt'   = 0
        'negative-template.example' = 0
    }

    $failed = @()
    foreach ($file in $expected.Keys) {
        $path = Join-Path $fixturesDir $file
        if (-not (Test-Path $path)) { $failed += "missing fixture: $file"; continue }
        $content = Get-Content $path -Raw
        # Synthesize a unified-diff so the scanner sees the file as added content.
        # Allowlisted-extension files (.example) get tested against the path-based allowlist via a
        # special header. We bypass the test-fixtures path allowlist here by overriding the file
        # path the diff reports so each fixture is scanned on its own merits.
        # Reported diff-header path. .example files still get scanned through
        # the path-allowlist check (so we verify the allowlist works), others
        # are reported under test-cases/ so the fixtures-dir allowlist doesn't
        # short-circuit them.
        $reportedPath = if ($file.EndsWith('.example')) { "test-cases/$file" } else { "test-cases/$file" }
        $lines = @($content -split "`n")
        $diffLines = @("--- a/$reportedPath", "+++ b/$reportedPath", "@@ -0,0 +1,$($lines.Count) @@")
        foreach ($l in $lines) { $diffLines += "+" + $l }
        $synthetic = $diffLines -join "`n"
        $found = @(Scan-Diff $synthetic)
        $count = $found.Count
        if ($count -ne $expected[$file]) {
            $failed += "  $file : expected $($expected[$file]) match(es), got $count"
        }
    }

    if ($failed.Count -gt 0) {
        Write-Host "[secret-scan] self-test FAILED:" -ForegroundColor Red
        $failed | ForEach-Object { Write-Host $_ }
        exit 1
    }
    $positives = ($expected.Keys | Where-Object { $expected[$_] -gt 0 }).Count
    $negatives = ($expected.Keys | Where-Object { $expected[$_] -eq 0 }).Count
    Write-Host "[secret-scan] self-test: $positives positives, $negatives negatives, all pass" -ForegroundColor Green
    exit 0
}

# --- Diff source ---
if ($PSCmdlet.ParameterSetName -eq 'Diff') {
    if ($Diff -eq '-') {
        $diffText = [Console]::In.ReadToEnd()
    } elseif (Test-Path $Diff) {
        $diffText = Get-Content $Diff -Raw
    } else {
        Write-Host "[secret-scan] FAIL: diff file not found: $Diff" -ForegroundColor Red
        exit 2
    }
} else {
    # Default: scan staged content
    $diffText = & git diff --cached -U0 2>&1 | Out-String
    if ($LASTEXITCODE -ne 0) {
        Write-Host "[secret-scan] FAIL: git diff --cached returned $LASTEXITCODE" -ForegroundColor Red
        exit 2
    }
}

$findings = @(Scan-Diff $diffText)
if ($findings.Count -eq 0) {
    exit 0
}

Write-Host "[secret-scan] BLOCKED: $($findings.Count) potential secret(s) in staged content:" -ForegroundColor Red
foreach ($f in $findings) {
    Write-Host ("  {0}:{1}  pattern={2}  match={3}" -f $f.File, $f.Line, $f.Pattern, $f.Match) -ForegroundColor Yellow
}
Write-Host ""
Write-Host "If a match is intentional (test fixture, doc example, etc.), add a 'secret-scan: ignore'"
Write-Host "comment on the same line, or move the file to .example / .template / a fixtures path."
Write-Host "To bypass this hook entirely (only when you're sure):  git commit --no-verify"
exit 1
