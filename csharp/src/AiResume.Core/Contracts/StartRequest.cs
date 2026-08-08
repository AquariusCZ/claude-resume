namespace AiResume.Core.Contracts;

/// <summary>
/// RunContract Start 请求。禁止任何总时限字段(deadline_ms/timeout_ms/timeout_minutes/静默阈值)。
/// 语义见 docs/RUN-CONTRACT.md 2.1。
/// </summary>
public sealed record StartRequest
{
    public const string ContractVersionValue = "1";

    public string ContractVersion { get; init; } = ContractVersionValue;

    /// <summary>调用方生成的幂等键;一次用户动作保持稳定。</summary>
    public Guid RequestId { get; init; }

    /// <summary>并发所有权键,由 <see cref="RunKey.Create"/> 唯一生成。</summary>
    public string RunKey { get; init; } = string.Empty;

    public TaskKind TaskKind { get; init; }

    /// <summary>open_id 或本地控制面身份。</summary>
    public string? Actor { get; init; }

    /// <summary>项目标识/路径引用;不得内联机密。</summary>
    public string? ProjectRef { get; init; }

    public string ProfileId { get; init; } = string.Empty;

    /// <summary>provider 原生 session/thread 引用。</summary>
    public object? SessionRef { get; init; }

    /// <summary>经策略校验的工作目录。</summary>
    public string? Cwd { get; init; }

    /// <summary>prompt/附件的受控存储引用;不把大文本放运行表。</summary>
    public string InputRef { get; init; } = string.Empty;

    /// <summary>DPAPI/凭据存储引用。</summary>
    public string? CredentialRef { get; init; }

    /// <summary>fallback 链关联;首次 Start 可省略。</summary>
    public Guid? AttemptGroupId { get; init; }

    /// <summary>仅显式 fallback/续跑关联。</summary>
    public Guid? ParentRunId { get; init; }

    public FallbackPolicy FallbackPolicy { get; init; } = FallbackPolicy.None;
}
