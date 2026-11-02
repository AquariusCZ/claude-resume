using AiResume.Core;
using AiResume.Worker.Probes;
using AiResume.Worker.Products;

namespace AiResume.Worker.Resume;

/// <summary>布防结果：固定的目标、逐项目绑定(会话 UUID + 模型)与需要告诉用户的提示(不阻止布防)。</summary>
public sealed record BoundResumeSelection(ResumeTarget Target, IReadOnlyList<ProjectRef> Projects)
{
    public IReadOnlyList<string> Warnings { get; init; } = [];
}

/// <summary>
/// 布防前预演的一行：只读本地会话事实(绑定哪个会话、会话最近用的模型、是否被限额打断、是否开在客户端),
/// 不发网络请求、不做推理。模型是否在目录里由界面拿自己的目录判断。
/// </summary>
public sealed record ProjectPreview(
    string Path,
    string Name,
    string? SessionId,
    string? SessionModel,
    string Evidence,
    string? LimitType,
    DateTimeOffset? LimitResetsAtUtc,
    DateTimeOffset? InterruptedAtUtc,
    string Session,
    string? Client,
    string? Problem);

public sealed class ResumeBindingService(ProviderCatalogService providers, ResumeSessionCatalog? sessions = null)
{
    private readonly ResumeSessionCatalog _sessions = sessions ?? new();

    public IReadOnlyList<ProjectPreview> Preview(string providerId, IReadOnlyList<string> paths)
    {
        string agent = providers.Registry.Find(providerId)?.Agent is "codex" ? "codex" : "claudecode";
        var normalized = new List<string>();
        var result = new List<ProjectPreview>();
        foreach (string path in paths)
        {
            try { normalized.Add(Path.GetFullPath(path)); }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                result.Add(new(path, path, null, null, "none", null, null, null, "n/a", null, "项目路径无效"));
            }
        }

        IReadOnlyDictionary<string, ProjectRef> bound = _sessions.BindAvailable(agent, normalized);
        foreach (string path in normalized.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            string name = Path.GetFileName(path.TrimEnd('\\', '/'));
            if (!bound.TryGetValue(path, out ProjectRef? project))
            {
                result.Add(new(path, name, null, null, "none", null, null, null, "n/a", null, $"没有可核实的 {agent} 会话"));
                continue;
            }

            if (agent != "claudecode")
            {
                result.Add(new(path, name, project.SessionId, null, "none", null, null, null, "n/a", null, null));
                continue;
            }

            ClaudeSessionEvidence? evidence = _sessions.ReadEvidence(project);
            SessionOccupancy occupancy = _sessions.Occupancy(project);
            bool interrupted = evidence?.InterruptedByLimit == true;
            result.Add(new(path, name, project.SessionId, evidence?.LastModel,
                evidence is null ? "unverified" : interrupted ? "interrupted" : "clear",
                interrupted ? evidence!.LimitType : null,
                interrupted ? evidence!.LimitResetsAtUtc : null,
                interrupted ? evidence!.LastAssistantAtUtc : null,
                occupancy.State, occupancy.Client, null));
        }

