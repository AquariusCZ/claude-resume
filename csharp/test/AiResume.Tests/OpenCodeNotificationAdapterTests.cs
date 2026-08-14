using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using AiResume.Worker.Notifications;
using Xunit;

namespace AiResume.Tests;

/// <summary>
/// OpenCodeNotificationAdapter 的单元测试。
/// 所有测试均在系统临时目录下创建唯一子目录,避免触碰真实用户配置。
/// </summary>
public class OpenCodeNotificationAdapterTests : IDisposable
{
    private readonly string _tempRoot;
    private readonly string _configDirectory;
    private readonly string _pluginsDirectory;
    private readonly OpenCodeNotificationAdapter _adapter;

    public OpenCodeNotificationAdapterTests()
    {
        // 创建唯一临时目录,模拟 ~/.config/opencode/plugins 结构
        _tempRoot = TestTemp.NewDir("AiResumeTests");
        _configDirectory = Path.Combine(_tempRoot, ".config", "opencode");
        _pluginsDirectory = Path.Combine(_configDirectory, "plugins");
        _adapter = new OpenCodeNotificationAdapter(_pluginsDirectory);
    }

    public void Dispose()
    {
        // 清理临时目录
        try
        {
            if (Directory.Exists(_tempRoot))
            {
                Directory.Delete(_tempRoot, recursive: true);
            }
        }
        catch
        {
            // 忽略清理失败
        }
    }

    /// <summary>
    /// 场景1:父目录不存在 -> IsInstalled=false,不抛异常。
    /// </summary>
    [Fact]
    public void Probe_WhenParentDirectoryMissing_ReturnsNotInstalled()
    {
        // 确保父目录不存在
        Assert.False(Directory.Exists(_configDirectory));

        var status = _adapter.Probe();

        Assert.False(status.IsInstalled);
        Assert.False(status.IsEnabled);
        Assert.Null(status.ConfigPath);
        Assert.Contains("不存在", status.Detail);
    }

    /// <summary>
    /// 场景2:父目录存在但插件文件不存在 -> IsInstalled=true, IsEnabled=false。
    /// </summary>
    [Fact]
    public void Probe_WhenParentExistsButPluginMissing_ReturnsInstalledNotEnabled()
    {
        // 创建父目录,但不创建插件文件
        Directory.CreateDirectory(_configDirectory);

        var status = _adapter.Probe();

        Assert.True(status.IsInstalled);
        Assert.False(status.IsEnabled);
        Assert.Equal(_configDirectory, status.ConfigPath);
        Assert.Contains("未安装", status.Detail);
    }

    /// <summary>
    /// 场景3:Enable 后插件文件存在且 IsEnabled=true,文件内容含 session.idle。
    /// </summary>
    [Fact]
    public void Enable_CreatesPluginFileWithSessionIdle()
    {
        Directory.CreateDirectory(_configDirectory);

        _adapter.Enable("notify-send test");

        var pluginPath = Path.Combine(_pluginsDirectory, OpenCodeNotificationAdapter.PluginFileName);
        Assert.True(File.Exists(pluginPath));

        var content = File.ReadAllText(pluginPath, Encoding.UTF8);
        Assert.Contains("session.idle", content);
        Assert.Contains("event.properties?.sessionID", content);
        Assert.Contains("client.session.get", content);
        Assert.Contains("session.parentID", content);
        Assert.Contains("if (!session || session.parentID) return;", content);
        Assert.Contains("Bun.spawn([cmd, ...args]", content);
        Assert.Contains("await run(payload, [\"opencode\"]);", content);
        Assert.Contains("new TextEncoder().encode(payload)", content);
        Assert.DoesNotContain("Bun.$`", content);

        var status = _adapter.Probe();
        Assert.True(status.IsInstalled);
        Assert.True(status.IsEnabled);
    }

    /// <summary>
    /// 场景4:重复 Enable 幂等:内容一致时不产生 .bak。
    /// </summary>
    [Fact]
    public void Enable_WhenContentSame_DoesNotCreateBackup()
    {
        Directory.CreateDirectory(_configDirectory);

        _adapter.Enable("notify-send test");
        var pluginPath = Path.Combine(_pluginsDirectory, OpenCodeNotificationAdapter.PluginFileName);
        var backupPath = pluginPath + ".bak";

        // 再次启用相同命令
        _adapter.Enable("notify-send test");

        Assert.True(File.Exists(pluginPath));
        Assert.False(File.Exists(backupPath));
    }

