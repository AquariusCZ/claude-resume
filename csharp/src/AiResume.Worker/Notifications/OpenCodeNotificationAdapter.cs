using System;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace AiResume.Worker.Notifications;

/// <summary>
/// OpenCode 通知适配器(OpenCode ≥ 2.0)。
/// 在 ~/.config/opencode/plugins/ 下写入官方 v2 目录形态插件 <c>airesume-notify/index.ts</c>
/// (默认导出 <c>{ id, setup }</c>,靠自动发现加载，不写进用户的 opencode.jsonc)。
/// 完成 = 顶层会话的 <c>session.execution.succeeded</c> 且没有仍在运行的直接子会话;
/// 等你 = <c>permission.asked</c> 与 <c>form.created</c>(question);失败 = 顶层 <c>session.execution.failed</c>。
/// v1 单文件 <c>airesume-notify.ts</c> 在 2.x 下会被拒绝加载(2026-10-29 起实证),启用时迁移并删除我方旧文件。
/// 不修改用户任何既有插件;Disable 仅删除我方文件。「已启用」以 OpenCode 自己的日志为外部证据。
/// </summary>
public sealed class OpenCodeNotificationAdapter : INotificationAdapter
{
    /// <summary>我方 v2 插件目录名。</summary>
    public const string PluginDirectoryName = "airesume-notify";

    /// <summary>目录形态插件的入口文件(没有 package.json 时 OpenCode 按 server → index 解析)。</summary>
    public const string PluginEntryFileName = "index.ts";

    /// <summary>OpenCode 1.x 时代的单文件插件，只用于迁移与所有权识别。</summary>
    public const string LegacyPluginFileName = "airesume-notify.ts";

    /// <summary>v2 插件 id;同 id 重复加载时 OpenCode 报 Duplicate plugin ID。</summary>
    public const string PluginId = "ai-resume.notify";

    /// <summary>文件内容中的稳定所有权标记。文件名相同不能证明文件属于 AI Resume。</summary>
    public const string ManagedMarker = "// AI Resume managed OpenCode notification plugin";

    /// <summary>2026-08-09 之前生成文件的精确首行,仅用于一次性原位升级。</summary>
    public const string LegacyManagedMarker = "// AI Resume 通知插件 - 由 AI Resume 自动生成,请勿手动修改";

    /// <summary>只读 OpenCode 日志末尾这么多字节找加载证据(日志常年二三十 MB)。</summary>
    private const int LogTailBytes = 4 << 20;

    private readonly string _pluginsDirectory;
    private readonly string _logPath;

