$ErrorActionPreference = 'Stop'

function Get-CcuRequiredFeishuSdkVersion([string]$SourceRoot){
  $packagePath = Join-Path $SourceRoot 'package.json'
  if(-not (Test-Path -LiteralPath $packagePath -PathType Leaf)){ throw "Missing package.json: $packagePath" }
  try { $package = Get-Content -LiteralPath $packagePath -Raw | ConvertFrom-Json }
  catch { throw ('Unable to parse package.json: ' + $_.Exception.Message) }
  $version = [string]$package.dependencies.'@larksuiteoapi/node-sdk'
  if($version -notmatch '^\d+\.\d+\.\d+$'){ throw 'The Feishu SDK dependency must be pinned to an exact semantic version' }
  return $version
}

function Test-CcuFeishuSdkContract([string]$AppDir,[string]$ExpectedVersion){
  $sdkRoot = Join-Path $AppDir 'node_modules\@larksuiteoapi\node-sdk'
  $sdkPackage = Join-Path $sdkRoot 'package.json'
  $sdkTypes = Join-Path $sdkRoot 'types\index.d.ts'
  if(-not (Test-Path -LiteralPath $sdkPackage -PathType Leaf) -or -not (Test-Path -LiteralPath $sdkTypes -PathType Leaf)){ return $false }
  try {
    $installed = Get-Content -LiteralPath $sdkPackage -Raw | ConvertFrom-Json
    if([string]$installed.version -ne $ExpectedVersion){ return $false }
    return [bool](Select-String -LiteralPath $sdkTypes -Pattern 'onReady\?: \(\) => void;' -Quiet)
  } catch { return $false }
}

function Ensure-CcuFeishuSdk {
  param(
    [Parameter(Mandatory=$true)][string]$AppDir,
    [Parameter(Mandatory=$true)][string]$ExpectedVersion,
    [scriptblock]$InstallSdk
  )
  if(Test-CcuFeishuSdkContract $AppDir $ExpectedVersion){ return }
  if($InstallSdk){
    & $InstallSdk $AppDir $ExpectedVersion
  } else {
    $npm = Get-Command npm -ErrorAction SilentlyContinue
    if(-not $npm){ throw "npm is required to install @larksuiteoapi/node-sdk@$ExpectedVersion" }
    & $npm.Source install --prefix $AppDir --no-save --package-lock=false --no-audit --no-fund ("@larksuiteoapi/node-sdk@" + $ExpectedVersion) 2>&1 | Out-Null
    if($LASTEXITCODE -ne 0){ throw "Unable to install @larksuiteoapi/node-sdk@$ExpectedVersion" }
  }
  if(-not (Test-CcuFeishuSdkContract $AppDir $ExpectedVersion)){
    throw "Installed Feishu SDK does not satisfy the pinned onReady contract: $ExpectedVersion"
  }
}

function Stop-CcuFeishuAgentProcessTree {
  param(
    [Parameter(Mandatory=$true)][string]$AppDir,
    [scriptblock]$ListProcesses,
    [scriptblock]$StopProcessTree,
    [scriptblock]$InspectProcess
  )
  $agentScript = [IO.Path]::GetFullPath((Join-Path $AppDir 'feishu-agent.js'))
  if(-not $ListProcesses){
    $ListProcesses = {
      param($scriptPath)
      @(Get-CimInstance Win32_Process -Filter "Name='node.exe'" -ErrorAction Stop | Where-Object {
        $_.CommandLine -and ([string]$_.CommandLine).IndexOf($scriptPath,[StringComparison]::OrdinalIgnoreCase) -ge 0
      })
    }
  }
  if(-not $StopProcessTree){
    $StopProcessTree = {
      param($processId)
      & taskkill.exe /PID ([string]$processId) /T /F 2>&1 | Out-Null
      return $LASTEXITCODE -eq 0
    }
  }
  if(-not $InspectProcess){
    $InspectProcess = {
      param($processId)
      $process = Get-CimInstance Win32_Process -Filter ("ProcessId=" + [string]$processId) -ErrorAction Stop
      if($null -eq $process){ return 'gone' }
      return 'found'
    }
  }

  try { $processes = @(& $ListProcesses $agentScript) }
  catch { throw ('Unable to enumerate the Feishu agent process: ' + $_.Exception.Message) }
  $stopped = @()
  foreach($process in $processes){
    $processId = [int]$process.ProcessId
    if($processId -le 0){ throw 'Feishu agent process enumeration returned an invalid PID' }
    try { $stopConfirmed = (& $StopProcessTree $processId) -eq $true }
    catch { throw ("Unable to stop Feishu agent process tree PID ${processId}: " + $_.Exception.Message) }
    if(-not $stopConfirmed){ throw "Unable to stop Feishu agent process tree PID ${processId}: taskkill was not confirmed" }
    $gone = $false
    for($attempt = 0; $attempt -lt 21; $attempt++){
      try { $state = [string](& $InspectProcess $processId) }
      catch { throw ("Unable to verify Feishu agent process PID ${processId}: " + $_.Exception.Message) }
      if($state -eq 'gone'){ $gone = $true; break }
      if($state -ne 'found'){ throw "Unable to verify Feishu agent process PID ${processId}: invalid probe state '$state'" }
      if($attempt -lt 20){ Start-Sleep -Milliseconds 100 }
    }
    if(-not $gone){ throw "Feishu agent process PID ${processId} is still alive after taskkill" }
    $stopped += $processId
  }
  return $stopped
}

