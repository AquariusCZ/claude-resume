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
    /// Stop 负载的 background_tasks(本机 Claude Code 的 schema:id/type/status/description,shell 带 command、
    /// subagent 带 agent_type;在途 = running 或 pending):只有结束后会唤醒会话的子代理/workflow/云端会话才算「还在忙」。
    /// </summary>
    [Theory]
    [InlineData("subagent", "pending", true)]
    [InlineData("subagent", "running", true)]
    [InlineData("workflow", "running", true)]
    [InlineData("cloud session", "running", true)]
    [InlineData("shell", "running", false)]
    [InlineData("monitor", "running", false)]
    [InlineData("teammate", "running", false)]
    [InlineData("dream", "running", false)]
    [InlineData("subagent", "completed", false)]
    public void ClaudeStop_BackgroundBusy_OnlyForWorkThatWakesTheSession(string type, string status, bool busy)
    {
        string cwd = Path.Combine(_tempDir, "bg-" + type.Replace(' ', '-') + "-" + status);
        Directory.CreateDirectory(cwd);
        var task = new Dictionary<string, object?> { ["id"] = "t1", ["type"] = type, ["status"] = status, ["description"] = "合成任务" };
        if (type == "shell") task["command"] = "npm run dev";
        if (type == "subagent") task["agent_type"] = "general-purpose";
        string stdinJson = JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["hook_event_name"] = "Stop",
            ["session_id"] = "sess-bg",
            ["cwd"] = cwd,
            ["stop_hook_active"] = false,
            ["background_tasks"] = new[] { task },
        });

        Assert.True(Program.TryWriteEvent(_tempDir, "claudecode", stdinJson, new Dictionary<string, string?>(), out string eventId));

        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(_tempDir, eventId + ".json")));
        Assert.Equal(busy, doc.RootElement.TryGetProperty("backgroundBusy", out JsonElement flag) && flag.ValueKind == JsonValueKind.True);
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
    public void TryWriteEvent_CodexHooksStopPayload_UsesTranscriptPathAndIsAccepted()
    {
        const string threadId = "019fe5b6-f28b-7e60-a01a-79c6ce5e1bdd";
        string codexHome = CreateCodexRollout(threadId, subagent: false);
        string rollout = Directory.GetFiles(codexHome, "*" + threadId + ".jsonl", SearchOption.AllDirectories).Single();
        string cwd = Path.Combine(_tempDir, "real-project");
        Directory.CreateDirectory(cwd);
        // hooks Stop 的 stdin 形状(固定上游 0.148.0):没有 type,有 hook_event_name/session_id/turn_id/transcript_path。
        string payload = System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["session_id"] = threadId,
            ["turn_id"] = "turn-hooks-1",
            ["cwd"] = cwd,
            ["transcript_path"] = rollout,
            ["hook_event_name"] = "Stop",
            ["model"] = "gpt-5.6-sol",
            ["stop_hook_active"] = false,
            ["last_assistant_message"] = "done",
        });
        var env = new Dictionary<string, string?>
        {
            // 不给 AI_RESUME_CODEX_HOME:transcript_path 已经指向 rollout,不应再依赖目录扫描。
            ["AI_RESUME_CODEX_DOCUMENTS_ROOT"] = Path.Combine(_tempDir, "Codex"),
        };

        bool written = Program.TryWriteEvent(_tempDir, "codex", payload, env, out string eventId, out string reason);

        Assert.True(written, reason);
        Assert.Equal("written", reason);
        string stored = File.ReadAllText(Path.Combine(_tempDir, eventId + ".json"));
        Assert.Contains("\"turn-hooks-1\"", stored, StringComparison.Ordinal);

        // 其它 hooks 事件不是 turn 结束,必须拒绝。
        string sessionEnd = payload.Replace("\"Stop\"", "\"SessionEnd\"");
        Assert.False(Program.TryWriteEvent(_tempDir, "codex", sessionEnd, env, out _, out string rejected));
        Assert.Equal("codex_event_not_complete", rejected);

        // transcript_path 文件名与线程 id 不符时退回按 id 扫描;扫描根不存在则拒绝。
        string forged = System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["session_id"] = threadId,
            ["turn_id"] = "turn-hooks-2",
            ["cwd"] = cwd,
            ["transcript_path"] = rollout.Replace(threadId, "f" + threadId[1..]),
            ["hook_event_name"] = "Stop",
        });
        var noHome = new Dictionary<string, string?> { ["AI_RESUME_CODEX_HOME"] = Path.Combine(_tempDir, "missing-home") };
        Assert.False(Program.TryWriteEvent(_tempDir, "codex", forged, noHome, out _, out string missing));
        Assert.Equal("rollout_missing", missing);
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

    [Theory]
    [InlineData("session.execution.succeeded", "completion", true)]
    [InlineData("session.idle", "completion", true)]
    [InlineData("session.execution.interrupted", "completion", false)]
    [InlineData("permission.asked", "decision", true)]
    [InlineData("form.created", "decision", true)]
    [InlineData("session.execution.failed", "decision", true)]
    [InlineData("session.execution.succeeded", "decision", false)]
    public void TryWriteEvent_OpenCode2事件准入(string eventName, string kind, bool admitted)
    {
        string cwd = Path.Combine(_tempDir, "open2-" + eventName + "-" + kind);
        Directory.CreateDirectory(cwd);
        string payload = JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["hook_event_name"] = eventName,
            ["session_id"] = "ses_test",
            ["cwd"] = cwd,
            ["event_id"] = "evt_" + eventName + kind,
        });

        bool written = Program.TryWriteEvent(_tempDir, "opencode", payload, new Dictionary<string, string?>(),
            out _, out string reason, kind);

        Assert.Equal(admitted, written);
        if (!admitted)
        {
            Assert.Contains("opencode", reason, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void TryWriteEvent_OpenCode同一上游事件经不同实例到达只写一次()
    {
        var env = new Dictionary<string, string?>();
        string Payload(string cwd, string timestamp) => JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["hook_event_name"] = "session.execution.succeeded",
            ["session_id"] = "ses_dup",
            ["cwd"] = cwd,
            ["event_id"] = "evt_0001",
            ["timestamp"] = timestamp,
        });
        string a = Path.Combine(_tempDir, "dup-a");
        string b = Path.Combine(_tempDir, "dup-b");
        Directory.CreateDirectory(a);
        Directory.CreateDirectory(b);

        Assert.True(Program.TryWriteEvent(_tempDir, "opencode", Payload(a, "2026-10-30T08:00:00Z"), env, out string first, out _));
        Assert.False(Program.TryWriteEvent(_tempDir, "opencode", Payload(b, "2026-10-30T08:00:05Z"), env, out string second, out string reason));
        Assert.Equal(first, second);
        Assert.Equal("duplicate", reason);
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

[Fact]
    public void TryWriteEvent_CodexLegacyNotifyAndHooksStop_SameTurnShareEventId()
    {
        const string threadId = "019fe5b6-f28b-7e60-a01a-79c6ce5e1bee";
        string codexHome = CreateCodexRollout(threadId, subagent: false);
        string rollout = Directory.GetFiles(codexHome, "*" + threadId + ".jsonl", SearchOption.AllDirectories).Single();
        string cwd = Path.Combine(_tempDir, "real-project-dual");
        Directory.CreateDirectory(cwd);
        var env = new Dictionary<string, string?>
        {
            ["AI_RESUME_CODEX_HOME"] = codexHome,
            ["AI_RESUME_CODEX_DOCUMENTS_ROOT"] = Path.Combine(_tempDir, "Codex"),
        };
        // 升级期:config.toml 旧 notify 链与 hooks.json 的 Stop 钩子同时存在,同一回合送来两个形状不同的负载。
        string legacy = JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["type"] = "agent-turn-complete", ["thread-id"] = threadId, ["turn-id"] = "turn-dual", ["cwd"] = cwd,
            ["last-assistant-message"] = "done",
        });
        string hooks = JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["session_id"] = threadId, ["turn_id"] = "turn-dual", ["cwd"] = cwd, ["transcript_path"] = rollout,
            ["hook_event_name"] = "Stop", ["stop_hook_active"] = false, ["last_assistant_message"] = "done",
        });

        Assert.True(Program.TryWriteEvent(_tempDir, "codex", legacy, env, out string first, out string reason1), reason1);
        bool second = Program.TryWriteEvent(_tempDir, "codex", hooks, env, out string secondId, out string reason2);

        Assert.Equal(first, secondId);
        Assert.False(second, reason2);
        Assert.Single(Directory.GetFiles(_tempDir, "*.json"));
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
