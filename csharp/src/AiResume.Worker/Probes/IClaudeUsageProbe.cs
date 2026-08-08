using AiResume.Core;

namespace AiResume.Worker.Probes;

/// <summary>
/// Claude 限额探测最小接口。存在的唯一理由是让续跑引擎依赖抽象而非
/// <see cref="ClaudeCodeProbe"/> 具体类型,从而能在测试中注入不起真实进程的替身
/// (红线:测试绝不对真实项目/会话启动 AI)。
/// </summary>
public interface IClaudeUsageProbe
{
    Task<ClaudeProbeResult> ProbeAsync(string model, string workingDirectory, CancellationToken cancellationToken = default);
}
