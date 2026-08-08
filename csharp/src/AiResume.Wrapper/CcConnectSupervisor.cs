using System.Diagnostics;

namespace AiResume.Wrapper;

/// <summary>cc-connect 宿主监督状态。</summary>
public enum CcConnectState
{
    /// <summary>从未启动或已完全停止。</summary>
    NotStarted,

    /// <summary>进程运行中。</summary>
    Running,

    /// <summary>经 StopAsync 主动整树终止后干净退出。</summary>
    Stopped,

    /// <summary>进程意外退出(崩溃);重启必须由调用方显式发起(不自动守护)。</summary>
    Crashed,
}

/// <summary>
/// S6-A cc-connect 进程监督选项:
/// - ExecutablePath 可注入(测试用假进程),生产默认 "cc-connect"(PATH);
/// - ConfigPath 必须显式(仓库外);日志落 LogPath(仓库外);
/// - 不安装 daemon/计划任务,不做自动重启循环(崩溃只置 Crashed + 回调)。
/// </summary>
public sealed class CcConnectSupervisorOptions
{
    public string ExecutablePath { get; init; } = "cc-connect";

    public required string ConfigPath { get; init; }

    /// <summary>cc-connect stdout/stderr 落盘路径(仓库外);缺省为 ConfigPath 同目录 cc-connect.log。</summary>
    public string? LogPath { get; init; }

    /// <summary>
    /// 参数构造钩子(缺省 `--config "&lt;ConfigPath&gt;"`,生产语义固定);
    /// 仅供测试注入假进程或特殊宿主形态,不得用于改变生产启动形状。
    /// </summary>
    public Func<string, string>? ArgumentsBuilder { get; init; }

    /// <summary>
    /// 启动前的单消费者检查。**为 null 时不做检查**——这只在明确不接飞书的场景
    /// (纯 bridge 冒烟、离线测试)下可接受;生产启动路径必须注入。
    /// </summary>
    public SingleConsumerGuard? ConsumerGuard { get; init; }
}

/// <summary>
/// S6-A cc-connect 前台进程监督器(Stage 6 wrapper 进程边界):
/// - StartAsync:单实例语义(Running 时拒绝重复启动);`--config &lt;显式路径&gt;` 启动,
///   stdout/stderr 重定向并追加写仓库外日志(不继承宿主输出句柄);
/// - StopAsync:整树 Kill(S4 实证 release 无优雅停止信号)+ 等待退出 + PID Gone 核验;
///   停止是显式动作,不提前声明成功(等真实退出);
/// - 意外退出:状态置 Crashed 并触发回调;重启必须调用方显式 RestartAsync(无自动守护循环);
/// - 日志写入不含 wrapper 侧凭据(凭据只在 config.toml,RenderSanitized 供展示)。
/// </summary>
public sealed class CcConnectSupervisor : IDisposable
{
    private readonly CcConnectSupervisorOptions _options;
    private readonly Action<CcConnectState>? _onStateChanged;
    private readonly object _gate = new();

    // 日志写独立锁:输出回调(AppendLog)只取 _logGate,永远不与状态锁 _gate 互等;
    // 否则持 _gate 杀进程 → 管道关闭触发输出回调抢 _gate → 死锁(实测教训)。
    private readonly object _logGate = new();
    private readonly List<int> _pids = new();

    private Process? _process;
    private StreamWriter? _logWriter;
    private CcConnectState _state = CcConnectState.NotStarted;
    private int? _pid;
    private bool _stopping;

    public CcConnectSupervisor(CcConnectSupervisorOptions options, Action<CcConnectState>? onStateChanged = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrEmpty(options.ConfigPath);
        _options = options;
        _onStateChanged = onStateChanged;
    }

    public CcConnectState State
    {
        get
        {
            lock (_gate)
            {
                return _state;
            }
        }
    }

    /// <summary>当前宿主 PID;未运行时为 null。</summary>
    public int? ProcessId
    {
        get
        {
            lock (_gate)
            {
                return _state == CcConnectState.Running ? _pid : null;
            }
        }
    }

    public string LogPath => _options.LogPath
        ?? Path.Combine(Path.GetDirectoryName(Path.GetFullPath(_options.ConfigPath))!, "cc-connect.log");

    /// <summary>启动 cc-connect 前台进程;Running 时拒绝(单实例语义)。</summary>
    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (_state == CcConnectState.Running)
            {
                throw new InvalidOperationException("cc-connect 已在运行,拒绝重复启动(单实例语义)。");
            }

