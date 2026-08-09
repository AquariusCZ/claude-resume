using System;
using System.Collections.Generic;
using System.Diagnostics;
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
        string source = "claudecode";
        string cwd = Path.Combine(_tempDir, "work");
        Directory.CreateDirectory(cwd);
        string stdinJson = JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["hook_event_name"] = "Stop",
            ["session_id"] = "sess-123",
            ["cwd"] = cwd,
            ["stop_hook_active"] = false,
        });
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
        Assert.Equal(cwd, root.GetProperty("cwd").GetString());
    }

    /// <summary>
    /// 测试5:同一负载连续两次 TryWriteEvent,第二次返回 false,目录中只有一个文件,eventId 相同。
    /// </summary>
    [Fact]
    public void TryWriteEvent_SamePayloadTwice_IsIdempotent()
    {
        // Arrange
        string source = "claudecode";
        string cwd = Path.Combine(_tempDir, "work2");
        Directory.CreateDirectory(cwd);
        string stdinJson = JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["hook_event_name"] = "Stop",
            ["session_id"] = "sess-456",
            ["cwd"] = cwd,
            ["stop_hook_active"] = false,
        });
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
    /// 测试8:缺少工作目录时不抛异常,但拒绝产生无法归属项目的事件。
    /// </summary>
    [Fact]
    public void TryWriteEvent_MissingFields_DoesNotThrow()
    {
        // Arrange
        string source = "claudecode";
        string stdinJson = """{"some_other_field": "value"}""";
        var env = new Dictionary<string, string?>();

        // Act & Assert:out 变量需在 lambda 外声明,否则其作用域不出 lambda。
        string capturedId = string.Empty;
        string capturedReason = string.Empty;
        var exception = Record.Exception(() =>
        {
            Program.TryWriteEvent(_tempDir, source, stdinJson, env, out string id, out string reason);
            capturedId = id;
            capturedReason = reason;
        });

        Assert.Null(exception);
        Assert.Equal(string.Empty, capturedId);
        Assert.Equal("stop_event_mismatch", capturedReason);
        Assert.Empty(Directory.GetFiles(_tempDir, "*.json"));
    }

    [Theory]
    [InlineData("claudecode")]
    [InlineData("qoder")]
    public void TryWriteEvent_StopSourceWithoutStopEvent_IsRejected(string source)
    {
        string cwd = Path.Combine(_tempDir, "missing-stop-" + source);
        Directory.CreateDirectory(cwd);
        string payload = JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["session_id"] = "session",
            ["cwd"] = cwd,
        });

        bool written = Program.TryWriteEvent(
            _tempDir, source, payload, new Dictionary<string, string?>(),
            out _, out string reason);

        Assert.False(written);
        Assert.Equal("stop_event_mismatch", reason);
    }

    [Fact]
    public void TryWriteEvent_RelativeWorkspace_IsRejectedForEverySource()
    {
        string payload = JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["hook_event_name"] = "TaskComplete",
            ["session_id"] = "session",
            ["cwd"] = "relative-project",
        });

        bool written = Program.TryWriteEvent(
            _tempDir, "cline", payload, new Dictionary<string, string?>(),
            out _, out string reason);

        Assert.False(written);
        Assert.Equal("workspace_not_absolute", reason);
    }

    [Fact]
    public void TryWriteEvent_ClaudeDoesNotBorrowQoderEnvironment()
    {
        string payload = JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["hook_event_name"] = "Stop",
            ["session_id"] = "claude-session",
        });
        var env = new Dictionary<string, string?>
        {
            ["QODER_CWD"] = Path.Combine(_tempDir, "qoder-only"),
        };

        bool written = Program.TryWriteEvent(
            _tempDir, "claudecode", payload, env, out _, out string reason);

        Assert.False(written);
        Assert.Equal("workspace_missing", reason);
    }

    [Fact]
    public void ResolvePayload_CodexReadsJsonFromLastArgument()
    {
        const string payload = """{"type":"agent-turn-complete","thread-id":"t","turn-id":"x","cwd":"C:\\work"}""";

        string? resolved = Program.ResolvePayload(
            "codex",
            ["--previous-notify", "[\"other.exe\"]", payload],
            string.Empty);

        Assert.Equal(payload, resolved);
    }

    [Fact]
    public void TryWriteEvent_CodexTopLevelPersistedThread_IsAccepted()
    {
        const string threadId = "019fe5b6-f28b-7e60-a01a-79c6ce5e1acc";
        string codexHome = CreateCodexRollout(threadId, subagent: false);
        string cwd = Path.Combine(_tempDir, "real-project");
        Directory.CreateDirectory(cwd);
        var env = new Dictionary<string, string?>
        {
            ["AI_RESUME_CODEX_HOME"] = codexHome,
            ["AI_RESUME_CODEX_DOCUMENTS_ROOT"] = Path.Combine(_tempDir, "Codex"),
        };

        bool written = Program.TryWriteEvent(
            _tempDir,
            "codex",
            CodexPayload(threadId, "turn-1", cwd),
            env,
            out string eventId,
            out string reason);

        Assert.True(written);
        Assert.Equal("written", reason);
        Assert.True(File.Exists(Path.Combine(_tempDir, eventId + ".json")));
    }

    [Fact]
    public void TryWriteEvent_CodexSubagentThread_IsRejected()
    {
        const string threadId = "019fe5ed-ce08-7b93-8a41-f342eeee9aff";
        string codexHome = CreateCodexRollout(threadId, subagent: true);
        string cwd = Path.Combine(_tempDir, "real-project");
        Directory.CreateDirectory(cwd);
        var env = new Dictionary<string, string?>
        {
            ["AI_RESUME_CODEX_HOME"] = codexHome,
            ["AI_RESUME_CODEX_DOCUMENTS_ROOT"] = Path.Combine(_tempDir, "Codex"),
        };

        bool written = Program.TryWriteEvent(
            _tempDir,
            "codex",
            CodexPayload(threadId, "turn-2", cwd),
            env,
            out _,
            out string reason);

        Assert.False(written);
        Assert.Equal("subagent_thread", reason);
    }

    [Theory]
    [InlineData("internal", false)]
    [InlineData("memory_consolidation", false)]
    [InlineData("user", true)]
    public void TryWriteEvent_CodexInternalThread_IsRejected(string threadSource, bool sourceInternal)
    {
        const string threadId = "019fe5ed-ce08-7b93-8a41-f342eeee9aff";
        string codexHome = CreateCodexRollout(
            threadId, subagent: false, threadSource: threadSource, sourceInternal: sourceInternal);
        string cwd = Path.Combine(_tempDir, "internal-project");
        Directory.CreateDirectory(cwd);
        var env = new Dictionary<string, string?>
        {
            ["AI_RESUME_CODEX_HOME"] = codexHome,
            ["AI_RESUME_CODEX_DOCUMENTS_ROOT"] = Path.Combine(_tempDir, "Codex"),
        };

        bool written = Program.TryWriteEvent(
            _tempDir, "codex", CodexPayload(threadId, "turn-internal", cwd), env,
            out _, out string reason);

        Assert.False(written);
        Assert.Equal("internal_thread", reason);
    }

    [Fact]
    public void TryWriteEvent_CodexGeneratedProjectlessDirectory_IsRejectedUnlessGitRootExists()
    {
        const string threadId = "019fe5b6-f28b-7e60-a01a-79c6ce5e1acc";
        string codexHome = CreateCodexRollout(threadId, subagent: false);
        string documentsRoot = Path.Combine(_tempDir, "Codex");
        string cwd = Path.Combine(documentsRoot, "2026-08-09", "generated-task");
        Directory.CreateDirectory(cwd);
        var env = new Dictionary<string, string?>
        {
            ["AI_RESUME_CODEX_HOME"] = codexHome,
            ["AI_RESUME_CODEX_DOCUMENTS_ROOT"] = documentsRoot,
        };

        bool rejected = Program.TryWriteEvent(
            _tempDir, "codex", CodexPayload(threadId, "turn-3", cwd), env,
            out _, out string rejectedReason);

        Directory.CreateDirectory(Path.Combine(cwd, ".git"));
        bool accepted = Program.TryWriteEvent(
            _tempDir, "codex", CodexPayload(threadId, "turn-4", cwd), env,
            out _, out string acceptedReason);

        Assert.False(rejected);
        Assert.Equal("projectless_workspace", rejectedReason);
        Assert.True(accepted);
        Assert.Equal("written", acceptedReason);
    }

    [Fact]
    public void TryWriteEvent_OpenCodeExplicitEventId_DistinguishesTwoIdleTurns()
    {
        string cwd = Path.Combine(_tempDir, "open-project");
        Directory.CreateDirectory(cwd);
        var env = new Dictionary<string, string?>();
        string First(string id) => JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["hook_event_name"] = "session.idle",
            ["session_id"] = "session-1",
            ["cwd"] = cwd,
            ["event_id"] = id,
        });

        Assert.True(Program.TryWriteEvent(_tempDir, "opencode", First("idle-1"), env, out string first));
        Assert.True(Program.TryWriteEvent(_tempDir, "opencode", First("idle-2"), env, out string second));
        Assert.NotEqual(first, second);
    }

    [Fact]
    public void ForwardPreviousNotify_TerminatesTimedOutProcessTree()
    {
        string pidPath = Path.Combine(_tempDir, "previous-notify.pid");
        string script = "require('fs').writeFileSync(" + JsonSerializer.Serialize(pidPath) +
                        ", String(process.pid)); setTimeout(() => {}, 30000);";
        string command = JsonSerializer.Serialize(new[] { "node", "-e", script });
        var stopwatch = Stopwatch.StartNew();

        bool forwarded = Program.ForwardPreviousNotify(
            ["--previous-notify", command], rawPayload: null, timeoutMilliseconds: 500);

        stopwatch.Stop();
        Assert.False(forwarded);
        Assert.InRange(stopwatch.Elapsed, TimeSpan.Zero, TimeSpan.FromSeconds(5));
        Assert.True(File.Exists(pidPath), "旧通知进程没有启动到可观测阶段");
        int pid = int.Parse(File.ReadAllText(pidPath));
        Assert.Throws<ArgumentException>(() => Process.GetProcessById(pid));
    }

    private string CreateCodexRollout(
        string threadId,
        bool subagent,
        string? threadSource = null,
        bool sourceInternal = false)
    {
        string codexHome = Path.Combine(_tempDir, "codex-home-" + threadId);
        string sessions = Path.Combine(codexHome, "sessions", "2026", "08", "09");
        Directory.CreateDirectory(sessions);
        var payload = new Dictionary<string, object?>
        {
            ["id"] = threadId,
            ["session_id"] = threadId,
            ["parent_thread_id"] = subagent ? "019fe5b6-f28b-7e60-a01a-79c6ce5e1acc" : null,
            ["thread_source"] = threadSource ?? (subagent ? "subagent" : "user"),
            ["source"] = subagent
                ? new Dictionary<string, object?> { ["subagent"] = new { } }
                : sourceInternal
                    ? new Dictionary<string, object?> { ["internal"] = new { } }
                    : "vscode",
        };
        string line = JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["type"] = "session_meta",
            ["payload"] = payload,
        });
        File.WriteAllText(Path.Combine(sessions, $"rollout-test-{threadId}.jsonl"), line + Environment.NewLine);
        return codexHome;
    }

    private static string CodexPayload(string threadId, string turnId, string cwd)
        => JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["type"] = "agent-turn-complete",
            ["thread-id"] = threadId,
            ["turn-id"] = turnId,
            ["cwd"] = cwd,
        });
}
