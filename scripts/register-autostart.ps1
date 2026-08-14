<#
.SYNOPSIS
    把 AI Resume 续跑引擎的开机自启升级成 S4U 计划任务(需要管理员)。

.DESCRIPTION
    默认安装用的是「Startup 快捷方式 → AiResume.Launcher.exe → Worker」这条零权限链路,
    已经能做到开机无窗口。本脚本是**可选升级**,多给两样东西:

      1. 进程不在交互桌面上跑(S4U),彻底没有可被误关的窗口;
      2. **失败自动重启**(3 次 / 每分钟) —— 快捷方式给不了这个。

    为什么要提权:非提权进程注册计划任务会得到 0x80070005 拒绝访问
    (2026-08-13 实测:根路径/子目录 × S4U/Interactive 四种组合全部被拒)。
    install 本身是非提权跑的,不适合为一个自启入口去弹 UAC,所以拆成这个脚本。

    脚本是幂等的:重复运行结果一致,不会留下第二个同名任务。
    注册成功后会删掉 Startup 里的快捷方式,避免登录时两条链路各拉起一个 Worker,
    抢同一份 SQLite 与 Named Pipe。

.PARAMETER InstallDir
    AI Resume 安装目录。默认 %LOCALAPPDATA%\AI Resume。

.PARAMETER Revert
    撤销:删除计划任务并恢复 Startup 快捷方式(指向 Launcher,仍然无窗口)。

.EXAMPLE
    # 在「以管理员身份运行」的 PowerShell 里:
    powershell -ExecutionPolicy Bypass -File .\scripts\register-autostart.ps1

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File .\scripts\register-autostart.ps1 -Revert
#>
[CmdletBinding()]
param(
    [string]$InstallDir = (Join-Path $env:LOCALAPPDATA 'AI Resume'),
    [switch]$Revert
)

$ErrorActionPreference = 'Stop'
$TaskName    = 'AI Resume 续跑引擎'
$LinkName    = 'AI Resume 续跑引擎.lnk'
$StartupDir  = Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs\Startup'
$StartupLink = Join-Path $StartupDir $LinkName

