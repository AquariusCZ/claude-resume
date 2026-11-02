using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading;
using AiResume.Worker.Probes;
using Tomlyn;
using Tomlyn.Model;

namespace AiResume.Worker.Notifications;

/// <summary>
/// Codex 完成通知适配器(hooks 版)。
///
/// **为什么不再用 <c>notify</c>**:<c>notify</c> 是 config.toml 里唯一的一条命令数组,
/// Codex Desktop 的 computer-use 插件会整条改写它——2026-10 本机实测 AI Resume 的
/// <c>--previous-notify</c> 链被它冲掉,通知静默消失。Codex 0.148.0 起的 hooks 是**可叠加**的:
/// <c>hooks.json</c> 里每个事件是一组 matcher group,我方只增删自己那一组,别人改 notify 与我们无关。
///
/// 上游契约(固定 0.148.0 源码 <c>codex-rs/hooks</c>、<c>codex-rs/config</c>):
/// <list type="bullet">
/// <item><c>$CODEX_HOME/hooks.json</c> → <c>{"hooks":{"Stop":[{"hooks":[{"type":"command","command":…,"timeout":…}]}]}}</c>;
///       Stop 忽略 matcher,stdin 收到 <c>session_id/cwd/transcript_path/hook_event_name/turn_id/stop_hook_active</c> JSON。</item>
/// <item>非 managed 的 hook 必须被信任才会执行:<c>config.toml</c> 的
///       <c>[hooks.state."&lt;hooks.json 全路径&gt;:stop:&lt;组序号&gt;:&lt;条序号&gt;"] trusted_hash = "sha256:…"</c>
///       必须等于当前哈希(<c>discovery.rs hook_hash</c> + <c>fingerprint.rs version_for_toml</c>:
///       对 <c>{"event_name":"stop","hooks":[{normalized handler}]}</c> 的键排序紧凑 JSON 取 SHA-256)。
///       本机用户已有的 codex-chatgpt-web interrupt 条目被用作该算法的 oracle,2026-10-12 复算一致。</item>
/// <item>信任键按组/条**序号**定位,所以我方永远追加在 Stop 数组末尾;停用时只摘自己那一组。</item>
/// </list>
/// 任何一步算不出或写不进,都如实报「未信任」而不是「已启用」。
/// </summary>
public sealed class CodexHooksNotificationAdapter : INotificationAdapter
{
    /// <summary>所有权标记文件名。</summary>
    public const string MarkerFileName = "AiResume.Hook.exe";

    public const string HooksFileName = "hooks.json";
    private const string EventName = "Stop";
    private const string EventKeyLabel = "stop";
    private const string Source = "codex";
    private const int TimeoutSeconds = 30;
    private const int WriteLockTimeoutSeconds = 10;

    private readonly string _codexHome;
    private readonly string _hooksPath;
    private readonly string _trustKeySource;
    private readonly string _configPath;
    private readonly string _writeLockName;
    private string? _hooksTextSnapshot;

    /// <summary>使用默认 Codex home(%USERPROFILE%\.codex 或 CODEX_HOME)。</summary>
    public CodexHooksNotificationAdapter()
        : this(CodexAuthProbe.ResolveCodexHome())
    {
    }

