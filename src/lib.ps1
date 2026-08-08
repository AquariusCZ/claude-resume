<#
  lib.ps1 - shared engine for "AI Resume"
  Correctness rules (from adversarial review + live testing):
   * NO reset-time estimation: the engine probes on a fixed interval and the only reset time
     ever shown is the server-exact resetsAt a probe returns. (ccusage/jsonl estimates were
     removed - they were display noise and once even mis-gated probing.)
   * Launch: claude.cmd via cmd.exe /c (UseShellExecute=false cannot exec a .cmd); tail the
     redirect file for live output; kill the WHOLE process tree on stop or an explicit timeout.
   * ExitCode: PS 5.1's Start-Process -PassThru returns $null from .ExitCode after the process
     exits unless $p.Handle was read first (WaitForExit(ms) opens SYNCHRONIZE-only; HasExited
     polling has the same hole - both verified live). Cache the handle AND never rely on the
     exit code alone: a stream-json "type":"result","is_error":false line is the success signal.
   * Fail-closed: bad claude reads assume "still limited", never "clear".
   * This file must be saved UTF-8 WITH BOM so Windows PowerShell 5.1 parses non-ASCII correctly.
#>
Set-StrictMode -Off
$ErrorActionPreference = 'Stop'
$env:AI_RESUME_INTERNAL_RUN = '1'

$script:AppDir     = if($env:CLAUDE_RESUME_APP_DIR){ [IO.Path]::GetFullPath($env:CLAUDE_RESUME_APP_DIR) } else { Join-Path $env:LOCALAPPDATA 'ClaudeResume' }
$script:LogDir     = Join-Path $script:AppDir 'logs'
$script:ConfigPath = Join-Path $script:AppDir 'config.json'
$script:StatePath  = Join-Path $script:AppDir 'state.json'
$script:BackgroundChildPath = Join-Path $script:AppDir 'checker-ai-child.json'
$script:ClaudeProjectsRoot = if($env:CLAUDE_RESUME_CLAUDE_PROJECTS_DIR){ [IO.Path]::GetFullPath($env:CLAUDE_RESUME_CLAUDE_PROJECTS_DIR) } else { Join-Path $env:USERPROFILE '.claude\projects' }
# dev runs from src/ may predate install: the probe uses AppDir as -WorkingDirectory, which throws if missing
try { if(-not (Test-Path $script:AppDir)){ New-Item -ItemType Directory -Force -Path $script:AppDir | Out-Null } } catch {}

function Get-ClaudeCmd {
  $c = Get-Command claude.cmd -ErrorAction SilentlyContinue
  if($c){ return $c.Source }
  $p = Join-Path $env:APPDATA 'npm\claude.cmd'; if(Test-Path $p){ return $p }
  return $null
}

function Write-CcuLog {
  param([string]$Message, [string]$Level = 'info', $UiSink = $null)
  $ts = (Get-Date).ToString('yyyy-MM-dd HH:mm:ss')
  $line = "[$ts] [$Level] $Message"
  try {
    if(-not (Test-Path $script:LogDir)){ New-Item -ItemType Directory -Force -Path $script:LogDir | Out-Null }
    $file = Join-Path $script:LogDir ("run-" + (Get-Date).ToString('yyyyMMdd') + ".log")
    [System.IO.File]::AppendAllText($file, $line + "`r`n", (New-Object System.Text.UTF8Encoding($false)))
  } catch {}
  if($UiSink){ try { & $UiSink $Message $Level } catch {} }
}

function Get-CcuConfig {
  # merge loaded config over defaults so EVERY field always exists (settable without throwing)
  $def = [ordered]@{
    enabled=$false; armed=$false; armCycleId=''; continuous=$false; selected=@(); customProjects=@(); hiddenProjects=@();
    resumePrompt='continue'; skipPermissions=$true; dirtyGuard='stash'; perProjectTimeoutMinutes=0;
    safetyMarginSeconds=60; weeklyBackoffMinutes=45; probeModel='haiku'; resumeModel=''; projectHome='';
    feishuWebhook=''; feishuSecret=''; probeIntervalMinutes=15;
    feishuAppId=''; feishuAppSecret=''; feishuChatId=''; feishuDefaultProject=''; feishuAllowOpenIds=@();
    feishuChatProfile='openai-sol'; feishuUserProfiles=@{}; feishuChatModel=''; feishuUserModels=@{};
    feishuQueryTimeoutMinutes=30; feishuChatTimeoutMinutes=30; completionNotifyEnabled=$true;
    aiFallbackProfiles=@('deepseek-v4','openai-sol'); aiProxy=''; aiNoProxy='127.0.0.1,localhost,::1';
    openaiBaseUrl='https://api.openai.com/v1'; openaiApiKey=''; openaiReasoning='xhigh';
    deepseekApiKey=''; deepseekMillionContext=$true; deepseekEffort='';
    sessionAutoCleanup=$true; feishuSessionArchiveDays=14; feishuSessionDeleteDays=30; sessionCleanupIntervalHours=6;
    feishuAuthOpenIds=@(); feishuViewerOpenIds=@(); feishuAuthPassword=''
  }
  if(Test-Path $script:ConfigPath){
    for($attempt=0; $attempt -lt 3; $attempt++){
      try {
        $loaded = Get-Content $script:ConfigPath -Raw -Encoding UTF8 | ConvertFrom-Json
        foreach($p in $loaded.PSObject.Properties){ $def[$p.Name] = $p.Value }
        break
      } catch { if($attempt -lt 2){ Start-Sleep -Milliseconds 20 } }
    }
  }
  # Background auto-resume is project modification too: it has no total duration limit.
  # Keep the legacy field at zero so old config files cannot silently restore the 30-minute cutoff.
  $def.perProjectTimeoutMinutes = 0
  return [pscustomobject]$def
}

