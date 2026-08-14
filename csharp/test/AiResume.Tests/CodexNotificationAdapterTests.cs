using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using AiResume.Worker.Notifications;
using Tomlyn;
using Tomlyn.Model;
using Xunit;

namespace AiResume.Tests;

/// <summary>
/// Codex 通知适配器测试。
/// 覆盖规格 §6 的第 1-10 条,全部使用系统临时目录,禁止触碰真实 %USERPROFILE%\.codex。
/// </summary>
public class CodexNotificationAdapterTests : IDisposable
{
    private readonly string _tempRoot;
    private readonly string _configDir;
    private readonly string _configPath;
    private readonly CodexNotificationAdapter _adapter;

    public CodexNotificationAdapterTests()
    {
        // 创建唯一临时目录
        _tempRoot = TestTemp.NewDir("AiResumeTests");
        _configDir = Path.Combine(_tempRoot, ".codex");
        _configPath = Path.Combine(_configDir, "config.toml");
        _adapter = new CodexNotificationAdapter(_configPath);
    }

    public void Dispose()
    {
        // 清理临时目录
        if (Directory.Exists(_tempRoot))
        {
            try
            {
                Directory.Delete(_tempRoot, recursive: true);
            }
            catch
            {
                // 忽略清理异常
            }
        }
    }

    /// <summary>
    /// 测试 1:目录不存在 → IsInstalled=false,不抛异常。
    /// </summary>
    [Fact]
    public void Probe_WhenDirectoryNotExists_ReturnsNotInstalled()
    {
        // 确保目录不存在
        if (Directory.Exists(_configDir))
        {
            Directory.Delete(_configDir, recursive: true);
        }

        var status = _adapter.Probe();

        Assert.False(status.IsInstalled);
        Assert.False(status.IsEnabled);
        Assert.NotNull(status.Detail);
    }

    /// <summary>
    /// 测试 2:无 notify 行 → Enable 后顶部区出现我方数组,IsEnabled=true。
    /// </summary>
    [Fact]
    public void Enable_WhenNoNotifyLine_AddsOwnArray()
    {
        // 准备:创建目录和空配置文件
        Directory.CreateDirectory(_configDir);
        File.WriteAllText(_configPath, "# 测试配置\r\n");

        var hookCommand = "C:\\tools\\AiResume.Hook.exe codex";
        _adapter.Enable(hookCommand);

        // 验证 IsEnabled
        var status = _adapter.Probe();
        Assert.True(status.IsInstalled);
        Assert.True(status.IsEnabled);

        // 验证文件内容
        var lines = File.ReadAllLines(_configPath);
        var notifyLine = FindNotifyLine(lines);
        Assert.NotNull(notifyLine);

        var array = ParseNotifyArray(notifyLine!);
        Assert.Equal(new[] { "C:\\tools\\AiResume.Hook.exe", "codex" }, array);
    }

    /// <summary>
    /// 测试 3:包装既有 notify → Enable 后我方在最外层且 --previous-notify 内容等于原数组;
    /// Disable 后完整还原为原数组。
    /// </summary>
    [Fact]
    public void EnableDisable_WrapsAndRestoresExistingNotify()
    {
        // 准备:预置既有 notify
        Directory.CreateDirectory(_configDir);
        var original = new[] { "C:\\tools\\my-notify.exe" };
        File.WriteAllText(_configPath, "notify = " + JsonSerializer.Serialize(original) + "\r\n");

        var hookCommand = "C:\\tools\\AiResume.Hook.exe codex";
        _adapter.Enable(hookCommand);

        // 验证 Enable 后
        var lines = File.ReadAllLines(_configPath);
        var notifyLine = FindNotifyLine(lines);
        Assert.NotNull(notifyLine);

        var array = ParseNotifyArray(notifyLine!);
        Assert.Equal("C:\\tools\\AiResume.Hook.exe", array[0]);
        Assert.Equal("codex", array[1]);
        Assert.Equal("--previous-notify", array[2]);

        // 验证 previous 内容等于原数组
        var previous = JsonSerializer.Deserialize<string[]>(array[3]);
        Assert.Equal(original, previous);

        // Disable 后完整还原
        _adapter.Disable();

        lines = File.ReadAllLines(_configPath);
        notifyLine = FindNotifyLine(lines);
        Assert.NotNull(notifyLine);

        array = ParseNotifyArray(notifyLine!);
        Assert.Equal(original, array);
    }

