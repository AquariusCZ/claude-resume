using System.Net;
using System.Text.Json;

namespace AiResume.Worker.Probes;

/// <summary>
/// Codex **真实授权探测**:带凭据 GET <c>{base_url}/v1/models</c>。
///
/// 为什么需要它:`codex doctor` 的 route probe 是**不带凭据**的,401 只说明"路由需要认证",
/// 证明不了你的 key 有没有权限;而唯一此前能证明的办法 `codex exec` 要 10-12 秒、
/// **2.3 万 tokens**(实测),贵到不可能每次开窗都跑。
///
/// 带凭据打 /v1/models 两边都占:**1.3 秒、0 token**,而且 200/401 的分野
/// 完全由凭据决定(实测:同一请求带 key → 200 并返回模型列表;去掉 key → 401 API_KEY_REQUIRED)。
/// 于是"每次探测都是真的"从奢侈变成默认。
///
/// **必须带正常 User-Agent。** 实测该端点在 Cloudflare 后面,
/// 用 .NET/Python 的默认 UA 一律 403 `error code: 1010`(客户端被封),
/// 带不带凭据都一样 —— 那会被误读成"认证失败",正是这个探测要避免的错。
///
/// 凭据只从本机 Codex 自己的文件读出来直接放进 Authorization 头,
/// **不落盘、不进日志、不进任何应答**。
/// </summary>
public enum CodexAuthOutcome
{
    /// <summary>带凭据请求成功——key 确实有权限。</summary>
    Authorized,

    /// <summary>
    /// 能列模型,但不允许推理。
    ///
    /// 这一档是 2026-08-08 第二轮审计逼出来的(A6):审计方架了个假端点,
    /// <c>/v1/models</c> 返 200、<c>/v1/responses</c> 返 403,界面照样绿着说"凭据已验证"。
    /// 而列模型往往是**公开或低权限**的路由 —— 它证明的是"服务端认识这把 key",
    /// 不是"这把 key 能跑活儿"。真实场景里 sub2api 的额度用尽、
    /// 按模型授权收紧,表现正是这个组合。
    /// </summary>
    NoInference,

    /// <summary>凭据被拒。</summary>
    Rejected,

    /// <summary>被限流。</summary>
    Limited,

    /// <summary>服务端异常(5xx)。</summary>
    ServerError,

    /// <summary>网络层失败(DNS/TCP/TLS/超时)。</summary>
    NetworkFailed,

    /// <summary>本机读不到 base_url 或 key,无法发起带凭据请求。</summary>
    NotConfigured,
}

public sealed record CodexAuthResult(CodexAuthOutcome Outcome, string Detail);

public static class CodexAuthProbe
{
    /// <summary>Cloudflare 会按 UA 拦默认的 HTTP 客户端,必须伪装成常规浏览器。</summary>
    private const string UserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36";

    private const int TimeoutSeconds = 20;

    /// <summary>
    /// 从 <c>~/.codex/config.toml</c> 与 <c>~/.codex/auth.json</c> 解析出
    /// 活动 provider 的 base_url 与 API key。**返回值含凭据,调用方不得回显。**
    /// </summary>
    public static (string? BaseUrl, string? ApiKey) ReadActiveProvider(string? codexHome = null)
    {
        string home = codexHome ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex");

        string? baseUrl = TryReadBaseUrl(Path.Combine(home, "config.toml"));
        string? apiKey = TryReadApiKey(Path.Combine(home, "auth.json"));
        return (baseUrl, apiKey);
    }

    /// <summary>
    /// 极简 TOML 读取:找 <c>model_provider = "X"</c>,再在 <c>[model_providers.X]</c>
    /// 段里取 <c>base_url</c>。
    ///
    /// **刻意不引 TOML 解析库**:这里只需要两个标量,而 Codex 的 config.toml 里
    /// 有多行数组、内联表和被转义得很深的字符串(notify 那条实测嵌了六层),
    /// 一个通用解析器在这些形态上失手的概率,比逐行找两个键高得多。
    /// 读不到就返回 null,由调用方降级到 doctor —— 绝不猜。
    /// </summary>
    private static string? TryReadBaseUrl(string configPath)
    {
        if (!File.Exists(configPath))
        {
            return null;
        }

        string[] lines;
        try
        {
            lines = File.ReadAllLines(configPath);
        }
        catch (IOException)
        {
            return null;
        }

        string? activeProvider = null;
        foreach (string raw in lines)
        {
            string line = raw.Trim();
            if (line.StartsWith('[') || line.StartsWith('#'))
            {
                // 顶层键必须在任何小节开始之前出现;进了小节就不会再有了。
                if (line.StartsWith('['))
                {
                    break;
                }

                continue;
            }

            if (TryReadStringValue(line, "model_provider", out string? v))
            {
                activeProvider = v;
                break;
            }
        }

        if (string.IsNullOrWhiteSpace(activeProvider))
        {
            return null;
        }

        string wanted = "[model_providers." + activeProvider + "]";
        bool inSection = false;
        foreach (string raw in lines)
        {
            string line = raw.Trim();
            if (line.StartsWith('['))
            {
                inSection = string.Equals(line, wanted, StringComparison.Ordinal);
                continue;
            }

            if (inSection && TryReadStringValue(line, "base_url", out string? url))
            {
                return url;
            }
        }

        return null;
    }

