using AiResume.Core;
using AiResume.Core.Contracts;
using AiResume.Worker.Supervision;
using Xunit;

namespace AiResume.Tests;

/// <summary>
/// 回收判据单测(S10-O/P2):登记三态核验是「允许终止进程」的唯一闸门。
/// 对应红线(CLAUDE.md 测试红线):「只有父 agent PID、PID、5 秒内启动时间、
/// provider 命令签名都匹配时才能回收,禁止只凭 PID 杀进程」。
/// 核心反例:PID 被系统复用给了别的进程(同 PID、启动时间差很远)→ 绝不能判 Matched。
/// 已知缺口(见 docs/RED-LINE-COVERAGE.md):本判定函数不含父 PID 核验一项,
/// 四项全匹配在实现层是三匹配;父 PID 只存于登记表,未参与判定。
/// </summary>
public class ProcessVerifierTests
{
    private static readonly DateTimeOffset Baseline = new(2026, 8, 6, 12, 0, 0, TimeSpan.Zero);

    private static ProcessRegistryEntry Entry(DateTimeOffset? startedAt = null, string? signature = null) => new(
        RunId: RunId.New(),
        ParentPid: 1111,
        ChildPid: 2222,
        JobId: null,
        StartedAt: startedAt ?? Baseline,
        CommandSignature: signature ?? ProcessSignature.Compute("claude.exe"),
        UpdatedAt: Baseline);

    private static ProcessProbeResult Alive(DateTimeOffset? startedAt, string? exePath) =>
        new(ProcessLiveness.Alive, startedAt, exePath);

    [Fact]
    public void Pid_reused_long_after_registration_is_mismatched_never_matched()
    {
        // 登记时间 12:00,系统把同一 PID 复用给了 3 小时后的新进程:
        // 启动时间差远超 ±5s 容差 → Mismatched(只删登记不终止),绝不 Matched。
        var entry = Entry();
        var probe = Alive(Baseline.AddHours(3), "claude.exe");

        Assert.Equal(ProcessVerdict.Mismatched, ProcessVerifier.Verify(entry, probe));
    }

    [Fact]
    public void Same_pid_but_different_command_signature_is_mismatched()
    {
        // 启动时间吻合但 exe 不同(PID 复用给别的程序)→ Mismatched。
        var entry = Entry();
        var probe = Alive(Baseline.AddSeconds(2), "notepad.exe");

        Assert.Equal(ProcessVerdict.Mismatched, ProcessVerifier.Verify(entry, probe));
    }

    [Fact]
    public void Match_within_tolerance_requires_time_and_signature_both()
    {
        var entry = Entry();
        // ±5s 容差内 + 签名一致 → 唯一允许终止的状态。
        Assert.Equal(ProcessVerdict.Matched,
            ProcessVerifier.Verify(entry, Alive(Baseline.AddSeconds(4.9), "claude.exe")));
        Assert.Equal(ProcessVerdict.Matched,
            ProcessVerifier.Verify(entry, Alive(Baseline.AddSeconds(-4.9), "claude.exe")));
        // 刚出容差(5.1s)→ Mismatched,不给模糊空间。
        Assert.Equal(ProcessVerdict.Mismatched,
            ProcessVerifier.Verify(entry, Alive(Baseline.AddSeconds(5.1), "claude.exe")));
    }

    [Fact]
    public void Alive_but_feature_missing_is_unverifiable_fail_closed()
    {
        // 进程活着但读不到启动时间或 exe 路径:特征缺一即不可核验,保留登记不动作。
        var entry = Entry();
        Assert.Equal(ProcessVerdict.Unverifiable, ProcessVerifier.Verify(entry, Alive(null, "claude.exe")));
        Assert.Equal(ProcessVerdict.Unverifiable, ProcessVerifier.Verify(entry, Alive(Baseline, null)));
        Assert.Equal(ProcessVerdict.Unverifiable, ProcessVerifier.Verify(entry, Alive(Baseline, "")));
    }

    [Fact]
    public void Probe_unknown_liveness_is_unverifiable_not_gone()
    {
        // 查询本身失败 ≠ 进程消失:不得当成 Gone 去清理(否则误杀在跑的子进程)。
        var entry = Entry();
        var probe = new ProcessProbeResult(ProcessLiveness.Unknown, null, null);

        Assert.Equal(ProcessVerdict.Unverifiable, ProcessVerifier.Verify(entry, probe));
    }

    [Fact]
    public void Explicitly_gone_process_is_gone()
    {
        var entry = Entry();
        var probe = new ProcessProbeResult(ProcessLiveness.Gone, null, null);

        Assert.Equal(ProcessVerdict.Gone, ProcessVerifier.Verify(entry, probe));
    }
}
