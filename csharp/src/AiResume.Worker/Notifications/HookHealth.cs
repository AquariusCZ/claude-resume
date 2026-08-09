namespace AiResume.Worker.Notifications;

/// <summary>
/// 「已启用」到底能不能兑现。
///
/// 此前 <see cref="NotificationProviderStatus.IsEnabled"/> 的全部含义是
/// **配置里有一条命令,而且命令里出现过 AiResume.Hook.exe 这几个字**。
/// 它回答不了用户真正在问的那个问题:*任务现在结束,我收得到通知吗?*
///
/// 2026-08-08 第二轮审计把 hook 可执行文件挪走,`notify list` 与界面开关
/// 照旧显示「已启用=True」、绿灯照旧亮着 —— 而那条命令已经永远执行不了。
/// 这是本项目第三次栽在同一处:第一次是钩子写成裸文件名(不在 PATH),
/// 第二次是钩子指向仓库 bin(清一次就断),这次是文件被删。
/// 三次的共同点都不是"写错了",而是**判据只看配置、不看世界**。
///
/// 所以这里补上最后那一步:把配置里那条命令的可执行文件拿出来,问文件系统它在不在。
/// </summary>
public static class HookHealth
{
    /// <summary>
    /// 从一条 hook 命令里取出可执行文件路径;取不到返回 null(不抛)。
    ///
    /// **按 <c>.exe</c> 边界切,不按空格。** 安装目录叫 "AI Resume" ——
    /// 按空格切会把路径截成 <c>…\Local\AI</c>,这个错误本身就造成过一次事故。
    /// </summary>
    public static string? ExtractExe(string? hookCommand)
        => HookCommand.ExtractExecutable(hookCommand);

    /// <summary>
    /// 这条命令是不是已经执行不了了。
    ///
    /// 只有**确证文件不存在**才返回 true。取不出可执行文件路径时返回 false ——
    /// 「核对不了」不等于「坏了」,把未知说成故障同样是在骗人,只是方向相反。
    /// </summary>
    public static bool IsBroken(string? hookCommand, Func<string, bool>? fileExists = null)
    {
        string? exe = ExtractExe(hookCommand);
        if (exe is null || exe.Length == 0)
        {
            return false;
        }

        Func<string, bool> exists = fileExists ?? File.Exists;
        try
        {
            return !exists(exe);
        }
        catch (Exception)
        {
            // 路径非法/无权访问:同样归入"核对不了",不冒充结论。
            return false;
        }
    }

    /// <summary>断链时给用户看的说明。必须写清后果,否则"路径不存在"读起来像个无关紧要的警告。</summary>
    public static string BrokenDetail(string? hookCommand)
        => $"钩子指向的程序不存在:{ExtractExe(hookCommand) ?? hookCommand}" +
           " —— 配置还在,但任务结束时这条命令执行不了,通知永远不会到。重新安装或关掉再打开本开关可修复。";
}
