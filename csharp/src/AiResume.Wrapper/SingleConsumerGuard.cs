using System.Diagnostics;

namespace AiResume.Wrapper;

/// <summary>消费者守卫判定。</summary>
public enum ConsumerGuardVerdict
{
    /// <summary>已核验无冲突,可以启动。</summary>
    Clear,

    /// <summary>发现其他消费者,必须先停止它们。</summary>
    Conflict,

    /// <summary>无法核验(枚举失败,或枚举器看不到命令行);fail-closed 拒绝启动。</summary>
    Unverifiable,
}

/// <summary>一个冲突的消费者进程。<see cref="Detail"/> 只放安全的短描述,**绝不放原始命令行**。</summary>
public sealed record ConflictingProcess(int Pid, string Kind, string Detail);

/// <summary>守卫检查结果。</summary>
public sealed record ConsumerGuardResult(
    ConsumerGuardVerdict Verdict,
    IReadOnlyList<ConflictingProcess> Conflicts,
    string? Reason)
{
    public bool CanStart => Verdict == ConsumerGuardVerdict.Clear;
}

/// <summary>一个正在运行的进程的简要信息。</summary>
public sealed record RunningProcessInfo(int Pid, string Name, string? CommandLine);

/// <summary>进程枚举抽象。</summary>
public interface IRunningProcessLister
{
    /// <summary>
    /// 本枚举器能否提供进程命令行。
    ///
    /// **这个能力声明是安全语义的一部分,不是可选元数据**:现役 node agent 只能靠
    /// 命令行里的 <c>feishu-agent.js</c> 识别。看不到命令行的枚举器无法排除它的存在,
    /// 因此守卫必须把这种情况判成无法核验,而不是"没找到冲突"。
    /// </summary>
    bool ProvidesCommandLine { get; }

    IReadOnlyList<RunningProcessInfo> List();
}

