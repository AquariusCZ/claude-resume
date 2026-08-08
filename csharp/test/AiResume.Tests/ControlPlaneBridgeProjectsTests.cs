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
public class ControlPlaneBridgeProjectsTests : IDisposable
{
    private readonly string _tempRoot;
    private readonly string _configDir;
    private readonly string _dbPath;
    private readonly string _fakeHome;
    private readonly string _systemp;
    private readonly string _prodApp;
    private readonly string _shadowRoot;

    public ControlPlaneBridgeProjectsTests()
    {
        // 创建临时目录,避免触碰真实用户目录与 shadow 根
        _tempRoot = TestTemp.NewDir("AiResumeTests");
        _configDir = Path.Combine(_tempRoot, "config");
        _fakeHome = Path.Combine(_tempRoot, "fakehome");
        _systemp = Path.Combine(_tempRoot, "systemp");
        _prodApp = Path.Combine(_tempRoot, "prodapp");
        _shadowRoot = Path.Combine(_tempRoot, "shadow");

        Directory.CreateDirectory(_configDir);
        Directory.CreateDirectory(_fakeHome);
        Directory.CreateDirectory(_systemp);
        Directory.CreateDirectory(_prodApp);
        Directory.CreateDirectory(_shadowRoot);

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

    /// <summary>
    /// 构造被测桥。必须注入 catalog(不能传 null),否则默认 catalog 会去扫真实的
    /// %USERPROFILE%\.claude\projects——测试红线禁止触碰真实用户目录。
    /// configFactory 传 null,让实现走默认值 ()=>configStore.Load(),正是被测行为。
    /// </summary>
    private ControlPlaneBridge CreateBridge()
    {
        var catalog = new ProjectCatalog(
            userProfilePath: () => _fakeHome,
            tempDir: _systemp,
            productionAppDir: _prodApp,
            indexPath: null,
            shadowRoot: _shadowRoot);

        return new ControlPlaneBridge(
            catalog: catalog,
            configFactory: null,
            notificationRegistry: null,
            quotaService: null,
            configStore: new ProductConfigStore(_configDir),
            stateStore: new ProductStateStore(_dbPath));
    }

    /// <summary>
    /// 构造被测桥,可注入 folderPicker。
    /// </summary>
    private ControlPlaneBridge CreateBridge(Func<CancellationToken, Task<string?>>? folderPicker)
    {
        var catalog = new ProjectCatalog(
            userProfilePath: () => _fakeHome,
            tempDir: _systemp,
            productionAppDir: _prodApp,
            indexPath: null,
            shadowRoot: _shadowRoot);

        return new ControlPlaneBridge(
            catalog: catalog,
            configFactory: null,
            notificationRegistry: null,
            quotaService: null,
            configStore: new ProductConfigStore(_configDir),
            stateStore: new ProductStateStore(_dbPath),
            folderPicker: folderPicker);
    }

    /// <summary>
    /// 把字符串转义成可直接嵌进 JSON 字面量的形式。
    /// Windows 路径里的反斜杠不转义会让请求 JSON 直接解析失败(实测踩到:
    /// C:\proj\lpha 里的 \p 被当成非法转义符)。
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

    /// <summary>从 payload 中提取 items 的 path 列表。</summary>
    private static List<string> GetItemPaths(JsonElement payload)
    {
        return payload.GetProperty("items").EnumerateArray()
            .Select(e => e.GetProperty("path").GetString() ?? string.Empty)
            .ToList();
    }

    /// <summary>从 payload 中提取 hidden 的 path 列表。</summary>
    private static List<string> GetHiddenPaths(JsonElement payload)
    {
        return payload.GetProperty("hidden").EnumerateArray()
            .Select(e => e.GetProperty("path").GetString() ?? string.Empty)
            .ToList();
    }

    /// <summary>判断 payload.items 中是否存在指定路径且 isCustom 为 true。</summary>
    private static bool HasCustomItem(JsonElement payload, string path)
    {
        return payload.GetProperty("items").EnumerateArray()
            .Any(e => string.Equals(e.GetProperty("path").GetString(), path, StringComparison.OrdinalIgnoreCase)
                      && e.GetProperty("isCustom").GetBoolean());
    }

    /// <summary>判断 payload.items 中是否存在指定路径(不关心 isCustom)。</summary>
    private static bool HasItem(JsonElement payload, string path)
    {
        return payload.GetProperty("items").EnumerateArray()
            .Any(e => string.Equals(e.GetProperty("path").GetString(), path, StringComparison.OrdinalIgnoreCase));
    }

    // ==================== 添加 ====================

    [Fact]
    public async Task Add_写入CustomProjects并出现在列表里()
    {
        // Arrange
        string projectDir = Path.Combine(_tempRoot, "proj_add_1");
        Directory.CreateDirectory(projectDir);
        var bridge = CreateBridge();
        string request = $$"""{"id":"1","type":"projects.add","path":"{{J(projectDir)}}"}""";

        // Act
        string response = await bridge.HandleAsync(request, CancellationToken.None);

        // Assert: 应答形状
        Assert.Equal("projects.add.result", GetType(response));
        Assert.Equal("1", GetId(response));
        Assert.Empty(GetError(response));
        JsonElement payload = GetPayload(response);
        Assert.True(HasCustomItem(payload, projectDir), "items 应包含刚添加的路径且 isCustom==true");

        // Assert: 持久化到磁盘
        var configStore = new ProductConfigStore(_configDir);
        ProductConfig config = configStore.Load();
        Assert.Contains(config.CustomProjects, c => string.Equals(c.Path, projectDir, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Add_重复添加不产生重复项()
    {
        // Arrange
        string projectDir = Path.Combine(_tempRoot, "proj_add_2");
        Directory.CreateDirectory(projectDir);
        var bridge = CreateBridge();
        string request = $$"""{"id":"2","type":"projects.add","path":"{{J(projectDir)}}"}""";

        // Act: 连加两次
        await bridge.HandleAsync(request, CancellationToken.None);
        string response2 = await bridge.HandleAsync(request, CancellationToken.None);

        // Assert: 配置里只有一条
        var configStore = new ProductConfigStore(_configDir);
        ProductConfig config = configStore.Load();
        Assert.Single(config.CustomProjects);

        // Assert: items 里也只有一条
        JsonElement payload = GetPayload(response2);
        var matching = payload.GetProperty("items").EnumerateArray()
            .Where(e => string.Equals(e.GetProperty("path").GetString(), projectDir, StringComparison.OrdinalIgnoreCase))
            .ToList();
        Assert.Single(matching);
    }

    [Fact]
    public async Task Add_目录不存在时返回error且不写配置()
    {
        // Arrange
        string missingDir = Path.Combine(_tempRoot, "does_not_exist");
        var bridge = CreateBridge();
        string request = $$"""{"id":"3","type":"projects.add","path":"{{J(missingDir)}}"}""";

        // Act
        string response = await bridge.HandleAsync(request, CancellationToken.None);

        // Assert
        Assert.Equal("projects.add.error", GetType(response));
        Assert.False(string.IsNullOrEmpty(GetError(response)));

        // 配置未被写入
        var configStore = new ProductConfigStore(_configDir);
        ProductConfig config = configStore.Load();
        Assert.Empty(config.CustomProjects);
    }

    [Fact]
    public async Task Add_保留区目录被拒绝()
    {
        // Arrange: 用 productionAppDir 下的真实子目录
        string reservedDir = Path.Combine(_prodApp, "sub");
        Directory.CreateDirectory(reservedDir);
        var bridge = CreateBridge();
        string request = $$"""{"id":"4","type":"projects.add","path":"{{J(reservedDir)}}"}""";

        // Act
        string response = await bridge.HandleAsync(request, CancellationToken.None);

        // Assert
        Assert.Equal("projects.add.error", GetType(response));
        Assert.False(string.IsNullOrEmpty(GetError(response)));

        // 配置未被写入
        var configStore = new ProductConfigStore(_configDir);
        ProductConfig config = configStore.Load();
        Assert.Empty(config.CustomProjects);
    }

    [Fact]
    public async Task Add_能把先前移除的项目恢复回来()
    {
        // Arrange
        string projectDir = Path.Combine(_tempRoot, "proj_add_5");
        Directory.CreateDirectory(projectDir);
        var bridge = CreateBridge();

        // 先移除
        string removeRequest = $$"""{"id":"5a","type":"projects.remove","path":"{{J(projectDir)}}"}""";
        await bridge.HandleAsync(removeRequest, CancellationToken.None);

        // Act: 再添加同一路径
        string addRequest = $$"""{"id":"5b","type":"projects.add","path":"{{J(projectDir)}}"}""";
        string response = await bridge.HandleAsync(addRequest, CancellationToken.None);

        // Assert: 应答里能看到它
        Assert.Equal("projects.add.result", GetType(response));
        JsonElement payload = GetPayload(response);
        Assert.True(HasCustomItem(payload, projectDir), "items 应包含恢复的项目");

        // Assert: 磁盘上 HiddenProjects 不含它、CustomProjects 含它
        var configStore = new ProductConfigStore(_configDir);
        ProductConfig config = configStore.Load();
        Assert.DoesNotContain(config.HiddenProjects, h => string.Equals(h, projectDir, StringComparison.OrdinalIgnoreCase));
        Assert.Contains(config.CustomProjects, c => string.Equals(c.Path, projectDir, StringComparison.OrdinalIgnoreCase));
    }

    // ==================== 移除 ====================

    [Fact]
    public async Task Remove_把自定义项目移出列表并写入HiddenProjects()
    {
        // Arrange
        string projectDir = Path.Combine(_tempRoot, "proj_remove_1");
        Directory.CreateDirectory(projectDir);
        var bridge = CreateBridge();

        // 先添加
        string addRequest = $$"""{"id":"6a","type":"projects.add","path":"{{J(projectDir)}}"}""";
        await bridge.HandleAsync(addRequest, CancellationToken.None);

        // Act: 再移除
        string removeRequest = $$"""{"id":"6b","type":"projects.remove","path":"{{J(projectDir)}}"}""";
        string response = await bridge.HandleAsync(removeRequest, CancellationToken.None);

        // Assert: 应答形状
        Assert.Equal("projects.remove.result", GetType(response));
        JsonElement payload = GetPayload(response);
        Assert.False(HasItem(payload, projectDir), "items 不应包含已移除的项目");
        Assert.Contains(GetHiddenPaths(payload), h => string.Equals(h, projectDir, StringComparison.OrdinalIgnoreCase));

        // Assert: 磁盘持久化
        var configStore = new ProductConfigStore(_configDir);
        ProductConfig config = configStore.Load();
        Assert.Empty(config.CustomProjects);
        Assert.Contains(config.HiddenProjects, h => string.Equals(h, projectDir, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Remove_同时把项目移出Selected()
    {
        // Arrange: 手工写一份含 Selected=[p1,p2]、Armed=true 的配置
        string p1 = Path.Combine(_tempRoot, "proj_remove_2a");
        string p2 = Path.Combine(_tempRoot, "proj_remove_2b");
        Directory.CreateDirectory(p1);
        Directory.CreateDirectory(p2);

        var configStore = new ProductConfigStore(_configDir);
        configStore.Save(new ProductConfig
        {
            Armed = true,
            ArmCycleId = "abc",
            Selected = new List<ProjectRef>
            {
                new() { Name = Path.GetFileName(p1), Path = p1 },
                new() { Name = Path.GetFileName(p2), Path = p2 }
            }
        });

        var bridge = CreateBridge();

        // Act: 移除 p1
        string removeRequest = $$"""{"id":"7","type":"projects.remove","path":"{{J(p1)}}"}""";
        string response = await bridge.HandleAsync(removeRequest, CancellationToken.None);

        // Assert: 应答成功
        Assert.Equal("projects.remove.result", GetType(response));

        // Assert: 磁盘 Selected 只剩 p2,Armed 仍为 true,cycleId 不变
        ProductConfig config = configStore.Load();
        Assert.Single(config.Selected);
        Assert.Equal(p2, config.Selected[0].Path, ignoreCase: true);
        Assert.True(config.Armed);
        Assert.Equal("abc", config.ArmCycleId);
    }

    [Fact]
    public async Task Remove_最后一个已布防项目时连带解除布防()
    {
        // Arrange: 手工写一份含 Selected=[p1]、Armed=true 的配置
        string p1 = Path.Combine(_tempRoot, "proj_remove_3");
        Directory.CreateDirectory(p1);

        var configStore = new ProductConfigStore(_configDir);
        configStore.Save(new ProductConfig
        {
            Armed = true,
            ArmCycleId = "abc",
            Selected = new List<ProjectRef>
            {
                new() { Name = Path.GetFileName(p1), Path = p1 }
            }
        });

        var bridge = CreateBridge();

        // Act: 移除 p1
        string removeRequest = $$"""{"id":"8","type":"projects.remove","path":"{{J(p1)}}"}""";
        string response = await bridge.HandleAsync(removeRequest, CancellationToken.None);

        // Assert: 应答成功
        Assert.Equal("projects.remove.result", GetType(response));

        // Assert: 磁盘 Selected 为空、Armed=false、cycleId 为空串
        ProductConfig config = configStore.Load();
        Assert.Empty(config.Selected);
        Assert.False(config.Armed);
        Assert.Equal(string.Empty, config.ArmCycleId);
    }

    [Fact]
    public async Task Remove_重复移除是幂等的()
    {
        // Arrange
        string projectDir = Path.Combine(_tempRoot, "proj_remove_4");
        Directory.CreateDirectory(projectDir);
        var bridge = CreateBridge();
        string removeRequest = $$"""{"id":"9","type":"projects.remove","path":"{{J(projectDir)}}"}""";

        // Act: 移除两次
        await bridge.HandleAsync(removeRequest, CancellationToken.None);
        string response2 = await bridge.HandleAsync(removeRequest, CancellationToken.None);

        // Assert: HiddenProjects 里该路径只出现一次
        Assert.Equal("projects.remove.result", GetType(response2));
        var configStore = new ProductConfigStore(_configDir);
        ProductConfig config = configStore.Load();
        Assert.Single(config.HiddenProjects, h => string.Equals(h, projectDir, StringComparison.OrdinalIgnoreCase));
    }

    // ==================== 恢复 ====================

    [Fact]
    public async Task Restore_单个路径只恢复该项()
    {
        // Arrange: 先隐藏两个
        string p1 = Path.Combine(_tempRoot, "proj_restore_1a");
        string p2 = Path.Combine(_tempRoot, "proj_restore_1b");
        Directory.CreateDirectory(p1);
        Directory.CreateDirectory(p2);

        var configStore = new ProductConfigStore(_configDir);
        configStore.Save(new ProductConfig
        {
            HiddenProjects = new List<string> { p1, p2 }
        });

        var bridge = CreateBridge();

        // Act: 只恢复 p1
        string request = $$"""{"id":"10","type":"projects.restore","path":"{{J(p1)}}"}""";
        string response = await bridge.HandleAsync(request, CancellationToken.None);

        // Assert: 应答成功,hidden 只剩 p2
        Assert.Equal("projects.restore.result", GetType(response));
        JsonElement payload = GetPayload(response);
        var hiddenPaths = GetHiddenPaths(payload);
        Assert.DoesNotContain(hiddenPaths, h => string.Equals(h, p1, StringComparison.OrdinalIgnoreCase));
        Assert.Contains(hiddenPaths, h => string.Equals(h, p2, StringComparison.OrdinalIgnoreCase));

        // Assert: 磁盘 HiddenProjects 只剩 p2
        ProductConfig config = configStore.Load();
        Assert.Single(config.HiddenProjects);
        Assert.Equal(p2, config.HiddenProjects[0], ignoreCase: true);
    }

    [Fact]
    public async Task Restore_不带path时全部恢复()
    {
        // Arrange: 先隐藏两个
        string p1 = Path.Combine(_tempRoot, "proj_restore_2a");
        string p2 = Path.Combine(_tempRoot, "proj_restore_2b");
        Directory.CreateDirectory(p1);
        Directory.CreateDirectory(p2);

        var configStore = new ProductConfigStore(_configDir);
        configStore.Save(new ProductConfig
        {
            HiddenProjects = new List<string> { p1, p2 }
        });

        var bridge = CreateBridge();

        // Act: 不带 path 全部恢复
        string request = """{"id":"11","type":"projects.restore"}""";
        string response = await bridge.HandleAsync(request, CancellationToken.None);

        // Assert: 应答成功,hidden 为空
        Assert.Equal("projects.restore.result", GetType(response));
        JsonElement payload = GetPayload(response);
        Assert.Empty(GetHiddenPaths(payload));

        // Assert: 磁盘 HiddenProjects 为空
        ProductConfig config = configStore.Load();
        Assert.Empty(config.HiddenProjects);
    }

    // ==================== 列表读回 ====================

    [Fact]
    public async Task List_开窗时读回持久化的自定义项目()
    {
        // Arrange: 直接写配置,含 CustomProjects=[真实临时目录]
        string projectDir = Path.Combine(_tempRoot, "proj_list_1");
        Directory.CreateDirectory(projectDir);

        var configStore = new ProductConfigStore(_configDir);
        configStore.Save(new ProductConfig
        {
            CustomProjects = new List<ProjectRef>
            {
                new() { Name = Path.GetFileName(projectDir), Path = projectDir }
            }
        });

        // Act: 新建 bridge 实例(模拟重开窗口)
        var bridge = CreateBridge();
        string request = """{"id":"12","type":"projects.list"}""";
        string response = await bridge.HandleAsync(request, CancellationToken.None);

        // Assert
        Assert.Equal("projects.list.result", GetType(response));
        JsonElement payload = GetPayload(response);
        Assert.True(HasCustomItem(payload, projectDir), "items 应包含持久化的自定义项目");
    }

    [Fact]
    public async Task List_持久化的HiddenProjects会出现在hidden字段里()
    {
        // Arrange: 直接写配置,含 HiddenProjects
        string hiddenDir = Path.Combine(_tempRoot, "proj_list_2");
        Directory.CreateDirectory(hiddenDir);

        var configStore = new ProductConfigStore(_configDir);
        configStore.Save(new ProductConfig
        {
            HiddenProjects = new List<string> { hiddenDir }
        });

        // Act: 新建 bridge 实例(模拟重开窗口)
        var bridge = CreateBridge();
        string request = """{"id":"13","type":"projects.list"}""";
        string response = await bridge.HandleAsync(request, CancellationToken.None);

        // Assert
        Assert.Equal("projects.list.result", GetType(response));
        JsonElement payload = GetPayload(response);
        Assert.Contains(GetHiddenPaths(payload), h => string.Equals(h, hiddenDir, StringComparison.OrdinalIgnoreCase));
    }

    // ==================== 选目录 ====================

    [Fact]
    public async Task PickFolder_返回选中的路径()
    {
        // Arrange
        string pickedPath = Path.Combine(_tempRoot, "picked_folder");
        var bridge = CreateBridge(_ => Task.FromResult<string?>(pickedPath));
        string request = """{"id":"14","type":"dialog.pickFolder"}""";

        // Act
        string response = await bridge.HandleAsync(request, CancellationToken.None);

        // Assert
        Assert.Equal("dialog.pickFolder.result", GetType(response));
        Assert.Empty(GetError(response));
        JsonElement payload = GetPayload(response);
        Assert.True(payload.TryGetProperty("path", out JsonElement pathEl), "payload 应包含 path 键");
        Assert.Equal(pickedPath, pathEl.GetString());
    }

    [Fact]
    public async Task PickFolder_用户取消时不报错()
    {
        // Arrange: 注入返回 null 的选择器
        var bridge = CreateBridge(_ => Task.FromResult<string?>(null));
        string request = """{"id":"15","type":"dialog.pickFolder"}""";

        // Act
        string response = await bridge.HandleAsync(request, CancellationToken.None);

        // Assert: 是 result 不是 error
        Assert.Equal("dialog.pickFolder.result", GetType(response));
        Assert.Empty(GetError(response));

        // Assert: payload 里没有非空 path(实现用 WhenWritingNull,null 时键不出现)
        JsonElement payload = GetPayload(response);
        Assert.False(payload.TryGetProperty("path", out JsonElement pathEl) && pathEl.ValueKind == JsonValueKind.String && !string.IsNullOrEmpty(pathEl.GetString()));
    }

    [Fact]
    public async Task PickFolder_宿主未注入选择器时返回error()
    {
        // Arrange: folderPicker 传 null
        var bridge = CreateBridge(folderPicker: null);
        string request = """{"id":"16","type":"dialog.pickFolder"}""";

        // Act
        string response = await bridge.HandleAsync(request, CancellationToken.None);

        // Assert
        Assert.Equal("dialog.pickFolder.error", GetType(response));
        Assert.False(string.IsNullOrEmpty(GetError(response)));
    }
}