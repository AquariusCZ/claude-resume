using System.Management;
using System.Runtime.Versioning;

namespace AiResume.Wrapper;

/// <summary>
/// 经 WMI <c>Win32_Process</c> 枚举进程,**能读到命令行**——这是关闭 D-008
/// (证明单消费者)的唯一前提:现役 node agent 与其它 node 服务在进程名上
/// 完全一样(都是 <c>node.exe</c>),只有命令行里的 <c>feishu-agent.js</c> 能区分。
///
/// 为什么用 <c>System.Management</c> 而不自己写:它是微软官方维护的 WMI 托管封装,
/// 上游已有且在 Windows 上可用(与 cc-connect 的 <c>UsageReporter</c> 情况相反)。
/// 手写 NtQueryInformationProcess + 跨位数读 PEB 才是重复造轮子,且在 WOW64 下极脆。
///
/// **安全**:命令行可能包含飞书 <c>app_secret</c> 等凭据。本类只把命令行交给
/// <see cref="SingleConsumerGuard"/> 做子串匹配,守卫写进 <c>ConflictingProcess.Detail</c>
/// 的只有进程名与固定文案。命令行绝不进日志、报告或异常消息。
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class CimRunningProcessLister : IRunningProcessLister
{
    public bool ProvidesCommandLine => true;

    public IReadOnlyList<RunningProcessInfo> List()
    {
        var result = new List<RunningProcessInfo>();

        // 只取需要的三列:少一列就少一份把凭据读进内存的理由。
        using var searcher = new ManagementObjectSearcher(
            "SELECT ProcessId, Name, CommandLine FROM Win32_Process");
        using ManagementObjectCollection collection = searcher.Get();

        foreach (ManagementBaseObject item in collection)
        {
            using (item)
            {
                try
                {
                    object? pidValue = item["ProcessId"];
                    if (pidValue is null)
                    {
                        continue;
                    }

                    int pid = Convert.ToInt32(pidValue);
                    string name = item["Name"] as string ?? string.Empty;

                    // 命令行为 null 是**正常情况**:系统进程与更高完整性级别的进程
                    // 不向本会话暴露命令行。这不影响 ProvidesCommandLine 的承诺——
                    // 我们要找的 node.exe / cc-connect 与本进程同用户同完整性,读得到。
                    string? commandLine = item["CommandLine"] as string;

                    result.Add(new RunningProcessInfo(pid, name, commandLine));
                }
                catch (ManagementException)
                {
                    // 单个实例在枚举途中退出:跳过它,不让整次枚举失败。
                }
                catch (InvalidCastException)
                {
                    // 属性类型异常:同上,跳过。
                }
            }
        }

        return result;
    }
}
