using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Xunit;
using AiResume.Hook;

namespace AiResume.Tests;

/// <summary>
/// HookHandler 核心逻辑测试:验证抑制判定、事件 ID 计算与幂等写入行为。
/// 所有测试均使用系统临时目录下的唯一子目录,不触碰真实 shadow 目录。
/// </summary>
public class HookHandlerTests : IDisposable
{
    private readonly string _tempDir;

    public HookHandlerTests()
    {
        // 为每个测试创建唯一临时目录
        _tempDir = TestTemp.NewDir("AiResumeTests");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        // 清理测试目录
        if (Directory.Exists(_tempDir))
        {
            try
            {
                Directory.Delete(_tempDir, recursive: true);
            }
            catch (IOException)
            {
                // 忽略清理失败
            }
            catch (UnauthorizedAccessException)
            {
                // 忽略清理失败
            }
        }
    }

    /// <summary>
    /// 测试1:stop_hook_active 为 true 时 ShouldSuppress 返回 true。
    /// </summary>
    [Fact]
    public void ShouldSuppress_StopHookActiveTrue_ReturnsTrue()
    {
        // Arrange
        string stdinJson = """{"stop_hook_active": true}""";
        var env = new Dictionary<string, string?>();

        // Act
        bool result = Program.ShouldSuppress(stdinJson, env);

        // Assert
        Assert.True(result);
    }

    /// <summary>
    /// 测试1:stop_hook_active 为 false 时 ShouldSuppress 返回 false。
    /// </summary>
    [Fact]
    public void ShouldSuppress_StopHookActiveFalse_ReturnsFalse()
    {
        // Arrange
        string stdinJson = """{"stop_hook_active": false}""";
        var env = new Dictionary<string, string?>();

        // Act
        bool result = Program.ShouldSuppress(stdinJson, env);

        // Assert
        Assert.False(result);
    }

    /// <summary>
    /// 测试1:stop_hook_active 缺失时 ShouldSuppress 返回 false。
    /// </summary>
    [Fact]
    public void ShouldSuppress_StopHookActiveMissing_ReturnsFalse()
    {
        // Arrange
        string stdinJson = """{"some_field": "value"}""";
        var env = new Dictionary<string, string?>();

        // Act
        bool result = Program.ShouldSuppress(stdinJson, env);

        // Assert
        Assert.False(result);
    }

    /// <summary>
    /// 测试2:AI_RESUME_INTERNAL_RUN=1 时 ShouldSuppress 返回 true。
    /// </summary>
    [Fact]
    public void ShouldSuppress_InternalRunSet_ReturnsTrue()
    {
        // Arrange
        string stdinJson = """{"stop_hook_active": false}""";
        var env = new Dictionary<string, string?>
        {
            ["AI_RESUME_INTERNAL_RUN"] = "1"
        };

        // Act
        bool result = Program.ShouldSuppress(stdinJson, env);

        // Assert
        Assert.True(result);
    }

    /// <summary>
    /// 测试3:stdinJson 为 null 时 ShouldSuppress 返回 true。
    /// </summary>
    [Fact]
    public void ShouldSuppress_NullStdin_ReturnsTrue()
    {
        // Arrange
        string? stdinJson = null;
        var env = new Dictionary<string, string?>();

        // Act
        bool result = Program.ShouldSuppress(stdinJson, env);

        // Assert
        Assert.True(result);
    }

    /// <summary>
    /// 测试3:stdinJson 为空串时 ShouldSuppress 返回 true。
    /// </summary>
    [Fact]
    public void ShouldSuppress_EmptyStdin_ReturnsTrue()
    {
        // Arrange
        string stdinJson = "";
        var env = new Dictionary<string, string?>();

        // Act
        bool result = Program.ShouldSuppress(stdinJson, env);

        // Assert
        Assert.True(result);
    }

    /// <summary>
    /// 测试3:stdinJson 为非法 JSON 时 ShouldSuppress 返回 true。
    /// </summary>
    [Fact]
    public void ShouldSuppress_InvalidJson_ReturnsTrue()
    {
        // Arrange
        string stdinJson = "not valid json";
        var env = new Dictionary<string, string?>();

        // Act
        bool result = Program.ShouldSuppress(stdinJson, env);

        // Assert
        Assert.True(result);
    }

    /// <summary>
    /// 测试4:TryWriteEvent 正常负载时返回 true,事件文件存在且字段正确。
    /// </summary>
    [Fact]
    public void TryWriteEvent_ValidPayload_WritesEventFile()
    {
        // Arrange
        string source = "test-source";
        string stdinJson = """{"session_id": "sess-123", "cwd": "/tmp/work", "stop_hook_active": false}""";
        var env = new Dictionary<string, string?>();

        // Act
        bool result = Program.TryWriteEvent(_tempDir, source, stdinJson, env, out string eventId);

        // Assert
        Assert.True(result);
        Assert.False(string.IsNullOrEmpty(eventId));

        string eventFile = Path.Combine(_tempDir, eventId + ".json");
        Assert.True(File.Exists(eventFile));

        using var doc = JsonDocument.Parse(File.ReadAllText(eventFile));
        var root = doc.RootElement;
        Assert.Equal(source, root.GetProperty("source").GetString());
        Assert.Equal("sess-123", root.GetProperty("sessionId").GetString());
        Assert.Equal("/tmp/work", root.GetProperty("cwd").GetString());
    }