    /// <summary>
    /// 场景5:已存在我方旧版文件时,Enable 先备份为 .bak 再刷新。
    /// </summary>
    [Fact]
    public void Enable_WhenContentDifferent_CreatesBackupAndOverwrites()
    {
        Directory.CreateDirectory(_pluginsDirectory);
        var pluginPath = Path.Combine(_pluginsDirectory, OpenCodeNotificationAdapter.PluginFileName);
        var backupPath = pluginPath + ".bak";

        // 预置含稳定所有权标记的旧版插件文件
        string oldManaged = OpenCodeNotificationAdapter.BuildPluginSource("notify-send old-command");
        File.WriteAllText(pluginPath, oldManaged, Encoding.UTF8);

        _adapter.Enable("notify-send new-command");

        Assert.True(File.Exists(backupPath));
        Assert.Equal(oldManaged, File.ReadAllText(backupPath, Encoding.UTF8));
        Assert.Contains("notify-send new-command", File.ReadAllText(pluginPath, Encoding.UTF8));
    }

    [Fact]
    public void Enable_WhenUserOwnsSameFileName_RefusesToOverwrite()
    {
        Directory.CreateDirectory(_pluginsDirectory);
        string pluginPath = Path.Combine(_pluginsDirectory, OpenCodeNotificationAdapter.PluginFileName);
        const string userPlugin = "// user plugin with the same filename";
        File.WriteAllText(pluginPath, userPlugin, Encoding.UTF8);

        InvalidOperationException error = Assert.Throws<InvalidOperationException>(
            () => _adapter.Enable("notify-send test"));

        Assert.Contains("拒绝覆盖", error.Message);
        Assert.Equal(userPlugin, File.ReadAllText(pluginPath, Encoding.UTF8));
        Assert.False(_adapter.Probe().IsEnabled);
    }

    [Fact]
    public void Enable_UpgradesPreviousAiResumePluginWithoutNewMarker()
    {
        Directory.CreateDirectory(_pluginsDirectory);
        string pluginPath = Path.Combine(_pluginsDirectory, OpenCodeNotificationAdapter.PluginFileName);
        string legacy = LegacyPluginSource("old-hook.exe");
        File.WriteAllText(pluginPath, legacy, Encoding.UTF8);

        Assert.True(_adapter.Probe().IsEnabled);
        _adapter.Enable("new-hook.exe");

        string refreshed = File.ReadAllText(pluginPath, Encoding.UTF8);
        Assert.StartsWith(OpenCodeNotificationAdapter.ManagedMarker, refreshed, StringComparison.Ordinal);
        Assert.Contains("new-hook.exe", refreshed);
    }

    [Fact]
    public void Enable_UpgradesPreviousManagedSpawnPluginBeforeParentFiltering()
    {
        Directory.CreateDirectory(_pluginsDirectory);
        string pluginPath = Path.Combine(_pluginsDirectory, OpenCodeNotificationAdapter.PluginFileName);
        string previous = PreviousManagedSpawnPluginSource("old-hook.exe");
        File.WriteAllText(pluginPath, previous, Encoding.UTF8);

        Assert.True(_adapter.Probe().IsEnabled);
        _adapter.Enable("new-hook.exe");

        string refreshed = File.ReadAllText(pluginPath, Encoding.UTF8);
        Assert.Contains("client.session.get", refreshed);
        Assert.Contains("session.parentID", refreshed);
        Assert.Contains("new-hook.exe", refreshed);
    }

    [Fact]
    public void GeneratedPlugin_NotifiesOnlyTopLevelSession()
    {
        string pluginPath = Path.Combine(_tempRoot, "plugin.mjs");
        File.WriteAllText(pluginPath, OpenCodeNotificationAdapter.BuildPluginSource("hook.exe"), Encoding.UTF8);
        string harnessPath = Path.Combine(_tempRoot, "harness.mjs");
        string pluginUrl = new Uri(pluginPath).AbsoluteUri;
        File.WriteAllText(harnessPath, $$"""
            import { AiResumeNotify } from {{JsonSerializer.Serialize(pluginUrl)}};
            let spawns = 0;
            globalThis.Bun = { spawn() { spawns++; return { unref() {} }; } };
            const run = async (session) => {
              const client = { session: { get: async () => session } };
              const plugin = await AiResumeNotify({ client, directory: "C:/project" });
              await plugin.event({ event: { type: "session.idle", properties: { sessionID: "s1" } } });
            };
            await run({ data: { id: "s1", parentID: "parent" } });
            const afterChild = spawns;
            await run({ data: { id: "s1" } });
            const afterTopLevel = spawns;
            await run(null);
            console.log(JSON.stringify([afterChild, afterTopLevel, spawns]));
            """, Encoding.UTF8);

        var psi = new ProcessStartInfo("node", harnessPath)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        using Process process = Process.Start(psi)!;
        Assert.True(process.WaitForExit(10_000), "Node 插件回归脚本超时");
        string stdout = process.StandardOutput.ReadToEnd().Trim();
        string stderr = process.StandardError.ReadToEnd();

        Assert.True(process.ExitCode == 0, stderr);
        Assert.Equal("[0,1,1]", stdout);
    }

