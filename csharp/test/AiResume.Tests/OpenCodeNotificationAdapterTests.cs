using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using AiResume.Worker.Notifications;
using Xunit;

namespace AiResume.Tests;

/// <summary>
/// OpenCodeNotificationAdapter(OpenCode ≥ 2.0)的单元测试。
/// 所有测试均在 TestTemp 唯一子目录下进行，绝不触碰真实 ~/.config/opencode 与 OpenCode 日志。
/// 日志夹具照抄 OpenCode 2.2.3 真实日志行的形状(用户名已换成合成值)。
/// </summary>
public class OpenCodeNotificationAdapterTests : IDisposable
{
    private readonly string _tempRoot;
    private readonly string _configDirectory;
    private readonly string _pluginsDirectory;
    private readonly string _logPath;
    private readonly OpenCodeNotificationAdapter _adapter;

    public OpenCodeNotificationAdapterTests()
    {
        _tempRoot = TestTemp.NewDir("AiResumeTests");
        _configDirectory = Path.Combine(_tempRoot, ".config", "opencode");
        _pluginsDirectory = Path.Combine(_configDirectory, "plugins");
        _logPath = Path.Combine(_tempRoot, "opencode.log");
        _adapter = new OpenCodeNotificationAdapter(_pluginsDirectory, _logPath);
    }

    private string EntryPath => Path.Combine(_pluginsDirectory, OpenCodeNotificationAdapter.PluginDirectoryName, OpenCodeNotificationAdapter.PluginEntryFileName);

    private string LegacyPath => Path.Combine(_pluginsDirectory, OpenCodeNotificationAdapter.LegacyPluginFileName);

