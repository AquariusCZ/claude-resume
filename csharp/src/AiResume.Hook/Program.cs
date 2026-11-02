using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AiResume.Hook;

/// <summary>
/// 各 AI 客户端完成钩子的统一入口。只做事件准入与落盘,不读取凭据、不联网。
/// </summary>
public static class Program
{
    private const int MaxRolloutScanDepth = 6;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly Regex CodexThreadIdRegex = new(
        "^[0-9a-z][0-9a-z-]{0,99}$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex CodexGeneratedDateRegex = new(
        "^\\d{4}-\\d{2}-\\d{2}$", RegexOptions.CultureInvariant);

    private sealed record NormalizedEvent(
        string Source,
        string? SessionId,
        string? TurnId,
        string? TaskId,
        string Cwd,
        string? TranscriptPath,
        string? ExplicitEventId,
        string? Timestamp,
        string? LastAssistantMessage,
        bool Smoke,
        string Kind = CompletionKind,
        string? NotificationType = null,
        ClaudeDetails? Claude = null);

    /// <summary>
    /// Claude Code 官方 hook 负载里与通知有关的字段(2026-10-30 按官方 hooks 文档核对)。
    /// 只留结构化短值：错误类别、通知类型、是否仍有后台任务;不留消息正文与错误详情。
    /// </summary>
    private sealed record ClaudeDetails(
        string? HookEvent,
        string? PromptId,
        string? Error,
        string? MessageKey,
        bool StopHookActive,
        bool BackgroundBusy,
        bool Bridged);

    /// <summary>任务跑完。原有行为,文案是「✅ 已完成」。</summary>
    public const string CompletionKind = "completion";

    /// <summary>AI 停下来等人:需要输入或弹出了确认框。卡住的这一刻才是要把人拉回来的时刻。</summary>
    public const string DecisionKind = "decision";

    /// <summary>
    /// 回合因 API 错误中断(Claude Code 的 StopFailure:用量上限、登录失效、账单等)。
    /// 官方说明这时 Stop 不触发——此前这类中断完全没有通知。
    /// </summary>
    public const string FailureKind = "failure";

    /// <summary>内部任务、递归 Stop、空负载或非法 JSON 一律不入队。</summary>
    public static bool ShouldSuppress(string? payloadJson, IDictionary<string, string?> env)
        => ShouldSuppress(payloadJson, env, source: null);

    /// <summary>
    /// 同上，按来源区分 <c>stop_hook_active</c>:Claude Code 的 <c>stop_hook_active=true</c> 只表示
    /// “Claude 正因某个 Stop hook 而继续”,我方 hook 从不阻断，没有递归风险;直接丢弃会吞掉
    /// /goal 等场景下真正的结束通知。Claude Code 改为记录该标志，由投递端按会话合并。
    /// </summary>
    public static bool ShouldSuppress(string? payloadJson, IDictionary<string, string?> env, string? source)
    {
        if (env.TryGetValue("AI_RESUME_INTERNAL_RUN", out string? internalRun) &&
            string.Equals(internalRun, "1", StringComparison.Ordinal))
        {
            return true;
        }

        if (string.IsNullOrWhiteSpace(payloadJson))
        {
            return true;
        }

        try
        {
            using JsonDocument doc = JsonDocument.Parse(payloadJson);
            return doc.RootElement.ValueKind != JsonValueKind.Object ||
                   (!string.Equals(source, "claudecode", StringComparison.Ordinal) &&
                    doc.RootElement.TryGetProperty("stop_hook_active", out JsonElement active) &&
                    active.ValueKind == JsonValueKind.True);
        }
        catch (JsonException)
        {
            return true;
        }
    }

    /// <summary>
    /// 解析上游负载。Codex 官方把 JSON 作为 notify 命令最后一个参数传入;
    /// 其余客户端把 JSON 放在 stdin。为兼容包装器,stdin 无效时也会从参数末尾回退。
    /// </summary>
    public static string? ResolvePayload(string source, IReadOnlyList<string> args, string? stdinJson)
    {
        if (!string.Equals(source, "codex", StringComparison.OrdinalIgnoreCase) && IsJsonObject(stdinJson))
        {
            return stdinJson;
        }

        for (int i = args.Count - 1; i >= 0; i--)
        {
            if (IsJsonObject(args[i]))
            {
                return args[i];
            }
        }

        return IsJsonObject(stdinJson) ? stdinJson : null;
    }

