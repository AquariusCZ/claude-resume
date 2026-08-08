using System;
using AiResume.Ipc;
using Xunit;

namespace AiResume.Tests;

/// <summary>
/// pipe 名测试后缀的行为约束。
///
/// 这个后缀存在的唯一理由是**测试隔离**:pipe 名派生出单实例互斥体,
/// 测试拉起的 Worker 宿主不隔离就会和本机生产 Worker 抢同一把锁而起不来。
/// 因此这里要钉住的是它**没有削弱生产语义**——未设置时名字必须一字不差,
/// 非法取值必须直接拒绝而不是静默降级。
/// </summary>
public sealed class PipeNamingSuffixTests : IDisposable
{
    private readonly string? _original;

    public PipeNamingSuffixTests()
    {
        // 保存并清空:测试进程本身可能带着后缀跑(测试宿主注入过)。
        _original = Environment.GetEnvironmentVariable(PipeNaming.TestSuffixEnvName);
        Environment.SetEnvironmentVariable(PipeNaming.TestSuffixEnvName, null);
    }

    public void Dispose()
        => Environment.SetEnvironmentVariable(PipeNaming.TestSuffixEnvName, _original);

    [Fact]
    public void 未设置后缀时生产名一字不变()
    {
        string baseName = PipeNaming.ComputePipeName("S-1-5-21-fake-sid");

        // 这是生产路径:名字必须完全等于 SID 派生值,不能被任何"默认后缀"污染。
        Assert.Equal(baseName, PipeNaming.ApplyTestSuffix(baseName));
    }

    [Fact]
    public void 空字符串视同未设置()
    {
        Environment.SetEnvironmentVariable(PipeNaming.TestSuffixEnvName, "");
        string baseName = PipeNaming.ComputePipeName("S-1-5-21-fake-sid");

        Assert.Equal(baseName, PipeNaming.ApplyTestSuffix(baseName));
    }

    [Fact]
    public void 合法后缀被追加且保留原名前缀()
    {
        Environment.SetEnvironmentVariable(PipeNaming.TestSuffixEnvName, "abc123DEF");
        string baseName = PipeNaming.ComputePipeName("S-1-5-21-fake-sid");

        string withSuffix = PipeNaming.ApplyTestSuffix(baseName);

        Assert.Equal(baseName + "-abc123DEF", withSuffix);
        // 与生产名不同,才谈得上隔离。
        Assert.NotEqual(baseName, withSuffix);
    }

    [Theory]
    [InlineData("has space")]
    [InlineData("has\\backslash")]
    [InlineData("has/slash")]
    [InlineData("has-dash")]      // 连字符也不放行:分隔符只由实现自己加
    [InlineData("有中文")]
    public void 非法后缀直接拒绝而不是静默忽略(string bad)
    {
        Environment.SetEnvironmentVariable(PipeNaming.TestSuffixEnvName, bad);
        string baseName = PipeNaming.ComputePipeName("S-1-5-21-fake-sid");

        // **必须抛而不是回退到生产名**:静默忽略会让人以为隔离生效了,
        // 实际两个宿主又挤在同一个名字上,失败现象和现在一模一样。
        Assert.Throws<InvalidOperationException>(() => PipeNaming.ApplyTestSuffix(baseName));
    }

    [Fact]
    public void 过长后缀被拒绝()
    {
        Environment.SetEnvironmentVariable(PipeNaming.TestSuffixEnvName, new string('a', 33));
        string baseName = PipeNaming.ComputePipeName("S-1-5-21-fake-sid");

        Assert.Throws<InvalidOperationException>(() => PipeNaming.ApplyTestSuffix(baseName));
    }

    [Fact]
    public void 环境变量名带TEST标记以免被误当成生产配置()
    {
        // 这条约束是刻意的:变量名可全仓 grep,评审时一眼能看出它不属于生产配置面。
        Assert.Contains("TEST", PipeNaming.TestSuffixEnvName, StringComparison.Ordinal);
    }
}