    public void Dispose()
    {
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

    [Fact]
    public void Probe_WhenParentDirectoryMissing_ReturnsNotInstalled()
    {
        Assert.False(Directory.Exists(_configDirectory));

        var status = _adapter.Probe();

        Assert.False(status.IsInstalled);
        Assert.False(status.IsEnabled);
        Assert.Null(status.ConfigPath);
        Assert.Contains("不存在", status.Detail);
    }

    [Fact]
    public void Probe_WhenParentExistsButPluginMissing_ReturnsInstalledNotEnabled()
    {
        Directory.CreateDirectory(_configDirectory);

        var status = _adapter.Probe();

        Assert.True(status.IsInstalled);
        Assert.False(status.IsEnabled);
        Assert.Equal(_configDirectory, status.ConfigPath);
        Assert.Contains("未安装", status.Detail);
    }

    [Fact]
    public void Enable写入官方v2目录形态插件_没有加载证据前只说等待核实()
    {
        Directory.CreateDirectory(_configDirectory);

        _adapter.Enable(@"C:\Program Files\AI Resume\AiResume.Hook.exe");

        string content = File.ReadAllText(EntryPath, Encoding.UTF8);
        Assert.StartsWith(OpenCodeNotificationAdapter.ManagedMarker, content, StringComparison.Ordinal);
        Assert.Contains("export default {", content, StringComparison.Ordinal);
        Assert.Contains("id: \"ai-resume.notify\"", content, StringComparison.Ordinal);
        Assert.Contains("ctx.event.subscribe(", content, StringComparison.Ordinal);
        Assert.Contains("import { spawn } from \"node:child_process\"", content, StringComparison.Ordinal);
        Assert.Contains("shell: false", content, StringComparison.Ordinal);
        Assert.Contains("windowsHide: true", content, StringComparison.Ordinal);
        Assert.Contains("\"session.execution.succeeded\"", content, StringComparison.Ordinal);
        Assert.Contains("\"form.created\"", content, StringComparison.Ordinal);
        Assert.DoesNotContain("Bun.spawn", content, StringComparison.Ordinal);
        Assert.DoesNotContain("Bun.$", content, StringComparison.Ordinal);
        Assert.DoesNotContain("event.properties", content, StringComparison.Ordinal);
        Assert.Equal(@"C:\Program Files\AI Resume\AiResume.Hook.exe", OpenCodeNotificationAdapter.ParseHookCommand(content));
        Assert.False(File.Exists(LegacyPath));

        var status = _adapter.Probe();
        Assert.True(status.IsEnabled);
        Assert.False(status.HookBroken);
        Assert.Contains("核实", status.Detail);
        Assert.Equal(@"C:\Program Files\AI Resume\AiResume.Hook.exe", status.HookCommand);
    }

    [Theory]
    [InlineData("C:\\Users\\测试 用户\\AI \"Resume\"\\AiResume.Hook.exe")]
    // 路径段以 n / r 开头:转义后是「\\n…」「\\r…」,逐个 Replace 的反解会把它们当成换行/回车。
    [InlineData("C:\\Users\\nina\\AppData\\Local\\AI Resume\\AiResume.Hook.exe")]
    [InlineData("D:\\repo\\csharp\\src\\AiResume.Hook\\bin\\Release\\net10.0-windows\\AiResume.Hook.exe")]
    public void 命令常量往返保留空格引号中文与n_r开头的路径段(string command)
    {
        string source = OpenCodeNotificationAdapter.BuildPluginSource(command);

        Assert.Equal(command, OpenCodeNotificationAdapter.ParseHookCommand(source));
        Assert.True(OpenCodeNotificationAdapter.IsManagedV2Source(source));
    }

    [Fact]
    public void 重复Enable内容一致时不重写也不留临时文件()
    {
        Directory.CreateDirectory(_configDirectory);
        _adapter.Enable("hook.exe");
        DateTime first = File.GetLastWriteTimeUtc(EntryPath);
        File.SetLastWriteTimeUtc(EntryPath, first.AddMinutes(-5));

        _adapter.Enable("hook.exe");

        Assert.Equal(first.AddMinutes(-5), File.GetLastWriteTimeUtc(EntryPath));
        Assert.Equal(new[] { OpenCodeNotificationAdapter.PluginEntryFileName },
            Directory.GetFileSystemEntries(Path.GetDirectoryName(EntryPath)!).Select(Path.GetFileName));
    }

    [Fact]
    public void 我方旧版单文件在启用时迁移到目录并退役_保留备份()
    {
        Directory.CreateDirectory(_pluginsDirectory);
        string legacy = PreviousManagedSpawnPluginSource("old-hook.exe");
        File.WriteAllText(LegacyPath, legacy, Encoding.UTF8);

        NotificationProviderStatus before = _adapter.Probe();
        Assert.True(before.IsEnabled);
        Assert.True(before.HookBroken);
        Assert.Contains("旧版插件", before.Detail);

        _adapter.Enable("new-hook.exe");

        Assert.False(File.Exists(LegacyPath));
        Assert.Equal(legacy, File.ReadAllText(LegacyPath + ".bak", Encoding.UTF8));
        Assert.Equal("new-hook.exe", OpenCodeNotificationAdapter.ParseHookCommand(File.ReadAllText(EntryPath, Encoding.UTF8)));
        Assert.False(_adapter.Probe().HookBroken);
    }

    [Fact]
    public void 最早一代旧插件同样能识别并迁移()
    {
        Directory.CreateDirectory(_pluginsDirectory);
        File.WriteAllText(LegacyPath, LegacyPluginSource("old-hook.exe"), Encoding.UTF8);

        _adapter.Enable("new-hook.exe");

        Assert.False(File.Exists(LegacyPath));
        Assert.True(File.Exists(EntryPath));
    }

    [Fact]
    public void 用户同名单文件与用户其它插件都不碰()
    {
        Directory.CreateDirectory(_pluginsDirectory);
        const string userPlugin = "// user plugin with the same filename";
        File.WriteAllText(LegacyPath, userPlugin, Encoding.UTF8);
        string other = Path.Combine(_pluginsDirectory, "other-plugin.ts");
        File.WriteAllText(other, "// 其他插件内容", Encoding.UTF8);

        _adapter.Enable("hook.exe");
        _adapter.Disable();

        Assert.Equal(userPlugin, File.ReadAllText(LegacyPath, Encoding.UTF8));
        Assert.Equal("// 其他插件内容", File.ReadAllText(other, Encoding.UTF8));
        Assert.False(File.Exists(EntryPath));
    }

    [Fact]
    public void 我方目录里有用户的入口文件时拒绝覆盖()
    {
        string directory = Path.GetDirectoryName(EntryPath)!;
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "index.ts"), "// user plugin", Encoding.UTF8);

        InvalidOperationException error = Assert.Throws<InvalidOperationException>(() => _adapter.Enable("hook.exe"));

