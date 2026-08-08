$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$temp = Join-Path ([IO.Path]::GetTempPath()) ('claude-resume-auto-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force -Path $temp | Out-Null
$oldAppDir = $env:CLAUDE_RESUME_APP_DIR
$oldPath = $env:Path
$oldCalls = $env:AUTO_RESUME_CALLS
$oldClaudeProjects = $env:CLAUDE_RESUME_CLAUDE_PROJECTS_DIR
$env:CLAUDE_RESUME_APP_DIR = $temp
$env:CLAUDE_RESUME_CLAUDE_PROJECTS_DIR = Join-Path $temp 'claude-projects'
. (Join-Path $repo 'src\lib.ps1')

$failures = New-Object System.Collections.Generic.List[string]
function Check([string]$Name, [bool]$Condition, [string]$Detail='') {
  if($Condition){ Write-Host ('[PASS] ' + $Name) }
  else { $failures.Add($Name + $(if($Detail){ ': ' + $Detail }else{ '' })); Write-Host ('[FAIL] ' + $Name) }
}
function Wait-Until([scriptblock]$Condition, [int]$TimeoutMs=10000) {
  $until = [DateTime]::UtcNow.AddMilliseconds($TimeoutMs)
  while([DateTime]::UtcNow -lt $until){ if(& $Condition){ return $true }; [Threading.Thread]::Sleep(100) }
  return [bool](& $Condition)
}
function New-TestConfig([string]$Cycle, $Projects, [bool]$Enabled=$true) {
  $cfg = Get-CcuConfig
  $cfg.enabled=$Enabled; $cfg.armed=$Enabled; $cfg.armCycleId=$Cycle; $cfg.selected=@($Projects)
  $cfg.skipPermissions=$false; $cfg.continuous=$false; $cfg.probeIntervalMinutes=2
  $cfg.resumePrompt='continue'; $cfg.probeModel='haiku'; $cfg.resumeModel=''
  return $cfg
}
function New-TestState([string]$Cycle, [bool]$SawLimited=$true) {
  $st = Get-CcuState
  $st.cycleId=$Cycle; $st.phase='waiting'; $st.sawLimited=$SawLimited; $st.projectStatus=@{}
  $st.lastProbeUtc=[DateTimeOffset]::UtcNow.AddMinutes(-10).ToString('o'); $st.limitedRefires=0
  return $st
}

$checkerProcesses = New-Object System.Collections.Generic.List[object]
try {
  [IO.File]::WriteAllText($script:ConfigPath, '{"perProjectTimeoutMinutes":30}', (New-Object Text.UTF8Encoding($false)))
  $cfg = Get-CcuConfig
  Check 'Legacy background timeout is normalized to unlimited' ($cfg.perProjectTimeoutMinutes -eq 0) ([string]$cfg.perProjectTimeoutMinutes)

  $newState = Get-CcuState; $newState.cycleId='cycle-new'; $newState.phase='waiting'; [void](Set-CcuState $newState -Force)
  $oldState = Get-CcuState; $oldState.cycleId='cycle-old'; $oldState.phase='done'
  Check 'A stale checker cannot overwrite a newer cycle state' (-not [bool](Set-CcuState $oldState))
  Check 'The newer cycle state remains authoritative' ((Get-CcuState).cycleId -eq 'cycle-new') ((Get-CcuState).cycleId)

  $script:exitChecks = 0; $script:starts = 0; $script:stoppedPid = $null; $script:clearCount = 0
  function Get-ClaudeCmd { return 'fake-claude.cmd' }
  function Start-Sleep { param([int]$Milliseconds) }
  function Register-CcuBackgroundLaunch { return $true }
  function Register-CcuBackgroundChild { return $true }
  function Clear-CcuBackgroundChild { param([int]$ProcessId) $script:clearCount++; return $true }
  function Stop-ProcessTree { param([int]$ProcessId) $script:stoppedPid=$ProcessId; return $true }
  function Start-Process {
    param($FilePath, $ArgumentList, $WorkingDirectory, [switch]$NoNewWindow, [switch]$PassThru,
          $RedirectStandardOutput, $RedirectStandardError)
    $script:starts++
    [IO.File]::WriteAllText($RedirectStandardOutput, '{"type":"result","is_error":false}', (New-Object Text.UTF8Encoding($false)))
    [IO.File]::WriteAllText($RedirectStandardError, '', (New-Object Text.UTF8Encoding($false)))
    $fake = New-Object psobject -Property @{ Id=4242; Handle=[intptr]1; ExitCode=0; StartTime=[datetime]::UtcNow }
    $fake | Add-Member ScriptProperty HasExited { $script:exitChecks++; return ($script:exitChecks -ge 3) }
    return $fake
  }

  $project = [pscustomobject]@{ name='fake-project'; path=$temp }
  $preCancelled = Invoke-ClaudeResume -Project $project -TimeoutMin 0 -CancelFlag { return $true }
  Check 'Cancellation is checked before the AI process starts' ($preCancelled.status -eq 'stopped' -and $script:starts -eq 0) ($preCancelled.status + '/' + $script:starts)

  $script:exitChecks=0
  $result = Invoke-ClaudeResume -Project $project -TimeoutMin 0
  Check 'Unlimited resume waits for natural completion' ($result.status -eq 'success') ($result.status)
  Check 'Unlimited resume does not invoke timeout termination' ($null -eq $script:stoppedPid) ([string]$script:stoppedPid)
  Check 'Unlimited resume crosses the old zero-minute deadline' ($script:exitChecks -ge 3) ([string]$script:exitChecks)

  $script:exitChecks=0; $script:cancelChecks=0; $script:stoppedPid=$null
  $cancel = { $script:cancelChecks++; return ($script:cancelChecks -ge 2) }
  $cancelledResult = Invoke-ClaudeResume -Project $project -TimeoutMin 0 -CancelFlag $cancel
  Check 'Unlimited resume remains explicitly cancellable' ($cancelledResult.status -eq 'stopped') ($cancelledResult.status)
  Check 'Confirmed cancellation terminates the process tree' ($script:stoppedPid -eq 4242) ([string]$script:stoppedPid)

  function Stop-ProcessTree { param([int]$ProcessId) return $false }
  $script:exitChecks=0; $script:cancelChecks=0
  $failedStop = Invoke-ClaudeResume -Project $project -TimeoutMin 0 -CancelFlag { $script:cancelChecks++; return ($script:cancelChecks -ge 2) }
  Check 'A failed process-tree termination is not reported as stopped' ($failedStop.status -eq 'stop-failed') ($failedStop.status)

  foreach($name in 'Get-ClaudeCmd','Start-Sleep','Register-CcuBackgroundLaunch','Register-CcuBackgroundChild','Clear-CcuBackgroundChild','Stop-ProcessTree','Start-Process'){
    Remove-Item -Path ('Function:\' + $name) -Force -ErrorAction SilentlyContinue
  }
  . (Join-Path $repo 'src\lib.ps1')

  [void](Set-CcuConfig (New-TestConfig 'cycle-newer' @()))
  [void](Set-CcuState (New-TestState 'cycle-newer' $false) -Force)
  $capturedOld=New-TestState 'cycle-old-init' $true
  Check 'A superseded checker cannot force-initialize state over a newer cycle' (-not [bool](Initialize-CcuCycleState $capturedOld 'cycle-old-init'))
  Check 'Forced initialization rejection preserves the newer state' ((Get-CcuState).cycleId -eq 'cycle-newer') ((Get-CcuState).cycleId)

  $negativeProject=[pscustomobject]@{name='negative-project';path=$temp}
  [void](Register-CcuBackgroundLaunch $negativeProject 'negative-cim')
  $negativeEntry=[ordered]@{version=2;status='active';pid=2147483000;parentPid=$PID;runKey='negative-cim';projectPath=$temp;projectName='negative-project';startedUtc=[DateTimeOffset]::UtcNow.ToString('o');registeredUtc=[DateTimeOffset]::UtcNow.ToString('o');updatedUtc=[DateTimeOffset]::UtcNow.ToString('o')}
  Write-CcuJsonAtomic $script:BackgroundChildPath $negativeEntry
  function Get-CcuProcessProbe { return [pscustomobject]@{status='failed';process=$null} }
  Check 'CIM failure is not reported as a successful process-tree stop' (-not [bool](Stop-ProcessTree 2147483000))
  Check 'CIM failure keeps orphan recovery fail-closed' (-not [bool](Recover-CcuBackgroundChild))
  Check 'CIM failure preserves the only child registry' (Test-Path $script:BackgroundChildPath)
  Remove-Item -Path 'Function:\Get-CcuProcessProbe' -Force; . (Join-Path $repo 'src\lib.ps1')
  Remove-CcuBackgroundRegistryFiles

  [void](Register-CcuBackgroundLaunch $negativeProject 'negative-start')
  $fakeProcess=[pscustomobject]@{Id=2147482999}
  function Get-CcuProcessProbe { param([int]$ProcessId) return [pscustomobject]@{status='found';process=[pscustomobject]@{ProcessId=$ProcessId;ParentProcessId=$PID;CreationDate='invalid';CommandLine='claude.cmd --continue'}} }
  function Get-CcuProcessStartUtc { return $null }
  Check 'A child without a verifiable start time is rejected' (-not [bool](Register-CcuBackgroundChild $fakeProcess $negativeProject 'negative-start'))
  $unverified=Get-Content $script:BackgroundChildPath -Raw -Encoding UTF8 | ConvertFrom-Json
  Check 'Rejected child remains fail-closed in the registry' ($unverified.status -eq 'unverified' -and [int]$unverified.pid -eq 2147482999) ($unverified.status)
  foreach($name in 'Get-CcuProcessProbe','Get-CcuProcessStartUtc'){ Remove-Item -Path ('Function:\'+$name) -Force }
  . (Join-Path $repo 'src\lib.ps1'); Remove-CcuBackgroundRegistryFiles

  $tempRegistry=$script:BackgroundChildPath+'.tmp-recovery'
  [IO.File]::WriteAllText($tempRegistry, ($negativeEntry | ConvertTo-Json -Depth 8), (New-Object Text.UTF8Encoding($false)))
  Check 'A complete temporary registry generation is recovered' ([bool](Recover-CcuBackgroundChild))
  Check 'Recovered temporary registry is removed' (-not (Test-Path $tempRegistry))

  $launchOnly=[ordered]@{version=2;status='launching';pid=0;parentPid=2147482998;runKey='launch-only';projectPath=$temp;projectName='negative-project';startedUtc='';launchRequestedUtc=[DateTimeOffset]::UtcNow.ToString('o');updatedUtc=[DateTimeOffset]::UtcNow.ToString('o')}
  Write-CcuJsonAtomic $script:BackgroundChildPath $launchOnly
  Check 'A crash before spawn leaves a launch intent that recovers safely' ([bool](Recover-CcuBackgroundChild))
  Check 'Launch intent with no child is cleared' (-not (Test-Path $script:BackgroundChildPath))

  $bin=Join-Path $temp 'bin'; $projectA=Join-Path $temp 'project-a'; $projectB=Join-Path $temp 'project-b'
  New-Item -ItemType Directory -Force -Path $bin,$projectA,$projectB | Out-Null
  $calls=Join-Path $temp 'calls.log'; $env:AUTO_RESUME_CALLS=$calls; $env:Path=$bin+';'+$oldPath
  $fakeClaude=@'
@echo off
echo %* | findstr /C:"--continue" >nul
if errorlevel 1 (
  echo {"type":"result","is_error":false}
  exit /b 0
)
echo continue:%CD%^|%*>>"%AUTO_RESUME_CALLS%"
powershell.exe -NoProfile -Command "Start-Sleep -Seconds 30"
echo {"type":"result","is_error":false}
'@
  [IO.File]::WriteAllText((Join-Path $bin 'claude.cmd'), $fakeClaude, (New-Object Text.ASCIIEncoding))
  $projects=@([pscustomobject]@{name='project-a';path=$projectA},[pscustomobject]@{name='project-b';path=$projectB})

  [void](Set-CcuConfig (New-TestConfig 'cycle-a' $projects))
  [void](Set-CcuState (New-TestState 'cycle-a') -Force)
  $checker=Microsoft.PowerShell.Management\Start-Process -FilePath 'powershell.exe' -ArgumentList '-NoProfile','-ExecutionPolicy','Bypass','-File',(Join-Path $repo 'src\checker.ps1') -PassThru -WindowStyle Hidden
  $checkerProcesses.Add($checker)
  Check 'Checker starts the first fake project' (Wait-Until { (Test-Path $calls) -and ((Get-Content $calls -ErrorAction SilentlyContinue) -match 'continue:') } 10000)

  [void](Set-CcuConfig (New-TestConfig 'cycle-b' $projects $false)); [void](Set-CcuState (New-TestState 'cycle-b' $false) -Force)
  [void](Set-CcuConfig (New-TestConfig 'cycle-c' $projects $true)); [void](Set-CcuState (New-TestState 'cycle-c' $false) -Force)
  Check 'Old checker exits after rapid disarm and re-arm' ($checker.WaitForExit(12000))
  $continued=@(Get-Content $calls -ErrorAction SilentlyContinue | Where-Object { $_ -like 'continue:*' })
  Check 'Old cycle never starts the second project' ($continued.Count -eq 1) ([string]$continued.Count)
  $liveCfg=Get-CcuConfig; $liveState=Get-CcuState
  Check 'Old checker does not overwrite the new config cycle' ($liveCfg.enabled -and $liveCfg.armCycleId -eq 'cycle-c') ($liveCfg.armCycleId)
  Check 'Old checker does not overwrite the new state cycle' ($liveState.cycleId -eq 'cycle-c' -and $liveState.phase -eq 'waiting') ($liveState.cycleId+'/'+$liveState.phase)
  Check 'Cancelled checker clears its child registry' (-not (Test-Path $script:BackgroundChildPath))

  if(Test-Path $calls){ [IO.File]::WriteAllText($calls,'') }
  [void](Set-CcuConfig (New-TestConfig 'cycle-orphan' @($projects[0])))
  [void](Set-CcuState (New-TestState 'cycle-orphan') -Force)
  $crashed=Microsoft.PowerShell.Management\Start-Process -FilePath 'powershell.exe' -ArgumentList '-NoProfile','-ExecutionPolicy','Bypass','-File',(Join-Path $repo 'src\checker.ps1') -PassThru -WindowStyle Hidden
  $checkerProcesses.Add($crashed)
  Check 'Crash scenario starts a registered fake child' (Wait-Until {
    if(-not (Test-Path $script:BackgroundChildPath) -or -not (Test-Path $calls) -or -not ((Get-Content $calls -ErrorAction SilentlyContinue) -match 'continue:')){ return $false }
    try { return ([int]((Get-Content $script:BackgroundChildPath -Raw -Encoding UTF8 | ConvertFrom-Json).pid) -gt 0) } catch { return $false }
  } 10000)
  $entry=Get-Content $script:BackgroundChildPath -Raw -Encoding UTF8 | ConvertFrom-Json
  Microsoft.PowerShell.Management\Stop-Process -Id $crashed.Id -Force -ErrorAction SilentlyContinue
  [void]$crashed.WaitForExit(5000)
  Check 'Forced checker exit leaves a recoverable child registry' (Test-Path $script:BackgroundChildPath)

  [void](Set-CcuConfig (New-TestConfig 'cycle-after-crash' @($projects[0]) $false))
  [void](Set-CcuState (New-TestState 'cycle-after-crash' $false) -Force)
  $recovery=Microsoft.PowerShell.Management\Start-Process -FilePath 'powershell.exe' -ArgumentList '-NoProfile','-ExecutionPolicy','Bypass','-File',(Join-Path $repo 'src\checker.ps1') -PassThru -WindowStyle Hidden
  $checkerProcesses.Add($recovery)
  Check 'Next checker completes orphan recovery before normal work' ($recovery.WaitForExit(12000))
  Check 'Recovered orphan registry is removed' (-not (Test-Path $script:BackgroundChildPath))
  Check 'Recovered fake AI process is no longer running' (-not (Get-CcuProcess ([int]$entry.pid))) ([string]$entry.pid)

  $taskLine=Get-Content -Raw (Join-Path $repo 'src\install.ps1')
  Check 'Installer removes the Scheduled Task execution limit' ($taskLine -match 'ExecutionTimeLimit\s+\(\[TimeSpan\]::Zero\)')
} finally {
  foreach($p in $checkerProcesses){ try { if($p -and -not $p.HasExited){ Microsoft.PowerShell.Management\Stop-Process -Id $p.Id -Force } } catch {} }
  try { if(Test-Path $script:BackgroundChildPath){ $entry=Get-Content $script:BackgroundChildPath -Raw|ConvertFrom-Json; [void](Stop-ProcessTree ([int]$entry.pid)) } } catch {}
  $env:CLAUDE_RESUME_APP_DIR=$oldAppDir; $env:CLAUDE_RESUME_CLAUDE_PROJECTS_DIR=$oldClaudeProjects; $env:Path=$oldPath; $env:AUTO_RESUME_CALLS=$oldCalls
  Remove-Item -LiteralPath $temp -Recurse -Force -ErrorAction SilentlyContinue
}

if($failures.Count){
  Write-Host ''
  Write-Host ('Failures: ' + ($failures -join '; '))
  exit 1
}
Write-Host ''
Write-Host 'Auto-resume timeout, cycle, and crash-recovery regression passed.'