    [Theory]
    [InlineData("\"notify\"")]
    [InlineData("'notify'")]
    public void EnableDisable_PreservesQuotedNotifyKeyWithoutAddingSemanticDuplicate(string key)
    {
        Directory.CreateDirectory(_configDir);
        string[] original = ["C:\\tools\\existing-notify.exe"];
        File.WriteAllText(
            _configPath,
            $"{key} = {JsonSerializer.Serialize(original)} # keep\r\n[model]\r\nname = \"gpt\"\r\n");

        _adapter.Enable("C:\\tools\\AiResume.Hook.exe codex");

        string[] lines = File.ReadAllLines(_configPath);
        string quotedLine = Assert.Single(
            lines, line => line.TrimStart().StartsWith(key, StringComparison.Ordinal));
        Assert.DoesNotContain(lines, line => line.TrimStart().StartsWith("notify =", StringComparison.Ordinal));
        Assert.Contains("# keep", quotedLine, StringComparison.Ordinal);
        Assert.True(_adapter.Probe().IsEnabled);

        _adapter.Disable();

        lines = File.ReadAllLines(_configPath);
        quotedLine = Assert.Single(
            lines, line => line.TrimStart().StartsWith(key, StringComparison.Ordinal));
        Assert.Equal(original, ParseNotifyArray(quotedLine));
        Assert.Contains("# keep", quotedLine, StringComparison.Ordinal);
    }

    /// <summary>
    /// 测试 4:幂等 — 连续两次 Enable,不产生嵌套两层我方命令。
    /// </summary>
    [Fact]
    public void Enable_Twice_IsIdempotent()
    {
        // 准备
        Directory.CreateDirectory(_configDir);
        File.WriteAllText(_configPath, "# 测试配置\r\n");

        var hookCommand = "C:\\tools\\AiResume.Hook.exe codex";
        _adapter.Enable(hookCommand);
        _adapter.Enable(hookCommand);

        // 验证只有一层我方命令
        var lines = File.ReadAllLines(_configPath);
        var notifyLine = FindNotifyLine(lines);
        Assert.NotNull(notifyLine);

        var array = ParseNotifyArray(notifyLine!);
        Assert.Equal(new[] { "C:\\tools\\AiResume.Hook.exe", "codex" }, array);
    }

    /// <summary>
    /// 测试 5:刷新 — 预置我方旧路径的数组,Enable 新路径后只有路径被更新,--previous-notify 链原样。
    /// </summary>
    [Fact]
    public void Enable_WithExistingOwnCommand_RefreshesPath()
    {
        // 准备:预置我方旧路径,带 previous 链
        Directory.CreateDirectory(_configDir);
        var previous = new[] { "C:\\tools\\old-notify.exe" };
        var existing = new[]
        {
            "C:\\old\\AiResume.Hook.exe",
            "codex",
            "--previous-notify",
            JsonSerializer.Serialize(previous)
        };
        File.WriteAllText(_configPath, "notify = " + JsonSerializer.Serialize(existing) + "\r\n");

        var hookCommand = "C:\\new\\AiResume.Hook.exe codex";
        _adapter.Enable(hookCommand);

        // 验证路径已更新,previous 链原样
        var lines = File.ReadAllLines(_configPath);
        var notifyLine = FindNotifyLine(lines);
        Assert.NotNull(notifyLine);

        var array = ParseNotifyArray(notifyLine!);
        Assert.Equal("C:\\new\\AiResume.Hook.exe", array[0]);
        Assert.Equal("codex", array[1]);
        Assert.Equal("--previous-notify", array[2]);

        var restoredPrevious = JsonSerializer.Deserialize<string[]>(array[3]);
        Assert.Equal(previous, restoredPrevious);
    }