    private static bool TryReadStringValue(string line, string key, out string? value)
    {
        value = null;
        int eq = line.IndexOf('=');
        if (eq <= 0 || !string.Equals(line[..eq].Trim(), key, StringComparison.Ordinal))
        {
            return false;
        }

        string rhs = line[(eq + 1)..].Trim();
        // TOML 的字符串可以是 "…" 或 '…';两种都要认。
        if (rhs.Length >= 2 && ((rhs[0] == '"' && rhs[^1] == '"') || (rhs[0] == '\'' && rhs[^1] == '\'')))
        {
            value = rhs[1..^1];
            return value.Length > 0;
        }

        return false;
    }

    /// <summary>
    /// 取顶层 <c>model = "…"</c>。最小推理探测要指定模型,而**必须用用户真正在跑的那个** ——
    /// 随便挑一个能跑的模型来证明"可以推理",证明的不是用户关心的那件事。
    /// 读不到返回 null,由调用方跳过推理探测并如实说明。
    /// </summary>
    public static string? TryReadModel(string configPath)
    {
        if (!File.Exists(configPath))
        {
            return null;
        }

        try
        {
            foreach (string raw in File.ReadAllLines(configPath))
            {
                string line = raw.Trim();
                if (line.StartsWith('['))
                {
                    // 顶层键只出现在第一个小节之前。
                    break;
                }

                if (line.StartsWith('#'))
                {
                    continue;
                }

                if (TryReadStringValue(line, "model", out string? v))
                {
                    return v;
                }
            }
        }
        catch (IOException)
        {
            return null;
        }

        return null;
    }

    private static string? TryReadApiKey(string authPath)
    {
        if (!File.Exists(authPath))
        {
            return null;
        }

        try
        {
            using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(authPath));
            if (doc.RootElement.ValueKind == JsonValueKind.Object &&
                doc.RootElement.TryGetProperty("OPENAI_API_KEY", out JsonElement k) &&
                k.ValueKind == JsonValueKind.String)
            {
                string? key = k.GetString();
                return string.IsNullOrWhiteSpace(key) ? null : key;
            }
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            return null;
        }

