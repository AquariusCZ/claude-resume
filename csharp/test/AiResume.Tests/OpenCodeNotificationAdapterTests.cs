using System;
using System.IO;
using System.Text;
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
        _tempRoot = Path.Combine(Path.GetTempPath(), "AiResumeTests_" + Guid.NewGuid().ToString("N"));
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
    /// 场景5:已存在同名文件但内容不同时,Enable 先备份为 .bak 再覆盖。
    /// </summary>
    [Fact]
    public void Enable_WhenContentDifferent_CreatesBackupAndOverwrites()
    {
        Directory.CreateDirectory(_pluginsDirectory);
        var pluginPath = Path.Combine(_pluginsDirectory, OpenCodeNotificationAdapter.PluginFileName);
        var backupPath = pluginPath + ".bak";

        // 预置不同内容的插件文件
        File.WriteAllText(pluginPath, "// 旧内容", Encoding.UTF8);

        _adapter.Enable("notify-send new-command");

        Assert.True(File.Exists(backupPath));
        Assert.Equal("// 旧内容", File.ReadAllText(backupPath, Encoding.UTF8));
        Assert.Contains("notify-send new-command", File.ReadAllText(pluginPath, Encoding.UTF8));
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
}