    [Fact]
    public void Disable_MarkerMentionOutsideFirstLineDoesNotClaimUserPlugin()
    {
        Directory.CreateDirectory(_pluginsDirectory);
        string pluginPath = Path.Combine(_pluginsDirectory, OpenCodeNotificationAdapter.PluginFileName);
        string userPlugin = "// user plugin\nconst note = \"" +
                            OpenCodeNotificationAdapter.ManagedMarker.Replace("\"", "\\\"") +
                            "\";\n";
        File.WriteAllText(pluginPath, userPlugin, Encoding.UTF8);

        _adapter.Disable();

        Assert.Equal(userPlugin, File.ReadAllText(pluginPath, Encoding.UTF8));
    }

    private static string LegacyPluginSource(string hookCommand) => $$"""
        {{OpenCodeNotificationAdapter.LegacyManagedMarker}}
        // 监听 session.idle 事件,在 agent 完成响应时触发通知

        export const AiResumeNotify = async ({ project, directory }) => {
          return {
            event: async ({ event }) => {
              try {
                if (event.type !== "session.idle") {
                  return;
                }
                const targetDir = directory || project?.directory || process.cwd();
                const cmd = "{{hookCommand}}";
                await Bun.$`${cmd} ${targetDir}`.quiet();
              } catch (err) {
                console.error("[airesume-notify] 通知执行失败:", err);
              }
            }
          };
        };

        export default AiResumeNotify;
        """;

    private static string PreviousManagedSpawnPluginSource(string hookCommand) => $$"""
        {{OpenCodeNotificationAdapter.ManagedMarker}}
        // 由 AI Resume 自动生成,请勿手动修改
        export const AiResumeNotify = async ({ project, directory, worktree }) => {
          return {
            event: async ({ event }) => {
              if (event.type !== "session.idle") return;
              const sessionId = event.properties?.sessionID || "";
              const targetDir = directory || worktree || project?.directory || process.cwd();
              const payload = JSON.stringify({
                hook_event_name: "session.idle",
                session_id: sessionId,
                cwd: targetDir
              });
              const cmd = "{{hookCommand}}";
              const child = Bun.spawn([cmd, "opencode"], {
                stdin: new TextEncoder().encode(payload),
                stdout: "ignore",
                stderr: "ignore"
              });
              child.unref();
            }
          };
        };
        export default AiResumeNotify;
        """;

    [Fact]
    public void Disable_WhenUserOwnsSameFileName_PreservesFile()
    {
        Directory.CreateDirectory(_pluginsDirectory);
        string pluginPath = Path.Combine(_pluginsDirectory, OpenCodeNotificationAdapter.PluginFileName);
        const string userPlugin = "// user plugin with the same filename";
        File.WriteAllText(pluginPath, userPlugin, Encoding.UTF8);

        _adapter.Disable();

        Assert.Equal(userPlugin, File.ReadAllText(pluginPath, Encoding.UTF8));
    }

    /// <summary>
    /// 场景6:不触碰他人文件:插件目录下预置 other-plugin.ts,Enable 与 Disable 后该文件都必须原样存在。
    /// </summary>
    [Fact]
    public void EnableAndDisable_DoNotTouchOtherPluginFiles()
    {
        Directory.CreateDirectory(_pluginsDirectory);
        var otherPluginPath = Path.Combine(_pluginsDirectory, "other-plugin.ts");
        const string otherContent = "// 其他插件内容";
        File.WriteAllText(otherPluginPath, otherContent, Encoding.UTF8);

        // Enable 后检查
        _adapter.Enable("notify-send test");
        Assert.True(File.Exists(otherPluginPath));
        Assert.Equal(otherContent, File.ReadAllText(otherPluginPath, Encoding.UTF8));

        // Disable 后检查
        _adapter.Disable();
        Assert.True(File.Exists(otherPluginPath));
        Assert.Equal(otherContent, File.ReadAllText(otherPluginPath, Encoding.UTF8));
    }

