using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using AiResume.Core;
using AiResume.Storage;
using AiResume.Worker;
using AiResume.Worker.Migration;
using AiResume.Worker.Notifications;
using AiResume.Worker.Probes;
using AiResume.Worker.Products;
using AiResume.Worker.Quota;
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

    public ControlPlaneBridge(
        ProjectCatalog? catalog = null,
        Func<ProductConfig>? configFactory = null,
        NotificationRegistry? notificationRegistry = null,
        QuotaService? quotaService = null,
        ProductConfigStore? configStore = null,
        ProductStateStore? stateStore = null,
        Func<CancellationToken, Task<string?>>? folderPicker = null)
    {
        // 选目录必须回到 UI 线程弹原生对话框,由宿主窗口注入;测试注入替身,不弹窗。
        _folderPicker = folderPicker;
        _quota = quotaService ?? new QuotaService();
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

    /// <summary>处理一条前端请求,返回应答 JSON;任何异常都转成 error 应答,不向宿主抛出。</summary>
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

            object payload = type switch
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
                "cutover.generate" => await Task.Run(() => GenerateCutoverConfig(), cancellationToken).ConfigureAwait(false),
                "cutover.preflight" => await Task.Run(() => Preflight(), cancellationToken).ConfigureAwait(false),
                "app.info" => AppInfo(),
                "notifications.list" => await Task.Run(() => ListNotifications(), cancellationToken).ConfigureAwait(false),
                "notifications.setEnabled" => await Task.Run(() => SetNotificationEnabled(root), cancellationToken).ConfigureAwait(false),
                "quota.get" => await GetQuotaAsync(root, cancellationToken).ConfigureAwait(false),
                "quota.local" => await Task.Run(() => GetLocalBlock(), cancellationToken).ConfigureAwait(false),
                "arm.get" => await Task.Run(() => GetArm(), cancellationToken).ConfigureAwait(false),
                "arm.set" => await Task.Run(() => SetArm(root), cancellationToken).ConfigureAwait(false),
                "providers.probe" => await ProbeProvidersAsync(root, cancellationToken).ConfigureAwait(false),
                "agent.get" => await Task.Run(() => GetAgent(), cancellationToken).ConfigureAwait(false),
                "agent.set" => await Task.Run(() => SetAgent(root), cancellationToken).ConfigureAwait(false),
                _ => throw new NotSupportedException($"未知请求类型:{type}"),
            };

            return Serialize(new Envelope(id, type + ".result", payload, null));
        }
        catch (Exception ex)
        {
            return Serialize(new Envelope(id, (type ?? "unknown") + ".error", null, ex.Message));
        }
    }

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

        // 构造 hook 命令:exe 完整路径 + 空格 + 小写 source 参数。
        //
        // 原本只在 AppContext.BaseDirectory 下找,而开发布局里 Hook 输出在它自己的
        // bin 目录、根本不会被复制过来 —— 于是这里恒抛 FileNotFound,启用永远失败。
        // 改用统一解析器(同目录 → 开发布局的同级项目 bin → PATH)。
        string hookExe = HookExecutable.TryResolve()
            ?? throw new FileNotFoundException(
                $"未找到 {HookExecutable.FileName}。请先构建 AiResume.Hook 项目;" +
                "钩子必须写绝对路径,写不存在的路径会表现为「已启用但永远收不到通知」。");

        string sourceArg = kind switch
        {
            NotificationProviderKind.ClaudeCode => "claudecode",
            NotificationProviderKind.Codex => "codex",
            NotificationProviderKind.Cline => "cline",
            NotificationProviderKind.Qoder => "qoder",
            NotificationProviderKind.OpenCode => "opencode",
            _ => throw new NotSupportedException($"不支持的提供程序类型: {kind}"),
        };
        string hookCommand = $"{hookExe} {sourceArg}";

        _notificationRegistry.SetEnabled(kind, enabled, hookCommand);

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

        var windows = (bucket?.Windows ?? Array.Empty<UsageWindow>()).Select(w => new QuotaWindow(
            w.Name,
            DescribeWindow(w.Name),
            w.Status,
            w.UsedPercent,
            w.WindowSeconds,
            w.ResetAtUnix,
            w.ResetAfterSeconds,
            w.DerivedWindowStart?.ToUnixTimeSeconds())).ToList();

        return new QuotaPayload(
            snapshot.Provider,
            snapshot.CapturedAt.ToUnixTimeSeconds(),
            snapshot.HasData,
            bucket?.LimitReached ?? false,
            snapshot.UnavailableReason,
            windows);
    }

    /// <summary>读布防现状 + 上一轮逐项目结果(供「预演」展示将按什么顺序续跑)。</summary>
    private ArmPayload GetArm()
    {
        ProductConfig config = _configStore.Load();
        CheckerState state = _stateStore.Load();

        var selected = config.Selected.Select(p => p.Path).ToList();
        var statuses = (state.ProjectStatus ?? new Dictionary<string, string>())
            .Select(kv => new ProjectStatusItem(kv.Key, kv.Value)).ToList();

        // **布防是意图,不是事实。** 只回 armed 的话,续跑 Worker 被杀掉之后
        // 面板照旧写着「监视中」(2026-08-08 审计 A4)——而用户会真的去睡觉。
        DateTimeOffset now = DateTimeOffset.UtcNow;
        EngineVerdict verdict = EngineLiveness.Evaluate(config, state, now);
        long? probeAge = state.LastProbeUtc is { } lp
            ? (long)Math.Max(0, (now - lp).TotalSeconds)
            : null;

        return new ArmPayload(
            config.Armed,
            config.Continuous,
            config.ArmCycleId,
            state.Phase,
            state.SawLimited,
            selected,
            statuses,
            verdict.ToString(),
            EngineLiveness.Describe(verdict),
            probeAge);
    }

    /// <summary>
    /// 布防/解除。布防时生成新的 <c>ArmCycleId</c>——周期 id 变化即让上一轮状态失效,
    /// 这是 <c>CheckerCycle</c> 隔离每次布防的唯一机制。
    ///
    /// **只改本次负责的字段后写回**:配置可能同时被 Worker 的续跑引擎修改(解除布防),
    /// 整体写回旧快照会互相覆盖。
    /// </summary>
    private ArmPayload SetArm(JsonElement root)
    {
        bool armed = root.TryGetProperty("armed", out JsonElement armedEl) && armedEl.ValueKind == JsonValueKind.True;

        var paths = new List<string>();
        if (armed)
        {
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
        }

        // 锁内重读后只改本次负责的字段:续跑引擎可能正好在周期结束时写同一份配置。
        _configStore.Update(config =>
        {
            if (armed)
            {
                // 名称由路径推导即可:它只用于日志与列表显示,GUI 自己那份列表才是展示真身。
                // **刻意不调 _catalog.Discover 去查名字**——那会为一个纯装饰字段引入一次全盘扫描,
                // 还会连带写索引文件,让"布防"这个本该纯粹的配置写入变得可能失败。
                config.Selected = paths.Select(p => new ProjectRef
                {
                    Path = p,
                    Name = Path.GetFileName(p.TrimEnd('\\', '/')) is { Length: > 0 } n ? n : p,
                }).ToList();

                config.Enabled = true;
                config.Armed = true;
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
            check = new CcConnectConfigCheck(
                CcConnectConfigState.Unknown, $"配置未能复核:{ex.Message}",
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
        return FeishuStatus();
    }

    private FeishuPayload ClearFeishu()
    {
        new FeishuCredentialStore().Clear();
        return FeishuStatus();
    }


    /// <summary>生成 cc-connect 配置。凭据由 <see cref="CutoverConfigCommand"/> 从 DPAPI 取,不经过本类。</summary>
    private CutoverPayload GenerateCutoverConfig()
    {
        CutoverConfigCommand.GenerateResult r = CutoverConfigCommand.Generate(
            CutoverConfigCommand.DefaultConfigPath, appId: null, appSecret: null);

        // SanitizedToml 里的 app_secret 已是 [REDACTED],可以安全展示给用户复核。
        return new CutoverPayload(r.Ok, r.Message, r.ProjectCount, r.SanitizedToml, r.OutPath);
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

    /// <summary>
    /// 读取当前 agent 选择与全部可选值。当前值经 Normalize 保证合法,
    /// 即使 shadow 配置被手改坏也不会把非法值回给前端。
    /// </summary>
    /// <summary>
    /// 探测各 provider 的**可用性**(不是额度)。
    ///
    /// 两档,对应项目铁律「绿色可用只能来自真实最小请求成功」:
    /// - 打开面板时走 shallow —— Codex 只跑 <c>codex doctor --json</c>,不发模型请求、不烧额度;
    /// - 用户点「刷新额度」时传 deep=true —— 才允许发一次最小真实请求。
    ///
    /// DeepSeek 两档相同:它查的是余额接口,本身不消耗 token,所以没必要分档。
    /// </summary>
    private async Task<object> ProbeProvidersAsync(JsonElement root, CancellationToken ct)
    {
        bool deep = root.TryGetProperty("deep", out JsonElement d) && d.ValueKind == JsonValueKind.True;

        Task<CodexProbeResult> codexTask = deep
            ? new CodexProbe().ProbeDeepAsync(ct)
            : new CodexProbe().ProbeShallowAsync(ct);
        Task<DeepSeekProbeResult> deepSeekTask = new DeepSeekProbe().ProbeAsync(ct);

        await Task.WhenAll(codexTask, deepSeekTask).ConfigureAwait(false);

        CodexProbeResult codex = codexTask.Result;
        DeepSeekProbeResult ds = deepSeekTask.Result;

        // state 只有三种,前端据此上色:ok(绿) / bad(红) / idle(灰)。
        // **不存在"看起来应该没问题"这一档** —— 没验证过就是灰。
        // 四态,与面板的颜色约定一致:ok=绿(正常) / wait=琥珀(在等,不是故障) /
        // bad=红(需要动手修) / idle=灰(没验证过)。
        // **被限流不是故障** —— 拿红色标它,真出问题时的红就不值钱了。
        // 默认探测现在也做**带凭据的真实请求**(1.3 秒、0 token),
        // 所以 DeepChecked 为真就给绿 —— 不再需要用户点一下才敢点亮。
        // DeepChecked 为假只可能是"读不到配置"或"网络失败",那仍然是灰的。
        static string CodexState(CodexProbeResult r, bool deep) => r.Readiness switch
        {
            CodexReadiness.Ok => r.DeepChecked ? "ok" : "idle",
            CodexReadiness.Limited => "wait",
            CodexReadiness.Auth or CodexReadiness.Unreachable => "bad",
            _ => "idle",
        };

        static string DeepSeekState(DeepSeekProbeResult r) => r.Readiness switch
        {
            ProviderReadiness.Ok => "ok",
            // 余额不足要充值,是真的要动手,归红;认证/不可达同理。
            ProviderReadiness.Auth or ProviderReadiness.Insufficient or ProviderReadiness.Unreachable => "bad",
            _ => "idle",
        };

        return new
        {
            deep,
            items = new object[]
            {
                new
                {
                    name = "DeepSeek",
                    state = DeepSeekState(ds),
                    // **余额本身就是那一行该说的话。** 上一版为了压短统一换成「可用」,
                    // 把唯一有信息量的数字丢掉了 —— 用户问的正是"我还有多少钱"。
                    // 而且它本来就短:¥48.23 比「余额充足」还短两个字。
                    text = ds.Readiness == ProviderReadiness.Ok && ds.BalanceCny is { } bal
                        ? "¥" + bal.ToString("0.##")
                        : ShortLabel(ds.Readiness.ToString(), ds.Summary),
                    detail = ds.Summary ?? "未探测",
                },
                new
                {
                    name = "Codex",
                    state = CodexState(codex, deep),
                    // 侧栏只有一行的宽度,长句必然被省略号截掉 ——
                    // 而被截掉的恰恰是结论那几个字(实测显示成"可用 · 凭据与推理已…")。
                    // 所以这里给**短标签**,完整那句放 detail,由前端挂到 title 上。
                    text = ShortLabel(codex.Reason, codex.Summary),
                    detail = codex.Summary ?? "未探测",
                },
            },
        };
    }

    /// <summary>
    /// 把探测结论压成 2–5 个字。
    ///
    /// 不是为了好看:侧栏一行放不下整句,截断之后剩的是前半句,
    /// 而结论在后半句 —— 截断等于把最有用的部分丢掉。短标签 + title 里的全文,
    /// 一眼能看懂,想细看也拿得到。
    /// </summary>
    internal static string ShortLabel(string? reason, string? summary) => reason switch
    {
        "authorized" => (summary ?? string.Empty).Contains("未核实", StringComparison.Ordinal)
            ? "未验推理"      // 凭据过了,但推理那一步没验到 —— 不能说成"已验证"
            : "已验证",
        "no-inference" => "不能推理",
        "auth-rejected" or "auth" => "凭据被拒",
        "http-429" or "limited" => "被限流",
        "server-error" => "服务端异常",
        "unverified" => "未验证",
        "no-cli" => "未安装",
        "timeout" => "探测超时",
        "unreachable" => "网络不可达",
        "Ok" => "可用",
        "Auth" => "凭据被拒",
        "Insufficient" => "余额不足",
        "Unreachable" => "网络不可达",
        _ => string.IsNullOrWhiteSpace(summary) ? "未探测" : "未探测",
    };

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
        [property: JsonPropertyName("outPath")] string? OutPath);

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
        [property: JsonPropertyName("cycleId")] string CycleId,
        [property: JsonPropertyName("phase")] string Phase,
        [property: JsonPropertyName("sawLimited")] bool SawLimited,
        [property: JsonPropertyName("selected")] IReadOnlyList<string> Selected,
        [property: JsonPropertyName("projectStatus")] IReadOnlyList<ProjectStatusItem> ProjectStatus,
        [property: JsonPropertyName("engine")] string Engine,
        [property: JsonPropertyName("engineText")] string EngineText,
        [property: JsonPropertyName("probeAgeSeconds")] long? ProbeAgeSeconds);

    private sealed record ProjectStatusItem(
        [property: JsonPropertyName("path")] string Path,
        [property: JsonPropertyName("status")] string Status);

    private sealed record QuotaPayload(
        [property: JsonPropertyName("provider")] string Provider,
        [property: JsonPropertyName("capturedAtUnix")] long CapturedAtUnix,
        [property: JsonPropertyName("hasData")] bool HasData,
        [property: JsonPropertyName("limitReached")] bool LimitReached,
        [property: JsonPropertyName("unavailableReason")] string? UnavailableReason,
        [property: JsonPropertyName("windows")] IReadOnlyList<QuotaWindow> Windows);

    private sealed record QuotaWindow(
        [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("label")] string Label,
        [property: JsonPropertyName("status")] string Status,
        [property: JsonPropertyName("usedPercent")] int? UsedPercent,
        [property: JsonPropertyName("windowSeconds")] int WindowSeconds,
        [property: JsonPropertyName("resetAtUnix")] long? ResetAtUnix,
        [property: JsonPropertyName("resetAfterSeconds")] int? ResetAfterSeconds,
        [property: JsonPropertyName("windowStartUnix")] long? WindowStartUnix);

    private sealed record NotificationItem(
        [property: JsonPropertyName("kind")] string Kind,
        [property: JsonPropertyName("displayName")] string DisplayName,
        [property: JsonPropertyName("isInstalled")] bool IsInstalled,
        [property: JsonPropertyName("isEnabled")] bool IsEnabled,
        [property: JsonPropertyName("configPath")] string? ConfigPath,
        [property: JsonPropertyName("detail")] string? Detail,
        [property: JsonPropertyName("hookBroken")] bool HookBroken);
}
