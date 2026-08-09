using AiResume.Gui;
using AiResume.Worker.Probes;
using Xunit;

namespace AiResume.Tests;

public sealed class ControlPlaneBridgeProviderTests
{
    [Fact]
    public void Codex只有真实推理成功才显示绿色()
    {
        Assert.Equal("ok", ControlPlaneBridge.CodexProviderState(
            new CodexProbeResult(CodexReadiness.Ok, "authorized", "已验证", true)));
        Assert.Equal("idle", ControlPlaneBridge.CodexProviderState(
            new CodexProbeResult(CodexReadiness.Ok, "inference-unverified", "未验推理", false)));
    }
}