    /// <summary>
    /// 场景7:Disable 删除我方插件文件,IsEnabled 变 false。
    /// </summary>
    [Fact]
    public void Disable_RemovesPluginFileAndDisables()
    {
        Directory.CreateDirectory(_configDirectory);
        _adapter.Enable("notify-send test");

        var pluginPath = Path.Combine(_pluginsDirectory, OpenCodeNotificationAdapter.PluginFileName);
        Assert.True(File.Exists(pluginPath));

        _adapter.Disable();

        Assert.False(File.Exists(pluginPath));
        var status = _adapter.Probe();
        Assert.True(status.IsInstalled);
        Assert.False(status.IsEnabled);
    }

    /// <summary>
    /// 场景8:Disable 未启用时不抛异常(幂等)。
    /// </summary>
    [Fact]
    public void Disable_WhenNotEnabled_DoesNotThrow()
    {
        Directory.CreateDirectory(_configDirectory);

        // 不应抛出异常
        var exception = Record.Exception(() => _adapter.Disable());
        Assert.Null(exception);
    }

    /// <summary>
    /// 场景9:Enable 失败路径不留下 .tmp 残留。
    /// 通过将插件目录设为只读来模拟写入失败,验证临时文件被清理。
    /// </summary>
    [Fact]
    public void Enable_WhenWriteFails_DoesNotLeaveTempFiles()
    {
        // 跳过在 Windows 上可能不稳定的只读目录测试
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        Directory.CreateDirectory(_pluginsDirectory);
        var pluginPath = Path.Combine(_pluginsDirectory, OpenCodeNotificationAdapter.PluginFileName);

        // 设置插件目录为只读
        var originalAttributes = File.GetAttributes(_pluginsDirectory);
        File.SetAttributes(_pluginsDirectory, originalAttributes | FileAttributes.ReadOnly);

        try
        {
            // 尝试启用,应抛出异常
            Assert.Throws<InvalidOperationException>(() => _adapter.Enable("notify-send test"));

            // 验证没有 .tmp 文件残留
            var tempFiles = Directory.GetFiles(_pluginsDirectory, "*.tmp*");
            Assert.Empty(tempFiles);
        }
        finally
        {
            // 恢复目录属性以便清理
            File.SetAttributes(_pluginsDirectory, originalAttributes);
        }
    }

    [Fact]
    public void 插件同时监听完成与授权两类事件()
    {
        string source = OpenCodeNotificationAdapter.BuildPluginSource(@"C:\Tools\AiResume.Hook.exe");

        Assert.Contains("event.type === \"permission.asked\"", source, StringComparison.Ordinal);
        Assert.Contains("event.type !== \"session.idle\"", source, StringComparison.Ordinal);
        // 决策那条必须带 --kind=decision,否则会被当成"又跑完了一次"。
        Assert.Contains("[\"opencode\", \"--kind=decision\"]", source, StringComparison.Ordinal);
        Assert.Contains("await run(payload, [\"opencode\"]);", source, StringComparison.Ordinal);
    }

    [Fact]
    public void 授权事件不做顶层session过滤()
    {
        string source = OpenCodeNotificationAdapter.BuildPluginSource(@"C:\Tools\AiResume.Hook.exe");

        int ask = source.IndexOf("event.type === \"permission.asked\"", StringComparison.Ordinal);
        int idle = source.IndexOf("event.type !== \"session.idle\"", StringComparison.Ordinal);
        string askBranch = source[ask..idle];

        // 子 agent 的授权请求同样会卡住整个会话,按 parentID 过滤等于漏掉真正需要人的那一刻。
        Assert.DoesNotContain("parentID", askBranch, StringComparison.Ordinal);
        Assert.DoesNotContain("client.session.get", askBranch, StringComparison.Ordinal);
        // 完成那条仍然要过滤,否则 Task 子 session 每次 idle 都会报一次"已完成"。
        Assert.Contains("session.parentID", source[idle..], StringComparison.Ordinal);
    }

    [Fact]
    public void 授权事件的id字段取不到时退回时间戳()
    {
        string source = OpenCodeNotificationAdapter.BuildPluginSource(@"C:\Tools\AiResume.Hook.exe");

        // 授权项的 id 字段名没查证过,不能赌它一定存在。
        Assert.Contains("event.properties?.id || event.properties?.permissionID || askedAt", source,
            StringComparison.Ordinal);
    }
}
