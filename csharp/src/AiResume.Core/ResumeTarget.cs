using System.Text.Json.Serialization;

namespace AiResume.Core;

/// <summary>
/// 一次布防固定的执行目标。只持久化身份指纹与模型 ID，绝不包含凭据。
/// 供应商、账号或端点改变时必须重新布防；目录刷新不能重写当前周期。
/// <see cref="ModelId"/> 是默认模型：项目自己的 <see cref="ProjectRef.ModelId"/> 优先。
/// </summary>
public sealed record ResumeTarget
{
    [JsonPropertyName("providerId")]
    public string ProviderId { get; init; } = string.Empty;
    [JsonPropertyName("providerIdentity")]
    public string ProviderIdentity { get; init; } = string.Empty;
    [JsonPropertyName("providerName")]
    public string ProviderName { get; init; } = string.Empty;
    [JsonPropertyName("agent")]
    public string Agent { get; init; } = string.Empty;
    [JsonPropertyName("modelId")]
    public string ModelId { get; init; } = string.Empty;
    [JsonPropertyName("modelName")]
    public string ModelName { get; init; } = string.Empty;
    [JsonPropertyName("boundAtUtc")]
    public DateTimeOffset BoundAtUtc { get; init; }

    [JsonIgnore]
    public bool IsValid =>
        ProviderId.Length > 0 && ProviderIdentity.Length > 0 &&
        Agent is "claudecode" or "codex" && ModelId.Length > 0;

    /// <summary>项目实际续跑用的模型：项目级优先，旧配置退回布防目标的默认模型。</summary>
    public static string EffectiveModel(ProjectRef project, ResumeTarget? target) =>
        project.ModelId is { Length: > 0 } model ? model : target?.ModelId ?? string.Empty;

    /// <summary>
    /// 额度授权所依据的完整执行身份。任一项目的路径、会话或模型变化都会让在途证据作废。
    /// 只在进程内比较，不落盘。
    /// </summary>
    public static string Identity(ProductConfig config) => config.ResumeTarget is { } target
        ? Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(System.Text.Json.JsonSerializer.Serialize(new[]
        {
            target.ProviderId, target.ProviderIdentity, target.Agent, target.ModelId,
            config.ResumeModel, config.ResumePrompt, config.SkipPermissions.ToString(),
            System.Text.Json.JsonSerializer.Serialize(config.Selected.Select(p => new[] { p.Path, p.SessionId, EffectiveModel(p, target) })),
        }))))
        : config.ResumeModel;

    /// <summary>
    /// 单个项目的执行身份：目标(供应商/账号/agent/默认模型)、续跑提示与权限开关，加上这个项目自己的路径、会话与有效模型。
    /// 在途续跑与最终门禁只看它——队列里别的项目增删或改模型，不影响这个项目已经取得的授权。
    /// 项目不在队列里时返回空串(与任何身份都不相等)。只在进程内比较，不落盘。
    /// </summary>
    public static string ProjectIdentity(ProductConfig config, string path)
    {
        string? wanted = SafeNormalize(path);
        ProjectRef? project = wanted is null ? null : config.Selected.FirstOrDefault(p => SafeNormalize(p.Path) == wanted);
        if (project is null)
        {
            return string.Empty;
        }

        if (config.ResumeTarget is not { } target)
        {
            // 旧配置没有固定目标:与 Identity 一样只看全局续跑模型(加上项目本身)。
            return config.ResumeModel + "|" + wanted;
        }

        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(System.Text.Json.JsonSerializer.Serialize(new[]
        {
            target.ProviderId, target.ProviderIdentity, target.Agent, target.ModelId,
            config.ResumeModel, config.ResumePrompt, config.SkipPermissions.ToString(),
            wanted, project.SessionId, EffectiveModel(project, target),
        }))));
    }

    private static string? SafeNormalize(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        try
        {
            return RunKey.NormalizeProjectPath(path);
        }
        catch (ArgumentException)
        {
            return null;
        }
    }
}
