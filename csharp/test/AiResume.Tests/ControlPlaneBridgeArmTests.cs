using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AiResume.Core;
using AiResume.Gui;
using AiResume.Storage;
using AiResume.Worker.Products;
using Microsoft.Data.Sqlite;
using Xunit;

namespace AiResume.Tests;

[Collection(SqliteCollection.Name)]
public class ControlPlaneBridgeArmTests : IDisposable
{
    private readonly string _tempRoot;
    private readonly string _configDir;
    private readonly string _dbPath;

    public ControlPlaneBridgeArmTests()
    {
        // 创建临时目录,避免触碰真实 shadow 根
        _tempRoot = Path.Combine(Path.GetTempPath(), "AiResumeTests_" + Guid.NewGuid().ToString("N"));
        _configDir = Path.Combine(_tempRoot, "config");
        Directory.CreateDirectory(_configDir);
        _dbPath = Path.Combine(_tempRoot, "state.db");

        // 必须先建表,否则 Load/Save 会抛 SQLite no such table
        AiResume.Storage.StorageDatabase.Migrate(_dbPath);
    }

    public void Dispose()
    {
        // 清理 SQLite 连接池,避免文件被占用
        SqliteConnection.ClearAllPools();
        try
        {
            if (Directory.Exists(_tempRoot))
            {
                Directory.Delete(_tempRoot, recursive: true);
            }
        }
        catch
        {
            // 清理失败不阻塞测试
        }
    }

    private ControlPlaneBridge CreateBridge()
    {
        return new ControlPlaneBridge(
            catalog: null,
            configFactory: null,
            notificationRegistry: null,
            quotaService: null,
            configStore: new ProductConfigStore(_configDir),
            stateStore: new ProductStateStore(_dbPath));
    }

    /// <summary>
    /// 把字符串转义成可直接嵌进 JSON 字面量的形式。
    /// Windows 路径里的反斜杠不转义会让请求 JSON 直接解析失败(实测踩到:
    /// C:\projlpha 里的 \p 被当成非法转义符)。
    /// </summary>
    private static string J(string raw) => System.Text.Json.JsonEncodedText.Encode(raw).ToString();

    private static JsonElement GetPayload(string responseJson)
    {
        using JsonDocument doc = JsonDocument.Parse(responseJson);
        // 先把 error 摊开:否则拿不到 payload 时只会看到一个 KeyNotFoundException,
        // 真正的失败原因被吞掉,排查要多花一轮。
        if (doc.RootElement.TryGetProperty("error", out JsonElement err) && err.ValueKind == JsonValueKind.String)
        {
            Assert.Fail("期望成功应答,实际返回 error:" + err.GetString());
        }

        return doc.RootElement.GetProperty("payload").Clone();
    }

    private static string GetType(string responseJson)
    {
        using JsonDocument doc = JsonDocument.Parse(responseJson);
        return doc.RootElement.GetProperty("type").GetString() ?? string.Empty;
    }

    private static string GetError(string responseJson)
    {
        using JsonDocument doc = JsonDocument.Parse(responseJson);
        return doc.RootElement.TryGetProperty("error", out JsonElement errEl)
            ? errEl.GetString() ?? string.Empty
            : string.Empty;
    }

    private static string GetId(string responseJson)
    {
        using JsonDocument doc = JsonDocument.Parse(responseJson);
        return doc.RootElement.GetProperty("id").GetString() ?? string.Empty;
    }

    [Fact]
    public async Task 初始未布防时_arm_get_返回未布防且selected为空()
    {
        // Arrange
        var bridge = CreateBridge();
        string request = """{"id":"req1","type":"arm.get"}""";

        // Act
        string response = await bridge.HandleAsync(request, CancellationToken.None);

        // Assert
        Assert.Equal("arm.get.result", GetType(response));
        Assert.Equal("req1", GetId(response));
        Assert.Empty(GetError(response));

        JsonElement payload = GetPayload(response);
        Assert.False(payload.GetProperty("armed").GetBoolean());
        Assert.Empty(payload.GetProperty("selected").EnumerateArray());
    }

    [Fact]
    public async Task 布防后_返回armed为true且selected顺序一致_cycleId非空()
    {
        // Arrange
        var bridge = CreateBridge();
        string[] paths = { @"C:\proj\alpha", @"C:\proj\beta" };
        string request = $$"""
            {"id":"req2","type":"arm.set","armed":true,"paths":["{{J(paths[0])}}","{{J(paths[1])}}"]}
            """;

        // Act
        string response = await bridge.HandleAsync(request, CancellationToken.None);

        // Assert
        Assert.Equal("arm.set.result", GetType(response));
        Assert.Empty(GetError(response));

        JsonElement payload = GetPayload(response);
        Assert.True(payload.GetProperty("armed").GetBoolean());
        Assert.Equal(paths, payload.GetProperty("selected").EnumerateArray().Select(e => e.GetString()).ToArray());
        Assert.False(string.IsNullOrEmpty(payload.GetProperty("cycleId").GetString()));
    }