    /// <summary>
    /// 从 <c>--kind=decision</c> 取事件种类。**由命令行而不是 payload 决定** ——
    /// settings.json 里每个 matcher 分组配一条自己的命令,种类因此在配置期就确定了,
    /// 不用赌上游 payload 里那个字段叫什么名字。认不出来一律按完成处理。
    /// </summary>
    public static string ReadKind(IReadOnlyList<string> args)
    {
        foreach (string arg in args)
        {
            if (arg.StartsWith("--kind=", StringComparison.Ordinal) &&
                arg["--kind=".Length..] is DecisionKind or FailureKind)
            {
                return arg["--kind=".Length..];
            }
        }

        return CompletionKind;
    }

    /// <summary>保留既有测试/调用方使用的稳定 ID 入口。</summary>
    public static string ComputeEventId(string source, string? sessionId, string? cwd, string? transcriptPath)
        => ComputeEventId(source, sessionId, null, null, cwd, transcriptPath, null, null, null);

    /// <summary>规范化并原子写入事件。返回 false 表示被抑制、被准入拒绝或已存在。</summary>
    public static bool TryWriteEvent(
        string eventsDirectory,
        string source,
        string? payloadJson,
        IDictionary<string, string?> env,
        out string eventId)
        => TryWriteEvent(eventsDirectory, source, payloadJson, env, out eventId, out _);

