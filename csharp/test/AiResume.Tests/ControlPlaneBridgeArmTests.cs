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
        _tempRoot = TestTemp.NewDir("AiResumeTests");
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

    private ControlPlaneBridge CreateBridge(
        Func<string, bool?>? activeResumeDetector = null,
        Func<bool?>? engineProcessDetector = null)
    {
        return new ControlPlaneBridge(
            catalog: null,
            configFactory: null,
            notificationRegistry: null,
            quotaService: null,
            configStore: new ProductConfigStore(_configDir),
            stateStore: new ProductStateStore(_dbPath),
            engineProcessDetector: engineProcessDetector ?? (() => true),
            activeResumeDetector: activeResumeDetector);
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
    public async Task 当前周期的ProjectStatus_arm_get能原样带出()
    {
        // Arrange
        const string cycleId = "active-cycle";
        var configStore = new ProductConfigStore(_configDir);
        configStore.Update(config =>
        {
            config.Enabled = true;
            config.Armed = true;
            config.ArmCycleId = cycleId;
            config.Selected = new List<ProjectRef>
            {
                new() { Name = "alpha", Path = @"C:\proj\alpha" },
                new() { Name = "beta", Path = @"C:\proj\beta" },
                new() { Name = "gamma", Path = @"C:\proj\gamma" },
                new() { Name = "delta", Path = @"C:\proj\delta" },
                new() { Name = "epsilon", Path = @"C:\proj\epsilon" },
            };
        });

        var stateStore = new ProductStateStore(_dbPath);
        CheckerState state = stateStore.Load();
        state.CycleId = cycleId;
        state.ProjectStatus = new Dictionary<string, string>
        {
            { @"C:\proj\alpha", "success" },
            { @"C:\proj\beta", "error" },
            { @"C:\proj\gamma", "no-claude" },
            { @"C:\proj\delta", "stopped" },
            { @"C:\proj\epsilon", "exit-17" },
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
                Status = e.GetProperty("status").GetString() ?? string.Empty,
                Category = e.GetProperty("category").GetString() ?? string.Empty,
                Text = e.GetProperty("text").GetString() ?? string.Empty,
            })
            .ToDictionary(x => x.Path, x => x.Status);

        Assert.Equal("success", statuses[@"C:\proj\alpha"]);
        Assert.Equal("error", statuses[@"C:\proj\beta"]);

        JsonElement[] items = payload.GetProperty("projectStatus").EnumerateArray().ToArray();
        JsonElement Find(string path) => items.Single(e => e.GetProperty("path").GetString() == path);
        Assert.Equal("success", Find(@"C:\proj\alpha").GetProperty("category").GetString());
        Assert.Equal("已完成", Find(@"C:\proj\alpha").GetProperty("text").GetString());
        Assert.Equal("failure", Find(@"C:\proj\gamma").GetProperty("category").GetString());
        Assert.Equal("Claude 或项目不可用", Find(@"C:\proj\gamma").GetProperty("text").GetString());
        Assert.Equal("waiting", Find(@"C:\proj\delta").GetProperty("category").GetString());
        Assert.Equal("已停止", Find(@"C:\proj\delta").GetProperty("text").GetString());
        Assert.Equal("failure", Find(@"C:\proj\epsilon").GetProperty("category").GetString());
        Assert.Equal("进程异常退出", Find(@"C:\proj\epsilon").GetProperty("text").GetString());
    }

    [Fact]
    public async Task 当前周期状态值为null时_arm_get失败关闭并清空旧项目状态()
    {
        const string cycleId = "active-cycle";
        var configStore = new ProductConfigStore(_configDir);
        configStore.Update(config =>
        {
            config.Enabled = true;
            config.Armed = true;
            config.ArmCycleId = cycleId;
            config.Selected = new List<ProjectRef>
            {
                new() { Name = "damaged", Path = @"C:\proj\damaged" },
            };
        });

        using (var connection = StorageDatabase.Open(_dbPath))
        {
            StorageDatabase.Execute(connection,
                "UPDATE product_state SET state_json = $json WHERE id = 1;",
                null,
                ("$json", "{\"phase\":\"waiting\",\"cycleId\":\"active-cycle\",\"projectStatus\":{\"C:\\\\proj\\\\damaged\":null}}"));
        }

        JsonElement payload = GetPayload(await CreateBridge().HandleAsync(
            """{"id":"req12b","type":"arm.get"}""",
            CancellationToken.None));

        Assert.Equal(EngineVerdict.StateUnverified.ToString(), payload.GetProperty("engine").GetString());
        Assert.Empty(payload.GetProperty("projectStatus").EnumerateArray());
    }

    [Fact]
    public async Task 当前周期只返回仍在Selected中的项目状态()
    {
        const string cycleId = "active-cycle";
        var configStore = new ProductConfigStore(_configDir);
        configStore.Update(config =>
        {
            config.Enabled = true;
            config.Armed = true;
            config.ArmCycleId = cycleId;
            config.Selected = new List<ProjectRef>
            {
                new() { Name = "alpha", Path = @"C:\proj\alpha" },
            };
        });

        var stateStore = new ProductStateStore(_dbPath);
        CheckerState state = stateStore.Load();
        state.CycleId = cycleId;
        state.ProjectStatus = new Dictionary<string, string>
        {
            [@"c:\PROJ\ALPHA"] = "success",
            [@"C:\proj\removed"] = "error",
        };
        stateStore.Save(state);

        JsonElement payload = GetPayload(await CreateBridge().HandleAsync(
            """{"id":"req12c","type":"arm.get"}""",
            CancellationToken.None));

        JsonElement status = Assert.Single(payload.GetProperty("projectStatus").EnumerateArray());
        Assert.Equal(@"C:\proj\alpha", status.GetProperty("path").GetString());
        Assert.Equal("success", status.GetProperty("status").GetString());
    }

    [Fact]
    public async Task 当前周期路径尾斜杠不同_仍能匹配项目状态()
    {
        const string cycleId = "active-cycle";
        var configStore = new ProductConfigStore(_configDir);
        configStore.Update(config =>
        {
            config.Enabled = true;
            config.Armed = true;
            config.ArmCycleId = cycleId;
            config.Selected = new List<ProjectRef>
            {
                new() { Name = "alpha", Path = @"C:\proj\alpha" },
            };
        });

        var stateStore = new ProductStateStore(_dbPath);
        CheckerState state = stateStore.Load();
        state.CycleId = cycleId;
        state.ProjectStatus = new Dictionary<string, string>
        {
            [@"C:\proj\alpha\"] = "success",
        };
        stateStore.Save(state);

        JsonElement payload = GetPayload(await CreateBridge().HandleAsync(
            """{"id":"req12d","type":"arm.get"}""",
            CancellationToken.None));

        JsonElement status = Assert.Single(payload.GetProperty("projectStatus").EnumerateArray());
        Assert.Equal(@"C:\proj\alpha", status.GetProperty("path").GetString());
        Assert.Equal("success", status.GetProperty("status").GetString());
    }

    [Fact]
    public async Task 重新布防后_arm_get不泄露旧周期状态()
    {
        // Arrange:配置已经进入新周期,Worker 尚未来得及初始化新状态。
        var configStore = new ProductConfigStore(_configDir);
        configStore.Update(config =>
        {
            config.Enabled = true;
            config.Armed = true;
            config.ArmCycleId = "new-cycle";
        });

        var stateStore = new ProductStateStore(_dbPath);
        CheckerState state = stateStore.Load();
        state.CycleId = "old-cycle";
        state.Phase = CheckerState.PhaseResuming;
        state.SawLimited = true;
        state.LastProbeUtc = DateTimeOffset.UtcNow.AddHours(-2);
        state.ProjectStatus = new Dictionary<string, string>
        {
            { @"C:\proj\alpha", "success" },
        };
        stateStore.Save(state);

        // Act
        JsonElement payload = GetPayload(await CreateBridge().HandleAsync(
            """{"id":"req13","type":"arm.get"}""",
            CancellationToken.None));

        // Assert
        Assert.True(payload.GetProperty("armed").GetBoolean());
        Assert.Equal("new-cycle", payload.GetProperty("cycleId").GetString());
        Assert.Equal(string.Empty, payload.GetProperty("phase").GetString());
        Assert.False(payload.GetProperty("sawLimited").GetBoolean());
        Assert.Empty(payload.GetProperty("projectStatus").EnumerateArray());
        Assert.False(payload.TryGetProperty("probeAgeSeconds", out _));
    }

    [Fact]
    public async Task 自动完成并解除布防后_arm_get隐藏已结束周期结果()
    {
        // Arrange
        var stateStore = new ProductStateStore(_dbPath);
        CheckerState state = stateStore.Load();
        state.CycleId = "finished-cycle";
        state.Phase = CheckerState.PhaseDone;
        state.SawLimited = true;
        state.LastProbeUtc = DateTimeOffset.UtcNow.AddMinutes(-1);
        state.ProjectStatus = new Dictionary<string, string>
        {
            { @"C:\proj\alpha", "success" },
        };
        stateStore.Save(state);

        // Act
        JsonElement payload = GetPayload(await CreateBridge().HandleAsync(
            """{"id":"req14","type":"arm.get"}""",
            CancellationToken.None));

        // Assert
        Assert.False(payload.GetProperty("armed").GetBoolean());
        Assert.Equal(string.Empty, payload.GetProperty("phase").GetString());
        Assert.False(payload.GetProperty("sawLimited").GetBoolean());
        Assert.Empty(payload.GetProperty("projectStatus").EnumerateArray());
        Assert.False(payload.TryGetProperty("probeAgeSeconds", out _));
    }

    [Fact]
    public async Task 完成提交期间_arm_get等待同一配置锁并返回解除后的完整快照()
    {
        const string cycleId = "finishing-cycle";
        var configStore = new ProductConfigStore(_configDir);
        configStore.Update(config =>
        {
            config.Enabled = true;
            config.Armed = true;
            config.ArmCycleId = cycleId;
            config.Selected = new List<ProjectRef>
            {
                new() { Name = "alpha", Path = @"C:\proj\alpha" },
            };
        });

        var stateStore = new ProductStateStore(_dbPath);
        CheckerState initial = stateStore.Load();
        initial.CycleId = cycleId;
        initial.Phase = CheckerState.PhaseResuming;
        stateStore.Save(initial);

        using var stateCommitted = new ManualResetEventSlim(false);
        using var allowConfigCommit = new ManualResetEventSlim(false);
        Task writer = Task.Run(() => configStore.Update(config =>
        {
            CheckerState done = stateStore.Load();
            done.Phase = CheckerState.PhaseDone;
            done.ProjectStatus = new Dictionary<string, string>
            {
                [@"C:\proj\alpha"] = "success",
            };
            stateStore.Save(done);
            stateCommitted.Set();
            Assert.True(allowConfigCommit.Wait(TimeSpan.FromSeconds(5)));
            config.Armed = false;
            config.ArmCycleId = string.Empty;
        }));

        Assert.True(stateCommitted.Wait(TimeSpan.FromSeconds(5)));
        Task<string> read = CreateBridge().HandleAsync(
            """{"id":"req14-lock","type":"arm.get"}""",
            CancellationToken.None);
        await Task.Delay(50);
        Assert.False(read.IsCompleted, "arm.get 不得读到 state 已完成但 config 尚未解除的中间快照。 ");

        allowConfigCommit.Set();
        await writer;
        JsonElement payload = GetPayload(await read);
        Assert.False(payload.GetProperty("armed").GetBoolean());
        Assert.Equal(string.Empty, payload.GetProperty("phase").GetString());
        Assert.Empty(payload.GetProperty("projectStatus").EnumerateArray());
    }

    [Fact]
    public async Task 连续布防完成一轮后_arm_get仍显示当前周期结果()
    {
        const string cycleId = "continuous-cycle";
        var configStore = new ProductConfigStore(_configDir);
        configStore.Update(config =>
        {
            config.Enabled = true;
            config.Armed = true;
            config.Continuous = true;
            config.ArmCycleId = cycleId;
            config.Selected = new List<ProjectRef>
            {
                new() { Name = "alpha", Path = @"C:\proj\alpha" },
            };
        });

        var stateStore = new ProductStateStore(_dbPath);
        CheckerState state = stateStore.Load();
        state.CycleId = cycleId;
        state.Phase = CheckerState.PhaseDone;
        state.ProjectStatus = new Dictionary<string, string>
        {
            { @"C:\proj\alpha", "success" },
        };
        stateStore.Save(state);

        JsonElement payload = GetPayload(await CreateBridge().HandleAsync(
            """{"id":"req14b","type":"arm.get"}""",
            CancellationToken.None));

        Assert.True(payload.GetProperty("armed").GetBoolean());
        Assert.Equal(CheckerState.PhaseDone, payload.GetProperty("phase").GetString());
        JsonElement status = Assert.Single(payload.GetProperty("projectStatus").EnumerateArray());
        Assert.Equal(@"C:\proj\alpha", status.GetProperty("path").GetString());
        Assert.Equal("success", status.GetProperty("status").GetString());
    }

    [Fact]
    public async Task 手动解除布防时_arm_get隐藏尚未完成的旧周期状态()
    {
        var stateStore = new ProductStateStore(_dbPath);
        CheckerState state = stateStore.Load();
        state.CycleId = "disarmed-cycle";
        state.Phase = CheckerState.PhaseResuming;
        state.ProjectStatus = new Dictionary<string, string>
        {
            { @"C:\proj\alpha", "running" },
        };
        stateStore.Save(state);

        JsonElement payload = GetPayload(await CreateBridge().HandleAsync(
            """{"id":"req15","type":"arm.get"}""",
            CancellationToken.None));

        Assert.False(payload.GetProperty("armed").GetBoolean());
        Assert.Empty(payload.GetProperty("projectStatus").EnumerateArray());
        Assert.Equal(string.Empty, payload.GetProperty("phase").GetString());
    }

    [Fact]
    public async Task 完成A后布防B并立即解除_不会重新展示A的结果()
    {
        var stateStore = new ProductStateStore(_dbPath);
        CheckerState state = stateStore.Load();
        state.CycleId = "cycle-a";
        state.Phase = CheckerState.PhaseDone;
        state.ProjectStatus = new Dictionary<string, string>
        {
            [@"C:\proj\alpha"] = "success",
        };
        stateStore.Save(state);

        var bridge = CreateBridge();
        await bridge.HandleAsync(
            $$"""{"id":"req16","type":"arm.set","armed":true,"paths":["{{J(@"C:\proj\beta")}}"]}""",
            CancellationToken.None);

        JsonElement payload = GetPayload(await bridge.HandleAsync(
            """{"id":"req17","type":"arm.set","armed":false}""",
            CancellationToken.None));

        Assert.False(payload.GetProperty("armed").GetBoolean());
        Assert.Equal(string.Empty, payload.GetProperty("phase").GetString());
        Assert.Empty(payload.GetProperty("projectStatus").EnumerateArray());

        // 手动解除不整体覆盖运行状态;旧周期只在界面边界被隐藏。
        CheckerState preserved = stateStore.Load();
        Assert.Equal(CheckerState.PhaseDone, preserved.Phase);
        Assert.Equal("cycle-a", preserved.CycleId);
        Assert.Equal("success", preserved.ProjectStatus![@"C:\proj\alpha"]);
    }

    [Fact]
    public async Task 只有当前running项目绑定的RunId存活才压制久未探测告警()
    {
        const string runId = "12345678-1234-1234-1234-1234567890ab";
        var configStore = new ProductConfigStore(_configDir);
        configStore.Update(config =>
        {
            config.Enabled = true;
            config.Armed = true;
            config.ArmCycleId = "active-cycle";
            config.Selected = new List<ProjectRef>
            {
                new() { Name = "alpha", Path = @"C:\proj\alpha" },
            };
        });

        var stateStore = new ProductStateStore(_dbPath);
        CheckerState state = stateStore.Load();
        state.CycleId = "active-cycle";
        state.Phase = CheckerState.PhaseResuming;
        state.LastProbeUtc = DateTimeOffset.UtcNow.AddHours(-2);
        state.ActiveRunId = runId;
        state.ActiveProjectPath = @"C:\proj\alpha";
        state.ProjectStatus = new Dictionary<string, string>
        {
            [state.ActiveProjectPath] = "running",
        };
        stateStore.Save(state);

        string? observedRunId = null;
        JsonElement payload = GetPayload(await CreateBridge(id =>
        {
            observedRunId = id;
            return true;
        }).HandleAsync("""{"id":"req18","type":"arm.get"}""", CancellationToken.None));

        Assert.Equal(runId, observedRunId);
        Assert.Equal(EngineVerdict.Alive.ToString(), payload.GetProperty("engine").GetString());

        JsonElement stalled = GetPayload(await CreateBridge(_ => false).HandleAsync(
            """{"id":"req19","type":"arm.get"}""",
            CancellationToken.None));
        Assert.Equal(EngineVerdict.RunMissing.ToString(), stalled.GetProperty("engine").GetString());
        JsonElement missingStatus = Assert.Single(stalled.GetProperty("projectStatus").EnumerateArray());
        Assert.Equal("exit-null", missingStatus.GetProperty("status").GetString());
        Assert.Equal("未确认完成", missingStatus.GetProperty("text").GetString());
    }

    [Fact]
    public async Task running对应进程不可核验时_不显示正在续跑()
    {
        var configStore = new ProductConfigStore(_configDir);
        configStore.Update(config =>
        {
            config.Enabled = true;
            config.Armed = true;
            config.ArmCycleId = "active-cycle";
            config.Selected = new List<ProjectRef>
            {
                new() { Name = "alpha", Path = @"C:\proj\alpha" },
            };
        });

        var stateStore = new ProductStateStore(_dbPath);
        CheckerState state = stateStore.Load();
        state.CycleId = "active-cycle";
        state.Phase = CheckerState.PhaseResuming;
        state.LastProbeUtc = DateTimeOffset.UtcNow;
        state.ActiveRunId = "12345678-1234-1234-1234-1234567890ab";
        state.ActiveProjectPath = @"C:\proj\alpha";
        state.ProjectStatus = new Dictionary<string, string>
        {
            [state.ActiveProjectPath] = "running",
        };
        stateStore.Save(state);

        JsonElement payload = GetPayload(await CreateBridge(_ => null).HandleAsync(
            """{"id":"req20","type":"arm.get"}""",
            CancellationToken.None));

        Assert.Equal(EngineVerdict.RunUnverified.ToString(), payload.GetProperty("engine").GetString());
        JsonElement status = Assert.Single(payload.GetProperty("projectStatus").EnumerateArray());
        Assert.Equal("run-unverified", status.GetProperty("status").GetString());
        Assert.Equal("状态未核实", status.GetProperty("text").GetString());
    }

    [Fact]
    public async Task running状态缺少ActiveRunId时_项目行不得显示进行中()
    {
        var configStore = new ProductConfigStore(_configDir);
        configStore.Update(config =>
        {
            config.Enabled = true;
            config.Armed = true;
            config.ArmCycleId = "active-cycle";
            config.Selected = new List<ProjectRef>
            {
                new() { Name = "alpha", Path = @"C:\proj\alpha" },
            };
        });

        var stateStore = new ProductStateStore(_dbPath);
        CheckerState state = stateStore.Load();
        state.CycleId = "active-cycle";
        state.Phase = CheckerState.PhaseResuming;
        state.ProjectStatus = new Dictionary<string, string>
        {
            [@"C:\proj\alpha"] = "running",
        };
        stateStore.Save(state);

        JsonElement payload = GetPayload(await CreateBridge().HandleAsync(
            """{"id":"req20-missing-id","type":"arm.get"}""",
            CancellationToken.None));

        Assert.Equal(EngineVerdict.RunMissing.ToString(), payload.GetProperty("engine").GetString());
        JsonElement status = Assert.Single(payload.GetProperty("projectStatus").EnumerateArray());
        Assert.Equal("exit-null", status.GetProperty("status").GetString());
        Assert.Equal("未确认完成", status.GetProperty("text").GetString());
    }

    [Fact]
    public async Task 已布防但引擎进程无法核验时_返回状态未核实()
    {
        var configStore = new ProductConfigStore(_configDir);
        configStore.Update(config =>
        {
            config.Enabled = true;
            config.Armed = true;
            config.ArmCycleId = "active-cycle";
        });

        var stateStore = new ProductStateStore(_dbPath);
        CheckerState state = stateStore.Load();
        state.CycleId = "active-cycle";
        state.Phase = CheckerState.PhaseWaiting;
        stateStore.Save(state);

        JsonElement payload = GetPayload(await CreateBridge(engineProcessDetector: () => null).HandleAsync(
            """{"id":"req20a","type":"arm.get"}""",
            CancellationToken.None));

        Assert.Equal(EngineVerdict.StateUnverified.ToString(), payload.GetProperty("engine").GetString());
    }

    [Fact]
    public async Task 引擎进程无法核验时_running项目行降级为状态未核实()
    {
        var configStore = new ProductConfigStore(_configDir);
        configStore.Update(config =>
        {
            config.Enabled = true;
            config.Armed = true;
            config.ArmCycleId = "active-cycle";
            config.Selected = new List<ProjectRef>
            {
                new() { Name = "alpha", Path = @"C:\proj\alpha" },
            };
        });

        var stateStore = new ProductStateStore(_dbPath);
        CheckerState state = stateStore.Load();
        state.CycleId = "active-cycle";
        state.Phase = CheckerState.PhaseResuming;
        state.ActiveRunId = "12345678-1234-1234-1234-1234567890ab";
        state.ActiveProjectPath = @"C:\proj\alpha";
        state.ProjectStatus = new Dictionary<string, string>
        {
            [state.ActiveProjectPath] = "running",
        };
        stateStore.Save(state);

        JsonElement payload = GetPayload(await CreateBridge(
            activeResumeDetector: _ => true,
            engineProcessDetector: () => null).HandleAsync(
                """{"id":"req20a-running","type":"arm.get"}""",
                CancellationToken.None));

        Assert.Equal(EngineVerdict.StateUnverified.ToString(), payload.GetProperty("engine").GetString());
        JsonElement status = Assert.Single(payload.GetProperty("projectStatus").EnumerateArray());
        Assert.Equal("run-unverified", status.GetProperty("status").GetString());
    }

    [Fact]
    public async Task Worker不在且活动进程确认消失_running项目行降级为未确认完成()
    {
        var configStore = new ProductConfigStore(_configDir);
        configStore.Update(config =>
        {
            config.Enabled = true;
            config.Armed = true;
            config.ArmCycleId = "active-cycle";
            config.Selected = new List<ProjectRef>
            {
                new() { Name = "alpha", Path = @"C:\proj\alpha" },
            };
        });

        var stateStore = new ProductStateStore(_dbPath);
        CheckerState state = stateStore.Load();
        state.CycleId = "active-cycle";
        state.Phase = CheckerState.PhaseResuming;
        state.ActiveRunId = "12345678-1234-1234-1234-1234567890ab";
        state.ActiveProjectPath = @"C:\proj\alpha";
        state.ProjectStatus = new Dictionary<string, string>
        {
            [state.ActiveProjectPath] = "running",
        };
        stateStore.Save(state);

        JsonElement payload = GetPayload(await CreateBridge(
            activeResumeDetector: _ => false,
            engineProcessDetector: () => false).HandleAsync(
                """{"id":"req20a-stopped","type":"arm.get"}""",
                CancellationToken.None));

        Assert.Equal(EngineVerdict.NotRunning.ToString(), payload.GetProperty("engine").GetString());
        JsonElement status = Assert.Single(payload.GetProperty("projectStatus").EnumerateArray());
        Assert.Equal("exit-null", status.GetProperty("status").GetString());
    }

    [Fact]
    public async Task 重新布防时旧周期ActiveRun仍存活_明确显示续跑仍在进行()
    {
        const string runId = "12345678-1234-1234-1234-1234567890ab";
        var configStore = new ProductConfigStore(_configDir);
        configStore.Update(config =>
        {
            config.Enabled = true;
            config.Armed = true;
            config.ArmCycleId = "new-cycle";
        });

        var stateStore = new ProductStateStore(_dbPath);
        CheckerState state = stateStore.Load();
        state.CycleId = "old-cycle";
        state.Phase = CheckerState.PhaseResuming;
        state.ActiveRunId = runId;
        state.ActiveProjectPath = @"C:\proj\alpha";
        state.ProjectStatus = new Dictionary<string, string>
        {
            [state.ActiveProjectPath] = "running",
        };
        stateStore.Save(state);

        string? observedRunId = null;
        JsonElement payload = GetPayload(await CreateBridge(id =>
        {
            observedRunId = id;
            return true;
        }).HandleAsync("""{"id":"req20b","type":"arm.get"}""", CancellationToken.None));

        Assert.Equal(runId, observedRunId);
        Assert.Equal(EngineVerdict.RunActive.ToString(), payload.GetProperty("engine").GetString());
        Assert.Equal(string.Empty, payload.GetProperty("phase").GetString());
        Assert.Empty(payload.GetProperty("projectStatus").EnumerateArray());
    }

    [Fact]
    public async Task 旧周期ActiveRun无法核验_重新布防后明确显示未确认()
    {
        var configStore = new ProductConfigStore(_configDir);
        configStore.Update(config =>
        {
            config.Enabled = true;
            config.Armed = true;
            config.ArmCycleId = "new-cycle";
        });

        var stateStore = new ProductStateStore(_dbPath);
        CheckerState state = stateStore.Load();
        state.CycleId = "old-cycle";
        state.ActiveRunId = "12345678-1234-1234-1234-1234567890ab";
        state.ActiveProjectPath = @"C:\proj\alpha";
        stateStore.Save(state);

        JsonElement payload = GetPayload(await CreateBridge(_ => null).HandleAsync(
            """{"id":"req20c","type":"arm.get"}""",
            CancellationToken.None));

        Assert.Equal(EngineVerdict.RunUnverified.ToString(), payload.GetProperty("engine").GetString());
    }

    [Fact]
    public async Task 旧周期ActiveRun确认消失_重新布防后明确显示中断()
    {
        var configStore = new ProductConfigStore(_configDir);
        configStore.Update(config =>
        {
            config.Enabled = true;
            config.Armed = true;
            config.ArmCycleId = "new-cycle";
        });

        var stateStore = new ProductStateStore(_dbPath);
        CheckerState state = stateStore.Load();
        state.CycleId = "old-cycle";
        state.ActiveRunId = "12345678-1234-1234-1234-1234567890ab";
        state.ActiveProjectPath = @"C:\proj\alpha";
        stateStore.Save(state);

        JsonElement payload = GetPayload(await CreateBridge(_ => false).HandleAsync(
            """{"id":"req20ca","type":"arm.get"}""",
            CancellationToken.None));

        Assert.Equal(EngineVerdict.RunMissing.ToString(), payload.GetProperty("engine").GetString());
    }

    [Fact]
    public async Task 已解除布防但ActiveRun仍存活_不能显示成普通未布防()
    {
        var stateStore = new ProductStateStore(_dbPath);
        CheckerState state = stateStore.Load();
        state.CycleId = "old-cycle";
        state.ActiveRunId = "12345678-1234-1234-1234-1234567890ab";
        state.ActiveProjectPath = @"C:\proj\alpha";
        stateStore.Save(state);

        JsonElement payload = GetPayload(await CreateBridge(_ => true).HandleAsync(
            """{"id":"req20d","type":"arm.get"}""",
            CancellationToken.None));

        Assert.False(payload.GetProperty("armed").GetBoolean());
        Assert.Equal(EngineVerdict.RunActive.ToString(), payload.GetProperty("engine").GetString());
    }

    [Fact]
    public async Task 旧周期ActiveRun仍存活但Worker不在_引擎停机告警优先()
    {
        var configStore = new ProductConfigStore(_configDir);
        configStore.Update(config =>
        {
            config.Enabled = true;
            config.Armed = true;
            config.ArmCycleId = "new-cycle";
        });

        var stateStore = new ProductStateStore(_dbPath);
        CheckerState state = stateStore.Load();
        state.CycleId = "old-cycle";
        state.ActiveRunId = "12345678-1234-1234-1234-1234567890ab";
        state.ActiveProjectPath = @"C:\proj\alpha";
        stateStore.Save(state);

        JsonElement payload = GetPayload(await CreateBridge(
            activeResumeDetector: _ => true,
            engineProcessDetector: () => false).HandleAsync(
                """{"id":"req20e","type":"arm.get"}""",
                CancellationToken.None));

        Assert.Equal(EngineVerdict.NotRunning.ToString(), payload.GetProperty("engine").GetString());
    }

    [Fact]
    public async Task 终止待确认即使缺少RunId_也不显示正在续跑()
    {
        var configStore = new ProductConfigStore(_configDir);
        configStore.Update(config =>
        {
            config.Enabled = true;
            config.Armed = true;
            config.ArmCycleId = "active-cycle";
            config.Selected = new List<ProjectRef>
            {
                new() { Name = "alpha", Path = @"C:\proj\alpha" },
            };
        });

        var stateStore = new ProductStateStore(_dbPath);
        CheckerState state = stateStore.Load();
        state.CycleId = "active-cycle";
        state.Phase = CheckerState.PhaseResuming;
        state.LastProbeUtc = DateTimeOffset.UtcNow;
        state.ProjectStatus = new Dictionary<string, string>
        {
            [@"C:\proj\alpha"] = "cancel-pending",
        };
        stateStore.Save(state);

        JsonElement payload = GetPayload(await CreateBridge(_ => true).HandleAsync(
            """{"id":"req21","type":"arm.get"}""",
            CancellationToken.None));

        Assert.Equal(EngineVerdict.RunUnverified.ToString(), payload.GetProperty("engine").GetString());
    }

    [Fact]
    public async Task 已解除布防但待终止进程仍存活_明确显示终止待确认()
    {
        var stateStore = new ProductStateStore(_dbPath);
        CheckerState state = stateStore.Load();
        state.PendingCancellationRunId = "12345678-1234-1234-1234-1234567890ab";
        state.PendingCancellationProjectPath = @"C:\proj\alpha";
        state.PendingCancellationCycleId = "old-cycle";
        stateStore.Save(state);

        JsonElement payload = GetPayload(await CreateBridge(_ => true).HandleAsync(
            """{"id":"req22","type":"arm.get"}""",
            CancellationToken.None));

        Assert.False(payload.GetProperty("armed").GetBoolean());
        Assert.Equal(EngineVerdict.CancelPending.ToString(), payload.GetProperty("engine").GetString());
    }

    [Fact]
    public async Task 已布防但Worker不在_待终止状态不能覆盖引擎停机告警()
    {
        var configStore = new ProductConfigStore(_configDir);
        configStore.Update(config =>
        {
            config.Enabled = true;
            config.Armed = true;
            config.ArmCycleId = "active-cycle";
        });

        var stateStore = new ProductStateStore(_dbPath);
        CheckerState state = stateStore.Load();
        state.CycleId = "active-cycle";
        state.PendingCancellationRunId = "12345678-1234-1234-1234-1234567890ab";
        state.PendingCancellationProjectPath = @"C:\proj\alpha";
        state.PendingCancellationCycleId = "active-cycle";
        stateStore.Save(state);

        JsonElement payload = GetPayload(await CreateBridge(
            activeResumeDetector: _ => true,
            engineProcessDetector: () => false).HandleAsync(
                """{"id":"req23","type":"arm.get"}""",
                CancellationToken.None));

        Assert.Equal(EngineVerdict.NotRunning.ToString(), payload.GetProperty("engine").GetString());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task 状态数据库损坏时_arm_get明确返回未核实且不泄露旧项目状态(bool armed)
    {
        var configStore = new ProductConfigStore(_configDir);
        configStore.Update(config =>
        {
            config.Enabled = true;
            config.Armed = armed;
            config.ArmCycleId = armed ? "active-cycle" : string.Empty;
            config.Selected = new List<ProjectRef>
            {
                new() { Name = "alpha", Path = @"C:\proj\alpha" },
            };
        });

        using (var connection = StorageDatabase.Open(_dbPath))
        {
            StorageDatabase.Execute(connection,
                "UPDATE product_state SET state_json = $json WHERE id = 1;",
                null,
                ("$json", "{\"phase\":\"done\",\"projectStatus\":{\"C:\\\\proj\\\\alpha\":\"success\"}"));
        }

        JsonElement payload = GetPayload(await CreateBridge().HandleAsync(
            """{"id":"req24","type":"arm.get"}""",
            CancellationToken.None));

        Assert.Equal(armed, payload.GetProperty("armed").GetBoolean());
        Assert.Equal(EngineVerdict.StateUnverified.ToString(), payload.GetProperty("engine").GetString());
        Assert.Equal("布防状态暂时无法核实", payload.GetProperty("engineText").GetString());
        Assert.Equal(string.Empty, payload.GetProperty("phase").GetString());
        Assert.False(payload.GetProperty("sawLimited").GetBoolean());
        Assert.Empty(payload.GetProperty("projectStatus").EnumerateArray());
        Assert.False(payload.TryGetProperty("probeAgeSeconds", out _));
    }

    [Fact]
    public async Task 状态数据库损坏且已布防但Worker不在_引擎停机告警优先()
    {
        var configStore = new ProductConfigStore(_configDir);
        configStore.Update(config =>
        {
            config.Enabled = true;
            config.Armed = true;
            config.ArmCycleId = "active-cycle";
        });

        using (var connection = StorageDatabase.Open(_dbPath))
        {
            StorageDatabase.Execute(connection,
                "UPDATE product_state SET state_json = '' WHERE id = 1;");
        }

        JsonElement payload = GetPayload(await CreateBridge(engineProcessDetector: () => false).HandleAsync(
            """{"id":"req25","type":"arm.get"}""",
            CancellationToken.None));

        Assert.Equal(EngineVerdict.NotRunning.ToString(), payload.GetProperty("engine").GetString());
    }
}