    /// <summary>
    /// 测试 6:Desktop wrapper — 预置 notify = ["C:\\x\\codex-computer-use.exe"],
    /// Enable 后 wrapper 仍在 [0],我方出现在其 --previous-notify 中。
    /// </summary>
    [Fact]
    public void Enable_WithDesktopWrapper_WrapsInside()
    {
        // 准备:预置 Desktop wrapper
        Directory.CreateDirectory(_configDir);
        var wrapper = new[] { "C:\\x\\codex-computer-use.exe" };
        File.WriteAllText(_configPath, "notify = " + JsonSerializer.Serialize(wrapper) + "\r\n");

        var hookCommand = "C:\\tools\\AiResume.Hook.exe codex";
        _adapter.Enable(hookCommand);

        // 验证 wrapper 仍在 [0]
        var lines = File.ReadAllLines(_configPath);
        var notifyLine = FindNotifyLine(lines);
        Assert.NotNull(notifyLine);

        var array = ParseNotifyArray(notifyLine!);
        Assert.Equal("C:\\x\\codex-computer-use.exe", array[0]);

        // 验证我方出现在 --previous-notify 中
        Assert.Contains("--previous-notify", array);
        var idx = Array.IndexOf(array, "--previous-notify");
        var previous = JsonSerializer.Deserialize<string[]>(array[idx + 1]);
        Assert.NotNull(previous);
        Assert.Contains(CodexNotificationAdapter.MarkerFileName, previous![0], StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Enable_DesktopWrapperPreviousNotify损坏时拒绝修改并保留原文()
    {
        Directory.CreateDirectory(_configDir);
        string nestedBroken = JsonSerializer.Serialize(new[]
        {
            @"C:\tools\user-notify.exe",
            "--previous-notify",
            "{broken",
        });
        string[][] brokenCommands =
        [
            [@"C:\x\codex-computer-use.exe", "--previous-notify"],
            [@"C:\x\codex-computer-use.exe", "--previous-notify", "{broken"],
            [@"C:\x\codex-computer-use.exe", "--previous-notify", nestedBroken],
        ];

        foreach (string[] command in brokenCommands)
        {
            string original = "notify = " + JsonSerializer.Serialize(command) + "\r\n";
            File.WriteAllText(_configPath, original);

            InvalidOperationException error = Assert.Throws<InvalidOperationException>(() =>
                _adapter.Enable(@"C:\new\AiResume.Hook.exe"));

            Assert.Contains("previous-notify", error.Message, StringComparison.Ordinal);
            Assert.Equal(original, File.ReadAllText(_configPath));
        }
    }

    /// <summary>
    /// 测试 7:批处理拒绝 — 预置 notify = ["C:\\x\\legacy.cmd"],Enable 抛异常且配置文件未被修改。
    /// </summary>
    [Fact]
    public void Enable_WithBatchFile_ThrowsAndPreservesFile()
    {
        // 准备:预置批处理 notify
        Directory.CreateDirectory(_configDir);
        // 注意:TOML 基本字符串与 JSON 同规则,路径反斜杠须转义。写入文件的实际内容为
        // notify = ["C:\\x\\legacy.cmd"],否则解析阶段就会报非法转义,测不到批处理拒绝分支。
        var originalContent = "notify = [\"C:\\\\x\\\\legacy.cmd\"]\r\n";
        File.WriteAllText(_configPath, originalContent);

        var hookCommand = "C:\\tools\\AiResume.Hook.exe codex";

        // 验证抛异常
        var ex = Assert.Throws<InvalidOperationException>(() => _adapter.Enable(hookCommand));
        Assert.Contains("批处理", ex.Message);

        // 验证文件内容逐字节相同
        var afterContent = File.ReadAllText(_configPath);
        Assert.Equal(originalContent, afterContent);
    }

    /// <summary>
    /// 测试 8:非单行数组拒绝 — 预置多行 notify,Enable 抛异常且文件未被修改。
    /// </summary>
    [Fact]
    public void Enable_WithMultilineNotify_ThrowsAndPreservesFile()
    {
        // 准备:预置多行 notify
        Directory.CreateDirectory(_configDir);
        var originalContent = "notify = [\r\n  \"C:\\\\tools\\\\my-notify.exe\"\r\n]\r\n";
        File.WriteAllText(_configPath, originalContent);

        var hookCommand = "C:\\tools\\AiResume.Hook.exe codex";

        // 验证抛异常
        var ex = Assert.Throws<InvalidOperationException>(() => _adapter.Enable(hookCommand));
        Assert.Contains("单行数组", ex.Message);

        // 验证文件内容逐字节相同
        var afterContent = File.ReadAllText(_configPath);
        Assert.Equal(originalContent, afterContent);
    }

    /// <summary>
    /// 测试 9:section 内同名键不受影响 — 预置顶部 notify 与 [profiles.x] 段内的 notify,
    /// 操作后段内那个逐字未变。
    /// </summary>
    [Fact]
    public void Enable_PreservesSectionNotify()
    {
        // 准备:顶部 notify 与 section 内 notify
        Directory.CreateDirectory(_configDir);
        var sectionNotify = "notify = [\"C:\\\\section\\\\notify.exe\"]";
        var originalContent = "notify = [\"C:\\\\tools\\\\my-notify.exe\"]\r\n" +
                              "[profiles.x]\r\n" +
                              "  " + sectionNotify + "\r\n";
        File.WriteAllText(_configPath, originalContent);

        var hookCommand = "C:\\tools\\AiResume.Hook.exe codex";
        _adapter.Enable(hookCommand);

        // 验证 section 内 notify 逐字未变
        var lines = File.ReadAllLines(_configPath);
        // 查找串必须与写入文件的实际内容一致(TOML 中反斜杠是转义过的双写形式)。
        var sectionLine = lines.FirstOrDefault(l => l.Contains("C:\\\\section\\\\notify.exe"));
        Assert.NotNull(sectionLine);
        Assert.Equal("  " + sectionNotify, sectionLine);
    }

    /// <summary>
    /// 测试 10:Enable 生成 .bak。
    /// </summary>
    [Fact]
    public void Enable_CreatesBackupFile()
    {
        // 准备
        Directory.CreateDirectory(_configDir);
        File.WriteAllText(_configPath, "# 测试配置\r\n");

        var hookCommand = "C:\\tools\\AiResume.Hook.exe codex";
        _adapter.Enable(hookCommand);

        // 验证 .bak 文件存在
        Assert.True(File.Exists(_configPath + ".bak"));
    }

    [Fact]
    public void Enable_原子替换窗口出现外部写入时恢复外部版本()
    {
        Directory.CreateDirectory(_configDir);
        const string original = "notify = [\"C:\\\\tools\\\\user.exe\"]\r\n";
        const string external = "# external update\r\nnotify = [\"C:\\\\tools\\\\user.exe\"]\r\n";
        File.WriteAllText(_configPath, original);
        var adapter = new CodexNotificationAdapter(
            _configPath,
            () => File.WriteAllText(_configPath, external));

        InvalidOperationException error = Assert.Throws<InvalidOperationException>(() =>
            adapter.Enable(@"C:\new\AiResume.Hook.exe"));

        Assert.Contains("原子提交期间被外部更新", error.Message, StringComparison.Ordinal);
        Assert.Equal(external, File.ReadAllText(_configPath));
        string conflict = Assert.Single(Directory.GetFiles(_configDir, "config.toml.conflict-*"));
        Assert.Contains("AiResume.Hook.exe", File.ReadAllText(conflict), StringComparison.Ordinal);
        Assert.Empty(Directory.GetFiles(_configDir, "config.toml.tmp-*"));
    }

    [Fact]
    public void Enable_冲突恢复窗口再次外部写入时保留并恢复最新版本()
    {
        Directory.CreateDirectory(_configDir);
        const string original = "notify = [\"C:\\\\tools\\\\user.exe\"]\r\n";
        const string firstExternal = "# external one\r\nnotify = [\"C:\\\\tools\\\\user.exe\"]\r\n";
        const string latestExternal = "# external latest\r\nnotify = [\"C:\\\\tools\\\\user.exe\"]\r\n";
        File.WriteAllText(_configPath, original);
        int restoreHooks = 0;
        var adapter = new CodexNotificationAdapter(
            _configPath,
            () => File.WriteAllText(_configPath, firstExternal),
            attempt =>
            {
                restoreHooks++;
                if (attempt == 0)
                {
                    File.WriteAllText(_configPath, latestExternal);
                }
            });

        InvalidOperationException error = Assert.Throws<InvalidOperationException>(() =>
            adapter.Enable(@"C:\new\AiResume.Hook.exe"));

        Assert.Contains("外部版本已恢复", error.Message, StringComparison.Ordinal);
        Assert.Equal(latestExternal, File.ReadAllText(_configPath));
        Assert.True(restoreHooks >= 2);
        Assert.Contains(
            Directory.GetFiles(_configDir, "config.toml.candidate-*"),
            path => File.ReadAllText(path).Contains("AiResume.Hook.exe", StringComparison.Ordinal));
        Assert.Contains(
            Directory.GetFiles(_configDir, "config.toml.recovery-*"),
            path => File.ReadAllText(path) == firstExternal);
        Assert.Empty(Directory.GetFiles(_configDir, "config.toml.tmp-*"));
    }

    [Fact]
    public void Disable_PreviousNotify损坏时拒绝修改并保留整行()
    {
        Directory.CreateDirectory(_configDir);
        string original = "notify = " + JsonSerializer.Serialize(new[]
        {
            @"C:\owned\AiResume.Hook.exe",
            "codex",
            "--previous-notify",
            "{not-json}",
        }) + "\r\n";
        File.WriteAllText(_configPath, original);

        InvalidOperationException error = Assert.Throws<InvalidOperationException>(() => _adapter.Disable());

        Assert.Contains("无法解析", error.Message, StringComparison.Ordinal);
        Assert.Equal(original, File.ReadAllText(_configPath));
        Assert.False(File.Exists(_configPath + ".bak"));
        Assert.Empty(Directory.GetFiles(_configDir, "config.toml.tmp-*"));
    }

    [Fact]
    public void Enable_没有顶层Notify时插在首个表头之前()
    {
        Directory.CreateDirectory(_configDir);
        File.WriteAllText(
            _configPath,
            "[projects.'c:\\\\work']\r\ntrust_level = \"trusted\"\r\n");

        _adapter.Enable("C:\\tools\\AiResume.Hook.exe codex");

        string[] lines = File.ReadAllLines(_configPath);
        Assert.StartsWith("notify = ", lines[0], StringComparison.Ordinal);
        Assert.Equal("[projects.'c:\\\\work']", lines[1]);
        Assert.Single(lines, line => line.TrimStart().StartsWith("notify =", StringComparison.Ordinal));
    }

    [Fact]
    public void Enable_顶层嵌套数组续行不会被误认成表头并制造重复Notify()
    {
        Directory.CreateDirectory(_configDir);
        const string oldNotify = "notify = [\"C:\\\\old\\\\AiResume.Hook.exe\", \"codex\"]";
        File.WriteAllText(
            _configPath,
            "sandbox_permissions = [\r\n" +
            "  [\"disk-read\", \"disk-write\"],\r\n" +
            "]\r\n" +
            oldNotify + "\r\n" +
            "[projects.'c:\\\\work']\r\n" +
            "trust_level = \"trusted\"\r\n");

        _adapter.Enable("C:\\new\\AiResume.Hook.exe codex");

        string[] lines = File.ReadAllLines(_configPath);
        string notify = Assert.Single(lines, line => line.TrimStart().StartsWith("notify =", StringComparison.Ordinal));
        Assert.Contains("C:\\\\new\\\\AiResume.Hook.exe", notify, StringComparison.Ordinal);
        Assert.Contains("  [\"disk-read\", \"disk-write\"],", lines, StringComparer.Ordinal);
        Assert.True(Array.IndexOf(lines, notify) < Array.IndexOf(lines, "[projects.'c:\\\\work']"));
    }

    [Fact]
    public void Enable_单元素嵌套数组续行不会被误认成表头()
    {
        Directory.CreateDirectory(_configDir);
        const string oldNotify = "notify = [\"C:\\\\old\\\\AiResume.Hook.exe\", \"codex\"]";
        File.WriteAllText(
            _configPath,
            "sandbox_permissions = [\r\n" +
            "  [\"disk-read\"],\r\n" +
            "]\r\n" +
            oldNotify + "\r\n" +
            "[projects.'c:\\\\work']\r\n" +
            "trust_level = \"trusted\"\r\n");

        _adapter.Enable("C:\\new\\AiResume.Hook.exe codex");

        string[] lines = File.ReadAllLines(_configPath);
        string notify = Assert.Single(lines, line => line.TrimStart().StartsWith("notify =", StringComparison.Ordinal));
        Assert.Contains("C:\\\\new\\\\AiResume.Hook.exe", notify, StringComparison.Ordinal);
        Assert.Contains("  [\"disk-read\"],", lines, StringComparer.Ordinal);
        Assert.True(Array.IndexOf(lines, notify) < Array.IndexOf(lines, "[projects.'c:\\\\work']"));
    }

    [Fact]
    public void Enable_顶层多行字符串里的伪Notify不会被替换()
    {
        Directory.CreateDirectory(_configDir);
        const string embedded = "notify = [\"C:\\\\text\\\\AiResume.Hook.exe\", \"codex\"]";
        File.WriteAllText(
            _configPath,
            "instructions = \"\"\"\r\n" +
            embedded + "\r\n" +
            "\"\"\"\r\n" +
            "[projects.'c:\\\\work']\r\n" +
            "trust_level = \"trusted\"\r\n");

        NotificationProviderStatus before = _adapter.Probe();
        Assert.False(before.IsEnabled);
        Assert.False(before.HookBroken);

        _adapter.Enable("C:\\new\\AiResume.Hook.exe codex");

        string[] lines = File.ReadAllLines(_configPath);
        Assert.StartsWith("notify = ", lines[0], StringComparison.Ordinal);
        Assert.Contains(embedded, lines, StringComparer.Ordinal);
        Assert.Equal("instructions = \"\"\"", lines[1]);
    }

    [Fact]
    public void Enable_子表多行字符串与嵌套值里的伪Notify保持原文()
    {
        Directory.CreateDirectory(_configDir);
        const string multiline = "  notify = [\"C:\\\\text\\\\AiResume.Hook.exe\", \"codex\"]";
        const string nested = "    notify = [\"C:\\\\nested\\\\AiResume.Hook.exe\", \"codex\"]";
        File.WriteAllText(
            _configPath,
            "[projects.'c:\\\\work']\r\n" +
            "instructions = '''\r\n" +
            multiline + "\r\n" +
            "'''\r\n" +
            "settings = [\r\n" +
            "  {\r\n" +
            nested + "\r\n" +
            "  },\r\n" +
            "]\r\n");

        NotificationProviderStatus before = _adapter.Probe();
        Assert.False(before.IsEnabled);
        Assert.False(before.HookBroken);

        _adapter.Enable("C:\\new\\AiResume.Hook.exe codex");

        string[] lines = File.ReadAllLines(_configPath);
        Assert.StartsWith("notify = ", lines[0], StringComparison.Ordinal);
        Assert.Contains(multiline, lines, StringComparer.Ordinal);
        Assert.Contains(nested, lines, StringComparer.Ordinal);
    }

    [Fact]
    public async Task 两个适配器并发刷新同一配置不会碰撞临时文件或生成重复键()
    {
        Directory.CreateDirectory(_configDir);
        File.WriteAllText(_configPath, "# concurrent\r\n");
        var first = new CodexNotificationAdapter(_configPath);
        var second = new CodexNotificationAdapter(_configPath);

        await Task.WhenAll(
            Task.Run(() => first.Enable(@"C:\one\AiResume.Hook.exe")),
            Task.Run(() => second.Enable(@"C:\two\AiResume.Hook.exe")));

        string[] lines = File.ReadAllLines(_configPath);
        Assert.Single(lines, line => line.TrimStart().StartsWith("notify =", StringComparison.Ordinal));
        Assert.True(_adapter.Probe().IsEnabled);
        Assert.Empty(Directory.GetFiles(_configDir, "config.toml.tmp-*"));
    }

    [Fact]
    public void Enable_收敛旧版追加在项目表内的重复自有Notify()
    {
        Directory.CreateDirectory(_configDir);
        string own = JsonSerializer.Serialize(new[] { "C:\\old\\AiResume.Hook.exe", "codex" });
        File.WriteAllText(
            _configPath,
            "[projects.'c:\\\\work']\r\n" +
            "trust_level = \"trusted\"\r\n" +
            "notify = " + own + "\r\n" +
            "notify = " + own + "\r\n");

        _adapter.Enable("C:\\new\\AiResume.Hook.exe codex");

        string[] lines = File.ReadAllLines(_configPath);
        string notify = Assert.Single(lines, line => line.TrimStart().StartsWith("notify =", StringComparison.Ordinal));
        Assert.Equal(0, Array.IndexOf(lines, notify));
        Assert.Contains("C:\\\\new\\\\AiResume.Hook.exe", notify, StringComparison.Ordinal);
        Assert.Contains("[projects.'c:\\\\work']", lines, StringComparer.Ordinal);
    }

    [Fact]
    public void Probe_项目表内旧版自有Notify保留启用意图并标记损坏()
    {
        Directory.CreateDirectory(_configDir);
        string own = JsonSerializer.Serialize(new[] { "C:\\old\\AiResume.Hook.exe", "codex" });
        File.WriteAllText(
            _configPath,
            "[projects.'c:\\\\work']\r\n" +
            "trust_level = \"trusted\"\r\n" +
            "notify = " + own + "\r\n");

        NotificationProviderStatus status = _adapter.Probe();

        Assert.True(status.IsInstalled);
        Assert.True(status.IsEnabled);
        Assert.True(status.HookBroken);
        Assert.Equal("C:\\old\\AiResume.Hook.exe", status.HookCommand);
        Assert.Contains("TOML 子表", status.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void Disable_删除项目表内旧版自有Notify但保留用户配置()
    {
        Directory.CreateDirectory(_configDir);
        string own = JsonSerializer.Serialize(new[] { "C:\\old\\AiResume.Hook.exe", "codex" });
        const string userNotify = "notify = [\"C:\\\\tools\\\\user-notify.exe\"]";
        File.WriteAllText(
            _configPath,
            userNotify + "\r\n" +
            "[projects.'c:\\\\work']\r\n" +
            "trust_level = \"trusted\"\r\n" +
            "notify = " + own + "\r\n");

        _adapter.Disable();

        string[] lines = File.ReadAllLines(_configPath);
        Assert.Contains(userNotify, lines, StringComparer.Ordinal);
        Assert.Contains("[projects.'c:\\\\work']", lines, StringComparer.Ordinal);
        Assert.Contains("trust_level = \"trusted\"", lines, StringComparer.Ordinal);
        Assert.DoesNotContain(lines, line => line.Contains("AiResume.Hook.exe", StringComparison.OrdinalIgnoreCase));
        Assert.True(File.Exists(_configPath + ".bak"));
    }

    [Fact]
    public void Enable_RefreshesOwnedLayerButPreservesAmbiguousPreviousCommand()
    {
        Directory.CreateDirectory(_configDir);
        string broken = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AI");
        string[] legacyBroken = [broken, "codex"];
        string[] own =
        [
            @"C:\old\AiResume.Hook.exe",
            "codex",
            "--previous-notify",
            JsonSerializer.Serialize(legacyBroken),
        ];
        string[] desktop =
        [
            @"C:\codex\codex-computer-use.exe",
            "turn-ended",
            "--previous-notify",
            JsonSerializer.Serialize(own),
        ];
        File.WriteAllText(_configPath, "notify = " + JsonSerializer.Serialize(desktop) + "\r\n");

        _adapter.Enable(@"C:\new path\AiResume.Hook.exe");

        string[] outer = ParseNotifyArray(FindNotifyLine(File.ReadAllLines(_configPath))!);
        int marker = Array.IndexOf(outer, "--previous-notify");
        string[] refreshed = JsonSerializer.Deserialize<string[]>(outer[marker + 1])!;
        Assert.Equal(@"C:\new path\AiResume.Hook.exe", refreshed[0]);
        Assert.Equal("codex", refreshed[1]);
        int previousMarker = Array.IndexOf(refreshed, "--previous-notify");
        Assert.True(previousMarker >= 0);
        Assert.Equal(legacyBroken, JsonSerializer.Deserialize<string[]>(refreshed[previousMarker + 1]));
    }

    [Fact]
    public void PruneDeadLinks_DoesNotDeleteUserOwnedOfflineCommand()
    {
        string[] user =
        [
            @"Z:\offline\user-notify.exe",
            "codex",
            "--previous-notify",
            JsonSerializer.Serialize(new[] { @"C:\other\notify.exe" }),
        ];

        string[] result = CodexNotificationAdapter.PruneDeadLinks(user, _ => false);

        Assert.Equal(user, result);
    }

    [Fact]
    public void EnableDisable_MarkerInUserArgumentIsPreservedAsPreviousCommand()
    {
        Directory.CreateDirectory(_configDir);
        string[] user = [@"C:\tools\notify.exe", "--label", "AiResume.Hook.exe"];
        File.WriteAllText(_configPath, "notify = " + JsonSerializer.Serialize(user) + "\r\n");

        Assert.False(_adapter.Probe().IsEnabled);
        _adapter.Enable(@"C:\tools\AiResume.Hook.exe");
        Assert.True(_adapter.Probe().IsEnabled);
        _adapter.Disable();

        string[] restored = ParseNotifyArray(FindNotifyLine(File.ReadAllLines(_configPath))!);
        Assert.Equal(user, restored);
    }

    [Fact]
    public void Probe_支持混合TOML引号和PreviousNotify中的嵌套Json数组()
    {
        Directory.CreateDirectory(_configDir);
        const string line =
            "notify = ['C:\\codex\\codex-computer-use.exe', \"turn-ended\", \"--previous-notify\", " +
            "'[\"C:\\\\old\\\\AiResume.Hook.exe\",\"codex\"]'] # keep";
        File.WriteAllText(_configPath, line + "\r\n");

        NotificationProviderStatus status = _adapter.Probe();

        Assert.True(status.IsInstalled);
        Assert.True(status.IsEnabled);
        Assert.Equal(@"C:\old\AiResume.Hook.exe", status.HookCommand);
    }

    [Fact]
    public void Probe_Notify解析失败仍保留Codex已安装事实()
    {
        Directory.CreateDirectory(_configDir);
        File.WriteAllText(_configPath, "notify = [1, true]\r\n");

        NotificationProviderStatus status = _adapter.Probe();

        Assert.True(status.IsInstalled);
        Assert.False(status.IsEnabled);
        Assert.Contains("探测异常", status.Detail, StringComparison.Ordinal);
        Assert.Contains("只能包含字符串", status.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void EnableDisable_兼容真实DesktopWrapper形状并保留尾注释()
    {
        Directory.CreateDirectory(_configDir);
        const string line =
            "'notify' = ['C:\\codex\\codex-computer-use.exe', \"turn-ended\", \"--previous-notify\", " +
            "'[\"C:\\\\old\\\\AiResume.Hook.exe\",\"codex\"]'] # desktop";
        File.WriteAllText(_configPath, line + "\r\n");

        _adapter.Enable(@"C:\new path\AiResume.Hook.exe");

        string enabledLine = Assert.Single(File.ReadAllLines(_configPath));
        string[] outer = ParseNotifyArray(enabledLine);
        Assert.Equal(@"C:\codex\codex-computer-use.exe", outer[0]);
        int previousIndex = Array.IndexOf(outer, "--previous-notify");
        string[] refreshed = JsonSerializer.Deserialize<string[]>(outer[previousIndex + 1])!;
        Assert.Equal(@"C:\new path\AiResume.Hook.exe", refreshed[0]);
        Assert.EndsWith("# desktop", enabledLine, StringComparison.Ordinal);

        _adapter.Disable();

        string restoredLine = Assert.Single(File.ReadAllLines(_configPath));
        Assert.Equal(
            new[] { @"C:\codex\codex-computer-use.exe", "turn-ended" },
            ParseNotifyArray(restoredLine));
        Assert.StartsWith("'notify' = ", restoredLine, StringComparison.Ordinal);
        Assert.EndsWith("# desktop", restoredLine, StringComparison.Ordinal);
    }

    /// <summary>在行集合中查找 notify 行。</summary>
    private static string? FindNotifyLine(string[] lines)
    {
        return lines.FirstOrDefault(l => l.TrimStart().StartsWith("notify", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>从 notify 行解析数组。</summary>
    private static string[] ParseNotifyArray(string notifyLine)
    {
        // 提取数组部分
        var start = notifyLine.IndexOf('[');
        var end = notifyLine.LastIndexOf(']');
        if (start < 0 || end < 0 || end <= start)
        {
            throw new InvalidOperationException("无法解析 notify 行: " + notifyLine);
        }

        var arrayText = notifyLine.Substring(start, end - start + 1);
        TomlTable root = TomlSerializer.Deserialize<TomlTable>("notify = " + arrayText)!;
        TomlArray array = Assert.IsType<TomlArray>(root["notify"]);
        return array.Cast<string>().ToArray();
    }
}
