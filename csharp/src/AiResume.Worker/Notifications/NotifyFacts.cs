using System.Text;
using System.Text.Json;
using AiResume.Core;
using AiResume.Storage;
using AiResume.Worker.Products;
using AiResume.Worker.Resume;

namespace AiResume.Worker.Notifications;

/// <summary>会话尾部记录的这次限额中断:哪个窗口、服务端给的重置时间、会话最近实际用的模型。</summary>
public sealed record LimitFacts(string? LimitType, DateTimeOffset? ResetsAtUtc, string? Model);

/// <summary>
/// 布防里的一个项目:固定的会话、一次性布防这一轮是否已处理过它(续上或已在别处继续),
/// 以及引擎是否已判定它要重新布防才会续(账号变化、目标未绑定、模型认不出模型族)。
/// </summary>
public sealed record ArmedProject(string Path, string SessionId, bool Handled, bool NeedsRearm = false);

/// <summary>
/// 投递那一刻的布防快照。<paramref name="Stopped"/> = 本轮出现过不可自动重放的结果,
/// 必须解除并重新布防才会再续(<c>ReplayBlocked</c> 或项目状态落在成功/待续/已在别处继续/运行中之外);
/// 已布防但配置里 <c>enabled=false</c> 时引擎同样不续,也记为 Stopped。
/// </summary>
public sealed record ArmSnapshot(bool Armed, bool Continuous, bool Stopped, IReadOnlyList<ArmedProject> Projects)
{
    /// <summary>布防目标或账号已不对:要重新布防才会续(引擎只会一直等)。</summary>
    public const string HoldRearm = "rearm";

    /// <summary>Claude 登录失效、没有凭据或凭据挪进了 Windows 凭据管理器:/login 之前读不到额度,不会续。</summary>
    public const string HoldLogin = "login";

    /// <summary>续跑监视很久没探过额度(<see cref="AiResume.Worker.Products.EngineVerdict.Stalled"/>):会不会续说不准。</summary>
    public const string HoldStalled = "stalled";

    /// <summary>引擎此刻整体卡在哪(null = 没有):决定「会续跑」这句能不能说。</summary>
    public string? Hold { get; init; }
}

/// <summary>布防目标对应的供应商此刻的账号:agent、账号身份指纹、凭据不可用的原因码(都不含凭据实值)。</summary>
public sealed record ProviderAccount(string Agent, string Identity, string? CredentialFailure);

/// <summary>
/// 投递端给 Claude Code 通知定稿前要核对的外部事实:会话落没落盘、撞的是哪个限额、会话叫什么、AI Resume 布没布防。
/// 每一项都允许回答「不知道」(null):投递端据此不过滤、不合并,文案写「未核实」,绝不猜一个肯定的说法。
/// 生产实现 <see cref="NotifyFacts"/>;测试注入假实现。
/// </summary>
public interface INotifyFacts
{
    /// <summary>会话转录在不在:true 在;false 确认不在(Claude 的 projects 目录可读、文件没有);null 判断不了。</summary>
    bool? TranscriptExists(string transcriptPath);

    /// <summary>会话尾部这次限额中断的窗口与重置时间;最近一条记录不是限额中断或读不了 → null。</summary>
    LimitFacts? ReadLimit(string transcriptPath);

    /// <summary>会话名(桌面端侧栏标题、/rename);没有或读不了 → null。只进消息,不进日志。</summary>
    string? SessionName(string? sessionId, string? transcriptPath);

    /// <summary>布防快照;配置或状态库读不了 → null。</summary>
    ArmSnapshot? ReadArm();
}

/// <summary>
/// <see cref="INotifyFacts"/> 的生产实现。只读 Claude Code 公开落盘的产物(会话转录尾部、<c>sessions\&lt;pid&gt;.json</c> 登记)
/// 与 AI Resume 自己的 <c>config.json</c> / 状态库;不读对话正文,不写任何文件。
/// </summary>
public sealed class NotifyFacts : INotifyFacts
{
    private const int TitleWindowBytes = 1 << 20;
    private const int LimitCacheSize = 256;

    private readonly string _configPath;
    private readonly string _databasePath;
    private readonly string _claudeRoot;
    private readonly Func<string, ProviderAccount?> _accounts;
    private readonly Func<DateTimeOffset> _clock;

