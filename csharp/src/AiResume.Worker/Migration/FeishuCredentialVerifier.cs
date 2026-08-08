using System.Net.Http.Json;
using System.Text.Json;

namespace AiResume.Worker.Migration;

/// <summary>凭据此刻的可信状态。</summary>
public enum FeishuCredentialVerdict
{
    /// <summary>DPAPI 里根本没有凭据。</summary>
    NoCredentials,

    /// <summary>飞书签发了 token —— 这份凭据现在真的能用。</summary>
    Valid,

    /// <summary>飞书拒绝了。secret 被重置、应用停用/未发布,或被限流。</summary>
    Rejected,

    /// <summary>网络层没走通。**不能据此判断凭据失效**,否则会让人白白去重置 secret。</summary>
    NetworkFailed,

    /// <summary>响应读不懂(不是 JSON、字段缺失)。没有结论。</summary>
    Unreadable,
}

/// <summary>一次校验的结论。<c>Code</c>/<c>Msg</c> 是飞书原文,不是机密,正是诊断依据。</summary>
public sealed record FeishuVerifyResult(
    FeishuCredentialVerdict Verdict, int? Code, string? Msg, string Summary)
{
    /// <summary>只有 Valid 才算通过。其余一律不得在界面上显示成绿色。</summary>
    public bool Ok => Verdict == FeishuCredentialVerdict.Valid;
}

/// <summary>
/// 「已配置」到底能不能用。
///
/// 界面原来的依据只有「DPAPI 里有值」—— 那只能证明用户**填过**,证明不了它现在有效。
/// 2026-08-08 第二轮审计用一份错误凭据换 token,飞书返回 <c>code=10003</c>,
/// 而界面照旧显示"已配置/已保存"(A2)。
///
/// 这个区别不是学术性的:app_secret 在开放平台被重置之后,本机这份就永久失效了,
/// 而失效的表现是**机器人不理你** —— 与"open_id 里夹了个空格"、"进程没起来"、
/// "钩子断链"的表现一模一样。面板若不把它们分开,用户只能一个个试。
///
/// 与 <see cref="FeishuCheckCommand"/> 共用同一条判定路径:诊断命令和界面必须给出同一个结论,
/// 否则"命令行说凭据没问题、界面说已配置"这种组合会把排查引向别处。
/// </summary>
public static class FeishuCredentialVerifier
{
    private const string TokenUrl =
        "https://open.feishu.cn/open-apis/auth/v3/tenant_access_token/internal";

    /// <summary>
    /// 从响应判结论。纯函数,离线可测。
    ///
    /// 飞书把业务错误码放在响应体里,HTTP 状态可能是 200 也可能是 400 ——
    /// **只看 HTTP 状态会把 code=10003 读成成功**。
    /// </summary>
    public static FeishuVerifyResult Classify(int httpStatus, string? body)
    {
        try
        {
            using JsonDocument doc = JsonDocument.Parse(body ?? string.Empty);
            JsonElement root = doc.RootElement;
            int? code = root.TryGetProperty("code", out JsonElement c) && c.TryGetInt32(out int v) ? v : null;
            string msg = root.TryGetProperty("msg", out JsonElement m) ? m.GetString() ?? "" : "";
            bool hasToken = root.TryGetProperty("tenant_access_token", out _);

            if (code == 0 && hasToken)
            {
                return new FeishuVerifyResult(
                    FeishuCredentialVerdict.Valid, code, msg, "凭据有效,飞书正常签发 token。");
            }

            // 有 code 但不是 0,或 code=0 却没有 token —— 都不能算通过。
            if (code is not null)
            {
                return new FeishuVerifyResult(
                    FeishuCredentialVerdict.Rejected, code, msg,
                    $"凭据被飞书拒绝(code={code}{(msg.Length > 0 ? " " + msg : "")})。" +
                    "常见原因:app_secret 已在开放平台被重置、应用被停用或未发布、该应用被限流。");
            }

            return new FeishuVerifyResult(
                FeishuCredentialVerdict.Unreadable, null, msg,
                $"响应里没有 code 字段(HTTP {httpStatus}),无法判定。");
        }
        catch (JsonException)
        {
            return new FeishuVerifyResult(
                FeishuCredentialVerdict.Unreadable, null, null,
                $"响应不是 JSON(HTTP {httpStatus}),无法判定。");
        }
    }

    /// <summary>
    /// 用 DPAPI 里的凭据换一次 token。<paramref name="handler"/> 供测试注入,不联网。
    /// </summary>
    public static async Task<FeishuVerifyResult> VerifyAsync(
        CancellationToken cancellationToken,
        FeishuCredentialStore? store = null,
        HttpMessageHandler? handler = null)
    {
        var credentials = store ?? new FeishuCredentialStore();
        if (!credentials.TryLoad(out string appId, out string appSecret, out _))
        {
            return new FeishuVerifyResult(
                FeishuCredentialVerdict.NoCredentials, null, null, "本机还没有保存飞书凭据。");
        }

        try
        {
            using var http = handler is null
                ? new HttpClient()
                : new HttpClient(handler, disposeHandler: false);
            http.Timeout = TimeSpan.FromSeconds(20);

            using HttpResponseMessage resp = await http.PostAsJsonAsync(
                TokenUrl, new { app_id = appId, app_secret = appSecret }, cancellationToken)
                .ConfigureAwait(false);

            string body = await resp.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            return Classify((int)resp.StatusCode, body);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // 网络类失败与凭据无关,必须分开说 —— 混为一谈会让用户去开放平台重置一个
            // 其实好好的 secret,而真正的问题(断网/代理)一直没被看见。
            return new FeishuVerifyResult(
                FeishuCredentialVerdict.NetworkFailed, null, ex.GetType().Name,
                $"请求没走通({ex.GetType().Name})。这是网络问题,不能据此判断凭据失效。");
        }
    }
}
