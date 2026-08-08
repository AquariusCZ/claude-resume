using System;
using System.IO;
using System.Text;
using AiResume.Worker.Notifications;
using Xunit;

namespace AiResume.Tests;

/// <summary>
/// ClineNotificationAdapter 的单元测试。
/// 使用系统临时目录下的唯一子目录作为 hooks 目录,避免触碰真实用户目录。
/// </summary>
public class ClineNotificationAdapterTests : IDisposable
{
    private readonly string _hooksDirectory;
    private readonly ClineNotificationAdapter _adapter;

    public ClineNotificationAdapterTests()
    {
        // 创建唯一临时目录
        _hooksDirectory = Path.Combine(Path.GetTempPath(), "AiResumeTests_" + Guid.NewGuid().ToString("N"));
        _adapter = new ClineNotificationAdapter(_hooksDirectory);
    }

    public void Dispose()
    {
        // 清理临时目录
        if (Directory.Exists(_hooksDirectory))
        {
            try
            {
                Directory.Delete(_hooksDirectory, recursive: true);
            }
            catch
            {
                // 忽略清理异常
            }
        }
    }

    private string HookPath => Path.Combine(_hooksDirectory, ClineNotificationAdapter.HookFileName);
    private string PreviousPath => Path.Combine(_hooksDirectory, ClineNotificationAdapter.PreviousFileName);

    [Fact]
    public void Probe_WhenHooksDirectoryDoesNotExist_ReturnsNotInstalled()
    {
        // Arrange - 目录不存在

        // Act
        var status = _adapter.Probe();

        // Assert
        Assert.False(status.IsInstalled);
        Assert.False(status.IsEnabled);
        Assert.Null(status.ConfigPath);
    }

    [Fact]
    public void Probe_WhenDirectoryExistsButNoHookFile_ReturnsInstalledButNotEnabled()
    {
        // Arrange
        Directory.CreateDirectory(_hooksDirectory);

        // Act
        var status = _adapter.Probe();

        // Assert
        Assert.True(status.IsInstalled);
        Assert.False(status.IsEnabled);
        Assert.Null(status.ConfigPath);
    }

    [Fact]
    public void Enable_CreatesHookFileWithMarkerAndEnables()
    {
        // Arrange
        Directory.CreateDirectory(_hooksDirectory);
        const string hookCommand = @"C:\tools\ai-resume-hook.exe";

        // Act
        _adapter.Enable(hookCommand);

        // Assert
        Assert.True(File.Exists(HookPath));
        var content = File.ReadAllText(HookPath);
        Assert.Contains(ClineNotificationAdapter.Marker, content);
        var status = _adapter.Probe();
        Assert.True(status.IsInstalled);
        Assert.True(status.IsEnabled);
        Assert.Equal(HookPath, status.ConfigPath);
    }

    [Fact]
    public void Enable_PreservesUserOriginalHookInPreviousBackup()
    {
        // Arrange
        Directory.CreateDirectory(_hooksDirectory);
        const string userHook = "# my own hook";
        File.WriteAllText(HookPath, userHook);
        const string hookCommand = @"C:\tools\ai-resume-hook.exe";

        // Act
        _adapter.Enable(hookCommand);

        // Assert
        Assert.True(File.Exists(PreviousPath));
        Assert.Equal(userHook, File.ReadAllText(PreviousPath));
        Assert.Contains(ClineNotificationAdapter.Marker, File.ReadAllText(HookPath));
    }

    [Fact]
    public void Enable_WhenAlreadyEnabled_DoesNotOverwritePreviousBackup()
    {
        // Arrange
        Directory.CreateDirectory(_hooksDirectory);
        const string userHook = "# my own hook";
        File.WriteAllText(HookPath, userHook);
        const string hookCommand = @"C:\tools\ai-resume-hook.exe";

        // 第一次 Enable
        _adapter.Enable(hookCommand);
        var previousAfterFirstEnable = File.ReadAllText(PreviousPath);
        var wrapperAfterFirstEnable = File.ReadAllText(HookPath);

        // Act - 第二次 Enable
        _adapter.Enable(hookCommand);

        // Assert
        Assert.Equal(userHook, previousAfterFirstEnable);
        Assert.Equal(userHook, File.ReadAllText(PreviousPath));
        Assert.Equal(wrapperAfterFirstEnable, File.ReadAllText(HookPath));
    }

    [Fact]
    public void Disable_RestoresUserOriginalHookAndDeletesPreviousBackup()
    {
        // Arrange
        Directory.CreateDirectory(_hooksDirectory);
        const string userHook = "# my own hook";
        File.WriteAllText(HookPath, userHook);
        const string hookCommand = @"C:\tools\ai-resume-hook.exe";
        _adapter.Enable(hookCommand);
        Assert.True(File.Exists(PreviousPath));

        // Act
        _adapter.Disable();

        // Assert
        Assert.Equal(userHook, File.ReadAllText(HookPath));
        Assert.False(File.Exists(PreviousPath));
        var status = _adapter.Probe();
        Assert.True(status.IsInstalled);
        Assert.False(status.IsEnabled);
    }

    [Fact]
    public void Disable_WhenNoPreviousBackup_DeletesHookFile()
    {
        // Arrange
        Directory.CreateDirectory(_hooksDirectory);
        const string hookCommand = @"C:\tools\ai-resume-hook.exe";
        _adapter.Enable(hookCommand);
        Assert.True(File.Exists(HookPath));
        Assert.False(File.Exists(PreviousPath));

        // Act
        _adapter.Disable();

        // Assert
        Assert.False(File.Exists(HookPath));
        var status = _adapter.Probe();
        Assert.True(status.IsInstalled);
        Assert.False(status.IsEnabled);
    }

    [Fact]
    public void Disable_WhenHookFileDoesNotContainMarker_DoesNotDeleteOrModify()
    {
        // Arrange
        Directory.CreateDirectory(_hooksDirectory);
        const string userHook = "# my own hook";
        File.WriteAllText(HookPath, userHook);

        // Act
        _adapter.Disable();

        // Assert
        Assert.True(File.Exists(HookPath));
        Assert.Equal(userHook, File.ReadAllText(HookPath));
        Assert.False(File.Exists(PreviousPath));
    }

    [Fact]
    public void Enable_WritesFileWithUtf8Bom()
    {
        // Arrange
        Directory.CreateDirectory(_hooksDirectory);
        const string hookCommand = @"C:\tools\ai-resume-hook.exe";

        // Act
        _adapter.Enable(hookCommand);

        // Assert
        var bytes = File.ReadAllBytes(HookPath);
        Assert.True(bytes.Length >= 3);
        Assert.Equal(0xEF, bytes[0]);
        Assert.Equal(0xBB, bytes[1]);
        Assert.Equal(0xBF, bytes[2]);
    }
}