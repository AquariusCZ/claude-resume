using System.Security.Cryptography;
using System.Text;

namespace AiResume.Worker.Supervision;

/// <summary>
/// 命令签名(登记与核验的统一函数)。
///
/// 决策说明:
/// - 签名维度 = 可执行文件名(小写规范化)的 SHA256。Toolhelp32 快照只提供 exe 文件名;
///   参数维度需 WMI(引入 System.Management 包,违反"不新增 NuGet"红线),故不参与签名。
/// - 防误杀能力:进程 PID 被系统复用为其他程序时,exe 文件名必然不同 → 签名不匹配 → mismatched,
///   配合 ±5 秒启动时间容差,可拦截绝大多数 PID 复用场景(D-009 教训:禁止只凭 PID 杀进程)。
/// - 登记与核验必须调用同一函数,保证可比。
/// </summary>
public static class ProcessSignature
{
    public static string Compute(string executableName)
    {
        string normalized = (executableName ?? string.Empty).Trim().ToLowerInvariant();
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(normalized));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
