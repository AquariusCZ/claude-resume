# AI Resume Stage 2 - S2-A local build & test script.
# Runs `dotnet build -warnaserror` followed by `dotnet test` on the whole solution.
$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$solution = Join-Path $root 'AiResume.sln'

Write-Host '==> dotnet build (warnaserror)'
dotnet build $solution -c Debug -warnaserror --nologo
if ($LASTEXITCODE -ne 0) {
    Write-Host "BUILD FAILED (exit $LASTEXITCODE)"
    exit $LASTEXITCODE
}

Write-Host '==> dotnet test'
dotnet test $solution -c Debug --no-build --nologo
if ($LASTEXITCODE -ne 0) {
    Write-Host "TEST FAILED (exit $LASTEXITCODE)"
    exit $LASTEXITCODE
}

Write-Host '==> secrets gate (S2-F)'
& (Join-Path $root 'scan-secrets.ps1')
if ($LASTEXITCODE -ne 0) {
    Write-Host "SECRETS GATE FAILED (exit $LASTEXITCODE)"
    exit $LASTEXITCODE
}

Write-Host '==> OK: build, tests and secrets gate passed'
exit 0