function Wait-CcuFeishuAgentReady {
  param(
    [Parameter(Mandatory=$true)][string]$AppDir,
    [Parameter(Mandatory=$true)][ValidatePattern('^[0-9a-fA-F]{32}$')][string]$ExpectedGeneration,
    [long]$LogOffset = 0,
    [long[]]$StoppedProcessIds = @(),
    [int]$TimeoutMilliseconds = 30000,
    [scriptblock]$ListProcesses,
    [scriptblock]$ReadLogTail,
    [scriptblock]$Sleep
  )
  $agentScript = [IO.Path]::GetFullPath((Join-Path $AppDir 'feishu-agent.js'))
  $logPath = Join-Path (Join-Path $AppDir 'logs') 'feishu-stdout.log'
  if(-not $ListProcesses){
    $ListProcesses = {
      param($scriptPath)
      @(Get-CimInstance Win32_Process -Filter "Name='node.exe'" -ErrorAction Stop | Where-Object {
        $_.CommandLine -and ([string]$_.CommandLine).IndexOf($scriptPath,[StringComparison]::OrdinalIgnoreCase) -ge 0
      })
    }
  }
  if(-not $ReadLogTail){
    $ReadLogTail = {
      param($path,$offset)
      if(-not (Test-Path -LiteralPath $path -PathType Leaf)){ return '' }
      $stream = [IO.File]::Open($path,[IO.FileMode]::Open,[IO.FileAccess]::Read,[IO.FileShare]::ReadWrite)
      try {
        $start = if($offset -ge 0 -and $offset -le $stream.Length){ $offset } else { 0 }
        [void]$stream.Seek($start,[IO.SeekOrigin]::Begin)
        $reader = [IO.StreamReader]::new($stream,[Text.Encoding]::UTF8,$true,4096,$true)
        try { return $reader.ReadToEnd() } finally { $reader.Dispose() }
      } finally { $stream.Dispose() }
    }
  }
  if(-not $Sleep){ $Sleep = { param($milliseconds) Start-Sleep -Milliseconds $milliseconds } }

  function Get-CcuProcessStartMilliseconds($process){
    if($null -eq $process.CreationDate){ throw 'restarted Feishu agent creation time is unavailable' }
    try {
      $created = [datetime]$process.CreationDate
      return [DateTimeOffset]::new($created.ToUniversalTime()).ToUnixTimeMilliseconds()
    } catch { throw ('restarted Feishu agent creation time is invalid: ' + $_.Exception.Message) }
  }

  $watch = [Diagnostics.Stopwatch]::StartNew()
  do {
    try { $processes = @(& $ListProcesses $agentScript) }
    catch { throw ('Unable to verify the restarted Feishu agent process: ' + $_.Exception.Message) }
    if($processes.Count -gt 1){ throw ('Multiple Feishu agent processes detected after redeploy: ' + $processes.Count) }
    if($processes.Count -eq 1){
      $processId = [int]$processes[0].ProcessId
      if($processId -le 0){ throw 'Restarted Feishu agent process has an invalid PID' }
      if(@($StoppedProcessIds) -contains [long]$processId){
        if($watch.ElapsedMilliseconds -lt $TimeoutMilliseconds){ & $Sleep 250 }
        continue
      }
      $creationMs = Get-CcuProcessStartMilliseconds $processes[0]
      try { $tail = [string](& $ReadLogTail $logPath $LogOffset) }
      catch { throw ('Unable to verify Feishu agent readiness log: ' + $_.Exception.Message) }
      $pattern = 'AI_RESUME_AGENT_BOOT pid=' + [regex]::Escape([string]$processId) +
        ' startedAt=(\d+) generation=' + [regex]::Escape($ExpectedGeneration.ToLowerInvariant()) + '(?![0-9a-f])'
      $markers = [regex]::Matches($tail,$pattern)
      $marker = if($markers.Count){ $markers[$markers.Count-1] } else { $null }
      if($null -ne $marker){
        $bootMs = [long]$marker.Groups[1].Value
        if([math]::Abs($bootMs - $creationMs) -gt 10000){
          if($watch.ElapsedMilliseconds -lt $TimeoutMilliseconds){ & $Sleep 250 }
          continue
        }
        $afterMarker = $tail.Substring($marker.Index + $marker.Length)
        $readyPattern = 'AI_RESUME_AGENT_READY pid=' + [regex]::Escape([string]$processId) +
          ' startedAt=' + [regex]::Escape([string]$bootMs) + ' generation=' +
          [regex]::Escape($ExpectedGeneration.ToLowerInvariant()) + '(?![0-9a-f])'
        if($afterMarker -notmatch $readyPattern){
          if($watch.ElapsedMilliseconds -lt $TimeoutMilliseconds){ & $Sleep 250 }
          continue
        }
        try { $confirmed = @(& $ListProcesses $agentScript) }
        catch { throw ('Unable to re-verify the ready Feishu agent process: ' + $_.Exception.Message) }
        if($confirmed.Count -gt 1){ throw ('Multiple Feishu agent processes detected after ready: ' + $confirmed.Count) }
        if($confirmed.Count -ne 1 -or [int]$confirmed[0].ProcessId -ne $processId){ continue }
        if((Get-CcuProcessStartMilliseconds $confirmed[0]) -ne $creationMs){ continue }
        return $processId
      }
    }
    if($watch.ElapsedMilliseconds -lt $TimeoutMilliseconds){ & $Sleep 250 }
  } while($watch.ElapsedMilliseconds -lt $TimeoutMilliseconds)
  throw 'Feishu agent restart was not confirmed: expected exactly one process and a matching structured ready marker'
}