    // 组卡住(缺收件人、通道熔断)时每 15 秒都要重定周期:同一份转录没变就不重读尾部。
    private readonly Dictionary<string, (long Length, DateTime WrittenUtc, LimitFacts? Facts)> _limitCache =
        new(StringComparer.OrdinalIgnoreCase);

    /// <param name="accounts">按供应商 ID 读此刻的账号;null 用只读的 <see cref="AiResume.Worker.Probes.ProviderRegistry"/>。</param>
    public NotifyFacts(string? shadowRoot = null, string? claudeRoot = null, Func<string, ProviderAccount?>? accounts = null,
        Func<DateTimeOffset>? clock = null)
    {
        string root = shadowRoot ?? ShadowPaths.Root;
        _configPath = Path.Combine(root, "config.json");
        _databasePath = Path.Combine(root, "runs.db");
        _claudeRoot = claudeRoot ?? Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR") ??
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude");
        _accounts = accounts ?? (id => new AiResume.Worker.Probes.ProviderRegistry().Find(id) is { } connection
            ? new ProviderAccount(connection.Agent, connection.Identity, connection.CredentialFailure)
            : null);
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    /// <summary>
    /// 只在路径是 <c>…\projects\&lt;目录&gt;\&lt;会话&gt;.jsonl</c> 形状、且 projects 目录本身可读时才回答「不在」:
    /// 映射盘、权限、陌生路径形状都按判断不了处理,绝不因此吞掉真会话的通知。
    /// </summary>
    public bool? TranscriptExists(string transcriptPath)
    {
        try
        {
            string full = Path.GetFullPath(transcriptPath);
            if (File.Exists(full))
            {
                return true;
            }

            string? folder = Path.GetDirectoryName(full);
            string? projects = folder is null ? null : Path.GetDirectoryName(folder);
            if (projects is null ||
                !string.Equals(Path.GetFileName(projects), "projects", StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(Path.GetExtension(full), ".jsonl", StringComparison.OrdinalIgnoreCase) ||
                !Directory.Exists(projects))
            {
                return null;
            }

            // 目录在就逐项列一遍:File.Exists 对没有权限的文件也回答 false,列目录被拒会抛,能分清「没有」与「看不见」。
            if (Directory.Exists(folder))
            {
                return Directory.EnumerateFiles(folder!, Path.GetFileName(full)).Any();
            }

            // Directory.Exists 对查询被拒的目录同样回答 false:projects 里列得出它却进不去,就是看不见,不是没有。
            return Directory.EnumerateDirectories(projects, Path.GetFileName(folder!)).Any() ? null : false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or
                                       System.Security.SecurityException)
        {
            return null;
        }
    }

    /// <summary>重置时间超出前后一个月的当作没给(防溢出与离谱值);转录没变就复用上次的结论。</summary>
    public LimitFacts? ReadLimit(string transcriptPath)
    {
        FileInfo file;
        try
        {
            file = new FileInfo(transcriptPath);
            if (!file.Exists)
            {
                return null;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or
                                       System.Security.SecurityException)
        {
            return null;
        }

        lock (_limitCache)
        {
            if (_limitCache.TryGetValue(file.FullName, out var cached) &&
                cached.Length == file.Length && cached.WrittenUtc == file.LastWriteTimeUtc)
            {
                return cached.Facts;
            }
        }

        LimitFacts? facts = null;
        if (ClaudeSessionEvidence.TryRead(file.FullName) is { InterruptedByLimit: true } evidence)
        {
            DateTimeOffset now = _clock();
            DateTimeOffset? resetsAt = evidence.LimitResetsAtUtc is { } reset && reset > now.AddDays(-31) && reset < now.AddDays(31)
                ? reset
                : null;
            facts = new LimitFacts(evidence.LimitType, resetsAt, evidence.LastModel);
        }

        lock (_limitCache)
        {
            if (_limitCache.Count >= LimitCacheSize)
            {
                _limitCache.Clear();
            }

            _limitCache[file.FullName] = (file.Length, file.LastWriteTimeUtc, facts);
        }

        return facts;
    }

    /// <summary>先找进程登记(<c>sessions\&lt;pid&gt;.json</c> 的 <c>name</c>),会话已关时退回转录尾部最近一条 <c>custom-title</c>。</summary>
    public string? SessionName(string? sessionId, string? transcriptPath)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return null;
        }

        return RegistryName(sessionId, ClaudeRootOf(transcriptPath) ?? _claudeRoot) ??
               (string.IsNullOrWhiteSpace(transcriptPath) ? null : TranscriptTitle(transcriptPath, sessionId));
    }

    /// <summary>
    /// 配置读不了、损坏,或已布防但状态库读不了 → null(不能把「不知道」说成「未布防」)。
    /// 已布防时另判引擎此刻是否只会一直等:目标或账号不对(要重新布防)、Claude 登录失效、续跑监视卡住。
    /// </summary>
    public ArmSnapshot? ReadArm()
    {
        ProductConfig? config = ReadConfig();
        if (config is null)
        {
            return null;
        }

        if (!config.Armed || string.IsNullOrEmpty(config.ArmCycleId))
        {
            return new ArmSnapshot(false, config.Continuous, false, []);
        }

        // 已布防却 enabled=false(手改或更早的配置):引擎不会续,界面仍显示已布防——按本轮已停止说,重新布防即恢复。
        if (!config.Enabled)
        {
            return new ArmSnapshot(true, config.Continuous, true, []);
        }

        CheckerState state;
        try
        {
            // 已布防却没有状态库:不知道这一轮走到哪了。也不能顺手打开——SQLite 会就地建一个空库。
            if (!File.Exists(_databasePath))
            {
                return null;
            }

            state = new ProductStateStore(_databasePath).LoadStrict();
        }
        catch (Exception)
        {
            return null;
        }

        // 状态库还停在上一轮(新一轮刚布防、引擎还没初始化):上一轮的结论都不算数。
        bool sameCycle = string.Equals(state.CycleId, config.ArmCycleId, StringComparison.Ordinal);
        bool stopped = sameCycle && (state.ReplayBlocked || state.Phase == CheckerState.PhaseBlocked ||
            state.ProjectStatus?.Values.Any(status => status is not ("success" or "limited" or "continued-elsewhere" or "running")) == true);
        ArmedProject[] projects = config.Selected
            .Select(project =>
            {
                ProjectNote? note = sameCycle ? ProjectPaths.Find(state.ProjectNotes, project.Path) : null;
                return new ArmedProject(project.Path, project.SessionId,
                    sameCycle && ProjectPaths.Find(state.ProjectStatus, project.Path) is "success" or "continued-elsewhere",
                    // 与界面「需要重新布防」同一判据(index.html 的 beaconModel):不会自己好的额度原因。
                    note is { Reason: "quota:provider-changed" or "quota:target-unbound" } or
                        { Reason: "quota:unknown", Detail: "model-invalid" });
            })
            .ToArray();
        string? hold = TargetHold(config) ??
            (sameCycle && state.LastProbeReason == "target-unbound" ? ArmSnapshot.HoldRearm : null);
        if (hold is null && sameCycle &&
            EngineLiveness.Evaluate(true, true, state.LastProbeUtc, _clock(), config.ProbeIntervalMinutes,
                state.Phase == CheckerState.PhaseResuming || !string.IsNullOrEmpty(state.ActiveRunId)) == EngineVerdict.Stalled)
        {
            hold = ArmSnapshot.HoldStalled;
        }

        return new ArmSnapshot(true, config.Continuous, stopped, projects) { Hold = hold };
    }

    /// <summary>
    /// 布防目标此刻还成不成立(与续跑取证同一判据:<c>ProviderResumeProbe</c> / <c>ProviderCatalogService.CheckModelsAsync</c>):
    /// 目标无效、供应商不在了、agent 或账号身份变了 → 要重新布防;Claude 登录失效 → /login 之前不会续。
    /// 账号读不出(令牌待续期、全局配置正被改写)只是等待,不下结论。
    /// </summary>
    private string? TargetHold(ProductConfig config)
    {
        if (config.ResumeTarget is not { IsValid: true } target || target.ModelId != config.ResumeModel)
        {
            return ArmSnapshot.HoldRearm;
        }

        ProviderAccount? account;
        try
        {
            account = _accounts(target.ProviderId);
        }
        catch (Exception)
        {
            return null;
        }

        if (account is null || account.Agent != target.Agent ||
            (account.Identity.Length > 0 && !string.Equals(account.Identity, target.ProviderIdentity, StringComparison.Ordinal)))
        {
            return ArmSnapshot.HoldRearm;
        }

        return account.CredentialFailure is "login_expired" or "no_credentials" or "credential_store_moved"
            ? ArmSnapshot.HoldLogin
            : null;
    }

    private ProductConfig? ReadConfig()
    {
        try
        {
            if (!File.Exists(_configPath))
            {
                return ProductConfig.CreateDefault();
            }

            using var stream = new FileStream(_configPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            string json = reader.ReadToEnd();
            return string.IsNullOrWhiteSpace(json)
                ? null
                : JsonSerializer.Deserialize<ProductConfig>(json, ProductConfig.JsonOptions);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    /// <summary>转录路径 <c>&lt;根&gt;\projects\&lt;目录&gt;\&lt;会话&gt;.jsonl</c> → 根;形状不对返回 null。</summary>
    internal static string? ClaudeRootOf(string? transcriptPath)
    {
        try
        {
            string? projects = string.IsNullOrWhiteSpace(transcriptPath)
                ? null
                : Path.GetDirectoryName(Path.GetDirectoryName(Path.GetFullPath(transcriptPath)));
            return projects is not null && string.Equals(Path.GetFileName(projects), "projects", StringComparison.OrdinalIgnoreCase)
                ? Path.GetDirectoryName(projects)
                : null;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or System.Security.SecurityException or IOException)
        {
            return null;
        }
    }

    private static string? RegistryName(string sessionId, string claudeRoot)
    {
        try
        {
            string registry = Path.Combine(claudeRoot, "sessions");
            if (!Directory.Exists(registry))
            {
                return null;
            }

            foreach (string path in Directory.EnumerateFiles(registry, "*.json"))
            {
                try
                {
                    using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                    using JsonDocument doc = JsonDocument.Parse(stream);
                    JsonElement row = doc.RootElement;
                    if (row.ValueKind == JsonValueKind.Object &&
                        row.TryGetProperty("sessionId", out JsonElement id) && id.ValueKind == JsonValueKind.String &&
                        string.Equals(id.GetString(), sessionId, StringComparison.Ordinal) &&
                        row.TryGetProperty("name", out JsonElement name) && name.ValueKind == JsonValueKind.String &&
                        !string.IsNullOrWhiteSpace(name.GetString()))
                    {
                        return name.GetString()!.Trim();
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
                {
                    // 正在被改写或已删除的登记:跳过这一个。
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }

        return null;
    }

    /// <summary>转录尾部最近一条 <c>{"type":"custom-title","customTitle":…,"sessionId":…}</c>;只读这一种元数据行。</summary>
    private static string? TranscriptTitle(string transcriptPath, string sessionId)
    {
        try
        {
            using var stream = new FileStream(transcriptPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            long start = Math.Max(0, stream.Length - TitleWindowBytes);
            stream.Seek(start, SeekOrigin.Begin);
            var buffer = new byte[stream.Length - start];
            int total = 0;
            while (total < buffer.Length)
            {
                int read = stream.Read(buffer, total, buffer.Length - total);
                if (read == 0)
                {
                    break;
                }

                total += read;
            }

            string[] lines = Encoding.UTF8.GetString(buffer, 0, total).Split('\n');
            for (int i = lines.Length - 1; i >= (start > 0 ? 1 : 0); i--)
            {
                if (!lines[i].Contains("\"custom-title\"", StringComparison.Ordinal))
                {
                    continue;
                }

                try
                {
                    using JsonDocument doc = JsonDocument.Parse(lines[i]);
                    JsonElement row = doc.RootElement;
                    if (row.ValueKind == JsonValueKind.Object &&
                        row.TryGetProperty("type", out JsonElement type) && type.ValueKind == JsonValueKind.String &&
                        type.GetString() == "custom-title" &&
                        (!row.TryGetProperty("sessionId", out JsonElement id) || id.ValueKind != JsonValueKind.String ||
                         string.Equals(id.GetString(), sessionId, StringComparison.Ordinal)) &&
                        row.TryGetProperty("customTitle", out JsonElement title) && title.ValueKind == JsonValueKind.String &&
                        !string.IsNullOrWhiteSpace(title.GetString()))
                    {
                        return title.GetString()!.Trim();
                    }
                }
                catch (JsonException)
                {
                    // 正在追加的半行:继续往前找。
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
        }

        return null;
    }
}
