using AiResume.Gui;
using Xunit;

namespace AiResume.Tests;

public sealed class ControlPlaneBridgeDemoTests
{
    [Theory]
    [InlineData("projects.list")]
    [InlineData("quota.local")]
    [InlineData("quota.get")]
    [InlineData("providers.probe")]
    [InlineData("arm.get")]
    [InlineData("feishu.status")]
    [InlineData("notifications.list")]
    [InlineData("app.info")]
    [InlineData("agent.get")]
    public async Task 截图模式只返回合成数据(string type)
    {
        var bridge = new ControlPlaneBridge(demoMode: true);

        string response = await bridge.HandleAsync($$"""{"id":"demo","type":"{{type}}"}""", CancellationToken.None);

        Assert.DoesNotContain(Environment.UserName, response, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), response,
            StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(".error", response, StringComparison.Ordinal);
    }
}