function Write-CcuJsonAtomic {
  param([string]$Path, $Value)
  $dir = Split-Path $Path -Parent
  if(-not (Test-Path $dir)){ New-Item -ItemType Directory -Force -Path $dir | Out-Null }
  $tmp = $Path + '.tmp-' + $PID + '-' + [guid]::NewGuid().ToString('N')
  $bytes = [Text.Encoding]::UTF8.GetBytes(($Value | ConvertTo-Json -Depth 8))
  $fs = New-Object IO.FileStream($tmp, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
  try { $fs.Write($bytes, 0, $bytes.Length); $fs.Flush($true) } finally { $fs.Close(); $fs.Dispose() }
  try {
    if(Test-Path $Path){
      $backup = $Path + '.replace-bak-' + [guid]::NewGuid().ToString('N')
      try { [IO.File]::Replace($tmp, $Path, $backup, $true) }
      finally { try { if(Test-Path $backup){ [IO.File]::Delete($backup) } } catch {} }
    }
    else { [IO.File]::Move($tmp, $Path) }
  } catch {
    try { if(Test-Path $tmp){ [IO.File]::Delete($tmp) } } catch {}
    throw
  }
}

function Invoke-CcuWriteLock {
  param([string]$Path, [scriptblock]$Action)
  $lock = $null
  for($attempt=0; $attempt -lt 100 -and -not $lock; $attempt++){
    try { $lock = [IO.File]::Open($Path, 'OpenOrCreate', 'ReadWrite', 'None') }
    catch { Start-Sleep -Milliseconds 20 }
  }
  if(-not $lock){ throw ('无法取得写锁: ' + $Path) }
  try { return (& $Action) } finally { $lock.Close(); $lock.Dispose() }
}

function Invoke-CcuPortableWriteLock {
  param([string]$Path, [scriptblock]$Action)
  $lock = $null
  for($attempt=0; $attempt -lt 250 -and -not $lock; $attempt++){
    try {
      $lock = New-Object IO.FileStream($Path, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::Read)
      $owner = [Text.Encoding]::UTF8.GetBytes(([ordered]@{ pid=$PID; createdUtc=[DateTimeOffset]::UtcNow.ToString('o') } | ConvertTo-Json -Compress))
      $lock.Write($owner,0,$owner.Length); $lock.Flush($true)
    } catch {
      $lock = $null
      try {
        if(Test-Path $Path){
          $item=Get-Item $Path -ErrorAction Stop
          if(([DateTime]::UtcNow-$item.LastWriteTimeUtc).TotalSeconds -gt 30){
            $stale=[IO.File]::Open($Path,'Open','ReadWrite','None'); $stale.Close(); $stale.Dispose(); [IO.File]::Delete($Path)
          }
        }
      } catch {}
      if(-not $lock){ Start-Sleep -Milliseconds 20 }
    }
  }
  if(-not $lock){ throw ('无法取得跨进程写锁: ' + $Path) }
  try { return (& $Action) }
  finally {
    $lock.Close(); $lock.Dispose()
    try { [IO.File]::Delete($Path) } catch {}
  }
}

function Set-CcuConfig { param($Config)
  # Atomic UTF-8 WITHOUT BOM: readers never observe a truncated JSON document.
  return Invoke-CcuPortableWriteLock ($script:ConfigPath + '.write.lock') { Write-CcuJsonAtomic $script:ConfigPath $Config; return $true }
}
function Update-CcuConfig { param([scriptblock]$Action)
  $mutator=$Action
  return Invoke-CcuPortableWriteLock ($script:ConfigPath + '.write.lock') {
    $live=Get-CcuConfig
    [void](& $mutator $live)
    Write-CcuJsonAtomic $script:ConfigPath $live
    return $live
  }
}
function Get-CcuState {
  # merge loaded state over defaults so EVERY field always exists (new fields settable without throwing)
  $def = [ordered]@{
    targetId=$null; targetEndUtc=$null; firedForId=$null; projectStatus=$null; phase='idle'; cycleId='';
    sawLimited=$false; lastProbeUtc=$null; limitedRefires=0;
    realFiveHourResetUtc=$null; realSevenDayResetUtc=$null; realResetProbedUtc=$null; realFiveHourUtil=$null
  }
  if(Test-Path $script:StatePath){
    for($attempt=0; $attempt -lt 3; $attempt++){
      try {
        $loaded = Get-Content $script:StatePath -Raw -Encoding UTF8 | ConvertFrom-Json
        foreach($p in $loaded.PSObject.Properties){ $def[$p.Name] = $p.Value }
        break
      } catch { if($attempt -lt 2){ Start-Sleep -Milliseconds 20 } }
    }
  }
  return [pscustomobject]$def
}
function Set-CcuState { param($State, [switch]$Force)
  return Invoke-CcuWriteLock ($script:StatePath + '.write.lock') {
    $incomingCycle = ''; try { $incomingCycle = "$($State.cycleId)" } catch {}
    if(-not $Force -and $incomingCycle -and (Test-Path $script:StatePath)){
      $current = Get-CcuState; $currentCycle = "$($current.cycleId)"
      if($currentCycle -and $currentCycle -ne $incomingCycle){ return $false }
    }
    Write-CcuJsonAtomic $script:StatePath $State
    return $true
  }
}

function New-CcuCycleId { return [guid]::NewGuid().ToString('N') }
function Get-OrCreate-CcuActiveCycle {
  return Invoke-CcuPortableWriteLock ($script:ConfigPath + '.write.lock') {
    $live = Get-CcuConfig
    if(-not [bool]$live.enabled){ return '' }
    $cycle = "$($live.armCycleId)"
    if(-not $cycle){
      $cycle=New-CcuCycleId; $live.armCycleId=$cycle
      Write-CcuJsonAtomic $script:ConfigPath $live
    }
    return $cycle
  }
}
function Test-CcuCycleActive { param([string]$CycleId)
  if(-not $CycleId){ return $false }
  for($attempt=0; $attempt -lt 3; $attempt++){
    try {
      $live = Get-Content $script:ConfigPath -Raw -Encoding UTF8 | ConvertFrom-Json
      return ([bool]$live.enabled -and "$($live.armCycleId)" -eq $CycleId)
    } catch { if($attempt -lt 2){ Start-Sleep -Milliseconds 20 } }
  }
  return $false
}

function Complete-CcuCycle { param([string]$CycleId)
  return Invoke-CcuPortableWriteLock ($script:ConfigPath + '.write.lock') {
    $live = Get-CcuConfig
    if(-not [bool]$live.enabled -or "$($live.armCycleId)" -ne $CycleId){ return 'superseded' }
    if([bool]$live.continuous){ return 'continuous' }
    $live.enabled=$false; $live.armed=$false; $live.armCycleId=New-CcuCycleId
    Write-CcuJsonAtomic $script:ConfigPath $live
    return 'disarmed'
  }
}

function Initialize-CcuCycleState { param($State, [string]$CycleId)
  if(-not $CycleId){ return $false }
  return Invoke-CcuPortableWriteLock ($script:ConfigPath + '.write.lock') {
    $live=Get-CcuConfig
    if(-not [bool]$live.enabled -or "$($live.armCycleId)" -ne $CycleId){ return $false }
    $State.cycleId=$CycleId; $State.phase='waiting'; $State.projectStatus=@{}; $State.sawLimited=$false; $State.limitedRefires=0
    return [bool](Set-CcuState $State -Force)
  }
}

function Clear-OldCaches {
  # Safe housekeeping (never touches real project conversations). Called each checker tick.
  try {
    # 1) throwaway probe sessions: every Test-ClaudeReady runs `claude -p "ready"` in AppDir, which
    #    leaves a session in ~/.claude/projects/<AppDir-encoded>/ (observed 900+). Delete old ones.
    #    Match the probe folder exactly (ends with 'ClaudeResume') — NOT '...-feishu-chat'.
    $root = $script:ClaudeProjectsRoot
    if(Test-Path $root){
      foreach($d in (Get-ChildItem $root -Directory -ErrorAction SilentlyContinue | Where-Object { $_.Name -like '*AppData-Local-ClaudeResume' })){
        foreach($f in @(Get-ChildItem $d.FullName -Filter *.jsonl -ErrorAction SilentlyContinue | Where-Object { $_.LastWriteTime -lt (Get-Date).AddMinutes(-20) })){
          $sid=[IO.Path]::GetFileNameWithoutExtension($f.Name)
          Remove-Item $f.FullName -Force -ErrorAction SilentlyContinue
          Remove-Item (Join-Path $d.FullName $sid) -Recurse -Force -ErrorAction SilentlyContinue
        }
      }
    }
    # 2) cap the Feishu agent stdout log (append-mode handle -> truncate is gap-safe; try/catch on lock)
    $so = Join-Path $script:LogDir 'feishu-stdout.log'
    if((Test-Path $so) -and ((Get-Item $so).Length -gt 2MB)){ try { [System.IO.File]::WriteAllText($so, '') } catch {} }
    # 3) prune daily logs older than 30 days
    if(Test-Path $script:LogDir){
      Get-ChildItem $script:LogDir -Filter '*.log' -ErrorAction SilentlyContinue |
        Where-Object { $_.Name -match '^(run|feishu)-\d{8}\.log$' -and $_.LastWriteTime -lt (Get-Date).AddDays(-30) } |
        Remove-Item -Force -ErrorAction SilentlyContinue
    }
  } catch {}
}

function Get-ClaudeProjects {
  $root = $script:ClaudeProjectsRoot
  $list = @()
  if(-not (Test-Path $root)){ return $list }
  foreach($dir in (Get-ChildItem $root -Directory -ErrorAction SilentlyContinue)){
    $jsonl = Get-ChildItem $dir.FullName -Filter *.jsonl -ErrorAction SilentlyContinue |
             Sort-Object LastWriteTime -Descending | Select-Object -First 1
    if(-not $jsonl){ continue }
    $cwd = $null; $sid = [IO.Path]::GetFileNameWithoutExtension($jsonl.Name)
    try {
      # UTF-8: without it, PS 5.1 mis-decodes non-ASCII cwd paths (Chinese folder names) and drops them
      foreach($ln in (Get-Content $jsonl.FullName -TotalCount 60 -Encoding UTF8 -ErrorAction SilentlyContinue)){
        if($ln -match '"cwd"'){
          try { $j = $ln | ConvertFrom-Json; if($j.cwd){ $cwd=$j.cwd; if($j.sessionId){ $sid=$j.sessionId }; break } } catch {}
        }
      }
    } catch {}
    if(-not $cwd){ continue }
    if(-not (Test-Path $cwd)){ continue }
    if($cwd -like "$env:WINDIR*"){ continue }
    if($cwd -like "$script:AppDir*"){ continue }   # the tool's own dirs (probe / feishu-chat) are never projects
    $list += [pscustomobject]@{
      name = Split-Path $cwd -Leaf; path = $cwd; sessionId = $sid;
      lastUsedUtc = $jsonl.LastWriteTimeUtc; folder = $dir.Name;
      isGit = (Test-Path (Join-Path $cwd '.git'))
    }
  }
  $list = $list | Group-Object path | ForEach-Object { $_.Group | Sort-Object lastUsedUtc -Descending | Select-Object -First 1 }
  return @($list | Sort-Object lastUsedUtc -Descending)
}

function Get-CcuProcessProbe { param([int]$ProcessId)
  try {
    $process=Get-CimInstance Win32_Process -Filter "ProcessId=$ProcessId" -ErrorAction Stop
    if($null -eq $process){ return [pscustomobject]@{ status='gone'; process=$null } }
    return [pscustomobject]@{ status='found'; process=$process }
  } catch { return [pscustomobject]@{ status='failed'; process=$null } }
}

function Get-CcuProcess { param([int]$ProcessId)
  $probe=Get-CcuProcessProbe $ProcessId
  if($probe.status -eq 'found'){ return $probe.process }
  return $null
}

function Get-CcuChildProcessProbe { param([int]$ParentProcessId)
  try { return [pscustomobject]@{ status='ok'; processes=@(Get-CimInstance Win32_Process -Filter "ParentProcessId=$ParentProcessId" -ErrorAction Stop) } }
  catch { return [pscustomobject]@{ status='failed'; processes=@() } }
}

function Get-CcuProcessStartUtc { param($Process)
  try {
    if($Process.CreationDate -is [datetime]){ return $Process.CreationDate.ToUniversalTime() }
    return [Management.ManagementDateTimeConverter]::ToDateTime("$($Process.CreationDate)").ToUniversalTime()
  } catch { return $null }
}

function Stop-ProcessTree { param([int]$ProcessId)
  $initial=Get-CcuProcessProbe $ProcessId
  if($initial.status -eq 'gone'){ return $true }
  if($initial.status -ne 'found'){ return $false }
  try {
    $psi = New-Object Diagnostics.ProcessStartInfo
    $psi.FileName = (Join-Path $env:WINDIR 'System32\taskkill.exe')
    $psi.Arguments = '/PID ' + $ProcessId + ' /T /F'
    $psi.UseShellExecute = $false; $psi.CreateNoWindow = $true
    $killer = [Diagnostics.Process]::Start($psi)
    if(-not $killer.WaitForExit(10000)){ try { $killer.Kill() } catch {}; return $false }
    if($killer.ExitCode -ne 0){
      $afterKill=Get-CcuProcessProbe $ProcessId
      if($afterKill.status -eq 'gone'){ return $true }
      return $false
    }
  } catch { return $false }
  for($i=0; $i -lt 30; $i++){
    $probe=Get-CcuProcessProbe $ProcessId
    if($probe.status -eq 'gone'){ return $true }
    Start-Sleep -Milliseconds 100
  }
  return $false
}

function Get-CcuBackgroundRegistryCandidates {
  $items=New-Object System.Collections.Generic.List[string]
  $items.Add($script:BackgroundChildPath)
  $dir=Split-Path $script:BackgroundChildPath -Parent; $base=Split-Path $script:BackgroundChildPath -Leaf
  try { Get-ChildItem -LiteralPath $dir -File -ErrorAction Stop | Where-Object { $_.Name -like ($base+'.tmp-*') -or $_.Name -like ($base+'.replace-bak-*') } | ForEach-Object { $items.Add($_.FullName) } } catch {}
  return @($items)
}

function Read-CcuBackgroundRegistry {
  $best=$null; $valid=0; $sawFile=$false
  foreach($path in (Get-CcuBackgroundRegistryCandidates)){
    if(-not (Test-Path $path)){ continue }
    $sawFile=$true
    try {
      $entry=Get-Content $path -Raw -Encoding UTF8 | ConvertFrom-Json; $valid++
      $score=(Get-Item $path).LastWriteTimeUtc.Ticks
      foreach($field in 'updatedUtc','registeredUtc','launchRequestedUtc'){
        try { $candidate=[DateTimeOffset]::Parse("$($entry.$field)").UtcDateTime.Ticks; if($candidate -gt $score){$score=$candidate} } catch {}
      }
      if(-not $best -or $score -gt $best.score){ $best=[pscustomobject]@{ entry=$entry; path=$path; score=$score } }
    } catch {}
  }
  return [pscustomobject]@{ entry=$(if($best){$best.entry}else{$null}); path=$(if($best){$best.path}else{$null}); valid=$valid; sawFile=$sawFile }
}

function Remove-CcuBackgroundRegistryFiles {
  foreach($path in (Get-CcuBackgroundRegistryCandidates)){ try { if(Test-Path $path){ [IO.File]::Delete($path) } } catch {} }
}

function Register-CcuBackgroundLaunch {
  param([pscustomobject]$Project, [string]$RunKey)
  try {
    $entry=[ordered]@{ version=2; status='launching'; pid=0; parentPid=$PID; runKey=$RunKey; projectPath="$($Project.path)"; projectName="$($Project.name)"; startedUtc=''; launchRequestedUtc=[DateTimeOffset]::UtcNow.ToString('o'); updatedUtc=[DateTimeOffset]::UtcNow.ToString('o') }
    Invoke-CcuWriteLock ($script:BackgroundChildPath + '.write.lock') {
      $existing=Read-CcuBackgroundRegistry
      if($existing.sawFile){ throw '已有后台子进程或启动意图登记' }
      Write-CcuJsonAtomic $script:BackgroundChildPath $entry
    } | Out-Null
    return $true
  } catch { Write-CcuLog ('后台启动意图登记失败: ' + $_.Exception.Message) 'error'; return $false }
}

function Register-CcuBackgroundChild {
  param($Process, [pscustomobject]$Project, [string]$RunKey)
  try {
    $probe=Get-CcuProcessProbe ([int]$Process.Id)
    if($probe.status -ne 'found'){ throw ('无法核验新后台进程:' + $probe.status) }
    $actual=$probe.process; $startedAt=Get-CcuProcessStartUtc $actual
    if(-not $startedAt){ throw '无法取得新后台进程启动时间' }
    if([int]$actual.ParentProcessId -ne $PID){ throw '新后台进程父 PID 不匹配' }
    $started=$startedAt.ToString('o')
    $entry = [ordered]@{
      version=2; status='active'; pid=[int]$Process.Id; parentPid=$PID; runKey=$RunKey;
      projectPath="$($Project.path)"; projectName="$($Project.name)";
      startedUtc=$started; registeredUtc=[DateTimeOffset]::UtcNow.ToString('o'); updatedUtc=[DateTimeOffset]::UtcNow.ToString('o')
    }
    Invoke-CcuWriteLock ($script:BackgroundChildPath + '.write.lock') {
      $existing=Read-CcuBackgroundRegistry
      if(-not $existing.entry -or "$($existing.entry.runKey)" -ne $RunKey -or [int]$existing.entry.parentPid -ne $PID){ throw '后台启动意图已丢失或被取代' }
      Write-CcuJsonAtomic $script:BackgroundChildPath $entry
    } | Out-Null
    return $true
  } catch {
    try {
      Invoke-CcuWriteLock ($script:BackgroundChildPath + '.write.lock') {
        $existing=Read-CcuBackgroundRegistry
        if($existing.entry -and "$($existing.entry.runKey)" -eq $RunKey){
          $existing.entry.status='unverified'; $existing.entry.pid=[int]$Process.Id; $existing.entry.updatedUtc=[DateTimeOffset]::UtcNow.ToString('o')
          Write-CcuJsonAtomic $script:BackgroundChildPath $existing.entry
        }
      } | Out-Null
    } catch {}
    Write-CcuLog ('后台子进程登记失败: ' + $_.Exception.Message) 'error'; return $false
  }
}

function Clear-CcuBackgroundChild { param([int]$ProcessId=-1, [string]$RunKey='')
  try {
    return Invoke-CcuWriteLock ($script:BackgroundChildPath + '.write.lock') {
      $record=Read-CcuBackgroundRegistry
      if(-not $record.sawFile){ return $true }
      if(-not $record.entry){ return $false }
      if($ProcessId -ge 0 -and [int]$record.entry.pid -ne $ProcessId){ return $false }
      if($RunKey -and "$($record.entry.runKey)" -ne $RunKey){ return $false }
      Remove-CcuBackgroundRegistryFiles
      return $true
    }
  } catch { return $false }
}

function Recover-CcuBackgroundChild {
  $record=Read-CcuBackgroundRegistry
  if(-not $record.sawFile){ return $true }
  if(-not $record.entry){ Write-CcuLog '后台子进程登记损坏 -> fail-closed,拒绝启动新续跑' 'error'; return $false }
  $entry=$record.entry
  $pidValue = 0; try { $pidValue=[int]$entry.pid } catch {}
  if($pidValue -le 0 -and "$($entry.status)" -eq 'launching'){
    $parent=0; try{$parent=[int]$entry.parentPid}catch{}
    $requested=$null; try{$requested=[DateTimeOffset]::Parse("$($entry.launchRequestedUtc)").UtcDateTime}catch{}
    if($parent -le 0 -or -not $requested){ Write-CcuLog '后台启动意图信息不完整 -> fail-closed' 'error'; return $false }
    $children=Get-CcuChildProcessProbe $parent
    if($children.status -ne 'ok'){ Write-CcuLog '无法核验后台启动意图 -> fail-closed' 'error'; return $false }
    $matches=@($children.processes | Where-Object {
      $cmd="$($_.CommandLine)"; $start=Get-CcuProcessStartUtc $_
      $cmd -match '(?i)claude(\.cmd|\.exe)?' -and $cmd -match '--continue' -and $start -and [Math]::Abs(($start-$requested).TotalSeconds) -le 10
    })
    if($matches.Count -eq 0){ [void](Clear-CcuBackgroundChild 0 "$($entry.runKey)"); return $true }
    if($matches.Count -ne 1){ Write-CcuLog '后台启动意图匹配到多个进程 -> fail-closed' 'error'; return $false }
    $pidValue=[int]$matches[0].ProcessId; $entry.startedUtc=(Get-CcuProcessStartUtc $matches[0]).ToString('o')
  }
  if($pidValue -le 0){ Write-CcuLog '后台子进程登记缺少 PID -> fail-closed' 'error'; return $false }
  $processProbe=Get-CcuProcessProbe $pidValue
  if($processProbe.status -eq 'gone'){ [void](Clear-CcuBackgroundChild $pidValue "$($entry.runKey)"); return $true }
  if($processProbe.status -ne 'found'){ Write-CcuLog ('无法核验后台登记进程 -> fail-closed pid=' + $pidValue) 'error'; return $false }
  $proc=$processProbe.process
  $registeredStart = $null; try { $registeredStart=[DateTimeOffset]::Parse("$($entry.startedUtc)").UtcDateTime } catch {}
  $actualStart = Get-CcuProcessStartUtc $proc
  if(-not $registeredStart -or -not $actualStart){ Write-CcuLog ('后台登记缺少可核验启动时间 -> fail-closed pid=' + $pidValue) 'error'; return $false }
  if([Math]::Abs(($actualStart-$registeredStart).TotalSeconds) -gt 5){
    Write-CcuLog ('后台登记 PID 已被其他进程复用,清理陈旧登记 pid=' + $pidValue) 'warn'
    [void](Clear-CcuBackgroundChild $pidValue "$($entry.runKey)"); return $true
  }
  if([int]$proc.ParentProcessId -ne [int]$entry.parentPid){ Write-CcuLog ('后台登记父 PID 不匹配,清理陈旧登记 pid=' + $pidValue) 'warn'; [void](Clear-CcuBackgroundChild $pidValue "$($entry.runKey)"); return $true }
  $cmd = "$($proc.CommandLine)"
  if($cmd -and ($cmd -notmatch '(?i)claude(\.cmd|\.exe)?' -or $cmd -notmatch '--continue')){
    Write-CcuLog ('后台登记 PID 命令签名不匹配,清理陈旧登记 pid=' + $pidValue) 'warn'
    [void](Clear-CcuBackgroundChild $pidValue "$($entry.runKey)"); return $true
  }
  if(-not $cmd){ Write-CcuLog ('无法核验后台孤儿命令行 -> fail-closed pid=' + $pidValue) 'error'; return $false }
  Write-CcuLog ('发现上次遗留后台 AI 进程,先回收 pid=' + $pidValue) 'warn'
  if(-not (Stop-ProcessTree $pidValue)){ Write-CcuLog ('后台孤儿回收失败 -> fail-closed pid=' + $pidValue) 'error'; return $false }
  [void](Clear-CcuBackgroundChild $pidValue "$($entry.runKey)")
  Write-CcuLog ('后台孤儿已回收 pid=' + $pidValue) 'ok'
  return $true
}

function Protect-GitRepo {
  param([string]$Path, [string]$Mode='stash')
  $res = @{ isRepo=$false; wasDirty=$false; action='none'; ref=$null }
  if(-not (Test-Path (Join-Path $Path '.git'))){ return $res }
  $res.isRepo = $true
  $env:GIT_TERMINAL_PROMPT='0'
  $dirty = (& git -C $Path status --porcelain 2>$null)
  if(-not $dirty){ return $res }
  $res.wasDirty = $true
  $stamp = (Get-Date).ToString('yyyyMMdd-HHmmss')
  if($Mode -eq 'branch'){
    & git -C $Path stash push -u -m "claude-resume-guard $stamp" 2>&1 | Out-Null
    & git -C $Path checkout -b "claude-resume/$stamp" 2>&1 | Out-Null
    & git -C $Path stash pop 2>&1 | Out-Null
    $res.action='branch'; $res.ref="claude-resume/$stamp"
  } else {
    & git -C $Path stash push -u -m "claude-resume-guard $stamp" 2>&1 | Out-Null
    $res.action='stash'; $res.ref="claude-resume-guard $stamp"
  }
  return $res
}

function Get-ClaudeProbeFailureReason {
  param([string]$Text='')
  $low = "$Text".ToLowerInvariant()
  if($low -match 'usage limit|rate.?limit|limit reached|5-hour limit|weekly limit|too many requests|resets at|quota exceeded|429'){ return 'limited' }
  if($low -match 'not logged in|please run /login|login required|unauthori[sz]ed|authentication|invalid api key|invalid.*auth|api key.*missing|\b401\b|\b403\b'){ return 'auth' }
  if($low -match 'subscription.*(expired|required|inactive)|billing|payment required|insufficient (credit|balance)|credit balance|plan expired'){ return 'billing' }
  if($low -match 'model.*(not found|unavailable|unsupported)|unknown model|模型.*不可用'){ return 'model_unavailable' }
  if($low -match 'timed? ?out|timeout|econn|socket|tls|dns|network|connection (reset|refused|failed)|\b502\b|\b503\b|\b504\b|server overloaded|temporar'){ return 'transient' }
  if($low -match 'enoent|not recognized|command not found|系统找不到指定的文件|启动.*失败'){ return 'no-claude' }
  return 'unknown'
}

function Test-ClaudeReady {
  # A live probe = source of truth. Runs claude -p as stream-json so we can read the EXACT
  # reset the server sends in `rate_limit_event` messages (same numbers the /usage screen shows):
  #   {"type":"rate_limit_event","rate_limit_info":{"status":"blocked","resetsAt":<unix>,
  #    "rateLimitType":"five_hour|seven_day","utilization":0..1,...}}
  # resetsAt is only sent once a window crosses ~0.75 utilization (and always when blocked),
  # so fiveHourResetUtc is $null when you're nowhere near the 5h cap -- callers show low/unknown,
  # never a locally estimated reset time.
  param([string]$Model='haiku', [int]$TimeoutSec=90)
  $claude = Get-ClaudeCmd
  $r = @{ ready=$false; reason='unknown'; output='';
          fiveHourResetUtc=$null; sevenDayResetUtc=$null; fiveHourUtil=$null; sevenDayUtil=$null }
  if(-not $claude){ $r.reason='no-claude'; return $r }
  $tmpOut = [IO.Path]::GetTempFileName(); $tmpErr = [IO.Path]::GetTempFileName()
  try {
    $a = @('/c','"'+$claude+'"','-p','ready','--model',$Model,'--max-turns','1','--output-format','stream-json','--verbose')
    # -WorkingDirectory AppDir: probe sessions land in one known .claude/projects folder,
    # keeping them out of the discovered project list.
    $p = Start-Process -FilePath $env:ComSpec -ArgumentList $a -NoNewWindow -PassThru `
          -WorkingDirectory $script:AppDir -RedirectStandardOutput $tmpOut -RedirectStandardError $tmpErr
    $null = $p.Handle   # cache NOW or .ExitCode reads $null after exit (PS 5.1, verified)
    if(-not $p.WaitForExit($TimeoutSec*1000)){ Stop-ProcessTree -ProcessId $p.Id; $r.reason='timeout'; return $r }
    $so=''; $se=''
    try { $so = [IO.File]::ReadAllText($tmpOut, [Text.Encoding]::UTF8) } catch {}
    try { $se = [IO.File]::ReadAllText($tmpErr, [Text.Encoding]::UTF8) } catch {}
    $r.output = ($so + "`n" + $se); $blob = $r.output

    # ---- exact reset times, parsed from every rate_limit_info block (flat JSON, no nested braces) ----
    foreach($m in [regex]::Matches($blob, '"rate_limit_info"\s*:\s*\{[^}]*\}')){
      $seg  = $m.Value
      $type = ([regex]::Match($seg, '"rateLimitType"\s*:\s*"([^"]+)"')).Groups[1].Value
      $ra   = ([regex]::Match($seg, '"resetsAt"\s*:\s*(\d+)')).Groups[1].Value
      $ut   = ([regex]::Match($seg, '"utilization"\s*:\s*([0-9.]+)')).Groups[1].Value
      if($ra){
        $dt = [DateTimeOffset]::FromUnixTimeSeconds([long]$ra)
        if($type -eq 'five_hour'){ $r.fiveHourResetUtc=$dt; if($ut){ $r.fiveHourUtil=[double]$ut } }
        elseif($type -eq 'seven_day'){ $r.sevenDayResetUtc=$dt; if($ut){ $r.sevenDayUtil=[double]$ut } }
      }
    }

    # ---- decide, most-authoritative first ----
    # 1) server says blocked -> limited. 2) a completed result line with is_error:false -> ready
    # (this is THE success signal; it must beat the fuzzy text match so an "approaching limit"
    # warning inside a successful run can't read as limited). 3) fuzzy limit text -> limited
    # (covers blocked runs that emitted no structured status). 4) exit code, last resort only:
    # it read $null for every successful probe pre-fix and silently blocked every resume.
    if([regex]::IsMatch($blob, '"status"\s*:\s*"(blocked|rejected|limited|exceeded)"')){ $r.reason='limited'; return $r }
    foreach($ln in ($blob -split "[`r`n]+")){
      if($ln -match '"type"\s*:\s*"result"' -and $ln -match '"is_error"\s*:\s*false'){ $r.ready=$true; $r.reason='ok'; return $r }
    }
    $classified = Get-ClaudeProbeFailureReason $blob
    if($classified -ne 'unknown'){ $r.reason=$classified; return $r }
    $exitCode = $null; try { $exitCode = $p.ExitCode } catch {}
    if($exitCode -eq 0){ $r.ready=$true; $r.reason='ok' }
    else { $r.reason = 'exit-' + $(if($null -eq $exitCode){ 'null' } else { $exitCode }) }
  } catch {
    $classified = Get-ClaudeProbeFailureReason $_.Exception.Message
    $r.reason = if($classified -ne 'unknown'){ $classified } else { "err:$($_.Exception.Message)" }
  }
  finally { try { [IO.File]::Delete($tmpOut) } catch {}; try { [IO.File]::Delete($tmpErr) } catch {} }
  return $r
}

function Get-FeishuTenantToken {
  # tenant_access_token for the self-built app, cached in a file until ~2 min before expiry.
  param([string]$AppId, [string]$AppSecret)
  try {
    if(-not $AppId -or -not $AppSecret){ return $null }
    $cachePath = Join-Path $script:AppDir 'feishu-token.json'
    $nowUnix = [DateTimeOffset]::UtcNow.ToUnixTimeSeconds()
    if(Test-Path $cachePath){
      try {
        $c = Get-Content $cachePath -Raw -Encoding UTF8 | ConvertFrom-Json
        if("$($c.appId)" -eq $AppId -and "$($c.token)" -and [long]$c.expiresAt -gt $nowUnix){ return "$($c.token)" }
      } catch {}
    }
    $r = Invoke-RestMethod -Uri 'https://open.feishu.cn/open-apis/auth/v3/tenant_access_token/internal' -Method Post -TimeoutSec 10 `
           -ContentType 'application/json; charset=utf-8' -Body ([System.Text.Encoding]::UTF8.GetBytes((@{ app_id=$AppId; app_secret=$AppSecret } | ConvertTo-Json -Compress)))
    if($r.code -eq 0 -and $r.tenant_access_token){
      try { (@{ appId=$AppId; token=$r.tenant_access_token; expiresAt=($nowUnix + [int]$r.expire - 120) } | ConvertTo-Json) | Set-Content -Path $cachePath -Encoding UTF8 } catch {}
      return "$($r.tenant_access_token)"
    }
    return $null
  } catch { return $null }
}

function Send-FeishuNotify {
  # Push one status line to Feishu. Prefers the single self-built app bot (app API -> the chat you
  # talk to it in, feishuChatId) so ONE bot does both notify + two-way; falls back to the custom-bot
  # webhook (optionally 签名校验-signed) if the app isn't fully set up. Never throws.
  param([string]$Text)
  try {
    # stamp every push with the local time — a resume can land hours later (after a 5h reset) and
    # Feishu only shows its own timestamp on some messages, so "when did this finish" must be in-line.
    $Text = "$Text · $((Get-Date).ToString('HH:mm'))"
    $cfg = Get-CcuConfig
    # 1) self-built app bot (im/v1/messages)
    if("$($cfg.feishuAppId)" -and "$($cfg.feishuAppSecret)" -and "$($cfg.feishuChatId)"){
      $token = Get-FeishuTenantToken -AppId "$($cfg.feishuAppId)" -AppSecret "$($cfg.feishuAppSecret)"
      if($token){
        $content = @{ text = $Text } | ConvertTo-Json -Compress
        $body = @{ receive_id="$($cfg.feishuChatId)"; msg_type='text'; content=$content } | ConvertTo-Json -Compress
        $resp = Invoke-RestMethod -Uri 'https://open.feishu.cn/open-apis/im/v1/messages?receive_id_type=chat_id' -Method Post -TimeoutSec 10 `
                  -Headers @{ Authorization = "Bearer $token" } -ContentType 'application/json; charset=utf-8' -Body ([System.Text.Encoding]::UTF8.GetBytes($body))
        if($resp.code -eq 0){ return $true }
      }
    }
    # 2) custom-bot webhook (optionally signed)
    $Webhook = "$($cfg.feishuWebhook)"; if(-not $Webhook){ return $false }
    $Secret = "$($cfg.feishuSecret)"
    $payload = [ordered]@{ msg_type='text'; content=@{ text=$Text } }
    if($Secret){
      $ts = [string][DateTimeOffset]::UtcNow.ToUnixTimeSeconds()
      $hmac = New-Object System.Security.Cryptography.HMACSHA256
      $hmac.Key = [System.Text.Encoding]::UTF8.GetBytes("$ts`n$Secret")
      $payload['sign'] = [Convert]::ToBase64String($hmac.ComputeHash([byte[]]@()))
      $hmac.Dispose()
      $payload['timestamp'] = $ts
    }
    $body = $payload | ConvertTo-Json -Depth 4 -Compress
    $null = Invoke-RestMethod -Uri $Webhook -Method Post -TimeoutSec 10 `
              -ContentType 'application/json; charset=utf-8' `
              -Body ([System.Text.Encoding]::UTF8.GetBytes($body))
    return $true
  } catch { return $false }
}

