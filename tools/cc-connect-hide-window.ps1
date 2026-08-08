# 把 cc-connect 计划任务改成 S4U —— 跑在非交互会话,根本不会有窗口。
#
# 为什么需要:任务动作已经是 `powershell.exe -WindowStyle Hidden ...`,
# 但该参数在**交互会话**里不生效 —— 控制台由系统在 PowerShell 处理它之前就分配好了,
# 桌面上于是留着一个黑窗口。那个窗口就是 cc-connect 本体:
# 2026-08-08 用户顺手关掉它,机器人立刻下线(CTRL_CLOSE_EVENT -> 0xC000013A),
# 同一原因至少停过三次。
#
# S4U = "Service For User":以本用户身份运行、不存密码、不要求已登录,
# 且跑在 session 0,没有桌面也就没有窗口。
#
# 必须以**管理员身份**运行:账户在 Administrators 组不够,进程本身还得是提权的。
#
# 本文件必须存成 **UTF-8 with BOM** —— Windows PowerShell 5.1 读无 BOM 的 UTF-8
# 会按 GBK 解码,中文和符号全乱,脚本直接语法错误(2026-08-08 实际发生过)。
# 因此下面一律不用 emoji,只用 ASCII 标记。

#Requires -RunAsAdministrator

$ErrorActionPreference = 'Stop'
$name = 'cc-connect'

$task = Get-ScheduledTask -TaskName $name -ErrorAction Stop
Write-Host "[改前] LogonType = $($task.Principal.LogonType)   (Interactive = 有窗口)" -ForegroundColor Cyan

# ---- Principal: S4U --------------------------------------------------
$principal = New-ScheduledTaskPrincipal `
    -UserId "$env:USERDOMAIN\$env:USERNAME" `
    -LogonType S4U `
    -RunLevel Limited          # 跑 cc-connect 不需要提权,给最小权限

# ---- settings: 逐项重申,不依赖"应该还在" -----------------------------
$s = $task.Settings
$s.ExecutionTimeLimit         = 'PT0S'      # 默认 PT72H 会把长跑进程掐掉
$s.StopIfGoingOnBatteries     = $false      # 默认 True: 拔电源就停
$s.DisallowStartIfOnBatteries = $false      # 默认 True: 电池供电时开机不启动
$s.RestartCount               = 3           # 默认 0: 崩了不会自动拉起
$s.RestartInterval            = 'PT1M'
$s.MultipleInstances          = 'IgnoreNew' # 在跑就忽略重复触发

# ---- 触发器: 登录时 + 每 5 分钟无限期重复(上游没有,停了就回不来)-----
$trigger = $task.Triggers[0]
$trigger.Repetition = (New-ScheduledTaskTrigger -Once -At (Get-Date) `
    -RepetitionInterval (New-TimeSpan -Minutes 5)).Repetition
$trigger.Repetition.Duration          = $null      # 无限期
$trigger.Repetition.StopAtDurationEnd = $false

Set-ScheduledTask -TaskName $name -Principal $principal -Trigger $trigger -Settings $s | Out-Null

# ---- 重启并复核 ------------------------------------------------------
Write-Host "[重启] 停止旧实例并重新拉起..." -ForegroundColor Cyan
try { Stop-ScheduledTask -TaskName $name } catch { }
Get-Process cc-connect -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
Start-Sleep -Seconds 4
Start-ScheduledTask -TaskName $name
Start-Sleep -Seconds 32

$t2   = Get-ScheduledTask -TaskName $name
$cc   = @(Get-CimInstance Win32_Process -Filter "Name='cc-connect.exe'")
$win  = if ($cc.Count -eq 1) {
            "$((Get-Process -Id $cc[0].ProcessId -ErrorAction SilentlyContinue).MainWindowHandle -ne 0)"
        } else { 'n/a' }
$port = try { $null = Get-NetTCPConnection -LocalPort 9820 -State Listen -ErrorAction Stop; 'True' }
        catch { 'False' }

Write-Host ""
Write-Host "[复核]" -ForegroundColor Cyan
$rows = @(
    ,@('LogonType',  "$($t2.Principal.LogonType)",            'S4U')
    ,@('任务状态',   "$($t2.State)",                          'Running')
    ,@('进程数',     "$($cc.Count)",                          '1')
    ,@('可见窗口',   $win,                                     'False')
    ,@('9820 监听',  $port,                                    'True')
    ,@('执行时限',   "$($t2.Settings.ExecutionTimeLimit)",     'PT0S')
    ,@('重复间隔',   "$($t2.Triggers[0].Repetition.Interval)", 'PT5M')
    ,@('多实例',     "$($t2.Settings.MultipleInstances)",      'IgnoreNew')
)
$bad = 0
foreach ($r in $rows) {
    $ok = ($r[1] -eq $r[2])
    if (-not $ok) { $bad++ }
    $mark = if ($ok) { '[ OK ]' } else { '[FAIL]' }
    $color = if ($ok) { 'Green' } else { 'Yellow' }
    Write-Host ("  {0} {1,-10} {2,-12} (期望 {3})" -f $mark, $r[0], $r[1], $r[2]) -ForegroundColor $color
}

Write-Host ""
Write-Host "[平台就绪] 日志尾部:" -ForegroundColor Cyan
$log = Join-Path $env:USERPROFILE '.cc-connect\logs\cc-connect.log'
if (Test-Path $log) {
    Get-Content $log -Tail 15 |
        Select-String 'platform ready' |
        ForEach-Object { '  ' + ($_.Line -replace '.*msg=', '') }
} else {
    Write-Host "  找不到日志 $log" -ForegroundColor Yellow
}

Write-Host ""
if ($bad -eq 0) {
    Write-Host "全部通过。窗口已彻底消失,机器人在后台运行。" -ForegroundColor Green
} else {
    Write-Host "$bad 项未达期望,请把上面的输出发回给我。" -ForegroundColor Yellow
}
Write-Host "提醒: 任何一次 'cc-connect daemon uninstall' + 'install' 都会把以上全部打回默认,重装后重跑本脚本。" -ForegroundColor Yellow