/// <summary>
/// 单消费者守卫:启动 cc-connect 前确认本机没有第二个飞书事件消费者。
///
/// **为什么必须有**(D-015 实证):飞书长连接是**集群模式**,同一应用有多个客户端在线时
/// 事件**随机投递给其中一个**,不是广播。两个消费者同时在线的表现不是"重复回复",
/// 而是用户消息被随机截走——比重复回复更难发现,也更难排查。
/// </summary>
public sealed class SingleConsumerGuard
{
    private static readonly string[] LegacyAgentMarkers = { "feishu-agent.js", "feishu-launch.vbs" };
    private const string CcConnectName = "cc-connect";
    private static readonly HashSet<string> LegacyHostNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "node", "wscript", "cscript", "cmd",
    };

    private readonly IRunningProcessLister _lister;
    private readonly int _selfPid;

    public SingleConsumerGuard(IRunningProcessLister lister, int selfPid)
    {
        _lister = lister ?? throw new ArgumentNullException(nameof(lister));
        _selfPid = selfPid;
    }

    /// <summary>
    /// 生产装配:Windows 上用能读命令行的 CIM 枚举器,其它平台退回只有进程名的
    /// <see cref="DiagnosticsRunningProcessLister"/>——后者会让守卫判 Unverifiable 而拒绝启动,
    /// 这正是想要的行为(现役 node agent 只在 Windows 上跑,非 Windows 上我们无从核验)。
    ///
    /// **任何生产启动路径都必须经这里取守卫**,不要自己 new 一个 Diagnostics 版本。
    /// </summary>
    public static SingleConsumerGuard CreateDefault() => new(
        OperatingSystem.IsWindows()
            ? new CimRunningProcessLister()
            : new DiagnosticsRunningProcessLister(),
        Environment.ProcessId);

    /// <summary>
    /// 执行检查。本方法绝不抛异常:任何核验失败都转成
    /// <see cref="ConsumerGuardVerdict.Unverifiable"/>(fail-closed)。
    /// </summary>
    /// <param name="feishuPlatformConfigured">本次启动的配置是否声明了飞书平台。</param>
    public ConsumerGuardResult Check(
        bool feishuPlatformConfigured,
        IReadOnlySet<int>? allowedCcConnectPids = null)
    {
        // 不接飞书平台就不消费飞书事件,不可能与谁抢事件。
        // 这一步刻意在枚举进程之前:bridge-only 启动不该为一次无意义的核验付出代价。
        if (!feishuPlatformConfigured)
        {
            return Clear();
        }

        // 看不到命令行 = 无法排除现役 node agent。此时报 Clear 等于凭空担保,
        // 只能报无法核验(生产切换前必须注入能读命令行的枚举器,见 D-008 关闭条件)。
        if (!_lister.ProvidesCommandLine)
        {
            return new ConsumerGuardResult(
                ConsumerGuardVerdict.Unverifiable,
                Array.Empty<ConflictingProcess>(),
                "当前进程枚举器无法读取命令行,不能排除现役 node agent 仍在消费同一飞书应用;拒绝启动。");
        }

        IReadOnlyList<RunningProcessInfo>? processes;
        try
        {
            processes = _lister.List();
        }
        catch (Exception ex)
        {
            return new ConsumerGuardResult(
                ConsumerGuardVerdict.Unverifiable,
                Array.Empty<ConflictingProcess>(),
                "无法枚举进程列表,拒绝启动:" + ex.Message);
        }

        if (processes is null)
        {
            return new ConsumerGuardResult(
                ConsumerGuardVerdict.Unverifiable,
                Array.Empty<ConflictingProcess>(),
                "进程枚举返回空引用,无法核验,拒绝启动。");
        }

        var conflicts = new List<ConflictingProcess>();
        foreach (RunningProcessInfo? proc in processes)
        {
            if (proc is null)
            {
                continue;
            }

            // 进程名统一取文件名并去掉 .exe:传进来的可能是全路径。
            string name = SafeFileName(proc.Name);

            if (LegacyHostNames.Contains(name) && string.IsNullOrWhiteSpace(proc.CommandLine))
            {
                return new ConsumerGuardResult(
                    ConsumerGuardVerdict.Unverifiable,
                    Array.Empty<ConflictingProcess>(),
                    "存在无法读取命令行的脚本宿主进程,不能排除现役 feishu-agent.js 仍在消费同一飞书应用;拒绝启动。");
            }

            string? legacyMarker = LegacyAgentMarkers.FirstOrDefault(marker =>
                !string.IsNullOrEmpty(proc.CommandLine) &&
                proc.CommandLine.Contains(marker, StringComparison.OrdinalIgnoreCase));
            bool legacy = legacyMarker is not null;
            bool otherCcConnect = string.Equals(name, CcConnectName, StringComparison.OrdinalIgnoreCase)
                && proc.Pid != _selfPid
                && !(allowedCcConnectPids?.Contains(proc.Pid) ?? false);

            // Detail 只由**进程名与固定文案**拼成。
            // 命令行里可能带飞书 app_secret,一旦进入结果就会流进日志与界面。
            if (legacy)
            {
                conflicts.Add(new ConflictingProcess(proc.Pid, "legacy-node-agent", name + "(命令行含 " + legacyMarker + ")"));
            }
            else if (otherCcConnect)
            {
                conflicts.Add(new ConflictingProcess(proc.Pid, "cc-connect", name));
            }
        }

        if (conflicts.Count == 0)
        {
            return Clear();
        }

        conflicts.Sort((a, b) => a.Pid.CompareTo(b.Pid));
        return new ConsumerGuardResult(
            ConsumerGuardVerdict.Conflict,
            conflicts,
            $"发现 {conflicts.Count} 个其他飞书事件消费者,必须先停止后才能启动。");
    }

    private static ConsumerGuardResult Clear() =>
        new(ConsumerGuardVerdict.Clear, Array.Empty<ConflictingProcess>(), null);

    /// <summary>取文件名并去掉 .exe 后缀;异常输入一律退回原串,不抛。</summary>
    private static string SafeFileName(string? raw)
    {
        if (string.IsNullOrEmpty(raw))
        {
            return string.Empty;
        }

        string name;
        try
        {
            name = Path.GetFileName(raw);
        }
        catch (Exception)
        {
            name = raw;
        }

        if (string.IsNullOrEmpty(name))
        {
            name = raw;
        }

        return name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            ? name[..^4]
            : name;
    }
}

/// <summary>
/// 基于 <see cref="Process.GetProcesses"/> 的枚举器。
///
/// **拿不到命令行**(Toolhelp32/.NET 进程 API 都只给 exe 名),因此
/// <see cref="ProvidesCommandLine"/> 为 false,守卫会据此判成无法核验。
/// 这不是缺陷而是如实声明:生产切换(Stage 10)前必须提供一个能读命令行的枚举器
/// (CIM <c>Win32_Process</c> 或等价手段),那也是 D-008「证明单消费者」的关闭条件。
/// </summary>
public sealed class DiagnosticsRunningProcessLister : IRunningProcessLister
{
    public bool ProvidesCommandLine => false;

    public IReadOnlyList<RunningProcessInfo> List()
    {
        var result = new List<RunningProcessInfo>();
        foreach (Process proc in Process.GetProcesses())
        {
            try
            {
                result.Add(new RunningProcessInfo(proc.Id, proc.ProcessName, null));
            }
            catch (Exception)
            {
                // 单个进程读属性失败(已退出/无权限)跳过它,不让整次枚举失败。
            }
            finally
            {
                proc.Dispose();
            }
        }

        return result;
    }
}