        return null;
    }

    /// <summary>把 HTTP 状态码翻成结论。抽成纯函数是为了能不联网地断言映射。</summary>
    public static CodexAuthResult Classify(HttpStatusCode status)
    {
        int code = (int)status;
        if (code is >= 200 and < 300)
        {
            return new CodexAuthResult(CodexAuthOutcome.Authorized, "凭据有效");
        }

        if (code is 401 or 403)
        {
            return new CodexAuthResult(CodexAuthOutcome.Rejected, $"凭据被拒(HTTP {code})");
        }

        if (code == 429)
        {
            return new CodexAuthResult(CodexAuthOutcome.Limited, "被限流(HTTP 429)");
        }

        if (code >= 500)
        {
            return new CodexAuthResult(CodexAuthOutcome.ServerError, $"服务端异常(HTTP {code})");
        }

        // 4xx 里其余的既不是"授权没问题"也不是"授权被拒",不硬塞进任何一档。
        return new CodexAuthResult(CodexAuthOutcome.NetworkFailed, $"意外状态(HTTP {code})");
    }

    /// <summary>
    /// 把最小推理请求的状态码翻成结论。**前提是 /v1/models 已经 200** ——
    /// 也就是说凭据已被服务端接受,这一步只回答"接受之后允不允许干活"。
    ///
    /// 400/404/422 单独一档:那是**端点形状不支持**,不是权限问题。
    /// sub2api 各家路由不一,把"这家不认识 chat/completions"读成"你没有推理权限",
    /// 会让一个好好的配置被标红 —— 误判和漏判一样是在骗人。
    /// </summary>
    public static CodexAuthResult ClassifyInference(HttpStatusCode status)
    {
        int code = (int)status;
        if (code is >= 200 and < 300)
        {
            return new CodexAuthResult(CodexAuthOutcome.Authorized, "可用 · 凭据与推理已验证");
        }

        if (code is 401 or 403)
        {
            return new CodexAuthResult(
                CodexAuthOutcome.NoInference,
                $"只能列模型,不允许推理(HTTP {code})—— 凭据本身被接受,但跑不了任务");
        }

        if (code == 429)
        {
            return new CodexAuthResult(CodexAuthOutcome.Limited, "被限流(HTTP 429)");
        }

        if (code >= 500)
        {
            return new CodexAuthResult(CodexAuthOutcome.ServerError, $"服务端异常(HTTP {code})");
        }

        // 端点不支持这种最小探测:凭据结论保持"已验证",但必须说清没验证到推理。
        return new CodexAuthResult(
            CodexAuthOutcome.Authorized,
            $"可用 · 凭据已验证(端点未接受最小推理探测 HTTP {code},推理权限未核实)");
    }

    public static async Task<CodexAuthResult> ProbeAsync(
        string? codexHome = null, HttpMessageHandler? handler = null, CancellationToken ct = default)
    {
        (string? baseUrl, string? apiKey) = ReadActiveProvider(codexHome);
        if (string.IsNullOrWhiteSpace(baseUrl) || string.IsNullOrWhiteSpace(apiKey))
        {
            return new CodexAuthResult(
                CodexAuthOutcome.NotConfigured, "读不到 Codex 的 base_url 或凭据");
        }

        using var client = handler is null ? new HttpClient() : new HttpClient(handler, disposeHandler: false);
        client.Timeout = TimeSpan.FromSeconds(TimeoutSeconds);

        var request = new HttpRequestMessage(HttpMethod.Get, baseUrl.TrimEnd('/') + "/v1/models");
        request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + apiKey);
        request.Headers.TryAddWithoutValidation("User-Agent", UserAgent);

        try
        {
            // 只要响应头就够判定;不读 body,避免把模型列表(以及任何回显)拉进内存。
            using HttpResponseMessage response = await client
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct)
                .ConfigureAwait(false);

            CodexAuthResult models = Classify(response.StatusCode);

            // 列模型没过就到此为止:后面那一步问的是"接受之后允不允许干活",
            // 前提不成立时问它没有意义,还白费一次请求。
            if (models.Outcome != CodexAuthOutcome.Authorized)
            {
                return models;
            }

            // **列得出模型 ≠ 跑得动模型。** 这一步是审计 A6 补上的:
            // 列模型往往是低权限路由,真正决定"能不能干活"的是推理路由。
            return await ProbeInferenceAsync(client, baseUrl, apiKey, codexHome, ct).ConfigureAwait(false);
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            return new CodexAuthResult(CodexAuthOutcome.NetworkFailed, "请求超时");
        }
        catch (HttpRequestException ex)
        {
            // DNS/TCP/TLS 失败是**本地失败**,不是授权失败 —— 两者混为一谈,
            // 断网时面板会红着说"认证被拒",把人往完全错误的方向带。
            return new CodexAuthResult(CodexAuthOutcome.NetworkFailed, "网络不可达:" + ex.Message);
        }
        finally
        {
            request.Dispose();
        }
    }

    /// <summary>
    /// 最小推理探测:<c>max_tokens = 1</c>、一个字符的提示词。
    ///
    /// 为什么可以每次都跑:它是**个位数 token**。此前唯一能证明推理权限的办法是
    /// <c>codex exec</c> —— 实测 10-12 秒、23,220 tokens,贵到只能当"深探测"偶尔用,
    /// 结果就是平时那个绿灯什么也没验证。一次几 token 的请求让"每次探测都是真的"重新成立。
    /// </summary>
    private static async Task<CodexAuthResult> ProbeInferenceAsync(
        HttpClient client, string baseUrl, string apiKey, string? codexHome, CancellationToken ct)
    {
        string home = codexHome ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex");
        string? model = TryReadModel(Path.Combine(home, "config.toml"));

        if (string.IsNullOrWhiteSpace(model))
        {
            // 不知道该用哪个模型就别猜一个。说清"验到哪一步"比给个漂亮结论重要。
            return new CodexAuthResult(
                CodexAuthOutcome.Authorized, "可用 · 凭据已验证(配置里没写 model,推理权限未核实)");
        }

        using var body = new StringContent(
            JsonSerializer.Serialize(new
            {
                model,
                max_tokens = 1,
                messages = new[] { new { role = "user", content = "1" } },
            }),
            System.Text.Encoding.UTF8,
            "application/json");

        using var request = new HttpRequestMessage(
            HttpMethod.Post, baseUrl.TrimEnd('/') + "/v1/chat/completions") { Content = body };
        request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + apiKey);
        request.Headers.TryAddWithoutValidation("User-Agent", UserAgent);

        try
        {
            using HttpResponseMessage response = await client
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct)
                .ConfigureAwait(false);
            return ClassifyInference(response.StatusCode);
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            // 凭据这一关已经过了,只是推理那一步没走完 —— 不能因此把凭据判成坏的。
            return new CodexAuthResult(
                CodexAuthOutcome.Authorized, "可用 · 凭据已验证(推理探测超时,推理权限未核实)");
        }
        catch (HttpRequestException ex)
        {
            // 措辞与其它降级分支保持一致:凡是没验到推理的,都必须出现"未核实"。
            // 说法不统一等于让用户逐句去猜哪句代表"验过了"。
            return new CodexAuthResult(
                CodexAuthOutcome.Authorized, "可用 · 凭据已验证(推理权限未核实:" + ex.Message + ")");
        }
    }
}
