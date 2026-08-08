using System.Net.Http.Json;
using System.Text.Json;

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

        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
            using HttpResponseMessage resp = http.PostAsJsonAsync(
                TokenUrl, new { app_id = appId, app_secret = appSecret }).GetAwaiter().GetResult();

            string body = resp.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            Console.WriteLine($"HTTP {(int)resp.StatusCode}");

            // 飞书把业务错误码放在 200/400 的响应体里。code/msg 不是机密,是诊断依据。
            try
            {
                using JsonDocument doc = JsonDocument.Parse(body);
                JsonElement root = doc.RootElement;
                int code = root.TryGetProperty("code", out JsonElement c) && c.TryGetInt32(out int v) ? v : -1;
                string msg = root.TryGetProperty("msg", out JsonElement m) ? m.GetString() ?? "" : "";
                bool hasToken = root.TryGetProperty("tenant_access_token", out _);

                Console.WriteLine($"code={code}  msg={msg}");
                Console.WriteLine(code == 0 && hasToken
                    ? "结论:**凭据有效**,飞书正常签发 token。问题不在凭据。"
                    : "结论:**凭据被飞书拒绝**。常见原因:app_secret 已在开放平台被重置、"
                      + "应用被停用/未发布、或该应用被限流。需在开放平台核对后更新凭据。");
                return code == 0 ? 0 : 1;
            }
            catch (JsonException)
            {
                Console.WriteLine("响应不是 JSON,无法判定。");
                return 1;
            }
        }
        catch (Exception ex)
        {
            // 网络类失败与凭据无关,必须分开说,否则会误导用户去重置 secret。
            Console.Error.WriteLine($"请求未能完成(网络层):{ex.GetType().Name}");
            Console.Error.WriteLine("结论:这是网络问题,**不能据此判断凭据失效**。");
            return 1;
        }
    }
}