    /// <summary>
    /// 初始化适配器。
    /// </summary>
    /// <param name="pluginsDirectory">插件目录路径;默认与 OpenCode 一致:<c>%XDG_CONFIG_HOME%\opencode\plugins</c>,未设置时 %USERPROFILE%\.config\opencode\plugins。</param>
    /// <param name="logPath">OpenCode 日志;默认 <c>%XDG_DATA_HOME%\opencode\log\opencode.log</c>,未设置时 %USERPROFILE%\.local\share\opencode\log\opencode.log。</param>
    public OpenCodeNotificationAdapter(string? pluginsDirectory = null, string? logPath = null)
    {
        _pluginsDirectory = pluginsDirectory ?? GetDefaultPluginsDirectory();
        _logPath = logPath ?? Path.Combine(
            Probes.OpenCodeCredentials.DataDirectory(Environment.GetEnvironmentVariable,
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)),
            "log", "opencode.log");
    }

    /// <inheritdoc />
    public NotificationProviderKind Kind => NotificationProviderKind.OpenCode;

    /// <inheritdoc />
    public string DisplayName => "OpenCode";

    private string PluginDirectory => Path.Combine(_pluginsDirectory, PluginDirectoryName);

    private string EntryPath => Path.Combine(PluginDirectory, PluginEntryFileName);

    private string LegacyPath => Path.Combine(_pluginsDirectory, LegacyPluginFileName);

    /// <inheritdoc />
    public NotificationProviderStatus Probe()
    {
        try
        {
            // 插件目录的父目录(~/.config/opencode)存在即视为已安装
            var configDirectory = Path.GetDirectoryName(_pluginsDirectory);
            var isInstalled = !string.IsNullOrEmpty(configDirectory) && Directory.Exists(configDirectory);
            if (!isInstalled)
            {
                return new NotificationProviderStatus(Kind, DisplayName, false, false, null,
                    $"OpenCode 配置目录不存在: {configDirectory}");
            }

            if (File.Exists(EntryPath))
            {
                string source = File.ReadAllText(EntryPath, Encoding.UTF8);
                if (!IsManagedV2Source(source))
                {
                    return new NotificationProviderStatus(Kind, DisplayName, true, false, configDirectory,
                        $"同名插件目录属于用户或其他工具,未接管: {PluginDirectory}");
                }

                // 文件在不等于 OpenCode 收下了它:以 OpenCode 自己的日志为外部证据(只看写入之后的记录)。
                (string verdict, DateTimeOffset? at, string? cause) = ReadLoadEvidence(
                    PluginDirectory, new DateTimeOffset(File.GetLastWriteTimeUtc(EntryPath), TimeSpan.Zero));
                string detail = verdict switch
                {
                    "failed" => "OpenCode 拒绝加载 AI Resume 通知插件" + (cause is null ? string.Empty : ":" + cause),
                    "loaded" => $"已安装,OpenCode 已加载(日志 {at!.Value.ToLocalTime():MM-dd HH:mm})",
                    _ => "已写入插件,等 OpenCode 下次加载后核实",
                };
                return new NotificationProviderStatus(Kind, DisplayName, true, true, configDirectory, detail,
                    HookCommand: ParseHookCommand(source), HookBroken: verdict == "failed");
            }

            if (File.Exists(LegacyPath))
            {
                string legacy = File.ReadAllText(LegacyPath, Encoding.UTF8);
                return IsManagedLegacySource(legacy)
                    ? new NotificationProviderStatus(Kind, DisplayName, true, true, configDirectory,
                        "旧版插件已不被 OpenCode 2.x 加载,完成通知收不到;关掉再打开本开关(或重新安装)即可升级",
                        HookCommand: ParseHookCommand(legacy), HookBroken: true)
                    : new NotificationProviderStatus(Kind, DisplayName, true, false, configDirectory,
                        $"同名插件属于用户或其他工具,未接管: {LegacyPath}");
            }

            return new NotificationProviderStatus(Kind, DisplayName, true, false, configDirectory, "插件未安装");
        }
        catch (Exception ex)
        {
            return new NotificationProviderStatus(
                Kind,
                DisplayName,
                false,
                false,
                null,
                $"探测失败: {ex.Message}",
                Unverified: true);
        }
    }

    /// <inheritdoc />
    public void Enable(string hookCommand)
    {
        try
        {
            string source = BuildPluginSource(hookCommand);
            Directory.CreateDirectory(PluginDirectory);
            foreach (string existing in Directory.EnumerateFiles(PluginDirectory))
            {
                string name = Path.GetFileName(existing);
                bool entryLike = name.Equals("package.json", StringComparison.OrdinalIgnoreCase) ||
                    name.StartsWith("index.", StringComparison.OrdinalIgnoreCase) ||
                    name.StartsWith("server.", StringComparison.OrdinalIgnoreCase);
                if (entryLike && !(name.Equals(PluginEntryFileName, StringComparison.Ordinal) &&
                                   IsManagedV2Source(File.ReadAllText(existing, Encoding.UTF8))))
                {
                    throw new InvalidOperationException($"插件目录里有不属于 AI Resume 的入口文件,拒绝覆盖: {existing}");
                }
            }

            if (!File.Exists(EntryPath) ||
                !string.Equals(File.ReadAllText(EntryPath, Encoding.UTF8), source, StringComparison.Ordinal))
            {
                // 临时文件不带 .ts/.js 扩展名并以点开头：写到一半也不会被 OpenCode 当成插件发现。
                AtomicWrite(EntryPath, Path.Combine(PluginDirectory, ".index.tmp-" + Guid.NewGuid().ToString("N")), source);
            }

            // 新版写好后再退役我方旧单文件(OpenCode 热重载:先删旧再写新会出现一段没有插件的空窗)。
            if (File.Exists(LegacyPath) && IsManagedLegacySource(File.ReadAllText(LegacyPath, Encoding.UTF8)))
            {
                File.Copy(LegacyPath, LegacyPath + ".bak", overwrite: true);
                File.Delete(LegacyPath);
            }
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"启用 OpenCode 通知插件失败: {ex.Message}", ex);
        }
    }

    /// <inheritdoc />
    public void Disable()
    {
        try
        {
            if (File.Exists(EntryPath) && IsManagedV2Source(File.ReadAllText(EntryPath, Encoding.UTF8)))
            {
                File.Delete(EntryPath);
                if (Directory.Exists(PluginDirectory) && !Directory.EnumerateFileSystemEntries(PluginDirectory).Any())
                {
                    Directory.Delete(PluginDirectory);
                }
            }

            if (File.Exists(LegacyPath) && IsManagedLegacySource(File.ReadAllText(LegacyPath, Encoding.UTF8)))
            {
                File.Delete(LegacyPath);
            }
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"停用 OpenCode 通知插件失败: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// 生成 OpenCode v2 插件源码(OpenCode 2.2.3 源码与官方 v2 插件文档核对;离线 21 个场景与真实 Hook 端到端验证过)。
    /// 只有 <c>const cmd = …;</c> 一行随安装位置变化，<see cref="ParseHookCommand"/> 按同一契约反解。
    /// 源码不得出现 TypeScript 专有语法：测试会把同一份源码当 .mjs 在 Node 下执行。
    /// </summary>
    public static string BuildPluginSource(string hookCommand) =>
        PluginTemplate.Replace("const cmd = \"__AIRESUME_HOOK__\";", "const cmd = " + QuoteForTs(hookCommand) + ";", StringComparison.Ordinal);

    private const string PluginTemplate = """
        // AI Resume managed OpenCode notification plugin
        // 由 AI Resume 自动生成,请勿手动修改
        // generation: opencode-v2 (OpenCode >= 2.0 server plugin: default export { id, setup })
        //
        // 事件依据(OpenCode v2.2.3 源码与官方 v2 插件文档):
        //   完成 = 顶层 session 的 session.execution.succeeded,且没有仍在运行的直接子 session
        //          (与官方 TUI/Desktop 通知同一判据;session.idle 在 v2 已标 deprecated,服务端不再发布)
        //   等你 = permission.asked(含子 session)与 form.created(metadata.kind=question,AI 提问)
        //   失败 = 顶层 session 的 session.execution.failed,宽限期内未被自动续跑才按"等你处理"上报
        // 插件只把一行 JSON 交给 AiResume.Hook.exe;任何异常都不得影响 OpenCode。
        // 注意:本文件不得出现 TypeScript 专有语法,测试会把同一份源码当 .mjs 在 Node 下执行。
        import { spawn } from "node:child_process"

        const cmd = "__AIRESUME_HOOK__";

        const HOOK_TIMEOUT_MS = 30000
        const COMPLETION_GRACE_MS = 5000
        const FAILURE_GRACE_MS = 60000
        const MAX_REMEMBERED = 1024
        // 漏收某个子会话的结束事件(事件流中断)时,它不能永远挡住父会话的完成通知。
        const RUNNING_TTL_MS = 6 * 60 * 60 * 1000
        const TYPES = [
          "session.created",
          "session.deleted",
          "session.execution.started",
          "session.execution.succeeded",
          "session.execution.failed",
          "session.execution.interrupted",
          "permission.asked",
          "form.created",
        ]

        export default {
          id: "ai-resume.notify",
          setup(ctx) {
            const controller = new AbortController()
            const parents = new Map() // sessionID -> parentID | null(顶层)
            const directories = new Map() // sessionID -> 绝对目录
            const running = new Map() // 正在执行的 sessionID -> 开跑时刻(ms)
            const generations = new Map() // sessionID -> execution.started 次数(判断宽限期内是否又开跑,不依赖时钟)
            const delivered = new Set() // 本实例已处理的去重键
            const warned = new Set()

            const warn = (key, message, err) => {
              if (warned.has(key)) return
              warned.add(key)
              try {
                console.warn("[airesume-notify] " + message, err instanceof Error ? err.message : (err ?? ""))
              } catch {}
            }

            const once = (key) => {
              if (delivered.has(key)) return false
              delivered.add(key)
              if (delivered.size > MAX_REMEMBERED) delivered.delete(delivered.values().next().value)
              return true
            }

            const isSessionID = (value) => typeof value === "string" && value.startsWith("ses")

            const remember = (sessionID, parentID, directory) => {
              if (!isSessionID(sessionID)) return
              parents.set(sessionID, isSessionID(parentID) ? parentID : null)
              if (typeof directory === "string" && directory) directories.set(sessionID, directory)
            }

            // parentID / 目录优先取 session.created 缓存,缺失时向 OpenCode 查询一次;失败抛出,由调用方决定 fail-closed。
            const describe = async (sessionID) => {
              if (parents.has(sessionID) && directories.has(sessionID)) {
                return { parentID: parents.get(sessionID), directory: directories.get(sessionID) }
              }
              const info = await ctx.session.get({ sessionID })
              remember(sessionID, info?.parentID, info?.location?.directory)
              return { parentID: parents.get(sessionID) ?? null, directory: directories.get(sessionID) }
            }

            // 只看直接子 session:孙 session 向自己的父汇报。后台子 agent 完成后会唤醒父 session 再跑一轮,
            // 所以父 session 在子 session 仍运行时 succeeded 并不代表整件事做完(官方 TUI/Desktop 同一判据)。
            const hasRunningChild = async (sessionID) => {
              for (const [child, since] of [...running]) {
                if (child === sessionID) continue
                if (Date.now() - since > RUNNING_TTL_MS) {
                  running.delete(child)
                  continue
                }
                let parentID = parents.get(child)
                if (parentID === undefined) {
                  try {
                    parentID = (await describe(child)).parentID
                  } catch {
                    continue
                  }
                }
                if (parentID === sessionID) return true
              }
              return false
            }

            // 可被卸载打断的等待;到点后摘掉 abort 监听,避免长期运行时监听器累积。
            const sleep = (ms) =>
              new Promise((resolve) => {
                if (controller.signal.aborted) return resolve(undefined)
                const done = () => {
                  clearTimeout(timer)
                  controller.signal.removeEventListener("abort", done)
                  resolve(undefined)
                }
                const timer = setTimeout(done, ms)
                timer.unref?.()
                controller.signal.addEventListener("abort", done, { once: true })
              })

            const deliver = (payload, args) => {
              try {
                // argv 数组 + shell:false:安装目录 "AI Resume" 含空格也不会被拆分;JSON 走 stdin 并立即关闭,
                // Hook 读到 EOF 即落盘退出。不 await:慢 Hook 不能拖住事件循环;超时只杀这一个子进程。
                const child = spawn(cmd, args, { stdio: ["pipe", "ignore", "ignore"], windowsHide: true, shell: false })
                const timer = setTimeout(() => {
                  try {
                    child.kill()
                  } catch {}
                }, HOOK_TIMEOUT_MS)
                timer.unref?.()
                child.once("error", (err) => {
                  clearTimeout(timer)
                  warn("spawn:" + (err?.code ?? "error"), "无法启动 AI Resume 通知程序:", err)
                })
                child.once("exit", () => clearTimeout(timer))
                child.stdin?.on("error", () => {})
                child.stdin?.end(JSON.stringify(payload), "utf8")
                child.unref?.()
              } catch (err) {
                warn("spawn:throw", "无法启动 AI Resume 通知程序:", err)
              }
            }

            const iso = (created) => {
              const date = new Date(typeof created === "number" ? created : Date.now())
              return Number.isNaN(date.getTime()) ? new Date().toISOString() : date.toISOString()
            }

            const fallbackDirectory = (event) => event?.location?.directory || ctx.location?.directory

            // 同步维护状态,保证 started → succeeded 的先后不被异步查询打乱。
            const track = (event) => {
              const data = event?.data ?? {}
              switch (event?.type) {
                case "session.created":
                  remember(data.sessionID, data.parentID, data.location?.directory)
                  return
                case "session.deleted":
                  parents.delete(data.sessionID)
                  directories.delete(data.sessionID)
                  running.delete(data.sessionID)
                  generations.delete(data.sessionID)
                  return
                case "session.execution.started":
                  if (!isSessionID(data.sessionID)) return
                  running.set(data.sessionID, Date.now())
                  generations.set(data.sessionID, (generations.get(data.sessionID) ?? 0) + 1)
                  return
                case "session.execution.succeeded":
                case "session.execution.failed":
                case "session.execution.interrupted":
                  running.delete(data.sessionID)
                  return
              }
            }

            const decide = async (event) => {
              const data = event?.data ?? {}
              switch (event?.type) {
                case "session.execution.succeeded":
                case "session.execution.failed": {
                  const sessionID = data.sessionID
                  if (!isSessionID(sessionID) || typeof event.id !== "string" || !once(event.id)) return
                  // 在第一个 await 之前取代次:track 已同步处理完本事件,之后再变就说明又开跑了。
                  const generation = generations.get(sessionID) ?? 0
                  let session
                  try {
                    session = await describe(sessionID)
                  } catch (err) {
                    warn("lookup", "查询 session 失败,本次结束不通知(fail-closed):", err)
                    return
                  }
                  if (session.parentID) return // 子 agent 的结束不是整个任务的结束
                  const failed = event.type === "session.execution.failed"
                  // 宽限期:其它插件(如 auto-resume)或排队输入可能立刻让它再跑一轮,那就还没结束。
                  await sleep(failed ? FAILURE_GRACE_MS : COMPLETION_GRACE_MS)
                  if (controller.signal.aborted) return
                  if (running.has(sessionID) || (generations.get(sessionID) ?? 0) !== generation) return
                  if (!failed && (await hasRunningChild(sessionID))) return
                  const directory = session.directory || fallbackDirectory(event)
                  if (!directory) return
                  deliver(
                    {
                      hook_event_name: event.type,
                      session_id: sessionID,
                      cwd: directory,
                      event_id: event.id,
                      timestamp: iso(event.created),
                      ...(failed ? { notification_type: "execution_failed" } : {}),
                    },
                    failed ? ["opencode", "--kind=decision"] : ["opencode"],
                  )
                  return
                }
                case "permission.asked": {
                  // 不做顶层过滤:子 agent 的授权请求同样会卡住整件事(官方 Desktop 也对子 session 请求提醒)。
                  const sessionID = data.sessionID
                  if (!isSessionID(sessionID) || typeof data.id !== "string" || !once("permission:" + data.id)) return
                  let directory
                  try {
                    directory = (await describe(sessionID)).directory
                  } catch {}
                  directory = directory || fallbackDirectory(event)
                  if (!directory) return
                  deliver(
                    {
                      hook_event_name: "permission.asked",
                      notification_type: "permission_prompt",
                      session_id: sessionID,
                      cwd: directory,
                      event_id: "permission:" + data.id,
                      timestamp: iso(event.created),
                    },
                    ["opencode", "--kind=decision"],
                  )
                  return
                }
                case "form.created": {
                  const form = data.form ?? {}
                  // MCP elicitation 用 "global" 哨兵而非真实 session,会被每个位置实例同时收到,这里不接。
                  if (form.metadata?.kind !== "question" || !isSessionID(form.sessionID) || typeof form.id !== "string") return
                  if (!once("form:" + form.id)) return
                  let directory
                  try {
                    directory = (await describe(form.sessionID)).directory
                  } catch {}
                  directory = directory || fallbackDirectory(event)
                  if (!directory) return
                  deliver(
                    {
                      hook_event_name: "form.created",
                      notification_type: "agent_needs_input",
                      session_id: form.sessionID,
                      cwd: directory,
                      event_id: "form:" + form.id,
                      timestamp: iso(event.created),
                    },
                    ["opencode", "--kind=decision"],
                  )
                  return
                }
              }
            }

            // setup 必须立即返回:事件循环放后台,流异常时退避重订阅,卸载时 abort。
            void (async () => {
              let backoff = 1000
              while (!controller.signal.aborted) {
                try {
                  for await (const event of ctx.event.subscribe({ types: TYPES, signal: controller.signal })) {
                    backoff = 1000
                    try {
                      track(event)
                    } catch (err) {
                      warn("track", "更新会话状态失败:", err)
                    }
                    decide(event).catch((err) => warn("decide:" + event?.type, "处理事件失败:", err))
                  }
                } catch (err) {
                  if (controller.signal.aborted) return
                  warn("stream", "事件流中断,稍后重新订阅:", err)
                }
                if (controller.signal.aborted) return
                // 中断期间的结束事件收不到了(订阅不重放历史):清掉「正在执行」的记账,宁可偶尔早报一次完成,
                // 也不能让一个漏掉结束事件的子会话把父会话的完成通知永久吞掉。
                running.clear()
                await sleep(backoff)
                backoff = Math.min(backoff * 2, 30000)
              }
            })()

            return () => controller.abort()
          },
        }

        """;

    /// <summary>
    /// 在 OpenCode 日志末尾找本插件的加载结果(只看 <paramref name="after"/> 之后的记录)。
    /// 日志行形如 <c>timestamp=…Z level=WARN … message="failed to load plugin" target="C:\\…\\airesume-notify…"</c>,
    /// 路径里的反斜杠被双写。返回 failed / loaded / unknown。
    /// </summary>
    private (string Verdict, DateTimeOffset? At, string? Cause) ReadLoadEvidence(string pluginDirectory, DateTimeOffset after)
    {
        string tail;
        try
        {
            using var stream = new FileStream(_logPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            long start = Math.Max(0, stream.Length - LogTailBytes);
            stream.Seek(start, SeekOrigin.Begin);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            tail = reader.ReadToEnd();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return ("unknown", null, null);
        }

        string needle = pluginDirectory.Replace("\\", "\\\\", StringComparison.Ordinal);
        (string Verdict, DateTimeOffset? At, string? Cause) latest = ("unknown", null, null);
        foreach (string line in tail.Split('\n'))
        {
            int index = line.IndexOf(needle, StringComparison.OrdinalIgnoreCase);
            if (index < 0)
            {
                continue;
            }

            // 只认我方目录本身或其中的文件，不把旧单文件 airesume-notify.ts 的失败记到新插件头上。
            char next = index + needle.Length < line.Length ? line[index + needle.Length] : '"';
            if (next is not ('"' or '\\'))
            {
                continue;
            }

            Match stamp = Regex.Match(line, @"^timestamp=(\S+)");
            if (!stamp.Success || !DateTimeOffset.TryParse(stamp.Groups[1].Value, CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out DateTimeOffset at) || at < after)
            {
                continue;
            }

            // 以最后一条结论为准:一次加载先记 loading 再记 failed(仍判失败);之后哪次重启成功加载,
            // 新的 loading 覆盖旧的 failed——否则一次偶发失败会让界面一直报断链、install 一直返回 2。
            if (line.Contains("failed to load plugin", StringComparison.Ordinal))
            {
                Match cause = Regex.Match(line, @"LoadError: ([^.(""]{1,120})");
                latest = ("failed", at, cause.Success ? cause.Groups[1].Value.Trim() : null);
            }
            else if (line.Contains("loading plugin", StringComparison.Ordinal))
            {
                latest = ("loaded", at, null);
            }
        }

        return latest;
    }

    private static void AtomicWrite(string target, string temp, string content)
    {
        try
        {
            using (var fs = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            using (var writer = new StreamWriter(fs, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)))
            {
                writer.Write(content);
                writer.Flush();
                fs.Flush(flushToDisk: true);
            }

            File.Move(temp, target, overwrite: true);
        }
        catch
        {
            // 清理临时文件,不留半成品
            try
            {
                if (File.Exists(temp))
                {
                    File.Delete(temp);
                }
            }
            catch
            {
                // 忽略清理失败
            }

            throw;
        }
    }

    /// <summary>
    /// 获取默认插件目录路径。
    /// </summary>
    private static string GetDefaultPluginsDirectory() =>
        Path.Combine(Probes.OpenCodeCredentials.ConfigDirectory(Environment.GetEnvironmentVariable,
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)), "plugins");

    /// <summary>
    /// 从插件源码里读回我方那条命令的原文;读不出返回 null。
    ///
    /// 插件是本适配器自己写的(<c>const cmd = "…";</c>),按同一份契约反解即可。
    /// 取它是为了核对"这个程序还在不在" —— 插件把执行异常整个吞掉,
    /// 文件没了 OpenCode 侧不会有任何可见迹象。
    /// </summary>
    public static string? FindHookCommand(string pluginPath)
    {
        try
        {
            return ParseHookCommand(File.ReadAllText(pluginPath, Encoding.UTF8));
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>反解插件源码里的命令常量。与 <see cref="QuoteForTs"/> 互为逆操作。</summary>
    public static string? ParseHookCommand(string source)
    {
        var m = Regex.Match(source, @"const\s+cmd\s*=\s*""(?<cmd>(?:[^""\\]|\\.)*)""\s*;");
        if (!m.Success)
        {
            return null;
        }

        // QuoteForTs 转义了 \ " \r \n。必须单遍从左到右还原:逐个 Replace 会把 C:\\nina 里
        // 转义后的第二个反斜杠连同后面的 n 当成换行,路径段以 n/r 开头时钩子被误判断链。
        return Regex.Replace(m.Groups["cmd"].Value, @"\\(.)", escape => escape.Groups[1].Value switch
        {
            "r" => "\r",
            "n" => "\n",
            var literal => literal,
        });
    }

    /// <summary>v2 生成物：首行标记 + 我方插件 id + 默认导出 + v2 事件订阅 + 命令常量，缺一不可。</summary>
    public static bool IsManagedV2Source(string source)
    {
        using var reader = new StringReader(source);
        return string.Equals(reader.ReadLine(), ManagedMarker, StringComparison.Ordinal) &&
               source.Contains("id: \"" + PluginId + "\"", StringComparison.Ordinal) &&
               source.Contains("export default {", StringComparison.Ordinal) &&
               source.Contains("ctx.event.subscribe(", StringComparison.Ordinal) &&
               source.Contains("const cmd = \"", StringComparison.Ordinal);
    }

    /// <summary>OpenCode 1.x 时代我方生成的单文件(两代),只用于迁移与退役。</summary>
    public static bool IsManagedLegacySource(string source)
    {
        using var reader = new StringReader(source);
        string? firstLine = reader.ReadLine();
        bool current = string.Equals(firstLine, ManagedMarker, StringComparison.Ordinal);
        bool legacy = string.Equals(firstLine, LegacyManagedMarker, StringComparison.Ordinal);
        if (!current && !legacy)
        {
            return false;
        }

        // 文件名和首行仍不够:再核对我方生成器的稳定结构,避免用户文档或字符串常量碰撞。
        bool sharedShape = source.Contains("export const AiResumeNotify", StringComparison.Ordinal) &&
                           source.Contains("event.type !== \"session.idle\"", StringComparison.Ordinal) &&
                           source.Contains("const cmd = ", StringComparison.Ordinal);
        if (!sharedShape)
        {
            return false;
        }

        return current
            ? source.Contains("hook_event_name: \"session.idle\"", StringComparison.Ordinal) &&
              source.Contains("new TextEncoder().encode(payload)", StringComparison.Ordinal) &&
              // 认出**我方任意一代**的投递形状。所有权判据绝不能绑在最新特性上
              // (比如 permission.asked):那样每加一个功能,上一代文件就变成"别人的",
              // 就地升级的路径随之断掉,用户会被备份+覆盖而不是平滑刷新。
              (source.Contains("Bun.spawn([cmd, ...args]", StringComparison.Ordinal) ||
               source.Contains("Bun.spawn([cmd, \"opencode\"]", StringComparison.Ordinal))
            : source.Contains("export const AiResumeNotify = async ({ project, directory })", StringComparison.Ordinal) &&
              source.Contains("await Bun.$`${cmd} ${targetDir}`.quiet();", StringComparison.Ordinal);
    }

    /// <summary>
    /// 将 C# 字符串转义为 TypeScript 字符串字面量。
    /// </summary>
    private static string QuoteForTs(string value)
    {
        // 转义反斜杠、双引号和换行符
        var escaped = value
            .Replace("\\", "\\\\")
            .Replace("\"", "\\\"")
            .Replace("\r", "\\r")
            .Replace("\n", "\\n");
        return "\"" + escaped + "\"";
    }
}