    [Fact]
    public async Task 布防后重建桥_状态仍为已布防_证明落盘()
    {
        // Arrange
        var bridge1 = CreateBridge();
        string[] paths = { @"C:\proj\alpha" };
        string armRequest = $$"""
            {"id":"req3","type":"arm.set","armed":true,"paths":["{{J(paths[0])}}"]}
            """;
        await bridge1.HandleAsync(armRequest, CancellationToken.None);

        // Act: 重新 new 一个桥,指向同一对存储
        var bridge2 = CreateBridge();
        string getRequest = """{"id":"req4","type":"arm.get"}""";
        string response = await bridge2.HandleAsync(getRequest, CancellationToken.None);

        // Assert
        JsonElement payload = GetPayload(response);
        Assert.True(payload.GetProperty("armed").GetBoolean());
        Assert.Equal(paths, payload.GetProperty("selected").EnumerateArray().Select(e => e.GetString()).ToArray());
    }

    [Fact]
    public async Task 连续两次布防_cycleId必须不同()
    {
        // Arrange
        var bridge = CreateBridge();
        string[] paths = { @"C:\proj\alpha" };
        string request1 = $$"""
            {"id":"req5","type":"arm.set","armed":true,"paths":["{{J(paths[0])}}"]}
            """;
        string request2 = $$"""
            {"id":"req6","type":"arm.set","armed":true,"paths":["{{J(paths[0])}}"]}
            """;

        // Act
        string response1 = await bridge.HandleAsync(request1, CancellationToken.None);
        string response2 = await bridge.HandleAsync(request2, CancellationToken.None);

        // Assert
        string cycleId1 = GetPayload(response1).GetProperty("cycleId").GetString() ?? string.Empty;
        string cycleId2 = GetPayload(response2).GetProperty("cycleId").GetString() ?? string.Empty;
        Assert.NotEqual(cycleId1, cycleId2);
        Assert.False(string.IsNullOrEmpty(cycleId1));
        Assert.False(string.IsNullOrEmpty(cycleId2));
    }

    [Fact]
    public async Task 解除布防后_armed为false且cycleId为空串()
    {
        // Arrange
        var bridge = CreateBridge();
        string[] paths = { @"C:\proj\alpha" };
        string armRequest = $$"""
            {"id":"req7","type":"arm.set","armed":true,"paths":["{{J(paths[0])}}"]}
            """;
        await bridge.HandleAsync(armRequest, CancellationToken.None);

        string disarmRequest = """{"id":"req8","type":"arm.set","armed":false}""";

        // Act
        string response = await bridge.HandleAsync(disarmRequest, CancellationToken.None);

        // Assert
        Assert.Equal("arm.set.result", GetType(response));
        JsonElement payload = GetPayload(response);
        Assert.False(payload.GetProperty("armed").GetBoolean());
        Assert.Equal(string.Empty, payload.GetProperty("cycleId").GetString());
    }

    [Fact]
    public async Task 空paths布防_返回error且配置仍为未布防()
    {
        // Arrange
        var bridge = CreateBridge();
        string request = """{"id":"req9","type":"arm.set","armed":true,"paths":[]}""";

        // Act
        string response = await bridge.HandleAsync(request, CancellationToken.None);

        // Assert
        Assert.Equal("arm.set.error", GetType(response));
        Assert.False(string.IsNullOrEmpty(GetError(response)));

        // 验证配置里 armed 仍为 false
        var configStore = new ProductConfigStore(_configDir);
        ProductConfig config = configStore.Load();
        Assert.False(config.Armed);
    }

    [Fact]
    public async Task 布防带continuous_true_返回continuous为true()
    {
        // Arrange
        var bridge = CreateBridge();
        string[] paths = { @"C:\proj\alpha" };
        string request = $$"""
            {"id":"req10","type":"arm.set","armed":true,"paths":["{{J(paths[0])}}"],"continuous":true}
            """;

        // Act
        string response = await bridge.HandleAsync(request, CancellationToken.None);

        // Assert
        Assert.Equal("arm.set.result", GetType(response));
        JsonElement payload = GetPayload(response);
        Assert.True(payload.GetProperty("continuous").GetBoolean());
    }

    [Fact]
    public async Task 未知请求类型_返回error信封不抛异常()
    {
        // Arrange
        var bridge = CreateBridge();
        string request = """{"id":"req11","type":"unknown.type"}""";

        // Act
        string response = await bridge.HandleAsync(request, CancellationToken.None);

        // Assert
        Assert.Equal("unknown.type.error", GetType(response));
        Assert.False(string.IsNullOrEmpty(GetError(response)));
    }

    [Fact]
    public async Task 预置ProjectStatus后_arm_get能原样带出()
    {
        // Arrange
        // 先预置 CheckerState 的 ProjectStatus
        var stateStore = new ProductStateStore(_dbPath);
        CheckerState state = stateStore.Load();
        state.ProjectStatus = new Dictionary<string, string>
        {
            { @"C:\proj\alpha", "success" },
            { @"C:\proj\beta", "error" }
        };
        stateStore.Save(state);

        var bridge = CreateBridge();
        string request = """{"id":"req12","type":"arm.get"}""";

        // Act
        string response = await bridge.HandleAsync(request, CancellationToken.None);

        // Assert
        Assert.Equal("arm.get.result", GetType(response));
        JsonElement payload = GetPayload(response);
        var statuses = payload.GetProperty("projectStatus").EnumerateArray()
            .Select(e => new
            {
                Path = e.GetProperty("path").GetString() ?? string.Empty,
                Status = e.GetProperty("status").GetString() ?? string.Empty
            })
            .ToDictionary(x => x.Path, x => x.Status);

        Assert.Equal("success", statuses[@"C:\proj\alpha"]);
        Assert.Equal("error", statuses[@"C:\proj\beta"]);
    }
}