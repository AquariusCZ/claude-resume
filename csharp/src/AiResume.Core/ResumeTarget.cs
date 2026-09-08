namespace AiResume.Core;

/// <summary>
/// 一次布防固定的执行目标。只持久化身份指纹与模型 ID，绝不包含凭据。
/// 供应商、账号或端点改变时必须重新布防；目录刷新不能重写当前周期。
/// </summary>
public sealed record ResumeTarget
{
    [System.Text.Json.Serialization.JsonPropertyName("providerId")]
    public string ProviderId { get; init; } = string.Empty;
    [System.Text.Json.Serialization.JsonPropertyName("providerIdentity")]
    public string ProviderIdentity { get; init; } = string.Empty;
    [System.Text.Json.Serialization.JsonPropertyName("providerName")]
    public string ProviderName { get; init; } = string.Empty;
    [System.Text.Json.Serialization.JsonPropertyName("agent")]
    public string Agent { get; init; } = string.Empty;
    [System.Text.Json.Serialization.JsonPropertyName("modelId")]
    public string ModelId { get; init; } = string.Empty;
    [System.Text.Json.Serialization.JsonPropertyName("modelName")]
    public string ModelName { get; init; } = string.Empty;
    [System.Text.Json.Serialization.JsonPropertyName("boundAtUtc")]
    public DateTimeOffset BoundAtUtc { get; init; }

    public bool IsValid =>
        ProviderId.Length > 0 && ProviderIdentity.Length > 0 &&
        Agent is "claudecode" or "codex" && ModelId.Length > 0;

    public static string Identity(ProductConfig config) => config.ResumeTarget is { } target
        ? Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(System.Text.Json.JsonSerializer.Serialize(new[]
        {
            target.ProviderId, target.ProviderIdentity, target.Agent, target.ModelId,
            config.ResumeModel, config.ResumePrompt, config.SkipPermissions.ToString(),
            System.Text.Json.JsonSerializer.Serialize(config.Selected.Select(p => new[] { p.Path, p.SessionId })),
        }))))
        : config.ResumeModel;
}
