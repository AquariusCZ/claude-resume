using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AiResume.Core;
using AiResume.Worker.Products;

namespace AiResume.Worker.BudgetGate;

/// <summary>
/// 子代理额度预检的控制面(ADR-0008 §E/§F):模式意图、config.json、settings.json 登记与对账。
///
/// 意图持久化在 <see cref="ProductConfig.BudgetGateMode"/>(空串 = 旧配置从未记录,install 按默认 shadow 记下),
/// <see cref="ProductConfig.BudgetGateConfigVersion"/> 每次变更递增并写进 config.json;mod 在心跳里回报它读到的版本,
/// 界面据此判断"真的生效"——安装成功不等于生效。enforce(拦截)只能经界面上用户的显式操作写入,install 从不默认它。
/// 「已关闭」即撤下 settings.json 里的登记(新会话不再加载 mod),这是不卸载 AI Resume 也能持久退出的办法。
/// 组织策略禁止旁加载、或策略读不出时一律不登记(<see cref="SideloadPolicy"/>)。
/// </summary>
public sealed class BudgetGateControl
{
    public static readonly string[] Modes = ["off", "observe", "shadow", "enforce"];
    public const string DefaultMode = "shadow";

    private readonly ProductConfigStore _store;
    private readonly BudgetGatePaths _paths;
    private readonly ClaudePluginDirs _env;
    private readonly SideloadPolicy _policy;

    public BudgetGateControl(ProductConfigStore store, BudgetGatePaths paths, ClaudePluginDirs env, SideloadPolicy? policy = null)
    {
        _store = store;
        _paths = paths;
        _env = env;
        _policy = policy ?? new SideloadPolicy();
    }

    public BudgetGatePaths Paths => _paths;
    public ClaudePluginDirs Env => _env;

    public static bool IsMode(string? mode) => mode is not null && Modes.Contains(mode, StringComparer.Ordinal);

    /// <summary>当前意图:模式(未记录时按默认)与配置版本。</summary>
    public (string Mode, int Version, bool Recorded) Intent()
    {
        ProductConfig config = _store.Load();
        bool recorded = IsMode(config.BudgetGateMode);
        return (recorded ? config.BudgetGateMode : DefaultMode, config.BudgetGateConfigVersion, recorded);
    }

    public SideloadVerdict Policy() => _policy.Check();

