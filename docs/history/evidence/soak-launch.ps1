# S10-O/P1 过夜浸泡宿主启动器(一次性工具,随证据入库)。
#
# 隔离三件套:
#   1. AIRESUME_TEST_PIPE_SUFFIX —— 独立 pipe 名 + 独立单实例互斥体,不抢生产 Worker;
#   2. AIRESUME_SHADOW_DIR —— 全部持久化落系统 temp 新目录,不碰 %LOCALAPPDATA%\ClaudeResumeShadow;
#   3. 产品配置 enabled/armed/continuous 全 false —— ResumeEngine 每拍空转,不起任何 AI。
#
# 本脚本只负责拉起宿主并记录 PID;采样由 soak-sampler.ps1 负责。
param(
    [string]$Suffix = 'soak8f3k',
    # 宿主从 temp 的 bin 副本跑:运行中宿主会锁住仓库 bin 里的依赖 DLL,
    # 锁着时 dotnet test 无法重 build(P2/P3 整夜都在改码重测)。
    [string]$WorkerExe = (Join-Path $env:TEMP 'ai-resume-soak-bin\AiResume.Worker.exe')
)

$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$shadow = Join-Path $env:TEMP 'ai-resume-soak-20260806'
New-Item -ItemType Directory -Force -Path $shadow | Out-Null

# 浸泡用产品配置:三个开关全关。浸泡测的是常驻开销,不是让它真去续跑。
Set-Content -Path (Join-Path $shadow 'config.json') -Encoding utf8 -Value '{"enabled":false,"armed":false,"continuous":false}'

$env:AIRESUME_SHADOW_DIR = $shadow
$env:AIRESUME_TEST_PIPE_SUFFIX = $Suffix
# GC 堆观测钩子(宿主内自报 gc-samples.csv;外部计数器对 .NET Core 不可用)。
$env:AIRESUME_TEST_GC_SAMPLE = '1'

# bin 副本不存在时从仓库拷一次(首次启动)。
if (-not (Test-Path $WorkerExe))
{
    Copy-Item (Join-Path $repo 'csharp\src\AiResume.Worker\bin\Debug\net10.0-windows') (Split-Path $WorkerExe) -Recurse -Force
}

$exe = $WorkerExe
if (-not [IO.Path]::IsPathRooted($exe)) { $exe = Join-Path $repo $WorkerExe }
$p = Start-Process -FilePath $exe -WorkingDirectory (Split-Path $exe) `
    -RedirectStandardOutput (Join-Path $shadow 'host-stdout.log') `
    -RedirectStandardError (Join-Path $shadow 'host-stderr.log') `
    -WindowStyle Hidden -PassThru

Set-Content -Path (Join-Path $shadow 'host.pid') -Value $p.Id
Set-Content -Path (Join-Path $shadow 'pipe-suffix.txt') -Value $Suffix
"SOAK_HOST_PID=$($p.Id) SHADOW=$shadow SUFFIX=$Suffix"
