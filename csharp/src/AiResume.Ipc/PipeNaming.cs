using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;

namespace AiResume.Ipc;

/// <summary>
/// pipe 名派生:airesume-&lt;当前用户 SID 的 SHA256 前 16 位&gt;。
/// SID 派生段保证同一台机器上不同用户的 Worker 互不冲突;
/// 16 位 hex 足够区分单机用户集合,且避免 pipe 名过长。
/// </summary>
public static class PipeNaming
{
    /// <summary>由任意 SID 字符串计算确定性 pipe 名(纯函数,测试可直接使用)。</summary>
    public static string ComputePipeName(string sid)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sid);
        byte[] digest = SHA256.HashData(Encoding.UTF8.GetBytes(sid));
        string hex = Convert.ToHexString(digest).ToLowerInvariant();
        return PipeProtocol.PipeNamePrefix + hex.Substring(0, 16);
    }

    /// <summary>
    /// 测试专用的 pipe 名后缀环境变量。
    ///
    /// **只为测试隔离存在**:<see cref="NamedPipeServer"/> 用 pipe 名派生单实例互斥体,
    /// 于是测试自己拉起的 Worker 宿主会和本机正在跑的生产 Worker 抢同一把锁而起不来
    /// (2026-08-06 实测:PowerLossRecoveryTests 两个用例超时 30 秒失败;停掉生产 Worker
    /// 后全绿——实现是对的,缺的是隔离)。
    ///
    /// **不削弱生产的单实例语义**:
    /// - 变量名带 TEST,一眼可辨、可全仓 grep,不会被误当成生产配置项;
    /// - 生产不设置它 → 名字仍是 SID 派生的固定值,第二个生产实例照样被互斥体拒绝;
    /// - 取值经白名单校验,非法值**直接抛异常而不是静默忽略**——静默忽略会让人
    ///   以为隔离生效了,实际两个宿主又挤在同一个名字上。
    /// </summary>
    public const string TestSuffixEnvName = "AIRESUME_TEST_PIPE_SUFFIX";

    /// <summary>
    /// 当前登录用户 SID 派生的默认 pipe 名;获取失败时抛出(该环境不支持 Windows 身份)。
    /// 设置了 <see cref="TestSuffixEnvName"/> 时追加 <c>-&lt;后缀&gt;</c>。
    /// </summary>
    public static string CurrentUserPipeName
    {
        get
        {
            WindowsIdentity identity = WindowsIdentity.GetCurrent();
            string? sid = identity.User?.Value;
            if (string.IsNullOrWhiteSpace(sid))
            {
                throw new InvalidOperationException("无法获取当前用户 SID,不能派生 Named Pipe 名。");
            }

            return ApplyTestSuffix(ComputePipeName(sid));
        }
    }

    /// <summary>
    /// 追加测试后缀。变量未设置时原样返回(生产路径)。
    /// 后缀限定 <c>[A-Za-z0-9]{1,32}</c>:pipe 名不接受路径分隔符,
    /// 长度也要留出互斥体名 <c>Local\&lt;pipe&gt;-mutex</c> 的余量。
    /// </summary>
    internal static string ApplyTestSuffix(string baseName)
    {
        string? suffix = Environment.GetEnvironmentVariable(TestSuffixEnvName);
        if (string.IsNullOrEmpty(suffix))
        {
            return baseName;
        }

        foreach (char c in suffix)
        {
            if (!char.IsAsciiLetterOrDigit(c))
            {
                throw new InvalidOperationException(
                    $"{TestSuffixEnvName} 只接受字母数字([A-Za-z0-9]{{1,32}});当前取值非法,拒绝启动。");
            }
        }

        if (suffix.Length > 32)
        {
            throw new InvalidOperationException(
                $"{TestSuffixEnvName} 最长 32 位;当前取值过长,拒绝启动。");
        }

        return baseName + "-" + suffix;
    }
}
