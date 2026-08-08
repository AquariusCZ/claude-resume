using System.Diagnostics;
using System.Text.Json;
using AiResume.Core.Contracts;
using AiResume.Storage;
using AiResume.Worker;
using AiResume.Worker.Supervision;
using Xunit;

namespace AiResume.Tests;

/// <summary>
/// S5-D 断电真 kill 恢复验证:真实 Worker 子进程宿主 + shadow 临时目录,
/// fake probe run 运行中 Process.Kill(entireProcessTree) 硬杀(不优雅退出)→
/// WAL 可读且无损坏、run 状态恢复(无半写)、对账报告核验处置(Gone)、
/// RecoverAsync 授权清理登记、重启宿主后观察循环恢复驱动至 terminal。
/// 全程使用临时 shadow 目录,不触碰生产状态;宿主进程由测试显式回收。
/// </summary>
[Collection(SqliteCollection.Name)]
public sealed class PowerLossRecoveryTests : IDisposable
{
    private readonly List<Process> _hosts = new();

    /// <summary>
    /// 宿主 PID 清单(独立于 Process 对象):测试方法内 using 声明会在方法结束时
    /// 提前 Dispose 进程句柄,类 Dispose 再 Kill 同一对象会抛 ObjectDisposedException
    /// 被吞掉 → 宿主残留并继承 testhost 输出句柄,使 vstest 永久挂起。
    /// 改用 PID 经 GetProcessById 重新获取句柄,确保兜底杀一定生效。
    /// </summary>
    private readonly List<int> _hostPids = new();

    /// <summary>
    /// 本测试类实例专属的 Named Pipe 后缀,隔离于生产 Worker(见 StartHost 的说明)。
    /// xUnit 为每个测试方法新建一次类实例,所以并行的用例之间也天然互不冲突。
    /// 只取 GUID 前 16 位:后缀限定字母数字,且要给互斥体名留长度余量。
    /// </summary>
    private readonly string _pipeSuffix = Guid.NewGuid().ToString("N")[..16];

    public void Dispose()
    {
        foreach (int pid in _hostPids)
        {
            try
            {
                using Process p = Process.GetProcessById(pid);
                p.Kill(entireProcessTree: true);
                p.WaitForExit(10_000);
            }
            catch
            {
                // 进程已退出或句柄不可得:不掩盖断言结果。
            }
        }

        foreach (Process host in _hosts)
        {
            try
            {
                host.Dispose();
            }
            catch
            {
                // 已释放的对象忽略。
            }
        }
    }

    // ---- 工具 ----

    /// <summary>定位 Worker 宿主:优先 apphost exe(项目引用通常不复制),回退 dotnet + dll。</summary>
    private static (string FileName, string Arguments) ResolveWorkerHost()
    {
        string dir = Path.GetDirectoryName(typeof(ShadowPaths).Assembly.Location)!;
        string exe = Path.Combine(dir, "AiResume.Worker.exe");
        if (File.Exists(exe))
        {
            return (exe, string.Empty);
        }

        string dll = Path.Combine(dir, "AiResume.Worker.dll");
        if (!File.Exists(dll))
        {
            throw new FileNotFoundException("找不到 Worker 宿主程序集(测试必须先构建 Worker)。", dll);
        }

        return ("dotnet", $"\"{dll}\"");
    }

    private Process StartHost(string shadowDir, string extraArgs = "")
    {
        var (fileName, arguments) = ResolveWorkerHost();
        string fullArgs = (arguments + " " + extraArgs).Trim();
        var psi = new ProcessStartInfo(fileName, fullArgs)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = shadowDir,
            // 重定向宿主输出并异步消费:否则宿主继承测试主机 stdout 句柄,
            // vstest 等待管道 EOF 会挂起(实测踩坑)。
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        psi.Environment["AIRESUME_SHADOW_DIR"] = shadowDir;
        psi.Environment["AIRESUME_TEST_AUTO_PROBE"] = "1";
        // Named Pipe 名派生出单实例互斥体,不隔离的话本测试拉起的宿主会和
        // **本机正在跑的生产 Worker**(开机自启)抢同一把锁而起不来,
        // 表现为 WaitForRunningRunAsync 超时 30 秒(2026-08-06 实测)。
        // 每个测试类实例一个后缀:同一用例内重启宿主(如断电恢复)要复用同一个名字,
        // 否则"重启后仍是同一实例"这个语义就被测试自己破坏了。
        psi.Environment[AiResume.Ipc.PipeNaming.TestSuffixEnvName] = _pipeSuffix;

        Process host = Process.Start(psi) ?? throw new InvalidOperationException("Worker 宿主启动失败。");
        host.OutputDataReceived += (_, _) => { };
        host.ErrorDataReceived += (_, _) => { };
        host.BeginOutputReadLine();
        host.BeginErrorReadLine();
        _hosts.Add(host);
        _hostPids.Add(host.Id);
        return host;
    }