        Assert.Contains("拒绝覆盖", error.Message);
        Assert.Equal("// user plugin", File.ReadAllText(EntryPath, Encoding.UTF8));
        Assert.False(_adapter.Probe().IsEnabled);
    }

    [Fact]
    public void 首行之外提到标记不能冒认用户插件()
    {
        Directory.CreateDirectory(_pluginsDirectory);
        string userPlugin = "// user plugin\nconst note = \"" +
                            OpenCodeNotificationAdapter.ManagedMarker.Replace("\"", "\\\"") +
                            "\";\nexport default { id: \"ai-resume.notify\", setup(ctx) { ctx.event.subscribe({}) } }\nconst cmd = \"x\";\n";
        File.WriteAllText(LegacyPath, userPlugin, Encoding.UTF8);

        _adapter.Disable();

        Assert.Equal(userPlugin, File.ReadAllText(LegacyPath, Encoding.UTF8));
        Assert.False(OpenCodeNotificationAdapter.IsManagedV2Source(userPlugin));
    }

    [Fact]
    public void Disable删除我方插件与空目录_未启用时幂等()
    {
        Directory.CreateDirectory(_configDirectory);
        _adapter.Enable("hook.exe");

        _adapter.Disable();
        _adapter.Disable();

        Assert.False(File.Exists(EntryPath));
        Assert.False(Directory.Exists(Path.GetDirectoryName(EntryPath)));
        Assert.False(_adapter.Probe().IsEnabled);
    }

    [Fact]
    public void OpenCode日志在写入之后报加载失败时标为断链()
    {
        Directory.CreateDirectory(_configDirectory);
        _adapter.Enable("hook.exe");
        DateTime written = File.GetLastWriteTimeUtc(EntryPath);
        string dir = EscapedPluginDirectory();
        File.WriteAllLines(_logPath, new[]
        {
            $"timestamp={Stamp(written.AddMinutes(-10))} level=WARN run=r1 message=\"failed to load plugin\" target=\"{dir}\" ref=err_old cause=\"Cause([Fail(PluginModule.LoadError: stale failure before rewrite. )])\" role=server",
            $"timestamp={Stamp(written.AddMinutes(1))} level=INFO run=r1 msg=\"loading plugin\" id=\"{dir}\" entrypoint=file:///C:/x role=server",
            $"timestamp={Stamp(written.AddMinutes(1))} level=WARN run=r1 message=\"failed to load plugin\" target=\"{dir}\" ref=err_1 cause=\"Cause([Fail(PluginModule.LoadError: Plugin must export a default definition with an id and an effect or setup function. (cause: x)])\" role=server",
        });

        NotificationProviderStatus status = _adapter.Probe();

        Assert.True(status.IsEnabled);
        Assert.True(status.HookBroken);
        Assert.Contains("拒绝加载", status.Detail);
        Assert.Contains("Plugin must export a default definition with an id and an effect or setup function", status.Detail);
    }

    [Fact]
    public void 先加载失败后来重启成功加载_以最后一条结论为准()
    {
        Directory.CreateDirectory(_configDirectory);
        _adapter.Enable("hook.exe");
        DateTime written = File.GetLastWriteTimeUtc(EntryPath);
        string dir = EscapedPluginDirectory();
        File.WriteAllLines(_logPath, new[]
        {
            $"timestamp={Stamp(written.AddMinutes(1))} level=INFO run=r1 msg=\"loading plugin\" id=\"{dir}\" entrypoint=file:///C:/x role=server",
            $"timestamp={Stamp(written.AddMinutes(1))} level=WARN run=r1 message=\"failed to load plugin\" target=\"{dir}\" ref=err_1 cause=\"Cause([Fail(PluginModule.LoadError: file busy. )])\" role=server",
            $"timestamp={Stamp(written.AddMinutes(5))} level=INFO run=r2 msg=\"loading plugin\" id=\"{dir}\" entrypoint=file:///C:/x role=server",
        });

        NotificationProviderStatus status = _adapter.Probe();

        Assert.False(status.HookBroken);
        Assert.Contains("已加载", status.Detail);
    }

    [Fact]
    public void OpenCode日志在写入之后加载成功时说已加载_旧单文件的失败不算到新插件头上()
    {
        Directory.CreateDirectory(_configDirectory);
        _adapter.Enable("hook.exe");
        DateTime written = File.GetLastWriteTimeUtc(EntryPath);
        string dir = EscapedPluginDirectory();
        File.WriteAllLines(_logPath, new[]
        {
            $"timestamp={Stamp(written.AddMinutes(1))} level=WARN run=r1 message=\"failed to load plugin\" target=\"{dir}.ts\" ref=err_legacy cause=\"Cause([Fail(PluginModule.LoadError: legacy. )])\" role=server",
            $"timestamp={Stamp(written.AddMinutes(2))} level=INFO run=r1 msg=\"loading plugin\" id=\"{dir}\" entrypoint=file:///C:/x role=server",
        });

        NotificationProviderStatus status = _adapter.Probe();

        Assert.False(status.HookBroken);
        Assert.Contains("已加载", status.Detail);
    }

    [Fact]
    public void 生成的插件通过OpenCode2事件形状的21个离线场景()
    {
        // 宽限常量在测试副本里缩到毫秒级;其余源码与生成物逐字一致。
        string source = OpenCodeNotificationAdapter.BuildPluginSource("hook.exe")
            .Replace("const COMPLETION_GRACE_MS = 5000", "const COMPLETION_GRACE_MS = 40", StringComparison.Ordinal)
            .Replace("const FAILURE_GRACE_MS = 60000", "const FAILURE_GRACE_MS = 80", StringComparison.Ordinal);
        Assert.Contains("const COMPLETION_GRACE_MS = 40", source, StringComparison.Ordinal);
        string pluginPath = Path.Combine(_tempRoot, "plugin.mjs");
        File.WriteAllText(pluginPath, source, new UTF8Encoding(false));
        string harnessPath = Path.Combine(_tempRoot, "harness.mjs");
        File.WriteAllText(harnessPath, Harness, new UTF8Encoding(false));

        var psi = new ProcessStartInfo("node")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
        };
        psi.ArgumentList.Add(harnessPath);
        psi.ArgumentList.Add(pluginPath);
        using Process process = Process.Start(psi)!;
        string stdout = process.StandardOutput.ReadToEnd();
        string stderr = process.StandardError.ReadToEnd();
        Assert.True(process.WaitForExit(30_000), "Node 插件场景脚本超时");

        Assert.True(process.ExitCode == 0, stdout + stderr);
        Assert.Contains("\"passed\": 21", stdout, StringComparison.Ordinal);
    }

    private string EscapedPluginDirectory() =>
        Path.Combine(_pluginsDirectory, OpenCodeNotificationAdapter.PluginDirectoryName).Replace("\\", "\\\\", StringComparison.Ordinal);

    private static string Stamp(DateTime utc) => utc.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", System.Globalization.CultureInfo.InvariantCulture);

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

    /// <summary>
    /// 离线场景脚本：替换 child_process.spawn,按 @opencode/client v2.2.3 生成类型的形状喂事件。
    /// 覆盖完成只报顶层一次、同事件不重复、子会话不报、有运行中子会话时抑制、宽限期内重跑抑制、
    /// 授权与提问按请求 id 去重、global 表单忽略、失败宽限、查询失败 fail-closed、spawn 出错不中断、
    /// 事件流异常重订阅、卸载后不再投递。
    /// </summary>
    private const string Harness = """
        import { createRequire, syncBuiltinESMExports } from "node:module"
        import { EventEmitter } from "node:events"
        import { pathToFileURL } from "node:url"

        const require = createRequire(import.meta.url)
        const cp = require("node:child_process")
        const spawns = []
        let spawnMode = "ok"
        cp.spawn = (file, args, options) => {
          const child = new EventEmitter()
          child.kill = () => {}
          child.unref = () => {}
          let body = ""
          child.stdin = new EventEmitter()
          child.stdin.end = (chunk) => {
            body += chunk ?? ""
            spawns.push({ file, args, options, payload: JSON.parse(body) })
            if (spawnMode === "enoent") queueMicrotask(() => child.emit("error", Object.assign(new Error("spawn ENOENT"), { code: "ENOENT" })))
            else queueMicrotask(() => child.emit("exit", 0))
          }
          if (spawnMode === "throw") throw new Error("boom")
          return child
        }
        syncBuiltinESMExports()

        const plugin = (await import(pathToFileURL(process.argv[2]).href)).default
        const results = []
        const check = (name, cond, extra) => {
          results.push({ name, ok: !!cond, ...(cond ? {} : { extra }) })
        }
        const tick = (ms = 150) => new Promise((r) => setTimeout(r, ms))

        let seq = 0
        const ev = (type, data, extra = {}) => ({ id: `evt_${String(++seq).padStart(4, "0")}`, created: 1793380000000 + seq, type, data, ...extra })
        const durable = (type, sessionID, data = {}) =>
          ev(type, { sessionID, ...data }, { durable: { aggregateID: sessionID, seq, version: 1 } })

        function makeSource() {
          let queue = []
          let wake
          let failNext = false
          let subscribeCount = 0
          const subscribe = ({ types, signal } = {}) => {
            subscribeCount++
            return {
              async *[Symbol.asyncIterator]() {
                while (!signal?.aborted) {
                  if (failNext) {
                    failNext = false
                    throw new Error("stream overflow")
                  }
                  if (queue.length) {
                    const next = queue.shift()
                    if (!types || types.includes(next.type)) yield next
                    continue
                  }
                  await new Promise((r) => {
                    wake = r
                    signal?.addEventListener("abort", r, { once: true })
                  })
                }
              },
            }
          }
          return {
            subscribe,
            push: (...events) => {
              queue.push(...events)
              wake?.()
            },
            fail: () => {
              failNext = true
              wake?.()
            },
            get subscribeCount() {
              return subscribeCount
            },
          }
        }

        const sessions = new Map()
        const source = makeSource()
        const ctx = {
          location: { directory: "C:\\plugin-location" },
          event: { subscribe: source.subscribe },
          session: {
            get: async ({ sessionID }) => {
              if (!sessions.has(sessionID)) throw new Error("Session not found: " + sessionID)
              return sessions.get(sessionID)
            },
          },
        }
        const info = (id, directory, parentID) => ({ id, ...(parentID ? { parentID } : {}), location: { directory }, time: { created: 1, updated: 1 } })

        const started = Date.now()
        const cleanup = plugin.setup(ctx)
        check("setup returns synchronously with a cleanup function", typeof cleanup === "function" && Date.now() - started < 50)
        check("plugin id", plugin.id === "ai-resume.notify")

        sessions.set("ses_root1", info("ses_root1", "C:\\work\\项目A"))
        const e1 = durable("session.execution.succeeded", "ses_root1")
        source.push(durable("session.execution.started", "ses_root1"), e1)
        await tick()
        check("root succeeded -> one completion", spawns.length === 1 && spawns[0].args.join(" ") === "opencode", spawns)
        check("completion payload uses event id + session directory", spawns[0]?.payload.event_id === e1.id && spawns[0]?.payload.cwd === "C:\\work\\项目A" && spawns[0]?.payload.hook_event_name === "session.execution.succeeded", spawns[0])
        check("spawn uses argv/no shell/windowsHide/stdin pipe", spawns[0]?.options.shell === false && spawns[0]?.options.windowsHide === true && spawns[0]?.options.stdio[0] === "pipe")
        check("timestamp derived from event.created", spawns[0]?.payload.timestamp === new Date(e1.created).toISOString())

        source.push(e1)
        await tick()
        check("same event id is not delivered twice", spawns.length === 1)

        spawns.length = 0
        source.push(
          durable("session.created", "ses_child1", { parentID: "ses_root1", location: { directory: "C:\\work\\项目A" }, projectID: "p", slug: "c", version: "2.2.3" }),
          durable("session.execution.started", "ses_child1"),
          durable("session.execution.succeeded", "ses_child1"),
        )
        await tick()
        check("child succeeded -> no completion", spawns.length === 0, spawns)

        spawns.length = 0
        source.push(
          durable("session.execution.started", "ses_root1"),
          durable("session.created", "ses_child2", { parentID: "ses_root1", location: { directory: "C:\\work\\项目A" }, projectID: "p", slug: "c2", version: "2.2.3" }),
          durable("session.execution.started", "ses_child2"),
          durable("session.execution.succeeded", "ses_root1"),
        )
        await tick()
        check("root succeeded with running direct child -> suppressed", spawns.length === 0, spawns)
        const finalRoot = durable("session.execution.succeeded", "ses_root1")
        source.push(
          durable("session.execution.succeeded", "ses_child2"),
          durable("session.execution.started", "ses_root1"),
          finalRoot,
        )
        await tick()
        check("root re-run after child report -> one completion", spawns.length === 1 && spawns[0].payload.event_id === finalRoot.id, spawns)

        spawns.length = 0
        const ask = ev("permission.asked", { id: "per_abc", sessionID: "ses_child1", action: "bash", resources: ["rm -rf build"] }, { location: { directory: "C:\\work\\项目A" } })
        source.push(ask, { ...ask, id: "evt_dup_same_request" })
        await tick()
        check("permission in child -> one decision", spawns.length === 1 && spawns[0].args.join(" ") === "opencode --kind=decision" && spawns[0].payload.notification_type === "permission_prompt" && spawns[0].payload.event_id === "permission:per_abc", spawns)

        spawns.length = 0
        source.push(
          ev("form.created", { form: { id: "frm_q1", sessionID: "ses_root1", title: "Questions", metadata: { kind: "question" }, fields: [{ key: "q0", type: "string" }] } }),
          ev("form.created", { form: { id: "frm_m1", sessionID: "global", title: "srv is requesting input", metadata: { kind: "mcp-elicitation" }, fields: [{ key: "x", type: "string" }] } }),
        )
        await tick()
        check("question form -> one decision (agent_needs_input)", spawns.length === 1 && spawns[0].payload.notification_type === "agent_needs_input" && spawns[0].payload.hook_event_name === "form.created", spawns)

        spawns.length = 0
        source.push(
          durable("session.execution.started", "ses_child1"),
          durable("session.execution.failed", "ses_child1", { error: { type: "provider.api", message: "x" } }),
          durable("session.execution.started", "ses_root1"),
          durable("session.execution.failed", "ses_root1", { error: { type: "provider.api", message: "quota" } }),
        )
        await tick()
        check("root failed -> one decision execution_failed; child failed ignored", spawns.length === 1 && spawns[0].payload.notification_type === "execution_failed", spawns)

        spawns.length = 0
        source.push(durable("session.execution.started", "ses_root1"), durable("session.execution.interrupted", "ses_root1", { reason: "user" }))
        await tick()
        check("interrupted -> no notification", spawns.length === 0, spawns)

        spawns.length = 0
        source.push(durable("session.execution.started", "ses_root1"), durable("session.execution.succeeded", "ses_root1"))
        await tick(10)
        source.push(durable("session.execution.started", "ses_root1"))
        await tick()
        check("re-run within completion grace -> suppressed", spawns.length === 0, spawns)
        const realEnd = durable("session.execution.succeeded", "ses_root1")
        source.push(realEnd)
        await tick()
        check("real end after re-run -> one completion", spawns.length === 1 && spawns[0].payload.event_id === realEnd.id, spawns)

        spawns.length = 0
        source.push(durable("session.execution.started", "ses_root1"), durable("session.execution.failed", "ses_root1", { error: { type: "provider.api", message: "stream" } }))
        await tick(20)
        source.push(durable("session.execution.started", "ses_root1"))
        await tick()
        check("failure auto-resumed within grace -> suppressed", spawns.length === 0, spawns)
        source.push(durable("session.execution.succeeded", "ses_root1"))
        await tick()
        spawns.length = 0

        spawns.length = 0
        source.push(durable("session.execution.succeeded", "ses_unknown"))
        await tick()
        check("lookup failure -> fail-closed", spawns.length === 0, spawns)

        spawnMode = "enoent"
        sessions.set("ses_root2", info("ses_root2", "C:\\work\\项目B"))
        source.push(durable("session.execution.succeeded", "ses_root2"))
        await tick()
        spawnMode = "throw"
        sessions.set("ses_root3", info("ses_root3", "C:\\work\\项目C"))
        source.push(durable("session.execution.succeeded", "ses_root3"))
        await tick()
        spawnMode = "ok"
        spawns.length = 0
        sessions.set("ses_root4", info("ses_root4", "C:\\work\\项目D"))
        source.push(durable("session.execution.succeeded", "ses_root4"))
        await tick()
        check("loop survives spawn errors", spawns.length === 1 && spawns[0].payload.cwd === "C:\\work\\项目D", spawns)

        spawns.length = 0
        const before = source.subscribeCount
        source.fail()
        await tick(1300)
        sessions.set("ses_root5", info("ses_root5", "C:\\work\\项目E"))
        source.push(durable("session.execution.succeeded", "ses_root5"))
        await tick()
        check("stream failure -> resubscribed", source.subscribeCount === before + 1 && spawns.length === 1, { before, after: source.subscribeCount, spawns })

        cleanup()
        await tick()
        spawns.length = 0
        sessions.set("ses_root6", info("ses_root6", "C:\\work\\项目F"))
        source.push(durable("session.execution.succeeded", "ses_root6"))
        await tick(100)
        check("after cleanup -> no delivery", spawns.length === 0, spawns)

        const failed = results.filter((r) => !r.ok)
        console.log(JSON.stringify({ passed: results.length - failed.length, total: results.length, failed }, null, 1))
        process.exit(failed.length ? 1 : 0)
        """;
}
