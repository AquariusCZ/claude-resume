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
    private readonly Func<string?> _hookExecutableResolver;
    private readonly ClaudeCredentialSource _claudeCredentials;
    private readonly Action<string, string> _probeFailureReporter;
    private readonly Action<string, string> _requestFailureReporter;
    private readonly Func<bool?> _engineProcessDetector;
    private readonly Func<bool> _workerPing;
    private readonly Func<string, bool?> _activeResumeDetector;
    private readonly bool _demoMode;
    private readonly Func<CancellationToken, Task<IReadOnlyList<ProviderObservation>>> _providerCatalog;
    private readonly Func<string, string, IReadOnlyList<string>, IReadOnlyDictionary<string, string>, ProductConfig, CancellationToken, Task<BoundResumeSelection>> _resumeBinding;
    private readonly Func<string, IReadOnlyList<string>, IReadOnlyList<ProjectPreview>> _resumePreview;
    private readonly NotifyControlPlane _notify;

    public ControlPlaneBridge(
        ProjectCatalog? catalog = null,
        Func<ProductConfig>? configFactory = null,
        NotificationRegistry? notificationRegistry = null,
        QuotaService? quotaService = null,
        ProductConfigStore? configStore = null,
        ProductStateStore? stateStore = null,
        Func<CancellationToken, Task<string?>>? folderPicker = null,
        Func<string?>? hookExecutableResolver = null,
        ClaudeCredentialSource? claudeCredentials = null,
        Action<string, string>? probeFailureReporter = null,
        Action<string, string>? requestFailureReporter = null,
        Func<bool?>? engineProcessDetector = null,
        Func<string, bool?>? activeResumeDetector = null,
        bool demoMode = false,
        Func<CancellationToken, Task<IReadOnlyList<ProviderObservation>>>? providerCatalog = null,
        Func<string, string, IReadOnlyList<string>, IReadOnlyDictionary<string, string>, ProductConfig, CancellationToken, Task<BoundResumeSelection>>? resumeBinding = null,
        Func<string, IReadOnlyList<string>, IReadOnlyList<ProjectPreview>>? resumePreview = null,
        NotifyControlPlane? notifyControl = null,
        Func<bool>? workerPing = null)
    {
        _demoMode = demoMode;
        // 选目录必须回到 UI 线程弹原生对话框,由宿主窗口注入;测试注入替身,不弹窗。
        _folderPicker = folderPicker;
        _hookExecutableResolver = hookExecutableResolver ?? HookExecutable.TryResolve;
        _probeFailureReporter = probeFailureReporter ?? ((_, _) => { });
        _requestFailureReporter = requestFailureReporter ?? ((_, _) => { });
        _engineProcessDetector = engineProcessDetector ?? (() => EngineLiveness.TryDetectEngineProcess());
        // 「后台服务运行中」还要常驻 Worker 的 Named Pipe 应答;测试注入了进程判定时不去碰本机真实的管道。
        _workerPing = workerPing ?? (engineProcessDetector is null
            ? () => InstallCommand.CanPingWorker(TimeSpan.FromMilliseconds(300))
            : () => true);
        // 测试注入自有 stateStore 时默认不碰生产 process_registry;生产窗口才读现役数据库。
        _activeResumeDetector = activeResumeDetector ?? (stateStore is null
            ? runId => EngineLiveness.TryDetectActiveOwnedProcess(ShadowPaths.RunDatabasePath, runId)
            : _ => false);
        _quota = quotaService ?? new QuotaService();
        var providers = new ProviderCatalogService(quota: _quota);
        // 页面在 quota.get 返回之后才读供应商目录:Claude 行复用这一轮刚拿到的额度读数，一轮刷新只请求一次 usage 端点。
        _providerCatalog = providerCatalog ?? (ct => providers.ReadAsync(ct, TimeSpan.FromMinutes(1)));
        // 长期令牌的保存/清除与供应商注册表共用同一个凭据解析器,面板上「现在靠哪份凭据」才和续跑一致。
        _claudeCredentials = claudeCredentials ?? providers.Registry.ClaudeCredentials;
        var binding = new ResumeBindingService(providers);
        _resumeBinding = resumeBinding ?? binding.BindAsync;
        _resumePreview = resumePreview ?? binding.Preview;
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
        // 飞书通知通道与 Worker 共用同一 shadow 根(凭据副本、投递健康文件、事件队列)。
        _notify = notifyControl ?? new NotifyControlPlane(_configStore);
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
                "app.info" => await Task.Run(() => AppInfo(), cancellationToken).ConfigureAwait(false),
                "notifications.list" => await Task.Run(() => ListNotifications(), cancellationToken).ConfigureAwait(false),
                "notifications.setEnabled" => await Task.Run(() => SetNotificationEnabled(root), cancellationToken).ConfigureAwait(false),
                // 额度与供应商目录的同步部分要读 ~/.claude.json(可达数 MB)等多份配置:同样放到线程池,不卡窗口消息泵。
                "quota.get" => await Task.Run(() => GetQuotaAsync(root, cancellationToken), cancellationToken).ConfigureAwait(false),
                "arm.get" => await Task.Run(() => GetArm(), cancellationToken).ConfigureAwait(false),
                "arm.set" => await Task.Run(() => SetArmAsync(root, cancellationToken), cancellationToken).ConfigureAwait(false),
                "arm.preview" => await Task.Run(() => PreviewArm(root), cancellationToken).ConfigureAwait(false),
                "providers.catalog" => new { items = (await Task.Run(() => _providerCatalog(cancellationToken), cancellationToken).ConfigureAwait(false)).Select(p => p.ToPayload()) },
                "claude.login.status" => await Task.Run(() => ClaudeLoginStatus(), cancellationToken).ConfigureAwait(false),
                // notify.* 会扫 PATH、解 DPAPI、拉起 lark-cli:与其它请求一样整个放到线程池,绝不在 UI 线程上做 I/O。
                "notify.channel" => await Task.Run(() => ChannelPayload(_notify.Describe()), cancellationToken).ConfigureAwait(false),
                "notify.check" => CheckPayload(await Task.Run(() => _notify.CheckAsync(cancellationToken), cancellationToken).ConfigureAwait(false)),
                "notify.credential.set" => ChannelPayload(await Task.Run(() => _notify.SetCredentialAsync(
                    ReadString(root, "appId"), ReadString(root, "appSecret"), cancellationToken), cancellationToken).ConfigureAwait(false)),
                "notify.credential.import" => ChannelPayload(await Task.Run(() => _notify.ImportLegacyAsync(cancellationToken), cancellationToken).ConfigureAwait(false)),
                "notify.credential.clear" => await Task.Run(() => ChannelPayload(_notify.ClearCredential()), cancellationToken).ConfigureAwait(false),
                "notify.recipient.set" => await Task.Run(() => ChannelPayload(_notify.SetRecipient(ReadString(root, "openId"))), cancellationToken).ConfigureAwait(false),
                "notify.recipient.detect" => await Task.Run(() => DetectPayloadAsync(cancellationToken), cancellationToken).ConfigureAwait(false),
                "notify.test" => await Task.Run(() => new
                {
                    enqueuedAtUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                    eventId = _notify.EnqueueTest(),
                    channel = ChannelPayload(_notify.Describe()),
                }, cancellationToken).ConfigureAwait(false),
                "notify.test.status" => await Task.Run(() =>
                {
                    string eventId = ReadString(root, "eventId");
                    string outcome = _notify.TestOutcome(eventId,
                        DateTimeOffset.FromUnixTimeSeconds(root.TryGetProperty("sinceUnix", out JsonElement since) && since.TryGetInt64(out long unix) ? unix : 0));
                    return new
                    {
                        outcome,
                        failure = outcome == "failed" ? _notify.TestFailure(eventId) : null,
                        channel = ChannelPayload(_notify.Describe()),
                    };
                }, cancellationToken).ConfigureAwait(false),
                "notify.test.withdraw" => await Task.Run(() => new { withdrawn = _notify.WithdrawTest(ReadString(root, "eventId")) }, cancellationToken).ConfigureAwait(false),
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
                    new { name = "orbit-planner", path = @"D:\Demo\orbit-planner", lastUsed = "2026/8/8 09:12", isGit = true, isCustom = false },
                    new { name = "ledger-api", path = @"D:\Demo\ledger-api", lastUsed = "2026/8/8 01:40", isGit = true, isCustom = false },
                    new { name = "atlas-docs", path = @"D:\Demo\atlas-docs", lastUsed = "2026/8/7 23:05", isGit = true, isCustom = false },
                    new { name = "pixel-sprites", path = @"D:\Demo\pixel-sprites", lastUsed = "2026/8/7 18:31", isGit = false, isCustom = true },
                    new { name = "kettle-firmware", path = @"D:\Demo\kettle-firmware", lastUsed = "2026/8/7 11:02", isGit = true, isCustom = false },
                    new { name = "tide-charts", path = @"D:\Demo\tide-charts", lastUsed = "2026/8/6 21:44", isGit = true, isCustom = false },
                },
                elapsedMs = 37,
                hidden = Array.Empty<object>(),
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
                    new { name = "weekly_scoped:Fable", label = "weekly_scoped:Fable", status = "blocked", usedPercent = 100, windowSeconds = 604_800, resetAtUnix = now + 180_000, resetAfterSeconds = 180_000, windowStartUnix = now - 424_800, carriedForward = false },
                },
            },
            "providers.catalog" => new { items = new object[] {
                new { id = "claude-subscription", name = "Claude · Anthropic 订阅", agent = "claudecode", kind = "claude-oauth", credentialSource = "claude-oauth:claude-cli", resumable = true, models = new[] { new { id = "claude-fable-5-1", name = "Claude Fable 5.1" }, new { id = "claude-opus-5-5", name = "Claude Opus 5.5" } }, modelsVerified = true, source = "供应商实时返回", observedAtUtc = DateTimeOffset.UtcNow, quotaKnown = true, limited = true, resumeNote = "布防时按项目固定模型调用 ID，下次布防重新选择", windows = new object[] { new { name = "five_hour", usedPercent = 41, resetAtUnix = now + 9_000, windowSeconds = 18_000, carriedForward = false, status = "allowed" }, new { name = "seven_day", usedPercent = 88, resetAtUnix = now + 180_000, windowSeconds = 604_800, carriedForward = false, status = "allowed" }, new { name = "weekly_scoped:Fable", usedPercent = 100, resetAtUnix = now + 180_000, windowSeconds = 604_800, carriedForward = false, status = "blocked" } } },
                new { id = "deepseek", name = "DeepSeek · OpenCode", agent = "opencode", kind = "deepseek", credentialSource = "opencode-auth", resumable = false, models = new[] { new { id = "deepseek-v4-flash", name = "deepseek-v4-flash" } }, modelsVerified = true, source = "供应商实时返回", observedAtUtc = DateTimeOffset.UtcNow, quotaKnown = true, limited = false, balance = 47.77, currency = "CNY", resumeNote = "OpenCode 使用的 DeepSeek 账户余额，只展示，不参与续跑" },
                new { id = "codex-official", name = "Codex · ChatGPT 官方", agent = "codex", kind = "chatgpt", credentialSource = "auth.json:chatgpt", resumable = true, models = new[] { new { id = "gpt-5.6-sol", name = "GPT-5.6 Sol" } }, modelsVerified = true, source = "供应商实时返回", observedAtUtc = DateTimeOffset.UtcNow, quotaKnown = true, limited = false, resumeNote = "账号窗口与模型可用性来自 ChatGPT 官方接口", windows = new object[] { new { name = "primary · 7D", usedPercent = 12, resetAtUnix = now + 500_000, windowSeconds = 604_800, carriedForward = false, status = "allowed" } } }
            } },
            "claude.login.status" => new
            {
                verdict = nameof(ClaudeLoginVerdict.LoginExpiring),
                accessTokenExpiresAtUnix = now + 6 * 3600,
                loginExpiresAtUnix = now + 2 * 86_400,
                subscriptionType = "max",
                accountVerified = true,
                problem = "Claude Code 登录将于 11-2 21:55 到期，届时需运行 claude 并 /login",
            },
            "arm.get" => new
            {
                armed = !Environment.GetCommandLineArgs().Contains("--screenshot-unarmed"),
                continuous = false,
                resumeModel = "claude-fable-5-1",
                resumeTarget = new ResumeTarget { ProviderId = "claude-subscription", ProviderName = "Claude · Anthropic 订阅", ProviderIdentity = "demo", Agent = "claudecode", ModelId = "claude-fable-5-1", ModelName = "Claude Fable 5.1", BoundAtUtc = DateTimeOffset.UtcNow },
                cycleId = "demo-cycle",
                phase = "waiting",
                sawLimited = true,
                selected,
                projectStatus = selected
                    .Select(path => DescribeProjectStatus(path, "limited"))
                    .ToArray(),
                projects = new object[]
                {
                    new { path = selected[0], name = "ledger-api", sessionId = "00000000-0000-4000-8000-000000000001", modelId = "claude-fable-5-1", modelName = "Claude Fable 5.1", modelSource = "session", status = "limited", category = "waiting", reason = "quota:limited", reasonText = "等额度", hint = "Fable 周额度已用尽", detail = "weekly_scoped:Fable:blocked", resetAtUnix = now + 180_000, interruptedAtUnix = now - 5_400 },
                    new { path = selected[1], name = "atlas-docs", sessionId = "00000000-0000-4000-8000-000000000002", modelId = "claude-opus-5-5", modelName = "Claude Opus 5.5", modelSource = "session", status = "limited", category = "waiting", reason = "session-in-use", reasonText = "会话开着 · 关闭后续跑", hint = "会话开在 Claude 桌面端:客户端开着时由它自己继续，关闭后自动续跑", interruptedAtUnix = now - 3_600, client = "claude-desktop" },
                    new { path = selected[2], name = "pixel-sprites", sessionId = "00000000-0000-4000-8000-000000000003", modelId = "claude-opus-5-5", modelName = "Claude Opus 5.5", modelSource = "user", category = "idle", reason = "not-interrupted", reasonText = "监视中 · 未被打断", hint = "会话没有被用量上限打断，不需要续跑" },
                },
                engine = "Alive",
                engineText = "引擎在跑",
                workerRunning = true,
                probeAgeSeconds = 12,
                probeReason = "quota:limited",
            },
            "arm.preview" => new
            {
                items = new object[]
                {
                    new { path = selected[0], name = "ledger-api", sessionId = "00000000-0000-4000-8000-000000000001", sessionModel = "claude-fable-5-1", evidence = "interrupted", limitType = "seven_day", limitResetsAtUnix = now + 180_000, interruptedAtUnix = now - 5_400, session = "idle" },
                    new { path = selected[1], name = "atlas-docs", sessionId = "00000000-0000-4000-8000-000000000002", sessionModel = "claude-opus-5-5", evidence = "interrupted", limitType = "five_hour", limitResetsAtUnix = now + 9_000, interruptedAtUnix = now - 3_600, session = "in-use", client = "claude-desktop" },
                    new { path = selected[2], name = "pixel-sprites", sessionId = "00000000-0000-4000-8000-000000000003", sessionModel = "claude-opus-5-5", evidence = "clear", session = "idle" },
                },
            },
            "notifications.list" => new
            {
                items = new object[]
                {
                    DemoNotification("ClaudeCode", "Claude Code"),
                    DemoNotification("Codex", "Codex", enabled: false),
                    DemoNotification("Cline", "Cline", enabled: false),
                    DemoNotification("Qoder", "Qoder", enabled: false),
                    DemoNotification("OpenCode", "OpenCode"),
                },
            },
            "notify.channel" => new
            {
                larkCliFound = true,
                credentialSource = "env",
                appIdMasked = "cli_a1…7f",
                legacyCredentialAvailable = false,
                recipientSet = true,
                recipientMasked = "ou_0123…cdef",
                health = new
                {
                    state = "healthy", lastSuccessAtUnix = now - 1_260, lastAttemptAtUnix = now - 1_260, backlog = 0, dead = 0,
                    credentialSource = "env", updatedAtUnix = now - 8,
                },
            },
            "notify.check" => new
            {
                ok = true,
                botReady = true,
                credentialSource = "env",
                checks = new object[]
                {
                    new { name = "app_resolved", status = "pass", message = "app: cli_a1…7f (feishu)" },
                    new { name = "bot_identity", status = "pass", message = "Bot identity: ready" },
                    new { name = "endpoint_open", status = "pass", message = "https://open.feishu.cn reachable" },
                },
            },
            "notify.recipient.detect" => new
            {
                items = new object[] { new { openId = "ou_0123456789abcdef0123456789abcdef", userName = "演示用户", tokenStatus = "valid" } },
            },
            "app.info" => new { version = typeof(ControlPlaneBridge).Assembly.GetName().Version?.ToString(3) ?? "dev", shadowRoot = @"D:\Demo\AI Resume\state", quotaRefreshMinutes = 15 },
            _ => throw new NotSupportedException($"截图合成模式不支持请求:{type}"),
        };
    }

    private static object DemoNotification(string kind, string displayName, bool enabled = true) => new
    {
        kind,
        displayName,
        isInstalled = true,
        isEnabled = enabled,
        configPath = @"D:\Demo\notifications",
        detail = enabled ? "已安装,最近一次加载正常" : "未启用",
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
            s.HookBroken,
            // 探测失败:开没开不知道,界面显示「未核实」而不是「已关闭」「未检测到」。
            s.Unverified)).ToList();
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

        // 意图先落盘、再改客户端配置。它既是重装后恢复通知源的**唯一依据**(卸载会把现状清空,审计 B3),
        // 也是投递端的来源过滤:记不下来就不能改开关,否则界面显示开着、事件却被投递端按旧意图丢弃。
        NotifyIntent.Apply(_configStore, kind, enabled, () => _notificationRegistry.SetEnabled(kind, enabled, hookExe),
            () => _notificationRegistry.ProbeEnabled(kind));

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
            bool? workerRunning = _engineProcessDetector();
            EngineVerdict unreadableVerdict = config.Armed && workerRunning == false
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
                null, config.ResumeTarget)
            {
                WorkerRunning = EngineLiveness.ResidentWorker(workerRunning, _workerPing),
            };
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

        // 引擎判定只看同名进程;「监视中」还要本用户的常驻 Worker 应答管道。有进程却不应答:核验不出，不说监视中。
        bool? residentWorker = EngineLiveness.ResidentWorker(engineRunning, _workerPing);
        if (verdict == EngineVerdict.Alive && residentWorker is null)
        {
            verdict = EngineVerdict.WorkerUnverified;
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
            probeAge, config.ResumeTarget, currentCycle ? state.LastProbeReason : string.Empty)
        {
            Projects = DescribeProjects(config, currentCycle ? state : null, statuses),
            WorkerRunning = residentWorker,
        };
    }

    /// <summary>
    /// 逐项目：绑定的会话与模型、当前状态，以及「为什么在等、在等什么」的一句话。
    /// 状态行已被引擎存活判定改写(如未核实)时以改写后的为准;只有 limited(待续跑)才展开等待原因。
    /// </summary>
    private static IReadOnlyList<ArmProjectItem> DescribeProjects(
        ProductConfig config, CheckerState? state, IReadOnlyList<ProjectStatusItem> statuses)
    {
        return config.Selected.Select(project =>
        {
            ProjectStatusItem? status = statuses.FirstOrDefault(item => PathEquals(item.Path, project.Path));
            ProjectNote? note = state is null ? null : ProjectPaths.Find(state.ProjectNotes, project.Path);
            string modelId = ResumeTarget.EffectiveModel(project, config.ResumeTarget);
            string modelName = project.ModelName is { Length: > 0 } name ? name
                : modelId == config.ResumeTarget?.ModelId ? config.ResumeTarget.ModelName : modelId;
            string? agent = config.ResumeTarget?.Agent;
            string? providerId = config.ResumeTarget?.ProviderId;
            (string category, string text, string hint) = status is { Status: "continued-elsewhere" } && note is { Reason: "continued-elsewhere" }
                ? DescribeNote(note, project.Path, agent, providerId)
                : status is { Status: not "limited" }
                ? (status.Category, status.Text, string.Empty)
                : note is not null
                    ? DescribeNote(note, project.Path, agent, providerId)
                    : status is not null
                        ? (status.Category, status.Text, string.Empty)
                        : state is not null ? ("waiting", "等待取证", string.Empty) : ("idle", string.Empty, string.Empty);
            return new ArmProjectItem(
                project.Path,
                project.Name,
                project.SessionId,
                modelId,
                modelName,
                project.ModelSource is { Length: > 0 } source ? source : "default",
                status?.Status,
                category,
                note?.Reason,
                text,
                hint,
                note?.Detail,
                note?.ResetAtUnix,
                note?.InterruptedAtUnix,
                note?.Client);
        }).ToArray();
    }

    /// <summary>
    /// 原因码 → 队列行的短句(窄列放得下)+ 悬停时的完整说明。
    /// 重置时间由前端按本地时区补在短句后面，说明里的「预计 HH:mm 起续跑」也由前端补。
    /// </summary>
    private static (string Category, string Text, string Hint) DescribeNote(ProjectNote note, string path, string? agent = null,
        string? providerId = null)
    {
        string reason = note.Reason;
        if (reason.StartsWith("blocked:", StringComparison.Ordinal))
        {
            ProjectStatusItem blocked = DescribeProjectStatus(path, reason["blocked:".Length..]);
            return (blocked.Category, blocked.Text, "本周期已停止，不会自动重放;检查后解除并重新布防");
        }

        // Codex 周期的账号与登录问题不能给出 Claude 的修复办法(/login、打开 Claude Code)。
        if (agent == "codex")
        {
            switch (reason)
            {
                case "quota:credentials-unavailable" or "quota:identity-unverified":
                    return ("waiting", "Codex 登录未核实", "读不出 Codex 官方账号:运行 codex login,或检查 ~/.codex/auth.json");
                case "quota:provider-changed":
                    return ("failure", "账号已变化 · 重新布防", "Codex 账号与布防时不同:不会用当前账号自动续跑,确认后解除并重新布防");
            }
        }

        // Claude Code 走 API 凭据(ANTHROPIC_* 指向的端点)的周期与订阅登录无关，不能给出 /login 的办法。
        if (agent == "claudecode" && providerId is { Length: > 0 } and not "claude-subscription" &&
            reason is "quota:credentials-unavailable" or "quota:identity-unverified")
        {
            return ("waiting", "API 凭据未核实", "读不出 Claude Code 设置里的 API 凭据(ANTHROPIC_* 环境变量或 settings.json 的 env)");
        }

        return reason switch
        {
            "continued-elsewhere" when note.Detail == "other-session" => ("success", "已在别的会话继续",
                "中断之后,你在同一目录新开的会话里接着干了:旧会话的上下文已经过时,为免它改动变化了的工作树,不再自动续跑"),
            "not-interrupted" => ("idle", "监视中 · 未被打断", "会话没有被用量上限打断，不需要续跑"),
            "watching" => ("idle", "监视中", string.Empty),
            "ready" => ("waiting", "额度已恢复 · 即将续跑", string.Empty),
            "running" => ("running", "续跑中", string.Empty),
            "done" => ("success", "已完成", string.Empty),
            "continued-elsewhere" => ("success", "已在客户端继续", "被打断之后会话里又有了新的对话，视为你已经自己续上"),
            "unevaluated" => ("waiting", "等待取证", string.Empty),
            "session-in-use" => ("waiting", "会话开着 · 关闭后续跑",
                "会话开在" + ClientText(note.Client) + ":客户端开着时由它自己继续，关闭后自动续跑"),
            "cwd-busy" => ("waiting", "同目录有会话在跑", "同一目录下另一个会话正在运行，它结束后再续跑"),
            "session-unverified" => ("waiting", "会话占用未核实", "读不出会话是否开在客户端，暂不续跑"),
            "evidence-unverified" => ("waiting", "会话记录未核实", "读不出会话记录的结尾，暂不续跑"),
            "quota:limited" => ("waiting", "等额度", WindowText(note.Detail) is { Length: > 0 } windows ? windows + "已用尽" : "额度已用尽"),
            // 读不到额度的原因不同,该做的事也不同(note.Detail = 凭据失败原因码)。
            "quota:credentials-unavailable" or "quota:identity-unverified" => note.Detail switch
            {
                "token_expired" => ("waiting", "等登录令牌续期", "Claude 访问令牌已过期:打开一次 Claude Code(或保持手机远程连接)即可自动续期"),
                "login_expired" or "no_credentials" => ("waiting", "Claude 登录失效 · 需 /login", "在终端运行 claude 后执行 /login;恢复前读不到额度"),
                "credential_store_moved" => ("waiting", "凭据位置变化", "Claude Code 把登录挪进了 Windows 凭据管理器,AI Resume 读不到它"),
                "unreadable" => ("waiting", "登录状态未核实", "读不出 Claude Code 的登录文件(可能正被改写),稍后自动重试"),
                _ => ("waiting", "账号身份未核实", "Claude Code 全局配置里没有一致的账号信息,打开一次 Claude Code 通常即可恢复"),
            },
            "quota:provider-changed" => ("failure", "账号已变化 · 重新布防",
                "Claude 账号身份与布防时不同(换了账号,或刚从 v2.1 升级):不会用当前账号自动续跑,确认后解除并重新布防"),
            "quota:model-unverified" when note.Detail == "absent" => ("waiting", "模型不在当前目录", "供应商当前的模型目录里找不到布防时固定的模型"),
            "quota:model-unverified" => ("waiting", "模型目录未核实", "这次没读出供应商的模型目录(限流、网络或凭据问题),下一轮重试"),
            "quota:unknown" when note.Detail == "model-invalid" => ("failure", "模型额度无法核实 · 重新布防",
                "认不出这个模型属于哪个模型族,无法按它的按模型额度判断何时续跑;解除布防后换一个模型重新布防"),
            "quota:target-unbound" => ("failure", "需要重新布防", "旧布防没有固定续跑目标"),
            "not-started:token-expiring" => ("waiting", "等登录令牌续期", "访问令牌剩余不足 45 分钟，等 Claude Code 续期后再启动"),
            "not-started:session-in-use" or "not-started:cwd-busy" => ("waiting", "会话刚被打开 · 已跳过", "启动前复核时会话正开在客户端，这次跳过，下一轮再评估"),
            "not-started:evidence-changed" => ("waiting", "会话有新记录", "启动前复核时会话结尾变了，下一轮重新评估"),
            "not-started:evidence-unverified" => ("waiting", "会话记录未核实", "启动前读不出会话记录的结尾，下一轮重试"),
            "not-started:execution-unverified" => ("waiting", "执行条件未核实", note.Detail is { Length: > 0 } d ? d : "下一轮重试"),
            "not-started:configuration-changed" => ("waiting", "配置刚变化 · 下次重试", "启动前发现布防配置被改过，这次不启动"),
            "not-started:no-claude" => ("failure", "执行器不可用", "找不到 Claude Code 可执行文件或项目目录"),
            _ when reason.StartsWith("not-started:", StringComparison.Ordinal) => ("waiting", "本次未启动 · 下次重试", string.Empty),
            _ when reason.StartsWith("quota:", StringComparison.Ordinal) => ("waiting", "额度未核实", string.Empty),
            _ => ("waiting", "状态未核实", string.Empty),
        };
    }

    private static string ClientText(string? client) => client switch
    {
        "claude-desktop" or "claude-desktop-3p" => " Claude 桌面端",
        "cli" => "终端",
        "claude-vscode" => " VS Code",
        _ => "客户端",
    };

    /// <summary><c>weekly_scoped:Fable:blocked,five_hour:blocked</c> → 「Fable 周额度、5 小时额度」。</summary>
    private static string WindowText(string? detail)
    {
        if (string.IsNullOrWhiteSpace(detail) || detail == "unattributed")
        {
            return string.Empty;
        }

        IEnumerable<string> names = detail.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(item => item.EndsWith(":blocked", StringComparison.Ordinal) ? item[..^":blocked".Length] : item)
            .Select(name => name switch
            {
                "five_hour" => "5 小时额度",
                "seven_day" => "7 天额度",
                _ when name.StartsWith("weekly_scoped:", StringComparison.OrdinalIgnoreCase) => name["weekly_scoped:".Length..] + " 周额度",
                _ => name,
            })
            .Distinct();
        return string.Join("、", names);
    }

    private static ProjectStatusItem DescribeProjectStatus(string path, string? status)
    {
        string normalizedStatus = status ?? string.Empty;
        (string category, string text) = normalizedStatus switch
        {
            "success" => ("success", "已完成"),
            "continued-elsewhere" => ("success", "已在客户端继续"),
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
        // 前置条件:调用方看到的周期必须仍是现在的周期。两个控制面窗口时,另一个窗口刚布防过,
        // 这里拒绝而不是静默替换掉别人的周期(连同它已消费的中断与逐项目进度)。
        string? expectCycleId = root.TryGetProperty("expectCycleId", out JsonElement expectEl) && expectEl.ValueKind == JsonValueKind.String
            ? expectEl.GetString() ?? string.Empty
            : null;
        if (armed && expectCycleId is not null && !string.Equals(expectCycleId, original.Armed ? original.ArmCycleId : string.Empty, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("布防状态已在别处改变(可能另一个控制面窗口刚操作过),请刷新后再试。");
        }

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
            // 逐项目模型:{路径: 模型调用 ID};空串或缺省 = 跟随该会话最近使用的模型。
            var projectModels = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (root.TryGetProperty("projectModels", out JsonElement modelsEl) && modelsEl.ValueKind == JsonValueKind.Object)
            {
                foreach (JsonProperty entry in modelsEl.EnumerateObject())
                {
                    string chosen = entry.Value.ValueKind == JsonValueKind.String ? entry.Value.GetString() ?? string.Empty : string.Empty;
                    if (chosen.Length > 0 && !ProviderModels.IsSafeId(chosen))
                    {
                        throw new ArgumentException("项目模型无效,请从供应商目录中选择。");
                    }

                    projectModels[entry.Name] = chosen;
                }
            }

            binding = await _resumeBinding(providerId, requestedModel, paths, projectModels, original, ct).ConfigureAwait(false);
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

        ArmPayload payload = GetArm();
        return binding is { Warnings.Count: > 0 } ? payload with { Warnings = binding.Warnings } : payload;
    }

    /// <summary>
    /// 布防前预演：逐项目只读本地会话事实(绑定的会话、会话最近用的模型、是否被限额打断、是否开在客户端)。
    /// 不发网络请求、不做推理、不写任何状态。
    /// </summary>
    private object PreviewArm(JsonElement root)
    {
        string providerId = root.TryGetProperty("providerId", out JsonElement providerEl) && providerEl.ValueKind == JsonValueKind.String
            ? providerEl.GetString()! : "claude-subscription";
        var paths = root.TryGetProperty("paths", out JsonElement pathsEl) && pathsEl.ValueKind == JsonValueKind.Array
            ? pathsEl.EnumerateArray().Select(e => e.GetString()).Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s!).ToList()
            : new List<string>();
        return new
        {
            items = _resumePreview(providerId, paths).Select(p => new
            {
                path = p.Path,
                name = p.Name,
                sessionId = p.SessionId,
                sessionModel = p.SessionModel,
                evidence = p.Evidence,
                limitType = p.LimitType,
                limitResetsAtUnix = p.LimitResetsAtUtc?.ToUnixTimeSeconds(),
                interruptedAtUnix = p.InterruptedAtUtc?.ToUnixTimeSeconds(),
                session = p.Session,
                client = p.Client,
                problem = p.Problem,
            }),
        };
    }

    // ── 完成通知 → 飞书 ──
    // 只回结论、来源与遮蔽后的标识;凭据实值从不出现在应答里。

    private static object ChannelPayload(NotifyChannelStatus status) => new
    {
        larkCliFound = status.LarkCliFound,
        credentialSource = status.CredentialSource,
        appIdMasked = status.AppIdMasked,
        legacyCredentialAvailable = status.LegacyCredentialAvailable,
        recipientSet = status.RecipientSet,
        recipientMasked = status.RecipientMasked,
        // 最近一次成功送达时的凭据/收件人指纹与现在是否一致;null = 还没有成功记录或无法比较。
        settingsVerified = status.SettingsVerified,
        health = status.Health is { } health
            ? new
            {
                state = health.State,
                reason = health.Reason,
                lastAttemptOk = health.LastAttemptOk,
                lastSuccessAtUnix = health.LastSuccessUtc?.ToUnixTimeSeconds(),
                lastAttemptAtUnix = health.LastAttemptUtc?.ToUnixTimeSeconds(),
                blockedUntilUnix = health.BlockedUntilUtc?.ToUnixTimeSeconds(),
                lastErrorClass = health.LastErrorClass,
                lastErrorCode = health.LastErrorCode,
                lastLogId = health.LastLogId,
                backlog = health.Backlog,
                dead = health.Dead,
                credentialSource = health.CredentialSource,
                updatedAtUnix = health.UpdatedAtUtc.ToUnixTimeSeconds(),
            }
            : null,
    };

    private async Task<object> DetectPayloadAsync(CancellationToken cancellationToken)
    {
        RecipientCandidates found = await _notify.DetectRecipientsAsync(cancellationToken).ConfigureAwait(false);
        return new
        {
            filteredByApp = found.FilteredByApp,
            items = found.Users.Select(user => new { openId = user.UserOpenId, userName = user.UserName, tokenStatus = user.TokenStatus }),
        };
    }

    private static object CheckPayload(LarkDoctorReport report) => new
    {
        ok = report.Ok,
        botReady = report.BotReady,
        credentialSource = report.CredentialSource,
        problem = report.Problem,
        checks = report.Checks.Select(check => new { name = check.Name, status = check.Status, message = check.Message }),
    };

    private static string ReadString(JsonElement root, string name) =>
        root.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? string.Empty : string.Empty;

    // ── Claude Code 登录 ──
    // 只回结论与到期时间，不回任何令牌、邮箱或账号 UUID。

    private object ClaudeLoginStatus()
    {
        ClaudeCredentialStatus status = _claudeCredentials.Describe();
        // 本地文件时间戳说「有效」不等于服务端认:usage 端点刚以 401/403 拒绝过本机令牌、且之后登录文件没再换过时,
        // 登录卡片必须说出来(改密码、被移出组织、在别处登出,本地文件都还是新的)。
        QuotaService.OAuthRejection? rejection = _quota.LastRejection;
        bool serverRejected = rejection is not null && !CredentialsRewrittenSince(rejection.AtUtc);
        return new
        {
            verdict = status.Verdict.ToString(),
            serverRejected,
            accessTokenExpiresAtUnix = status.AccessTokenExpiresAtUtc?.ToUnixTimeSeconds(),
            loginExpiresAtUnix = status.LoginExpiresAtUtc?.ToUnixTimeSeconds(),
            subscriptionType = status.SubscriptionType,
            accountVerified = status.AccountVerified,
            problem = status.Problem,
        };
    }

    /// <summary>登录文件在这之后被改写过(多半是 /login 或令牌续期):那次拒绝说的是旧令牌,不再作数。</summary>
    private bool CredentialsRewrittenSince(DateTimeOffset atUtc)
    {
        try
        {
            string path = _claudeCredentials.CredentialsPath;
            return File.Exists(path) && File.GetLastWriteTimeUtc(path) > atUtc.UtcDateTime;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
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

    private sealed record AppInfoPayload(
        [property: JsonPropertyName("version")] string Version,
        [property: JsonPropertyName("shadowRoot")] string ShadowRoot,
        [property: JsonPropertyName("quotaRefreshMinutes")] int QuotaRefreshMinutes);

    private sealed record NotificationsPayload(
        [property: JsonPropertyName("items")] IReadOnlyList<NotificationItem> Items);

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
        [property: JsonPropertyName("probeReason")] string ProbeReason = "")
    {
        /// <summary>逐项目：会话、模型、状态与等待原因(按 Selected 顺序)。</summary>
        [JsonPropertyName("projects")]
        public IReadOnlyList<ArmProjectItem> Projects { get; init; } = [];

        /// <summary>布防时需要告诉用户、但不阻止布防的提示(如会话正开在客户端、会话模型不在目录)。</summary>
        [JsonPropertyName("warnings")]
        public IReadOnlyList<string>? Warnings { get; init; }

        /// <summary>
        /// 后台 Worker 是否在跑(不论是否布防):完成通知和续跑都由它执行。true 要有同名进程且常驻 Worker 的
        /// Named Pipe 应答;null = 核验不出(进程枚举失败,或有同名进程但管道不应答),界面显示未核实而不是「在跑」。
        /// null 是有意义的结论,必须原样写出:桥的序列化选项默认丢掉 null 字段,字段一缺,页面就分不清
        /// 「核验不出」和「还没读到」——页脚空白,机器人行照样显示可送达。
        /// </summary>
        [JsonPropertyName("workerRunning")]
        [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
        public bool? WorkerRunning { get; init; }
    }

    private sealed record ArmProjectItem(
        [property: JsonPropertyName("path")] string Path,
        [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("sessionId")] string SessionId,
        [property: JsonPropertyName("modelId")] string ModelId,
        [property: JsonPropertyName("modelName")] string ModelName,
        [property: JsonPropertyName("modelSource")] string ModelSource,
        [property: JsonPropertyName("status")] string? Status,
        [property: JsonPropertyName("category")] string Category,
        [property: JsonPropertyName("reason")] string? Reason,
        [property: JsonPropertyName("reasonText")] string ReasonText,
        [property: JsonPropertyName("hint")] string Hint,
        [property: JsonPropertyName("detail")] string? Detail,
        [property: JsonPropertyName("resetAtUnix")] long? ResetAtUnix,
        [property: JsonPropertyName("interruptedAtUnix")] long? InterruptedAtUnix,
        [property: JsonPropertyName("client")] string? Client);

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
        [property: JsonPropertyName("hookBroken")] bool HookBroken,
        [property: JsonPropertyName("unverified")] bool Unverified = false);
}
