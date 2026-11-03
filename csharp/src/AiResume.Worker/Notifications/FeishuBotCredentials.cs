using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using AiResume.Secrets;

namespace AiResume.Worker.Notifications;

/// <summary>
/// 飞书通知机器人的应用凭据(app_id + app_secret),供 S4U Worker 换成 tenant_access_token 后交给 lark-cli。
///
/// **为什么要有这份副本**(2026-10-30 体检实证):Worker 跑在 S4U 计划任务的会话 0,lark-cli 1.0.81 每次
/// <c>--as bot</c> 都要用 CurrentUser DPAPI 解密自己存在 HKCU 钥匙串里的 appSecret,这在 S4U 下间歇失败
/// (0x8009000B),10-14 以来 96% 的投递失败都是它。所以这里存一份 **LocalMachine** 范围的 DPAPI 密文
/// (文件 ACL 收紧到当前用户 + SYSTEM + Administrators),只在交互式 GUI 里写入(用户输入或从旧版导入),Worker 只读。
///
/// **App Secret 不交给 lark-cli**(ADR-0007,2026-11-02 用假值实验查证):lark-cli 的 env 凭据来源只接受现成令牌
/// (<c>LARKSUITE_CLI_TENANT_ACCESS_TOKEN</c>),给它 <c>LARKSUITE_CLI_APP_SECRET</c> 不会去换，必报
/// <c>no tat available from credential source "env"</c>。v2.2 当初「本机 dry-run 证实」的说法不成立:dry-run 不换令牌。
/// 现在由 <see cref="FeishuTenantToken"/> 用这份凭据换令牌，只把 App ID + 令牌注入子进程;写入前同样经
/// 真实换令牌 + 一次只读机器人接口调用核验。
///
/// 实值只出现在：用户输入那一刻、本类调用栈内、发往飞书换令牌端点的请求体。绝不进日志、应答、argv、子进程环境。
/// </summary>
public sealed class FeishuBotCredentials
{
    public const string CredentialRef = "feishu-bot";

    /// <summary>v2.1 及更早由「飞书接入」保存的 CurrentUser 密文(含 allow_from),只用于一次性导入。</summary>
    public const string LegacyCredentialRef = "feishu-platform";

    private readonly DpapiSecretStore _store;
    private readonly string _root;

    public FeishuBotCredentials(string? secretsRoot = null)
    {
        _root = secretsRoot ?? ShadowPaths.SecretsRoot;
        _store = new DpapiSecretStore(_root);
    }

    public string SecretPath => Path.Combine(_root, "secrets", CredentialRef + ".bin");

    public bool Exists => File.Exists(SecretPath);

    public bool LegacyExists => File.Exists(Path.Combine(_root, "secrets", LegacyCredentialRef + ".bin"));

    /// <summary>只给界面看的遮蔽 app_id(cli_ab…ef);没有凭据或读不出时为 null。</summary>
    public string? MaskedAppId() => TryLoad(out string appId, out _) ? Mask(appId) : null;

    public static string Mask(string appId) =>
        appId.Length <= 8 ? appId[..Math.Min(4, appId.Length)] + "…" : appId[..6] + "…" + appId[^2..];

    public void Save(string appId, string appSecret)
    {
        appId = appId.Trim();
        appSecret = appSecret.Trim();
        if (!IsValidAppId(appId))
        {
            throw new ArgumentException("飞书 App ID 格式不对(应为 cli_ 开头的字母数字)。", nameof(appId));
        }

        if (appSecret.Length is < 8 or > 256 || appSecret.Any(char.IsWhiteSpace))
        {
            throw new ArgumentException("飞书 App Secret 格式不对。", nameof(appSecret));
        }

        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(new Payload(appId, appSecret));
        try
        {
            _store.SaveAsync(CredentialRef, payload, DpapiScope.LocalMachine, CancellationToken.None).GetAwaiter().GetResult();
        }
        finally
        {
            CryptographicOperations.ZeroMemory(payload);
        }
    }

    public bool TryLoad(out string appId, out string appSecret) => TryLoad(CredentialRef, out appId, out appSecret);

    /// <summary>读旧版「飞书接入」保存的凭据(CurrentUser DPAPI,只在交互会话里可靠)。</summary>
    public bool TryLoadLegacy(out string appId, out string appSecret) => TryLoad(LegacyCredentialRef, out appId, out appSecret);

    public void Delete() => _store.DeleteAsync(CredentialRef, CancellationToken.None).GetAwaiter().GetResult();

    public static bool IsValidAppId(string appId) =>
        appId.StartsWith("cli_", StringComparison.Ordinal) && appId.Length is > 4 and <= 64 &&
        appId.All(c => char.IsAsciiLetterOrDigit(c) || c == '_');

    private bool TryLoad(string credentialRef, out string appId, out string appSecret)
    {
        appId = string.Empty;
        appSecret = string.Empty;
        byte[]? plaintext = null;
        try
        {
            plaintext = _store.LoadAsync(credentialRef, CancellationToken.None).GetAwaiter().GetResult();
            Payload? payload = JsonSerializer.Deserialize<Payload>(plaintext);
            if (payload is null || !IsValidAppId(payload.AppId ?? string.Empty) || string.IsNullOrWhiteSpace(payload.AppSecret))
            {
                return false;
            }

            appId = payload.AppId!;
            appSecret = payload.AppSecret!;
            return true;
        }
        catch (Exception ex) when (ex is KeyNotFoundException or CryptographicException or JsonException or IOException or
                                   UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            return false;
        }
        finally
        {
            if (plaintext is not null)
            {
                CryptographicOperations.ZeroMemory(plaintext);
            }
        }
    }

    // 旧版 FeishuCredentialStore 用默认(PascalCase)属性名序列化 {AppId, AppSecret, AllowFrom};新旧两份共用同一形状。
    private sealed record Payload(
        [property: JsonPropertyName("AppId")] string? AppId,
        [property: JsonPropertyName("AppSecret")] string? AppSecret);
}
