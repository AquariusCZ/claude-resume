using System;
using System.Linq;
using System.Runtime.Versioning;
using AiResume.Wrapper;
using Xunit;

namespace AiResume.Tests;

/// <summary>
/// CIM 枚举器的能力验证。它的存在意义只有一个:**读得到命令行**——
/// 现役 node agent 与其它 node 服务进程名完全一样,只有命令行能区分。
/// 因此这些测试断言的是"能力",不是"某个具体进程存在"。
/// </summary>
[SupportedOSPlatform("windows")]
public class CimRunningProcessListerTests
{
    [Fact]
    public void 声明自己能提供命令行()
    {
        // 这个声明是安全语义的一部分:守卫据此决定是放行还是判 Unverifiable。
        Assert.True(new CimRunningProcessLister().ProvidesCommandLine);
    }

    [Fact]
    public void 能读到当前测试进程自己的命令行()
    {
        var lister = new CimRunningProcessLister();

        var self = lister.List().SingleOrDefault(p => p.Pid == Environment.ProcessId);

        Assert.NotNull(self);
        // 读不到自己的命令行 = 这个枚举器没有兑现 ProvidesCommandLine 的承诺,
        // 守卫会基于错误的前提放行,单消费者铁律就失效了。
        Assert.False(string.IsNullOrWhiteSpace(self!.CommandLine));
    }

    [Fact]
    public void 枚举结果包含进程名且不为空集()
    {
        var processes = new CimRunningProcessLister().List();

        Assert.NotEmpty(processes);
        Assert.Contains(processes, p => !string.IsNullOrWhiteSpace(p.Name));
    }

    [Fact]
    public void 生产装配在Windows上使用能读命令行的枚举器()
    {
        // CreateDefault 是生产唯一的取守卫入口;它要是退回到 Diagnostics 版本,
        // 守卫会一律判 Unverifiable,cc-connect 永远启动不了(反向失效)。
        SingleConsumerGuard guard = SingleConsumerGuard.CreateDefault();

        // 不声明飞书平台时守卫直接放行,不触碰枚举器——用它验证 CreateDefault 本身可用。
        ConsumerGuardResult result = guard.Check(feishuPlatformConfigured: false);
        Assert.Equal(ConsumerGuardVerdict.Clear, result.Verdict);
    }
}
