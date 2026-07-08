<#
  lib.ps1 - shared engine for "Claude Resume"
  Correctness rules (from adversarial review + live testing):
   * Timezone: extract endTime from RAW ccusage json (ConvertFrom-Json rebases ISO-Z to local),
     then [DateTimeOffset]::Parse(...RoundtripKind).ToUniversalTime() vs [DateTimeOffset]::UtcNow.
   * Launch: claude.cmd via cmd.exe /c (UseShellExecute=false cannot exec a .cmd); tail the
     redirect file for live output; kill the WHOLE process tree on stop/timeout (verified).
   * Fail-closed: bad ccusage/claude reads assume "still limited", never "clear".
   * This file must be saved UTF-8 WITH BOM so Windows PowerShell 5.1 parses non-ASCII correctly.
#>
Set-StrictMode -Off
$ErrorActionPreference = 'Stop'

$script:AppDir     = Join-Path $env:LOCALAPPDATA 'ClaudeResume'
$script:LogDir     = Join-Path $script:AppDir 'logs'
$script:ConfigPath = Join-Path $script:AppDir 'config.json'
$script:StatePath  = Join-Path $script:AppDir 'state.json'

function Get-CcuCmd {
  $c = Get-Command ccusage.cmd -ErrorAction SilentlyContinue
  if(-not $c){ $c = Get-Command ccusage -ErrorAction SilentlyContinue }
  if($c){ return $c.Source }
  $p = Join-Path $env:APPDATA 'npm\ccusage.cmd'; if(Test-Path $p){ return $p }
  return $null
}
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
    enabled=$false; armed=$false; continuous=$false; selected=@(); customProjects=@(); hiddenProjects=@();
    resumePrompt='continue'; skipPermissions=$true; dirtyGuard='stash'; perProjectTimeoutMinutes=30;
    safetyMarginSeconds=60; weeklyBackoffMinutes=45; probeModel='haiku'; resumeModel=''; projectHome=''
  }
  if(Test-Path $script:ConfigPath){
    try {
      $loaded = Get-Content $script:ConfigPath -Raw -Encoding UTF8 | ConvertFrom-Json
      foreach($p in $loaded.PSObject.Properties){ $def[$p.Name] = $p.Value }
    } catch {}
  }
  return [pscustomobject]$def
}
function Set-CcuConfig { param($Config)
  ($Config | ConvertTo-Json -Depth 6) | Set-Content -Path $script:ConfigPath -Encoding UTF8
}
function Get-CcuState {
  if(Test-Path $script:StatePath){
    try { return (Get-Content $script:StatePath -Raw -Encoding UTF8 | ConvertFrom-Json) } catch {}
  }
  return [pscustomobject]@{ targetId=$null; targetEndUtc=$null; firedForId=$null; projectStatus=$null; phase='idle'; sawLimited=$false; lastProbeUtc=$null }
}
function Set-CcuState { param($State)
  ($State | ConvertTo-Json -Depth 6) | Set-Content -Path $script:StatePath -Encoding UTF8
}

function Get-CcuResetInfo {
  $r = @{ ok=$false; hasActive=$false; empty=$false; resetUtc=$null;
          nowUtc=[DateTimeOffset]::UtcNow; secondsUntilReset=$null; blockId=$null }
  $ccu = Get-CcuCmd; if(-not $ccu){ return $r }
  try {
    $out = (& $ccu blocks --active --json 2>$null | Out-String).Trim()
    if($LASTEXITCODE -ne 0 -or -not $out){ return $r }
    $o = $out | ConvertFrom-Json
    $r.ok = $true
    $active = @($o.blocks) | Where-Object { $_.isActive -and -not $_.isGap } | Select-Object -First 1
    if(-not $active){ $r.empty = $true; return $r }
    $r.hasActive = $true
    # Extract endTime + id from RAW json: ConvertFrom-Json silently rebases ISO-Z to local (~8h error).
    $mEnd = [regex]::Match($out, '"endTime"\s*:\s*"([^"]+)"')
    $mId  = [regex]::Match($out, '"id"\s*:\s*"([^"]+)"')
    $endRaw = if($mEnd.Success){ $mEnd.Groups[1].Value } else { "$($active.endTime)" }
    $r.blockId = if($mId.Success){ $mId.Groups[1].Value } else { "$($active.id)" }
    $reset = [DateTimeOffset]::Parse($endRaw, [Globalization.CultureInfo]::InvariantCulture, [Globalization.DateTimeStyles]::RoundtripKind).ToUniversalTime()
    $r.resetUtc = $reset
    $r.secondsUntilReset = ($reset - $r.nowUtc).TotalSeconds
  } catch { $r.ok = $false }
  return $r
}

