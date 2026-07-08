<#
  checker.ps1 - the stateless engine, run by a Scheduled Task every ~2 min.
  It waits for the 5h usage window to reset, confirms via a live probe, then
  resumes the selected projects once (one-shot), with a git dirty-guard.
  -DryRun computes and logs the decision without probing or launching claude.
#>
param([switch]$DryRun)
Set-StrictMode -Off
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'lib.ps1')

# ---- single-instance guard (backup to the task's 'no new instance') ----
$lockPath = Join-Path $script:AppDir 'checker.lock'
try {
  $lock = [System.IO.File]::Open($lockPath, 'OpenOrCreate', 'ReadWrite', 'None')
} catch { Write-CcuLog 'checker: another instance running, skip tick' 'info'; return }

try {
  $cfg = Get-CcuConfig
  if(-not $cfg.enabled -and -not $DryRun){ return }     # disarmed -> nothing to do (but Preview/DryRun always computes)
  $selected = @($cfg.selected)
  if($selected.Count -eq 0){ Write-CcuLog 'checker: enabled but no projects selected' 'warn'; return }

  $state = Get-CcuState
  # projectStatus as a hashtable (JSON round-trips it to a PSObject)
  $pstat = @{}
  if($state.projectStatus){ foreach($pp in $state.projectStatus.PSObject.Properties){ $pstat[$pp.Name] = $pp.Value } }

  $ri = Get-CcuResetInfo
  if(-not $ri.ok){ Write-CcuLog 'checker: ccusage read failed -> skip tick (fail-closed)' 'warn'; return }

  $nowU = [DateTimeOffset]::UtcNow
  $margin = [double]$cfg.safetyMarginSeconds

  # ---- adopt the live window; never fire mid-window ----
  if($ri.hasActive){
    if($state.targetId -ne $ri.blockId){
      # a NEW window -> adopt it, reset per-window bookkeeping
      $state.targetId    = $ri.blockId
      $state.targetEndUtc = $ri.resetUtc.ToString('o')
      $state.firedForId  = $null
      $pstat = @{}
    }
    if($ri.secondsUntilReset -gt 0){
      $state.phase = 'waiting'
      $state.projectStatus = $pstat
      Set-CcuState $state
      Write-CcuLog ('checker: window live, resets in ' + (Format-Countdown $ri.secondsUntilReset)) 'info'
      return
    }
  }

  # ---- window ended (or blocks empty). Decide whether to fire. ----
  $haveTarget = [bool]$state.targetId
  $targetEnd = if($state.targetEndUtc){
    [DateTimeOffset]::Parse($state.targetEndUtc, [Globalization.CultureInfo]::InvariantCulture, [Globalization.DateTimeStyles]::RoundtripKind)
  } else { $nowU }

  # only act on a window we actually adopted while it was live (avoids cold-start surprise fire)
  if(-not $haveTarget){
    Write-CcuLog 'checker: no adopted window to wait on (account not currently limited) -> idle' 'info'
    $state.phase = 'idle'; $state.projectStatus = $pstat; Set-CcuState $state
    return
  }
  if($nowU -lt $targetEnd.AddSeconds($margin)){
    # window technically has an id but we are within margin; wait a tick
    $state.projectStatus = $pstat; Set-CcuState $state
    return
  }

  $alreadyFired = ($state.firedForId -eq $state.targetId)
  $allDone = $true
  foreach($sel in $selected){ if($pstat[$sel.path] -ne 'success'){ $allDone = $false } }
  if($alreadyFired -and $allDone){
    $state.phase = 'done'; $state.projectStatus = $pstat; Set-CcuState $state
    if(-not $cfg.continuous){ $cfg.enabled = $false; Set-CcuConfig $cfg }
    return
  }

  if($DryRun){
    Write-CcuLog ('DRY-RUN: window ended -> would probe, then resume ' + $selected.Count + ' project(s): ' + (($selected | ForEach-Object { $_.name }) -join ', ')) 'info'
    return
  }

  # ---- Gate B: live probe (the only thing that also proves the weekly cap is clear) ----
  Write-CcuLog 'checker: window ended -> probing account readiness' 'info'
  $probe = Test-ClaudeReady -Model $cfg.probeModel
  if(-not $probe.ready){
    if($probe.reason -eq 'limited'){
      $state.phase = 'weekly-backoff'; $state.projectStatus = $pstat; Set-CcuState $state
      Write-CcuLog ('checker: still limited (weekly cap) -> back off ' + $cfg.weeklyBackoffMinutes + 'm') 'warn'
    } else {
      $state.projectStatus = $pstat; Set-CcuState $state
      Write-CcuLog ('checker: probe not ready (' + $probe.reason + ') -> retry next tick') 'warn'
    }
    return
  }

  # ---- FIRE: mark fired FIRST (idempotency), then resume sequentially ----
  $state.firedForId = $state.targetId
  $state.phase = 'resuming'
  $state.projectStatus = $pstat
  Set-CcuState $state
  Write-CcuLog 'checker: account ready -> resuming selected projects' 'ok'

  foreach($sel in $selected){
    if($pstat[$sel.path] -eq 'success'){ Write-CcuLog ('skip (already done): ' + $sel.name) 'info'; continue }

    if($cfg.skipPermissions){
      try {
        $g = Protect-GitRepo -Path $sel.path -Mode $cfg.dirtyGuard
        if($g.wasDirty){ Write-CcuLog ('git-guard: ' + $sel.name + ' was dirty -> ' + $g.action + ' (' + $g.ref + ')') 'warn' }
      } catch { Write-CcuLog ('git-guard error on ' + $sel.name + ': ' + $_.Exception.Message) 'warn' }
    }

    Write-CcuLog ('resume -> ' + $sel.name + '  [' + $sel.path + ']') 'launch'
    $r = Invoke-ClaudeResume -Project $sel -Prompt $cfg.resumePrompt `
           -SkipPermissions:([bool]$cfg.skipPermissions) -Model $cfg.resumeModel `
           -TimeoutMin ([int]$cfg.perProjectTimeoutMinutes)
    $pstat[$sel.path] = $r.status
    $state.projectStatus = $pstat
    Set-CcuState $state
    $lvl = if($r.status -eq 'success'){ 'ok' } else { 'error' }
    Write-CcuLog ($sel.name + ' -> ' + $r.status + ' (exit ' + $r.exitCode + ')') $lvl

    if($r.status -eq 'limited'){
      Write-CcuLog 'checker: hit limit mid-run -> stop, weekly back-off' 'warn'
      $state.phase = 'weekly-backoff'; Set-CcuState $state
      return
    }
  }

  $state.phase = 'done'; $state.projectStatus = $pstat; Set-CcuState $state
  if(-not $cfg.continuous){ $cfg.enabled = $false; Set-CcuConfig $cfg; Write-CcuLog 'checker: one-shot complete -> disarmed' 'ok' }
  else { Write-CcuLog 'checker: resume run complete (continuous mode)' 'ok' }
}
finally {
  try { $lock.Close(); $lock.Dispose() } catch {}
  try { [System.IO.File]::Delete($lockPath) } catch {}
}