function Assert-Elevated {
    $isAdmin = ([Security.Principal.WindowsPrincipal] `
        [Security.Principal.WindowsIdentity]::GetCurrent()
        ).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
    if (-not $isAdmin) {
        Write-Error @'
需要管理员权限。请在「以管理员身份运行」的 PowerShell 窗口里重跑本脚本。
不提权时 Register-ScheduledTask 会返回 0x80070005 拒绝访问。
'@
        exit 1
    }
}

function New-StartupShortcut([string]$Target, [string]$WorkDir, [string]$IconPath) {
    $shell = New-Object -ComObject WScript.Shell
    $lnk = $shell.CreateShortcut($StartupLink)
    $lnk.TargetPath       = $Target
    $lnk.WorkingDirectory = $WorkDir
    if (Test-Path $IconPath) { $lnk.IconLocation = $IconPath }
    $lnk.Description      = 'AI Resume 续跑引擎(后台):限额恢复后按队列顺序继续'
    $lnk.Save()
}

Assert-Elevated

$worker   = Join-Path $InstallDir 'AiResume.Worker.exe'
$launcher = Join-Path $InstallDir 'AiResume.Launcher.exe'
$icon     = Join-Path $InstallDir 'icon.ico'

if ($Revert) {
    Unregister-ScheduledTask -TaskName $TaskName -Confirm:$false -ErrorAction SilentlyContinue
    Write-Host "已删除计划任务:$TaskName"
    # 撤销后必须把快捷方式加回来,否则开机就没有任何东西会拉起续跑引擎。
    if (-not (Test-Path $launcher)) {
        Write-Error "找不到 $launcher,无法恢复开机自启。请先运行 AiResume.Worker.exe install。"
        exit 1
    }
    New-StartupShortcut -Target $launcher -WorkDir $InstallDir -IconPath $icon
    Write-Host "已恢复开机快捷方式(经启动器,仍然无窗口):$StartupLink"
    exit 0
}

if (-not (Test-Path $worker)) {
    Write-Error "找不到 $worker。请先运行 AiResume.Worker.exe install。"
    exit 1
}

# 账号一律用当前用户 SID:显示名可能带域、可能被本地化,SID 不会。
$sid = ([Security.Principal.WindowsIdentity]::GetCurrent()).User.Value

$action    = New-ScheduledTaskAction -Execute $worker -WorkingDirectory $InstallDir
$trigger   = New-ScheduledTaskTrigger -AtLogOn
$principal = New-ScheduledTaskPrincipal -Id 'Author' -UserId $sid `
                -LogonType S4U -RunLevel Limited
$settings  = New-ScheduledTaskSettingsSet `
                -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -StartWhenAvailable `
                -ExecutionTimeLimit ([TimeSpan]::Zero) -MultipleInstances IgnoreNew `
                -RestartCount 3 -RestartInterval (New-TimeSpan -Minutes 1)

# 先注销再注册 = 幂等:重装、换安装路径、旧任务参数不对,结果都收敛到同一份定义。
Unregister-ScheduledTask -TaskName $TaskName -Confirm:$false -ErrorAction SilentlyContinue
Register-ScheduledTask -TaskName $TaskName -TaskPath '\' -Action $action `
    -Trigger $trigger -Principal $principal -Settings $settings | Out-Null

# **注册成功 ≠ 装对了。** 组策略或既有同名任务会让实际定义和请求的不一样,
# 而 Register-ScheduledTask 照样返回成功。所以必须读回来逐项核验。
$t = @(Get-ScheduledTask -TaskName $TaskName -ErrorAction SilentlyContinue)
if ($t.Count -ne 1) { Write-Error "读回失败:同名任务有 $($t.Count) 个,期望 1 个。"; exit 2 }
$t = $t[0]
$a = @($t.Actions)

$taskSid = (New-Object Security.Principal.NTAccount($t.Principal.UserId)
           ).Translate([Security.Principal.SecurityIdentifier]).Value

$checks = [ordered]@{
    '根路径唯一'      = ($t.TaskPath -eq '\')
    '单个 action'     = ($a.Count -eq 1)
    'action 指向 Worker' = ($a[0].Execute -eq $worker)
    '账号=当前用户'   = ($taskSid -eq $sid)
    'S4U'             = ($t.Principal.LogonType.ToString() -eq 'S4U')
    'Limited'         = ($t.Principal.RunLevel.ToString() -eq 'Limited')
    '无执行时限'      = ($t.Settings.ExecutionTimeLimit -eq 'PT0S')
    '电池不阻止启动'  = (-not $t.Settings.DisallowStartIfOnBatteries)
    '电池不停止运行'  = (-not $t.Settings.StopIfGoingOnBatteries)
    'IgnoreNew'       = ($t.Settings.MultipleInstances.ToString() -eq 'IgnoreNew')
    '登录触发器唯一'  = (@($t.Triggers).Count -eq 1)
}

$failed = @($checks.GetEnumerator() | Where-Object { -not $_.Value })
foreach ($c in $checks.GetEnumerator()) {
    Write-Host ("  [{0}] {1}" -f $(if ($c.Value) { 'OK' } else { '!!' }), $c.Key)
}
if ($failed.Count -gt 0) {
    Write-Error "计划任务已注册,但 $($failed.Count) 项核验未通过,不能认为自启已就绪。"
    exit 2
}

Write-Host "计划任务已注册并核验通过:$TaskName"

# 两条链路只能留一条,否则登录时会各拉起一个 Worker。
if (Test-Path $StartupLink) {
    Remove-Item $StartupLink -Force
    Write-Host "已移除 Startup 快捷方式(自启改由计划任务负责):$StartupLink"
}

Write-Host ''
Write-Host '完成。下次登录时续跑引擎将由计划任务启动:无窗口,失败自动重启 3 次(每分钟一次)。'
Write-Host '注意:此后重新运行 install 会再创建 Startup 快捷方式,届时请重跑本脚本。'
