<#
  checker.ps1 - stateless engine, run by a Scheduled Task every ~2 min.
  PROBE-DRIVEN: it does NOT trust any estimated reset time (ccusage blocks and the
  jsonl window-chaining are both only estimates that can differ from claude.ai). It
  waits while the account is rate-limited and resumes the moment a LIVE probe shows
  the account is usable again -> always fires at the real reset.
  Cost control: only probes near the estimated reset (or once known-limited), throttled
  to ~4 min; a rate-limited probe is rejected server-side so it doesn't consume quota.
  -DryRun reports the plan without probing or resuming.
#>
param([switch]$DryRun)
Set-StrictMode -Off
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'lib.ps1')

$lockPath = Join-Path $script:AppDir 'checker.lock'
try { $lock = [System.IO.File]::Open($lockPath, 'OpenOrCreate', 'ReadWrite', 'None') }
catch { Write-CcuLog 'checker: another instance running, skip tick' 'info'; return }

try {
  $cfg = Get-CcuConfig
  if(-not $cfg.enabled -and -not $DryRun){ return }
  $selected = @($cfg.selected)
  if($selected.Count -eq 0){ Write-CcuLog 'checker: enabled but no projects selected' 'warn'; return }

  $state = Get-CcuState
  $pstat = @{}; if($state.projectStatus){ foreach($pp in $state.projectStatus.PSObject.Properties){ $pstat[$pp.Name] = $pp.Value } }
  $sawLimited = [bool]$state.sawLimited
  $nowU = [DateTimeOffset]::UtcNow

  # probe cadence: FIXED interval — no reset-time estimation anywhere (the old estimate was
  # display noise that once even gated probing). Limited -> every 4 min (rejected server-side,
  # free, fire promptly); usable -> every probeIntervalMinutes (GUI chip, default 15; one tiny
  # haiku call). The only reset TIME shown is the server-exact value a probe returns.
  $ivl = 15; try { if([int]$cfg.probeIntervalMinutes -ge 2){ $ivl = [int]$cfg.probeIntervalMinutes } } catch {}
  $minGapMin = if($sawLimited){ 4 } else { $ivl }

  if($DryRun){
    $names = ($selected | ForEach-Object { $_.name }) -join ', '
    Write-CcuLog ("DRY-RUN: 布防后策略 = 每 ${ivl}m 实探一次额度(限流后加密到 4m), 恢复瞬间自动续跑这 $($selected.Count) 个项目: $names") 'info'
    return
  }

  $lastProbe = [DateTimeOffset]::MinValue
  if($state.lastProbeUtc){ try { $lastProbe = [DateTimeOffset]::Parse($state.lastProbeUtc, [Globalization.CultureInfo]::InvariantCulture, [Globalization.DateTimeStyles]::RoundtripKind) } catch {} }
  $sinceProbe = ($nowU - $lastProbe).TotalMinutes
  if($sinceProbe -lt $minGapMin){
    $state.phase='waiting'; $state.projectStatus=$pstat; Set-CcuState $state
    if(-not $sawLimited){
      $nextIn = [int][Math]::Max(0, [Math]::Ceiling($minGapMin - $sinceProbe))
      Write-CcuLog ("等待中(额度可用) · 下次实探 ~${nextIn}m (间隔 ${minGapMin}m)") 'info'
    }
    return
  }

  # ---- live probe = source of truth ----
  $state.lastProbeUtc = $nowU.ToString('o'); Set-CcuState $state
  $probe = Test-ClaudeReady -Model $cfg.probeModel
  # capture the EXACT reset the server just told us (persisted by the Set-CcuState in each branch below)
  $state = Save-RealResetFromProbe -Probe $probe -State $state
  $realStr = $null
  if($state.realFiveHourResetUtc){
    try { $realStr = Format-Countdown ((([DateTimeOffset]::FromUnixTimeSeconds([long]$state.realFiveHourResetUtc)) - [DateTimeOffset]::UtcNow).TotalSeconds) } catch {}
  }

  if($probe.reason -eq 'limited'){
    # fresh limited cycle: clear last cycle's per-project results, or continuous mode would
    # "skip (already done)" every project forever; also reset the refire-loop guard
    if(-not $sawLimited){ $pstat=@{}; $state.limitedRefires=0 }
    $state.sawLimited=$true; $state.phase='waiting'; $state.projectStatus=$pstat; Set-CcuState $state
    $wait = if($realStr){ "限流中,距真实重置 $realStr (服务器精确值)" } else { "限流中,等待额度恢复 (精确时间等下次实探读取)" }
    Write-CcuLog ("$wait · (限流探测不消耗额度)") 'info'
    # notify once per cycle, on the first limited observation (not every 4-min tick)
    if(-not $sawLimited){ [void](Send-FeishuNotify ("限流,等待重置(恢复后自动续跑 $($selected.Count) 个项目)")) }
    return
  }
  if(-not $probe.ready){
    $state.phase='waiting'; $state.projectStatus=$pstat; Set-CcuState $state
    Write-CcuLog ("探测未就绪 ($($probe.reason)) -> 下次重试(fail-closed,不误触发)") 'warn'
    return
  }

  # probe says usable but we never saw a limit yet: the user armed BEFORE hitting the cap.
  # Stay armed and keep watching (the old auto-disarm here silently cancelled exactly the
  # "arm right before the limit, go to bed" flow this tool exists for).
  if(-not $sawLimited){
    $state.phase='waiting'; $state.projectStatus=$pstat; Set-CcuState $state
    Write-CcuLog '额度当前可用(尚未限流) -> 保持布防继续监视;检测到限流后会在恢复瞬间自动续跑' 'info'
    return
  }

  # usable AND we had been limited -> the reset happened. FIRE.
  $state.phase='resuming'; $state.projectStatus=$pstat; Set-CcuState $state
  Write-CcuLog '额度已恢复 -> 开始逐个续跑' 'ok'
  [void](Send-FeishuNotify ('额度恢复,开始续跑 ' + $selected.Count + ' 个项目'))
  foreach($sel in $selected){
    if($pstat[$sel.path] -eq 'success'){ Write-CcuLog ('跳过(已完成): ' + $sel.name) 'info'; continue }
    if($cfg.skipPermissions){
      try {
        $g = Protect-GitRepo -Path $sel.path -Mode $cfg.dirtyGuard
        if($g.wasDirty){ Write-CcuLog ('git-guard: ' + $sel.name + ' 有未提交改动 -> ' + $g.action + ' (' + $g.ref + ')') 'warn' }
      } catch { Write-CcuLog ('git-guard 出错 ' + $sel.name + ': ' + $_.Exception.Message) 'warn' }
    }
    Write-CcuLog ('续跑 -> ' + $sel.name + '  [' + $sel.path + ']') 'launch'
    $r = Invoke-ClaudeResume -Project $sel -Prompt $cfg.resumePrompt `
           -SkipPermissions:([bool]$cfg.skipPermissions) -Model $cfg.resumeModel `
           -TimeoutMin ([int]$cfg.perProjectTimeoutMinutes)
    $pstat[$sel.path] = $r.status; $state.projectStatus=$pstat; Set-CcuState $state
    Write-CcuLog ($sel.name + ' -> ' + $r.status + ' (exit ' + $r.exitCode + ')') $(if($r.status -eq 'success'){'ok'}else{'error'})
    [void](Send-FeishuNotify ($(if($r.status -eq 'success'){'✅ '}else{'❌ '}) + $sel.name + $(if($r.status -eq 'success'){''}else{' ('+$r.status+')'})))
    if($r.status -eq 'limited'){
      # refire-loop guard: a genuine account-wide limit waits ~5h per refire, so a fast-growing
      # count means the "limited" is a misclassification (limit-looking text in a failing run)
      # -> mark the project error and move on instead of burning quota every ~6 min forever
      $state.limitedRefires = [int]$state.limitedRefires + 1
      if($state.limitedRefires -ge 6){
        Write-CcuLog ('已连续 ' + $state.limitedRefires + ' 次续跑被判为限流 -> ' + $sel.name + ' 标记为 error,继续其余项目(防误判死循环)') 'warn'
        [void](Send-FeishuNotify ('⚠️ ' + $sel.name + ' 多次判限流,跳过'))
        $pstat[$sel.path]='error'; $state.projectStatus=$pstat; Set-CcuState $state
        continue
      }
      Write-CcuLog '续跑中又被限流 -> 停,回到等待' 'warn'
      [void](Send-FeishuNotify ($sel.name + ' 续跑中又限流,等待重置后重试'))
      $state.sawLimited=$true; $state.phase='waiting'; Set-CcuState $state; return
    }
  }
  $state.phase='done'; $state.sawLimited=$false; $state.limitedRefires=0; $state.projectStatus=$pstat; Set-CcuState $state
  if(-not $cfg.continuous){ $cfg.enabled=$false; Set-CcuConfig $cfg; Write-CcuLog 'checker: 一次性完成 -> 已解除' 'ok'; [void](Send-FeishuNotify '🎉 全部完成,已解除布防') }
  else { Write-CcuLog 'checker: 续跑完成(连续模式)' 'ok'; [void](Send-FeishuNotify '本轮完成(连续模式)') }
}
finally {
  try { $lock.Close(); $lock.Dispose() } catch {}
  try { [System.IO.File]::Delete($lockPath) } catch {}
}
