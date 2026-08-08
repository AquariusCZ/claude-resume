using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AiResume.Core;
using AiResume.Storage;
using AiResume.Worker.Products;

namespace AiResume.Worker.Migration;

/// <summary>迁移选项。</summary>
public sealed record MigrationOptions(
    string LegacyAppDir,      // 现役 AppDir(目录)
    string ShadowRoot,        // shadow 根(目录)
    string DatabasePath,      // shadow SQLite 文件完整路径
    bool DryRun,              // true = 只读 + 出报告,不写任何目标、不做备份
    bool Force);              // true = 已迁移过也重跑

/// <summary>单个源文件的迁移结果。</summary>
public sealed record MigrationItemResult(
    string Source,            // "config.json" / "state.json" / "completion-events-seen.json"
    string Status,            // "migrated" / "skipped" / "missing" / "failed"
    int Count,                // 实际迁移的字段/条目数
    string? SourceSha256,     // 源文件内容 SHA256(十六进制大写);缺失时 null
    string? BackupPath,       // 备份落点;DryRun 时 null
    string? Reason);          // skipped/failed 的理由

/// <summary>迁移报告。</summary>
public sealed record MigrationReport(
    bool DryRun,
    DateTimeOffset StartedAt,
    IReadOnlyList<MigrationItemResult> Items,
    int SkippedNonOwnedKeys,      // config.json 里被白名单挡掉的顶层键数量(只给数量)
    int DroppedLegacyFields,      // state.json 里目标没有的字段数量
    bool Success);                // 无 failed 项即 true

/// <summary>
/// S9 产品状态迁移器:把现役 AppDir 的自有状态迁移到 shadow 根。
///
/// 红线:
/// - 现役 AppDir 全程只读,绝不删除、绝不修改任何文件;
/// - 凭据字段不读取、不迁移、不出现在报告里(报告只给跳过计数,连键名都不列);
/// - 幂等:shadow 根下 migration-state.json 记录源哈希,源未变则跳过;
/// - DryRun 绝不写目标、绝不备份、绝不写标记。
/// </summary>
public sealed class ProductStateMigrator
{
    // config.json 白名单:只读这 14 个键,其余一律不取值。
    private static readonly HashSet<string> ConfigWhitelist = new(StringComparer.Ordinal)
    {
        "enabled", "armed", "armCycleId", "continuous", "selected", "customProjects", "hiddenProjects",
        "projectHome", "probeIntervalMinutes", "probeModel", "resumeModel", "resumePrompt",
        "skipPermissions", "dirtyGuard",
    };

    // state.json 里现役独有的 targetId/targetEndUtc/firedForId 不另立名单:
    // ApplyStateField 的 default 分支已经覆盖"目标没有的字段",单列一份会随
    // CheckerState 演进而失效(新增字段忘了从名单里删就会被误丢)。

    /// <summary>
    /// 迁移标记的读写选项。**写和读必须共用同一份**——曾经写用 camelCase 策略、
    /// 读用默认选项,于是 <c>sourceHashes</c> 永远绑不上 <c>SourceHashes</c>,
    /// 标记读出来是空的,幂等形同虚设、每次都重迁(被幂等测试抓到)。
    /// </summary>
    private static readonly JsonSerializerOptions MarkerJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    private const string MigrationStateFileName = "migration-state.json";
    private const string BackupDirectoryName = "migration-backup";

    private readonly ProductConfigStore _configStore;
    private readonly ProductStateStore _stateStore;

    public ProductStateMigrator(ProductConfigStore configStore, ProductStateStore stateStore)
    {
        _configStore = configStore ?? throw new ArgumentNullException(nameof(configStore));
        _stateStore = stateStore ?? throw new ArgumentNullException(nameof(stateStore));
    }

