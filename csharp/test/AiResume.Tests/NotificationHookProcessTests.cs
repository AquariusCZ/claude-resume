using System.Diagnostics;
using System.Text;
using System.Text.Json;
using AiResume.Worker.Notifications;
using Xunit;

namespace AiResume.Tests;

/// <summary>
/// 通知 Hook 的真实进程边界测试。这里不调用 Program.TryWriteEvent,
/// 而是启动最终交付的 AiResume.Hook.exe,覆盖 stdin/argv、环境变量和脚本包装层。
/// </summary>
public sealed class NotificationHookProcessTests : IDisposable
{
    private readonly string _root = TestTemp.NewDir("notify-hook-process");

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    [Theory]
    [InlineData("claudecode", "Stop")]
    [InlineData("cline", "TaskComplete")]
    [InlineData("qoder", "Stop")]
    [InlineData("opencode", "session.idle")]
    public async Task HookExe_StdinProtocols_WriteOneEvent(string source, string eventName)
    {
        string shadow = Path.Combine(_root, source);
        string cwd = Path.Combine(_root, "公司内网SOP项目-" + source);
        Directory.CreateDirectory(cwd);
        string payload = JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["hook_event_name"] = eventName,
            ["session_id"] = "session-" + source,
            ["cwd"] = cwd,
            ["event_id"] = "process-" + source,
        });

        ProcessResult result = await RunHookAsync(source, payload, shadow, payloadAsArgument: false);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(string.Empty, result.Stdout);
        string eventPath = Assert.Single(Directory.GetFiles(Path.Combine(shadow, "completion-events"), "*.json"));
        using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(eventPath));
        Assert.Equal(source, doc.RootElement.GetProperty("source").GetString());
        Assert.Equal(cwd, doc.RootElement.GetProperty("cwd").GetString());
    }

    [Fact]
    public async Task HookExe_CodexArgumentProtocol_AdmitsPersistedTopLevelThread()
    {
        const string threadId = "019fe5b6-f28b-7e60-a01a-79c6ce5e1acc";
        string shadow = Path.Combine(_root, "codex-shadow");
        string codexHome = CreateCodexHome(threadId);
        string cwd = Path.Combine(_root, "公司内网SOP项目-codex");
        Directory.CreateDirectory(cwd);
        string payload = JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["type"] = "agent-turn-complete",
            ["thread-id"] = threadId,
            ["turn-id"] = "turn-process",
            ["cwd"] = cwd,
        });

        ProcessResult result = await RunHookAsync(
            "codex",
            payload,
            shadow,
            payloadAsArgument: true,
            new Dictionary<string, string?>
            {
                ["AI_RESUME_CODEX_HOME"] = codexHome,
                ["AI_RESUME_CODEX_DOCUMENTS_ROOT"] = Path.Combine(_root, "generated-codex"),
            });

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(string.Empty, result.Stdout);
        string eventPath = Assert.Single(Directory.GetFiles(Path.Combine(shadow, "completion-events"), "*.json"));
        using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(eventPath));
        Assert.Equal("codex", doc.RootElement.GetProperty("source").GetString());
        Assert.Equal(threadId, doc.RootElement.GetProperty("sessionId").GetString());
        Assert.Equal("turn-process", doc.RootElement.GetProperty("turnId").GetString());
    }

    [Fact]
    public async Task HookExe_CodexArgumentProtocol_DoesNotWaitForInheritedStdinEof()
    {
        const string threadId = "019fe5b6-f28b-7e60-a01a-79c6ce5e1acc";
        string shadow = Path.Combine(_root, "codex-open-stdin-shadow");
        string cwd = Path.Combine(_root, "workspace-codex-open-stdin");
        Directory.CreateDirectory(cwd);
        string payload = JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["type"] = "agent-turn-complete",
            ["thread-id"] = threadId,
            ["turn-id"] = "turn-open-stdin",
            ["cwd"] = cwd,
        });
        var psi = new ProcessStartInfo(HookExe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        psi.ArgumentList.Add("codex");
        psi.ArgumentList.Add(payload);
        psi.Environment["AIRESUME_SHADOW_DIR"] = shadow;
        psi.Environment["AI_RESUME_CODEX_HOME"] = CreateCodexHome(threadId);
        psi.Environment["AI_RESUME_CODEX_DOCUMENTS_ROOT"] = Path.Combine(_root, "generated-codex");

        using Process process = Process.Start(psi)!;
        Task exit = process.WaitForExitAsync();
        Task completed = await Task.WhenAny(exit, Task.Delay(TimeSpan.FromSeconds(3)));
        if (completed != exit)
        {
            process.Kill(entireProcessTree: true);
        }

        Assert.Same(exit, completed);
        Assert.Equal(0, process.ExitCode);
        Assert.Single(Directory.GetFiles(Path.Combine(shadow, "completion-events"), "*.json"));
    }

    [Fact]
    public async Task HookExe_InvalidUtf8Stdin_IsRejectedWithoutQueueing()
    {
        string shadow = Path.Combine(_root, "invalid-utf8-shadow");
        byte[] prefix = Encoding.UTF8.GetBytes(
            "{\"hook_event_name\":\"Stop\",\"session_id\":\"invalid\",\"cwd\":\"C:\\\\bad");
        byte[] suffix = Encoding.UTF8.GetBytes("name\"}");
        byte[] payload = Encoding.UTF8.GetPreamble()
            .Concat(prefix)
            .Append((byte)0xFF)
            .Concat(suffix)
            .ToArray();
        var psi = new ProcessStartInfo(HookExe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        psi.ArgumentList.Add("claudecode");
        psi.Environment["AIRESUME_SHADOW_DIR"] = shadow;

        using Process process = Process.Start(psi)!;
        await process.StandardInput.BaseStream.WriteAsync(payload);
        process.StandardInput.BaseStream.Close();
        string stdout = await process.StandardOutput.ReadToEndAsync();
        string stderr = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();

        Assert.Equal(0, process.ExitCode);
        Assert.Equal(string.Empty, stdout);
        Assert.False(string.IsNullOrWhiteSpace(stderr));
        Assert.False(Directory.Exists(Path.Combine(shadow, "completion-events")));
    }

    [Fact]
    public async Task HookExe_Utf8BomStdin_PreservesChinesePayload()
    {
        string shadow = Path.Combine(_root, "utf8-bom-shadow");
        string cwd = Path.Combine(_root, "公司内网SOP项目-utf8-bom");
        Directory.CreateDirectory(cwd);
        string json = JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["hook_event_name"] = "Stop",
            ["session_id"] = "utf8-bom",
            ["cwd"] = cwd,
            ["event_id"] = "utf8-bom",
        });
        byte[] payload = Encoding.UTF8.GetPreamble()
            .Concat(Encoding.UTF8.GetBytes(json))
            .ToArray();
        var psi = new ProcessStartInfo(HookExe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        psi.ArgumentList.Add("claudecode");
        psi.Environment["AIRESUME_SHADOW_DIR"] = shadow;

        using Process process = Process.Start(psi)!;
        await process.StandardInput.BaseStream.WriteAsync(payload);
        process.StandardInput.BaseStream.Close();
        await process.WaitForExitAsync();

        Assert.Equal(0, process.ExitCode);
        string eventPath = Assert.Single(Directory.GetFiles(Path.Combine(shadow, "completion-events"), "*.json"));
        using JsonDocument queued = JsonDocument.Parse(File.ReadAllText(eventPath));
        Assert.Equal(cwd, queued.RootElement.GetProperty("cwd").GetString());
    }

    [Fact]
    public async Task ClineWrapper_PipesStdinToPreviousAndAiResumeHook()
    {
        string shadow = Path.Combine(_root, "cline-wrapper-shadow");
        string cwd = Path.Combine(_root, "公司内网SOP项目-cline-wrapper");
        Directory.CreateDirectory(cwd);
        string previousInput = Path.Combine(_root, "previous-input.json");
        string previousScript = Path.Combine(_root, "previous.ps1");
        string wrapper = Path.Combine(_root, "TaskComplete.ps1");
        File.WriteAllText(
            previousScript,
            "$stdin = [Console]::In.ReadToEnd()\r\n" +
            $"[IO.File]::WriteAllText('{EscapePowerShell(previousInput)}', $stdin)\r\n" +
            "Write-Output '{\"cancel\":false}'\r\n");
        WriteClineWrapper(wrapper, previousScript);
        string payload = JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["hook_event_name"] = "TaskComplete",
            ["session_id"] = "cline-wrapper-session",
            ["cwd"] = cwd,
            ["event_id"] = "cline-wrapper-event",
        });

        var psi = new ProcessStartInfo("powershell.exe")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false),
            StandardOutputEncoding = new UTF8Encoding(false),
            StandardErrorEncoding = new UTF8Encoding(false),
        };
        psi.ArgumentList.Add("-NoProfile");
        psi.ArgumentList.Add("-ExecutionPolicy");
        psi.ArgumentList.Add("Bypass");
        psi.ArgumentList.Add("-File");
        psi.ArgumentList.Add(wrapper);
        psi.Environment["AIRESUME_SHADOW_DIR"] = shadow;

        using Process process = Process.Start(psi)!;
        await process.StandardInput.WriteAsync(payload);
        process.StandardInput.Close();
        string stdout = await process.StandardOutput.ReadToEndAsync();
        string stderr = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();

        Assert.Equal(0, process.ExitCode);
        Assert.True(string.IsNullOrWhiteSpace(stderr), stderr);
        using JsonDocument response = JsonDocument.Parse(stdout.Trim());
        Assert.False(response.RootElement.GetProperty("cancel").GetBoolean());
        Assert.Equal(payload, File.ReadAllText(previousInput).TrimEnd());
        string eventPath = Assert.Single(Directory.GetFiles(Path.Combine(shadow, "completion-events"), "*.json"));
        using JsonDocument queued = JsonDocument.Parse(File.ReadAllText(eventPath));
        Assert.Equal(cwd, queued.RootElement.GetProperty("cwd").GetString());
    }

    [Fact]
    public async Task ClineWrapper_WithoutPrevious_PreservesUtf8Stdin()
    {
        string shadow = Path.Combine(_root, "cline-wrapper-no-previous-shadow");
        string cwd = Path.Combine(_root, "公司内网SOP项目-cline-no-previous");
        Directory.CreateDirectory(cwd);
        string wrapper = Path.Combine(_root, "TaskComplete-no-previous.ps1");
        WriteClineWrapper(wrapper, string.Empty);
        string payload = JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["hook_event_name"] = "TaskComplete",
            ["session_id"] = "cline-no-previous-session",
            ["cwd"] = cwd,
            ["event_id"] = "cline-no-previous-event",
        });

        var psi = new ProcessStartInfo("powershell.exe")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false),
            StandardOutputEncoding = new UTF8Encoding(false),
            StandardErrorEncoding = new UTF8Encoding(false),
        };
        foreach (string arg in new[] { "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", wrapper })
        {
            psi.ArgumentList.Add(arg);
        }
        psi.Environment["AIRESUME_SHADOW_DIR"] = shadow;

        using Process process = Process.Start(psi)!;
        await process.StandardInput.WriteAsync(payload);
        process.StandardInput.Close();
        string stdout = await process.StandardOutput.ReadToEndAsync();
        string stderr = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();

        Assert.Equal(0, process.ExitCode);
        Assert.True(string.IsNullOrWhiteSpace(stderr), stderr);
        using JsonDocument response = JsonDocument.Parse(stdout.Trim());
        Assert.False(response.RootElement.GetProperty("cancel").GetBoolean());
        string eventPath = Assert.Single(Directory.GetFiles(Path.Combine(shadow, "completion-events"), "*.json"));
        using JsonDocument queued = JsonDocument.Parse(File.ReadAllText(eventPath));
        Assert.Equal(cwd, queued.RootElement.GetProperty("cwd").GetString());
    }

    [Fact]
    public async Task ClineWrapper_InvalidUtf8_DoesNotBlockClientOrQueueEvent()
    {
        string shadow = Path.Combine(_root, "cline-wrapper-invalid-utf8-shadow");
        string wrapper = Path.Combine(_root, "TaskComplete-invalid-utf8.ps1");
        WriteClineWrapper(wrapper, string.Empty);
        byte[] prefix = Encoding.UTF8.GetBytes("{\"hook_event_name\":\"TaskComplete\",\"cwd\":\"C:\\\\bad");
        byte[] suffix = Encoding.UTF8.GetBytes("name\"}");
        byte[] payload = Encoding.UTF8.GetPreamble()
            .Concat(prefix)
            .Append((byte)0xFF)
            .Concat(suffix)
            .ToArray();
        var psi = new ProcessStartInfo("powershell.exe")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = new UTF8Encoding(false),
            StandardErrorEncoding = new UTF8Encoding(false),
        };
        foreach (string arg in new[] { "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", wrapper })
        {
            psi.ArgumentList.Add(arg);
        }
        psi.Environment["AIRESUME_SHADOW_DIR"] = shadow;

        using Process process = Process.Start(psi)!;
        await process.StandardInput.BaseStream.WriteAsync(payload);
        process.StandardInput.BaseStream.Close();
        string stdout = await process.StandardOutput.ReadToEndAsync();
        string stderr = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();

        Assert.Equal(0, process.ExitCode);
        Assert.True(string.IsNullOrWhiteSpace(stderr), stderr);
        using JsonDocument response = JsonDocument.Parse(stdout.Trim());
        Assert.False(response.RootElement.GetProperty("cancel").GetBoolean());
        Assert.False(Directory.Exists(Path.Combine(shadow, "completion-events")));
    }

    [Fact]
    public async Task ClineWrapper_ParsesCancelFromStdoutEvenWhenPreviousWritesStderr()
    {
        string shadow = Path.Combine(_root, "cline-cancel-shadow");
        string cwd = Path.Combine(_root, "cline-cancel-workspace");
        Directory.CreateDirectory(cwd);
        string previousScript = Path.Combine(_root, "previous-cancel.ps1");
        string wrapper = Path.Combine(_root, "TaskComplete-cancel.ps1");
        File.WriteAllText(
            previousScript,
            "[Console]::Error.WriteLine('previous warning')\r\n" +
            "Write-Output '{\"cancel\":true}'\r\n");
        WriteClineWrapper(wrapper, previousScript);
        string payload = JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["hook_event_name"] = "TaskComplete",
            ["session_id"] = "cline-cancel-session",
            ["cwd"] = cwd,
        });

        var psi = new ProcessStartInfo("powershell.exe")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (string arg in new[] { "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", wrapper })
        {
            psi.ArgumentList.Add(arg);
        }
        psi.Environment["AIRESUME_SHADOW_DIR"] = shadow;

        using Process process = Process.Start(psi)!;
        await process.StandardInput.WriteAsync(payload);
        process.StandardInput.Close();
        string stdout = await process.StandardOutput.ReadToEndAsync();
        string stderr = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();

        Assert.Equal(0, process.ExitCode);
        using JsonDocument response = JsonDocument.Parse(stdout.Trim());
        Assert.True(response.RootElement.GetProperty("cancel").GetBoolean());
        Assert.Contains("previous warning", stderr);
        Assert.False(Directory.Exists(Path.Combine(shadow, "completion-events")));
    }

    [Fact]
    public async Task ClineWrapper_PreservesPreviousNonZeroExitCode()
    {
        string shadow = Path.Combine(_root, "cline-previous-exit-shadow");
        string previousDirectory = Path.Combine(_root, "O'Brien 公司内网");
        Directory.CreateDirectory(previousDirectory);
        string previousScript = Path.Combine(previousDirectory, "previous exit.ps1");
        string wrapper = Path.Combine(_root, "TaskComplete-previous-exit.ps1");
        File.WriteAllText(previousScript, "Write-Output 'previous nonzero marker'\r\nexit 7\r\n");
        WriteClineWrapper(wrapper, previousScript);
        string payload = JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["hook_event_name"] = "TaskComplete",
            ["session_id"] = "cline-previous-exit-session",
            ["cwd"] = Path.Combine(_root, "公司内网SOP项目-cline-exit"),
        });
        var psi = new ProcessStartInfo("powershell.exe")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false),
            StandardOutputEncoding = new UTF8Encoding(false),
            StandardErrorEncoding = new UTF8Encoding(false),
        };
        foreach (string arg in new[] { "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", wrapper })
        {
            psi.ArgumentList.Add(arg);
        }
        psi.Environment["AIRESUME_SHADOW_DIR"] = shadow;

        using Process process = Process.Start(psi)!;
        await process.StandardInput.WriteAsync(payload);
        process.StandardInput.Close();
        string stdout = await process.StandardOutput.ReadToEndAsync();
        string stderr = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();

        Assert.True(process.ExitCode == 7,
            $"应保留旧脚本退出码 7，实际 {process.ExitCode}；stdout={stdout}；stderr={stderr}");
        Assert.Contains("previous nonzero marker", stdout);
        Assert.False(Directory.Exists(Path.Combine(shadow, "completion-events")));
    }

    private async Task<ProcessResult> RunHookAsync(
        string source,
        string payload,
        string shadow,
        bool payloadAsArgument,
        IReadOnlyDictionary<string, string?>? extraEnvironment = null)
    {
        var psi = new ProcessStartInfo(HookExe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false),
            StandardOutputEncoding = new UTF8Encoding(false),
            StandardErrorEncoding = new UTF8Encoding(false),
        };
        psi.ArgumentList.Add(source);
        if (payloadAsArgument)
        {
            psi.ArgumentList.Add(payload);
        }
        psi.Environment["AIRESUME_SHADOW_DIR"] = shadow;
        if (extraEnvironment is not null)
        {
            foreach ((string key, string? value) in extraEnvironment)
            {
                psi.Environment[key] = value;
            }
        }

        using Process process = Process.Start(psi)!;
        if (!payloadAsArgument)
        {
            await process.StandardInput.WriteAsync(payload);
        }
        process.StandardInput.Close();
        string stdout = await process.StandardOutput.ReadToEndAsync();
        string stderr = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        return new ProcessResult(process.ExitCode, stdout, stderr);
    }

    private string CreateCodexHome(string threadId)
    {
        string home = Path.Combine(_root, "codex-home");
        string sessions = Path.Combine(home, "sessions", "2026", "08", "09");
        Directory.CreateDirectory(sessions);
        string meta = JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["type"] = "session_meta",
            ["payload"] = new Dictionary<string, object?>
            {
                ["id"] = threadId,
                ["session_id"] = threadId,
                ["parent_thread_id"] = null,
                ["thread_source"] = "user",
                ["source"] = "vscode",
            },
        });
        File.WriteAllText(Path.Combine(sessions, $"rollout-process-{threadId}.jsonl"), meta + Environment.NewLine);
        return home;
    }

    private static void WriteClineWrapper(string path, string previousScript)
    {
        File.WriteAllText(
            path,
            ClineNotificationAdapter.BuildWrapperScript(HookExe, previousScript),
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
    }

    private static string HookExe
    {
        get
        {
            string path = Path.ChangeExtension(typeof(AiResume.Hook.Program).Assembly.Location, ".exe");
            Assert.True(File.Exists(path), $"缺少测试用 Hook 可执行文件: {path}");
            return path;
        }
    }

    private static string EscapePowerShell(string value) => value.Replace("'", "''");

    private sealed record ProcessResult(int ExitCode, string Stdout, string Stderr);
}
