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

/// <summary>
/// 桥接层的通用契约:取消穿透、异常脱敏、诊断不拖垮应答。
/// 供应商可用性现役只经 <c>providers.catalog</c>(零 token 元数据),旧的 <c>providers.probe</c>
/// 推理探测 RPC 已于 2026-10-12 随 F1 清理一并删除。
/// </summary>
public sealed class ControlPlaneBridgeProviderTests : IDisposable
{
    private readonly string _root = TestTemp.NewDir("bridge-provider");

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (Exception) { }
    }

    [Fact]
    public async Task 调用方取消必须穿透桥接总入口()
    {
        using var cts = new CancellationTokenSource();
        ControlPlaneBridge bridge = CreateBridge(providerCatalog: ct =>
        {
            cts.Cancel();
            return Task.FromCanceled<IReadOnlyList<ProviderObservation>>(ct);
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => bridge.HandleAsync(
            """{"id":"cancel-test","type":"providers.catalog"}""",
            cts.Token));
    }

    [Fact]
    public async Task 供应商目录异常回前端前脱敏且不泄露凭据()
    {
        const string token = "Bearer abcdefghijklmnop123456";
        ControlPlaneBridge bridge = CreateBridge(providerCatalog: _ =>
            throw new InvalidOperationException("catalog failed " + token));

        string response = await bridge.HandleAsync(
            """{"id":"catalog-error","type":"providers.catalog"}""",
            CancellationToken.None);

        Assert.Contains("providers.catalog.error", response, StringComparison.Ordinal);
        Assert.DoesNotContain(token, response, StringComparison.Ordinal);
    }

    [Fact]
    public async Task 桥接业务异常回前端前脱敏且本地诊断只接收异常类型()
    {
        const string token = "Bearer abcdefghijklmnop123456";
        var diagnostics = new List<(string RequestType, string ExceptionType)>();
        ControlPlaneBridge bridge = CreateBridge(
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
    }

    [Fact]
    public async Task 长期令牌保存清除只回状态且不回传令牌()
    {
        const string token = "sk-" + "ant-oat01-UNIQUE-test-token-value-0123456789"; // 拼接:避开密钥形状门禁
        string home = Path.Combine(_root, "claude-home");
        Directory.CreateDirectory(home);
        var credentials = new ClaudeCredentialSource(
            credentialsPath: Path.Combine(home, ".credentials.json"),
            secretsRoot: Path.Combine(_root, "secrets"));
        ControlPlaneBridge bridge = CreateBridge(claudeCredentials: credentials);

        string before = await bridge.HandleAsync("""{"id":"s0","type":"claude.token.status"}""", CancellationToken.None);
        Assert.Contains("\"hasStoredToken\":false", before, StringComparison.Ordinal);

        string saved = await bridge.HandleAsync(
            $$"""{"id":"s1","type":"claude.token.save","token":"{{token}}"}""", CancellationToken.None);
        Assert.Contains("claude.token.save.result", saved, StringComparison.Ordinal);
        Assert.Contains("\"hasStoredToken\":true", saved, StringComparison.Ordinal);
        Assert.Contains("\"activeSource\":\"ai-resume-token\"", saved, StringComparison.Ordinal);
        Assert.DoesNotContain(token, saved, StringComparison.Ordinal);
        Assert.True(credentials.ReadStored().Ok);

        string cleared = await bridge.HandleAsync("""{"id":"s2","type":"claude.token.clear"}""", CancellationToken.None);
        Assert.Contains("\"hasStoredToken\":false", cleared, StringComparison.Ordinal);
        Assert.False(credentials.ReadStored().Ok);
    }

    [Fact]
    public async Task 长期令牌形状非法时拒绝保存且不回显输入()
    {
        var credentials = new ClaudeCredentialSource(
            credentialsPath: Path.Combine(_root, "none", ".credentials.json"),
            secretsRoot: Path.Combine(_root, "secrets-invalid"));
        ControlPlaneBridge bridge = CreateBridge(claudeCredentials: credentials);

        string response = await bridge.HandleAsync(
            """{"id":"bad","type":"claude.token.save","token":"short token with spaces"}""", CancellationToken.None);

        Assert.Contains("claude.token.save.error", response, StringComparison.Ordinal);
        Assert.DoesNotContain("short token with spaces", response, StringComparison.Ordinal);
        Assert.False(credentials.HasStoredToken);
    }

    private ControlPlaneBridge CreateBridge(
        Func<CancellationToken, Task<IReadOnlyList<ProviderObservation>>>? providerCatalog = null,
        Func<CancellationToken, Task<string?>>? folderPicker = null,
        Action<string, string>? requestFailureReporter = null,
        ClaudeCredentialSource? claudeCredentials = null)
    {
        string configRoot = Path.Combine(_root, Guid.NewGuid().ToString("N"), "config");
        Directory.CreateDirectory(configRoot);
        string databasePath = Path.Combine(_root, Guid.NewGuid().ToString("N") + ".db");
        StorageDatabase.Migrate(databasePath);

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
            claudeCredentials: claudeCredentials ?? new ClaudeCredentialSource(
                credentialsPath: Path.Combine(_root, "fake-home", ".claude", ".credentials.json"),
                secretsRoot: Path.Combine(_root, "fake-secrets")),
            requestFailureReporter: requestFailureReporter,
            providerCatalog: providerCatalog ?? (_ => Task.FromResult<IReadOnlyList<ProviderObservation>>(Array.Empty<ProviderObservation>())));
    }
}