function Save-RealResetFromProbe {
  # Persist the EXACT reset(s) a probe returned into a state object (only overwrites when the
  # server actually sent a value, so a low-utilization probe never wipes a good number).
  # Stored as Unix SECONDS (integers): ConvertFrom-Json silently rebases ISO strings to a local
  # [DateTime], but leaves integers untouched -> timezone-safe round-trip. Read with FromUnixTimeSeconds.
  param($Probe, $State)
  if($null -eq $State){ $State = Get-CcuState }
  if(-not $Probe){ return $State }
  $nowUnix = [DateTimeOffset]::UtcNow.ToUnixTimeSeconds()
  if($Probe.fiveHourResetUtc){
    $State.realFiveHourResetUtc = $Probe.fiveHourResetUtc.ToUnixTimeSeconds()
    $State.realResetProbedUtc   = $nowUnix
    if($null -ne $Probe.fiveHourUtil){ $State.realFiveHourUtil = $Probe.fiveHourUtil }
  }
  if($Probe.sevenDayResetUtc){
    $State.realSevenDayResetUtc = $Probe.sevenDayResetUtc.ToUnixTimeSeconds()
    $State.realResetProbedUtc   = $nowUnix
  }
  return $State
}

function Invoke-ClaudeResume {
  param([pscustomobject]$Project, [string]$Prompt='continue', [switch]$SkipPermissions,
        [string]$Model='', [int]$TimeoutMin=0, $UiSink=$null, $CancelFlag=$null, [string]$RunKey='')
  $claude = Get-ClaudeCmd
  $res = @{ project=$Project.name; status='error'; exitCode=$null; limited=$false; resultOk=$false }
  if(-not $claude){ $res.status='no-claude'; return $res }

  $isCancelled = {
    if($CancelFlag -is [scriptblock]){ try { return [bool](& $CancelFlag) } catch { return $false } }
    return [bool]($CancelFlag -and $CancelFlag.v)
  }
  if(& $isCancelled){ $res.status='stopped'; return $res }
  if(-not $RunKey){ $RunKey = 'resume|' + "$($Project.path)" }

  $outFile = [IO.Path]::GetTempFileName(); $errFile = [IO.Path]::GetTempFileName()
  $a = New-Object System.Collections.Generic.List[string]
  $a.Add('/c'); $a.Add('"'+$claude+'"'); $a.Add('--continue')
  $a.Add('-p'); $a.Add($Prompt); $a.Add('--output-format'); $a.Add('stream-json'); $a.Add('--verbose')
  if($Model){ $a.Add('--model'); $a.Add($Model) }
  if($SkipPermissions){ $a.Add('--dangerously-skip-permissions') }

  if(-not (Register-CcuBackgroundLaunch -Project $Project -RunKey $RunKey)){
    $res.status='registry-error'; try { [IO.File]::Delete($outFile) } catch {}; try { [IO.File]::Delete($errFile) } catch {}; return $res
  }
  try {
    $p = Start-Process -FilePath $env:ComSpec -ArgumentList $a -WorkingDirectory $Project.path `
          -NoNewWindow -PassThru -RedirectStandardOutput $outFile -RedirectStandardError $errFile
  } catch {
    [void](Clear-CcuBackgroundChild 0 $RunKey)
    $res.status='launch-error'; try { [IO.File]::Delete($outFile) } catch {}; try { [IO.File]::Delete($errFile) } catch {}; return $res
  }
  try { $null = $p.Handle } catch {}   # cache NOW or .ExitCode reads $null after exit (PS 5.1, verified)
  if(-not (Register-CcuBackgroundChild -Process $p -Project $Project -RunKey $RunKey)){
    $stopped = Stop-ProcessTree -ProcessId $p.Id
    $res.status = if($stopped){ 'registry-error' } else { 'registry-stop-failed' }
    if($stopped){ [void](Clear-CcuBackgroundChild $p.Id $RunKey) }
    try { [IO.File]::Delete($outFile) } catch {}; try { [IO.File]::Delete($errFile) } catch {}; return $res
  }

  $posO = New-Object psobject -Property @{ v = [long]0 }
  $posE = New-Object psobject -Property @{ v = [long]0 }
  $drain = {
    param($file, $pos)
    try {
      $fs = [System.IO.File]::Open($file, 'Open', 'Read', 'ReadWrite')
      [void]$fs.Seek($pos.v, 'Begin')
      $sr = New-Object System.IO.StreamReader($fs, [System.Text.Encoding]::UTF8)
      while($null -ne ($ln = $sr.ReadLine())){
        if($ln.Length -gt 0){
          if($ln -match '"status"\s*:\s*"(blocked|rejected|limited|exceeded)"' -or
             $ln.ToLower() -match 'usage limit|rate limit|limit reached|weekly limit'){ $res.limited = $true }
          if($ln -match '"type"\s*:\s*"result"' -and $ln -match '"is_error"\s*:\s*false'){ $res.resultOk = $true }
          Write-CcuLog $ln 'stream' $UiSink
        }
      }
      $pos.v = $fs.Position; $sr.Close(); $fs.Close()
    } catch {}
  }

  $deadline = if($TimeoutMin -gt 0){ (Get-Date).AddMinutes($TimeoutMin) } else { $null }
  while(-not $p.HasExited){
    Start-Sleep -Milliseconds 500
    & $drain $outFile $posO; & $drain $errFile $posE
    if(& $isCancelled){
      if(Stop-ProcessTree -ProcessId $p.Id){ $res.status='stopped'; [void](Clear-CcuBackgroundChild $p.Id $RunKey) }
      else { $res.status='stop-failed' }
      break
    }
    if($deadline -and (Get-Date) -gt $deadline){
      if(Stop-ProcessTree -ProcessId $p.Id){ $res.status='timeout'; [void](Clear-CcuBackgroundChild $p.Id $RunKey) }
      else { $res.status='timeout-stop-failed' }
      break
    }
  }
  Start-Sleep -Milliseconds 300
  & $drain $outFile $posO; & $drain $errFile $posE
  # authoritative re-scan of the FULL output: the drain's ReadLine can split a line that was
  # flushed in chunks, defeating the same-line matches above (structured checks only here)
  try {
    $all = ''
    try { $all  = [IO.File]::ReadAllText($outFile, [Text.Encoding]::UTF8) } catch {}
    try { $all += "`n" + [IO.File]::ReadAllText($errFile, [Text.Encoding]::UTF8) } catch {}
    foreach($ln in ($all -split "[`r`n]+")){
      if($ln -match '"type"\s*:\s*"result"' -and $ln -match '"is_error"\s*:\s*false'){ $res.resultOk = $true }
      if($ln -match '"status"\s*:\s*"(blocked|rejected|limited|exceeded)"'){ $res.limited = $true }
    }
  } catch {}
  if(@('stopped','stop-failed','timeout','timeout-stop-failed') -notcontains $res.status){
    try { $res.exitCode = $p.ExitCode } catch {}
    # a completed result line beats everything (a successful run may TALK about rate limits;
    # a genuinely limited run never completes with is_error:false); exit code is last resort
    if($res.resultOk -or $res.exitCode -eq 0){ $res.status='success' }
    elseif($res.limited){ $res.status='limited' }
    else { $res.status = 'exit-' + $(if($null -eq $res.exitCode){ 'null' } else { $res.exitCode }) }
  }
  try { if($p.HasExited){ [void](Clear-CcuBackgroundChild $p.Id $RunKey) } } catch {}
  try { [IO.File]::Delete($outFile) } catch {}; try { [IO.File]::Delete($errFile) } catch {}
  return $res
}