    /// <summary>执行迁移,返回报告。</summary>
    public MigrationReport Run(MigrationOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var startedAt = DateTimeOffset.UtcNow;
        var items = new List<MigrationItemResult>();
        int skippedNonOwnedKeys = 0;
        int droppedLegacyFields = 0;
        bool success = true;

        // 源文件路径。
        string legacyConfigPath = Path.Combine(options.LegacyAppDir, "config.json");
        string legacyStatePath = Path.Combine(options.LegacyAppDir, "state.json");
        string legacyCompletionPath = Path.Combine(options.LegacyAppDir, "completion-events-seen.json");

        // 幂等检查:标记存在且源哈希一致 → 全部 skipped。
        // **DryRun 也要走这一步**:演练的意义就是预测实跑会发生什么,
        // 如果实跑会因为已迁移而跳过,演练却报告"将迁移 14 字段",演练就是在撒谎。
        if (!options.Force)
        {
            var marker = LoadMigrationMarker(options.ShadowRoot);
            if (marker is not null)
            {
                bool configUnchanged = HashesMatch(marker, "config.json", legacyConfigPath);
                bool stateUnchanged = HashesMatch(marker, "state.json", legacyStatePath);
                if (configUnchanged && stateUnchanged)
                {
                    string reason = "已迁移且源未变化";
                    items.Add(new MigrationItemResult("config.json", "skipped", 0, ComputeSha256(legacyConfigPath), null, reason));
                    items.Add(new MigrationItemResult("state.json", "skipped", 0, ComputeSha256(legacyStatePath), null, reason));
                    items.Add(new MigrationItemResult("completion-events-seen.json", "skipped", 0, ComputeSha256(legacyCompletionPath), null, "两侧 eventId 算法不同,迁移无意义(见 S9 规格 §2)"));
                    return new MigrationReport(options.DryRun, startedAt, items, 0, 0, true);
                }
            }
        }

        // 备份目录(非 DryRun 且确实要写目标时创建)。
        string? backupDir = null;
        if (!options.DryRun)
        {
            backupDir = Path.Combine(options.ShadowRoot, BackupDirectoryName, startedAt.ToString("yyyyMMdd-HHmmss"));
        }

        // 1. config.json。
        var configResult = MigrateConfig(legacyConfigPath, options, backupDir, out skippedNonOwnedKeys, out int configDropped);
        items.Add(configResult);
        if (configResult.Status == "failed")
        {
            success = false;
        }

        // 2. state.json。
        var stateResult = MigrateState(legacyStatePath, options, backupDir, out int stateDropped);
        items.Add(stateResult);
        if (stateResult.Status == "failed")
        {
            success = false;
        }

        droppedLegacyFields = configDropped + stateDropped;

        // 3. completion-events-seen.json:恒为 skipped,但备份仍要做(现场快照)。
        string? completionSha = ComputeSha256(legacyCompletionPath);
        string? completionBackup = null;
        if (!options.DryRun && File.Exists(legacyCompletionPath) && backupDir is not null)
        {
            completionBackup = BackupFile(legacyCompletionPath, backupDir);
        }
        items.Add(new MigrationItemResult(
            "completion-events-seen.json",
            "skipped",
            0,
            completionSha,
            completionBackup,
            "两侧 eventId 算法不同,迁移无意义(见 S9 规格 §2)"));

        // 写迁移标记(非 DryRun 且无 failed 项时)。
        if (!options.DryRun && success)
        {
            WriteMigrationMarker(options.ShadowRoot, legacyConfigPath, legacyStatePath);
        }

        return new MigrationReport(options.DryRun, startedAt, items, skippedNonOwnedKeys, droppedLegacyFields, success);
    }

