using System.Diagnostics;
using AiResume.Core.Contracts;
using AiResume.Worker.Supervision;
using AiResume.Wrapper;
using Xunit;

namespace AiResume.Tests;

/// <summary>
/// S6-A cc-connect wrapper 进程监督与配置生成回归:
/// 全部用假进程(powershell sleep)驱动,不真跑 AI、不连任何飞书应用;
/// 配置/日志落系统 temp,仓库零写入;凭据用显著假值(fake-app-secret)。
/// </summary>
public sealed class CcConnectSupervisorTests : IDisposable
{
    private readonly List<CcConnectSupervisor> _supervisors = new();
    private readonly List<string> _dirs = new();

    public void Dispose()
    {
        foreach (CcConnectSupervisor supervisor in _supervisors)
        {
            try
            {
                supervisor.Dispose();
            }
            catch
            {
                // 清理失败不掩盖断言结果。
            }
        }

        foreach (string dir in _dirs)
        {
            try
            {
                Directory.Delete(dir, recursive: true);
            }
            catch
            {
                // 清理失败不掩盖断言结果。
            }
        }
    }

    // ---- 工具 ----

    private string NewDir()
    {
        string dir = Path.Combine(Path.GetTempPath(), "s6a-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        _dirs.Add(dir);
        return dir;
    }

    private static CcConnectConfig SampleConfig() => new(
        Projects: new List<CcConnectProject>
        {
            new("pilot", "claudecode", @"C:\temp\pilot"),
        },
        Feishu: new CcConnectPlatformOptions("cli_fake_test_app", "fake-app-secret-value", "ou_test_owner"));

    /// <summary>长驻假 cc-connect(30 秒);输出经 stdout 供日志断言。</summary>
    private static CcConnectSupervisorOptions FakeLongHostOptions(string dir) => new()
    {
        ExecutablePath = "powershell.exe",
        ConfigPath = Path.Combine(dir, "config.toml"),
        LogPath = Path.Combine(dir, "cc-connect.log"),
        ArgumentsBuilder = _ => "-NoProfile -Command \"Write-Output fake-cc-connect-started; Start-Sleep -Seconds 30\"",
    };

    /// <summary>短命假 cc-connect(约 1 秒后自然退出,模拟崩溃)。</summary>
    private static CcConnectSupervisorOptions FakeShortHostOptions(string dir) => new()
    {
        ExecutablePath = "powershell.exe",
        ConfigPath = Path.Combine(dir, "config.toml"),
        LogPath = Path.Combine(dir, "cc-connect.log"),
        ArgumentsBuilder = _ => "-NoProfile -Command \"Start-Sleep -Milliseconds 800\"",
    };

    /// <summary>可注入的假进程枚举器,用于单消费者守卫用例。</summary>
    private sealed class StubLister : IRunningProcessLister
    {
        private readonly RunningProcessInfo[] _procs;

        public StubLister(params RunningProcessInfo[] procs) => _procs = procs;

        public bool ProvidesCommandLine { get; init; } = true;

        public IReadOnlyList<RunningProcessInfo> List() => _procs;
    }

    /// <summary>
    /// 单消费者铁律:配置里声明了飞书平台、而本机仍有现役 node agent 在跑时,
    /// StartAsync 必须**在 spawn 之前**拒绝。启动后再发现冲突,消息已经开始被随机截走了。
    /// </summary>
    [Fact]
    public async Task Start_refuses_when_legacy_feishu_consumer_is_running()
    {
        string dir = NewDir();
        CcConnectSupervisorOptions baseOptions = FakeLongHostOptions(dir);
        // 配置里必须真的含 feishu 字样,守卫才会启动核验。
        await File.WriteAllTextAsync(baseOptions.ConfigPath, "[[projects.platforms]]\n  type = \"feishu\"\n");

        var options = new CcConnectSupervisorOptions
        {
            ExecutablePath = baseOptions.ExecutablePath,
            ConfigPath = baseOptions.ConfigPath,
            LogPath = baseOptions.LogPath,
            ArgumentsBuilder = baseOptions.ArgumentsBuilder,
            ConsumerGuard = new SingleConsumerGuard(
                new StubLister(new RunningProcessInfo(4321, "node.exe", @"node C:\x\feishu-agent.js")),
                selfPid: 1),
        };
        CcConnectSupervisor supervisor = NewSupervisor(options);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => supervisor.StartAsync());

        Assert.Contains("单消费者", ex.Message);
        Assert.Contains("legacy-node-agent", ex.Message);
        Assert.Equal(CcConnectState.NotStarted, supervisor.State);
        // 拒绝必须发生在 spawn 之前:没有任何进程被启动。
        Assert.Null(supervisor.ProcessId);
    }

