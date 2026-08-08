using AiResume.Worker.Notifications;
using Xunit;

namespace AiResume.Tests;

/// <summary>
/// 「已启用」到底能不能兑现。
///
/// 2026-08-08 第二轮审计把 <c>AiResume.Hook.exe</c> 挪走,
/// <c>notify list</c> 与界面开关照旧显示已启用(A1)——判据只看配置、不看世界。
/// 这一组把那一步补齐,并且把两种错法都钉死:
/// 漏判(等于没做)和误判(把用户能用的配置说成坏的)。
/// </summary>
public sealed class HookHealthTests
{
    [Fact]
    public void 按exe边界切不按空格()
    {
        // 安装目录叫 "AI Resume"。按空格切会得到 "C:\…\Local\AI",
        // 这个错误本身就造成过一次事故(notify 链套到 8 层、9909 字符)。
        Assert.Equal(
            @"C:\Users\me\AppData\Local\AI Resume\AiResume.Hook.exe",
            HookHealth.ExtractExe(@"C:\Users\me\AppData\Local\AI Resume\AiResume.Hook.exe claudecode"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("node /path/to/hook.js")]   // 不是 exe 形式
    public void 取不出可执行文件就返回null(string? command)
    {
        Assert.Null(HookHealth.ExtractExe(command));
    }

    [Fact]
    public void 文件不在就是断链()
    {
        Assert.True(HookHealth.IsBroken(@"C:\gone\AiResume.Hook.exe codex", _ => false));
    }

    [Fact]
    public void 文件在就不是断链()
    {
        Assert.False(HookHealth.IsBroken(@"C:\here\AiResume.Hook.exe codex", _ => true));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("node hook.js")]
    public void 核对不了不等于坏了(string? command)
    {
        // 把未知说成故障,和把故障说成正常一样是在骗人,只是方向相反。
        Assert.False(HookHealth.IsBroken(command, _ => false));
    }

    [Fact]
    public void 判定异常时不冒充结论()
    {
        Assert.False(HookHealth.IsBroken(
            @"C:\x\AiResume.Hook.exe cline", _ => throw new UnauthorizedAccessException()));
    }

    [Fact]
    public void 断链说明必须写出后果()
    {
        string detail = HookHealth.BrokenDetail(@"C:\gone\AiResume.Hook.exe codex");

        // 只说"路径不存在"读起来像个无关紧要的警告。
        Assert.Contains(@"C:\gone\AiResume.Hook.exe", detail);
        Assert.Contains("通知永远不会到", detail);
    }

    [Fact]
    public void 已启用但断链时注册表改写探测结果()
    {
        var registry = new NotificationRegistry(
            new INotificationAdapter[] { new StubAdapter(enabled: true, command: @"C:\gone\AiResume.Hook.exe codex") },
            fileExists: _ => false);

        NotificationProviderStatus s = registry.ProbeAll().Single();

        Assert.True(s.IsEnabled);          // 配置里确实有这条
        Assert.True(s.HookBroken);         // 但它执行不了
        Assert.Contains("通知永远不会到", s.Detail);
    }

    [Fact]
    public void 未启用时不去标断链()
    {
        // 未启用本来就收不到通知,再标一次只会制造噪音。
        var registry = new NotificationRegistry(
            new INotificationAdapter[] { new StubAdapter(enabled: false, command: null) },
            fileExists: _ => false);

        Assert.False(registry.ProbeAll().Single().HookBroken);
    }

    [Fact]
    public void cline的wrapper脚本能反解回命令()
    {
        string script = ClineNotificationAdapter.BuildWrapperScript(
            @"C:\Program Files\AI Resume\AiResume.Hook.exe", previousPath: string.Empty);

        Assert.Equal(
            @"C:\Program Files\AI Resume\AiResume.Hook.exe",
            ClineNotificationAdapter.ParseHookCommand(script));
    }

    [Fact]
    public void opencode的插件源码能反解回命令()
    {
        string source = OpenCodeNotificationAdapter.BuildPluginSource(
            @"C:\Users\me\AppData\Local\AI Resume\AiResume.Hook.exe opencode");

        Assert.Equal(
            @"C:\Users\me\AppData\Local\AI Resume\AiResume.Hook.exe opencode",
            OpenCodeNotificationAdapter.ParseHookCommand(source));
    }

    [Fact]
    public void 反解不出来时返回null而不是残缺路径()
    {
        Assert.Null(ClineNotificationAdapter.ParseHookCommand("# 用户自己写的脚本\nWrite-Output 'hi'"));
        Assert.Null(OpenCodeNotificationAdapter.ParseHookCommand("export default () => {};"));
    }

    [Fact]
    public void codex条目被别人包住时要剥到里层()
    {
        // 2026-08-08 本机实测:Codex 自己的 codex-computer-use.exe 占了 notify 的第 0 位,
        // 把我们整个塞进它的 --previous-notify 里。于是含标记的那一项是一段 JSON 数组文本。
        // 直接当路径用会得到 ["C:\…\AiResume.Hook.exe 这种带方括号的残缺路径,
        // 面板红着说"钩子断链"而钩子其实好好的 —— 误判比漏判更糟。
        const string nested =
            """["C:\\Users\\me\\AppData\\Local\\AI Resume\\AiResume.Hook.exe","codex","--previous-notify","[]"]""";

        Assert.Equal(
            @"C:\Users\me\AppData\Local\AI Resume\AiResume.Hook.exe",
            CodexNotificationAdapter.ResolveOwnCommand(nested));
    }

    [Fact]
    public void codex条目在最外层时原样返回()
    {
        Assert.Equal(
            @"C:\x\AiResume.Hook.exe",
            CodexNotificationAdapter.ResolveOwnCommand(@"C:\x\AiResume.Hook.exe"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(@"C:\other\some-notify.exe")]        // 不含标记 = 不是我们的
    [InlineData("[不是合法 JSON")]                     // 形状认不出
    public void codex剥不动时返回null不冒充结论(string? element)
    {
        Assert.Null(CodexNotificationAdapter.ResolveOwnCommand(element));
    }

    [Fact]
    public void codex嵌套过深时停下而不是无限递归()
    {
        string s = @"C:\x\AiResume.Hook.exe";
        for (int i = 0; i < 12; i++)
        {
            s = System.Text.Json.JsonSerializer.Serialize(new[] { s, "codex" });
        }

        Assert.Null(CodexNotificationAdapter.ResolveOwnCommand(s));
    }

    private sealed class StubAdapter : INotificationAdapter
    {
        private readonly bool _enabled;
        private readonly string? _command;

        public StubAdapter(bool enabled, string? command)
        {
            _enabled = enabled;
            _command = command;
        }

        public NotificationProviderKind Kind => NotificationProviderKind.Codex;

        public string DisplayName => "Codex";

        public NotificationProviderStatus Probe() => new(
            Kind, DisplayName, IsInstalled: true, IsEnabled: _enabled,
            ConfigPath: "cfg", Detail: "已安装 AI Resume 通知钩子", HookCommand: _command);

        public void Enable(string hookCommand)
        {
        }

        public void Disable()
        {
        }
    }
}
