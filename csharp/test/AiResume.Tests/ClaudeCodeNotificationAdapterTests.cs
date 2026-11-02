using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using AiResume.Worker.Notifications;
using Xunit;

namespace AiResume.Tests;

/// <summary>
/// ClaudeCodeNotificationAdapter:三组 exec form hooks(Stop / StopFailure / Notification)的合并、迁移与清理。
/// 配置形状照抄官方 hooks 文档(command + args + timeout,2.1.139+)。全部在临时目录，绝不碰真实 ~/.claude。
/// </summary>
public class ClaudeCodeNotificationAdapterTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _settingsPath;
    private readonly ClaudeCodeNotificationAdapter _adapter;

    // 路径刻意含空格：安装目录就叫 "AI Resume",exec form 不经过 shell,带空格的绝对路径合法。
    private const string TestHookExe = "C:\\Users\\x\\AppData\\Local\\AI Resume\\AiResume.Hook.exe";

    public ClaudeCodeNotificationAdapterTests()
    {
        _tempDir = TestTemp.NewDir("AiResumeTests");
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

    private sealed record OwnHook(string Event, string? Matcher, string Command, string[] Args, int GroupSize);

    private OwnHook[] ReadOwnHooks()
    {
        using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(_settingsPath));
        if (!doc.RootElement.TryGetProperty("hooks", out JsonElement hooks))
        {
            return [];
        }

        var result = new List<OwnHook>();
        foreach (JsonProperty eventProperty in hooks.EnumerateObject())
        {
            foreach (JsonElement group in eventProperty.Value.EnumerateArray())
            {
                JsonElement entries = group.GetProperty("hooks");
                foreach (JsonElement hook in entries.EnumerateArray())
                {
                    string command = hook.GetProperty("command").GetString() ?? string.Empty;
                    if (!command.Contains("AiResume.Hook.exe", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    string[] args = hook.TryGetProperty("args", out JsonElement argsElement)
                        ? argsElement.EnumerateArray().Select(a => a.GetString()!).ToArray()
                        : [];
                    string? matcher = group.TryGetProperty("matcher", out JsonElement m) ? m.GetString() : null;
                    result.Add(new OwnHook(eventProperty.Name, matcher, command, args, entries.GetArrayLength()));
                }
            }
        }

        return result.ToArray();
    }

    [Fact]
    public void Probe_DirectoryNotExists_ReturnsNotInstalled()
    {
        Directory.Delete(_tempDir, true);

        var status = _adapter.Probe();

        Assert.False(status.IsInstalled);
        Assert.False(status.IsEnabled);
        Assert.Null(status.ConfigPath);
        Assert.NotNull(status.Detail);
    }

    [Fact]
    public void Probe_DirectoryExistsNoFile_ReturnsInstalledNotEnabled()
    {
        var status = _adapter.Probe();

        Assert.True(status.IsInstalled);
        Assert.False(status.IsEnabled);
        Assert.Equal(_settingsPath, status.ConfigPath);
    }

    [Fact]
    public void Enable_按官方exec形式写入完成_中断_等你三组()
    {
        _adapter.Enable(TestHookExe);

        OwnHook[] own = ReadOwnHooks();
        Assert.Equal(3, own.Length);

        OwnHook stop = Assert.Single(own, h => h.Event == "Stop");
        Assert.Null(stop.Matcher);                       // 官方:Stop 不支持 matcher
        Assert.Equal(TestHookExe, stop.Command);         // exec form:command 只放 exe,不加引号
        Assert.Equal(new[] { "claudecode" }, stop.Args);

        OwnHook failure = Assert.Single(own, h => h.Event == "StopFailure");
        Assert.Null(failure.Matcher);                    // 不写 matcher = 接收全部 error
        Assert.Equal(new[] { "claudecode", "--kind=failure" }, failure.Args);

        OwnHook decision = Assert.Single(own, h => h.Event == "Notification");
        Assert.Equal(ClaudeCodeNotificationAdapter.DecisionMatcher, decision.Matcher);
        Assert.Contains("quota_auto_resume_stale", decision.Matcher, StringComparison.Ordinal);
        Assert.Contains("permission_prompt", decision.Matcher, StringComparison.Ordinal);
        Assert.DoesNotContain("idle_prompt", decision.Matcher, StringComparison.Ordinal);
        Assert.Equal(new[] { "claudecode", "--kind=decision" }, decision.Args);

        var status = _adapter.Probe();
        Assert.True(status.IsEnabled);
        Assert.False(status.HookBroken);
        Assert.Equal(TestHookExe, status.HookCommand);
    }

    [Fact]
    public void Enable_重复调用幂等且不改写文件()
    {
        _adapter.Enable(TestHookExe);
        string first = File.ReadAllText(_settingsPath);
        DateTime firstWrite = File.GetLastWriteTimeUtc(_settingsPath);

        _adapter.Enable(TestHookExe);

        Assert.Equal(first, File.ReadAllText(_settingsPath));
        Assert.Equal(firstWrite, File.GetLastWriteTimeUtc(_settingsPath));
    }

    [Fact]
    public void Enable_把旧shell格式迁移为exec形式且不留第二条()
    {
        // v2.1 及以前写出的真实形状。
        File.WriteAllText(_settingsPath, """
            {
              "model": "opus",
              "hooks": {
                "Stop": [
                  {"matcher":"","hooks":[{"type":"command","command":"\"C:\\old\\AI Resume\\AiResume.Hook.exe\" claudecode","timeout":30}]}
                ],
                "Notification": [
                  {"matcher":"agent_needs_input|elicitation_dialog|elicitation_url_dialog|permission_prompt","hooks":[{"type":"command","command":"\"C:\\old\\AI Resume\\AiResume.Hook.exe\" claudecode --kind=decision","timeout":30}]}
                ]
              }
            }
            """);
        Assert.True(_adapter.Probe().HookBroken);   // 只剩旧格式两组:中断类永远不会到,必须标出来

        _adapter.Enable(TestHookExe);

        OwnHook[] own = ReadOwnHooks();
        Assert.Equal(3, own.Length);
        Assert.All(own, h => Assert.Equal(TestHookExe, h.Command));
        Assert.DoesNotContain("C:\\old", File.ReadAllText(_settingsPath), StringComparison.Ordinal);
        Assert.Contains("\"model\": \"opus\"", File.ReadAllText(_settingsPath), StringComparison.Ordinal);
        Assert.False(_adapter.Probe().HookBroken);
    }

    [Fact]
    public void Enable_我方条目混在用户分组时移出单独成组且不改用户matcher()
    {
        File.WriteAllText(_settingsPath, """
            {"hooks":{"Notification":[
              {"matcher":"permission_prompt","hooks":[
                {"type":"command","command":"C:\\me\\beep.exe"},
                {"type":"command","command":"C:\\tools\\AiResume.Hook.exe","args":["claudecode","--kind=decision"]}
              ]}
            ]}}
            """);

        _adapter.Enable(TestHookExe);

        using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(_settingsPath));
        JsonElement groups = doc.RootElement.GetProperty("hooks").GetProperty("Notification");
        JsonElement userGroup = groups.EnumerateArray().Single(g =>
            g.GetProperty("hooks").EnumerateArray().Any(h => h.GetProperty("command").GetString() == "C:\\me\\beep.exe"));
        Assert.Equal("permission_prompt", userGroup.GetProperty("matcher").GetString());
        Assert.Equal(1, userGroup.GetProperty("hooks").GetArrayLength());
        OwnHook decision = Assert.Single(ReadOwnHooks(), h => h.Event == "Notification");
        Assert.Equal(1, decision.GroupSize);
        Assert.Equal(ClaudeCodeNotificationAdapter.DecisionMatcher, decision.Matcher);
    }

    [Fact]
    public void Disable_清掉全部我方条目并保留用户钩子()
    {
        _adapter.Enable(TestHookExe);
        JsonNode root = JsonNode.Parse(File.ReadAllText(_settingsPath))!;
        ((JsonObject)root["hooks"]!)["Notification"]!.AsArray().Add(new JsonObject
        {
            ["matcher"] = "idle_prompt",
            ["hooks"] = new JsonArray { new JsonObject { ["type"] = "command", ["command"] = "user-own.exe" } },
        });
        ((JsonObject)root["hooks"]!)["SubagentStop"] = new JsonArray
        {
            new JsonObject
            {
                ["hooks"] = new JsonArray
                {
                    new JsonObject { ["type"] = "command", ["command"] = TestHookExe, ["args"] = new JsonArray { "claudecode" } },
                },
            },
        };
        File.WriteAllText(_settingsPath, root.ToJsonString());

        _adapter.Disable();

        string json = File.ReadAllText(_settingsPath);
        Assert.Empty(ReadOwnHooks());
        Assert.Contains("user-own.exe", json, StringComparison.Ordinal);
        using JsonDocument doc = JsonDocument.Parse(json);
        JsonElement hooks = doc.RootElement.GetProperty("hooks");
        Assert.False(hooks.TryGetProperty("Stop", out _));
        Assert.False(hooks.TryGetProperty("StopFailure", out _));
        Assert.False(hooks.TryGetProperty("SubagentStop", out _));
        Assert.False(_adapter.Probe().IsEnabled);
    }

    [Fact]
    public void Enable_保留其它顶层字段与别的事件()
    {
        File.WriteAllText(_settingsPath, """
            {"theme":"dark","hooks":{"SessionStart":[{"matcher":"","hooks":[{"type":"command","command":"C:\\other\\session-start.cmd","timeout":15}]}]}}
            """);

        _adapter.Enable(TestHookExe);

        using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(_settingsPath));
        Assert.Equal("dark", doc.RootElement.GetProperty("theme").GetString());
        JsonElement sessionStart = doc.RootElement.GetProperty("hooks").GetProperty("SessionStart");
        Assert.Equal("C:\\other\\session-start.cmd",
            sessionStart[0].GetProperty("hooks")[0].GetProperty("command").GetString());
        Assert.True(File.Exists(_settingsPath + ".bak"));
    }

    [Fact]
    public void 参数不同的同名exe与参数里出现标记都不算我方条目()
    {
        File.WriteAllText(_settingsPath, """
            {"hooks":{"Stop":[
              {"hooks":[{"type":"command","command":"\"C:\\tools\\notify.exe\" --label AiResume.Hook.exe"}]},
              {"hooks":[{"type":"command","command":"C:\\tools\\AiResume.Hook.exe","args":["claudecode","--verbose"]}]}
            ]}}
            """);

        _adapter.Enable(TestHookExe);
        _adapter.Disable();

        string preserved = File.ReadAllText(_settingsPath);
        Assert.Contains("--label AiResume.Hook.exe", preserved, StringComparison.Ordinal);
        Assert.Contains("--verbose", preserved, StringComparison.Ordinal);
    }

    [Fact]
    public void Disable_未启用时不抛异常()
    {
        Assert.Null(Record.Exception(() => _adapter.Disable()));
        Directory.Delete(_tempDir, true);
        Assert.Null(Record.Exception(() => _adapter.Disable()));
    }

    [Fact]
    public void Probe_非法JSON不抛异常()
    {
        File.WriteAllText(_settingsPath, "{ invalid json content !!!");

        var status = _adapter.Probe();

        Assert.True(status.IsInstalled);
        Assert.False(status.IsEnabled);
        Assert.Contains("配置读取失败", status.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void Enable_拒绝相对路径或带引号的可执行文件()
    {
        Assert.Throws<ArgumentException>(() => _adapter.Enable("AiResume.Hook.exe"));
        Assert.False(File.Exists(_settingsPath));
    }
}
