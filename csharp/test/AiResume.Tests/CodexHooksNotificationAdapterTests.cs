using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AiResume.Worker.Notifications;
using Tomlyn;
using Tomlyn.Model;
using Xunit;

namespace AiResume.Tests;

/// <summary>
/// Codex hooks 适配器:hooks.json 只增删自己那一组,config.toml 只增删自己的信任记录,
/// 哈希与固定上游 0.148.0 的 hook_hash/version_for_toml 一致(用户现有条目 2026-10-12 复算验证)。
/// 全部在临时 Codex home 下进行,不碰真机 ~/.codex。
/// </summary>
public sealed class CodexHooksNotificationAdapterTests
{
    private const string HookExe = @"C:\Users\me\AppData\Local\AI Resume\AiResume.Hook.exe";

    [Fact]
    public void 信任哈希与上游canonicalJson算法一致()
    {
        const string command = "\"C:\\x\\AiResume.Hook.exe\" codex";
        // 上游:TOML 值 → JSON → 递归按键排序 → 紧凑序列化 → SHA-256 → "sha256:" + 小写 hex。
        string canonical = """{"event_name":"stop","hooks":[{"async":false,"command":"\"C:\\x\\AiResume.Hook.exe\" codex","timeout":30,"type":"command"}]}""";
        string expected = "sha256:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();

        Assert.Equal(expected, CodexHooksNotificationAdapter.ComputeTrustHash(command));
    }

    [Fact]
    public void 用户现有interrupt条目作为oracle证明序列化形状正确()
    {
        // 本机 codex-chatgpt-web 写入的条目(2026-10-12 读取):同一算法对 event interrupt / timeout 3 的结果。
        const string command = "\"C:\\Users\\23230\\.codex-chatgpt-web\\versions\\5.0.8-win32-x64\\runtime\\bun.exe\" \"C:\\Users\\23230\\.codex-chatgpt-web\\versions\\5.0.8-win32-x64\\app\\cli.js\" \"--home\" \"C:\\Users\\23230\\.codex-chatgpt-web\" \"hook\" \"interrupt\"";
        string canonical = JsonSerializer.Serialize(new SortedDictionary<string, object?>(StringComparer.Ordinal)
        {
            ["event_name"] = "interrupt",
            ["hooks"] = new object[]
            {
                new SortedDictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["async"] = false, ["command"] = command, ["timeout"] = 3, ["type"] = "command",
                },
            },
        }, new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
        string hash = "sha256:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();

        Assert.Equal("sha256:c1232d05f083d5f93608f67adafbcd813724743ef9068d1734694a7b20c14052", hash);
    }

    [Fact]
    public void Enable写入hooks并登记信任_Probe报已启用_Disable全部收回()
    {
        string home = TestTemp.NewDir("codex-hooks");
        string config = Path.Combine(home, "config.toml");
        string original = "model = \"gpt-5\"\nnotify = [\"C:\\\\x\\\\other.exe\", \"turn-ended\"]\n\n[features]\nhooks = true\n";
        File.WriteAllText(config, original);
        var adapter = new CodexHooksNotificationAdapter(home);
        Assert.False(adapter.Probe().IsEnabled);

        adapter.Enable(HookExe);

        string hooksJson = File.ReadAllText(adapter.HooksPath);
        using (JsonDocument doc = JsonDocument.Parse(hooksJson))
        {
            JsonElement stop = doc.RootElement.GetProperty("hooks").GetProperty("Stop");
            JsonElement handler = stop[0].GetProperty("hooks")[0];
            Assert.Equal("command", handler.GetProperty("type").GetString());
            Assert.Equal("\"" + HookExe + "\" codex", handler.GetProperty("command").GetString());
            Assert.Equal(30, handler.GetProperty("timeout").GetInt32());
        }

        TomlTable root = TomlSerializer.Deserialize<TomlTable>(File.ReadAllText(config))!;
        var state = (TomlTable)((TomlTable)root["hooks"])["state"];
        string key = adapter.StateKey(0, 0);
        Assert.Equal(CodexHooksNotificationAdapter.ComputeTrustHash("\"" + HookExe + "\" codex"),
            ((TomlTable)state[key])["trusted_hash"]);
        // 用户原有内容逐字保留(含原换行风格),我方记录只追加在末尾。
        Assert.StartsWith(original, File.ReadAllText(config), StringComparison.Ordinal);
        Assert.Contains("notify = [\"C:\\\\x\\\\other.exe\", \"turn-ended\"]", File.ReadAllText(config), StringComparison.Ordinal);

        NotificationProviderStatus status = adapter.Probe();
        Assert.True(status.IsInstalled);
        Assert.True(status.IsEnabled);
        Assert.False(status.HookBroken);
        Assert.Contains("信任哈希已登记", status.Detail, StringComparison.Ordinal);
        Assert.Equal("\"" + HookExe + "\" codex", status.HookCommand);

        // 幂等:再启用一次不会追加第二组,也不会追加第二条信任记录。
        adapter.Enable(HookExe);
        using (JsonDocument doc = JsonDocument.Parse(File.ReadAllText(adapter.HooksPath)))
        {
            Assert.Equal(1, doc.RootElement.GetProperty("hooks").GetProperty("Stop").GetArrayLength());
        }
        Assert.Equal(1, File.ReadAllText(config).Split("[hooks.state.").Length - 1);

        adapter.Disable();
        Assert.False(File.Exists(adapter.HooksPath));
        string after = File.ReadAllText(config);
        Assert.DoesNotContain("hooks.state", after, StringComparison.Ordinal);
        Assert.Contains("[features]", after, StringComparison.Ordinal);
        Assert.Contains("hooks = true", after, StringComparison.Ordinal);
        Assert.False(adapter.Probe().IsEnabled);
    }