    /// <summary>
    /// 测试5:同一负载连续两次 TryWriteEvent,第二次返回 false,目录中只有一个文件,eventId 相同。
    /// </summary>
    [Fact]
    public void TryWriteEvent_SamePayloadTwice_IsIdempotent()
    {
        // Arrange
        string source = "test-source";
        string stdinJson = """{"session_id": "sess-456", "cwd": "/tmp/work2", "stop_hook_active": false}""";
        var env = new Dictionary<string, string?>();

        // Act
        bool firstResult = Program.TryWriteEvent(_tempDir, source, stdinJson, env, out string firstEventId);
        bool secondResult = Program.TryWriteEvent(_tempDir, source, stdinJson, env, out string secondEventId);

        // Assert
        Assert.True(firstResult);
        Assert.False(secondResult);
        Assert.Equal(firstEventId, secondEventId);

        var files = Directory.GetFiles(_tempDir, "*.json");
        Assert.Single(files);
    }

    /// <summary>
    /// 测试6:被抑制时 TryWriteEvent 返回 false 且目录中无任何文件。
    /// </summary>
    [Fact]
    public void TryWriteEvent_Suppressed_ReturnsFalseAndNoFiles()
    {
        // Arrange
        string source = "test-source";
        string stdinJson = """{"stop_hook_active": true}""";
        var env = new Dictionary<string, string?>();

        // Act
        bool result = Program.TryWriteEvent(_tempDir, source, stdinJson, env, out string eventId);

        // Assert
        Assert.False(result);
        Assert.Equal(string.Empty, eventId);
        Assert.Empty(Directory.GetFiles(_tempDir));
    }

    /// <summary>
    /// 测试7:ComputeEventId 对相同输入稳定。
    /// </summary>
    [Fact]
    public void ComputeEventId_SameInput_ReturnsSameValue()
    {
        // Arrange
        string source = "source-a";
        string sessionId = "session-1";
        string cwd = "/tmp";
        string? transcriptPath = null;

        // Act
        string first = Program.ComputeEventId(source, sessionId, cwd, transcriptPath);
        string second = Program.ComputeEventId(source, sessionId, cwd, transcriptPath);

        // Assert
        Assert.Equal(first, second);
        Assert.Equal(16, first.Length);
    }

    /// <summary>
    /// 测试7:ComputeEventId 对不同 source 产生不同值。
    /// </summary>
    [Fact]
    public void ComputeEventId_DifferentSource_ReturnsDifferentValue()
    {
        // Arrange
        string sessionId = "session-1";
        string cwd = "/tmp";
        string? transcriptPath = null;

        // Act
        string first = Program.ComputeEventId("source-a", sessionId, cwd, transcriptPath);
        string second = Program.ComputeEventId("source-b", sessionId, cwd, transcriptPath);

        // Assert
        Assert.NotEqual(first, second);
    }

    /// <summary>
    /// 测试7:ComputeEventId 对不同 sessionId 产生不同值。
    /// </summary>
    [Fact]
    public void ComputeEventId_DifferentSessionId_ReturnsDifferentValue()
    {
        // Arrange
        string source = "source-a";
        string cwd = "/tmp";
        string? transcriptPath = null;

        // Act
        string first = Program.ComputeEventId(source, "session-1", cwd, transcriptPath);
        string second = Program.ComputeEventId(source, "session-2", cwd, transcriptPath);

        // Assert
        Assert.NotEqual(first, second);
    }

    /// <summary>
    /// 测试8:字段缺失(无 session_id/cwd)时不抛异常。
    /// </summary>
    [Fact]
    public void TryWriteEvent_MissingFields_DoesNotThrow()
    {
        // Arrange
        string source = "test-source";
        string stdinJson = """{"some_other_field": "value"}""";
        var env = new Dictionary<string, string?>();

        // Act & Assert:out 变量需在 lambda 外声明,否则其作用域不出 lambda。
        string capturedId = string.Empty;
        var exception = Record.Exception(() =>
        {
            Program.TryWriteEvent(_tempDir, source, stdinJson, env, out string id);
            capturedId = id;
        });

        Assert.Null(exception);
        Assert.False(string.IsNullOrEmpty(capturedId));
        Assert.True(File.Exists(Path.Combine(_tempDir, capturedId + ".json")));
    }
}