            if (!File.Exists(_options.ConfigPath))
            {
                throw new FileNotFoundException("cc-connect 配置文件不存在(fail-fast,不得以默认配置启动)。", _options.ConfigPath);
            }

            // 单消费者铁律:飞书长连接是集群模式,同一应用两个客户端在线时事件被**随机截走**。
            // 这一检查必须在 spawn 之前且不可绕过——启动之后再发现冲突,消息已经开始丢了。
            ConsumerGuardResult guard = _options.ConsumerGuard is { } g
                ? g.Check(DeclaresFeishuPlatform(_options.ConfigPath))
                : new ConsumerGuardResult(ConsumerGuardVerdict.Clear, Array.Empty<ConflictingProcess>(), null);
            if (!guard.CanStart)
            {
                string detail = guard.Conflicts.Count > 0
                    ? " 冲突进程:" + string.Join("、", guard.Conflicts.Select(c => $"{c.Kind}#{c.Pid}({c.Detail})"))
                    : string.Empty;
                throw new InvalidOperationException(
                    $"单消费者检查未通过({guard.Verdict}):{guard.Reason}{detail}");
            }

            string? logDir = Path.GetDirectoryName(Path.GetFullPath(LogPath));
            if (!string.IsNullOrEmpty(logDir))
            {
                Directory.CreateDirectory(logDir);
            }

