namespace AiResume.LarkCli;

/// <summary>封装失败类别(S3-A 结构化错误分类)。</summary>
public enum LarkCliFailureKind
{
    /// <summary>超时未完成,进程树已终止。</summary>
    Timeout,

    /// <summary>exit 10:高风险写操作需要显式确认;提示原样透传,绝不自动 --yes。</summary>
    HighRiskConfirmationRequired,

    /// <summary>退出码 0 但 stdout 不是合法 JSON envelope。</summary>
    InvalidOutput,
}

/// <summary>lark-cli 调用失败(超时/高风险确认/非法输出)。</summary>
public sealed class LarkCliException : Exception
{
    public LarkCliFailureKind Kind { get; }

    /// <summary>脱敏后的 stdout(若可读取)。</summary>
    public string? Stdout { get; }

    /// <summary>脱敏后的 stderr(若可读取)。</summary>
    public string? Stderr { get; }

    public LarkCliException(LarkCliFailureKind kind, string message, string? stdout = null, string? stderr = null)
        : base(message)
    {
        Kind = kind;
        Stdout = stdout;
        Stderr = stderr;
    }
}