    /// <summary>同上,额外返回稳定的拒绝原因,供测试与诊断使用。</summary>
    public static bool TryWriteEvent(
        string eventsDirectory,
        string source,
        string? payloadJson,
        IDictionary<string, string?> env,
        out string eventId,
        out string reason,
        string kind = CompletionKind)
    {
        eventId = string.Empty;
        reason = "suppressed";
        string normalizedSource = NormalizeSource(source);

        if (ShouldSuppress(payloadJson, env, normalizedSource))
        {
            return false;
        }

        using JsonDocument doc = JsonDocument.Parse(payloadJson!);
        if (!TryNormalize(normalizedSource, doc.RootElement, env, kind, out NormalizedEvent? item, out reason) ||
            item is null)
        {
            return false;
        }

        if (string.Equals(item.Source, "codex", StringComparison.Ordinal) &&
            !AdmitCodex(item, env, out reason))
        {
            return false;
        }

        eventId = ComputeEventId(
            item.Source, item.SessionId, item.TurnId, item.TaskId, item.Cwd,
            item.TranscriptPath, item.ExplicitEventId, item.Timestamp, item.LastAssistantMessage,
            item.Kind, item.NotificationType, item.Claude);
        string targetPath = Path.Combine(eventsDirectory, eventId + ".json");
        if (File.Exists(targetPath))
        {
            reason = "duplicate";
            return false;
        }

        var eventPayload = new Dictionary<string, object?>
        {
            ["version"] = 1,
            ["eventId"] = eventId,
            ["source"] = item.Source,
            ["sessionId"] = item.SessionId,
            ["turnId"] = item.TurnId,
            ["taskId"] = item.TaskId,
            ["cwd"] = item.Cwd,
            ["transcriptPath"] = item.TranscriptPath,
            ["smoke"] = item.Smoke,
            ["kind"] = item.Kind,
            ["notificationType"] = item.NotificationType,
            ["atUtc"] = DateTimeOffset.UtcNow.ToString("o", CultureInfo.InvariantCulture),
        };
        if (item.Claude is { } claude)
        {
            eventPayload["hookEvent"] = claude.HookEvent;
            eventPayload["promptId"] = claude.PromptId;
            eventPayload["error"] = claude.Error;
            eventPayload["stopHookActive"] = claude.StopHookActive;
            eventPayload["backgroundBusy"] = claude.BackgroundBusy;
            eventPayload["bridged"] = claude.Bridged;
        }

        try
        {
            Directory.CreateDirectory(eventsDirectory);
            string tempPath = Path.Combine(eventsDirectory, eventId + ".tmp-" + Guid.NewGuid().ToString("N"));
            try
            {
                string json = JsonSerializer.Serialize(eventPayload, new JsonSerializerOptions { WriteIndented = true });
                using (var stream = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                using (var writer = new StreamWriter(stream, new UTF8Encoding(false)))
                {
                    writer.Write(json);
                    writer.Flush();
                    stream.Flush(true);
                }

                File.Move(tempPath, targetPath, overwrite: false);
                reason = "written";
                return true;
            }
            finally
            {
                try
                {
                    if (File.Exists(tempPath))
                    {
                        File.Delete(tempPath);
                    }
                }
                catch (IOException)
                {
                }
            }
        }
        catch (IOException)
        {
            reason = File.Exists(targetPath) ? "duplicate" : "write_failed";
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            reason = "write_denied";
            return false;
        }
    }

    /// <summary>进程入口。任何异常都返回 0,不阻断宿主客户端。</summary>
    public static int Main(string[] args)
    {
        try
        {
            string source = NormalizeSource(args.Length > 0 ? args[0] : string.Empty);
            string[] sourceArgs = args.Skip(1).ToArray();
            string kind = ReadKind(sourceArgs);
            // Codex 官方把 JSON 作为最后一个 argv。若先无条件 ReadToEnd(stdin),
            // 宿主继承了未关闭的控制台输入时会永久等待 EOF,完成通知进程也就不退出。
            // 只有 argv 没有可用 JSON 时才做兼容性 stdin 回退。
            string? stdinJson = string.Equals(source, "codex", StringComparison.Ordinal) &&
                                sourceArgs.Any(IsJsonObject)
                ? null
                : ReadUtf8StandardInput();
            string? payload = ResolvePayload(source, sourceArgs, stdinJson);
            Dictionary<string, string?> env = SnapshotEnvironment();

            string eventsDirectory = Path.Combine(AiResume.Worker.ShadowPaths.Root, "completion-events");
            TryWriteEvent(eventsDirectory, source, payload, env, out _, out _, kind);

            if (string.Equals(source, "codex", StringComparison.Ordinal))
            {
                ForwardPreviousNotify(sourceArgs, payload);
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"AiResume.Hook error: {ex.Message}");
        }

        return 0;
    }

    /// <summary>
    /// Claude Code、Cline、Qoder 和 OpenCode 都按 UTF-8 向 Hook stdin 写 JSON。
    /// Windows 控制台默认代码页可能是 CP936，不能通过 <see cref="Console.In"/> 解码。
    /// </summary>
    private static string ReadUtf8StandardInput()
    {
        using Stream input = Console.OpenStandardInput();
        using var reader = new StreamReader(
            input,
            StrictUtf8,
            detectEncodingFromByteOrderMarks: false,
            bufferSize: 1024,
            leaveOpen: false);
        string payload = reader.ReadToEnd();
        return payload.Length > 0 && payload[0] == '\uFEFF'
            ? payload[1..]
            : payload;
    }

    /// <summary>把 Codex 既有 notify 链继续向后转发;失败不影响本次 AI Resume 入队。</summary>
    public static bool ForwardPreviousNotify(
        IReadOnlyList<string> args,
        string? rawPayload,
        int timeoutMilliseconds = 5000)
    {
        int marker = -1;
        for (int i = 0; i < args.Count; i++)
        {
            if (string.Equals(args[i], "--previous-notify", StringComparison.Ordinal))
            {
                marker = i;
                break;
            }
        }

        if (marker < 0 || marker + 1 >= args.Count)
        {
            return false;
        }

        string[]? command;
        try
        {
            command = JsonSerializer.Deserialize<string[]>(args[marker + 1]);
        }
        catch (JsonException)
        {
            return false;
        }

        if (command is null || command.Length == 0 ||
            command[0].EndsWith(".cmd", StringComparison.OrdinalIgnoreCase) ||
            command[0].EndsWith(".bat", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        try
        {
            var psi = new ProcessStartInfo(command[0])
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            foreach (string arg in command.Skip(1))
            {
                psi.ArgumentList.Add(arg);
            }
            if (!string.IsNullOrWhiteSpace(rawPayload))
            {
                psi.ArgumentList.Add(rawPayload);
            }

            using Process? process = Process.Start(psi);
            if (process is null)
            {
                return false;
            }

            Task<string> stdout = process.StandardOutput.ReadToEndAsync();
            Task<string> stderr = process.StandardError.ReadToEndAsync();
            bool exited = process.WaitForExit(timeoutMilliseconds);
            if (!exited)
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                    process.WaitForExit(1000);
                }
                catch (Exception)
                {
                }
            }
            Task.WaitAll([stdout, stderr], 1000);
            return exited;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static bool TryNormalize(
        string source,
        JsonElement root,
        IDictionary<string, string?> env,
        string kind,
        out NormalizedEvent? item,
        out string reason)
    {
        item = null;
        reason = "source_unsupported";
        if (source is not ("codex" or "claudecode" or "cline" or "qoder" or "opencode"))
        {
            return false;
        }

        string? eventName = GetString(root, "hook_event_name") ?? GetString(root, "hookEventName");
        if (source == "codex")
        {
            // 两种官方协议并存:旧 notify 把 {"type":"agent-turn-complete",…} 放在最后一个 argv;
            // 0.148.0 起的 hooks Stop 把 {"hook_event_name":"Stop","session_id":…,"turn_id":…} 写进 stdin。
            // 两者都代表一整个 turn 结束;其它事件(SessionEnd、SubagentStop 等)一律拒绝。
            string? legacyType = GetString(root, "type");
            bool legacyComplete = string.Equals(legacyType, "agent-turn-complete", StringComparison.Ordinal);
            bool hookStop = legacyType is null && string.Equals(eventName, "Stop", StringComparison.Ordinal);
            if (!legacyComplete && !hookStop)
            {
                reason = "codex_event_not_complete";
                return false;
            }
            eventName = legacyComplete ? "agent-turn-complete" : "Stop";
        }
        else if (source == "claudecode" && kind == DecisionKind)
        {
            // 决策类只认 Notification。种类由 settings.json 的 matcher 选定并经命令行传入;
            // 官方文档列出了 notification_type / message / title 字段，但准入仍以事件名为准。
            if (!string.Equals(eventName, "Notification", StringComparison.Ordinal))
            {
                reason = "notification_event_mismatch";
                return false;
            }
        }
        else if (source == "claudecode" && kind == FailureKind)
        {
            if (!string.Equals(eventName, "StopFailure", StringComparison.Ordinal))
            {
                reason = "stop_failure_event_mismatch";
                return false;
            }
        }
        else if ((source == "claudecode" || source == "qoder") &&
                 !string.Equals(eventName, "Stop", StringComparison.Ordinal))
        {
            reason = "stop_event_mismatch";
            return false;
        }
        else if (source == "cline" && eventName is not null &&
                 !string.Equals(eventName, "TaskComplete", StringComparison.Ordinal))
        {
            reason = "cline_event_mismatch";
            return false;
        }
        else if (source == "opencode" && kind == DecisionKind)
        {
            // OpenCode 2.x 只在三种「停下来等人」时带 --kind=decision:permission.asked(授权,含子 agent ——
            // 子 agent 的授权请求同样会卡住整件事)、form.created(AI 提问)、session.execution.failed
            // (顶层运行失败且宽限期内未被自动续跑)。
            if (eventName is not ("permission.asked" or "form.created" or "session.execution.failed"))
            {
                reason = "opencode_decision_event_mismatch";
                return false;
            }
        }
        else if (source == "opencode" &&
                 eventName is not ("session.execution.succeeded" or "session.idle"))
        {
            // session.execution.succeeded 是 2.x 的完成边界;session.idle 只为 1.x 旧插件保留兼容。
            reason = "opencode_event_mismatch";
            return false;
        }

        string? sessionId = GetFirstString(root, "thread-id", "thread_id", "threadId", "session_id", "sessionId");
        string? turnId = GetFirstString(root, "turn-id", "turn_id", "turnId");
        string? taskId = GetFirstString(root, "task_id", "taskId");
        string? cwd = GetFirstString(root, "cwd", "working_directory", "workingDirectory") ??
                      GetFirstArrayString(root, "workspaceRoots", "workspace_roots");
        string? transcriptPath = GetFirstString(root, "transcript_path", "transcriptPath");

        // Qoder 的环境变量是该客户端专属补充字段。宿主进程可能长期保留这些变量,
        // 不能让它们替 Claude/Cline/OpenCode 的缺失字段“补出”一个伪事件。
        if (source == "qoder" && string.IsNullOrWhiteSpace(sessionId))
        {
            env.TryGetValue("QODER_SESSION_ID", out sessionId);
        }
        if (source == "qoder" && string.IsNullOrWhiteSpace(cwd))
        {
            env.TryGetValue("QODER_CWD", out cwd);
        }
        if (source == "qoder" && string.IsNullOrWhiteSpace(transcriptPath))
        {
            env.TryGetValue("QODER_TRANSCRIPT_PATH", out transcriptPath);
        }

        if (string.IsNullOrWhiteSpace(cwd))
        {
            reason = "workspace_missing";
            return false;
        }

        if (!Path.IsPathFullyQualified(cwd))
        {
            reason = "workspace_not_absolute";
            return false;
        }

        if (source == "codex" && string.IsNullOrWhiteSpace(sessionId))
        {
            reason = "thread_id_missing";
            return false;
        }
        if (source == "codex" && string.IsNullOrWhiteSpace(turnId))
        {
            reason = "turn_id_missing";
            return false;
        }

        bool smoke = root.TryGetProperty("smoke", out JsonElement smokeElement) &&
                     smokeElement.ValueKind == JsonValueKind.True;
        ClaudeDetails? claude = source == "claudecode" ? ReadClaudeDetails(root, eventName, env) : null;
        item = new NormalizedEvent(
            source,
            NullIfBlank(sessionId),
            NullIfBlank(turnId),
            NullIfBlank(taskId),
            cwd,
            NullIfBlank(transcriptPath),
            GetFirstString(root, "event_id", "eventId"),
            GetFirstString(root, "timestamp", "created_at", "createdAt"),
            GetFirstString(root, "last_assistant_message", "lastAssistantMessage", "last-assistant-message"),
            smoke,
            kind,
            GetFirstString(root, "notification_type", "notificationType"),
            claude);
        reason = "normalized";
        return true;
    }

    /// <summary>
    /// Claude Code 官方 hook 负载的通知相关字段。<c>error</c> 是官方枚举(rate_limit / authentication_failed …),
    /// 只收短的字母数字值;Notification 的 <c>message</c> 只取哈希参与事件 ID,正文不落盘。
    /// <c>background_tasks</c>(2.1.145+)里仍有非 shell 类任务在跑，说明这次 Stop 只是暂停等后台唤醒。
    /// <c>CLAUDE_CODE_BRIDGE_SESSION_ID</c> 有值表示该会话正连着 Remote Control(手机可直接处理)。
    /// </summary>
    private static ClaudeDetails ReadClaudeDetails(JsonElement root, string? eventName, IDictionary<string, string?> env)
    {
        string? error = GetString(root, "error");
        if (error is not null && (error.Length > 64 || !error.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-')))
        {
            error = "unknown";
        }

        string? message = GetString(root, "message");
        string? messageKey = string.IsNullOrEmpty(message)
            ? null
            : Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(message)).AsSpan(0, 8)).ToLowerInvariant();

        // background_tasks 是「在途」的后台工作(官方 schema:running/pending + backgrounded)。只有结束后会唤醒会话
        // 继续干活的类型才说明这次 Stop 只是暂停:子代理、workflow、云端会话。shell(含开发服务器)、monitor、
        // teammate、dream、auto-mode scan、memory import 这类常驻或内务任务一直在,算进来会让每次完成都推迟 30 分钟。
        // 类型名取自本机 Claude Code 的映射表(local_agent→subagent、local_workflow→workflow、remote_agent→cloud session)。
        bool backgroundBusy = false;
        if (root.TryGetProperty("background_tasks", out JsonElement tasks) && tasks.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement task in tasks.EnumerateArray())
            {
                if (task.ValueKind == JsonValueKind.Object &&
                    GetString(task, "status")?.ToLowerInvariant() is "running" or "pending" &&
                    GetString(task, "type")?.ToLowerInvariant() is "subagent" or "workflow" or "cloud session")
                {
                    backgroundBusy = true;
                    break;
                }
            }
        }

        return new ClaudeDetails(
            eventName,
            GetFirstString(root, "prompt_id", "promptId"),
            error,
            messageKey,
            root.TryGetProperty("stop_hook_active", out JsonElement active) && active.ValueKind == JsonValueKind.True,
            backgroundBusy,
            GetEnv(env, "CLAUDE_CODE_BRIDGE_SESSION_ID") is not null);
    }

    private static bool AdmitCodex(
        NormalizedEvent item,
        IDictionary<string, string?> env,
        out string reason)
    {
        if (!Path.IsPathFullyQualified(item.Cwd))
        {
            reason = "workspace_not_absolute";
            return false;
        }

        if (IsCodexProjectlessRoot(item.Cwd, env))
        {
            reason = "projectless_workspace";
            return false;
        }

        // hooks Stop 直接给了 transcript_path:文件名以 -<thread id>.jsonl 结尾才采信,
        // 否则仍按线程 id 扫描 sessions/,不让一条可伪造的路径替代归属核验。
        string? rollout = null;
        if (!string.IsNullOrWhiteSpace(item.TranscriptPath) &&
            item.TranscriptPath.EndsWith("-" + item.SessionId + ".jsonl", StringComparison.OrdinalIgnoreCase) &&
            Path.IsPathFullyQualified(item.TranscriptPath) && File.Exists(item.TranscriptPath))
        {
            rollout = item.TranscriptPath;
        }
        else if (!TryFindCodexRollout(item.SessionId!, env, out rollout, out reason))
        {
            return false;
        }

        if (!TryReadSessionMeta(
                rollout!, out string? id, out string? parentId, out string? threadSource,
                out bool sourceSubagent, out bool sourceInternal))
        {
            reason = "rollout_meta_missing";
            return false;
        }

        if (!string.Equals(id, item.SessionId, StringComparison.OrdinalIgnoreCase))
        {
            reason = "rollout_meta_mismatch";
            return false;
        }

        if (!string.IsNullOrWhiteSpace(parentId) ||
            string.Equals(threadSource, "subagent", StringComparison.OrdinalIgnoreCase) ||
            sourceSubagent)
        {
            reason = "subagent_thread";
            return false;
        }

        if (sourceInternal || IsInternalThreadSource(threadSource))
        {
            reason = "internal_thread";
            return false;
        }

        reason = "ok";
        return true;
    }

    private static bool TryFindCodexRollout(
        string threadId,
        IDictionary<string, string?> env,
        out string? file,
        out string reason)
    {
        file = null;
        if (!CodexThreadIdRegex.IsMatch(threadId))
        {
            reason = "thread_id_invalid";
            return false;
        }

        string codexHome = GetEnv(env, "AI_RESUME_CODEX_HOME") ?? GetEnv(env, "CODEX_HOME") ??
                           Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex");
        string suffix = "-" + threadId + ".jsonl";
        var roots = new List<(string Path, int Depth)>();
        if (TryCodexThreadDate(threadId, out string[]? dateParts))
        {
            roots.Add((Path.Combine([codexHome, "sessions", .. dateParts!]), 1));
        }
        roots.Add((Path.Combine(codexHome, "sessions"), MaxRolloutScanDepth));
        roots.Add((Path.Combine(codexHome, "archived_sessions"), MaxRolloutScanDepth));

        bool scanFailed = false;
        foreach ((string root, int depth) in roots)
        {
            if (TryFindFile(root, suffix, depth, out file, out bool failed))
            {
                reason = "ok";
                return true;
            }
            scanFailed |= failed;
        }

        reason = scanFailed ? "rollout_scan_error" : "rollout_missing";
        return false;
    }

    private static bool TryFindFile(
        string root,
        string suffix,
        int maxDepth,
        out string? file,
        out bool scanFailed)
    {
        file = null;
        scanFailed = false;
        var stack = new Stack<(string Path, int Depth)>();
        stack.Push((root, 0));

        while (stack.Count > 0)
        {
            (string current, int depth) = stack.Pop();
            IEnumerable<string> entries;
            try
            {
                entries = Directory.EnumerateFileSystemEntries(current).ToArray();
            }
            catch (DirectoryNotFoundException)
            {
                continue;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                scanFailed = true;
                continue;
            }

            foreach (string entry in entries)
            {
                try
                {
                    FileAttributes attributes = File.GetAttributes(entry);
                    if ((attributes & FileAttributes.Directory) == 0)
                    {
                        if (entry.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                        {
                            file = entry;
                            return true;
                        }
                    }
                    else if (depth < maxDepth && (attributes & FileAttributes.ReparsePoint) == 0)
                    {
                        stack.Push((entry, depth + 1));
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    scanFailed = true;
                }
            }
        }

        return false;
    }

    private static bool TryReadSessionMeta(
        string file,
        out string? id,
        out string? parentId,
        out string? threadSource,
        out bool sourceSubagent,
        out bool sourceInternal)
    {
        id = parentId = threadSource = null;
        sourceSubagent = sourceInternal = false;
        try
        {
            using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, leaveOpen: true);
            while (stream.Position <= 256 * 1024 && reader.ReadLine() is { } line)
            {
                if (!IsJsonObject(line))
                {
                    continue;
                }

                using JsonDocument doc = JsonDocument.Parse(line);
                JsonElement root = doc.RootElement;
                if (!string.Equals(GetString(root, "type"), "session_meta", StringComparison.Ordinal) ||
                    !root.TryGetProperty("payload", out JsonElement payload) ||
                    payload.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                id = GetString(payload, "id") ?? GetString(payload, "session_id");
                parentId = GetString(payload, "parent_thread_id");
                threadSource = GetString(payload, "thread_source");
                if (payload.TryGetProperty("source", out JsonElement source) &&
                    source.ValueKind == JsonValueKind.Object)
                {
                    sourceSubagent = source.TryGetProperty("subagent", out _);
                    sourceInternal = source.TryGetProperty("internal", out _);
                }
                return true;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
        }

        return false;
    }

    private static bool IsInternalThreadSource(string? threadSource)
    {
        string value = threadSource?.Trim() ?? string.Empty;
        return string.Equals(value, "internal", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(value, "memory_consolidation", StringComparison.OrdinalIgnoreCase) ||
               value.StartsWith("memory_", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsCodexProjectlessRoot(string cwd, IDictionary<string, string?> env)
    {
        string documentsRoot = GetEnv(env, "AI_RESUME_CODEX_DOCUMENTS_ROOT") ??
                               Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Codex");
        string resolved;
        string relative;
        try
        {
            resolved = Path.GetFullPath(cwd);
            relative = Path.GetRelativePath(Path.GetFullPath(documentsRoot), resolved);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }

        if (string.IsNullOrEmpty(relative) || Path.IsPathFullyQualified(relative) ||
            relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
        {
            return false;
        }

        string[] parts = relative.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
        return parts.Length >= 2 && CodexGeneratedDateRegex.IsMatch(parts[0]) &&
               !HasGitBoundary(resolved, documentsRoot);
    }

    private static bool HasGitBoundary(string start, string stop)
    {
        string current = Path.GetFullPath(start).TrimEnd(Path.DirectorySeparatorChar);
        string boundary = Path.GetFullPath(stop).TrimEnd(Path.DirectorySeparatorChar);
        while (!string.IsNullOrEmpty(current))
        {
            if (Directory.Exists(Path.Combine(current, ".git")) || File.Exists(Path.Combine(current, ".git")))
            {
                return true;
            }
            if (string.Equals(current, boundary, StringComparison.OrdinalIgnoreCase))
            {
                break;
            }
            string? parent = Directory.GetParent(current)?.FullName;
            if (parent is null || !parent.StartsWith(boundary, StringComparison.OrdinalIgnoreCase))
            {
                break;
            }
            current = parent;
        }
        return false;
    }

    private static bool TryCodexThreadDate(string threadId, out string[]? parts)
    {
        parts = null;
        string hex = threadId.Replace("-", string.Empty, StringComparison.Ordinal);
        if (hex.Length < 12 || !long.TryParse(hex[..12], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out long ms))
        {
            return false;
        }

        try
        {
            DateTimeOffset date = DateTimeOffset.FromUnixTimeMilliseconds(ms);
            if (date.Year is < 2020 or > 2200)
            {
                return false;
            }
            parts = [date.Year.ToString("0000", CultureInfo.InvariantCulture),
                     date.Month.ToString("00", CultureInfo.InvariantCulture),
                     date.Day.ToString("00", CultureInfo.InvariantCulture)];
            return true;
        }
        catch (ArgumentOutOfRangeException)
        {
            return false;
        }
    }

    private static string ComputeEventId(
        string source,
        string? sessionId,
        string? turnId,
        string? taskId,
        string? cwd,
        string? transcriptPath,
        string? explicitEventId,
        string? timestamp,
        string? lastAssistantMessage,
        string kind = CompletionKind,
        string? notificationType = null,
        ClaudeDetails? claude = null)
    {
        string transcriptTime = string.Empty;
        if (!string.IsNullOrWhiteSpace(transcriptPath) && File.Exists(transcriptPath))
        {
            try
            {
                transcriptTime = File.GetLastWriteTimeUtc(transcriptPath).ToString("o", CultureInfo.InvariantCulture);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }

        // Codex 的 turn_id 是全局唯一的回合身份。旧 notify 负载与 hooks Stop 负载描述同一回合时字段集合不同
        // (有无 transcript_path / last_assistant_message),把这些拼进哈希就会让升级期用户每回合收到两条。
        if (source == "codex" && !string.IsNullOrWhiteSpace(sessionId) && !string.IsNullOrWhiteSpace(turnId))
        {
            string turnRaw = string.Join("|", source, sessionId, turnId, cwd, kind);
            return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(turnRaw)).AsSpan(0, 8)).ToLowerInvariant();
        }

        // OpenCode 2.x 的 event_id 由上游全局唯一生成(evt_… / permission:per_… / form:…),同一事件可能经
        // 多个位置实例或重复投递到达,cwd 与时间戳不得参与去重。
        if (source == "opencode" && !string.IsNullOrWhiteSpace(explicitEventId))
        {
            string openCodeRaw = string.Join("|", source, explicitEventId, kind);
            return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(openCodeRaw)).AsSpan(0, 8)).ToLowerInvariant();
        }

        // Claude Code:同一提示里可能有多次 Stop(/goal 继续)与多次 Notification(不同工具的授权),
        // 只靠 transcript 修改时间(官方说明异步写入、可能滞后)会撞 ID。把官方的 prompt_id、事件名、
        // 通知类型、错误类别和消息哈希都纳入，让不同的等待/中断各有自己的 ID,“谁是最终结束”交给投递端合并。
        string raw = claude is null
            ? string.Join("|", source, sessionId, turnId, taskId, cwd, transcriptTime,
                explicitEventId, timestamp, lastAssistantMessage, kind)
            : string.Join("|", source, sessionId, turnId, taskId, cwd, transcriptTime,
                explicitEventId, timestamp, lastAssistantMessage, kind,
                claude.HookEvent, claude.PromptId, notificationType, claude.Error, claude.MessageKey);
        byte[] hashBytes = SHA256.HashData(Encoding.UTF8.GetBytes(raw));
        return Convert.ToHexString(hashBytes.AsSpan(0, 8)).ToLowerInvariant();
    }

    private static string NormalizeSource(string source)
        => source.Trim().ToLowerInvariant() switch
        {
            "claude" or "claude-code" or "claudecode" => "claudecode",
            "codex" => "codex",
            "cline" => "cline",
            "qoder" => "qoder",
            "opencode" => "opencode",
            _ => source.Trim().ToLowerInvariant(),
        };

    private static Dictionary<string, string?> SnapshotEnvironment()
    {
        var env = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (System.Collections.DictionaryEntry entry in Environment.GetEnvironmentVariables())
        {
            env[entry.Key?.ToString() ?? string.Empty] = entry.Value?.ToString();
        }
        return env;
    }

    private static string? GetEnv(IDictionary<string, string?> env, string name)
        => env.TryGetValue(name, out string? value) && !string.IsNullOrWhiteSpace(value) ? value : null;

    private static bool IsJsonObject(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }
        try
        {
            using JsonDocument doc = JsonDocument.Parse(value);
            return doc.RootElement.ValueKind == JsonValueKind.Object;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static string? GetFirstString(JsonElement root, params string[] names)
    {
        foreach (string name in names)
        {
            string? value = GetString(root, name);
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value;
            }
        }
        return null;
    }

    private static string? GetFirstArrayString(JsonElement root, params string[] names)
    {
        foreach (string name in names)
        {
            if (!root.TryGetProperty(name, out JsonElement array) || array.ValueKind != JsonValueKind.Array)
            {
                continue;
            }
            foreach (JsonElement item in array.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(item.GetString()))
                {
                    return item.GetString();
                }
            }
        }
        return null;
    }

    private static string? GetString(JsonElement root, string propertyName)
        => root.TryGetProperty(propertyName, out JsonElement element) && element.ValueKind == JsonValueKind.String
            ? element.GetString()
            : null;

    private static string? NullIfBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;
}
