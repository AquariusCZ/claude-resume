namespace AiResume.Worker.Migration;

/// <summary>
/// <c>AiResume.Worker.exe feishu-check</c>:用 DPAPI 里的凭据换一次 tenant_access_token,
/// 只报告 HTTP 状态与飞书返回的 <c>code</c>/<c>msg</c>。
///
/// **为什么需要这个命令**:2026-08-06 回滚后现役 node agent 每 5 秒报
/// `tenant_access_token 400`,而 `config.json` 的最后写入时间证明它两天没被改过、
/// 同一份凭据当天 08:04 还认证成功。要区分「凭据失效」和「客户端坏了」,
/// 唯一可靠的办法是用同一份凭据独立发一次请求。
///
/// **绝不打印 app_secret**;飞书的错误码与错误消息不是机密,正是诊断需要的东西。
/// </summary>
public static class FeishuCheckCommand
{
    private const string TokenUrl = "https://open.feishu.cn/open-apis/auth/v3/tenant_access_token/internal";

    public static int Run()
    {
        if (!new FeishuCredentialStore().TryLoad(out string appId, out string appSecret, out string allowFrom))
        {
            Console.Error.WriteLine("DPAPI 里没有飞书凭据。先运行 import-feishu。");
            return 1;
        }

        Console.WriteLine($"app_id      : {FeishuCredentialStore.Mask(appId)}");
        Console.WriteLine($"app_secret  : 已加载({appSecret.Length} 位,不打印)");
        Console.WriteLine($"allow_from  : {allowFrom}");
        Console.WriteLine();

        // 判定逻辑搬到 FeishuCredentialVerifier,与界面共用同一条路径。
        // 命令行和界面对同一份凭据给出不同结论,比两边都不说更能把排查引向别处。
        FeishuVerifyResult r = FeishuCredentialVerifier
            .VerifyAsync(CancellationToken.None).GetAwaiter().GetResult();

        if (r.Code is not null)
        {
            Console.WriteLine($"code={r.Code}  msg={r.Msg}");
        }

        Console.WriteLine("结论:" + r.Summary);
        return r.Ok ? 0 : 1;
    }
}
