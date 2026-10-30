using AiResume.Core;
using AiResume.Worker.Probes;

namespace AiResume.Worker.Resume;

public sealed record BoundResumeSelection(ResumeTarget Target, IReadOnlyList<ProjectRef> Projects);

public sealed class ResumeBindingService(ProviderCatalogService providers, ResumeSessionCatalog? sessions = null)
{
    private readonly ResumeSessionCatalog _sessions = sessions ?? new();

    public async Task<BoundResumeSelection> BindAsync(string providerId, string modelId,
        IReadOnlyList<string> paths, ProductConfig config, CancellationToken ct)
    {
        ResumeTarget target = await providers.BindAsync(providerId, modelId, ct).ConfigureAwait(false);
        IReadOnlyList<ProjectRef> projects = _sessions.Bind(target.Agent, paths);
        config.ResumeTarget = target;
        config.ResumeModel = target.ModelId;
        var execution = new ProviderExecutionPlan(providers.Registry, _sessions);
        // 布防时就把每个项目的执行计划完整走一遍(不 spawn):官方 ChatGPT 也不例外,
        // 不能等到额度恢复那一刻才发现 Codex 未登录或管理配置锁不住。
        foreach (ProjectRef project in projects) { using ResumeExecutionPlan plan = execution.Create(project, config); }
        if (!execution.IsCurrent(target)) throw new InvalidOperationException("布防期间供应商已改变，请重新布防");
        return new(target, projects);
    }
}