    /// <summary>使用指定 Codex home(测试用)。</summary>
    public CodexHooksNotificationAdapter(string codexHome)
    {
        _codexHome = Path.GetFullPath(codexHome);
        _hooksPath = Path.Combine(_codexHome, HooksFileName);
        _trustKeySource = TrustKeySource(_codexHome);
        _configPath = Path.Combine(_codexHome, "config.toml");
        string pathHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(_codexHome.ToUpperInvariant())));
        _writeLockName = @"Local\AIResume.CodexHooks." + pathHash;
    }

    public NotificationProviderKind Kind => NotificationProviderKind.Codex;

    public string DisplayName => "Codex";

    /// <summary>hooks.json 的全路径;信任键以它为前缀。</summary>
    public string HooksPath => _hooksPath;

    /// <summary>Codex 用于信任的哈希:对规范化后的 handler 身份取 SHA-256(与上游 version_for_toml 一致)。</summary>
    public static string ComputeTrustHash(string command, int timeoutSeconds = TimeoutSeconds)
    {
        // 键必须按序排好:上游先序列化成 TOML 值,再转 JSON 并递归按键排序后紧凑输出。
        // Stop 没有 matcher(被忽略,不进身份);Option::None 的字段在 TOML 里根本不存在。
        var handler = new SortedDictionary<string, object?>(StringComparer.Ordinal)
        {
            ["async"] = false,
            ["command"] = command,
            ["timeout"] = timeoutSeconds,
            ["type"] = "command",
        };
        var identity = new SortedDictionary<string, object?>(StringComparer.Ordinal)
        {
            ["event_name"] = EventKeyLabel,
            ["hooks"] = new object[] { handler },
        };
        string json = JsonSerializer.Serialize(identity, new JsonSerializerOptions
        {
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        });
        return "sha256:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json))).ToLowerInvariant();
    }

    /// <summary>config.toml 里 hooks.state 的键:<c>&lt;hooks.json 全路径&gt;:stop:&lt;组&gt;:&lt;条&gt;</c>。</summary>
    public string StateKey(int groupIndex, int handlerIndex) =>
        $"{_trustKeySource}:{EventKeyLabel}:{groupIndex}:{handlerIndex}";

    /// <summary>
    /// 信任键里 hooks.json 的路径字面。上游(0.148.0 <c>utils/home-dir</c>)只对 <c>CODEX_HOME</c> 环境变量指定的目录做
    /// <c>canonicalize()</c>(解析链接、还原磁盘真实大小写,再去掉 <c>\\?\</c> 前缀);默认的 <c>%USERPROFILE%\.codex</c> 原样使用。
    /// 这里按同样规则生成,否则 <c>CODEX_HOME=c:\users\me\.codex</c> 这类写法会让我方自算的键与 Codex 读到的键不同,
    /// 界面绿灯而 Codex 静默不执行。拿不到最终路径时退回 GetFullPath。
    /// </summary>
    private static string TrustKeySource(string codexHome)
    {
        string dir = codexHome;
        if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("CODEX_HOME")))
        {
            dir = TryCanonicalDirectory(codexHome) ?? codexHome;
        }

        return Path.Combine(dir, HooksFileName);
    }

    private static string? TryCanonicalDirectory(string directory)
    {
        try
        {
            using Microsoft.Win32.SafeHandles.SafeFileHandle handle = CreateFileW(
                directory, 0, FileShareAll, IntPtr.Zero, OpenExisting, FileFlagBackupSemantics, IntPtr.Zero);
            if (handle.IsInvalid)
            {
                return null;
            }

            var buffer = new StringBuilder(1024);
            uint length = GetFinalPathNameByHandleW(handle, buffer, (uint)buffer.Capacity, 0);
            if (length == 0 || length >= buffer.Capacity)
            {
                return null;
            }

            string final = buffer.ToString(0, (int)length);
            if (final.StartsWith(@"\\?\UNC\", StringComparison.Ordinal))
            {
                return @"\\" + final[8..];
            }

            return final.StartsWith(@"\\?\", StringComparison.Ordinal) ? final[4..] : final;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private const uint FileShareAll = 0x7;
    private const uint OpenExisting = 3;
    private const uint FileFlagBackupSemantics = 0x02000000;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern Microsoft.Win32.SafeHandles.SafeFileHandle CreateFileW(
        string lpFileName, uint dwDesiredAccess, uint dwShareMode, IntPtr lpSecurityAttributes,
        uint dwCreationDisposition, uint dwFlagsAndAttributes, IntPtr hTemplateFile);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandleW(
        Microsoft.Win32.SafeHandles.SafeFileHandle hFile, StringBuilder lpszFilePath, uint cchFilePath, uint dwFlags);

    public NotificationProviderStatus Probe()
    {
        bool installed = false;
        try
        {
            if (!Directory.Exists(_codexHome))
            {
                return new NotificationProviderStatus(Kind, DisplayName, false, false, null, "Codex 配置目录不存在");
            }

            installed = true;
            OwnEntry? own = FindOwnEntry(ReadHooksRoot());
            if (own is null)
            {
                string legacy = HasLegacyNotify() ? ";检测到旧版 notify 链残留,可手动清理 config.toml 的 notify" : string.Empty;
                return new NotificationProviderStatus(Kind, DisplayName, true, false, _hooksPath,
                    "未安装 AI Resume 通知钩子" + legacy);
            }

            string expectedHash = ComputeTrustHash(own.Command, own.Timeout);
            string key = StateKey(own.GroupIndex, own.HandlerIndex);
            if (HooksDisabledInConfig())
            {
                return new NotificationProviderStatus(Kind, DisplayName, true, true, _hooksPath,
                    "config.toml 的 [features] hooks = false:Codex 已禁用 hooks,钩子不会执行;删除该项或改为 true 后新开会话",
                    HookCommand: own.Command, HookBroken: true);
            }

            (bool trusted, string why) = IsTrusted(key, expectedHash);
            // 旧 notify 链里若仍有 AI Resume,同一回合会有两个负载;Hook 已按 turn_id 去重,这里只提示不改写。
            string legacyNote = HasLegacyNotify()
                ? ";config.toml 的旧版 notify 仍链着 AI Resume,Hook 已按回合去重,如仍收到重复通知请手动删除 notify 中的 AI Resume 元素"
                : string.Empty;
            return trusted
                ? new NotificationProviderStatus(Kind, DisplayName, true, true, _hooksPath,
                    "已安装 AI Resume Stop 钩子,信任哈希已登记(" + key + ");新开的 Codex 会话即生效" + legacyNote, HookCommand: own.Command)
                : new NotificationProviderStatus(Kind, DisplayName, true, true, _hooksPath,
                    "钩子已写入 hooks.json 但 Codex 尚未信任它(" + why + ";键 " + key + "):在 codex 里运行 /hooks 信任,或关掉再打开本开关重写信任记录" + legacyNote,
                    HookCommand: own.Command, HookBroken: true);
        }
        catch (Exception ex)
        {
            return new NotificationProviderStatus(Kind, DisplayName, installed, false, _hooksPath, $"探测异常: {ex.Message}", Unverified: true);
        }
    }

    public void Enable(string hookCommand)
    {
        string exe = HookCommand.ExtractExecutable(hookCommand) ??
                     throw new ArgumentException("hook 可执行文件路径为空或无效", nameof(hookCommand));
        string command = HookCommand.Format(exe, Source);

        Directory.CreateDirectory(_codexHome);
        using IDisposable lease = AcquireWriteLease();

        JsonObject root = ReadHooksRoot() ?? new JsonObject();
        if (!root.TryGetPropertyValue("hooks", out JsonNode? hooksNode) || hooksNode is not JsonObject hooks)
        {
            hooks = new JsonObject();
            root["hooks"] = hooks;
        }

        if (!hooks.TryGetPropertyValue(EventName, out JsonNode? stopNode) || stopNode is not JsonArray stop)
        {
            stop = new JsonArray();
            hooks[EventName] = stop;
        }

        OwnEntry? own = FindOwnEntry(root);
        int groupIndex;
        int handlerIndex;
        if (own is not null)
        {
            JsonObject handler = (JsonObject)((JsonObject)stop[own.GroupIndex]!)["hooks"]![own.HandlerIndex]!;
            handler["type"] = "command";
            handler["command"] = command;
            handler["timeout"] = TimeoutSeconds;
            handler.Remove("async");
            handler.Remove("statusMessage");
            handler.Remove("additionalContextLimit");
            handler.Remove("commandWindows");
            groupIndex = own.GroupIndex;
            handlerIndex = own.HandlerIndex;
        }
        else
        {
            // 永远追加到末尾:信任键按序号定位,插到前面会把用户自己条目的信任记录全部错位。
            stop.Add(new JsonObject
            {
                ["hooks"] = new JsonArray(new JsonObject
                {
                    ["type"] = "command",
                    ["command"] = command,
                    ["timeout"] = TimeoutSeconds,
                }),
            });
            groupIndex = stop.Count - 1;
            handlerIndex = 0;
        }

        string hash = ComputeTrustHash(command);
        string key = StateKey(groupIndex, handlerIndex);
        // 先写信任、再写 hooks.json:指向尚不存在序号的信任记录无害,而「有钩子、无信任」会让 Codex 静默不执行。
        TrustWrite trust = UpsertTrust(key, hash);
        try
        {
            WriteHooksRoot(root);
        }
        catch
        {
            TryRestoreConfig(trust);
            throw;
        }

        // 序号漂移后留下的旧记录(同哈希、不同键)会让日后落到该序号的用户钩子显示为 Modified,顺手清掉。
        RemoveTrustEntries(hash, exceptKey: key);
    }

    private sealed record TrustWrite(bool Changed, bool Existed, string Original);

    private void TryRestoreConfig(TrustWrite trust)
    {
        if (!trust.Changed)
        {
            return;
        }

        try
        {
            if (trust.Existed)
            {
                WriteAtomic(_configPath, trust.Original);
            }
            else if (File.Exists(_configPath))
            {
                File.Delete(_configPath);
            }
        }
        catch (Exception)
        {
            // 回滚失败只能保留现场;上层异常已经在传播。
        }
    }

    public void Disable()
    {
        if (!Directory.Exists(_codexHome))
        {
            return;
        }

        using IDisposable lease = AcquireWriteLease();
        JsonObject? root = ReadHooksRoot();
        OwnEntry? own = root is null ? null : FindOwnEntry(root);
        // 信任记录按「哈希等于我方命令的哈希」识别,不能按键前缀:用户自己的 Stop 钩子
        // 和我方共用同一个 hooks.json 前缀,按前缀删会把他的信任记录一起清掉。
        string? ownHash = own is null ? null : ComputeTrustHash(own.Command, own.Timeout);
        if (own is not null && root is not null)
        {
            var hooks = (JsonObject)root["hooks"]!;
            var stop = (JsonArray)hooks[EventName]!;
            var group = (JsonObject)stop[own.GroupIndex]!;
            var handlers = (JsonArray)group["hooks"]!;
            handlers.RemoveAt(own.HandlerIndex);
            bool groupRemoved = handlers.Count == 0;
            if (groupRemoved)
            {
                stop.RemoveAt(own.GroupIndex);
            }
            if (stop.Count == 0)
            {
                hooks.Remove(EventName);
            }

            bool nothingLeft = hooks.Count == 0 && root.Count == 1;
            if (nothingLeft)
            {
                File.Delete(_hooksPath);
            }
            else
            {
                WriteHooksRoot(root);
            }

            RemoveTrustEntries(ownHash);
            // 信任键按序号定位:摘掉我方那一组后,排在其后的用户分组整体前移一位,它们的信任记录必须跟着改,
            // 否则 Codex 会把别人的钩子当成未信任而静默停跑。同组内只摘一条时,同组其后的条序号同理。
            RenumberTrustEntries(own.GroupIndex, groupRemoved ? null : own.HandlerIndex);
            return;
        }

        RemoveTrustEntries(ownHash);
    }

    private void RenumberTrustEntries(int removedGroup, int? removedHandlerInGroup)
    {
        if (!File.Exists(_configPath))
        {
            return;
        }

        string original = File.ReadAllText(_configPath);
        string[] lines = original.Replace("\r\n", "\n").Split('\n');
        string newline = original.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        string prefix = _trustKeySource + ":" + EventKeyLabel + ":";
        bool changed = false;
        for (int i = 0; i < lines.Length; i++)
        {
            string? key = TryReadStateKey(lines[i]);
            if (key is null || !key.StartsWith(prefix, StringComparison.Ordinal))
            {
                continue;
            }

            string[] parts = key[prefix.Length..].Split(':');
            if (parts.Length != 2 || !int.TryParse(parts[0], out int g) || !int.TryParse(parts[1], out int h))
            {
                continue;
            }

            int newG = g;
            int newH = h;
            if (removedHandlerInGroup is null)
            {
                if (g <= removedGroup) continue;
                newG = g - 1;
            }
            else
            {
                if (g != removedGroup || h <= removedHandlerInGroup.Value) continue;
                newH = h - 1;
            }

            string indent = lines[i][..(lines[i].Length - lines[i].TrimStart().Length)];
            lines[i] = indent + "[hooks.state." + TomlBasicString($"{prefix}{newG}:{newH}") + "]";
            changed = true;
        }

        if (!changed)
        {
            return;
        }

        string candidate = string.Join(newline, lines);
        TomlSerializer.Deserialize<TomlTable>(candidate);
        WriteAtomic(_configPath, candidate, expectedExisting: original);
    }

    private bool HooksDisabledInConfig()
    {
        try
        {
            if (!File.Exists(_configPath))
            {
                return false;
            }

            TomlTable root = TomlSerializer.Deserialize<TomlTable>(File.ReadAllText(_configPath)) ?? new TomlTable();
            return root.TryGetValue("features", out object? featuresRaw) && featuresRaw is TomlTable features &&
                   features.TryGetValue("hooks", out object? hooks) && hooks is false;
        }
        catch (Exception)
        {
            return false;
        }
    }

    // ── hooks.json ──

    private sealed record OwnEntry(int GroupIndex, int HandlerIndex, string Command, int Timeout);

    private JsonObject? ReadHooksRoot()
    {
        _hooksTextSnapshot = null;
        if (!File.Exists(_hooksPath))
        {
            return null;
        }

        string json = File.ReadAllText(_hooksPath);
        _hooksTextSnapshot = json;
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        return JsonNode.Parse(json) as JsonObject ??
               throw new InvalidOperationException("hooks.json 根节点不是 JSON 对象,拒绝修改");
    }

    private static OwnEntry? FindOwnEntry(JsonObject? root)
    {
        if (root is null ||
            !root.TryGetPropertyValue("hooks", out JsonNode? hooksNode) || hooksNode is not JsonObject hooks ||
            !hooks.TryGetPropertyValue(EventName, out JsonNode? stopNode) || stopNode is not JsonArray stop)
        {
            return null;
        }

        for (int g = 0; g < stop.Count; g++)
        {
            if (stop[g] is not JsonObject group ||
                !group.TryGetPropertyValue("hooks", out JsonNode? handlersNode) || handlersNode is not JsonArray handlers)
            {
                continue;
            }

            for (int h = 0; h < handlers.Count; h++)
            {
                if (handlers[h] is JsonObject handler &&
                    handler["command"] is JsonValue value && value.TryGetValue(out string? command) &&
                    HookCommand.IsManaged(command, MarkerFileName, Source))
                {
                    int timeout = handler["timeout"] is JsonValue t && t.TryGetValue(out int seconds) ? seconds : 600;
                    return new OwnEntry(g, h, command, timeout);
                }
            }
        }

        return null;
    }

    private void WriteHooksRoot(JsonObject root)
    {
        string json = root.ToJsonString(new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        });
        // 读取与写入之间若被别的程序改写,放弃本次而不是盖掉它的改动。
        WriteAtomic(_hooksPath, json + Environment.NewLine, expectedExisting: _hooksTextSnapshot);
    }

    // ── config.toml 的信任记录 ──

    private (bool Trusted, string Why) IsTrusted(string key, string expectedHash)
    {
        if (!File.Exists(_configPath))
        {
            return (false, "config.toml 不存在");
        }

        TomlTable root;
        try
        {
            root = TomlSerializer.Deserialize<TomlTable>(File.ReadAllText(_configPath)) ?? new TomlTable();
        }
        catch (TomlException)
        {
            return (false, "config.toml 无法解析");
        }

        if (!root.TryGetValue("hooks", out object? hooksRaw) || hooksRaw is not TomlTable hooks ||
            !hooks.TryGetValue("state", out object? stateRaw) || stateRaw is not TomlTable state ||
            !state.TryGetValue(key, out object? entryRaw) || entryRaw is not TomlTable entry)
        {
            return (false, "hooks.state 里没有本钩子的信任记录");
        }

        if (entry.TryGetValue("enabled", out object? enabled) && enabled is false)
        {
            return (false, "信任记录被标记为 enabled=false");
        }

        return entry.TryGetValue("trusted_hash", out object? hashRaw) && hashRaw is string hash &&
               string.Equals(hash, expectedHash, StringComparison.Ordinal)
            ? (true, string.Empty)
            : (false, "trusted_hash 与当前钩子定义不一致");
    }

    private static readonly Regex StateHeaderRegex = new(
        @"^\s*\[\s*hooks\s*\.\s*state\s*\.\s*(?:""(?<basic>(?:\\.|[^""\\])*)""|'(?<literal>[^']*)'|(?<bare>[A-Za-z0-9_-]+))\s*\]\s*(?:#.*)?$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex AnyHeaderRegex = new(@"^\s*\[", RegexOptions.Compiled);

    private TrustWrite UpsertTrust(string key, string hash)
    {
        bool existed = File.Exists(_configPath);
        string original = existed ? File.ReadAllText(_configPath) : string.Empty;
        string[] lines = original.Replace("\r\n", "\n").Split('\n');
        string newline = original.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        int header = FindStateHeader(lines, key);
        var output = new List<string>(lines.Length + 3);
        if (header >= 0)
        {
            int end = header + 1;
            while (end < lines.Length && !AnyHeaderRegex.IsMatch(lines[end]))
            {
                end++;
            }

            bool replaced = false;
            for (int i = 0; i < lines.Length; i++)
            {
                // 用户在 /hooks 里停用过(enabled = false):重新启用就是要它再跑,顺手去掉这一行。
                if (i > header && i < end && Regex.IsMatch(lines[i], @"^\s*enabled\s*="))
                {
                    continue;
                }

                if (i > header && i < end && Regex.IsMatch(lines[i], @"^\s*trusted_hash\s*="))
                {
                    if (!replaced)
                    {
                        output.Add("trusted_hash = " + TomlBasicString(hash));
                        replaced = true;
                    }
                    continue;
                }

                output.Add(lines[i]);
                if (i == header && !replaced)
                {
                    // 表头后紧跟写入;若该表原本就有 trusted_hash,上面的分支会替换并跳过旧行。
                    bool hasHash = false;
                    for (int j = header + 1; j < end; j++)
                    {
                        if (Regex.IsMatch(lines[j], @"^\s*trusted_hash\s*=")) { hasHash = true; break; }
                    }
                    if (!hasHash)
                    {
                        output.Add("trusted_hash = " + TomlBasicString(hash));
                        replaced = true;
                    }
                }
            }
        }
        else
        {
            output.AddRange(lines);
            while (output.Count > 0 && string.IsNullOrWhiteSpace(output[^1]))
            {
                output.RemoveAt(output.Count - 1);
            }
            if (output.Count > 0)
            {
                output.Add(string.Empty);
            }
            output.Add("# AI Resume: Codex Stop 钩子的信任记录(与 hooks.json 同步维护,勿手改)");
            output.Add("[hooks.state." + TomlBasicString(key) + "]");
            output.Add("trusted_hash = " + TomlBasicString(hash));
        }

        string candidate = string.Join(newline, output);
        if (!candidate.EndsWith(newline, StringComparison.Ordinal))
        {
            candidate += newline;
        }

        VerifyTrustWritten(candidate, key, hash);
        if (string.Equals(candidate, original, StringComparison.Ordinal))
        {
            return new TrustWrite(false, existed, original);
        }

        WriteAtomic(_configPath, candidate, expectedExisting: existed ? original : null);
        return new TrustWrite(true, existed, original);
    }

    private void RemoveTrustEntries(string? ownHash, string? exceptKey = null)
    {
        if (ownHash is null || !File.Exists(_configPath))
        {
            return;
        }

        string original = File.ReadAllText(_configPath);
        string[] lines = original.Replace("\r\n", "\n").Split('\n');
        string newline = original.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        string prefix = _trustKeySource + ":" + EventKeyLabel + ":";
        var drop = new HashSet<int>();
        for (int i = 0; i < lines.Length; i++)
        {
            string? key = TryReadStateKey(lines[i]);
            if (key is null || !key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(key, exceptKey, StringComparison.Ordinal))
            {
                continue;
            }

            int end = i + 1;
            while (end < lines.Length && !AnyHeaderRegex.IsMatch(lines[end]))
            {
                end++;
            }

            // 只删我方写入的记录:trusted_hash 必须正好是我方命令的哈希,且表里除了
            // trusted_hash/enabled 不该有别的键;任何一条不满足都不是我们的,留着。
            bool foreign = true;
            for (int j = i + 1; j < end; j++)
            {
                string t = lines[j].Trim();
                if (t.Length == 0 || t.StartsWith('#') || Regex.IsMatch(t, @"^enabled\s*="))
                {
                    continue;
                }

                Match hashLine = Regex.Match(t, "^trusted_hash\\s*=\\s*\"([^\"]*)\"");
                if (hashLine.Success)
                {
                    foreign = !string.Equals(hashLine.Groups[1].Value, ownHash, StringComparison.Ordinal);
                    if (foreign)
                    {
                        break;
                    }
                    continue;
                }

                foreign = true;
                break;
            }

            if (foreign)
            {
                continue;
            }

            for (int j = i; j < end; j++)
            {
                drop.Add(j);
            }

            // 我方注释行紧贴在表头之前。
            if (i > 0 && lines[i - 1].TrimStart().StartsWith("# AI Resume: Codex Stop", StringComparison.Ordinal))
            {
                drop.Add(i - 1);
            }
        }

        if (drop.Count == 0)
        {
            return;
        }

        var output = new List<string>(lines.Length);
        for (int i = 0; i < lines.Length; i++)
        {
            if (!drop.Contains(i))
            {
                output.Add(lines[i]);
            }
        }

        while (output.Count > 1 && string.IsNullOrWhiteSpace(output[^1]) && string.IsNullOrWhiteSpace(output[^2]))
        {
            output.RemoveAt(output.Count - 1);
        }

        string candidate = string.Join(newline, output);
        TomlSerializer.Deserialize<TomlTable>(candidate);
        WriteAtomic(_configPath, candidate, expectedExisting: original);
    }

    private static int FindStateHeader(string[] lines, string key)
    {
        for (int i = 0; i < lines.Length; i++)
        {
            if (string.Equals(TryReadStateKey(lines[i]), key, StringComparison.Ordinal))
            {
                return i;
            }
        }

        return -1;
    }

    private static string? TryReadStateKey(string line)
    {
        Match match = StateHeaderRegex.Match(line);
        if (!match.Success)
        {
            return null;
        }

        if (match.Groups["basic"].Success)
        {
            return UnescapeTomlBasic(match.Groups["basic"].Value);
        }

        return match.Groups["literal"].Success ? match.Groups["literal"].Value : match.Groups["bare"].Value;
    }

    private static string UnescapeTomlBasic(string value)
    {
        var sb = new StringBuilder(value.Length);
        for (int i = 0; i < value.Length; i++)
        {
            char c = value[i];
            if (c != '\\' || i + 1 >= value.Length)
            {
                sb.Append(c);
                continue;
            }

            char n = value[++i];
            sb.Append(n switch
            {
                'n' => '\n',
                't' => '\t',
                'r' => '\r',
                '"' => '"',
                '\\' => '\\',
                _ => n,
            });
        }

        return sb.ToString();
    }

    private static void VerifyTrustWritten(string candidate, string key, string hash)
    {
        TomlTable root = TomlSerializer.Deserialize<TomlTable>(candidate) ??
                         throw new InvalidOperationException("config.toml 候选内容为空");
        if (!root.TryGetValue("hooks", out object? hooksRaw) || hooksRaw is not TomlTable hooks ||
            !hooks.TryGetValue("state", out object? stateRaw) || stateRaw is not TomlTable state ||
            !state.TryGetValue(key, out object? entryRaw) || entryRaw is not TomlTable entry ||
            !entry.TryGetValue("trusted_hash", out object? written) || written is not string value ||
            !string.Equals(value, hash, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("写入 hooks.state 信任记录后经 TOML 解析无法读回,拒绝提交");
        }
    }

    private static string TomlBasicString(string value)
    {
        var sb = new StringBuilder("\"");
        foreach (char c in value)
        {
            switch (c)
            {
                case '\\': sb.Append("\\\\"); break;
                case '"': sb.Append("\\\""); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                default:
                    if (char.IsControl(c)) sb.Append("\\u").Append(((int)c).ToString("X4"));
                    else sb.Append(c);
                    break;
            }
        }

        return sb.Append('"').ToString();
    }

    private bool HasLegacyNotify()
    {
        try
        {
            if (!File.Exists(_configPath))
            {
                return false;
            }

            TomlTable root = TomlSerializer.Deserialize<TomlTable>(File.ReadAllText(_configPath)) ?? new TomlTable();
            return root.TryGetValue("notify", out object? notify) && notify is TomlArray items &&
                   items.OfType<string>().Any(item =>
                       item.Contains(MarkerFileName, StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception)
        {
            return false;
        }
    }

    // ── 文件写入 ──

    private static void WriteAtomic(string path, string content, string? expectedExisting = null)
    {
        if (expectedExisting is not null && File.Exists(path) &&
            !string.Equals(File.ReadAllText(path), expectedExisting, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(Path.GetFileName(path) + " 在修改期间被外部改写,本次放弃,请重试");
        }

        string temp = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                byte[] bytes = new UTF8Encoding(false).GetBytes(content);
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }

            if (File.Exists(path))
            {
                File.Replace(temp, path, path + ".bak", ignoreMetadataErrors: true);
            }
            else
            {
                File.Move(temp, path);
            }
        }
        finally
        {
            if (File.Exists(temp))
            {
                try { File.Delete(temp); } catch (IOException) { }
            }
        }
    }

    private IDisposable AcquireWriteLease()
    {
        var mutex = new Mutex(false, _writeLockName);
        bool acquired;
        try
        {
            acquired = mutex.WaitOne(TimeSpan.FromSeconds(WriteLockTimeoutSeconds));
        }
        catch (AbandonedMutexException)
        {
            acquired = true;
        }

        if (!acquired)
        {
            mutex.Dispose();
            throw new TimeoutException("另一个 AI Resume 进程正在修改 Codex hooks 配置,请稍后重试");
        }

        return new MutexLease(mutex);
    }

    private sealed class MutexLease(Mutex mutex) : IDisposable
    {
        public void Dispose()
        {
            try { mutex.ReleaseMutex(); } catch (ApplicationException) { }
            mutex.Dispose();
        }
    }
}
