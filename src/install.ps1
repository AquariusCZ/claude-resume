<#
  install.ps1 - set up AI Resume: icon, Desktop shortcut, and the Scheduled Task
  that runs the checker every 2 minutes. Safe to re-run.
#>
$ErrorActionPreference = 'Stop'
$AppDir = Join-Path $env:LOCALAPPDATA 'ClaudeResume'
. (Join-Path $PSScriptRoot 'deploy-files.ps1')

# 0) deploy the program files from src/ to the runtime folder (this IS the redeploy step)
if(-not (Test-Path $AppDir)){ New-Item -ItemType Directory -Force -Path $AppDir | Out-Null }
$requiredFeishuSdk = Get-CcuRequiredFeishuSdkVersion $PSScriptRoot
Ensure-CcuFeishuSdk -AppDir $AppDir -ExpectedVersion $requiredFeishuSdk
$deployFiles = New-CcuDeploymentPlan $PSScriptRoot $AppDir
$agentStdout = Join-Path (Join-Path $AppDir 'logs') 'feishu-stdout.log'
$agentLogOffset = if(Test-Path -LiteralPath $agentStdout -PathType Leaf){ (Get-Item -LiteralPath $agentStdout).Length } else { 0 }
$agentBootGeneration = [guid]::NewGuid().ToString('N')
$agentBootChallenge = Join-Path $AppDir 'feishu-agent.boot-challenge'
$stoppedAgentPids = $null
try {
  [IO.File]::WriteAllText($agentBootChallenge,$agentBootGeneration,[Text.UTF8Encoding]::new($false))
  [void](Invoke-CcuFileDeployment $deployFiles $AppDir -AfterApply {
    @(Stop-CcuFeishuAgentProcessTree -AppDir $AppDir)
  } -AfterApplyResult ([ref]$stoppedAgentPids))
  $stoppedAgentPids = @($stoppedAgentPids)
  if($stoppedAgentPids.Count){
    Write-Host ("  Feishu agent    : stopped and verified process tree for redeploy (" + $stoppedAgentPids.Count + ")")
    $readyPid = Wait-CcuFeishuAgentReady -AppDir $AppDir -ExpectedGeneration $agentBootGeneration `
      -LogOffset $agentLogOffset -StoppedProcessIds $stoppedAgentPids
    Write-Host ("  Feishu agent    : restarted and ready (PID " + $readyPid + ")")
  }
} finally {
  if(Test-Path -LiteralPath $agentBootChallenge -PathType Leaf){ Remove-Item -LiteralPath $agentBootChallenge -Force -ErrorAction SilentlyContinue }
}

# Install global completion hooks after every deploy. The installer merges with existing Codex,
# Claude Code, and Cline hooks and is safe to re-run.
$node = Get-Command node -ErrorAction SilentlyContinue
$hookInstaller = Join-Path $AppDir 'install-completion-hooks.js'
if($node -and (Test-Path $hookInstaller)){
  try {
    $documentsDir = [Environment]::GetFolderPath('MyDocuments')
    $hookResult = & $node.Source $hookInstaller --app-dir $AppDir --documents-dir $documentsDir
    if($LASTEXITCODE -ne 0){ throw ('completion hook installer returned non-zero: ' + $hookResult) }
    Write-Host ('  Completion hooks: ' + $hookResult)
  } catch { Write-Warning ('Completion hooks were not installed: ' + $_.Exception.Message) }
} else { Write-Warning 'Completion hooks skipped: node or installer missing' }

# 1) allow local scripts (RemoteSigned) for both PowerShell editions if needed
try { if((Get-ExecutionPolicy -Scope CurrentUser) -in @('Restricted','Undefined','AllSigned')){ Set-ExecutionPolicy -Scope CurrentUser RemoteSigned -Force } } catch {}

# 2) use the versioned multi-resolution brand icon. Keeping the ICO in source control makes
#    Desktop/taskbar rendering deterministic and avoids silently falling back to a script icon.
$IcoPath = Join-Path $AppDir 'icon.ico'
if(-not (Test-Path -LiteralPath $IcoPath)){ throw "Brand icon was not deployed: $IcoPath" }

# 3) Desktop shortcut -> wscript launcher.vbs (AV-safe, opens the GUI)
$wsh = New-Object -ComObject WScript.Shell
$lnk = Join-Path ([Environment]::GetFolderPath('Desktop')) 'AI Resume.lnk'
$sc = $wsh.CreateShortcut($lnk)
$sc.TargetPath = Join-Path $env:SystemRoot 'System32\wscript.exe'
$sc.Arguments = '"' + (Join-Path $AppDir 'launcher.vbs') + '"'
$sc.WorkingDirectory = $AppDir
$sc.IconLocation = "$IcoPath,0"
$sc.WindowStyle = 1
$sc.Description = 'AI Resume - local multi-AI project assistant and automatic resume console'
$sc.Save()
$savedShortcut = $wsh.CreateShortcut($lnk)
if(-not [string]::Equals("$($savedShortcut.IconLocation)","$IcoPath,0",[StringComparison]::OrdinalIgnoreCase)){
  throw "Desktop shortcut icon was not saved: $lnk"
}
$legacyLnk = Join-Path ([Environment]::GetFolderPath('Desktop')) 'Claude续跑.lnk'
if(Test-Path $legacyLnk){ Remove-Item $legacyLnk -Force -ErrorAction SilentlyContinue }

# 4) Scheduled Task: run the checker every 2 minutes (paths have no spaces -> no quote hell)
$tr = "wscript.exe $AppDir\checker-launch.vbs"
& schtasks /Create /F /TN 'ClaudeResumeChecker' /SC MINUTE /MO 2 /TR $tr | Out-Null
$taskSettings = New-ScheduledTaskSettingsSet -ExecutionTimeLimit ([TimeSpan]::Zero) -MultipleInstances IgnoreNew -StartWhenAvailable -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries
Set-ScheduledTask -TaskName 'ClaudeResumeChecker' -Settings $taskSettings | Out-Null

# 4b) Feishu two-way agent: start at logon via a Startup-folder shortcut (no admin needed;
#     an ONLOGON scheduled task requires elevation). The vbs auto-restarts node, so this one
#     entry keeps the long-connection listener alive. Only if credentials are configured.
. (Join-Path $AppDir 'lib.ps1')
$cfg = Get-CcuConfig
$startupLnk = Join-Path ([Environment]::GetFolderPath('Startup')) 'ClaudeResumeFeishu.lnk'
if("$($cfg.feishuAppId)" -and (Test-Path (Join-Path $AppDir 'feishu-launch.vbs'))){
  $fs = $wsh.CreateShortcut($startupLnk)
  $fs.TargetPath = Join-Path $env:SystemRoot 'System32\wscript.exe'
  $fs.Arguments = '"' + (Join-Path $AppDir 'feishu-launch.vbs') + '"'
  $fs.WorkingDirectory = $AppDir
  $fs.WindowStyle = 7
  $fs.Description = 'AI Resume - Feishu multi-AI agent (long-connection listener)'
  $fs.Save()
  Write-Host "  Feishu agent    : starts at logon (Startup shortcut), auto-restart"
} else {
  if(Test-Path $startupLnk){ Remove-Item $startupLnk -Force -ErrorAction SilentlyContinue }
  Write-Host "  Feishu agent    : skipped (set feishuAppId/feishuAppSecret in config.json, then re-run)"
}
# 5) start disarmed (the GUI's "布防" button arms it)
$installCycle=New-CcuCycleId
[void](Update-CcuConfig { param($live) $live.enabled=$false; $live.armed=$false; $live.armCycleId=$installCycle })

Write-Host "AI Resume installed." -ForegroundColor Green
Write-Host ("  Desktop shortcut: " + $lnk)
Write-Host ("  Scheduled task  : ClaudeResumeChecker (every 2 min)")
Write-Host ("  App folder      : " + $AppDir)
