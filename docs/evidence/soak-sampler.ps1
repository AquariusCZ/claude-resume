# S10-O/P1 浸泡采样器(一次性工具,随证据入库)。
#
# 每 5 分钟采样一次浸泡宿主的常驻开销并写 CSV:
#   句柄数 / 线程数 / 私有字节(按 PID 直读)+ GC 堆(.NET CLR Memory 计数器,
#   实例名歧义时记 NA)+ 三个 IPC 命令的应答类型(ping/list-runs/status)。
#
# 诚实性:status 命令需要 runId,浸泡宿主没有任何 run,故用固定假 runId 打
# 错误路径——它仍然完整走一遍 帧编解码→路由→应答 的管道层,足以暴露管道层
# 累积泄漏;「查真实 run 状态」不在浸泡范围内。
#
# 用法:pwsh -File soak-sampler.ps1(由 soak-launch.ps1 之后手动拉起,P6 停掉)。
param(
    [int]$IntervalSeconds = 300,
    [string]$Shadow = (Join-Path $env:TEMP 'ai-resume-soak-20260806')
)

$ErrorActionPreference = 'Continue'
$csv = (Join-Path $PSScriptRoot 'soak-20260806.csv')
$hostPid = [int](Get-Content (Join-Path $Shadow 'host.pid') -Raw).Trim()
$suffix = (Get-Content (Join-Path $Shadow 'pipe-suffix.txt') -Raw).Trim()

# pipe 名推导与 PipeNaming.ComputePipeName 一致:airesume-<SID SHA256 前16位>-<后缀>。
$sid = [System.Security.Principal.WindowsIdentity]::GetCurrent().User.Value
$sha = [System.Security.Cryptography.SHA256]::Create().ComputeHash([Text.Encoding]::UTF8.GetBytes($sid))
$hex = ([BitConverter]::ToString($sha) -replace '-', '').ToLowerInvariant().Substring(0, 16)
$pipeName = "airesume-$hex-$suffix"

$statusRunId = '00000000-0000-0000-0000-00000000dead'
if (-not (Test-Path $csv))
{
    Set-Content -Path $csv -Value 'time_utc,host_pid,handles,threads,private_bytes,gc_heap_bytes,ipc_ping,ipc_list_runs,ipc_status'
}

function Read-Exact([System.IO.Stream]$s, [byte[]]$buf, [int]$count)
{
    $off = 0
    while ($off -lt $count)
    {
        $n = $s.Read($buf, $off, $count - $off)
        if ($n -le 0) { throw [System.IO.IOException]::new('对端提前关闭连接') }
        $off += $n
    }
}

function Send-Ipc([string]$type, [string]$payloadJson)
{
    $corr = [Guid]::NewGuid().ToString()
    $json = '{"envelopeVersion":"1","type":"' + $type + '","correlationId":"' + $corr + '"'
    if ($payloadJson) { $json += ',"payload":' + $payloadJson }
    $json += '}'
    $client = New-Object System.IO.Pipes.NamedPipeClientStream('.', $pipeName, [System.IO.Pipes.PipeDirection]::InOut)
    try
    {
        $client.Connect(5000)
        $body = [Text.Encoding]::UTF8.GetBytes($json)
        $header = [BitConverter]::GetBytes([int32]$body.Length)
        $client.Write($header, 0, 4)
        $client.Write($body, 0, $body.Length)
        $client.Flush()
        $rh = New-Object byte[] 4
        Read-Exact $client $rh 4
        $len = [BitConverter]::ToInt32($rh, 0)
        if ($len -le 0 -or $len -gt 1048576) { return 'ERR:bad_frame_len' }
        $rb = New-Object byte[] $len
        Read-Exact $client $rb $len
        $resp = [Text.Encoding]::UTF8.GetString($rb) | ConvertFrom-Json
        if ($resp.correlationId -ne $corr) { return 'ERR:correlation_mismatch' }
        return $resp.type
    }
    catch { return ('ERR:' + $_.Exception.GetType().Name) }
    finally { $client.Dispose() }
}

while ($true)
{
    $now = (Get-Date).ToUniversalTime().ToString('yyyy-MM-ddTHH:mm:ssZ')
    try
    {
        $proc = Get-Process -Id $hostPid -ErrorAction Stop
        $handles = $proc.HandleCount
        $threads = $proc.Threads.Count
        $priv = $proc.PrivateMemorySize64
    }
    catch
    {
        Add-Content -Path $csv -Value "$now,$hostPid,GONE,GONE,GONE,GONE,GONE,GONE,GONE"
        Start-Sleep -Seconds $IntervalSeconds
        continue
    }

    # GC 堆:.NET Core 不发布性能计数器实例,改读宿主内自报的 gc-samples.csv 最后一行
    # (TestGcSampleHook,AIRESUME_TEST_GC_SAMPLE=1 门控)。钩子未写或时间差超过 10 分钟记 NA。
    $gc = 'NA'
    try
    {
        $gcFile = Join-Path $Shadow 'gc-samples.csv'
        if (Test-Path $gcFile)
        {
            $last = @(Get-Content $gcFile | Where-Object { $_ -match ',' } | Select-Object -Last 1)
            if ($last.Count -eq 1)
            {
                $parts = $last[0].Split(',')
                $ts = [DateTimeOffset]::Parse($parts[0])
                if (((Get-Date) - $ts.LocalDateTime).TotalMinutes -le 10) { $gc = $parts[1] }
            }
        }
    }
    catch { $gc = 'NA' }

    $ping = Send-Ipc 'ping' $null
    $listRuns = Send-Ipc 'list-runs' $null
    $status = Send-Ipc 'status' ('{"runId":"' + $statusRunId + '"}')

    Add-Content -Path $csv -Value "$now,$hostPid,$handles,$threads,$priv,$gc,$ping,$listRuns,$status"
    Start-Sleep -Seconds $IntervalSeconds
}