function Get-SessionReset {
  # Estimate the 5h session reset as (oldest message still within the last 5h) + 5h -- a rolling
  # window that tracks Claude's real session limit far better than ccusage's gap-split blocks.
  # Still an ESTIMATE (exact value is on claude.ai); the checker fires on a live probe, not this.
  $r = @{ ok=$false; resetUtc=$null; secondsUntilReset=$null; hasActivity=$false; nowUtc=[DateTimeOffset]::UtcNow }
  try {
    $root = Join-Path $env:USERPROFILE '.claude\projects'
    if(-not (Test-Path $root)){ return $r }
    $nowU = $r.nowUtc; $cutoff = $nowU.AddHours(-11)
    $ts = New-Object System.Collections.Generic.List[DateTimeOffset]
    foreach($f in (Get-ChildItem $root -Recurse -Filter *.jsonl -ErrorAction SilentlyContinue | Where-Object { $_.LastWriteTimeUtc -gt $cutoff.UtcDateTime })){
      $raw = [System.IO.File]::ReadAllText($f.FullName)
      foreach($m in [regex]::Matches($raw, '"timestamp"\s*:\s*"([^"]+Z)"')){
        try { $t=[DateTimeOffset]::Parse($m.Groups[1].Value,[Globalization.CultureInfo]::InvariantCulture,[Globalization.DateTimeStyles]::RoundtripKind).ToUniversalTime(); if($t -gt $cutoff -and $t -le $nowU.AddMinutes(2)){ $ts.Add($t) } } catch {}
      }
    }
    $r.ok = $true
    if($ts.Count -eq 0){ return $r }   # no recent activity
    # Rolling 5h window: reset = (oldest message still within the last 5h) + 5h.
    # Matches Claude's rolling session limit far better than gap-split blocks or window-chaining.
    $sorted = $ts.ToArray(); [Array]::Sort($sorted)
    $win5 = $nowU.AddHours(-5)
    $oldest = $null
    foreach($t in $sorted){ if($t -gt $win5){ $oldest = $t; break } }
    if(-not $oldest){ return $r }      # activity exists but none in the last 5h -> window already clear
    $r.hasActivity = $true
    $reset = $oldest.AddHours(5)
    $r.resetUtc = $reset; $r.secondsUntilReset = ($reset - $nowU).TotalSeconds
  } catch { $r.ok = $false }
  return $r
}

function Get-ClaudeProjects {
  $root = Join-Path $env:USERPROFILE '.claude\projects'
  $list = @()
  if(-not (Test-Path $root)){ return $list }
  foreach($dir in (Get-ChildItem $root -Directory -ErrorAction SilentlyContinue)){
    $jsonl = Get-ChildItem $dir.FullName -Filter *.jsonl -ErrorAction SilentlyContinue |
             Sort-Object LastWriteTime -Descending | Select-Object -First 1
    if(-not $jsonl){ continue }
    $cwd = $null; $sid = [IO.Path]::GetFileNameWithoutExtension($jsonl.Name)
    try {
      foreach($ln in (Get-Content $jsonl.FullName -TotalCount 60 -ErrorAction SilentlyContinue)){
        if($ln -match '"cwd"'){
          try { $j = $ln | ConvertFrom-Json; if($j.cwd){ $cwd=$j.cwd; if($j.sessionId){ $sid=$j.sessionId }; break } } catch {}
        }
      }
    } catch {}
    if(-not $cwd){ continue }
    if(-not (Test-Path $cwd)){ continue }
    if($cwd -like "$env:WINDIR*"){ continue }
    $list += [pscustomobject]@{
      name = Split-Path $cwd -Leaf; path = $cwd; sessionId = $sid;
      lastUsedUtc = $jsonl.LastWriteTimeUtc; folder = $dir.Name;
      isGit = (Test-Path (Join-Path $cwd '.git'))
    }
  }
  $list = $list | Group-Object path | ForEach-Object { $_.Group | Sort-Object lastUsedUtc -Descending | Select-Object -First 1 }
  return @($list | Sort-Object lastUsedUtc -Descending)
}

