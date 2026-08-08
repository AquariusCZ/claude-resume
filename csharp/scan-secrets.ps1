# AI Resume Stage 2 - S2-F secrets gate.
# Scans the csharp/ tree for credential-shaped strings (real secrets must never land in the repo).
# Uses `rg` when available, otherwise falls back to a PowerShell regex walk (same patterns).
# Exit code: 0 = clean, 1 = hits found (or scan infrastructure failure).
$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$scanRoot = $root   # csharp/ itself; bin/obj are excluded below

# Patterns: real credential shapes only. Test fixtures use clearly-fake values (sk-test..., etc.)
# which are deliberately NOT matched (length thresholds below the real shapes).
$patterns = @(
    'sk-[A-Za-z0-9_\-]{20,}',                                   # OpenAI/DeepSeek API keys (real: ~40+ chars)
    '\bghp_[A-Za-z0-9]{36,}',                                   # GitHub PAT (real: 40 chars)
    '\bxox[baprs]-[A-Za-z0-9-]{30,}',                           # Slack tokens
    'eyJ[A-Za-z0-9_\-]{30,}\.[A-Za-z0-9_\-]{10,}\.[A-Za-z0-9_\-]{10,}',  # JWT(真实头段一般 30+;测试假值 20-29 不命中)
    '\bAKIA[A-Z0-9]{16}',                                       # AWS access key id
    'feishu\.appSecret\s*[=:]\s*[A-Za-z0-9]{20,}',              # Feishu app secret assignment
    'appId\s*[=:]\s*cli_[A-Za-z0-9]{20,}'                       # Feishu app id assignment
)

$exclude = @(
    'bin', 'obj', '.git'
)

function Test-Patterns {
    param([string]$file, [string]$content)
    foreach ($p in $patterns) {
        $m = [regex]::Matches($content, $p)
        if ($m.Count -gt 0) {
            foreach ($match in $m) {
                Write-Host "HIT $file :: pattern '$p' :: '$($match.Value)'"
            }
            return $true
        }
    }
    return $false
}

$hits = @()

# Prefer ripgrep when present (fast, honors .gitignore).
if (Get-Command rg -ErrorAction SilentlyContinue) {
    Write-Host '==> scan-secrets: using rg'
    $rgArgs = @('--line-number', '--no-heading')
    foreach ($p in $patterns) { $rgArgs += @('-e', $p) }
    $files = & rg -l @rgArgs $scanRoot 2>$null
    if ($LASTEXITCODE -eq 1) { $files = @() }   # no matches
    foreach ($file in $files) {
        if ($file) {
            $full = Join-Path $scanRoot $file
            $content = Get-Content -Raw $full
            if (Test-Patterns $full $content) { $hits += $full }
        }
    }
}
else {
    Write-Host '==> scan-secrets: rg not found, using PowerShell fallback'
    Get-ChildItem -Path $scanRoot -Recurse -File |
        Where-Object {
            $exclude -notcontains $_.Directory.Name -and
            $_.FullName -notmatch '\\(bin|obj)\\' -and
            $_.FullName -notmatch '\.git\\'
        } |
        ForEach-Object {
            $content = Get-Content -Raw $_.FullName
            if (Test-Patterns $_.FullName $content) { $hits += $_.FullName }
        }
}

if ($hits.Count -gt 0) {
    Write-Host "SECRETS GATE FAILED: $($hits.Count) file(s) contain credential-shaped strings."
    exit 1
}

Write-Host '==> OK: secrets gate passed (0 credential-shaped hits)'
exit 0
