# 把 cc-connect 计划任务改成 S4U —— 跑在非交互会话,**根本不会有窗口**。
#
# 为什么需要:任务动作已经是 `powershell.exe -WindowStyle Hidden …`,
# 但该参数在**交互会话**里不生效 —— 控制台由系统在 PowerShell 处理它之前就分配好了,
# 桌面上于是留着一个黑窗口。那个窗口就是 cc-connect 本体:
# 2026-08-08 用户顺手关掉它,机器人立刻下线(CTRL_CLOSE_EVENT → 0xC000013A),
# 同一原因至少停过三次。
#
# S4U = "Service For User":以本用户身份运行、**不存密码**、不要求已登录,
# 且跑在 session 0,没有桌面也就没有窗口。
#
# **必须以管理员身份运行**(改 Principal 需要提权;账户本身在 Administrators 组不够,
# 进程还得是提权的)。
#
# 改完会顺带重新落实全部加固项 —— 改 Principal 有可能带走其它设置,
# 而这些项每一条都是踩过坑才加的,不能靠"应该还在"。

#Requires -RunAsAdministrator

$ErrorActionPreference = 'Stop'
$name = 'cc-connect'

$task = Get-ScheduledTask -TaskName $name -ErrorAction Stop
Write-Host "改前:" -ForegroundColor Cyan
Write-Host "  LogonType = $($task.Principal.LogonType)   (Interactive = 有窗口)"

# ── Principal:S4U ─────────────────────────────────────────────
$principal = New-ScheduledTaskPrincipal `
    -UserId "$env:USERDOMAIN\$env:USERNAME" `
    -LogonType S4U `
    -RunLevel Limited          # 不需要提权跑 cc-connect,给最小权限

# ── settings:逐项重申,不依赖"应该还在" ────────────────────────
$s = $task.Settings
$s.ExecutionTimeLimit         = 'PT0S'      # 默认 PT72H 会把长跑进程掐掉
$s.StopIfGoingOnBatteries     = $false      # 默认 True:拔电源就停
$s.DisallowStartIfOnBatteries = $false      # 默认 True:电池供电时开机不启动
$s.RestartCount               = 3           # 默认 0:崩了不会自动拉起
$s.RestartInterval            = 'PT1M'
$s.MultipleInstances          = 'IgnoreNew' # 在跑就忽略重复触发

# ── 触发器:登录时 + 每 5 分钟无限期重复(上游没有,停了就回不来)──
$trigger = $task.Triggers[0]
$trigger.Repetition = (New-ScheduledTaskTrigger -Once -At (Get-Date) `
    -RepetitionInterval (New-TimeSpan -Minutes 5)).Repetition
$trigger.Repetition.Duration          = $null      # 无限期
$trigger.Repetition.StopAtDurationEnd = $false

Set-ScheduledTask -TaskName $name -Principal $principal -Trigger $trigger -Settings $s | Out-Null

# ── 重启并复核 ────────────────────────────────────────────────
Write-Host "`n重启任务…" -ForegroundColor Cyan
try { Stop-ScheduledTask -TaskName $name } catch { }
Get-Process cc-connect -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
Start-Sleep -Seconds 4
Start-ScheduledTask -TaskName $name
Start-Sleep -Seconds 32

$t2 = Get-ScheduledTask -TaskName $name
$cc = @(Get-CimInstance Win32_Process -Filter "Name='cc-connect.exe'")
$win = if ($cc.Count -eq 1) {
    (Get-Process -Id $cc[0].ProcessId -ErrorAction SilentlyContinue).MainWindowHandle -ne 0
} else { '—' }
$port = try { Get-NetTCPConnection -LocalPort 9820 -State Listen -ErrorAction Stop | Out-Null; $true } catch { $false }

Write-Host "`n复核:" -ForegroundColor Cyan
$rows = @(
    @{ 项 = 'LogonType';   值 = $t2.Principal.LogonType; 期望 = 'S4U' }
    @{ 项 = '任务状态';    值 = $t2.State;               期望 = 'Running' }
    @{ 项 = 'cc-connect';  值 = "$($cc.Count) 个进程";    期望 = '1 个进程' }
    @{ 项 = '可见窗口';    值 = $win;                     期望 = 'False' }
    @{ 项 = '9820 监听';   值 = $port;                    期望 = 'True' }
    @{ 项 = '执行时限';    值 = $t2.Settings.ExecutionTimeLimit; 期望 = 'PT0S' }
    @{ 项 = '重复间隔';    值 = $t2.Triggers[0].Repetition.Interval; 期望 = 'PT5M' }
)
foreach ($r in $rows) {
    $ok = "$($r.值)" -eq "$($r.期望)"
    $mark = if ($ok) { '✅' } else { '❌' }
    Write-Host ("  {0} {1,-12} {2,-14} (期望 {3})" -f $mark, $r.项, $r.值, $r.期望) `
        -ForegroundColor $(if ($ok) { 'Green' } else { 'Yellow' })
}

Write-Host "`n平台就绪(日志尾部):" -ForegroundColor Cyan
Get-Content "$env:USERPROFILE\.cc-connect\logs\cc-connect.log" -Tail 15 |
    Select-String 'platform ready' |
    ForEach-Object { '  ' + ($_.Line -replace '.*msg=', '') }

Write-Host "`n注意:任何一次 ``cc-connect daemon uninstall`` + ``install`` 都会把以上全部打回默认,重装后重跑本脚本。" -ForegroundColor Yellow