function Stop-ProcessTree { param([int]$ProcessId)
  try {
    foreach($k in (Get-CimInstance Win32_Process -Filter "ParentProcessId=$ProcessId" -ErrorAction SilentlyContinue)){
      Stop-ProcessTree -ProcessId ([int]$k.ProcessId)
    }
  } catch {}
  try { Stop-Process -Id $ProcessId -Force -ErrorAction SilentlyContinue } catch {}
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

function Test-ClaudeReady {
  param([string]$Model='haiku', [int]$TimeoutSec=60)
  $claude = Get-ClaudeCmd
  $r = @{ ready=$false; reason='unknown'; output='' }
  if(-not $claude){ $r.reason='no-claude'; return $r }
  $tmpOut = [IO.Path]::GetTempFileName(); $tmpErr = [IO.Path]::GetTempFileName()
  try {
    $a = @('/c','"'+$claude+'"','-p','ready','--model',$Model,'--max-turns','1','--output-format','text')
    $p = Start-Process -FilePath $env:ComSpec -ArgumentList $a -NoNewWindow -PassThru `
          -RedirectStandardOutput $tmpOut -RedirectStandardError $tmpErr
    if(-not $p.WaitForExit($TimeoutSec*1000)){ Stop-ProcessTree -ProcessId $p.Id; $r.reason='timeout'; return $r }
    $so=''; $se=''
    try { $so = [IO.File]::ReadAllText($tmpOut, [Text.Encoding]::UTF8) } catch {}
    try { $se = [IO.File]::ReadAllText($tmpErr, [Text.Encoding]::UTF8) } catch {}
    $r.output = ($so + "`n" + $se); $blob = $r.output.ToLower()
    if($blob -match 'usage limit|rate limit|limit reached|5-hour limit|weekly limit|too many requests|resets at'){ $r.reason='limited'; return $r }
    if($p.ExitCode -eq 0){ $r.ready=$true; $r.reason='ok' } else { $r.reason="exit-$($p.ExitCode)" }
  } catch { $r.reason="err:$($_.Exception.Message)" }
  finally { try { [IO.File]::Delete($tmpOut) } catch {}; try { [IO.File]::Delete($tmpErr) } catch {} }
  return $r
}

function Invoke-ClaudeResume {
  param([pscustomobject]$Project, [string]$Prompt='continue', [switch]$SkipPermissions,
        [string]$Model='', [int]$TimeoutMin=30, $UiSink=$null, $CancelFlag=$null)
  $claude = Get-ClaudeCmd
  $res = @{ project=$Project.name; status='error'; exitCode=$null; limited=$false }
  if(-not $claude){ $res.status='no-claude'; return $res }

  $outFile = [IO.Path]::GetTempFileName(); $errFile = [IO.Path]::GetTempFileName()
  $a = New-Object System.Collections.Generic.List[string]
  $a.Add('/c'); $a.Add('"'+$claude+'"'); $a.Add('--continue')
  $a.Add('-p'); $a.Add($Prompt); $a.Add('--output-format'); $a.Add('stream-json'); $a.Add('--verbose')
  if($Model){ $a.Add('--model'); $a.Add($Model) }
  if($SkipPermissions){ $a.Add('--dangerously-skip-permissions') }

  $p = Start-Process -FilePath $env:ComSpec -ArgumentList $a -WorkingDirectory $Project.path `
        -NoNewWindow -PassThru -RedirectStandardOutput $outFile -RedirectStandardError $errFile

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
          if($ln.ToLower() -match 'usage limit|rate limit|limit reached|weekly limit'){ $res.limited = $true }
          Write-CcuLog $ln 'stream' $UiSink
        }
      }
      $pos.v = $fs.Position; $sr.Close(); $fs.Close()
    } catch {}
  }

  $deadline = (Get-Date).AddMinutes($TimeoutMin)
  while(-not $p.HasExited){
    Start-Sleep -Milliseconds 500
    & $drain $outFile $posO; & $drain $errFile $posE
    if($CancelFlag -and $CancelFlag.v){ Stop-ProcessTree -ProcessId $p.Id; $res.status='stopped'; break }
    if((Get-Date) -gt $deadline){ Stop-ProcessTree -ProcessId $p.Id; $res.status='timeout'; break }
  }
  Start-Sleep -Milliseconds 300
  & $drain $outFile $posO; & $drain $errFile $posE
  if(@('stopped','timeout') -notcontains $res.status){
    try { $res.exitCode = $p.ExitCode } catch {}
    if($res.limited){ $res.status='limited' }
    elseif($res.exitCode -eq 0){ $res.status='success' }
    else { $res.status="exit-$($res.exitCode)" }
  }
  try { [IO.File]::Delete($outFile) } catch {}; try { [IO.File]::Delete($errFile) } catch {}
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
