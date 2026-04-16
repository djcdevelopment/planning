# One-shot git-hooks installer. Points git at .githooks/ (tracked in the repo)
# so the secret-scanner pre-commit runs on every `git commit`. Idempotent --
# safe to re-run.
#
# Usage:  .\scripts\install-githooks.ps1

$ErrorActionPreference = 'Stop'
$repoRoot = (& git rev-parse --show-toplevel).Trim()
if (-not $repoRoot) {
    Write-Host "[git-hooks] FAIL: not inside a git repo" -ForegroundColor Red
    exit 1
}

& git config --local core.hooksPath .githooks
$current = (& git config --local core.hooksPath).Trim()
if ($current -ne '.githooks') {
    Write-Host "[git-hooks] FAIL: core.hooksPath is '$current', expected '.githooks'" -ForegroundColor Red
    exit 1
}

# Git for Windows respects executable bit via its internal perms; the bash shim
# runs under Git Bash which reads it as a bash script regardless. This chmod is
# a no-op on Windows but makes the hook executable on WSL / Linux / Mac clones.
$hook = Join-Path $repoRoot '.githooks/pre-commit'
if (Test-Path $hook) {
    try { & git update-index --chmod=+x --add .githooks/pre-commit 2>$null } catch { }
}

Write-Host "[git-hooks] installed; core.hooksPath = .githooks" -ForegroundColor Green
Write-Host "[git-hooks] self-test:  .\infra\check-staged-secrets.ps1 -Test"
