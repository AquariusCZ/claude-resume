using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using AiResume.Core;
using AiResume.Secrets;
using AiResume.Storage;
using AiResume.Worker;
using AiResume.Worker.Migration;
using AiResume.Worker.Notifications;
using AiResume.Worker.Probes;
using AiResume.Worker.Products;
using AiResume.Worker.Quota;
using AiResume.Worker.Resume;
using AiResume.Wrapper;

namespace AiResume.Gui;

/// <summary>
/// S7-B 控制面数据桥。前端经 postMessage 发来 {"type":"...","id":"..."},
/// 本类返回同 id 的应答 JSON,由 MainWindow 回投给页面。
///
/// 设计约束(ADR-0003 §5.4):**任何请求都不得在 UI 线程上做 I/O**——
/// 全部处理在线程池执行,前端以骨架态先渲染、拿到应答再填充。
/// 只读:本类不写任何产品状态;项目索引落 shadow 目录,绝不触碰生产 AppDir。
/// </summary>
public sealed class ControlPlaneBridge
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly ProjectCatalog _catalog;
    private readonly Func<ProductConfig> _configFactory;
    private readonly NotificationRegistry _notificationRegistry;
    private readonly QuotaService _quota;
    private readonly ProductConfigStore _configStore;
    private readonly ProductStateStore _stateStore;
    private readonly Func<CancellationToken, Task<string?>>? _folderPicker;
    private readonly Func<CcConnectDaemonController> _daemonControllerFactory;
    private readonly Func<string, CutoverConfigCommand.GenerateResult> _cutoverGenerate;
    private readonly Func<string?> _hookExecutableResolver;
    private readonly ClaudeCredentialSource _claudeCredentials;
    private readonly Action<string, string> _probeFailureReporter;
    private readonly Action<string, string> _requestFailureReporter;
    private readonly Func<bool?> _engineProcessDetector;
    private readonly Func<string, bool?> _activeResumeDetector;
    private readonly string _cutoverConfigPath;
    private readonly bool _demoMode;
    private readonly Func<CancellationToken, Task<IReadOnlyList<ProviderObservation>>> _providerCatalog;
    private readonly Func<string, string, IReadOnlyList<string>, ProductConfig, CancellationToken, Task<BoundResumeSelection>> _resumeBinding;
    private int _cutoverInProgress;

    public bool IsCutoverInProgress => Volatile.Read(ref _cutoverInProgress) != 0;

    public ControlPlaneBridge(
        ProjectCatalog? catalog = null,
        Func<ProductConfig>? configFactory = null,
        NotificationRegistry? notificationRegistry = null,
        QuotaService? quotaService = null,
        ProductConfigStore? configStore = null,
        ProductStateStore? stateStore = null,
        Func<CancellationToken, Task<string?>>? folderPicker = null,
        Func<CcConnectDaemonController>? daemonControllerFactory = null,
        Func<string, CutoverConfigCommand.GenerateResult>? cutoverGenerate = null,
        string? cutoverConfigPath = null,
        Func<string?>? hookExecutableResolver = null,
        ClaudeCredentialSource? claudeCredentials = null,
        Action<string, string>? probeFailureReporter = null,
        Action<string, string>? requestFailureReporter = null,
        Func<bool?>? engineProcessDetector = null,
        Func<string, bool?>? activeResumeDetector = null,
        bool demoMode = false,
        Func<CancellationToken, Task<IReadOnlyList<ProviderObservation>>>? providerCatalog = null,
        Func<string, string, IReadOnlyList<string>, ProductConfig, CancellationToken, Task<BoundResumeSelection>>? resumeBinding = null)
    {
        _demoMode = demoMode;
        // 选目录必须回到 UI 线程弹原生对话框,由宿主窗口注入;测试注入替身,不弹窗。
        _folderPicker = folderPicker;
        _daemonControllerFactory = daemonControllerFactory ?? (() => new CcConnectDaemonController());
        _cutoverGenerate = cutoverGenerate ?? (path => CutoverConfigCommand.Generate(
            path, appId: null, appSecret: null, requireLoadable: true));
        _hookExecutableResolver = hookExecutableResolver ?? HookExecutable.TryResolve;
        _probeFailureReporter = probeFailureReporter ?? ((_, _) => { });
        _requestFailureReporter = requestFailureReporter ?? ((_, _) => { });
        _engineProcessDetector = engineProcessDetector ?? (() => EngineLiveness.TryDetectEngineProcess());
        // 测试注入自有 stateStore 时默认不碰生产 process_registry;生产窗口才读现役数据库。
        _activeResumeDetector = activeResumeDetector ?? (stateStore is null
            ? runId => EngineLiveness.TryDetectActiveOwnedProcess(ShadowPaths.RunDatabasePath, runId)
            : _ => false);
        _cutoverConfigPath = cutoverConfigPath ?? CutoverConfigCommand.DefaultConfigPath;
        _quota = quotaService ?? new QuotaService();
        var providers = new ProviderCatalogService(quota: _quota);
        _providerCatalog = providerCatalog ?? providers.ReadAsync;
        // 长期令牌的保存/清除与供应商注册表共用同一个凭据解析器,面板上「现在靠哪份凭据」才和续跑一致。
        _claudeCredentials = claudeCredentials ?? providers.Registry.ClaudeCredentials;
        _resumeBinding = resumeBinding ?? new ResumeBindingService(providers).BindAsync;
        // 与 Worker 的续跑引擎读写同一份 shadow 配置/状态:GUI 布防 → 引擎消费。
        _configStore = configStore ?? new ProductConfigStore(ShadowPaths.Root);
        _stateStore = stateStore ?? new ProductStateStore(ShadowPaths.RunDatabasePath);
        // 索引落 shadow 目录:把 2227ms 的全量扫描降到 35-40ms(S7-A 实测)。
        _catalog = catalog ?? new ProjectCatalog(
            indexPath: Path.Combine(ShadowPaths.Root, "project-index.json"));
        // **必须读 shadow 配置**,不能用 CreateDefault:手动添加/移除的项目就存在
        // customProjects / hiddenProjects 里,读默认值等于每次开窗都把用户的增删丢掉。
        _configFactory = configFactory ?? (() => _configStore.Load());
        _notificationRegistry = notificationRegistry ?? new NotificationRegistry();
    }

    /// <summary>
    /// 处理一条前端请求并返回应答 JSON。业务异常转成 error 应答；调用方取消原样传播给宿主。
    /// </summary>
    public async Task<string> HandleAsync(string requestJson, CancellationToken cancellationToken)
    {
        string? id = null;
        string? type = null;
        try
        {
            using JsonDocument doc = JsonDocument.Parse(requestJson);
            JsonElement root = doc.RootElement;
            id = root.TryGetProperty("id", out JsonElement idEl) ? idEl.GetString() : null;
            type = root.TryGetProperty("type", out JsonElement tEl) ? tEl.GetString() : null;

            object payload = _demoMode ? DemoPayload(type) : type switch
            {
                "projects.list" => await Task.Run(() => ListProjects(), cancellationToken).ConfigureAwait(false),
                "projects.add" => await Task.Run(() => AddProject(root), cancellationToken).ConfigureAwait(false),
                "projects.remove" => await Task.Run(() => RemoveProject(root), cancellationToken).ConfigureAwait(false),
                "projects.restore" => await Task.Run(() => RestoreProjects(root), cancellationToken).ConfigureAwait(false),
                "dialog.pickFolder" => await PickFolderAsync(cancellationToken).ConfigureAwait(false),
                "feishu.status" => await Task.Run(() => FeishuStatus(), cancellationToken).ConfigureAwait(false),
                "feishu.save" => await Task.Run(() => SaveFeishu(root), cancellationToken).ConfigureAwait(false),
                "feishu.clear" => await Task.Run(() => ClearFeishu(), cancellationToken).ConfigureAwait(false),
                "feishu.verify" => await VerifyFeishuAsync(cancellationToken).ConfigureAwait(false),
                "cutover.generate" => await Task.Run(() => GenerateAndRestartCutover(), cancellationToken).ConfigureAwait(false),
                "cutover.preflight" => await Task.Run(() => Preflight(), cancellationToken).ConfigureAwait(false),
                "app.info" => AppInfo(),
                "notifications.list" => await Task.Run(() => ListNotifications(), cancellationToken).ConfigureAwait(false),
                "notifications.setEnabled" => await Task.Run(() => SetNotificationEnabled(root), cancellationToken).ConfigureAwait(false),
                "quota.get" => await GetQuotaAsync(root, cancellationToken).ConfigureAwait(false),
                "quota.local" => await Task.Run(() => GetLocalBlock(), cancellationToken).ConfigureAwait(false),
                "arm.get" => await Task.Run(() => GetArm(), cancellationToken).ConfigureAwait(false),
                "arm.set" => await Task.Run(() => SetArmAsync(root, cancellationToken), cancellationToken).ConfigureAwait(false),
                "providers.catalog" => new { items = (await _providerCatalog(cancellationToken).ConfigureAwait(false)).Select(p => p.ToPayload()) },
                "claude.token.status" => await Task.Run(() => ClaudeTokenStatus(), cancellationToken).ConfigureAwait(false),
                "claude.token.save" => await Task.Run(() => SaveClaudeToken(root), cancellationToken).ConfigureAwait(false),
                "claude.token.clear" => await Task.Run(() => ClearClaudeToken(), cancellationToken).ConfigureAwait(false),
                "agent.get" => await Task.Run(() => GetAgent(), cancellationToken).ConfigureAwait(false),
                "agent.set" => await Task.Run(() => SetAgent(root), cancellationToken).ConfigureAwait(false),
                _ => throw new NotSupportedException($"未知请求类型:{type}"),
            };

            return Serialize(new Envelope(id, type + ".result", payload, null));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            ReportRequestFailure(type ?? "unknown", ex);
            return Serialize(new Envelope(
                id,
                (type ?? "unknown") + ".error",
                null,
                SecretRedactor.RedactText(ex.Message)));
        }
    }

    private static object DemoPayload(string? type)
    {
        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        string[] selected = [@"D:\Demo\ledger-api", @"D:\Demo\atlas-docs", @"D:\Demo\pixel-sprites"];
        return type switch
        {
            "projects.list" => new
            {
                items = new object[]
                {
                    new { name = "orbit-planner", path = @"D:\Demo\orbit-planner", lastUsed = "已完成", isGit = true, isCustom = false },
                    new { name = "ledger-api", path = @"D:\Demo\ledger-api", lastUsed = "等额度", isGit = true, isCustom = false },
                    new { name = "atlas-docs", path = @"D:\Demo\atlas-docs", lastUsed = "2026/8/7 23:05", isGit = true, isCustom = false },
                    new { name = "pixel-sprites", path = @"D:\Demo\pixel-sprites", lastUsed = "2026/8/7 18:31", isGit = false, isCustom = true },
                    new { name = "kettle-firmware", path = @"D:\Demo\kettle-firmware", lastUsed = "2026/8/7 11:02", isGit = true, isCustom = false },
                    new { name = "tide-charts", path = @"D:\Demo\tide-charts", lastUsed = "2026/8/6 21:44", isGit = true, isCustom = false },
                },
                elapsedMs = 37,
                hidden = Array.Empty<object>(),
            },
            "quota.local" => new
            {
                active = true,
                startUnix = now - 9_000,
                endUnix = now + 9_000,
                totalTokens = 18_420,
                messageCount = 6,
                elapsedMs = 21,
            },
            "quota.get" => new
            {
                provider = "claude",
                capturedAtUnix = now,
                hasData = true,
                allowed = false,
                limitReached = true,
                unattributedLimitReached = false,
                globalHasCurrentData = true,
                globalHasCarried = false,
                globalLimitReached = false,
                unavailableReason = (string?)null,
                storageWarning = (string?)null,
                windows = new object[]
                {
                    new { name = "five_hour", label = "5 小时", status = "available", usedPercent = 41, windowSeconds = 18_000, resetAtUnix = now + 9_000, resetAfterSeconds = 9_000, windowStartUnix = now - 9_000, carriedForward = false },
                    new { name = "seven_day", label = "7 天", status = "available", usedPercent = 88, windowSeconds = 604_800, resetAtUnix = now + 180_000, resetAfterSeconds = 180_000, windowStartUnix = now - 424_800, carriedForward = false },
                    new { name = "weekly_scoped:fable", label = "Fable", status = "blocked", usedPercent = 100, windowSeconds = 604_800, resetAtUnix = now + 180_000, resetAfterSeconds = 180_000, windowStartUnix = now - 424_800, carriedForward = false },
                },
            },
            "providers.catalog" => new { items = new object[] {
                new { id = "claude-subscription", name = "Claude · Anthropic 订阅", agent = "claudecode", kind = "claude-oauth", models = new[] { new { id = "claude-fable-5-1", name = "Claude Fable 5.1" }, new { id = "claude-opus-5", name = "Claude Opus 5" } }, modelsVerified = true, source = "供应商实时返回", observedAtUtc = DateTimeOffset.UtcNow, quotaKnown = true, limited = true, resumeNote = "布防后固定调用 ID", windows = new[] { new { name = "Fable", usedPercent = 100, carriedForward = false } } },
                new { id = "deepseek", name = "DeepSeek · 官方 API", agent = "claudecode", kind = "deepseek", models = new[] { new { id = "deepseek-v4-flash", name = "deepseek-v4-flash" } }, modelsVerified = true, source = "供应商实时返回", observedAtUtc = DateTimeOffset.UtcNow, quotaKnown = true, limited = false, balance = 47.77, currency = "CNY", resumeNote = "固定调用 ID；同一 ID 的后端可能更新" },
                new { id = "codex-official", name = "Codex · ChatGPT 官方", agent = "codex", kind = "chatgpt", credentialSource = "auth.json:chatgpt", models = new[] { new { id = "gpt-5.6-sol", name = "GPT-5.6 Sol" } }, modelsVerified = true, source = "供应商实时返回", observedAtUtc = DateTimeOffset.UtcNow, quotaKnown = true, limited = false, resumeNote = "账号窗口与模型可用性来自 ChatGPT 官方接口", windows = new object[] { new { name = "primary · 7D", usedPercent = 12, resetAtUnix = now + 500_000, windowSeconds = 604_800, carriedForward = false, status = "allowed" } } }
            } },
            "claude.token.status" => new
            {
                hasStoredToken = true,
                storedTokenSavedAtUnix = now - 86_400,
                cliLoggedIn = false,
                cliExpiresAtUnix = (long?)null,
                cliProblem = "Claude CLI 未登录",
                activeSource = "ai-resume-token",
                problem = (string?)null,
                storedRejectedReason = (string?)null,
                storedProblem = (string?)null,
            },
            "arm.get" => new
            {
                armed = !Environment.GetCommandLineArgs().Contains("--screenshot-provider"),
                continuous = true,
                resumeModel = "claude-fable-5-1",
                resumeTarget = new ResumeTarget { ProviderId = "claude-subscription", ProviderName = "Claude · Anthropic 订阅", ProviderIdentity = "demo", Agent = "claudecode", ModelId = "claude-fable-5-1", ModelName = "Claude Fable 5.1", BoundAtUtc = DateTimeOffset.UtcNow },
                cycleId = "demo-cycle",
                phase = "waiting",
                sawLimited = true,
                selected,
                projectStatus = selected
                    .Select(path => DescribeProjectStatus(path, "limited"))
                    .ToArray(),
                engine = "Healthy",
                engineText = "运行中",
                probeAgeSeconds = 12,
            },
            "feishu.status" => new
            {
                hasCredentials = false,
                appIdMasked = (string?)null,
                allowFrom = (string?)null,
                configPath = @"D:\Demo\.cc-connect\config.toml",
                configExists = true,
                configState = "ok",
                configSummary = "配置可加载",
                configProblems = Array.Empty<string>(),
                configWarnings = Array.Empty<string>(),
            },
            "notifications.list" => new
            {
                items = new object[]
                {
                    DemoNotification("ClaudeCode", "Claude Code"),
                    DemoNotification("Codex", "Codex"),
                    DemoNotification("Cline", "Cline"),
                    DemoNotification("Qoder", "Qoder"),
                    DemoNotification("OpenCode", "OpenCode"),
                },
            },
            "app.info" => new { version = "2.0.0", shadowRoot = @"D:\Demo\AI Resume\state", quotaRefreshMinutes = 15 },
            "agent.get" => new
            {
                current = "claudecode",
                options = new object[]
                {
                    new { id = "claudecode", display = "Claude Code", installed = true },
                    new { id = "codex", display = "Codex", installed = true },
                    new { id = "qoder", display = "Qoder", installed = true },
                    new { id = "opencode", display = "OpenCode", installed = true },
                },
            },
            _ => throw new NotSupportedException($"截图合成模式不支持请求:{type}"),
        };
    }

    private static object DemoNotification(string kind, string displayName) => new
    {
        kind,
        displayName,
        isInstalled = true,
        isEnabled = true,
        configPath = @"D:\Demo\notifications",
        detail = "已安装 AI Resume 通知钩子",
        hookBroken = false,
    };

    private ProjectsPayload ListProjects()
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        ProductConfig config = _configFactory();
        List<ProjectEntry> entries = _catalog.Discover(config);
        sw.Stop();

        var items = entries.Select(e => new ProjectItem(
            e.Name,
            e.Path,
            e.LastWriteUtc == DateTimeOffset.MinValue ? null : e.LastWriteUtc.ToLocalTime().ToString("yyyy/M/d HH:mm"),
            Directory.Exists(Path.Combine(e.Path, ".git")),
            e.IsCustom)).ToList();

        // 目录已经不在了的条目直接不列:这份名单的唯一作用是「扫描时跳过它」,
        // 而不存在的目录本来就扫不出来 —— 留着只是让「不再扫描 N」这个数字虚高,
        // 用户看见一串早就删掉的目录名,会以为是清不掉的历史记录。
        var hidden = config.HiddenProjects
            .Where(h => !string.IsNullOrWhiteSpace(h) && Directory.Exists(h))
            .Select(h => new HiddenItem(SafeLeaf(h), h))
            .ToList();

        return new ProjectsPayload(items, sw.ElapsedMilliseconds, hidden);
    }

    /// <summary>
    /// 手动添加项目。写 <c>customProjects</c> 并从 <c>hiddenProjects</c> 移除——
    /// 「添加」与「移除」必须互为逆操作,否则移除过的目录再添加会被隐藏名单挡住,
    /// 表现为"点了添加却什么都没发生"。
    /// </summary>
    private ProjectsPayload AddProject(JsonElement root)
    {
        string full = NormalizeRequestPath(root);

        if (!Directory.Exists(full))
        {
            throw new DirectoryNotFoundException($"目录不存在:{full}");
        }

        if (_catalog.IsReserved(full))
        {
            throw new ArgumentException($"该目录属于系统或程序自身目录,不能作为项目:{full}");
        }

        _configStore.Update(config =>
        {
            config.HiddenProjects.RemoveAll(h => PathEquals(h, full));
            if (!config.CustomProjects.Any(c => PathEquals(c.Path, full)))
            {
                config.CustomProjects.Add(new ProjectRef { Name = SafeLeaf(full), Path = full });
            }
        });

        _catalog.ClearCache();
        return ListProjects();
    }

    /// <summary>
    /// 从队列移除项目:退出 <c>customProjects</c> 并进入 <c>hiddenProjects</c>。
    /// 两件事都要做——手动添加的目录同时也可能被会话扫描发现,只删 custom 会让它立刻又冒出来。
    ///
    /// 同时把它移出 <c>selected</c>:否则用户在界面上删掉的项目,引擎照样会去续跑。
    /// 若移除后已布防的选择清空,则连带解除布防(布防零个项目没有意义)。
    /// </summary>
    private ProjectsPayload RemoveProject(JsonElement root)
    {
        string full = NormalizeRequestPath(root);

        _configStore.Update(config =>
        {
            config.CustomProjects.RemoveAll(c => PathEquals(c.Path, full));
            if (!config.HiddenProjects.Any(h => PathEquals(h, full)))
            {
                config.HiddenProjects.Add(full);
            }

            config.Selected.RemoveAll(s => PathEquals(s.Path, full));
            if (config.Armed && config.Selected.Count == 0)
            {
                config.Armed = false;
                config.ArmCycleId = string.Empty;
            }
        });

        _catalog.ClearCache();
        return ListProjects();
    }

    /// <summary>恢复被移除的项目;带 path 时恢复单个,不带则全部恢复。</summary>
    private ProjectsPayload RestoreProjects(JsonElement root)
    {
        string? one = root.TryGetProperty("path", out JsonElement pathEl) && pathEl.ValueKind == JsonValueKind.String
            ? pathEl.GetString()
            : null;

        _configStore.Update(config =>
        {
            if (string.IsNullOrWhiteSpace(one))
            {
                config.HiddenProjects.Clear();
            }
            else
            {
                config.HiddenProjects.RemoveAll(h => PathEquals(h, one));
            }
        });

        _catalog.ClearCache();
        return ListProjects();
    }

    /// <summary>弹原生选目录对话框;宿主未注入选择器(如自测/无窗口环境)时明确报错而非静默返回空。</summary>
    private async Task<PickFolderPayload> PickFolderAsync(CancellationToken cancellationToken)
    {
        if (_folderPicker is null)
        {
            throw new NotSupportedException("当前宿主未提供目录选择器。");
        }

        string? picked = await _folderPicker(cancellationToken).ConfigureAwait(false);
        return new PickFolderPayload(string.IsNullOrWhiteSpace(picked) ? null : picked);
    }

    private static string NormalizeRequestPath(JsonElement root)
    {
        string raw = root.TryGetProperty("path", out JsonElement el) && el.ValueKind == JsonValueKind.String
            ? el.GetString() ?? string.Empty
            : string.Empty;

        if (string.IsNullOrWhiteSpace(raw))
        {
            throw new ArgumentException("缺少项目路径。");
        }

        try
        {
            return Path.GetFullPath(raw.Trim());
        }
        catch (Exception ex)
        {
            throw new ArgumentException($"路径无法解析:{raw}", ex);
        }
    }

    /// <summary>路径比较:规范化后大小写不敏感、忽略尾部分隔符(Windows 语义,与发现阶段一致)。</summary>
    private static bool PathEquals(string? a, string? b)
    {
        if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b))
        {
            return false;
        }

        static string Norm(string p)
        {
            try
            {
                return Path.GetFullPath(p).TrimEnd('\\', '/');
            }
            catch (Exception)
            {
                return p.Trim().TrimEnd('\\', '/');
            }
        }

        return string.Equals(Norm(a), Norm(b), StringComparison.OrdinalIgnoreCase);
    }

    private static string SafeLeaf(string path)
    {
        try
        {
            string leaf = Path.GetFileName(path.TrimEnd('\\', '/'));
            return string.IsNullOrEmpty(leaf) ? path : leaf;
        }
        catch (Exception)
        {
            return path;
        }
    }

    private AppInfoPayload AppInfo()
    {
        int refresh = 15;
        try
        {
            int configured = _configStore.Load().QuotaRefreshMinutes;
            if (configured >= 1)
            {
                refresh = configured;
            }
        }
        catch (Exception)
        {
            // 配置读不出来就用默认节奏,不让版本信息这类次要请求失败。
        }

        return new AppInfoPayload(
            typeof(ControlPlaneBridge).Assembly.GetName().Version?.ToString(3) ?? "dev",
            ShadowPaths.Root,
            refresh);
    }

    /// <summary>
    /// 探测所有通知提供程序,返回与 notifications.list 相同形状的负载。
    /// 在线程池执行(由调用方 Task.Run 保证)。
    /// </summary>
    private NotificationsPayload ListNotifications()
    {
        var statuses = _notificationRegistry.ProbeAll();
        var items = statuses.Select(s => new NotificationItem(
            s.Kind.ToString(),
            s.DisplayName,
            s.IsInstalled,
            s.IsEnabled,
            s.ConfigPath,
            s.Detail,
            // 「已启用但送不到」必须能被界面单独表达。只回 isEnabled 的话,
            // 钩子程序被删之后开关照旧是绿的(2026-08-08 审计 A1)。
            s.HookBroken)).ToList();
        return new NotificationsPayload(items);
    }

    /// <summary>
    /// 启用/停用指定通知提供程序,返回最新列表。
    /// 在线程池执行(由调用方 Task.Run 保证)。
    /// </summary>
    private NotificationsPayload SetNotificationEnabled(JsonElement root)
    {
        // 解析 kind 字符串为枚举;失败时抛异常由外层 catch 转 error 应答。
        string kindStr = root.TryGetProperty("kind", out JsonElement kindEl) ? kindEl.GetString() ?? "" : "";
        if (!Enum.TryParse<NotificationProviderKind>(kindStr, ignoreCase: true, out var kind))
        {
            throw new ArgumentException($"未知的通知提供程序类型: '{kindStr}'");
        }

        bool enabled = root.TryGetProperty("enabled", out JsonElement enabledEl) && enabledEl.GetBoolean();

        // 注册表契约是“可执行文件路径”,各适配器再按自己的配置格式追加固定 source。
        // GUI 不能提前拼 source:Cline/OpenCode 会把整串当成一个文件名,再追加一次参数。
        // 停用完全不需要 Hook 文件;即使安装目录被手工删坏,用户也必须仍能关闭坏开关。
        string hookExe = string.Empty;
        if (enabled)
        {
            hookExe = _hookExecutableResolver()
                ?? throw new FileNotFoundException(
                    $"未找到 {HookExecutable.FileName}。请先构建 AiResume.Hook 项目;" +
                    "钩子必须写绝对路径,写不存在的路径会表现为「已启用但永远收不到通知」。");
        }

        _notificationRegistry.SetEnabled(kind, enabled, hookExe);

        // 把开关记进配置。这条记录是重装后恢复通知源的**唯一依据**——
        // 卸载会把 ~/.claude 之类里的现状清空,清空之后就再没有东西能说出
        // "本来开着哪几个"(2026-08-08 审计 B3)。记不下来不影响本次开关生效,所以不抛。
        try
        {
            _configStore.Update(c => c.NotifySources = NotifyIntent.Toggle(c.NotifySources, kind, enabled));
        }
        catch (Exception)
        {
        }

        // 返回与 notifications.list 相同形状的最新列表。
        return ListNotifications();
    }

    /// <summary>
    /// 取额度快照。探测约 7 秒且已在 <see cref="QuotaService"/> 内做了缓存与单航班,
    /// 这里只做形状转换;前端先渲染骨架、拿到应答再填充。
    /// </summary>
    private async Task<QuotaPayload> GetQuotaAsync(JsonElement root, CancellationToken cancellationToken)
    {
        bool force = root.TryGetProperty("force", out JsonElement forceEl)
                     && forceEl.ValueKind == JsonValueKind.True;

        UsageSnapshot snapshot = await _quota.GetAsync(force, cancellationToken).ConfigureAwait(false);
        UsageBucket? bucket = snapshot.Buckets.FirstOrDefault();
        QuotaGlobalFacts global = ClassifyGlobalQuota(snapshot);

        var windows = (bucket?.Windows ?? Array.Empty<UsageWindow>()).Select(w => new QuotaWindow(
            w.Name,
            DescribeWindow(w.Name),
            w.Status,
            w.UsedPercent,
            w.WindowSeconds,
            w.ResetAtUnix,
            w.ResetAfterSeconds,
            w.DerivedWindowStart?.ToUnixTimeSeconds(),
            w.CarriedForward)).ToList();

        return new QuotaPayload(
            snapshot.Provider,
            snapshot.CapturedAt.ToUnixTimeSeconds(),
            snapshot.HasData,
            bucket?.Allowed ?? false,
            bucket?.LimitReached ?? false,
            global.UnattributedLimitReached,
            global.HasCurrentData,
            global.HasCarried,
            global.LimitReached,
            snapshot.UnavailableReason,
            _quota.StorageWarning,
            windows);
    }

    /// <summary>
    /// 把安全聚合 bucket 拆成 GUI 的账户主窗口事实。只有 5H/7D 两窗都来自本次
    /// 成功观测且未满时才可显示正常；scoped 满额留给模型行，未归因限流仍归总行。
    /// </summary>
    internal static QuotaGlobalFacts ClassifyGlobalQuota(UsageSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        UsageBucket? bucket = snapshot.Buckets.FirstOrDefault();
        UsageWindow? fiveHour = bucket?.Windows.FirstOrDefault(window =>
            window.Name.Equals("five_hour", StringComparison.OrdinalIgnoreCase));
        UsageWindow? sevenDay = bucket?.Windows.FirstOrDefault(window =>
            window.Name.Equals("seven_day", StringComparison.OrdinalIgnoreCase));
        UsageWindow?[] globalWindows = [fiveHour, sevenDay];
        bool hasCurrentData = snapshot.UnavailableReason is null && snapshot.HasData &&
            globalWindows.All(window =>
                window is { UsedPercent: not null, CarriedForward: false } &&
                !IsBlockedQuotaWindow(window));
        bool hasCarried = globalWindows.Any(window => window?.CarriedForward == true);
        bool unattributedLimitReached = bucket?.UnattributedLimitReached == true ||
            (bucket is { LimitReached: true } && !bucket.Windows.Any(IsBlockedQuotaWindow));
        bool limitReached = globalWindows.Any(window =>
            window is not null && IsBlockedQuotaWindow(window)) || unattributedLimitReached;
        return new QuotaGlobalFacts(
            hasCurrentData,
            hasCarried,
            limitReached,
            unattributedLimitReached);
    }

    private static bool IsBlockedQuotaWindow(UsageWindow window) =>
        window.Status.Equals("blocked", StringComparison.OrdinalIgnoreCase) ||
        window.UsedPercent is >= 100;

    /// <summary>读当前布防周期与逐项目进度。</summary>
    private ArmPayload GetArm()
    {
        ArmReadSnapshot snapshot = _configStore.ReadLocked(config =>
        {
            try
            {
                return new ArmReadSnapshot(config, _stateStore.LoadStrict(), null);
            }
            catch (Exception ex)
            {
                return new ArmReadSnapshot(config, null, ex);
            }
        });

        ProductConfig config = snapshot.Config;
        Exception? readError = snapshot.Error;
        if (readError is not null || snapshot.State is null)
        {
            ReportRequestFailure("arm.get", readError ?? new InvalidDataException("布防状态快照为空。"));
            var unreadableSelected = config.Selected.Select(p => p.Path).ToList();
            EngineVerdict unreadableVerdict = config.Armed && _engineProcessDetector() == false
                ? EngineVerdict.NotRunning
                : EngineVerdict.StateUnverified;
            return new ArmPayload(
                config.Armed,
                config.Continuous,
                config.ResumeModel,
                config.ArmCycleId,
                string.Empty,
                false,
                unreadableSelected,
                Array.Empty<ProjectStatusItem>(),
                unreadableVerdict.ToString(),
                EngineLiveness.Describe(unreadableVerdict),
                null, config.ResumeTarget);
        }

        CheckerState state = snapshot.State;
        var selected = config.Selected.Select(p => p.Path).ToList();

        bool currentCycle = config.Armed &&
            !string.IsNullOrEmpty(config.ArmCycleId) &&
            string.Equals(config.ArmCycleId, state.CycleId, StringComparison.Ordinal);
        var statuses = new List<ProjectStatusItem>();
        if (currentCycle && state.ProjectStatus is not null)
        {
            foreach (string selectedPath in selected)
            {
                KeyValuePair<string, string> match = state.ProjectStatus.FirstOrDefault(kv =>
                    PathEquals(kv.Key, selectedPath));
                if (match.Value is not null)
                {
                    statuses.Add(DescribeProjectStatus(selectedPath, match.Value));
                }
            }
        }

        // **布防是意图,不是事实。** 只回 armed 的话,续跑 Worker 被杀掉之后
        // 面板照旧写着「监视中」(2026-08-08 审计 A4)——而用户会真的去睡觉。
        DateTimeOffset now = DateTimeOffset.UtcNow;
        bool cancellationPending = currentCycle &&
            statuses.Any(s => s.Status == "cancel-pending");
        bool hasActiveRun = !string.IsNullOrEmpty(state.ActiveRunId);
        bool hasRunningClaim = currentCycle &&
            statuses.Any(s => s.Status is "running" or "cancel-pending");
        bool claimsWorkInProgress = currentCycle &&
            state.Phase == CheckerState.PhaseResuming &&
            hasActiveRun &&
            !string.IsNullOrEmpty(state.ActiveProjectPath) &&
            statuses.Any(s =>
                PathEquals(s.Path, state.ActiveProjectPath) &&
                s.Status is "running" or "cancel-pending");
        bool? activeRunEvidence = hasActiveRun
            ? _activeResumeDetector(state.ActiveRunId)
            : null;
        bool? engineRunning = _engineProcessDetector();
        EngineVerdict verdict = config.Armed && engineRunning is null
            ? EngineVerdict.StateUnverified
            : EngineLiveness.Evaluate(
                config.Armed,
                engineRunning == true,
                currentCycle ? state.LastProbeUtc : null,
                now,
                config.ProbeIntervalMinutes,
                (claimsWorkInProgress && activeRunEvidence == true) ||
                    (currentCycle && state.ReplayBlocked));
        bool canApplyRunVerdict = verdict != EngineVerdict.NotRunning &&
            !(config.Armed && verdict == EngineVerdict.StateUnverified);
        bool pendingRunBlocks = false;
        if (!string.IsNullOrEmpty(state.PendingCancellationRunId))
        {
            bool? pendingEvidence = _activeResumeDetector(state.PendingCancellationRunId);
            if (canApplyRunVerdict)
            {
                verdict = pendingEvidence switch
                {
                    true => EngineVerdict.CancelPending,
                    null => EngineVerdict.RunUnverified,
                    _ => verdict,
                };
                pendingRunBlocks = pendingEvidence != false;
            }
        }

        if (!pendingRunBlocks && cancellationPending && canApplyRunVerdict)
        {
            verdict = activeRunEvidence == false
                ? EngineVerdict.RunMissing
                : EngineVerdict.RunUnverified;
        }
        else if (!pendingRunBlocks && hasActiveRun && canApplyRunVerdict)
        {
            verdict = activeRunEvidence switch
            {
                true when !claimsWorkInProgress => EngineVerdict.RunActive,
                false => EngineVerdict.RunMissing,
                null => EngineVerdict.RunUnverified,
                _ => verdict,
            };
        }
        else if (!pendingRunBlocks && hasRunningClaim && canApplyRunVerdict)
        {
            // 项目行声称 running/cancel-pending 却没有精确 RunId,不能继续肯定地写“进行中”。
            verdict = EngineVerdict.RunMissing;
        }

        if (verdict == EngineVerdict.StateUnverified)
        {
            ReplaceClaimedRunningStatuses(statuses, "run-unverified");
        }
        else if (verdict == EngineVerdict.NotRunning && hasRunningClaim)
        {
            if (activeRunEvidence == false)
            {
                ReplaceClaimedRunningStatuses(statuses, "exit-null");
            }
            else if (activeRunEvidence is null)
            {
                ReplaceClaimedRunningStatuses(statuses, "run-unverified");
            }
        }
        else if (verdict == EngineVerdict.RunMissing)
        {
            if (hasActiveRun)
            {
                ReplaceProjectStatus(statuses, state.ActiveProjectPath, "exit-null");
            }
            else
            {
                ReplaceClaimedRunningStatuses(statuses, "exit-null");
            }
        }
        else if (verdict == EngineVerdict.RunUnverified)
        {
            string? affectedPath = !string.IsNullOrEmpty(state.PendingCancellationProjectPath)
                ? state.PendingCancellationProjectPath
                : state.ActiveProjectPath;
            ReplaceProjectStatus(statuses, affectedPath, "run-unverified");
        }

        if (!hasActiveRun && hasRunningClaim)
        {
            ReplaceClaimedRunningStatuses(statuses, "exit-null");
        }
        long? probeAge = currentCycle && state.LastProbeUtc is { } lp
            ? (long)Math.Max(0, (now - lp).TotalSeconds)
            : null;

        return new ArmPayload(
            config.Armed,
            config.Continuous,
            config.ResumeModel,
            config.ArmCycleId,
            currentCycle ? state.Phase : string.Empty,
            currentCycle && state.SawLimited,
            selected,
            statuses,
            verdict.ToString(),
            EngineLiveness.Describe(verdict),
            probeAge, config.ResumeTarget, currentCycle ? state.LastProbeReason : string.Empty);
    }

    private static ProjectStatusItem DescribeProjectStatus(string path, string? status)
    {
        string normalizedStatus = status ?? string.Empty;
        (string category, string text) = normalizedStatus switch
        {
            "success" => ("success", "已完成"),
            "limited" => ("waiting", "等额度"),
            "running" => ("running", "进行中"),
            "cancel-pending" => ("waiting", "终止待确认"),
            "stopped" => ("waiting", "已停止"),
            "error" => ("failure", "失败"),
            "no-claude" => ("failure", "Claude 或项目不可用"),
            "prompt-multiline" => ("failure", "续跑提示词无效"),
            "launch-error" => ("failure", "启动失败"),
            "configuration-changed" => ("waiting", "配置已改变，请重新布防"),
            "execution-unverified" => ("waiting", "执行条件未核实，请重新布防"),
            "registry-error" => ("failure", "进程登记失败"),
            "monitor-error" => ("failure", "进程监控失败"),
            "limited-side-effects" => ("failure", "限流前已执行操作"),
            "exit-null" => ("failure", "未确认完成"),
            "run-unverified" => ("waiting", "状态未核实"),
            _ when normalizedStatus.StartsWith("exit-", StringComparison.Ordinal) => ("failure", "进程异常退出"),
            _ => ("failure", "未知异常"),
        };

        return new ProjectStatusItem(path, normalizedStatus, category, text);
    }

    private static void ReplaceProjectStatus(
        List<ProjectStatusItem> statuses,
        string? projectPath,
        string replacementStatus)
    {
        if (string.IsNullOrEmpty(projectPath))
        {
            return;
        }

        int index = statuses.FindIndex(item => PathEquals(item.Path, projectPath));
        if (index >= 0)
        {
            statuses[index] = DescribeProjectStatus(statuses[index].Path, replacementStatus);
        }
    }

    private static void ReplaceClaimedRunningStatuses(
        List<ProjectStatusItem> statuses,
        string replacementStatus)
    {
        for (int index = 0; index < statuses.Count; index++)
        {
            if (statuses[index].Status is "running" or "cancel-pending")
            {
                statuses[index] = DescribeProjectStatus(statuses[index].Path, replacementStatus);
            }
        }
    }

    /// <summary>
    /// 布防/解除。布防时生成新的 <c>ArmCycleId</c>——周期 id 变化即让上一轮状态失效,
    /// 这是 <c>CheckerCycle</c> 隔离每次布防的唯一机制。
    ///
    /// **只改本次负责的字段后写回**:配置可能同时被 Worker 的续跑引擎修改(解除布防),
    /// 整体写回旧快照会互相覆盖。
    /// </summary>
    private async Task<ArmPayload> SetArmAsync(JsonElement root, CancellationToken ct)
    {
        bool armed = root.TryGetProperty("armed", out JsonElement armedEl) && armedEl.ValueKind == JsonValueKind.True;

        var paths = new List<string>();
        string resumeModel = string.Empty;
        BoundResumeSelection? binding = null;
        ProductConfig original = _configStore.Load();
        string originalIntent = JsonSerializer.Serialize(original, ProductConfig.JsonOptions);
        if (armed)
        {
            string? requestedModel = root.TryGetProperty("resumeModel", out JsonElement modelEl) &&
                modelEl.ValueKind == JsonValueKind.String
                    ? modelEl.GetString()
                    : null;
            if (requestedModel is null || !ProviderModels.IsSafeId(requestedModel))
            {
                throw new ArgumentException("请选择供应商目录中的有效续跑模型。");
            }

            if (root.TryGetProperty("paths", out JsonElement pathsEl) && pathsEl.ValueKind == JsonValueKind.Array)
            {
                paths.AddRange(pathsEl.EnumerateArray()
                    .Select(e => e.GetString())
                    .Where(s => !string.IsNullOrWhiteSpace(s))!);
            }

            if (paths.Count == 0)
            {
                throw new ArgumentException("未选择任何项目,无法布防。");
            }
            string providerId = root.TryGetProperty("providerId", out JsonElement providerEl) && providerEl.ValueKind == JsonValueKind.String
                ? providerEl.GetString()! : "claude-subscription";
            binding = await _resumeBinding(providerId, requestedModel, paths, original, ct).ConfigureAwait(false);
            resumeModel = binding.Target.ModelId;
        }

        // 锁内重读后只改本次负责的字段:续跑引擎可能正好在周期结束时写同一份配置。
        _configStore.Update(config =>
        {
            if (armed)
            {
                if (JsonSerializer.Serialize(config, ProductConfig.JsonOptions) != originalIntent)
                    throw new InvalidOperationException("布防期间设置已改变，请重试");
                config.Selected = binding!.Projects.ToList();
                config.ResumeTarget = binding.Target;

                config.Enabled = true;
                config.Armed = true;
                config.ResumeModel = resumeModel;
                config.ArmCycleId = Guid.NewGuid().ToString("N");

                if (root.TryGetProperty("continuous", out JsonElement contEl))
                {
                    config.Continuous = contEl.ValueKind == JsonValueKind.True;
                }
            }
            else
            {
                config.Armed = false;
                config.ArmCycleId = string.Empty;
            }
        });

        return GetArm();
    }

    /// <summary>
    /// 本地 5 小时块:直接读会话 jsonl 计算,**不起进程、毫秒级**,所以开窗即可渲染。
    /// 服务端的 rate_limit_event 是偶发下发的(实测常常只给 seven_day),
    /// 只等它会让 5 小时窗口时有时无——社区监视器(ccusage / Claude-Code-Usage-Monitor)
    /// 同样是本地算这个块。token 总量是本地事实,**不是**占限额的百分比(账户档位未知,不臆测)。
    /// </summary>
    private LocalBlockPayload GetLocalBlock()
    {
        string root = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", "projects");

        var sw = System.Diagnostics.Stopwatch.StartNew();
        UsageBlock? block = ClaudeUsageBlocks.FindActiveBlock(root, DateTimeOffset.UtcNow);
        sw.Stop();

        if (block is null)
        {
            return new LocalBlockPayload(false, null, null, 0, 0, sw.ElapsedMilliseconds);
        }

        return new LocalBlockPayload(
            true,
            block.StartUtc.ToUnixTimeSeconds(),
            block.EndUtc.ToUnixTimeSeconds(),
            block.TotalTokens,
            block.MessageCount,
            sw.ElapsedMilliseconds);
    }

    // ── 飞书接入(S10-D)──
    // **实值单向流动**:前端 → 桥 → DPAPI → cc-connect 配置文件。
    // 任何一个应答都只回"有没有"与遮蔽后的 app_id,app_secret 永不回传前端。

    private FeishuPayload FeishuStatus()
    {
        FeishuCredentialStatus status = new FeishuCredentialStore().Describe();
        if (status.HasCredentials)
        {
            SyncNotifyRecipient(status.AllowFrom);
        }

        // **文件在 ≠ cc-connect 加载得了。** 原来这里只回 File.Exists,
        // 于是配置被改坏之后界面照旧说"配置已生成"(2026-08-08 审计 A3)。
        // 校验走 cc-connect 自己的解析器,且只校验副本(见 CcConnectConfigValidator)。
        CcConnectConfigCheck check;
        try
        {
            check = CcConnectConfigValidator.CheckFile(CutoverConfigCommand.DefaultConfigPath);
        }
        catch (Exception ex)
        {
            ReportRequestFailure("feishu.status", ex);
            check = new CcConnectConfigCheck(
                CcConnectConfigState.Unknown,
                $"配置未能复核:{SecretRedactor.RedactText(ex.Message)}",
                Array.Empty<string>(), Array.Empty<string>());
        }

        return new FeishuPayload(
            status.HasCredentials,
            status.AppIdMasked,
            // 授权 open_id 不是口令,展示它才能让用户确认"锁的是我"。
            status.AllowFrom,
            CutoverConfigCommand.DefaultConfigPath,
            File.Exists(CutoverConfigCommand.DefaultConfigPath),
            check.State.ToString().ToLowerInvariant(),
            check.Summary,
            check.Problems.ToList(),
            check.Warnings.ToList());
    }

    /// <summary>
    /// 真实校验飞书凭据:拿它去换一次 tenant_access_token。
    ///
    /// 「DPAPI 里有值」只证明用户填过。secret 在开放平台被重置之后本机这份就永久失效,
    /// 而失效的表现是**机器人不理你** —— 和进程没起来、open_id 夹空格、钩子断链
    /// 长得一模一样。不真发一次请求,界面就没有能力把它们分开(审计 A2)。
    /// </summary>
    private async Task<object> VerifyFeishuAsync(CancellationToken cancellationToken)
    {
        FeishuVerifyResult r = await FeishuCredentialVerifier
            .VerifyAsync(cancellationToken).ConfigureAwait(false);

        // 只回结论与飞书原文的 code/msg(不是机密);app_secret 从不离开 DPAPI。
        return new
        {
            ok = r.Ok,
            verdict = r.Verdict.ToString(),
            code = r.Code,
            summary = r.Summary,
        };
    }

    private FeishuPayload SaveFeishu(JsonElement root)
    {
        string appId = root.TryGetProperty("appId", out JsonElement idEl) ? idEl.GetString() ?? "" : "";
        string appSecret = root.TryGetProperty("appSecret", out JsonElement secEl) ? secEl.GetString() ?? "" : "";
        string allowFrom = root.TryGetProperty("allowFrom", out JsonElement allowEl) ? allowEl.GetString() ?? "" : "";

        // 校验信息里绝不回显用户输入的内容(哪怕是错的那一份也可能是真凭据的手误)。
        if (string.IsNullOrWhiteSpace(appId) || string.IsNullOrWhiteSpace(appSecret))
        {
            throw new ArgumentException("App ID 与 App Secret 都必须填写。");
        }

        // fail-closed:allow_from 为空时 cc-connect 会放行所有飞书用户。
        if (string.IsNullOrWhiteSpace(allowFrom))
        {
            throw new ArgumentException("必须填写授权用户 open_id,否则任何人都能驱动本机 AI 改你的项目。");
        }

        new FeishuCredentialStore().Save(appId, appSecret, allowFrom);
        SyncNotifyRecipient(allowFrom);
        return FeishuStatus();
    }

    private FeishuPayload ClearFeishu()
    {
        new FeishuCredentialStore().Clear();
        try
        {
            _configStore.Update(c => c.NotifyRecipientOpenId = string.Empty);
        }
        catch (Exception ex)
        {
            ReportRequestFailure("feishu.recipient", ex);
        }
        return FeishuStatus();
    }


    /// <summary>
    /// 生成、验证并重启 cc-connect。凭据由 <see cref="CutoverConfigCommand"/> 从 DPAPI 取,
    /// 全流程在 <see cref="CcConnectApplyCommand"/>(与 CLI 共用)里完成;本类只做窗口内互斥与应答映射。
    /// </summary>
    private CutoverPayload GenerateAndRestartCutover()
    {
        if (Interlocked.CompareExchange(ref _cutoverInProgress, 1, 0) != 0)
        {
            return new CutoverPayload(
                false, "已有一次 cc-connect 配置切换正在进行,请等待其完成。", 0, null,
                _cutoverConfigPath, ConfigWritten: false, RestartVerified: false,
                Agent: null, Pid: null, Phase: "busy");
        }

        try
        {
            CcConnectApplyResult r = CcConnectApplyCommand.Apply(_cutoverConfigPath, _cutoverGenerate, _daemonControllerFactory);
            if (r.Phase == "exception")
            {
                ReportRequestFailure("cutover.generate", new InvalidOperationException(r.Message));
            }

            return new CutoverPayload(r.Ok, r.Message, r.ProjectCount, r.SanitizedToml, r.OutPath,
                r.ConfigWritten, r.RestartVerified, r.Agent, r.Pid, r.Phase);
        }
        finally
        {
            Volatile.Write(ref _cutoverInProgress, 0);
        }
    }

    /// <summary>单消费者预检。只读,不停任何进程。</summary>
    private PreflightPayload Preflight()
    {
        ConsumerGuardResult result = SingleConsumerGuard.CreateDefault()
            .Check(feishuPlatformConfigured: true);

        // Detail 只有进程名与固定文案(SingleConsumerGuard 保证),不含原始命令行。
        var conflicts = result.Conflicts
            .Select(c => new PreflightConflict(c.Pid, c.Kind, c.Detail))
            .ToList();

        return new PreflightPayload(result.Verdict.ToString(), result.CanStart, result.Reason, conflicts);
    }

    // ── Claude 凭据(长期令牌)──
    // 实值单向流动:输入框 → 桥 → DPAPI(机器范围)。应答只回「有没有、何时存的、现在靠哪份」。

    private object ClaudeTokenStatus()
    {
        ClaudeCredentialStatus status = _claudeCredentials.Describe();
        return new
        {
            hasStoredToken = status.HasStoredToken,
            storedTokenSavedAtUnix = status.StoredTokenSavedAtUtc?.ToUnixTimeSeconds(),
            cliLoggedIn = status.CliLoggedIn,
            cliExpiresAtUnix = status.CliExpiresAtUtc?.ToUnixTimeSeconds(),
            cliProblem = status.CliProblem,
            activeSource = status.ActiveSource,
            problem = status.Problem,
            storedRejectedReason = status.StoredRejectedReason,
            storedProblem = status.StoredProblem,
        };
    }

    private object SaveClaudeToken(JsonElement root)
    {
        string token = root.TryGetProperty("token", out JsonElement tokenEl) && tokenEl.ValueKind == JsonValueKind.String
            ? tokenEl.GetString() ?? string.Empty
            : string.Empty;
        if (string.IsNullOrWhiteSpace(token))
        {
            throw new ArgumentException("请粘贴 claude setup-token 输出的令牌。");
        }

        // 形状校验失败的异常文案不含输入内容;保存后由下一次额度请求真实验证。
        _claudeCredentials.SaveToken(token);
        return ClaudeTokenStatus();
    }

    private object ClearClaudeToken()
    {
        _claudeCredentials.ClearToken();
        return ClaudeTokenStatus();
    }

    /// <summary>
    /// 把通知收件人 open_id 同步进明文产品配置。Worker 的通知路径只读这份明文,
    /// 不再在 S4U 会话里解 DPAPI(2026-09/10 间歇失败数小时的根因)。只在值变化时写。
    /// </summary>
    private void SyncNotifyRecipient(string? allowFrom)
    {
        string? recipient = NotificationWorker.FirstOpenId(allowFrom);
        try
        {
            if (recipient is not null &&
                !string.Equals(_configStore.Load().NotifyRecipientOpenId, recipient, StringComparison.Ordinal))
            {
                _configStore.Update(c => c.NotifyRecipientOpenId = recipient);
            }
        }
        catch (Exception ex)
        {
            ReportRequestFailure("feishu.recipient", ex);
        }
    }

    private void ReportRequestFailure(string requestType, Exception exception)
    {
        try
        {
            _requestFailureReporter(
                requestType,
                exception.GetType().FullName ?? exception.GetType().Name);
        }
        catch (Exception)
        {
            // 本地诊断失败不能覆盖原始业务错误,也不能改变前端应答契约。
        }
    }

    /// <summary>
    /// 读取当前 agent 选择与全部可选值。当前值经 Normalize 保证合法,
    /// 即使 shadow 配置被手改坏也不会把非法值回给前端。
    /// </summary>
    private object GetAgent()
    {
        string current = CcConnectAgents.Normalize(_configStore.Load().CcConnectAgent);

        // installed 决定界面能不能点:选一个本机没装 CLI 的 agent,
        // cc-connect 重启后会起不动那个 agent —— 表现为机器人不回话,
        // 而用户只会觉得"刚换了个模型就坏了",排查方向完全跑偏。
        var options = CcConnectAgents.Supported
            .Select(a => new { id = a.Id, display = a.Display, installed = CcConnectAgents.IsInstalled(a.Id) })
            .ToArray();

        return new { current, options };
    }

    /// <summary>
    /// 设置 agent 选择。**必须走 Update 的锁内读-改-写**:配置可能同时被 Worker 的
    /// 续跑引擎修改,整体写回旧快照会互相覆盖(本项目有过锁外读旧快照整体写回的事故)。
    /// 返回 restartRequired=true,因为 cc-connect 运行时切不了 agent,必须重启才生效。
    /// </summary>
    private object SetAgent(JsonElement root)
    {
        string? raw = root.TryGetProperty("agent", out JsonElement agentEl) && agentEl.ValueKind == JsonValueKind.String
            ? agentEl.GetString()
            : null;

        string next = CcConnectAgents.Normalize(raw);

        // 拒绝选未安装的:写进去等于生成一份重启后起不来的配置,
        // 而失败现象(机器人不回话)离原因(选了没装的 agent)非常远。
        if (!CcConnectAgents.IsInstalled(next))
        {
            throw new InvalidOperationException(
                $"本机未安装 {next} 的命令行工具,选它会让 cc-connect 重启后起不来。请先安装再切换。");
        }

        _configStore.Update(c => c.CcConnectAgent = next);

        return new { current = next, restartRequired = true };
    }

    private static string DescribeWindow(string name) => name switch
    {
        "five_hour" => "5 小时窗口",
        "seven_day" => "7 天窗口",
        _ => name,
    };

    private static string Serialize(Envelope envelope) => JsonSerializer.Serialize(envelope, JsonOptions);

    private sealed record Envelope(
        [property: JsonPropertyName("id")] string? Id,
        [property: JsonPropertyName("type")] string Type,
        [property: JsonPropertyName("payload")] object? Payload,
        [property: JsonPropertyName("error")] string? Error);

    private sealed record ProjectsPayload(
        [property: JsonPropertyName("items")] IReadOnlyList<ProjectItem> Items,
        [property: JsonPropertyName("elapsedMs")] long ElapsedMs,
        [property: JsonPropertyName("hidden")] IReadOnlyList<HiddenItem> Hidden);

    private sealed record ProjectItem(
        [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("path")] string Path,
        [property: JsonPropertyName("lastUsed")] string? LastUsed,
        [property: JsonPropertyName("isGit")] bool IsGit,
        [property: JsonPropertyName("isCustom")] bool IsCustom);

    private sealed record HiddenItem(
        [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("path")] string Path);

    private sealed record PickFolderPayload(
        [property: JsonPropertyName("path")] string? Path);

    /// <summary>飞书接入状态。**没有 appSecret 字段,这是刻意的**——实值不回传前端。</summary>
    private sealed record FeishuPayload(
        [property: JsonPropertyName("hasCredentials")] bool HasCredentials,
        [property: JsonPropertyName("appIdMasked")] string? AppIdMasked,
        [property: JsonPropertyName("allowFrom")] string? AllowFrom,
        [property: JsonPropertyName("configPath")] string ConfigPath,
        [property: JsonPropertyName("configExists")] bool ConfigExists,
        // missing / ok / invalid / unknown —— 「文件在」和「加载得了」是两件事。
        [property: JsonPropertyName("configState")] string ConfigState,
        [property: JsonPropertyName("configSummary")] string ConfigSummary,
        [property: JsonPropertyName("configProblems")] IReadOnlyList<string> ConfigProblems,
        // agent / provider / model 对不上:配置照常加载,但行为不是用户以为的那样。
        [property: JsonPropertyName("configWarnings")] IReadOnlyList<string> ConfigWarnings);

    private sealed record CutoverPayload(
        [property: JsonPropertyName("ok")] bool Ok,
        [property: JsonPropertyName("message")] string Message,
        [property: JsonPropertyName("projectCount")] int ProjectCount,
        [property: JsonPropertyName("sanitizedToml")] string? SanitizedToml,
        [property: JsonPropertyName("outPath")] string? OutPath,
        [property: JsonPropertyName("configWritten")] bool ConfigWritten,
        [property: JsonPropertyName("restartVerified")] bool RestartVerified,
        [property: JsonPropertyName("agent")] string? Agent,
        [property: JsonPropertyName("pid")] int? Pid,
        [property: JsonPropertyName("phase")] string Phase);

    private sealed record PreflightPayload(
        [property: JsonPropertyName("verdict")] string Verdict,
        [property: JsonPropertyName("canStart")] bool CanStart,
        [property: JsonPropertyName("reason")] string? Reason,
        [property: JsonPropertyName("conflicts")] IReadOnlyList<PreflightConflict> Conflicts);

    private sealed record PreflightConflict(
        [property: JsonPropertyName("pid")] int Pid,
        [property: JsonPropertyName("kind")] string Kind,
        [property: JsonPropertyName("detail")] string Detail);

    private sealed record AppInfoPayload(
        [property: JsonPropertyName("version")] string Version,
        [property: JsonPropertyName("shadowRoot")] string ShadowRoot,
        [property: JsonPropertyName("quotaRefreshMinutes")] int QuotaRefreshMinutes);

    private sealed record NotificationsPayload(
        [property: JsonPropertyName("items")] IReadOnlyList<NotificationItem> Items);

    private sealed record LocalBlockPayload(
        [property: JsonPropertyName("active")] bool Active,
        [property: JsonPropertyName("startUnix")] long? StartUnix,
        [property: JsonPropertyName("endUnix")] long? EndUnix,
        [property: JsonPropertyName("totalTokens")] long TotalTokens,
        [property: JsonPropertyName("messageCount")] int MessageCount,
        [property: JsonPropertyName("elapsedMs")] long ElapsedMs);

    private sealed record ArmPayload(
        [property: JsonPropertyName("armed")] bool Armed,
        [property: JsonPropertyName("continuous")] bool Continuous,
        [property: JsonPropertyName("resumeModel")] string ResumeModel,
        [property: JsonPropertyName("cycleId")] string CycleId,
        [property: JsonPropertyName("phase")] string Phase,
        [property: JsonPropertyName("sawLimited")] bool SawLimited,
        [property: JsonPropertyName("selected")] IReadOnlyList<string> Selected,
        [property: JsonPropertyName("projectStatus")] IReadOnlyList<ProjectStatusItem> ProjectStatus,
        [property: JsonPropertyName("engine")] string Engine,
        [property: JsonPropertyName("engineText")] string EngineText,
        [property: JsonPropertyName("probeAgeSeconds")] long? ProbeAgeSeconds,
        [property: JsonPropertyName("resumeTarget")] ResumeTarget? ResumeTarget = null,
        [property: JsonPropertyName("probeReason")] string ProbeReason = "");

    private sealed record ProjectStatusItem(
        [property: JsonPropertyName("path")] string Path,
        [property: JsonPropertyName("status")] string Status,
        [property: JsonPropertyName("category")] string Category,
        [property: JsonPropertyName("text")] string Text);

    private sealed record ArmReadSnapshot(
        ProductConfig Config,
        CheckerState? State,
        Exception? Error);

    private sealed record QuotaPayload(
        [property: JsonPropertyName("provider")] string Provider,
        [property: JsonPropertyName("capturedAtUnix")] long CapturedAtUnix,
        [property: JsonPropertyName("hasData")] bool HasData,
        [property: JsonPropertyName("allowed")] bool Allowed,
        [property: JsonPropertyName("limitReached")] bool LimitReached,
        [property: JsonPropertyName("unattributedLimitReached")] bool UnattributedLimitReached,
        [property: JsonPropertyName("globalHasCurrentData")] bool GlobalHasCurrentData,
        [property: JsonPropertyName("globalHasCarried")] bool GlobalHasCarried,
        [property: JsonPropertyName("globalLimitReached")] bool GlobalLimitReached,
        [property: JsonPropertyName("unavailableReason")] string? UnavailableReason,
        [property: JsonPropertyName("storageWarning")] string? StorageWarning,
        [property: JsonPropertyName("windows")] IReadOnlyList<QuotaWindow> Windows);

    private sealed record QuotaWindow(
        [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("label")] string Label,
        [property: JsonPropertyName("status")] string Status,
        [property: JsonPropertyName("usedPercent")] int? UsedPercent,
        [property: JsonPropertyName("windowSeconds")] int WindowSeconds,
        [property: JsonPropertyName("resetAtUnix")] long? ResetAtUnix,
        [property: JsonPropertyName("resetAfterSeconds")] int? ResetAfterSeconds,
        [property: JsonPropertyName("windowStartUnix")] long? WindowStartUnix,
        [property: JsonPropertyName("carriedForward")] bool CarriedForward);

    internal readonly record struct QuotaGlobalFacts(
        bool HasCurrentData,
        bool HasCarried,
        bool LimitReached,
        bool UnattributedLimitReached);

    private sealed record NotificationItem(
        [property: JsonPropertyName("kind")] string Kind,
        [property: JsonPropertyName("displayName")] string DisplayName,
        [property: JsonPropertyName("isInstalled")] bool IsInstalled,
        [property: JsonPropertyName("isEnabled")] bool IsEnabled,
        [property: JsonPropertyName("configPath")] string? ConfigPath,
        [property: JsonPropertyName("detail")] string? Detail,
        [property: JsonPropertyName("hookBroken")] bool HookBroken);
}