    /// <summary>
    /// 枚举器读不到命令行时无法排除现役 agent,必须判无法核验并拒绝——不得放行。
    /// </summary>
    [Fact]
    public async Task Start_refuses_when_consumer_check_is_unverifiable()
    {
        string dir = NewDir();
        CcConnectSupervisorOptions baseOptions = FakeLongHostOptions(dir);
        await File.WriteAllTextAsync(baseOptions.ConfigPath, "[[projects.platforms]]\n  type = \"feishu\"\n");

        var options = new CcConnectSupervisorOptions
        {
            ExecutablePath = baseOptions.ExecutablePath,
            ConfigPath = baseOptions.ConfigPath,
            LogPath = baseOptions.LogPath,
            ArgumentsBuilder = baseOptions.ArgumentsBuilder,
            ConsumerGuard = new SingleConsumerGuard(
                new StubLister() { ProvidesCommandLine = false }, selfPid: 1),
        };
        CcConnectSupervisor supervisor = NewSupervisor(options);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => supervisor.StartAsync());

        Assert.Contains("Unverifiable", ex.Message);
        Assert.Equal(CcConnectState.NotStarted, supervisor.State);
    }

    /// <summary>配置未声明飞书平台(bridge-only 冒烟)时守卫直接放行,不阻塞启动。</summary>
    [Fact]
    public async Task Start_allowed_when_config_declares_no_feishu_platform()
    {
        string dir = NewDir();
        CcConnectSupervisorOptions baseOptions = FakeLongHostOptions(dir);
        await File.WriteAllTextAsync(baseOptions.ConfigPath, "[[projects]]\n  name = \"pilot\"\n");

        var options = new CcConnectSupervisorOptions
        {
            ExecutablePath = baseOptions.ExecutablePath,
            ConfigPath = baseOptions.ConfigPath,
            LogPath = baseOptions.LogPath,
            ArgumentsBuilder = baseOptions.ArgumentsBuilder,
            // 即便枚举器看不到命令行,未声明飞书平台也应放行(不做无谓核验)。
            ConsumerGuard = new SingleConsumerGuard(
                new StubLister() { ProvidesCommandLine = false }, selfPid: 1),
        };
        CcConnectSupervisor supervisor = NewSupervisor(options);

        await supervisor.StartAsync();

        Assert.Equal(CcConnectState.Running, supervisor.State);
        await supervisor.StopAsync(TimeSpan.FromSeconds(10));
    }

    private CcConnectSupervisor NewSupervisor(CcConnectSupervisorOptions options, Action<CcConnectState>? onStateChanged = null)
    {
        var supervisor = new CcConnectSupervisor(options, onStateChanged);
        _supervisors.Add(supervisor);
        return supervisor;
    }

    private static async Task WaitForStateAsync(CcConnectSupervisor supervisor, CcConnectState expected, TimeSpan timeout)
    {
        DateTime deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (supervisor.State == expected)
            {
                return;
            }

            await Task.Delay(100);
        }

        Assert.Fail($"等待状态 {expected} 超时,当前 {supervisor.State}。");
    }

    // ---- 配置生成 ----

    [Fact]
    public void Config_render_is_deterministic_and_shape_complete()
    {
        CcConnectConfig config = SampleConfig();
        string first = CcConnectConfigGenerator.Render(config);
        string second = CcConnectConfigGenerator.Render(config);
        Assert.Equal(first, second); // 确定性:相同输入 → 相同字节。

        // 形状必须与 cc-connect 的解析器一致:agent 是**表**,work_dir/mode 在
        // projects.agent.options 里。原断言写的是 `agent = "claudecode"` 这种顶层标量,
        // 那是我们自己臆想的格式——2026-08-06 生产切换时 cc-connect 直接拒绝加载:
        //   type mismatch for config.AgentConfig: expected table but found string。
        // 断言从此照抄 cc-connect 自带模板的形状,不再照抄我们的实现。
        Assert.Contains("[[projects]]", first, StringComparison.Ordinal);
        Assert.Contains("name = \"pilot\"", first, StringComparison.Ordinal);
        Assert.Contains("[projects.agent]", first, StringComparison.Ordinal);
        Assert.Contains("type = \"claudecode\"", first, StringComparison.Ordinal);
        Assert.Contains("[projects.agent.options]", first, StringComparison.Ordinal);
        Assert.Contains("work_dir = ", first, StringComparison.Ordinal);
        Assert.Contains("mode = ", first, StringComparison.Ordinal);
        Assert.Contains("[[projects.platforms]]", first, StringComparison.Ordinal);
        Assert.Contains("type = \"feishu\"", first, StringComparison.Ordinal);
        Assert.Contains("[projects.platforms.options]", first, StringComparison.Ordinal);
        Assert.Contains("app_id = \"cli_fake_test_app\"", first, StringComparison.Ordinal);
        // 反向断言:顶层标量形式一旦回归,这里立刻红。
        Assert.DoesNotContain("\nagent = ", first, StringComparison.Ordinal);
    }

    [Fact]
    public void Config_render_sanitized_never_contains_secret()
    {
        CcConnectConfig config = SampleConfig();
        string sanitized = CcConnectConfigGenerator.RenderSanitized(config);
        Assert.Contains("[REDACTED]", sanitized, StringComparison.Ordinal);
        Assert.DoesNotContain("fake-app-secret-value", sanitized, StringComparison.Ordinal);
        Assert.DoesNotContain("fake-app-secret-value", CcConnectConfigGenerator.RenderSanitized(config), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Config_write_is_atomic_and_leaves_no_tmp()
    {
        string dir = NewDir();
        string path = Path.Combine(dir, "config.toml");
        CcConnectConfigGenerator.Write(path, SampleConfig());
        Assert.Equal(CcConnectConfigGenerator.Render(SampleConfig()), File.ReadAllText(path));
        Assert.Empty(Directory.EnumerateFiles(dir, "*.tmp-*"));
    }

    [Fact]
    public void Config_validate_rejects_empty_secret_or_no_projects()
    {
        Assert.Throws<ArgumentException>(() => CcConnectConfig.Validate(new CcConnectConfig(
            Projects: new List<CcConnectProject> { new("p", "claudecode", "w") },
            Feishu: new CcConnectPlatformOptions("app", " ", "ou_test_owner"))));
        Assert.Throws<ArgumentException>(() => CcConnectConfig.Validate(new CcConnectConfig(
            Projects: new List<CcConnectProject>(),
            Feishu: new CcConnectPlatformOptions("app", "secret", "ou_test_owner"))));
    }

    [Fact]
    public void Config_toml_escapes_backslash_and_quote()
    {
        var config = new CcConnectConfig(
            Projects: new List<CcConnectProject> { new("p\"x", "claudecode", @"C:\a\b") },
            Feishu: new CcConnectPlatformOptions("app", "secret", "ou_test_owner"));
        string text = CcConnectConfigGenerator.Render(config);
        Assert.Contains("name = \"p\\\"x\"", text, StringComparison.Ordinal);
        Assert.Contains("work_dir = \"C:\\\\a\\\\b\"", text, StringComparison.Ordinal);
    }

    // ---- 进程监督 ----

    [Fact]
    public async Task Supervisor_starts_fake_process_and_rejects_double_start()
    {
        string dir = NewDir();
        CcConnectConfigGenerator.Write(Path.Combine(dir, "config.toml"), SampleConfig());
        CcConnectSupervisor supervisor = NewSupervisor(FakeLongHostOptions(dir));

        await supervisor.StartAsync();
        Assert.Equal(CcConnectState.Running, supervisor.State);
        Assert.True(supervisor.ProcessId > 0);

        await Assert.ThrowsAsync<InvalidOperationException>(() => supervisor.StartAsync());
    }

    [Fact]
    public async Task Supervisor_start_fails_fast_when_config_missing()
    {
        string dir = NewDir();
        CcConnectSupervisor supervisor = NewSupervisor(FakeLongHostOptions(dir)); // 未写 config.toml
        await Assert.ThrowsAsync<FileNotFoundException>(() => supervisor.StartAsync());
        Assert.Equal(CcConnectState.NotStarted, supervisor.State);
    }

    [Fact]
    public async Task Supervisor_stop_kills_process_and_pid_gone()
    {
        string dir = NewDir();
        CcConnectConfigGenerator.Write(Path.Combine(dir, "config.toml"), SampleConfig());
        CcConnectSupervisor supervisor = NewSupervisor(FakeLongHostOptions(dir));

        await supervisor.StartAsync();
        int pid = supervisor.ProcessId!.Value;
        await supervisor.StopAsync(TimeSpan.FromSeconds(15));

        Assert.Equal(CcConnectState.Stopped, supervisor.State);
        Assert.Null(supervisor.ProcessId);
        Assert.Equal(ProcessLiveness.Gone, new NativeProcessProbe().Probe(pid).Liveness);
    }

    [Fact]
    public async Task Supervisor_log_captures_redirected_output()
    {
        string dir = NewDir();
        CcConnectConfigGenerator.Write(Path.Combine(dir, "config.toml"), SampleConfig());
        CcConnectSupervisorOptions options = FakeLongHostOptions(dir);
        CcConnectSupervisor supervisor = NewSupervisor(options);

        await supervisor.StartAsync();
        DateTime deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        bool found = false;
        while (DateTime.UtcNow < deadline && !found)
        {
            try
            {
                if (File.Exists(options.LogPath))
                {
                    // 写入器持 Write 句柄:读取必须声明 ReadWrite 共享,否则 IOException。
                    using var fs = new FileStream(options.LogPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                    using var reader = new StreamReader(fs);
                    found = reader.ReadToEnd().Contains("fake-cc-connect-started", StringComparison.Ordinal);
                }
            }
            catch (IOException)
            {
                // 并发写入瞬间窗口:重试即可。
            }

            if (!found)
            {
                await Task.Delay(200);
            }
        }

        Assert.True(found, "假进程 stdout 必须经重定向落入仓库外日志。");
    }

    [Fact]
    public async Task Supervisor_unexpected_exit_marks_crashed_then_restart_works()
    {
        string dir = NewDir();
        CcConnectConfigGenerator.Write(Path.Combine(dir, "config.toml"), SampleConfig());
        var states = new List<CcConnectState>();
        CcConnectSupervisorOptions shortOptions = FakeShortHostOptions(dir);
        CcConnectSupervisor supervisor = NewSupervisor(shortOptions, state =>
        {
            lock (states)
            {
                states.Add(state);
            }
        });

        await supervisor.StartAsync();
        await WaitForStateAsync(supervisor, CcConnectState.Crashed, TimeSpan.FromSeconds(30));
        lock (states)
        {
            Assert.Contains(CcConnectState.Crashed, states);
            Assert.DoesNotContain(CcConnectState.Stopped, states); // 意外退出不得冒充主动停止。
        }

        // 显式重启(无自动守护):RestartAsync 后进入 Running。
        CcConnectSupervisor restarted = NewSupervisor(FakeLongHostOptions(dir));
        await restarted.RestartAsync();
        Assert.Equal(CcConnectState.Running, restarted.State);
    }

    [Fact]
    public async Task Supervisor_restart_rejected_while_running()
    {
        string dir = NewDir();
        CcConnectConfigGenerator.Write(Path.Combine(dir, "config.toml"), SampleConfig());
        CcConnectSupervisor supervisor = NewSupervisor(FakeLongHostOptions(dir));

        await supervisor.StartAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => supervisor.RestartAsync());
    }
}