    /// <summary>
    /// 迁移 config.json 的白名单字段。
    ///
    /// **演练与实跑走同一条统计路径**:先把每个白名单字段试着应用到一个丢弃用的对象上,
    /// 只有真正应用成功的才计入 <c>Count</c>,类型不符的计入 dropped。
    /// DryRun 与实跑的唯一差别是最后写不写目标——否则"演练报告 14 字段、实跑只成功 12"
    /// 这种偏差正好发生在演练本该发现问题的地方。
    /// </summary>
    private MigrationItemResult MigrateConfig(
        string legacyPath, MigrationOptions options, string? backupDir,
        out int skippedNonOwnedKeys, out int droppedFields)
    {
        skippedNonOwnedKeys = 0;
        droppedFields = 0;

        if (!File.Exists(legacyPath))
        {
            return new MigrationItemResult("config.json", "missing", 0, null, null, "源文件不存在(全新安装)");
        }

        string? sha = ComputeSha256(legacyPath);
        // 备份先于解析:损坏的源同样要留下现场证据。
        string? backupPath = options.DryRun ? null : BackupFile(legacyPath, backupDir);

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(File.ReadAllText(legacyPath));
        }
        catch (JsonException)
        {
            return new MigrationItemResult("config.json", "failed", 0, sha, backupPath, "源文件 JSON 损坏");
        }

        using (doc)
        {
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                return new MigrationItemResult("config.json", "failed", 0, sha, backupPath, "源文件顶层不是 JSON 对象");
            }

            var applicable = new List<(string Name, JsonElement Value)>();
            foreach (JsonProperty prop in doc.RootElement.EnumerateObject())
            {
                if (!ConfigWhitelist.Contains(prop.Name))
                {
                    // 非自有键(含全部凭据):只数个数,**绝不取值、绝不记键名**。
                    skippedNonOwnedKeys++;
                    continue;
                }

                try
                {
                    ApplyConfigField(ProductConfig.CreateDefault(), prop.Name, prop.Value);
                    applicable.Add((prop.Name, prop.Value));
                }
                catch (Exception)
                {
                    // 类型不符(如 probeIntervalMinutes 写成字符串):跳过该字段,其余照迁。
                    droppedFields++;
                }
            }

            if (!options.DryRun)
            {
                try
                {
                    // 锁内重读只改本次负责字段:GUI/续跑引擎可能并发写同一份配置。
                    _configStore.Update(cfg =>
                    {
                        foreach ((string name, JsonElement value) in applicable)
                        {
                            ApplyConfigField(cfg, name, value);
                        }
                    });
                }
                catch (Exception ex)
                {
                    return new MigrationItemResult("config.json", "failed", 0, sha, backupPath, $"写入目标失败:{ex.Message}");
                }
            }

