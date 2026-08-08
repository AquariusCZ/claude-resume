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

    public LarkCliResult(int exitCode, string stdout, string stderr, LarkEnvelope? envelope)
    {
        ExitCode = exitCode;
        Stdout = stdout;
        Stderr = stderr;
        Envelope = envelope;
    }
}