function New-CcuDeploymentPlan([string]$SourceRoot,[string]$DestinationRoot){
  $requiredFiles = @(
    'install.ps1','deploy-files.ps1','lib.ps1','checker.ps1','picker.ps1','provider-health.js',
    'launcher.vbs','checker-launch.vbs','feishu-agent.js','feishu-runtime.js','channel-adapter.js','authorization-policy.js',
    'completion-events.js','conversation-store.js','session-manager.js','feishu-launch.vbs','completion-notify.js',
    'install-completion-hooks.js','task-orchestrator.js','icon.ico','package.json','package-lock.json'
  )
  $requiredAiFiles = @('profiles.js','runners.js','codex-sessions.js','agent-adapter.js')
  $files = @()

  foreach($name in $requiredFiles){
    $source = Join-Path $SourceRoot $name
    if(-not (Test-Path -LiteralPath $source -PathType Leaf)){
      throw "Required deployment source is missing or is not a file: $source"
    }
    $files += [pscustomobject]@{Source=$source;Destination=(Join-Path $DestinationRoot $name)}
  }

  $aiSource = Join-Path $SourceRoot 'ai'
  if(-not (Test-Path -LiteralPath $aiSource -PathType Container)){
    throw "Required AI source directory is missing or is not a directory: $aiSource"
  }
  foreach($name in $requiredAiFiles){
    $source = Join-Path $aiSource $name
    if(-not (Test-Path -LiteralPath $source -PathType Leaf)){
      throw "Required AI deployment source is missing or is not a file: $source"
    }
  }
  foreach($source in Get-ChildItem -LiteralPath $aiSource -File){
    $files += [pscustomobject]@{Source=$source.FullName;Destination=(Join-Path (Join-Path $DestinationRoot 'ai') $source.Name)}
  }
  return $files
}

function Test-SameFileContent([string]$Source,[string]$Destination){
  if(-not (Test-Path -LiteralPath $Source) -or -not (Test-Path -LiteralPath $Destination)){ return $false }
  try {
    $src = Get-Item -LiteralPath $Source
    $dst = Get-Item -LiteralPath $Destination
    if($src.Length -ne $dst.Length){ return $false }
    return (Get-FileHash -Algorithm SHA256 -LiteralPath $Source).Hash -eq
      (Get-FileHash -Algorithm SHA256 -LiteralPath $Destination).Hash
  } catch { return $false }
}

