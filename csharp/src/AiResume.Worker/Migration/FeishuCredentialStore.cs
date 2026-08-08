using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AiResume.Secrets;

namespace AiResume.Worker.Migration;

/// <summary>飞书凭据的存在性描述。**只有存在性与遮蔽后的 app_id,没有任何实值。**</summary>
public sealed record FeishuCredentialStatus(bool HasCredentials, string? AppIdMasked, string? AllowFrom);

/// <summary>
/// 飞书接入凭据(app_id + app_secret)的本机存储。
///
/// 落 DPAPI(<see cref="DpapiSecretStore"/>),按当前 Windows 用户加密——
/// 这正是 D-013「明文 secret」要清掉的债:现役把 app_secret 明文写在
/// AppDir 的 config.json 里,任何能读该文件的进程都能拿走。
///
/// **实值只在三个地方出现**:用户在 GUI 里输入的那一刻、本类的调用栈内、
/// 以及最终写出的 <c>~/.cc-connect/config.toml</c>(cc-connect 自己要读它)。
/// 绝不回传前端、绝不进日志、绝不进异常消息——
/// <see cref="Describe"/> 是唯一对外的读接口,它只说"有没有"。
/// </summary>
public sealed class FeishuCredentialStore
{
    private const string CredentialRef = "feishu-platform";

    private readonly DpapiSecretStore _store;

    public FeishuCredentialStore(string? secretsRoot = null)
        => _store = new DpapiSecretStore(secretsRoot ?? ShadowPaths.SecretsRoot);

    /// <summary>
    /// 清掉 allow_from 里的**全部空白**,不只是首尾。
    ///
    /// **2026-08-08 真事故**:用户填进来的 open_id 中间夹了一个空格
    /// (<c>ou_160a7866f6c507d6c 508896eadda3c34</c>,36 字符而不是 35),
    /// `.Trim()` 去不掉它。于是 cc-connect 拿着一个永远匹配不上的白名单跑,
    /// **每一条飞书消息都被静默丢弃** —— 日志里连一行 "message received" 都没有,
    /// 表现就是"机器人不理我",而所有健康检查全绿。
    ///
    /// open_id / union_id 里不可能有空白,所以这里剥掉是安全的;
    /// 逗号分隔的多个 id 也一并规整(去空项、去重复逗号)。
    /// **读路径也要过这一遭**,否则已经存坏的值只能靠重填修复,
    /// 而重填要用户再拿一次 app_secret。
    /// </summary>
    public static string NormalizeAllowFrom(string? allowFrom)
    {
        if (string.IsNullOrWhiteSpace(allowFrom))
        {
            return string.Empty;
        }

        IEnumerable<string> ids = allowFrom
            .Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(x => new string(x.Where(c => !char.IsWhiteSpace(c)).ToArray()))
            .Where(x => x.Length > 0);

        return string.Join(",", ids);
    }

    public void Save(string appId, string appSecret, string allowFrom)
    {
        if (string.IsNullOrWhiteSpace(appId))
        {
            throw new ArgumentException("飞书 app_id 不得为空。", nameof(appId));
        }

        if (string.IsNullOrWhiteSpace(appSecret))
        {
            throw new ArgumentException("飞书 app_secret 不得为空。", nameof(appSecret));
        }

        // fail-closed:allow_from 为空时 cc-connect 放行所有飞书用户。
        if (string.IsNullOrWhiteSpace(allowFrom))
        {
            throw new ArgumentException("授权用户(allow_from)不得为空,否则任何人都能驱动本机 AI。", nameof(allowFrom));
        }

        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(
            new Payload(appId.Trim(), appSecret.Trim(), NormalizeAllowFrom(allowFrom)));
        try
        {
            _store.SaveAsync(CredentialRef, payload, CancellationToken.None).GetAwaiter().GetResult();
        }
        finally
        {
            // 明文字节及时清零:GC 之前它会一直躺在托管堆上。
            CryptographicOperations.ZeroMemory(payload);
        }
    }

