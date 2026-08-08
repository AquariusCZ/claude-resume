$ErrorActionPreference = 'Stop'
. (Join-Path (Split-Path $PSScriptRoot -Parent) 'src\deploy-files.ps1')

$root = Join-Path ([IO.Path]::GetTempPath()) ('ai-resume-deploy-test-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force -Path $root | Out-Null
try {
  $repoRoot = Split-Path $PSScriptRoot -Parent
  $repoSource = Join-Path $repoRoot 'src'
  $source = Join-Path $root 'source'; $target = Join-Path $root 'target'; $tx = Join-Path $root 'tx'
  New-Item -ItemType Directory -Force -Path $source,$target,$tx | Out-Null

  $plan = @(New-CcuDeploymentPlan $repoSource $target)
  $destinations = @($plan | ForEach-Object { [IO.Path]::GetFileName([string]$_.Destination) })
  foreach($required in 'authorization-policy.js','completion-events.js','conversation-store.js','task-orchestrator.js','channel-adapter.js','feishu-runtime.js','package-lock.json'){
    if($destinations -notcontains $required){ throw "installer omitted required Feishu module: $required" }
  }
  # D-006:稳定入口(wrapper)与兼容 runtime 的直接本地 require 都必须有部署来源。
  foreach($entryFile in 'feishu-agent.js','feishu-runtime.js'){
    $entrySource = Get-Content -LiteralPath (Join-Path $repoSource $entryFile) -Raw
    $topLevelRequires = [regex]::Matches($entrySource,"require\('./([a-z0-9-]+)'\)")
    foreach($match in $topLevelRequires){
      $required = $match.Groups[1].Value + '.js'
      if($destinations -notcontains $required){ throw "installer omitted direct Feishu dependency: $required (from $entryFile)" }
    }
  }
  Write-Host '[PASS] Installer deploys every top-level Feishu agent + runtime module'
  $requiredSdk = Get-CcuRequiredFeishuSdkVersion $repoSource
  if($requiredSdk -ne '1.70.0'){ throw "unexpected pinned Feishu SDK: $requiredSdk" }
  $sdkRoot = Join-Path $target 'node_modules\@larksuiteoapi\node-sdk'
  New-Item -ItemType Directory -Force -Path (Join-Path $sdkRoot 'types') | Out-Null
  [IO.File]::WriteAllText((Join-Path $sdkRoot 'package.json'),'{"version":"1.53.0"}',[Text.UTF8Encoding]::new($false))
  [IO.File]::WriteAllText((Join-Path $sdkRoot 'types\index.d.ts'),'interface IConstructorParams {}',[Text.UTF8Encoding]::new($false))
  if(Test-CcuFeishuSdkContract $target $requiredSdk){ throw 'unsupported SDK contract was accepted' }
  $script:sdkInstallCalls = 0
  Ensure-CcuFeishuSdk -AppDir $target -ExpectedVersion $requiredSdk -InstallSdk {
    param($appDir,$version)
    $script:sdkInstallCalls++
    $root = Join-Path $appDir 'node_modules\@larksuiteoapi\node-sdk'
    New-Item -ItemType Directory -Force -Path (Join-Path $root 'types') | Out-Null
    [IO.File]::WriteAllText((Join-Path $root 'package.json'),('{"version":"' + $version + '"}'),[Text.UTF8Encoding]::new($false))
    [IO.File]::WriteAllText((Join-Path $root 'types\index.d.ts'),'onReady?: () => void;',[Text.UTF8Encoding]::new($false))
  }
  if($script:sdkInstallCalls -ne 1 -or -not (Test-CcuFeishuSdkContract $target $requiredSdk)){
    throw 'installer did not repair and verify the pinned SDK contract'
  }
  Write-Host '[PASS] Installer pins and verifies the real WSClient onReady SDK contract before redeploy'
  $installSource = Get-Content -LiteralPath (Join-Path $repoSource 'install.ps1') -Raw
  $offsetIndex = $installSource.IndexOf('$agentLogOffset =',[StringComparison]::Ordinal)
  $deploymentIndex = $installSource.IndexOf('Invoke-CcuFileDeployment',[StringComparison]::Ordinal)
  if($offsetIndex -lt 0 -or $deploymentIndex -lt 0 -or $offsetIndex -gt $deploymentIndex -or $installSource -notmatch '-AfterApply'){
    throw 'install.ps1 must capture the ready-log offset before deployment and keep process stop inside the rollback transaction'
  }
  Write-Host '[PASS] Installer captures log offset before redeploy and stops the agent inside the rollback transaction'

  $stopped = @(Stop-CcuFeishuAgentProcessTree -AppDir $target `
    -ListProcesses { param($scriptPath) @([pscustomobject]@{ProcessId=41001;CommandLine=('node.exe "' + $scriptPath + '"')}) } `
    -StopProcessTree { param($processId) return $processId -eq 41001 } `
    -InspectProcess { param($processId) return 'gone' })
  if($stopped.Count -ne 1 -or $stopped[0] -ne 41001){ throw 'verified process-tree stop did not return the stopped PID' }
  Write-Host '[PASS] Agent process-tree stop verifies every old PID is gone'

  $taskkillFailed = $false
  try {
    Stop-CcuFeishuAgentProcessTree -AppDir $target `
      -ListProcesses { param($scriptPath) @([pscustomobject]@{ProcessId=41002;CommandLine=('node.exe "' + $scriptPath + '"')}) } `
      -StopProcessTree { param($processId) return $false } `
      -InspectProcess { param($processId) return 'found' } | Out-Null
  } catch { $taskkillFailed = $_.Exception.Message -like '*taskkill was not confirmed*' }
  if(-not $taskkillFailed){ throw 'taskkill failure was swallowed' }
  Write-Host '[PASS] taskkill failure aborts deployment flow'

  $cimFailed = $false
  try {
    Stop-CcuFeishuAgentProcessTree -AppDir $target `
      -ListProcesses { param($scriptPath) throw 'synthetic CIM failure' } `
      -StopProcessTree { param($processId) return $true } `
      -InspectProcess { param($processId) return 'gone' } | Out-Null
  } catch { $cimFailed = $_.Exception.Message -like '*Unable to enumerate*' }
  if(-not $cimFailed){ throw 'CIM enumeration failure was swallowed' }
  Write-Host '[PASS] CIM failure aborts deployment flow'

  $readyStartedAt = [DateTimeOffset]::UtcNow.ToUnixTimeMilliseconds()
  $readyCreatedAt = [DateTimeOffset]::FromUnixTimeMilliseconds($readyStartedAt).UtcDateTime
  $readyGeneration = '11111111111111111111111111111111'
  $readyPid = Wait-CcuFeishuAgentReady -AppDir $target -ExpectedGeneration $readyGeneration -LogOffset 10 -StoppedProcessIds @(41001) -TimeoutMilliseconds 100 `
    -ListProcesses { param($scriptPath) @([pscustomobject]@{ProcessId=42001;CreationDate=$readyCreatedAt;CommandLine=('node.exe "' + $scriptPath + '"')}) } `
    -ReadLogTail { param($path,$offset) return ("AI_RESUME_AGENT_BOOT pid=42001 startedAt=${readyStartedAt} generation=${readyGeneration}`r`nAI_RESUME_AGENT_READY pid=42001 startedAt=${readyStartedAt} generation=${readyGeneration}") } `
    -Sleep { param($milliseconds) }
  if($readyPid -ne 42001){ throw 'ready verification returned the wrong PID' }
  Write-Host '[PASS] Redeploy success requires one restarted agent and a matching structured ready marker'

  $duplicateFailed = $false
  try {
    Wait-CcuFeishuAgentReady -AppDir $target -ExpectedGeneration '22222222222222222222222222222222' -TimeoutMilliseconds 100 `
      -ListProcesses { param($scriptPath) @([pscustomobject]@{ProcessId=42002},[pscustomobject]@{ProcessId=42003}) } `
      -ReadLogTail { param($path,$offset) return 'ws client ready' } `
      -Sleep { param($milliseconds) } | Out-Null
  } catch { $duplicateFailed = $_.Exception.Message -like '*Multiple Feishu agent processes*' }
  if(-not $duplicateFailed){ throw 'duplicate restarted agents were accepted' }
  Write-Host '[PASS] Redeploy rejects multiple restarted agent processes'

  $swapAStartedAt = [DateTimeOffset]::UtcNow.ToUnixTimeMilliseconds()
  $swapBStartedAt = $swapAStartedAt + 1000
  $swapAGeneration = 'aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa'
  $swapBGeneration = 'bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb'
  $swapACreatedAt = [DateTimeOffset]::FromUnixTimeMilliseconds($swapAStartedAt).UtcDateTime
  $swapBCreatedAt = [DateTimeOffset]::FromUnixTimeMilliseconds($swapBStartedAt).UtcDateTime
  $script:readySwapCalls = 0
  $swappedProcessRejected = $false
  try {
    Wait-CcuFeishuAgentReady -AppDir $target -ExpectedGeneration $swapBGeneration -TimeoutMilliseconds 25 `
      -ListProcesses {
        param($scriptPath)
        $script:readySwapCalls++
        if($script:readySwapCalls -eq 1){ return @([pscustomobject]@{ProcessId=43001;CreationDate=$swapACreatedAt}) }
        return @([pscustomobject]@{ProcessId=43002;CreationDate=$swapBCreatedAt})
      } `
      -ReadLogTail {
        param($path,$offset)
        return ("AI_RESUME_AGENT_BOOT pid=43001 startedAt=${swapAStartedAt} generation=${swapAGeneration}`r`nAI_RESUME_AGENT_READY pid=43001 startedAt=${swapAStartedAt} generation=${swapAGeneration}`r`nAI_RESUME_AGENT_BOOT pid=43002 startedAt=${swapBStartedAt} generation=${swapBGeneration}")
      } `
      -Sleep { param($milliseconds) Start-Sleep -Milliseconds 1 } | Out-Null
  } catch { $swappedProcessRejected = $_.Exception.Message -like '*restart was not confirmed*' }
  if(-not $swappedProcessRejected){ throw 'ready log from an exited process was accepted for its replacement PID' }
  Write-Host '[PASS] Ready verification binds the log line to the same stable PID and creation time'

  $reusedPid = 44001
  $reuseAStartedAt = [DateTimeOffset]::UtcNow.ToUnixTimeMilliseconds()
  $reuseBStartedAt = $reuseAStartedAt + 5000
  $reuseAGeneration = 'cccccccccccccccccccccccccccccccc'
  $reuseBGeneration = 'dddddddddddddddddddddddddddddddd'
  $reuseBCreatedAt = [DateTimeOffset]::FromUnixTimeMilliseconds($reuseBStartedAt).UtcDateTime
  $reusedPidRejected = $false
  try {
    Wait-CcuFeishuAgentReady -AppDir $target -ExpectedGeneration $reuseBGeneration -TimeoutMilliseconds 25 `
      -ListProcesses { param($scriptPath) @([pscustomobject]@{ProcessId=$reusedPid;CreationDate=$reuseBCreatedAt}) } `
      -ReadLogTail {
        param($path,$offset)
        return ("AI_RESUME_AGENT_BOOT pid=${reusedPid} startedAt=${reuseAStartedAt} generation=${reuseAGeneration}`r`nAI_RESUME_AGENT_READY pid=${reusedPid} startedAt=${reuseAStartedAt} generation=${reuseAGeneration}`r`nAI_RESUME_AGENT_BOOT pid=${reusedPid} startedAt=${reuseBStartedAt} generation=${reuseBGeneration}")
      } `
      -Sleep { param($milliseconds) Start-Sleep -Milliseconds 1 } | Out-Null
  } catch { $reusedPidRejected = $_.Exception.Message -like '*restart was not confirmed*' }
  if(-not $reusedPidRejected){ throw 'structured ready marker from an earlier process generation was accepted after PID reuse' }
  Write-Host '[PASS] Ready verification rejects an old structured marker after numeric PID reuse'

  $interleavedStartedAt = [DateTimeOffset]::UtcNow.ToUnixTimeMilliseconds()
  $interleavedCreatedAt = [DateTimeOffset]::FromUnixTimeMilliseconds($interleavedStartedAt).UtcDateTime
  $interleavedGeneration = 'eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee'
  $genericReadyRejected = $false
  try {
    Wait-CcuFeishuAgentReady -AppDir $target -ExpectedGeneration $interleavedGeneration -TimeoutMilliseconds 25 `
      -ListProcesses { param($scriptPath) @([pscustomobject]@{ProcessId=45002;CreationDate=$interleavedCreatedAt}) } `
      -ReadLogTail {
        param($path,$offset)
        return ("AI_RESUME_AGENT_BOOT pid=45001 startedAt=$($interleavedStartedAt - 1000) generation=ffffffffffffffffffffffffffffffff`r`nAI_RESUME_AGENT_BOOT pid=45002 startedAt=${interleavedStartedAt} generation=${interleavedGeneration}`r`nws client ready")
      } `
      -Sleep { param($milliseconds) Start-Sleep -Milliseconds 1 } | Out-Null
  } catch { $genericReadyRejected = $_.Exception.Message -like '*restart was not confirmed*' }
  if(-not $genericReadyRejected){ throw 'generic SDK ready log was accepted for an interleaved process' }
  Write-Host '[PASS] Generic SDK ready logs cannot satisfy the structured process-generation check'

  $oldOnlyCurrentStartedAt = [DateTimeOffset]::UtcNow.ToUnixTimeMilliseconds()
  $oldOnlyCreatedAt = [DateTimeOffset]::FromUnixTimeMilliseconds($oldOnlyCurrentStartedAt).UtcDateTime
  $oldOnlyGeneration = '1234567890abcdef1234567890abcdef'
  $currentExpectedGeneration = 'fedcba0987654321fedcba0987654321'
  $oldOnlyRejected = $false
  try {
    Wait-CcuFeishuAgentReady -AppDir $target -ExpectedGeneration $currentExpectedGeneration -TimeoutMilliseconds 25 `
      -ListProcesses { param($scriptPath) @([pscustomobject]@{ProcessId=46001;CreationDate=$oldOnlyCreatedAt}) } `
      -ReadLogTail {
        param($path,$offset)
        $oldStartedAt = $oldOnlyCurrentStartedAt - 5000
        return ("AI_RESUME_AGENT_BOOT pid=46001 startedAt=${oldStartedAt} generation=${oldOnlyGeneration}`r`nAI_RESUME_AGENT_READY pid=46001 startedAt=${oldStartedAt} generation=${oldOnlyGeneration}")
      } `
      -Sleep { param($milliseconds) Start-Sleep -Milliseconds 1 } | Out-Null
  } catch { $oldOnlyRejected = $_.Exception.Message -like '*restart was not confirmed*' }
  if(-not $oldOnlyRejected){ throw 'old generation was accepted while the current process had not written BOOT' }
  Write-Host '[PASS] A current PID without its install generation cannot reuse an old BOOT/READY pair'

  function Assert-DeploymentPlanFails([string]$Fixture,[string]$Expected){
    $failed = $false
    try { New-CcuDeploymentPlan $Fixture $target | Out-Null } catch {
      $failed = $true
      if($_.Exception.Message -notlike ('*' + $Expected + '*')){ throw }
    }
    if(-not $failed){ throw "deployment plan unexpectedly accepted: $Expected" }
  }

  $fixture = Join-Path $root 'fixture'
  Copy-Item -LiteralPath $repoSource -Destination $fixture -Recurse
  Remove-Item -LiteralPath (Join-Path $fixture 'authorization-policy.js') -Force
  Assert-DeploymentPlanFails $fixture 'authorization-policy.js'
  Write-Host '[PASS] Missing top-level runtime module fails fast'

  Remove-Item -LiteralPath $fixture -Recurse -Force
  Copy-Item -LiteralPath $repoSource -Destination $fixture -Recurse
  Remove-Item -LiteralPath (Join-Path $fixture 'ai') -Recurse -Force
  Assert-DeploymentPlanFails $fixture 'AI source directory'
  [IO.File]::WriteAllText((Join-Path $fixture 'ai'),'not-a-directory',[Text.UTF8Encoding]::new($false))
  Assert-DeploymentPlanFails $fixture 'AI source directory'
  Write-Host '[PASS] Missing or non-directory AI source fails fast'

  foreach($requiredAi in 'profiles.js','runners.js','codex-sessions.js','agent-adapter.js'){
    Remove-Item -LiteralPath $fixture -Recurse -Force
    Copy-Item -LiteralPath $repoSource -Destination $fixture -Recurse
    Remove-Item -LiteralPath (Join-Path (Join-Path $fixture 'ai') $requiredAi) -Force
    Assert-DeploymentPlanFails $fixture $requiredAi
  }
  Write-Host '[PASS] Missing required AI module fails fast'

  Remove-Item -LiteralPath $fixture -Recurse -Force
  Copy-Item -LiteralPath $repoSource -Destination $fixture -Recurse
  Remove-Item -LiteralPath (Join-Path $fixture 'task-orchestrator.js') -Force
  Assert-DeploymentPlanFails $fixture 'task-orchestrator.js'
  Write-Host '[PASS] Missing task-orchestrator.js fails fast'

  Remove-Item -LiteralPath $fixture -Recurse -Force
  Copy-Item -LiteralPath $repoSource -Destination $fixture -Recurse
  Remove-Item -LiteralPath (Join-Path $fixture 'channel-adapter.js') -Force
  Assert-DeploymentPlanFails $fixture 'channel-adapter.js'
  Write-Host '[PASS] Missing channel-adapter.js fails fast'

  Remove-Item -LiteralPath $fixture -Recurse -Force
  Copy-Item -LiteralPath $repoSource -Destination $fixture -Recurse
  Remove-Item -LiteralPath (Join-Path $fixture 'feishu-runtime.js') -Force
  Assert-DeploymentPlanFails $fixture 'feishu-runtime.js'
  Write-Host '[PASS] Missing feishu-runtime.js fails fast'

  [IO.File]::WriteAllText((Join-Path $source 'same.txt'),'same',[Text.UTF8Encoding]::new($false))
  [IO.File]::WriteAllText((Join-Path $target 'same.txt'),'same',[Text.UTF8Encoding]::new($false))
  $lock = [IO.File]::Open((Join-Path $target 'same.txt'),[IO.FileMode]::Open,[IO.FileAccess]::Read,[IO.FileShare]::Read)
  try {
    $changed = Invoke-CcuFileDeployment @([pscustomobject]@{Source=(Join-Path $source 'same.txt');Destination=(Join-Path $target 'same.txt')}) $tx
    if($changed -ne 0){ throw 'identical locked file was not skipped' }
  } finally { $lock.Dispose() }
  Write-Host '[PASS] Identical locked file is skipped by SHA256'

  foreach($name in 'a.txt','b.txt'){
    [IO.File]::WriteAllText((Join-Path $source $name),('new-' + $name),[Text.UTF8Encoding]::new($false))
    [IO.File]::WriteAllText((Join-Path $target $name),('old-' + $name),[Text.UTF8Encoding]::new($false))
  }
  $lock = [IO.File]::Open((Join-Path $target 'b.txt'),[IO.FileMode]::Open,[IO.FileAccess]::Read,[IO.FileShare]::Read)
  $failed = $false
  try {
    Invoke-CcuFileDeployment @(
      [pscustomobject]@{Source=(Join-Path $source 'a.txt');Destination=(Join-Path $target 'a.txt')},
      [pscustomobject]@{Source=(Join-Path $source 'b.txt');Destination=(Join-Path $target 'b.txt')}
    ) $tx | Out-Null
  } catch { $failed = $true }
  finally { $lock.Dispose() }
  if(-not $failed){ throw 'locked changed file did not fail deployment' }
  if((Get-Content -LiteralPath (Join-Path $target 'a.txt') -Raw) -ne 'old-a.txt'){ throw 'earlier file was not rolled back' }
  if((Get-Content -LiteralPath (Join-Path $target 'b.txt') -Raw) -ne 'old-b.txt'){ throw 'locked file changed unexpectedly' }
  Write-Host '[PASS] Partial deployment rolls earlier files back'

  [IO.File]::WriteAllText((Join-Path $source 'callback.txt'),'new-callback',[Text.UTF8Encoding]::new($false))
  [IO.File]::WriteAllText((Join-Path $target 'callback.txt'),'old-callback',[Text.UTF8Encoding]::new($false))
  $callbackFailed = $false
  try {
    Invoke-CcuFileDeployment @([pscustomobject]@{
      Source=(Join-Path $source 'callback.txt');Destination=(Join-Path $target 'callback.txt')
    }) $tx -AfterApply { throw 'synthetic process stop failure' } | Out-Null
  } catch { $callbackFailed = $_.Exception.Message -like '*synthetic process stop failure*' }
  if(-not $callbackFailed -or (Get-Content -LiteralPath (Join-Path $target 'callback.txt') -Raw) -ne 'old-callback'){
    throw 'post-deploy process stop failure did not roll deployed files back'
  }
  Write-Host '[PASS] Process-stop failure inside the deployment transaction restores the old disk state'

  $afterApplyResult = $null
  $changed = Invoke-CcuFileDeployment @(
    [pscustomobject]@{Source=(Join-Path $source 'a.txt');Destination=(Join-Path $target 'a.txt')},
    [pscustomobject]@{Source=(Join-Path $source 'b.txt');Destination=(Join-Path $target 'b.txt')}
  ) $tx -AfterApply { return 43003 } -AfterApplyResult ([ref]$afterApplyResult)
  if($changed -ne 2 -or (Get-Content -LiteralPath (Join-Path $target 'a.txt') -Raw) -ne 'new-a.txt' -or
    (Get-Content -LiteralPath (Join-Path $target 'b.txt') -Raw) -ne 'new-b.txt' -or @($afterApplyResult)[0] -ne 43003){
    throw 'unlocked changed files were not deployed'
  }
  Write-Host '[PASS] Changed files deploy after the lock is released'

  $missingSourceFailed = $false
  try {
    Invoke-CcuFileDeployment @([pscustomobject]@{
      Source=(Join-Path $source 'missing.txt');Destination=(Join-Path $target 'missing.txt')
    }) $tx | Out-Null
  } catch { $missingSourceFailed = $_.Exception.Message -like '*missing.txt*' }
  if(-not $missingSourceFailed){ throw 'transactional deployment did not fail on a missing source' }
  Write-Host '[PASS] Transactional deployment rejects a missing source'
} finally {
  if(Test-Path -LiteralPath $root){ Remove-Item -LiteralPath $root -Recurse -Force }
}