    [Fact]
    public void 用户自己的hooks与信任记录原样保留且我方追加在末尾()
    {
        string home = TestTemp.NewDir("codex-hooks-user");
        string hooksPath = Path.Combine(home, "hooks.json");
        File.WriteAllText(hooksPath, """
            {"hooks":{"Stop":[{"hooks":[{"type":"command","command":"python3 mine.py","timeout":5}]}],"PreToolUse":[{"matcher":"^Bash$","hooks":[{"type":"command","command":"guard.py"}]}]}}
            """);
        string config = Path.Combine(home, "config.toml");
        string userKey = hooksPath.Replace("\\", "\\\\") + ":stop:0:0";
        File.WriteAllText(config, "[hooks.state.\"" + userKey + "\"]\ntrusted_hash = \"sha256:user\"\n");
        var adapter = new CodexHooksNotificationAdapter(home);

        adapter.Enable(HookExe);

        using (JsonDocument doc = JsonDocument.Parse(File.ReadAllText(hooksPath)))
        {
            JsonElement stop = doc.RootElement.GetProperty("hooks").GetProperty("Stop");
            Assert.Equal(2, stop.GetArrayLength());
            Assert.Equal("python3 mine.py", stop[0].GetProperty("hooks")[0].GetProperty("command").GetString());
            Assert.Equal(1, doc.RootElement.GetProperty("hooks").GetProperty("PreToolUse").GetArrayLength());
        }
        string dump = File.ReadAllText(config);
        TomlTable root = TomlSerializer.Deserialize<TomlTable>(dump)!;
        var state = (TomlTable)((TomlTable)root["hooks"])["state"];
        Assert.True(state.ContainsKey(hooksPath + ":stop:0:0"), "keys=" + string.Join("|", state.Keys) + "\n" + dump);
        Assert.Equal("sha256:user", ((TomlTable)state[hooksPath + ":stop:0:0"])["trusted_hash"]);
        Assert.True(state.ContainsKey(adapter.StateKey(1, 0)), "keys=" + string.Join("|", state.Keys) + "\n" + dump);

        adapter.Disable();
        using (JsonDocument doc = JsonDocument.Parse(File.ReadAllText(hooksPath)))
        {
            Assert.Equal(1, doc.RootElement.GetProperty("hooks").GetProperty("Stop").GetArrayLength());
        }
        root = TomlSerializer.Deserialize<TomlTable>(File.ReadAllText(config))!;
        state = (TomlTable)((TomlTable)root["hooks"])["state"];
        Assert.True(state.ContainsKey(hooksPath + ":stop:0:0"));
        Assert.False(state.ContainsKey(adapter.StateKey(1, 0)));
    }

    [Fact]
    public void Disable后排在我方之后的分组信任键序号前移()
    {
        string home = TestTemp.NewDir("codex-hooks-renumber");
        var adapter = new CodexHooksNotificationAdapter(home);
        adapter.Enable(HookExe);
        // 之后另一个工具追加了 group 1 并被 Codex 信任。
        string hooksPath = adapter.HooksPath;
        var root = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(hooksPath))!.AsObject();
        root["hooks"]!["Stop"]!.AsArray().Add(new System.Text.Json.Nodes.JsonObject
        {
            ["hooks"] = new System.Text.Json.Nodes.JsonArray(new System.Text.Json.Nodes.JsonObject
            {
                ["type"] = "command", ["command"] = "other.exe interrupt", ["timeout"] = 3,
            }),
        });
        File.WriteAllText(hooksPath, root.ToJsonString());
        string config = Path.Combine(home, "config.toml");
        string otherKey = adapter.StateKey(1, 0);
        File.AppendAllText(config, "\n[hooks.state." + TomlQuote(otherKey) + "]\ntrusted_hash = \"sha256:other\"\n");