    /// <summary>
    /// install 对账:记下意图(旧配置补默认 shadow)→ 建数据目录、子目录与盐 → 写 config.json → settings.json 里我方条目
    /// 恰好指向 <paramref name="modDir"/>(「已关闭」或策略不允许时一条不留;开发期旧条目被替换,别人的条目不动)→ 读回核对。
    /// 任何一步没对齐都返回 false 并给出原因,调用方按退出码 2 处理。
    /// </summary>
    public BudgetGateReconcile Reconcile(string modDir)
    {
        try
        {
            string missing = MissingModFiles(modDir);
            if (missing.Length > 0)
                return new BudgetGateReconcile(false, $"mod 文件不全:缺 {missing}");

            (string mode, int version) = EnsureIntent();
            EnsureDataDir();
            // 版本相同但模式不同(手改过或开发期留下的)同样按意图重写:意图是唯一真身。
            if (ReadConfigState() != (version, mode))
                WriteConfig(mode, version);
            if (ReadConfigState() != (version, mode))
                return new BudgetGateReconcile(false, "config.json 写入后读回的版本或模式不一致");

            string registration = ApplyRegistration(modDir, mode, out bool ok);
            return new BudgetGateReconcile(ok, $"子代理预检:{ModeLabel(mode)}(配置 v{version});{registration}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
        {
            // 续跑运行期间 ExecutionConfigLease 只读共享持有 settings.json,原子替换会失败:按未对齐处理,下次 install 再来,绝不吞掉。
            return new BudgetGateReconcile(false, $"子代理预检对账失败:{ex.Message}");
        }
    }

    /// <summary>
    /// 按模式与策略对齐 settings.json 的登记,返回给人看的一句话;<paramref name="ok"/> 为是否对齐。
    /// 「已关闭」或策略不允许 → 撤下我方条目;否则恰好一条指向 <paramref name="modDir"/>。
    /// </summary>
    private string ApplyRegistration(string modDir, string mode, out bool ok)
    {
        if (mode == "off")
        {
            _env.Apply(modDir, enable: false);
            ok = !_env.Inspect(modDir).Ours.Any();
            return ok ? "已关闭:settings.json 里不登记 mod" : "已关闭,但没能撤下 settings.json 里的登记";
        }

        SideloadVerdict policy = _policy.Check();
        if (!policy.CanRegister)
        {
            _env.Apply(modDir, enable: false);
            ok = false;
            return $"{policy.Detail};不登记 mod(否则新启动的 Claude Code 会直接退出)";
        }

        bool changed = _env.Apply(modDir, enable: true);
        PluginDirsStatus status = _env.Inspect(modDir);
        ok = status.IsExact;
        return !ok ? $"settings.json 未对齐:{status.Detail}"
            : changed ? "已在 settings.json 登记 mod;新启动的 Claude Code 会话才加载,是否生效以界面上的心跳为准"
            : "mod 已登记;是否生效以界面上的心跳为准";
    }

    /// <summary>
    /// uninstall:先从 settings.json 移除我方条目(在拆任何入口之前做,失败就中止卸载、什么都没动);意图原样保留供重装恢复。
    /// settings.json 读不懂时,文本里没有我方条目就当没有可撤的,继续卸载;有我方条目却读不懂才抛出。
    /// </summary>
    public bool Retire()
    {
        try
        {
            return _env.Apply(_paths.ModDir, enable: false);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            if (!_env.RawContains(BudgetGatePaths.ModName))
                return false;
            throw;
        }
    }

    /// <summary>
    /// 界面切换模式:先写意图(版本 +1)再写 config.json;config.json 写不进去时把意图恢复原样再抛出。
    /// 写成功后按新模式对齐登记(「已关闭」撤下,其它模式登记);登记失败不回滚模式,由界面的证据显示「未生效」与原因。
    /// </summary>
    public int SetMode(string mode)
    {
        if (!IsMode(mode))
            throw new ArgumentException($"未知模式:{mode}", nameof(mode));
        string previousMode = string.Empty;
        int previousVersion = 0;
        int version = 0;
        _store.Update(c =>
        {
            previousMode = c.BudgetGateMode;
            previousVersion = c.BudgetGateConfigVersion;
            c.BudgetGateMode = mode;
            c.BudgetGateConfigVersion = previousVersion + 1;
            version = c.BudgetGateConfigVersion;
        });
        try
        {
            EnsureDataDir();
            WriteConfig(mode, version);
        }
        catch
        {
            _store.Update(c =>
            {
                if (c.BudgetGateConfigVersion == version)
                {
                    c.BudgetGateMode = previousMode;
                    c.BudgetGateConfigVersion = previousVersion;
                }
            });
            throw;
        }

        try
        {
            ApplyRegistration(_paths.ModDir, mode, out _);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
        {
            // 登记没对上:模式已经记下,界面证据会显示「未生效」与原因,下次切换或 install 再对齐。
        }

        return version;
    }

    /// <summary>
    /// config.json 缺失、版本落后于意图、或版本相同模式却不同时按意图重写(Worker 定期调用);返回是否重写了。
    /// 只"补"不"降":版本高于意图时一律不动,免得和界面刚做的切换交错时把旧模式写回去。
    /// </summary>
    public bool HealConfig()
    {
        (string mode, int version, bool recorded) = Intent();
        if (!recorded)
            return false;
        (int? current, string? currentMode) = ReadConfigState();
        if (current is not null && (current > version || (current == version && currentMode == mode)))
            return false;
        EnsureDataDir();
        WriteConfig(mode, version);
        return true;
    }

    /// <summary>策略看护(Worker 每小时):策略不允许旁加载时撤下我方登记,免得新会话一启动就退出。返回是否撤了。</summary>
    public bool EnforcePolicy()
    {
        SideloadVerdict policy = _policy.Check();
        return !policy.CanRegister && _env.Inspect(_paths.ModDir).Ours.Any() && _env.Apply(_paths.ModDir, enable: false);
    }

    /// <summary>界面、CLI 与文档共用的模式名(与 index.html 的 <c>BUDGET_MODE</c> 一致)。</summary>
    public static string ModeLabel(string mode) => mode switch
    {
        "off" => "已关闭",
        "observe" => "只记用量",
        "shadow" => "试运行",
        "enforce" => "拦截",
        _ => mode,
    };

    /// <summary>缺哪些 mod 文件(空串 = 齐全)。</summary>
    public static string MissingModFiles(string modDir) =>
        string.Join("、", BudgetGatePaths.RequiredModFiles.Where(f => !File.Exists(Path.Combine(modDir, f))));

    /// <summary>mod 自己声明的版本(plugin.json),读不出为 null。</summary>
    public static string? ModVersion(string modDir)
    {
        try
        {
            return JsonNode.Parse(ReadShared(Path.Combine(modDir, ".claude-plugin", "plugin.json")))?["version"]?.GetValue<string>();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
        {
            return null;
        }
    }

    private (string Mode, int Version) EnsureIntent()
    {
        string mode = DefaultMode;
        int version = 0;
        _store.Update(c =>
        {
            if (!IsMode(c.BudgetGateMode))
            {
                c.BudgetGateMode = DefaultMode;
                c.BudgetGateConfigVersion += 1;
            }
            else if (c.BudgetGateConfigVersion <= 0)
            {
                c.BudgetGateConfigVersion = 1;
            }

            mode = c.BudgetGateMode;
            version = c.BudgetGateConfigVersion;
        });
        return (mode, version);
    }

    /// <summary>
    /// 数据目录、mod 要写的子目录与哈希盐都由 AI Resume 先建好:mod 只往已存在的目录里写(MSIX 虚拟化),
    /// 盐让日志里的项目键与提示哈希无法按常见路径或短提示反推。
    /// </summary>
    private void EnsureDataDir()
    {
        Directory.CreateDirectory(_paths.SessionsDir);
        Directory.CreateDirectory(_paths.LogsDir);
        string salt = Path.Combine(_paths.DataDir, "salt");
        if (!File.Exists(salt))
            File.WriteAllText(salt, Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant(), new UTF8Encoding(false));
    }

    /// <summary>读 config.json 里的版本;不存在或读不出为 null。</summary>
    public int? ReadConfigVersion() => ReadConfigState().Version;

    /// <summary>
    /// 证据汇总用的 config.json 现状:版本、模式与最近一次被改写的时刻(取文件修改时间:mod 也是按修改时间决定要不要重读),
    /// 用来分清「设置刚更新、心跳还没轮到」与「过了一个心跳周期仍没跟上」,并核对文件里的模式是不是当前设置。
    /// 读的前后修改时间不一样(读到一半被替换)就再读一次,免得拼出对不上的一组。不存在或读不出的字段为 null。
    /// </summary>
    public BudgetGateConfigStamp ReadConfigStamp()
    {
        DateTimeOffset? before = ConfigWrittenAt();
        (int? version, string? mode) = ReadConfigState();
        DateTimeOffset? after = ConfigWrittenAt();
        if (before != after)
        {
            // 读到一半被替换:再读一次;还对不上就不给修改时间(证据按「分不清」处理,不会因此说成生效中)。
            before = after;
            (version, mode) = ReadConfigState();
            after = ConfigWrittenAt();
        }

        return new BudgetGateConfigStamp(version, mode, before == after ? after : null);
    }

    private DateTimeOffset? ConfigWrittenAt()
    {
        try
        {
            var file = new FileInfo(_paths.ConfigFile);
            return file.Exists ? new DateTimeOffset(file.LastWriteTimeUtc, TimeSpan.Zero) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>读 config.json 里的版本与模式;不存在或读不出的字段为 null。</summary>
    public (int? Version, string? Mode) ReadConfigState()
    {
        try
        {
            if (!File.Exists(_paths.ConfigFile))
                return (null, null);
            JsonNode? root = JsonNode.Parse(ReadShared(_paths.ConfigFile));
            int? version = root?["version"] is JsonValue v && v.TryGetValue(out int n) ? n : null;
            string? mode = root?["mode"] is JsonValue m && m.TryGetValue(out string? s) ? s : null;
            return (version, mode);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return (null, null);
        }
    }

    private void WriteConfig(string mode, int version)
    {
        Directory.CreateDirectory(_paths.DataDir);
        var root = new JsonObject
        {
            ["schema"] = 1,
            ["version"] = version,
            ["mode"] = mode,
            ["updatedAt"] = DateTimeOffset.UtcNow.ToString("O"),
        };
        string temp = _paths.ConfigFile + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            File.WriteAllText(temp, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }), new UTF8Encoding(false));
            File.Move(temp, _paths.ConfigFile, overwrite: true);
        }
        catch
        {
            try { if (File.Exists(temp)) File.Delete(temp); } catch { /* 忽略清理失败 */ }
            throw;
        }
    }

    /// <summary>读别的进程可能正在原子替换的文件:共享读写与删除打开。</summary>
    internal static string ReadShared(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(fs, Encoding.UTF8);
        return reader.ReadToEnd();
    }
}

public sealed record BudgetGateReconcile(bool Ok, string Detail);

/// <summary>config.json 的版本、模式与修改时间(同一次读出);读不出的字段为 null。</summary>
public sealed record BudgetGateConfigStamp(int? Version, string? Mode, DateTimeOffset? WrittenAt);
