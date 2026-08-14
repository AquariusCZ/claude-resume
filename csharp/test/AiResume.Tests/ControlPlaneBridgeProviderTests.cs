using System.Text.Json;
using AiResume.Core;
using AiResume.Gui;
using AiResume.Storage;
using AiResume.Worker.Notifications;
using AiResume.Worker.Probes;
using AiResume.Worker.Products;
using AiResume.Worker.Quota;
using Xunit;

namespace AiResume.Tests;

public sealed class ControlPlaneBridgeProviderTests : IDisposable
{
    private readonly string _root = TestTemp.NewDir("provider-bridge");

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_root, recursive: true); } catch (Exception) { }
    }

    [Fact]
    public void Codex真实推理成功或Sub2API正余额均显示绿色()
    {
        Assert.Equal("ok", ControlPlaneBridge.CodexProviderState(
            new CodexProbeResult(CodexReadiness.Ok, "authorized", "已验证", true)));
        Assert.Equal("idle", ControlPlaneBridge.CodexProviderState(
            new CodexProbeResult(CodexReadiness.Ok, "inference-unverified", "未验推理", false)));
        Assert.Equal("ok", ControlPlaneBridge.CodexProviderState(
            new CodexProbeResult(CodexReadiness.Ok, "inference-unverified", "未验推理", false),
            new CodexBalanceResult(ProviderReadiness.Ok, "ok", "余额 8 USD", 8m, "USD")));
        Assert.Equal("bad", ControlPlaneBridge.CodexProviderState(
            new CodexProbeResult(CodexReadiness.Auth, "auth-rejected", "凭据被拒", true),
            new CodexBalanceResult(ProviderReadiness.Ok, "ok", "余额 8 USD", 8m, "USD")));
        Assert.Equal("ok", ControlPlaneBridge.CodexProviderState(
            new CodexProbeResult(CodexReadiness.Unreachable, "server-error", "models 暂时不可达", false),
            new CodexBalanceResult(ProviderReadiness.Ok, "ok", "余额 8 USD", 8m, "USD")));
    }

    [Fact]
    public void 刷新期间切换provider时状态与文案都必须说配置已切换()
    {
        var codexA = new CodexProbeResult(
            CodexReadiness.Ok, "inference-unverified", "未验推理", false, "identity-A");
        var balanceB = new CodexBalanceResult(
            ProviderReadiness.Ok, "ok", "余额 8 USD", 8m, "USD", "identity-B");

        Assert.Equal("idle", ControlPlaneBridge.CodexProviderState(codexA, balanceB));

        // 状态灰了但文案还显示着另一个 provider 的余额,等于用 A 的灯照 B 的数。
        string text = InvokeProviderText(codexA, balanceB);
        Assert.Equal("配置已切换", text);
        Assert.DoesNotContain("8 USD", text, StringComparison.Ordinal);
    }

    [Fact]
    public void 限流与CDN拦截归琥珀但不压过本轮deep成功()
    {
        var shallow = new CodexProbeResult(
            CodexReadiness.Ok, "inference-unverified", "未验推理", false);
        var deepOk = new CodexProbeResult(CodexReadiness.Ok, "authorized", "已验证", true);
        var throttled = new CodexBalanceResult(
            ProviderReadiness.Unknown, "http-429", "余额接口被限流", null, null);
        var blocked = new CodexBalanceResult(
            ProviderReadiness.Unknown, "cdn-blocked", "余额接口被 CDN 拦截(非凭据问题)", null, null);

        Assert.Equal("wait", ControlPlaneBridge.CodexProviderState(shallow, throttled));
        Assert.Equal("wait", ControlPlaneBridge.CodexProviderState(shallow, blocked));
        Assert.Equal("ok", ControlPlaneBridge.CodexProviderState(deepOk, throttled));
        Assert.Equal("ok", ControlPlaneBridge.CodexProviderState(deepOk, blocked));
        Assert.Equal("CDN 拦截", InvokeProviderText(shallow, blocked));
    }

    private static string InvokeProviderText(CodexProbeResult codex, CodexBalanceResult balance) =>
        (string)typeof(ControlPlaneBridge)
            .GetMethod(
                "CodexProviderText",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!
            .Invoke(null, new object[] { codex, balance })!;

    [Fact]
    public void 余额明确失败优先于上一枪deep成功()
    {
        var deepOk = new CodexProbeResult(CodexReadiness.Ok, "authorized", "已验证", true);

        Assert.Equal("bad", ControlPlaneBridge.CodexProviderState(
            deepOk,
            new CodexBalanceResult(ProviderReadiness.Insufficient, "empty", "余额 0 USD", 0m, "USD")));
        Assert.Equal("bad", ControlPlaneBridge.CodexProviderState(
            deepOk,
            new CodexBalanceResult(ProviderReadiness.Insufficient, "invalid", "账户不可用", 8m, "USD")));
        Assert.Equal("bad", ControlPlaneBridge.CodexProviderState(
            deepOk,
            new CodexBalanceResult(ProviderReadiness.Auth, "http-401", "凭据被拒", null, null)));
        Assert.Equal("bad", ControlPlaneBridge.CodexProviderState(
            deepOk,
            new CodexBalanceResult(ProviderReadiness.Insufficient, "http-402", "余额不足", null, null)));
        Assert.Equal("wait", ControlPlaneBridge.CodexProviderState(
            deepOk,
            new CodexBalanceResult(ProviderReadiness.Insufficient, "http-429", "余额限流", null, null)));
    }

    [Fact]
    public void 未安装Codex和跨Provider证据都不能被正余额点绿()
    {
        Assert.Equal("bad", ControlPlaneBridge.CodexProviderState(
            new CodexProbeResult(CodexReadiness.NoCli, "no-cli", "未安装", false, "same"),
            new CodexBalanceResult(ProviderReadiness.Ok, "ok", "余额 8 USD", 8m, "USD", "same")));

        Assert.Equal("idle", ControlPlaneBridge.CodexProviderState(
            new CodexProbeResult(CodexReadiness.Ok, "inference-unverified", "未验推理", false, "provider-a"),
            new CodexBalanceResult(ProviderReadiness.Ok, "ok", "余额 8 USD", 8m, "USD", "provider-b")));
    }

    [Fact]
    public void 最近成功余额显示琥珀而不是实时绿色()
    {
        Assert.Equal("wait", ControlPlaneBridge.CodexProviderState(
            new CodexProbeResult(CodexReadiness.Unreachable, "server-error", "网络抖动", false, "same"),
            new CodexBalanceResult(
                ProviderReadiness.Ok,
                "stale-timeout",
                "最近余额 8 USD；本次探测超时",
                8m,
                "USD",
                "same",
                IsStale: true)));
    }

    [Fact]
    public async Task Codex余额不足与临时限流使用不同状态和短标签()
    {
        var insufficient = new CodexProbeResult(
            CodexReadiness.Limited, "http-402", "余额不足或需充值", true);
        var throttled = new CodexProbeResult(
            CodexReadiness.Limited, "http-429", "被限流", true);

        Assert.Equal("bad", ControlPlaneBridge.CodexProviderState(insufficient));
        Assert.Equal("wait", ControlPlaneBridge.CodexProviderState(throttled));

        CodexBalanceResult unavailableBalance = new(
            ProviderReadiness.Unknown, "no-config", "余额未探测", null, null);
        JsonElement insufficientPayload = await ProbeAsync(
            CreateBridge(insufficient, unavailableBalance, out _), deep: true);
        JsonElement throttledPayload = await ProbeAsync(
            CreateBridge(throttled, unavailableBalance, out _), deep: true);

        Assert.Equal("余额不足", FindCodex(insufficientPayload).GetProperty("text").GetString());
        Assert.Equal("被限流", FindCodex(throttledPayload).GetProperty("text").GetString());
    }

    [Theory]
    [InlineData("http-402", "余额不足")]
    [InlineData("http-429", "余额限流")]
    public async Task 余额接口额度状态同时进入主文案和可用性灯(string reason, string expectedText)
    {
        ControlPlaneBridge bridge = CreateBridge(
            new CodexProbeResult(CodexReadiness.Ok, "inference-unverified", "推理未核实", false),
            new CodexBalanceResult(
                ProviderReadiness.Insufficient,
                reason,
                reason == "http-402" ? "余额不足或需充值" : "余额接口被限流",
                null,
                null),
            out _);

        JsonElement codex = FindCodex(await ProbeAsync(bridge, deep: false));

        Assert.Equal(reason == "http-429" ? "wait" : "bad", codex.GetProperty("state").GetString());
        Assert.Equal(expectedText, codex.GetProperty("text").GetString());
    }

    [Fact]
    public async Task 无效账户的正余额不能成为主文案()
    {
        ControlPlaneBridge bridge = CreateBridge(
            new CodexProbeResult(CodexReadiness.Ok, "inference-unverified", "凭据已验证 · 推理未核实", false),
            new CodexBalanceResult(
                ProviderReadiness.Insufficient,
                "invalid",
                "账户不可用(余额 19.5 USD)",
                19.5m,
                "USD"),
            out _);

        JsonElement payload = await ProbeAsync(bridge, deep: false);
        JsonElement codex = FindCodex(payload);

        Assert.Equal("bad", codex.GetProperty("state").GetString());
        Assert.Equal("账户不可用", codex.GetProperty("text").GetString());
        Assert.Contains("账户不可用", codex.GetProperty("detail").GetString());
    }

    [Fact]
    public async Task 有效零余额可以显示数字但不能把可用性染绿()
    {
        ControlPlaneBridge bridge = CreateBridge(
            new CodexProbeResult(CodexReadiness.Ok, "inference-unverified", "推理未核实", false),
            new CodexBalanceResult(ProviderReadiness.Insufficient, "empty", "余额 0 USD", 0m, "USD"),
            out _);

        JsonElement payload = await ProbeAsync(bridge, deep: false);
        JsonElement codex = FindCodex(payload);

        Assert.Equal("bad", codex.GetProperty("state").GetString());
        Assert.Equal("0 USD", codex.GetProperty("text").GetString());
    }

    [Fact]
    public async Task deep标志原样传给Codex探针且最终JSON标记deep()
    {
        ControlPlaneBridge bridge = CreateBridge(
            new CodexProbeResult(CodexReadiness.Ok, "authorized", "推理已验证", true),
            new CodexBalanceResult(ProviderReadiness.Ok, "ok", "余额 8 USD", 8m, "USD"),
            out List<bool> deepCalls);

        JsonElement payload = await ProbeAsync(bridge, deep: true);

        Assert.Equal(new[] { true }, deepCalls);
        Assert.True(payload.GetProperty("deep").GetBoolean());
        JsonElement codex = FindCodex(payload);
        Assert.Equal("ok", codex.GetProperty("state").GetString());
        Assert.Equal("8 USD", codex.GetProperty("text").GetString());
    }

    [Fact]
    public async Task 一轮刷新只读取一次Provider并把同一快照交给两个探针()
    {
        int snapshotReads = 0;
        var seen = new List<string?>();
        ControlPlaneBridge bridge = CreateBridge(
            new CodexProbeResult(CodexReadiness.Ok, "inference-unverified", "unused", false),
            new CodexBalanceResult(ProviderReadiness.Ok, "ok", "unused", 8m, "USD"),
            out _,
            codexProbeOverride: (provider, _, _) =>
            {
                seen.Add("probe:" + provider.ProviderId);
                return Task.FromResult(new CodexProbeResult(
                    CodexReadiness.Ok,
                    "inference-unverified",
                    "未验推理",
                    false));
            },
            balanceProbeOverride: (provider, _) =>
            {
                seen.Add("balance:" + provider.ProviderId);
                return Task.FromResult(new CodexBalanceResult(
                    ProviderReadiness.Ok,
                    "ok",
                    "余额 8 USD",
                    8m,
                    "USD"));
            },
            providerSnapshotOverride: () =>
            {
                snapshotReads++;
                return FakeProvider(snapshotReads == 1 ? "provider-a" : "provider-b");
            });

        JsonElement codex = FindCodex(await ProbeAsync(bridge, deep: false));

        Assert.Equal(1, snapshotReads);
        Assert.Equal(new[] { "balance:provider-a", "probe:provider-a" }, seen.OrderBy(x => x, StringComparer.Ordinal));
        Assert.Equal("ok", codex.GetProperty("state").GetString());
    }

    [Fact]
    public async Task 单个探针异常只降级自身且不泄露异常文本()
    {
        const string sensitive = "secret-host-and-token";
        var diagnostics = new List<(string Probe, string ExceptionType)>();
        ControlPlaneBridge bridge = CreateBridge(
            new CodexProbeResult(CodexReadiness.Ok, "authorized", "unused", true),
            new CodexBalanceResult(ProviderReadiness.Ok, "ok", "unused", 1m, "USD"),
            out _,
            codexProbeOverride: (_, _, _) => throw new InvalidOperationException(sensitive),
            balanceProbeOverride: (_, _) => throw new InvalidOperationException(sensitive),
            deepSeekProbeOverride: _ => throw new InvalidOperationException(sensitive),
            probeFailureReporter: (probe, exceptionType) => diagnostics.Add((probe, exceptionType)));

        JsonElement payload = await ProbeAsync(bridge, deep: false);
        string json = payload.GetRawText();

        Assert.Equal(2, payload.GetProperty("items").GetArrayLength());
        Assert.Equal("idle", FindCodex(payload).GetProperty("state").GetString());
        Assert.Contains("探测异常", json, StringComparison.Ordinal);
        Assert.DoesNotContain(sensitive, json, StringComparison.Ordinal);
        Assert.Equal(
            new[] { "codex", "codex-balance", "deepseek" },
            diagnostics.Select(d => d.Probe).OrderBy(p => p, StringComparer.Ordinal));
        Assert.All(diagnostics, d => Assert.Equal(typeof(InvalidOperationException).FullName, d.ExceptionType));
        Assert.DoesNotContain(sensitive, string.Join('|', diagnostics), StringComparison.Ordinal);
    }

    [Fact]
    public async Task 调用方取消必须穿透桥接总入口()
    {
        using var cts = new CancellationTokenSource();
        ControlPlaneBridge bridge = CreateBridge(
            new CodexProbeResult(CodexReadiness.Ok, "authorized", "unused", true),
            new CodexBalanceResult(ProviderReadiness.Ok, "ok", "unused", 1m, "USD"),
            out _,
            codexProbeOverride: (_, _, ct) =>
            {
                cts.Cancel();
                return Task.FromCanceled<CodexProbeResult>(ct);
            });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => bridge.HandleAsync(
            """{"id":"cancel-test","type":"providers.probe","deep":false}""",
            cts.Token));
    }

    [Fact]
    public async Task 诊断写入失败不能拖垮Provider面板()
    {
        ControlPlaneBridge bridge = CreateBridge(
            new CodexProbeResult(CodexReadiness.Ok, "authorized", "unused", true),
            new CodexBalanceResult(ProviderReadiness.Ok, "ok", "unused", 1m, "USD"),
            out _,
            codexProbeOverride: (_, _, _) => throw new InvalidOperationException("probe-failed"),
            probeFailureReporter: (_, _) => throw new IOException("log-failed"));

        JsonElement payload = await ProbeAsync(bridge, deep: false);

        Assert.Equal("ok", FindCodex(payload).GetProperty("state").GetString());
        Assert.Contains("探测异常", FindCodex(payload).GetProperty("detail").GetString());
    }

    [Fact]
    public async Task 桥接业务异常回前端前脱敏且本地诊断只接收异常类型()
    {
        const string token = "Bearer abcdefghijklmnop123456";
        var diagnostics = new List<(string RequestType, string ExceptionType)>();
        ControlPlaneBridge bridge = CreateBridge(
            new CodexProbeResult(CodexReadiness.Ok, "authorized", "unused", true),
            new CodexBalanceResult(ProviderReadiness.Ok, "ok", "unused", 1m, "USD"),
            out _,
            folderPicker: _ => throw new InvalidOperationException("folder failed " + token),
            requestFailureReporter: (requestType, exceptionType) => diagnostics.Add((requestType, exceptionType)));

        string response = await bridge.HandleAsync(
            """{"id":"bridge-error","type":"dialog.pickFolder"}""",
            CancellationToken.None);

        Assert.DoesNotContain(token, response, StringComparison.Ordinal);
        Assert.Contains("folder failed [redacted]", response, StringComparison.Ordinal);
        (string requestType, string exceptionType) = Assert.Single(diagnostics);
        Assert.Equal("dialog.pickFolder", requestType);
        Assert.Equal(typeof(InvalidOperationException).FullName, exceptionType);
        Assert.DoesNotContain(token, exceptionType, StringComparison.Ordinal);
    }

    private ControlPlaneBridge CreateBridge(
        CodexProbeResult codex,
        CodexBalanceResult balance,
        out List<bool> deepCalls,
        Func<CodexProviderCredentials, bool, CancellationToken, Task<CodexProbeResult>>? codexProbeOverride = null,
        Func<CodexProviderCredentials, CancellationToken, Task<CodexBalanceResult>>? balanceProbeOverride = null,
        Func<CancellationToken, Task<DeepSeekProbeResult>>? deepSeekProbeOverride = null,
        Action<string, string>? probeFailureReporter = null,
        Func<CancellationToken, Task<string?>>? folderPicker = null,
        Action<string, string>? requestFailureReporter = null,
        Func<CodexProviderCredentials>? providerSnapshotOverride = null)
    {
        string configRoot = Path.Combine(_root, Guid.NewGuid().ToString("N"), "config");
        Directory.CreateDirectory(configRoot);
        string databasePath = Path.Combine(_root, Guid.NewGuid().ToString("N") + ".db");
        StorageDatabase.Migrate(databasePath);
        deepCalls = new List<bool>();
        List<bool> calls = deepCalls;
        CodexProviderCredentials provider = FakeProvider();

        return new ControlPlaneBridge(
            catalog: new ProjectCatalog(
                userProfilePath: () => Path.Combine(_root, "fake-home"),
                tempDir: Path.Combine(_root, "fake-temp"),
                productionAppDir: Path.Combine(_root, "fake-app"),
                indexPath: null,
                shadowRoot: Path.Combine(_root, "fake-shadow")),
            configFactory: ProductConfig.CreateDefault,
            notificationRegistry: new NotificationRegistry(Array.Empty<INotificationAdapter>()),
            quotaService: new QuotaService(
                probe: _ => Task.FromResult(new ClaudeProbeResult { Reason = "not-used" })),
            configStore: new ProductConfigStore(configRoot),
            stateStore: new ProductStateStore(databasePath),
            folderPicker: folderPicker,
            codexProviderSnapshot: providerSnapshotOverride ?? (() => provider),
            codexProbe: codexProbeOverride ?? ((_, deep, _) =>
            {
                calls.Add(deep);
                return Task.FromResult(codex);
            }),
            codexBalanceProbe: balanceProbeOverride ?? ((_, _) => Task.FromResult(balance)),
            deepSeekProbe: deepSeekProbeOverride ?? (_ => Task.FromResult(new DeepSeekProbeResult(
                ProviderReadiness.NoCredential,
                "no-key",
                "未设置测试密钥",
                null))),
            probeFailureReporter: probeFailureReporter,
            requestFailureReporter: requestFailureReporter);
    }

    private static CodexProviderCredentials FakeProvider(string id = "test-provider") => new(
        "https://relay.example.invalid/v1",
        "test-token",
        "gpt-5.5",
        "responses",
        new Dictionary<string, string>(StringComparer.Ordinal),
        new Dictionary<string, string>(StringComparer.Ordinal),
        "test",
        null,
        null,
        id,
        IsBuiltInOpenAi: false,
        RequiresOpenAiAuth: true);

    private static async Task<JsonElement> ProbeAsync(ControlPlaneBridge bridge, bool deep)
    {
        string response = await bridge.HandleAsync(
            $$"""{"id":"provider-test","type":"providers.probe","deep":{{deep.ToString().ToLowerInvariant()}}}""",
            CancellationToken.None);
        using JsonDocument doc = JsonDocument.Parse(response);
        if (doc.RootElement.TryGetProperty("error", out JsonElement error) &&
            error.ValueKind == JsonValueKind.String)
        {
            Assert.Fail(error.GetString());
        }

        return doc.RootElement.GetProperty("payload").Clone();
    }

    private static JsonElement FindCodex(JsonElement payload) =>
        payload.GetProperty("items").EnumerateArray()
            .Single(item => item.GetProperty("name").GetString() == "Codex");
}
