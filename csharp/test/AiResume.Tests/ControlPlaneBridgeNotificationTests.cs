using System.Text.Json;
using AiResume.Gui;
using AiResume.Storage;
using AiResume.Worker.Notifications;
using AiResume.Worker.Products;
using Microsoft.Data.Sqlite;
using Xunit;

namespace AiResume.Tests;

[Collection(SqliteCollection.Name)]
public sealed class ControlPlaneBridgeNotificationTests : IDisposable
{
    private readonly string _root = TestTemp.NewDir("bridge-notifications");
    private readonly string _databasePath;

    public ControlPlaneBridgeNotificationTests()
    {
        _databasePath = Path.Combine(_root, "state.db");
        StorageDatabase.Migrate(_databasePath);
    }

    [Theory]
    [InlineData(NotificationProviderKind.Cline)]
    [InlineData(NotificationProviderKind.OpenCode)]
    public async Task Gui启用只把可执行文件路径交给适配器(NotificationProviderKind kind)
    {
        string hookExe = Path.Combine(_root, "Program Files", "AI Resume", HookExecutable.FileName);
        INotificationAdapter adapter = kind switch
        {
            NotificationProviderKind.Cline => new ClineNotificationAdapter(Path.Combine(_root, "cline-hooks")),
            NotificationProviderKind.OpenCode => new OpenCodeNotificationAdapter(
                Path.Combine(_root, "opencode", "plugins")),
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };
        var registry = new NotificationRegistry(new[] { adapter }, _ => true);
        ControlPlaneBridge bridge = CreateBridge(registry, () => hookExe);

        string response = await bridge.HandleAsync(
            $$"""{"type":"notifications.setEnabled","id":"1","kind":"{{kind}}","enabled":true}""",
            CancellationToken.None);

        AssertResult(response);
        Assert.Equal(hookExe, adapter.Probe().HookCommand);
    }

    [Fact]
    public async Task Gui停用不要求Hook可执行文件仍然存在()
    {
        var adapter = new TrackingAdapter(NotificationProviderKind.Cline, enabled: true);
        var registry = new NotificationRegistry(new[] { adapter }, _ => false);
        ControlPlaneBridge bridge = CreateBridge(
            registry,
            () => throw new InvalidOperationException("停用时不应解析 Hook 路径"));

        string response = await bridge.HandleAsync(
            """{"type":"notifications.setEnabled","id":"2","kind":"Cline","enabled":false}""",
            CancellationToken.None);

        AssertResult(response);
        Assert.False(adapter.Enabled);
        Assert.Equal(1, adapter.DisableCalls);
    }

    private ControlPlaneBridge CreateBridge(NotificationRegistry registry, Func<string?> resolver)
    {
        string storeRoot = Path.Combine(_root, "store-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(storeRoot);
        return new ControlPlaneBridge(
            catalog: new ProjectCatalog(indexPath: Path.Combine(storeRoot, "project-index.json")),
            notificationRegistry: registry,
            configStore: new ProductConfigStore(storeRoot),
            stateStore: new ProductStateStore(_databasePath),
            hookExecutableResolver: resolver);
    }

    private static void AssertResult(string response)
    {
        using JsonDocument doc = JsonDocument.Parse(response);
        Assert.Equal("notifications.setEnabled.result", doc.RootElement.GetProperty("type").GetString());
        Assert.False(doc.RootElement.TryGetProperty("error", out JsonElement error) &&
                     error.ValueKind == JsonValueKind.String,
            error.ValueKind == JsonValueKind.String ? error.GetString() : response);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_root, recursive: true); } catch (Exception) { }
    }

    private sealed class TrackingAdapter : INotificationAdapter
    {
        public TrackingAdapter(NotificationProviderKind kind, bool enabled)
        {
            Kind = kind;
            Enabled = enabled;
        }

        public NotificationProviderKind Kind { get; }
        public string DisplayName => Kind.ToString();
        public bool Enabled { get; private set; }
        public int DisableCalls { get; private set; }

        public NotificationProviderStatus Probe() => new(
            Kind, DisplayName, IsInstalled: true, IsEnabled: Enabled,
            ConfigPath: null, Detail: null,
            HookCommand: Enabled ? @"C:\missing\AiResume.Hook.exe" : null);

        public void Enable(string hookCommand) => Enabled = true;

        public void Disable()
        {
            DisableCalls++;
            Enabled = false;
        }
    }
}