        return result;
    }

    public Task<BoundResumeSelection> BindAsync(string providerId, string modelId,
        IReadOnlyList<string> paths, ProductConfig config, CancellationToken ct) =>
        BindAsync(providerId, modelId, paths, new Dictionary<string, string>(), config, ct);

    /// <summary>
    /// 逐项目确定续跑模型并冻结：用户显式选的必须在本次实时目录里，否则整次布防拒绝;
    /// 留空时跟随该会话最近一次真实应答的模型，它不在目录里才退回默认模型(并提示)。
    /// 布防只核验执行条件，不因会话正开在客户端而失败——占用在启动前才检查，只挡那一个项目。
    /// </summary>
    public async Task<BoundResumeSelection> BindAsync(string providerId, string defaultModelId,
        IReadOnlyList<string> paths, IReadOnlyDictionary<string, string> projectModels, ProductConfig config, CancellationToken ct)
    {
        (ResumeTarget target, ProviderModelCatalog catalog) = await providers
            .BindWithCatalogAsync(providerId, defaultModelId, ct).ConfigureAwait(false);
        IReadOnlyList<ProjectRef> projects = _sessions.Bind(target.Agent, paths);
        var warnings = new List<string>();
        foreach (ProjectRef project in projects)
        {
            string? requested = projectModels.FirstOrDefault(pair => ProjectPaths.Equal(pair.Key, project.Path)).Value;
            string? sessionModel = string.IsNullOrEmpty(requested) && target.Agent == "claudecode"
                ? _sessions.ReadEvidence(project)?.LastModel
                : null;
            (ProviderModel chosen, string source) = ResolveProjectModel(project.Name, requested, sessionModel, catalog, target, warnings);
            project.ModelId = chosen.Id;
            project.ModelName = chosen.Name;
            project.ModelSource = source;
        }

        config.ResumeTarget = target;
        config.ResumeModel = target.ModelId;
        var execution = new ProviderExecutionPlan(providers.Registry, _sessions);
        // 布防时就把每个项目的执行计划完整走一遍(不 spawn):官方 ChatGPT 也不例外,
        // 不能等到额度恢复那一刻才发现 Codex 未登录或管理配置锁不住。
        foreach (ProjectRef project in projects)
        {
            using ResumeExecutionPlan plan = execution.Create(project, config, ExecutionPurpose.Arm);
            if (target.Agent == "claudecode" && _sessions.Occupancy(project).State is "in-use" or "cwd-busy")
                warnings.Add($"{project.Name}:会话正在客户端打开。额度恢复后先交给客户端自己继续，仍未继续会提醒你");
        }

        if (!execution.IsCurrent(target)) throw new InvalidOperationException("布防期间供应商已改变，请重新布防");
        return new(target, projects) { Warnings = warnings };
    }

    /// <summary>
    /// 一个项目最终冻结的续跑模型与来源;需要告诉用户的回退说明加进 <paramref name="warnings"/>,每次回退只给一句真实原因。
    /// Claude 订阅的额度门禁要知道模型族(fable/opus/sonnet/haiku)才能挑出相关的按模型窗口;
    /// 认不出的模型布防进去只会一直「额度未核实」地等下去，布防时就说清楚。
    /// </summary>
    public static (ProviderModel Model, string Source) ResolveProjectModel(string projectName, string? requested, string? sessionModel,
        ProviderModelCatalog catalog, ResumeTarget target, List<string> warnings)
    {
        ArgumentNullException.ThrowIfNull(warnings);
        (ProviderModel chosen, string source) = ChooseModel(projectName, requested, sessionModel, catalog, target);
        if (source == "default" && sessionModel is not null)
            warnings.Add($"{projectName}:会话模型 {sessionModel} 不在当前目录，改用默认模型 {target.ModelName}");
        if (target.ProviderId == "claude-subscription" && !ClaudeModelFamilies.TryNormalizeConfiguredModel(chosen.Id, out _))
        {
            bool defaultKnown = ClaudeModelFamilies.TryNormalizeConfiguredModel(target.ModelId, out _);
            if (source != "session" || !defaultKnown)
                throw new InvalidOperationException(
                    $"项目 {projectName} 的模型 {chosen.Id} 认不出模型族,AI Resume 无法按它的额度判断何时续跑;请换一个模型再布防");
            warnings.Add($"{projectName}:会话模型 {chosen.Id} 认不出模型族，额度无法核实，改用默认模型 {target.ModelName}");
            (chosen, source) = (new ProviderModel(target.ModelId, target.ModelName), "default");
        }

        return (chosen, source);
    }

    /// <summary>
    /// 一个项目的续跑模型：显式选择(user)必须在本次实时目录里，否则拒绝整次布防;
    /// 没选时跟随会话最近使用的模型(session),它不在目录里才退回默认模型(default)。
    /// </summary>
    public static (ProviderModel Model, string Source) ChooseModel(
        string projectName, string? requested, string? sessionModel, ProviderModelCatalog catalog, ResumeTarget target)
    {
        if (!string.IsNullOrEmpty(requested))
        {
            ProviderModel chosen = catalog.Models.SingleOrDefault(model => model.Id == requested && ProviderModels.IsSafeId(model.Id))
                ?? throw new InvalidOperationException($"项目 {projectName} 选择的模型不在当前供应商目录中，请刷新后重试");
            return (chosen, "user");
        }

        ProviderModel? followed = sessionModel is null
            ? null
            : catalog.Models.FirstOrDefault(model => model.Id == sessionModel && ProviderModels.IsSafeId(model.Id));
        return followed is not null
            ? (followed, "session")
            : (new ProviderModel(target.ModelId, target.ModelName), "default");
    }
}
