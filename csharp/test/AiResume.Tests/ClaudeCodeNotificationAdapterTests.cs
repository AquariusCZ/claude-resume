using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using AiResume.Worker.Notifications;
using Xunit;

namespace AiResume.Tests;

/// <summary>
/// ClaudeCodeNotificationAdapter 的单元测试。
/// 所有测试均在系统临时目录下创建唯一子目录,避免触碰真实用户配置。
/// </summary>
public class ClaudeCodeNotificationAdapterTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _settingsPath;
    private readonly ClaudeCodeNotificationAdapter _adapter;

    public ClaudeCodeNotificationAdapterTests()
    {
        _tempDir = TestTemp.NewDir("AiResumeTests");
        Directory.CreateDirectory(_tempDir);
        _settingsPath = Path.Combine(_tempDir, "settings.json");
        _adapter = new ClaudeCodeNotificationAdapter(_settingsPath);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDir))
            {
                Directory.Delete(_tempDir, true);
            }
        }
        catch
        {
            // 清理失败忽略
        }
    }

    // 必须含 ClaudeCodeNotificationAdapter.MarkerFileName("AiResume.Hook.exe"),
    // 否则写入的条目不会被自身的所有权判定认出,IsEnabled 与幂等断言都会失败。
    private const string TestHookCommand = "C:\\tools\\AiResume.Hook.exe";
    private const string ExpectedStoredCommand = "\"C:\\tools\\AiResume.Hook.exe\" claudecode";

    /// <summary>
    /// 场景1:目录不存在 -> Probe 的 IsInstalled=false,不抛异常。
    /// </summary>
    [Fact]
    public void Probe_DirectoryNotExists_ReturnsNotInstalled()
    {
        // 删除目录
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, true);
        }

        var status = _adapter.Probe();

        Assert.False(status.IsInstalled);
        Assert.False(status.IsEnabled);
        Assert.Null(status.ConfigPath);
        Assert.NotNull(status.Detail);
    }

    /// <summary>
    /// 场景2:目录存在但无 settings.json -> IsInstalled=true, IsEnabled=false。
    /// </summary>
    [Fact]
    public void Probe_DirectoryExistsNoFile_ReturnsInstalledNotEnabled()
    {
        var status = _adapter.Probe();

        Assert.True(status.IsInstalled);
        Assert.False(status.IsEnabled);
        Assert.Equal(_settingsPath, status.ConfigPath);
        Assert.NotNull(status.Detail);
    }

    /// <summary>
    /// 场景3:Enable 后 IsEnabled=true 且 settings.json 中出现含 MarkerFileName 的 command。
    /// </summary>
    [Fact]
    public void Enable_CreatesEntry_IsEnabledTrue()
    {
        _adapter.Enable(TestHookCommand);

        var status = _adapter.Probe();
        Assert.True(status.IsInstalled);
        Assert.True(status.IsEnabled);

        var json = File.ReadAllText(_settingsPath);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        Assert.True(root.TryGetProperty("hooks", out var hooks));
        Assert.True(hooks.TryGetProperty("Stop", out var stop));
        Assert.Equal(JsonValueKind.Array, stop.ValueKind);
        Assert.Single(stop.EnumerateArray());

        var entry = stop[0];
        Assert.True(entry.TryGetProperty("hooks", out var entryHooks));
        Assert.Equal(JsonValueKind.Array, entryHooks.ValueKind);
        Assert.Single(entryHooks.EnumerateArray());

        var hook = entryHooks[0];
        Assert.True(hook.TryGetProperty("command", out var cmd));
        Assert.Equal(ExpectedStoredCommand, cmd.GetString());
        Assert.Contains(ClaudeCodeNotificationAdapter.MarkerFileName, cmd.GetString(), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 场景4:既有配置保留 - 预置含其他顶层字段与其他 hook 事件的 settings.json,Enable 后这些内容必须原样保留。
    /// </summary>
    [Fact]
    public void Enable_PreservesExistingConfiguration()
    {
        var existingJson = new JsonObject
        {
            ["theme"] = "dark",
            ["hooks"] = new JsonObject
            {
                ["SessionStart"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["matcher"] = "",
                        ["hooks"] = new JsonArray
                        {
                            new JsonObject
                            {
                                ["type"] = "command",
                                ["command"] = "C:\\other\\session-start.cmd",
                                ["timeout"] = 15
                            }
                        }
                    }
                }
            }
        };
        File.WriteAllText(_settingsPath, existingJson.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));

        _adapter.Enable(TestHookCommand);

        var json = File.ReadAllText(_settingsPath);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        // 顶层字段保留
        Assert.True(root.TryGetProperty("theme", out var theme));
        Assert.Equal("dark", theme.GetString());

        // SessionStart 保留
        Assert.True(root.TryGetProperty("hooks", out var hooks));
        Assert.True(hooks.TryGetProperty("SessionStart", out var sessionStart));
        Assert.Equal(JsonValueKind.Array, sessionStart.ValueKind);
        Assert.Single(sessionStart.EnumerateArray());

        var sessionEntry = sessionStart[0];
        Assert.True(sessionEntry.TryGetProperty("hooks", out var sessionHooks));
        Assert.Equal(JsonValueKind.Array, sessionHooks.ValueKind);
        Assert.Single(sessionHooks.EnumerateArray());
        Assert.True(sessionHooks[0].TryGetProperty("command", out var sessionCmd));
        Assert.Equal("C:\\other\\session-start.cmd", sessionCmd.GetString());

        // Stop 中我方条目存在
        Assert.True(hooks.TryGetProperty("Stop", out var stop));
        Assert.Equal(JsonValueKind.Array, stop.ValueKind);
        Assert.Single(stop.EnumerateArray());
    }

    /// <summary>
    /// 场景5:重复 Enable 幂等 - 连续两次 Enable,hooks.Stop 中我方条目只有一条。
    /// </summary>
    [Fact]
    public void Enable_Twice_Idempotent()
    {
        _adapter.Enable(TestHookCommand);
        _adapter.Enable(TestHookCommand);

        var json = File.ReadAllText(_settingsPath);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        Assert.True(root.TryGetProperty("hooks", out var hooks));
        Assert.True(hooks.TryGetProperty("Stop", out var stop));
        Assert.Equal(JsonValueKind.Array, stop.ValueKind);
        Assert.Single(stop.EnumerateArray());
    }

    /// <summary>
    /// 场景6:Disable 只移除我方 - 预置我方条目 + 别人的 Stop 条目,Disable 后别人的还在、我方的没了。
    /// </summary>
    [Fact]
    public void Disable_RemovesOnlyOwnEntries()
    {
        var existingJson = new JsonObject
        {
            ["hooks"] = new JsonObject
            {
                ["Stop"] = new JsonArray
                {
                    // 别人的条目
                    new JsonObject
                    {
                        ["matcher"] = "",
                        ["hooks"] = new JsonArray
                        {
                            new JsonObject
                            {
                                ["type"] = "command",
                                ["command"] = "C:\\other\\stop-hook.cmd",
                                ["timeout"] = 30
                            }
                        }
                    },
                    // 我方条目
                    new JsonObject
                    {
                        ["matcher"] = "",
                        ["hooks"] = new JsonArray
                        {
                            new JsonObject
                            {
                                ["type"] = "command",
                                ["command"] = TestHookCommand,
                                ["timeout"] = 30
                            }
                        }
                    }
                }
            }
        };
        File.WriteAllText(_settingsPath, existingJson.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));

        _adapter.Disable();

        var json = File.ReadAllText(_settingsPath);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        Assert.True(root.TryGetProperty("hooks", out var hooks));
        Assert.True(hooks.TryGetProperty("Stop", out var stop));
        Assert.Equal(JsonValueKind.Array, stop.ValueKind);
        Assert.Single(stop.EnumerateArray());

        var remainingEntry = stop[0];
        Assert.True(remainingEntry.TryGetProperty("hooks", out var entryHooks));
        Assert.Equal(JsonValueKind.Array, entryHooks.ValueKind);
        Assert.Single(entryHooks.EnumerateArray());
        Assert.True(entryHooks[0].TryGetProperty("command", out var cmd));
        Assert.Equal("C:\\other\\stop-hook.cmd", cmd.GetString());
        Assert.DoesNotContain(ClaudeCodeNotificationAdapter.MarkerFileName, cmd.GetString(), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 场景7:Disable 未启用时不抛异常(幂等)。
    /// </summary>
    [Fact]
    public void Disable_NotEnabled_NoException()
    {
        // 目录存在但无文件
        var exception = Record.Exception(() => _adapter.Disable());
        Assert.Null(exception);

        // 目录不存在
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, true);
        }
        exception = Record.Exception(() => _adapter.Disable());
        Assert.Null(exception);
    }

    /// <summary>
    /// 场景8:settings.json 内容为非法 JSON -> Probe 不抛异常。
    /// </summary>
    [Fact]
    public void Probe_InvalidJson_NoException()
    {
        File.WriteAllText(_settingsPath, "{ invalid json content !!!");

        var exception = Record.Exception(() => _adapter.Probe());
        Assert.Null(exception);

        var status = _adapter.Probe();
        Assert.True(status.IsInstalled);
        Assert.False(status.IsEnabled);
        Assert.NotNull(status.Detail);
        Assert.Contains("配置读取失败", status.Detail);
    }

    /// <summary>
    /// 场景9:Enable 会生成 .bak 备份文件。
    /// </summary>
    [Fact]
    public void Enable_CreatesBackupFile()
    {
        // 先创建初始配置
        var initialJson = new JsonObject
        {
            ["theme"] = "light"
        };
        File.WriteAllText(_settingsPath, initialJson.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));

        _adapter.Enable(TestHookCommand);

        var bakPath = _settingsPath + ".bak";
        Assert.True(File.Exists(bakPath));

        // 备份内容应为原始内容
        var bakContent = File.ReadAllText(bakPath);
        using var bakDoc = JsonDocument.Parse(bakContent);
        var bakRoot = bakDoc.RootElement;
        Assert.True(bakRoot.TryGetProperty("theme", out var theme));
        Assert.Equal("light", theme.GetString());
        Assert.False(bakRoot.TryGetProperty("hooks", out _));
    }

    [Fact]
    public void Enable_RefreshesLegacyBarePathWithQuotedSourceCommand()
    {
        var legacy = new JsonObject
        {
            ["hooks"] = new JsonObject
            {
                ["Stop"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["matcher"] = "",
                        ["hooks"] = new JsonArray
                        {
                            new JsonObject
                            {
                                ["type"] = "command",
                                ["command"] = TestHookCommand,
                                ["timeout"] = 30,
                            },
                        },
                    },
                },
            },
        };
        File.WriteAllText(_settingsPath, legacy.ToJsonString());

        _adapter.Enable(TestHookCommand);

        Assert.Equal(ExpectedStoredCommand, _adapter.Probe().HookCommand);
    }

    [Fact]
    public void Disable_DoesNotTreatMarkerInUserArgumentsAsOwnership()
    {
        const string userCommand = "\"C:\\tools\\notify.exe\" --label AiResume.Hook.exe";
        var root = new JsonObject
        {
            ["hooks"] = new JsonObject
            {
                ["Stop"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["hooks"] = new JsonArray
                        {
                            new JsonObject { ["type"] = "command", ["command"] = userCommand },
                        },
                    },
                },
            },
        };
        File.WriteAllText(_settingsPath, root.ToJsonString());

        _adapter.Enable(TestHookCommand);
        _adapter.Disable();

        string preserved = File.ReadAllText(_settingsPath);
        Assert.Contains("notify.exe", preserved);
        Assert.Contains("--label AiResume.Hook.exe", preserved);
    }
}