    /// <summary>读出实值。**只有需要真正写配置时才调**;调用方不得把结果放进日志或应答。</summary>
    public bool TryLoad(out string appId, out string appSecret, out string allowFrom)
    {
        appId = string.Empty;
        appSecret = string.Empty;
        allowFrom = string.Empty;

        byte[]? plaintext = null;
        try
        {
            plaintext = _store.LoadAsync(CredentialRef, CancellationToken.None).GetAwaiter().GetResult();
            Payload? payload = JsonSerializer.Deserialize<Payload>(plaintext);
            if (payload is null || string.IsNullOrWhiteSpace(payload.AppId)
                || string.IsNullOrWhiteSpace(payload.AppSecret) || string.IsNullOrWhiteSpace(payload.AllowFrom))
            {
                return false;
            }

            appId = payload.AppId;
            appSecret = payload.AppSecret;
            // 读路径也规整:已经存坏的值(中间夹空格)靠这一步就地修好,
            // 不必让用户为了改一个 open_id 再去开放平台取一次 app_secret。
            allowFrom = NormalizeAllowFrom(payload.AllowFrom);
            return string.IsNullOrEmpty(allowFrom) ? false : true;
        }
        catch (KeyNotFoundException)
        {
            return false;
        }
        catch (CryptographicException)
        {
            // 密文属于别的 Windows 用户,或已损坏。当作"没有凭据"处理,
            // 让用户重新输入——绝不把解密异常细节抛给前端。
            return false;
        }
        catch (JsonException)
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

    /// <summary>
    /// 从现役 AppDir 的 config.json 导入 <c>feishuAppId</c> / <c>feishuAppSecret</c>。
    ///
    /// 切换的前提就是"同一个飞书应用换一个消费者",凭据本来就在本机;让用户去
    /// 开放平台重新抄一遍既麻烦又多一次经手。这里是**机器到机器**的搬运:
    /// 值不显示、不返回、不入日志,只从明文 config.json 读出来立刻加密落盘。
    ///
    /// 这也是 D-013(明文 secret)的收口动作之一——导入后目标侧就不再依赖那份明文了。
    /// 只读这两个键,其余 45 个键(含 openaiApiKey 等)一律不取值。
    /// </summary>
    public void ImportFromLegacy(string legacyConfigPath)
    {
        if (!File.Exists(legacyConfigPath))
        {
            throw new FileNotFoundException("找不到现役配置文件。", legacyConfigPath);
        }

        string appId, appSecret, allowFrom;
        using (JsonDocument doc = JsonDocument.Parse(File.ReadAllText(legacyConfigPath)))
        {
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidDataException("现役配置文件顶层不是 JSON 对象。");
            }

            appId = ReadString(doc.RootElement, "feishuAppId");
            appSecret = ReadString(doc.RootElement, "feishuAppSecret");
            // 现役靠 feishuAuthOpenIds 锁定"谁能改项目";cc-connect 的对应物是
            // allow_from(逗号分隔字符串)。不搬这一项就等于把授权边界丢在迁移路上。
            allowFrom = string.Join(",", ReadStringArray(doc.RootElement, "feishuAuthOpenIds"));
        }

        if (string.IsNullOrWhiteSpace(appId) || string.IsNullOrWhiteSpace(appSecret))
        {
            // 只说缺哪个键,绝不回显读到的内容。
            throw new InvalidDataException("现役配置里没有 feishuAppId / feishuAppSecret,无法导入。");
        }

        if (string.IsNullOrWhiteSpace(allowFrom))
        {
            throw new InvalidDataException(
                "现役配置的 feishuAuthOpenIds 为空。现役语义下这表示未锁定(所有人可改),"
                + "但 cc-connect 没有『非 owner 禁文件工具』那层兜底,直接照搬会把项目目录开放给所有人。"
                + "请先在现役 GUI 里设定授权用户,或在控制面手工填写授权 open_id。");
        }

        Save(appId, appSecret, allowFrom);
    }

    private static string[] ReadStringArray(JsonElement root, string name)
        => root.TryGetProperty(name, out JsonElement el) && el.ValueKind == JsonValueKind.Array
            ? el.EnumerateArray()
                .Where(e => e.ValueKind == JsonValueKind.String)
                .Select(e => e.GetString()!)
                .Where(v => !string.IsNullOrWhiteSpace(v))
                .ToArray()
            : Array.Empty<string>();

    private static string ReadString(JsonElement root, string name)
        => root.TryGetProperty(name, out JsonElement el) && el.ValueKind == JsonValueKind.String
            ? el.GetString() ?? string.Empty
            : string.Empty;

    /// <summary>对外唯一的读接口:只回答"有没有"以及遮蔽后的 app_id。</summary>
    public FeishuCredentialStatus Describe()
        => TryLoad(out string appId, out _, out string allowFrom)
            ? new FeishuCredentialStatus(true, Mask(appId), allowFrom)
            : new FeishuCredentialStatus(false, null, null);

    public void Clear()
    {
        try
        {
            _store.DeleteAsync(CredentialRef, CancellationToken.None).GetAwaiter().GetResult();
        }
        catch (KeyNotFoundException)
        {
            // 本来就没有:清除是幂等的。
        }
    }

    /// <summary>
    /// app_id 遮蔽:保留前 8 位与后 4 位。它是标识符不是口令,但仍然能定位到具体应用,
    /// 所以界面上只给"够你认出是哪一个"的信息量。
    /// </summary>
    internal static string Mask(string appId)
    {
        if (appId.Length <= 12)
        {
            return new string('•', appId.Length);
        }

        return string.Concat(appId.AsSpan(0, 8), "…", appId.AsSpan(appId.Length - 4, 4));
    }

    private sealed record Payload(string AppId, string AppSecret, string AllowFrom);
}