            return new MigrationItemResult("config.json", "migrated", applicable.Count, sha, backupPath, null);
        }
    }

    /// <summary>
    /// 迁移 state.json 到 CheckerState。同样是演练与实跑共用一条路径:
    /// 状态对象无条件构造(这就是演练的计算结果),只有 Save 一步区分。
    /// </summary>
    private MigrationItemResult MigrateState(
        string legacyPath, MigrationOptions options, string? backupDir, out int droppedLegacyFields)
    {
        droppedLegacyFields = 0;

        if (!File.Exists(legacyPath))
        {
            return new MigrationItemResult("state.json", "missing", 0, null, null, "源文件不存在(全新安装)");
        }

        string? sha = ComputeSha256(legacyPath);
        string? backupPath = options.DryRun ? null : BackupFile(legacyPath, backupDir);

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(File.ReadAllText(legacyPath));
        }
        catch (JsonException)
        {
            return new MigrationItemResult("state.json", "failed", 0, sha, backupPath, "源文件 JSON 损坏");
        }

        using (doc)
        {
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                return new MigrationItemResult("state.json", "failed", 0, sha, backupPath, "源文件顶层不是 JSON 对象");
            }

            var state = CheckerState.CreateDefault();
            int migratedCount = 0;
            foreach (JsonProperty prop in doc.RootElement.EnumerateObject())
            {
                try
                {
                    if (ApplyStateField(state, prop.Name, prop.Value))
                    {
                        migratedCount++;
                    }
                    else
                    {
                        // 目标没有的字段(targetId/targetEndUtc/firedForId 等现役定时器实现细节)。
                        droppedLegacyFields++;
                    }
                }
                catch (Exception)
                {
                    // 类型不符:跳过该字段,其余照迁。
                    droppedLegacyFields++;
                }
            }

            if (!options.DryRun)
            {
                try
                {
                    // 建表落在 stateStore 自己的库上:options.DatabasePath 与它不一致时
                    // 会把表建到另一个文件里,Save 仍然报 no such table。
                    StorageDatabase.Migrate(_stateStore.DatabasePath);
                    _stateStore.Save(state);
                }
                catch (Exception ex)
                {
                    return new MigrationItemResult("state.json", "failed", 0, sha, backupPath, $"写入目标失败:{ex.Message}");
                }
            }

            return new MigrationItemResult("state.json", "migrated", migratedCount, sha, backupPath, null);
        }
    }

    /// <summary>把单个白名单字段写入 ProductConfig。</summary>
    private static void ApplyConfigField(ProductConfig cfg, string name, JsonElement value)
    {
        switch (name)
        {
            case "enabled":
                cfg.Enabled = value.GetBoolean();
                break;
            case "armed":
                cfg.Armed = value.GetBoolean();
                break;
            case "armCycleId":
                cfg.ArmCycleId = value.GetString() ?? string.Empty;
                break;
            case "continuous":
                cfg.Continuous = value.GetBoolean();
                break;
            case "selected":
                cfg.Selected = ReadProjectRefList(value);
                break;
            case "customProjects":
                cfg.CustomProjects = ReadProjectRefList(value);
                break;
            case "hiddenProjects":
                cfg.HiddenProjects = ReadStringList(value);
                break;
            case "projectHome":
                cfg.ProjectHome = value.GetString() ?? string.Empty;
                break;
            case "probeIntervalMinutes":
                cfg.ProbeIntervalMinutes = value.GetInt32();
                break;
            case "probeModel":
                cfg.ProbeModel = value.GetString() ?? "haiku";
                break;
            case "resumeModel":
                cfg.ResumeModel = value.GetString() ?? string.Empty;
                break;
            case "resumePrompt":
                cfg.ResumePrompt = value.GetString() ?? "continue";
                break;
            case "skipPermissions":
                cfg.SkipPermissions = value.GetBoolean();
                break;
            case "dirtyGuard":
                cfg.DirtyGuard = value.GetString() ?? "stash";
                break;
            default:
                // 白名单外的键不应到达这里。
                break;
        }
    }

    /// <summary>把单个字段写入 CheckerState;返回 false 表示未知字段(丢弃)。</summary>
    private static bool ApplyStateField(CheckerState state, string name, JsonElement value)
    {
        switch (name)
        {
            case "phase":
                state.Phase = value.GetString() ?? CheckerState.PhaseIdle;
                return true;
            case "cycleId":
                state.CycleId = value.GetString() ?? string.Empty;
                return true;
            case "sawLimited":
                state.SawLimited = value.GetBoolean();
                return true;
            case "lastProbeUtc":
                // 现役是 ISO 8601 字符串,直接 parse。
                state.LastProbeUtc = DateTimeOffset.Parse(value.GetString()!);
                return true;
            case "limitedRefires":
                state.LimitedRefires = value.GetInt32();
                return true;
            case "realFiveHourResetUtc":
                // 现役是 Unix 秒整数。
                state.RealFiveHourResetUtc = DateTimeOffset.FromUnixTimeSeconds(value.GetInt64());
                return true;
            case "realSevenDayResetUtc":
                // 现役是 Unix 秒整数。
                state.RealSevenDayResetUtc = DateTimeOffset.FromUnixTimeSeconds(value.GetInt64());
                return true;
            case "realResetProbedUtc":
                // 现役是 Unix 秒整数。
                state.RealResetProbedUtc = DateTimeOffset.FromUnixTimeSeconds(value.GetInt64());
                return true;
            case "realFiveHourUtil":
                state.RealFiveHourUtil = value.GetDouble();
                return true;
            case "projectStatus":
                state.ProjectStatus = ReadStringDictionary(value);
                return true;
            default:
                return false;
        }
    }

    /// <summary>读取 ProjectRef 列表。</summary>
    private static List<ProjectRef> ReadProjectRefList(JsonElement value)
    {
        var result = new List<ProjectRef>();
        if (value.ValueKind != JsonValueKind.Array)
        {
            return result;
        }

        foreach (var item in value.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var projectRef = new ProjectRef();
            foreach (var prop in item.EnumerateObject())
            {
                if (prop.NameEquals("name"))
                {
                    projectRef.Name = prop.Value.GetString() ?? string.Empty;
                }
                else if (prop.NameEquals("path"))
                {
                    projectRef.Path = prop.Value.GetString() ?? string.Empty;
                }
            }

            result.Add(projectRef);
        }

        return result;
    }

    /// <summary>读取字符串列表。</summary>
    private static List<string> ReadStringList(JsonElement value)
    {
        var result = new List<string>();
        if (value.ValueKind != JsonValueKind.Array)
        {
            return result;
        }

        foreach (var item in value.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String)
            {
                result.Add(item.GetString()!);
            }
        }

        return result;
    }

    /// <summary>读取字符串字典。</summary>
    private static Dictionary<string, string>? ReadStringDictionary(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var prop in value.EnumerateObject())
        {
            if (prop.Value.ValueKind == JsonValueKind.String)
            {
                result[prop.Name] = prop.Value.GetString()!;
            }
        }

        return result;
    }

    /// <summary>备份单个文件到备份目录,返回备份路径;源文件不存在时返回 null。</summary>
    private static string? BackupFile(string sourcePath, string? backupDir)
    {
        if (backupDir is null || !File.Exists(sourcePath))
        {
            return null;
        }

        Directory.CreateDirectory(backupDir);
        string fileName = Path.GetFileName(sourcePath);
        string destPath = Path.Combine(backupDir, fileName);
        File.Copy(sourcePath, destPath, overwrite: true);
        return destPath;
    }

    /// <summary>计算文件 SHA256(十六进制大写);文件不存在时返回 null。</summary>
    private static string? ComputeSha256(string path)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        using var sha = SHA256.Create();
        using var fs = File.OpenRead(path);
        byte[] hash = sha.ComputeHash(fs);
        return Convert.ToHexString(hash);
    }

    /// <summary>读取迁移标记;不存在或损坏时返回 null。</summary>
    private static MigrationMarker? LoadMigrationMarker(string shadowRoot)
    {
        string markerPath = Path.Combine(shadowRoot, MigrationStateFileName);
        if (!File.Exists(markerPath))
        {
            return null;
        }

        try
        {
            string json = File.ReadAllText(markerPath);
            return JsonSerializer.Deserialize<MigrationMarker>(json, MarkerJsonOptions);
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>检查标记中的哈希是否与当前源文件一致。</summary>
    private static bool HashesMatch(MigrationMarker marker, string sourceName, string sourcePath)
    {
        if (marker.SourceHashes is null || !marker.SourceHashes.TryGetValue(sourceName, out string? recordedHash))
        {
            return false;
        }

        string? currentHash = ComputeSha256(sourcePath);
        return currentHash is not null && string.Equals(recordedHash, currentHash, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>写迁移标记。</summary>
    private static void WriteMigrationMarker(string shadowRoot, string legacyConfigPath, string legacyStatePath)
    {
        Directory.CreateDirectory(shadowRoot);
        var marker = new MigrationMarker
        {
            CompletedAt = DateTimeOffset.UtcNow.ToString("o"),
            SourceHashes = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["config.json"] = ComputeSha256(legacyConfigPath) ?? string.Empty,
                ["state.json"] = ComputeSha256(legacyStatePath) ?? string.Empty,
            },
        };

        string json = JsonSerializer.Serialize(marker, MarkerJsonOptions);
        string markerPath = Path.Combine(shadowRoot, MigrationStateFileName);
        string tmp = markerPath + ".tmp-" + Guid.NewGuid().ToString("N");
        File.WriteAllText(tmp, json, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        File.Move(tmp, markerPath, overwrite: true);
    }

    /// <summary>迁移标记数据结构。</summary>
    private sealed class MigrationMarker
    {
        public string CompletedAt { get; set; } = string.Empty;

        public Dictionary<string, string>? SourceHashes { get; set; }
    }
}