            string arguments = _options.ArgumentsBuilder?.Invoke(_options.ConfigPath)
                ?? $"--config \"{_options.ConfigPath}\"";
            var psi = new ProcessStartInfo(_options.ExecutablePath, arguments)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = Path.GetDirectoryName(Path.GetFullPath(_options.ConfigPath))!,
                // 重定向并异步消费:不得让子进程继承宿主输出句柄(实测 vstest 挂起教训)。
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };

            _logWriter = new StreamWriter(new FileStream(LogPath, FileMode.Append, FileAccess.Write, FileShare.Read))
            {
                AutoFlush = true,
            };

            _process = Process.Start(psi) ?? throw new InvalidOperationException("cc-connect 启动失败。");
            _pids.Add(_process.Id);
            _pid = _process.Id;
            _stopping = false;
            _process.OutputDataReceived += (_, e) => AppendLog(e.Data);
            _process.ErrorDataReceived += (_, e) => AppendLog(e.Data);
            _process.Exited += OnProcessExited;
            _process.BeginOutputReadLine();
            _process.BeginErrorReadLine();
            _process.EnableRaisingEvents = true;
            SetStateUnlocked(CcConnectState.Running);
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// 停止:整树 Kill + 等待真实退出(不提前声明成功)。
    /// release 二进制无优雅停止信号(S4 实证),Kill 是唯一停止手段。
    /// </summary>
    public async Task StopAsync(TimeSpan waitTimeout, CancellationToken cancellationToken = default)
    {
        Process? process;
        lock (_gate)
        {
            if (_state != CcConnectState.Running)
            {
                return;
            }

            _stopping = true;
            process = _process;
        }

        if (process is null)
        {
            return;
        }

        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
            // 进程恰在 Kill 前退出:按已退出处理。
        }

        bool exited = await WaitForExitAsync(process, waitTimeout, cancellationToken).ConfigureAwait(false);
        if (!exited)
        {
            // 未确认退出不得冒充成功:锁外按 PID 清单兜底再杀一次。
            List<int> snapshot;
            lock (_gate)
            {
                snapshot = new List<int>(_pids);
            }

            KillByPidList(snapshot);
        }

        // 锁外清理:kill/wait/Dispose 都可能触发输出或 Exited 回调,
        // 回调内部会抢锁(日志锁、状态锁),持锁执行必死锁。
        CleanupResources(process, removeHandlers: true);

        lock (_gate)
        {
            _pids.Clear();
            _pid = null;
            _stopping = false;
            SetStateUnlocked(CcConnectState.Stopped);
        }
    }

    /// <summary>崩溃后显式重启(无自动守护);仅 NotStarted/Stopped/Crashed 允许。</summary>
    public Task RestartAsync(CancellationToken cancellationToken = default)
    {
        Process? stale;
        lock (_gate)
        {
            if (_state == CcConnectState.Running)
            {
                throw new InvalidOperationException("cc-connect 仍在运行,先停止再重启。");
            }

            stale = _process;
            _process = null;
        }

        // 锁外释放残留句柄(Process.Dispose 可能等待异步读取器)。
        CleanupResources(stale, removeHandlers: true);
        return StartAsync(cancellationToken);
    }

    /// <summary>
    /// 配置里是否声明了飞书平台。
    /// **刻意保守**:读不到文件、解析不确定时一律返回 true —— 宁可多做一次核验,
    /// 也不能因为"看起来没配飞书"就跳过守卫。同时匹配 feishu 与 lark 两种写法。
    /// </summary>
    internal static bool DeclaresFeishuPlatform(string configPath)
    {
        try
        {
            string text = File.ReadAllText(configPath);
            return text.Contains("feishu", StringComparison.OrdinalIgnoreCase)
                || text.Contains("lark", StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception)
        {
            return true;
        }
    }

    /// <summary>等待进程真实退出;超时返回 false。</summary>
    private static async Task<bool> WaitForExitAsync(Process process, TimeSpan timeout, CancellationToken cancellationToken)
    {
        DateTime deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                if (process.HasExited)
                {
                    return true;
                }
            }
            catch (InvalidOperationException)
            {
                return true;
            }

            await Task.Delay(100, cancellationToken).ConfigureAwait(false);
        }

        try
        {
            return process.HasExited;
        }
        catch (InvalidOperationException)
        {
            return true;
        }
    }

    private void OnProcessExited(object? sender, EventArgs e)
    {
        StreamWriter? writer;
        lock (_gate)
        {
            if (_state != CcConnectState.Running)
            {
                return;
            }

            _pid = null;
            // 主动停止路径(StopAsync)会把状态置 Stopped;此处只处理意外退出。
            SetStateUnlocked(_stopping ? CcConnectState.Stopped : CcConnectState.Crashed);
        }

        // 进程已退出,日志写入器不再有新输出到达;锁外释放句柄,
        // 使后续 Restart/新 supervisor 可 Append 同一日志。
        lock (_logGate)
        {
            writer = _logWriter;
            _logWriter = null;
        }

        if (writer is not null)
        {
            try
            {
                writer.Dispose();
            }
            catch
            {
                // 忽略。
            }
        }
    }

    private static void KillByPidList(IReadOnlyList<int> pids)
    {
        foreach (int pid in pids)
        {
            try
            {
                using Process p = Process.GetProcessById(pid);
                p.Kill(entireProcessTree: true);
                p.WaitForExit(5_000);
            }
            catch
            {
                // 进程已退出或句柄不可得。
            }
        }
    }

    /// <summary>
    /// 锁外资源清理:摘事件处理器后 Dispose 进程与日志写入器。
    /// Dispose 会等待异步输出读取器,绝不能在 _gate 内执行。
    /// </summary>
    private void CleanupResources(Process? process, bool removeHandlers)
    {
        if (process is not null)
        {
            if (removeHandlers)
            {
                process.Exited -= OnProcessExited;
            }

            try
            {
                process.Dispose();
            }
            catch
            {
                // 已释放忽略。
            }
        }

        StreamWriter? writer;
        lock (_logGate)
        {
            writer = _logWriter;
            _logWriter = null;
        }

        if (writer is not null)
        {
            try
            {
                writer.Dispose();
            }
            catch
            {
                // 忽略。
            }
        }
    }

    private void SetStateUnlocked(CcConnectState next)
    {
        if (_state == next)
        {
            return;
        }

        _state = next;
        try
        {
            _onStateChanged?.Invoke(next);
        }
        catch
        {
            // 回调异常不得影响监督器状态机。
        }
    }

    private void AppendLog(string? line)
    {
        if (line is null)
        {
            return;
        }

        lock (_logGate)
        {
            try
            {
                _logWriter?.WriteLine(line);
            }
            catch
            {
                // 日志写入失败不影响进程监督。
            }
        }
    }

    public void Dispose()
    {
        Process? process;
        List<int> pids;
        lock (_gate)
        {
            // 锁内只做标记与引用摘除;kill/wait/Dispose 一律锁外,
            // 否则进程死亡触发输出/Exited 回调抢 _gate → 死锁。
            _stopping = true;
            process = _process;
            _process = null;
            _pid = null;
            pids = new List<int>(_pids);
            _pids.Clear();
            _state = CcConnectState.NotStarted;
        }

        if (process is not null)
        {
            KillByPidList(pids);
        }

        CleanupResources(process, removeHandlers: true);
    }
}