# Generate/refresh a project's AI_GUIDE.md (the project-tour flow) by running claude headless in the
# project. The multi-line instruction is fed via STDIN (a -p arg would be truncated at the first
# newline by cmd — see LESSONS.md); success = AI_GUIDE.md's mtime advanced.
function Invoke-ProjectTour {
  param([pscustomobject]$Project, [string]$Model='sonnet', [int]$TimeoutMin=12, $CancelFlag=$null)
  $claude = Get-ClaudeCmd
  $res = @{ project=$Project.name; status='error'; wrote=$false }
  if(-not $claude){ $res.status='no-claude'; return $res }
  $guide = Join-Path $Project.path 'AI_GUIDE.md'
  $before = if(Test-Path $guide){ (Get-Item $guide).LastWriteTimeUtc } else { [datetime]::MinValue }
  $prompt = @'
为当前目录的这个代码项目生成一份面向 AI 只读问答的导览文件 AI_GUIDE.md(写在项目根),让别人能快速理解项目并回答技术问题。

步骤:
1) 先看清项目结构;
2) 生成符号级摘要(在 bash 里跑):npx --yes repomix --compress --style markdown --ignore "test_data/**,**/*.mat,**/RawData/**,**/__pycache__/**,*.png,*.jpg,*.csv,dist/**,build/**,node_modules/**,.git/**" -o ".repomix.md" —— 失败就用 ls / 读源码替代;
3) 读 docs/、README、.repomix.md,理解架构/数据流/模块职责/测试运行流程/数据文件命名约定;
4) 写 AI_GUIDE.md:第一行必须是 <!-- project-tour · generated <当前本地时间到分钟> · git <运行 git rev-parse --short HEAD 得到的短 hash;非 git 仓库写 nogit> -->;之后按 8 节写:①一句话定位 ②架构与数据流(ASCII 图)③模块职责表(路径→职责→关键函数)④测试/运行流程(入口/命令/参数/依赖)⑤数据格式与命名约定(逐字段解码一个真实数据文件名样例)⑥FAQ(同事最可能问的 5-10 个技术问题,直接给答案)⑦术语表(中英对照)⑧文档索引;
5) 全文用中文、200-400 行、自足(常见问题不用打开别的文件就能答);
6) 删除临时 .repomix.md。

