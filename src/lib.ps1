<#
  lib.ps1 - shared engine for "Claude Resume"
  Correctness rules (from adversarial review + live testing):
   * NO reset-time estimation: the engine probes on a fixed interval and the only reset time
     ever shown is the server-exact resetsAt a probe returns. (ccusage/jsonl estimates were
     removed - they were display noise and once even mis-gated probing.)
   * Launch: claude.cmd via cmd.exe /c (UseShellExecute=false cannot exec a .cmd); tail the
     redirect file for live output; kill the WHOLE process tree on stop/timeout (verified).
   * ExitCode: PS 5.1's Start-Process -PassThru returns $null from .ExitCode after the process
     exits unless $p.Handle was read first (WaitForExit(ms) opens SYNCHRONIZE-only; HasExited
     polling has the same hole - both verified live). Cache the handle AND never rely on the
     exit code alone: a stream-json "type":"result","is_error":false line is the success signal.
   * Fail-closed: bad claude reads assume "still limited", never "clear".
   * This file must be saved UTF-8 WITH BOM so Windows PowerShell 5.1 parses non-ASCII correctly.
#>
Set-StrictMode -Off
$ErrorActionPreference = 'Stop'

$script:AppDir     = Join-Path $env:LOCALAPPDATA 'ClaudeResume'
$script:LogDir     = Join-Path $script:AppDir 'logs'
$script:ConfigPath = Join-Path $script:AppDir 'config.json'
$script:StatePath  = Join-Path $script:AppDir 'state.json'
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
    enabled=$false; armed=$false; continuous=$false; selected=@(); customProjects=@(); hiddenProjects=@();
    resumePrompt='continue'; skipPermissions=$true; dirtyGuard='stash'; perProjectTimeoutMinutes=30;
    safetyMarginSeconds=60; weeklyBackoffMinutes=45; probeModel='haiku'; resumeModel=''; projectHome='';
    feishuWebhook=''; feishuSecret=''; probeIntervalMinutes=15;
    feishuAppId=''; feishuAppSecret=''; feishuChatId=''; feishuDefaultProject=''; feishuAllowOpenIds=@(); feishuChatModel=''
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
  # UTF-8 WITHOUT BOM: PS 5.1's `Set-Content -Encoding UTF8` prepends a BOM that makes Node's
  # JSON.parse throw, which would kill the Feishu agent on its next restart. Write clean bytes.
  [System.IO.File]::WriteAllText($script:ConfigPath, ($Config | ConvertTo-Json -Depth 6), (New-Object System.Text.UTF8Encoding($false)))
}
function Get-CcuState {
  # merge loaded state over defaults so EVERY field always exists (new fields settable without throwing)
  $def = [ordered]@{
    targetId=$null; targetEndUtc=$null; firedForId=$null; projectStatus=$null; phase='idle';
    sawLimited=$false; lastProbeUtc=$null; limitedRefires=0;
    realFiveHourResetUtc=$null; realSevenDayResetUtc=$null; realResetProbedUtc=$null; realFiveHourUtil=$null
  }
  if(Test-Path $script:StatePath){
    try {
      $loaded = Get-Content $script:StatePath -Raw -Encoding UTF8 | ConvertFrom-Json
      foreach($p in $loaded.PSObject.Properties){ $def[$p.Name] = $p.Value }
    } catch {}
  }
  return [pscustomobject]$def
}
function Set-CcuState { param($State)
  # UTF-8 WITHOUT BOM (state.json is read by the Node agent too — see Set-CcuConfig)
  [System.IO.File]::WriteAllText($script:StatePath, ($State | ConvertTo-Json -Depth 6), (New-Object System.Text.UTF8Encoding($false)))
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
  # A live probe = source of truth. Runs claude -p as stream-json so we can read the EXACT
  # reset the server sends in `rate_limit_event` messages (same numbers the /usage screen shows):
  #   {"type":"rate_limit_event","rate_limit_info":{"status":"blocked","resetsAt":<unix>,
  #    "rateLimitType":"five_hour|seven_day","utilization":0..1,...}}
  # resetsAt is only sent once a window crosses ~0.75 utilization (and always when blocked),
  # so fiveHourResetUtc is $null when you're nowhere near the 5h cap -- callers fall back to the estimate.
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
    $low = $blob.ToLower()
    if($low -match 'usage limit|rate limit|limit reached|5-hour limit|weekly limit|too many requests|resets at'){ $r.reason='limited'; return $r }
    $exitCode = $null; try { $exitCode = $p.ExitCode } catch {}
    if($exitCode -eq 0){ $r.ready=$true; $r.reason='ok' }
    else { $r.reason = 'exit-' + $(if($null -eq $exitCode){ 'null' } else { $exitCode }) }
  } catch { $r.reason="err:$($_.Exception.Message)" }
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
        [string]$Model='', [int]$TimeoutMin=30, $UiSink=$null, $CancelFlag=$null)
  $claude = Get-ClaudeCmd
  $res = @{ project=$Project.name; status='error'; exitCode=$null; limited=$false; resultOk=$false }
  if(-not $claude){ $res.status='no-claude'; return $res }

  $outFile = [IO.Path]::GetTempFileName(); $errFile = [IO.Path]::GetTempFileName()
  $a = New-Object System.Collections.Generic.List[string]
  $a.Add('/c'); $a.Add('"'+$claude+'"'); $a.Add('--continue')
  $a.Add('-p'); $a.Add($Prompt); $a.Add('--output-format'); $a.Add('stream-json'); $a.Add('--verbose')
  if($Model){ $a.Add('--model'); $a.Add($Model) }
  if($SkipPermissions){ $a.Add('--dangerously-skip-permissions') }

  $p = Start-Process -FilePath $env:ComSpec -ArgumentList $a -WorkingDirectory $Project.path `
        -NoNewWindow -PassThru -RedirectStandardOutput $outFile -RedirectStandardError $errFile
  try { $null = $p.Handle } catch {}   # cache NOW or .ExitCode reads $null after exit (PS 5.1, verified)

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

  $deadline = (Get-Date).AddMinutes($TimeoutMin)
  while(-not $p.HasExited){
    Start-Sleep -Milliseconds 500
    & $drain $outFile $posO; & $drain $errFile $posE
    if($CancelFlag -and $CancelFlag.v){ Stop-ProcessTree -ProcessId $p.Id; $res.status='stopped'; break }
    if((Get-Date) -gt $deadline){ Stop-ProcessTree -ProcessId $p.Id; $res.status='timeout'; break }
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
  if(@('stopped','timeout') -notcontains $res.status){
    try { $res.exitCode = $p.ExitCode } catch {}
    # a completed result line beats everything (a successful run may TALK about rate limits;
    # a genuinely limited run never completes with is_error:false); exit code is last resort
    if($res.resultOk -or $res.exitCode -eq 0){ $res.status='success' }
    elseif($res.limited){ $res.status='limited' }
    else { $res.status = 'exit-' + $(if($null -eq $res.exitCode){ 'null' } else { $res.exitCode }) }
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