function Set-CcuDeployedFile([string]$Source,[string]$Destination){
  $parent = Split-Path $Destination -Parent
  if(-not (Test-Path -LiteralPath $parent)){ New-Item -ItemType Directory -Force -Path $parent | Out-Null }
  $temp = Join-Path $parent ('.' + [IO.Path]::GetFileName($Destination) + '.deploy-' + [guid]::NewGuid().ToString('N') + '.tmp')
  $replacedBackup = $temp + '.bak'
  try {
    Copy-Item -LiteralPath $Source -Destination $temp -Force
    if(Test-Path -LiteralPath $Destination){
      [IO.File]::Replace($temp,$Destination,$replacedBackup,$true)
    } else {
      [IO.File]::Move($temp,$Destination)
    }
  } finally {
    if(Test-Path -LiteralPath $temp){ Remove-Item -LiteralPath $temp -Force -ErrorAction SilentlyContinue }
    if(Test-Path -LiteralPath $replacedBackup){ Remove-Item -LiteralPath $replacedBackup -Force -ErrorAction SilentlyContinue }
  }
}

function Invoke-CcuFileDeployment {
  param(
    [Parameter(Mandatory=$true)][object[]]$Files,
    [Parameter(Mandatory=$true)][string]$TransactionRoot,
    [scriptblock]$AfterApply,
    [System.Management.Automation.PSReference]$AfterApplyResult
  )
  $root = [IO.Path]::GetFullPath($TransactionRoot)
  if(-not (Test-Path -LiteralPath $root)){ New-Item -ItemType Directory -Force -Path $root | Out-Null }
  $transaction = Join-Path $root ('.deploy-' + [guid]::NewGuid().ToString('N'))
  $stage = Join-Path $transaction 'stage'
  $backup = Join-Path $transaction 'backup'
  $cleanupTransaction = $true
  New-Item -ItemType Directory -Force -Path $stage,$backup | Out-Null
  try {
    $plan = @()
    $index = 0
    foreach($file in $Files){
      $source = [IO.Path]::GetFullPath([string]$file.Source)
      $destination = [IO.Path]::GetFullPath([string]$file.Destination)
      if(-not (Test-Path -LiteralPath $source -PathType Leaf)){ throw "Deployment source is missing or is not a file: $source" }
      if(Test-SameFileContent $source $destination){ continue }
      $stageFile = Join-Path $stage ([string]$index + '.bin')
      $backupFile = Join-Path $backup ([string]$index + '.bin')
      Copy-Item -LiteralPath $source -Destination $stageFile -Force
      $hadDestination = Test-Path -LiteralPath $destination
      if($hadDestination){ Copy-Item -LiteralPath $destination -Destination $backupFile -Force }
      $plan += [pscustomobject]@{
        Source=$source; Destination=$destination; Stage=$stageFile; Backup=$backupFile; HadDestination=$hadDestination
      }
      $index++
    }

    $applied = @()
    try {
      foreach($item in $plan){
        $applied += $item
        Set-CcuDeployedFile $item.Stage $item.Destination
      }
      if($AfterApply){
        $callbackResult = @(& $AfterApply)
        if($null -ne $AfterApplyResult){ $AfterApplyResult.Value = $callbackResult }
      }
    } catch {
      $applyError = $_
      $rollbackErrors = @()
      for($i=$applied.Count-1; $i -ge 0; $i--){
        $item = $applied[$i]
        try {
          if($item.HadDestination){
            if(-not (Test-SameFileContent $item.Backup $item.Destination)){ Set-CcuDeployedFile $item.Backup $item.Destination }
          }
          elseif(Test-Path -LiteralPath $item.Destination){ Remove-Item -LiteralPath $item.Destination -Force }
        } catch { $rollbackErrors += $_.Exception.Message }
      }
      if($rollbackErrors.Count){
        $cleanupTransaction = $false
        throw ('Deployment failed: ' + $applyError.Exception.Message + '; rollback failed: ' + ($rollbackErrors -join ' | ') + '; recovery files kept at: ' + $transaction)
      }
      throw $applyError
    }
    return $plan.Count
  } finally {
    if($cleanupTransaction -and (Test-Path -LiteralPath $transaction)){ Remove-Item -LiteralPath $transaction -Recurse -Force -ErrorAction SilentlyContinue }
  }
}