        adapter.Disable();

        using (JsonDocument doc = JsonDocument.Parse(File.ReadAllText(hooksPath)))
        {
            JsonElement remaining = doc.RootElement.GetProperty("hooks").GetProperty("Stop");
            Assert.Equal(1, remaining.GetArrayLength());
            Assert.Equal("other.exe interrupt", remaining[0].GetProperty("hooks")[0].GetProperty("command").GetString());
        }
        TomlTable parsed = TomlSerializer.Deserialize<TomlTable>(File.ReadAllText(config))!;
        var state = (TomlTable)((TomlTable)parsed["hooks"])["state"];
        Assert.False(state.ContainsKey(otherKey));
        Assert.Equal("sha256:other", ((TomlTable)state[adapter.StateKey(0, 0)])["trusted_hash"]);
    }

    [Fact]
    public void hooks写入失败时回滚刚写的信任记录()
    {
        string home = TestTemp.NewDir("codex-hooks-rollback");
        // 同名目录让 hooks.json 无法落盘:信任记录先写、后回滚,不得留下「有信任、无钩子」的半成品。
        Directory.CreateDirectory(Path.Combine(home, "hooks.json"));
        var adapter = new CodexHooksNotificationAdapter(home);

        Assert.ThrowsAny<Exception>(() => adapter.Enable(HookExe));

        Assert.False(File.Exists(Path.Combine(home, "config.toml")));
    }

    [Fact]
    public void features_hooks为false时报断链()
    {
        string home = TestTemp.NewDir("codex-hooks-features-off");
        File.WriteAllText(Path.Combine(home, "config.toml"), "[features]\nhooks = false\n");
        var adapter = new CodexHooksNotificationAdapter(home);
        adapter.Enable(HookExe);

        NotificationProviderStatus status = adapter.Probe();

        Assert.True(status.HookBroken);
        Assert.Contains("features", status.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void 已启用时旧notify残留在Detail里提示并带出信任键()
    {
        string home = TestTemp.NewDir("codex-hooks-legacy-enabled");
        File.WriteAllText(Path.Combine(home, "config.toml"), "notify = [\"C:\\\\old\\\\AiResume.Hook.exe\", \"codex\"]\n");
        var adapter = new CodexHooksNotificationAdapter(home);
        adapter.Enable(HookExe);

        NotificationProviderStatus status = adapter.Probe();

        Assert.True(status.IsEnabled);
        Assert.False(status.HookBroken);
        Assert.Contains("旧版 notify", status.Detail, StringComparison.Ordinal);
        Assert.Contains(adapter.StateKey(0, 0), status.Detail, StringComparison.Ordinal);
    }

    private static string TomlQuote(string value) =>
        "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";

    [Fact]
    public void 信任记录不匹配时报断链而不是已启用()
    {
        string home = TestTemp.NewDir("codex-hooks-untrusted");
        var adapter = new CodexHooksNotificationAdapter(home);
        adapter.Enable(HookExe);
        string config = Path.Combine(home, "config.toml");
        File.WriteAllText(config, File.ReadAllText(config).Replace("sha256:", "sha256:0000"));

        NotificationProviderStatus status = adapter.Probe();

        Assert.True(status.IsEnabled);
        Assert.True(status.HookBroken);
        Assert.Contains("/hooks", status.Detail, StringComparison.Ordinal);

        // 重新启用会把信任记录改回正确值。
        adapter.Enable(HookExe);
        Assert.False(adapter.Probe().HookBroken);
    }

    [Fact]
    public void 旧版notify残留只提示不自动改写()
    {
        string home = TestTemp.NewDir("codex-hooks-legacy");
        string config = Path.Combine(home, "config.toml");
        string original = "notify = [\"C:\\\\old\\\\AiResume.Hook.exe\", \"codex\"]\n";
        File.WriteAllText(config, original);
        var adapter = new CodexHooksNotificationAdapter(home);

        NotificationProviderStatus status = adapter.Probe();

        Assert.False(status.IsEnabled);
        Assert.Contains("旧版 notify", status.Detail, StringComparison.Ordinal);
        Assert.Equal(original, File.ReadAllText(config));
    }

    [Fact]
    public void hooks根节点不是对象时拒绝修改()
    {
        string home = TestTemp.NewDir("codex-hooks-bad");
        File.WriteAllText(Path.Combine(home, "hooks.json"), "[1,2,3]");
        var adapter = new CodexHooksNotificationAdapter(home);

        Assert.Throws<InvalidOperationException>(() => adapter.Enable(HookExe));
        Assert.Equal("[1,2,3]", File.ReadAllText(Path.Combine(home, "hooks.json")));
        Assert.False(File.Exists(Path.Combine(home, "config.toml")));
    }
}