    /// <summary>轮询 shadow 数据库直到出现完整登记的 running run(钩子驱动)。</summary>
    private static async Task<(string RunId, int ChildPid)> WaitForRunningRunAsync(string dbPath, TimeSpan timeout)
    {
        DateTime deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (File.Exists(dbPath))
            {
                try
                {
                    using var connection = StorageDatabase.Open(dbPath);
                    using var cmd = connection.CreateCommand();
                    cmd.CommandText = """
                        SELECT r.run_id, r.state, p.child_pid
                        FROM runs r LEFT JOIN process_registry p ON p.run_id = r.run_id
                        ORDER BY r.created_at;
                        """;
                    using var reader = cmd.ExecuteReader();
                    while (reader.Read())
                    {
                        string state = reader.GetString(1);
                        bool pidKnown = !reader.IsDBNull(2);
                        if (state == "running" && pidKnown)
                        {
                            return (reader.GetString(0), reader.GetInt32(2));
                        }
                    }
                }
                catch
                {
                    // 宿主可能仍在迁移/写库,重试。
                }
            }

            await Task.Delay(250);
        }

        throw new TimeoutException($"等待 fake probe run 进入 running 超时({timeout.TotalSeconds}s)。");
    }

    /// <summary>轮询 runs 表直到 run 进入 terminal(重启宿主后由观察循环恢复驱动)。</summary>
    private static async Task WaitForTerminalAsync(string dbPath, string runId, TimeSpan timeout)
    {
        DateTime deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                using var connection = StorageDatabase.Open(dbPath);
                using var cmd = connection.CreateCommand();
                cmd.CommandText = "SELECT state FROM runs WHERE run_id = $rid;";
                cmd.Parameters.AddWithValue("$rid", runId);
                string? state = (string?)cmd.ExecuteScalar();
                if (state is "succeeded" or "failed_provider" or "failed_local" or "cancelled")
                {
                    Assert.Equal("succeeded", state); // 骨架级:假 provider 干净退出 = 成功。
                    return;
                }
            }
            catch
            {
                // 宿主写库中,重试。
            }

            await Task.Delay(500);
        }

        throw new TimeoutException($"等待 run {runId} 恢复驱动至 terminal 超时({timeout.TotalSeconds}s)。");
    }

    private static string NewShadowDir()
    {
        string dir = Path.Combine(Path.GetTempPath(), "s5d-powerloss-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void CleanupShadowDir(string shadowDir)
    {
        try
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            Directory.Delete(shadowDir, recursive: true);
        }
        catch
        {
            // 清理失败不掩盖断言结果。
        }
    }

    // ---- 测试 ----

    [Fact]
    public async Task Auto_probe_hook_starts_run_and_logs_structured_marker()
    {
        string shadowDir = NewShadowDir();
        try
        {
            using Process host = StartHost(shadowDir);
            string dbPath = Path.Combine(shadowDir, "runs.db");
            (string runId, int childPid) = await WaitForRunningRunAsync(dbPath, TimeSpan.FromSeconds(30));

            Assert.True(childPid > 0);

            // 钩子必须打结构化日志标记(shadow 日志目录;宿主启动后日志文件写入有延迟,轮询等待)。
            // 注意:DailyJsonFileLoggerProvider 按日滚动文件名是 worker-yyyyMMdd.log(扩展名 .log)。
            string logsDir = Path.Combine(shadowDir, "logs");
            bool markerFound = false;
            DateTime markerDeadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
            while (DateTime.UtcNow < markerDeadline && !markerFound)
            {
                if (Directory.Exists(logsDir))
                {
                    markerFound = Directory.EnumerateFiles(logsDir, "*.log", SearchOption.AllDirectories)
                        .SelectMany(path =>
                        {
                            try
                            {
                                return new[] { File.ReadAllText(path) };
                            }
                            catch
                            {
                                return Array.Empty<string>();
                            }
                        })
                        .Any(text => text.Contains("test.auto.probe.started", StringComparison.Ordinal) &&
                                     text.Contains(runId, StringComparison.Ordinal));
                }

                if (!markerFound)
                {
                    await Task.Delay(250);
                }
            }

            Assert.True(markerFound, "日志必须包含 test.auto.probe.started 且带 runId。");

            // 钩子 run 的 runKey 必须为规范形(对账器视角)。
            ReconcileReport report = new Reconciler(dbPath).Reconcile();
            ReconcileRunItem item = Assert.Single(report.Runs);
            Assert.True(item.RunKeyCanonical, "钩子生成的 runKey 必须规范。");
        }
        finally
        {
            CleanupShadowDir(shadowDir);
        }
    }

    [Fact]
    public async Task Kill_mid_run_recovers_cleanly_with_reconcile_and_restart()
    {
        string shadowDir = NewShadowDir();
        try
        {
            // 1) 宿主启动,fake probe run 进入 running(登记完整)。
            using Process host = StartHost(shadowDir);
            string dbPath = Path.Combine(shadowDir, "runs.db");
            (string runId, int childPid) = await WaitForRunningRunAsync(dbPath, TimeSpan.FromSeconds(30));

            // 2) 断电模拟:整树硬杀,不优雅退出。
            host.Kill(entireProcessTree: true);
            Assert.True(host.WaitForExit(15_000), "宿主进程应在 15 秒内退出。");
            host.Dispose();
            _hosts.Remove(host);

            // 3) WAL 可读、无损坏;run 状态恢复(断电前最后持久状态),无半写。
            using (var connection = StorageDatabase.Open(dbPath))
            {
                using var integrity = connection.CreateCommand();
                integrity.CommandText = "PRAGMA integrity_check;";
                Assert.True((string)integrity.ExecuteScalar()! == "ok", "断电后 WAL 必须可读且无损坏。");

                using (var cmd = connection.CreateCommand())
                {
                    cmd.CommandText = "SELECT state, state_version, seq FROM runs WHERE run_id = $rid;";
                    cmd.Parameters.AddWithValue("$rid", runId);
                    using var reader = cmd.ExecuteReader();
                    Assert.True(reader.Read(), "断电后 run 必须仍在 runs 表。");
                    Assert.Equal("running", reader.GetString(0));
                    long stateVersion = reader.GetInt64(1);
                    long seq = reader.GetInt64(2);
                    Assert.True(stateVersion >= 3, $"状态版本无半写:期望 ≥3(queued/starting/running),实际 {stateVersion}。");
                    Assert.True(seq >= 2, $"事件 seq 必须连续推进:期望 ≥2(queued→starting→running 两次推进),实际 {seq}。");
                }

                using (var cmd = connection.CreateCommand())
                {
                    cmd.CommandText = "SELECT COUNT(*) FROM run_events WHERE run_id = $rid AND envelope_json LIKE '%run.started%';";
                    cmd.Parameters.AddWithValue("$rid", runId);
                    Assert.True((long)cmd.ExecuteScalar()! == 1L, "run.started 事件必须已落盘。");
                }

                using (var cmd = connection.CreateCommand())
                {
                    cmd.CommandText = "SELECT COUNT(*) FROM runs;";
                    Assert.True((long)cmd.ExecuteScalar()! == 1L, "断电不产生半写行。");
                }
            }

            // 4) Job kill-on-close/自然退出:断电后子进程已消失。
            Assert.Equal(ProcessLiveness.Gone, new NativeProcessProbe().Probe(childPid).Liveness);

            // 5) 对账报告:run running + 登记在 + 进程 gone → attention + Gone(待恢复处置)。
            ReconcileReport report = new Reconciler(dbPath).Reconcile();
            Assert.Equal("attention", report.Status);
            ReconcileRunItem item = Assert.Single(report.Runs);
            Assert.Equal(ReconcileVerdict.Gone, item.Verdict);
            Assert.False(item.ProcessAlive);
            Assert.True(item.RunKeyCanonical);
            Assert.Empty(report.Orphans);
            Assert.True(report.ToJson().Contains("\"status\":\"attention\"", StringComparison.Ordinal),
                "对账报告 JSON 输出必须可解析且形状正确。");

            // 6) 恢复流程授权清理 Gone 登记。
            using (var supervisor = new ProcessSupervisor(dbPath))
            {
                RecoveryReport recovery = await supervisor.RecoverAsync(CancellationToken.None);
                RecoveryReportItem recoveryItem = Assert.Single(recovery.Items);
                Assert.Equal(ProcessVerdict.Gone, recoveryItem.Verdict);
                Assert.Equal(RecoveryAction.RemoveRegistry, recoveryItem.Action);
            }

            using (var connection = StorageDatabase.Open(dbPath))
            using (var cmd = connection.CreateCommand())
            {
                cmd.CommandText = "SELECT COUNT(*) FROM process_registry;";
                Assert.True((long)cmd.ExecuteScalar()! == 0L, "恢复后登记必须清理。");
            }

            // 7) 重启宿主:观察循环(15 秒)兜底推进 run 至 terminal,最终对账一致。
            using Process restarted = StartHost(shadowDir, "--Observation:IntervalSeconds=00:00:15");
            await WaitForTerminalAsync(dbPath, runId, TimeSpan.FromSeconds(90));

            ReconcileReport final = new Reconciler(dbPath).Reconcile();
            Assert.Equal("consistent", final.Status);
            Assert.Equal(0, final.ActiveRunCount);
            Assert.Equal(0, final.OrphanRegistryCount);
            Assert.Equal(0, final.RunKeyInvalidCount);

            // 8) 对账报告 JSON 落盘形状(Stage 9 对账演练直接复用)。
            using var doc = JsonDocument.Parse(final.ToJson());
            Assert.Equal("consistent", doc.RootElement.GetProperty("status").GetString());
        }
        finally
        {
            CleanupShadowDir(shadowDir);
        }
    }
}