约束:只读分析代码、绝不修改任何源码,只新增/覆盖 AI_GUIDE.md 这一个文件。数据文件(成千上万的 .mat/.png 等)必须忽略、绝不逐个读。完成后简短说明写了哪几节。
'@
  # feed the multi-line prompt via STDIN (a -p arg would be truncated at the first newline by cmd;
  # PS 5.1 has no Start-Process -RedirectStandardInput, and `cmd < file` breaks under -ArgumentList).
  $psi = New-Object System.Diagnostics.ProcessStartInfo
  $psi.FileName = $env:ComSpec
  $psi.Arguments = '/c "' + $claude + '" -p --output-format stream-json --verbose --dangerously-skip-permissions' + $(if($Model){ ' --model ' + $Model } else { '' })
  $psi.WorkingDirectory = $Project.path
  $psi.UseShellExecute = $false
  $psi.RedirectStandardInput = $true; $psi.RedirectStandardOutput = $true; $psi.RedirectStandardError = $true
  $psi.StandardOutputEncoding = [System.Text.Encoding]::UTF8
  try {
    $p = [System.Diagnostics.Process]::Start($psi)
    try { $null = $p.Handle } catch {}
    $outTask = $p.StandardOutput.ReadToEndAsync()   # drain both streams so a full pipe never blocks claude
    $errTask = $p.StandardError.ReadToEndAsync()
    $bytes = [System.Text.Encoding]::UTF8.GetBytes($prompt)
    $p.StandardInput.BaseStream.Write($bytes, 0, $bytes.Length); $p.StandardInput.BaseStream.Flush(); $p.StandardInput.Close()
    $deadline = (Get-Date).AddMinutes($TimeoutMin)
    while(-not $p.HasExited){
      Start-Sleep -Milliseconds 800
      if($CancelFlag -and $CancelFlag.v){ Stop-ProcessTree -ProcessId $p.Id; $res.status='stopped'; break }
      if((Get-Date) -gt $deadline){ Stop-ProcessTree -ProcessId $p.Id; $res.status='timeout'; break }
    }
  } catch { $res.status = 'error'; return $res }
  Start-Sleep -Milliseconds 400
  if(@('stopped','timeout') -notcontains $res.status){
    $after = if(Test-Path $guide){ (Get-Item $guide).LastWriteTimeUtc } else { [datetime]::MinValue }
    $res.wrote = ($after -gt $before)
    $res.status = if($res.wrote){ 'success' } else { 'error' }
  }
  return $res
}

function Format-Countdown { param([double]$Seconds)
  if($null -eq $Seconds){ return '-' }
  if($Seconds -lt 0){ $Seconds = 0 }
  $t = [TimeSpan]::FromSeconds($Seconds)
  # NOTE: [int]4.59 ROUNDS to 5 in PowerShell -> use Floor for the hour part
  if($t.TotalHours -ge 1){ return ('{0}h {1:00}m' -f [int][Math]::Floor($t.TotalHours), $t.Minutes) }
  return ('{0}m {1:00}s' -f $t.Minutes, $t.Seconds)
}
