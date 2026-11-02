namespace AiResume.LarkCli;

/// <summary>一次 lark-cli 调用的结构化结果。所有文本字段已经过脱敏,可直接用于日志/展示。</summary>
public sealed class LarkCliResult
{
    public int ExitCode { get; }

    /// <summary>脱敏后的 stdout。</summary>
    public string Stdout { get; }

    /// <summary>脱敏后的 stderr。</summary>
    public string Stderr { get; }

    /// <summary>从脱敏后 stdout 解析的信封;输出非 JSON 时为 null。</summary>
    public LarkEnvelope? Envelope { get; }

    /// <summary>
    /// 退出码非 0 时的错误信封：lark-cli 把它写在 **stderr**,stdout 只作兜底。
    /// 退出码为 0 时为 null。
    /// </summary>
    public LarkEnvelope? ErrorEnvelope { get; }

    public TimeSpan Duration { get; }

    public LarkCliResult(int exitCode, string stdout, string stderr, LarkEnvelope? envelope,
        LarkEnvelope? errorEnvelope = null, TimeSpan duration = default)
    {
        ExitCode = exitCode;
        Stdout = stdout;
        Stderr = stderr;
        Envelope = envelope;
        ErrorEnvelope = errorEnvelope;
        Duration = duration;
    }
}
