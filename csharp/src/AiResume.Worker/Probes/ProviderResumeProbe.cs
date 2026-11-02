using AiResume.Core;
using AiResume.Worker.Resume;

namespace AiResume.Worker.Probes;

/// <summary>
/// 续跑取证：一次供应商观测按模型给出额度结论，再逐项目叠加会话尾部证据与客户端占用。
/// 同账号同模型的项目共用同一份实时证据，不为每个项目重复请求。
/// </summary>
public sealed class ProviderResumeProbe(ProviderCatalogService providers, ResumeSessionCatalog? sessions = null) : IClaudeUsageProbe
{
    private readonly IProjectSessionInspector _sessions = sessions ?? new ResumeSessionCatalog();

    public Task<ClaudeProbeResult> ProbeAsync(string model, string workingDirectory, CancellationToken cancellationToken = default)
        => Task.FromResult(new ClaudeProbeResult { Reason = "target-unbound" });

    public async Task<ClaudeProbeResult> ProbeAsync(ProductConfig config, string workingDirectory, CancellationToken cancellationToken = default)
    {
        if (config.ResumeTarget is not { IsValid: true } target || target.ModelId != config.ResumeModel)
            return new ClaudeProbeResult { Reason = "target-unbound" };

        bool claude = target.Agent == "claudecode";
        // 先读本地事实(毫秒级),再发额度请求：会话证据比额度更早过时的话，状态机拿到的是新额度配旧尾部。
        var local = config.Selected
            .Select(project => (Project: project, Model: ResumeTarget.EffectiveModel(project, target),
                Evidence: claude ? _sessions.ReadEvidence(project) : null))
            .ToArray();
        IReadOnlyDictionary<string, ClaudeProbeResult> quota = await providers
            .CheckModelsAsync(target, local.Select(item => item.Model).Distinct(StringComparer.Ordinal).ToArray(), cancellationToken)
            .ConfigureAwait(false);

        var admissions = new List<ProjectAdmission>(local.Length);
        foreach ((ProjectRef project, string model, ClaudeSessionEvidence? evidence) in local)
        {
            ClaudeProbeResult modelQuota = quota.TryGetValue(model, out ClaudeProbeResult? found) ? found : new ClaudeProbeResult();
            SessionOccupancy occupancy = claude ? _sessions.Occupancy(project) : new SessionOccupancy("n/a");
            // 没有 uuid 的中断记录无法证明「同一次中断只续一次」,按未核实处理。
            bool limitRecord = evidence is { InterruptedByLimit: true, LastAssistantUuid.Length: > 0 };
            // 中断之后同目录新开的会话有了真实应答:用户已经换会话接着干，旧会话不再自动续;判断不了也只等待。
            bool? continuedElsewhere = limitRecord && evidence!.LastAssistantAtUtc is { } interruptedAt
                ? _sessions.ContinuedInAnotherSession(project, interruptedAt)
                : false;
            bool superseded = continuedElsewhere == true;
            bool unverified = claude && (evidence is null || (evidence.InterruptedByLimit && !limitRecord) || continuedElsewhere is null);
            bool interrupted = limitRecord && !superseded && !unverified;
            admissions.Add(new ProjectAdmission
            {
                Path = project.Path,
                ModelId = model,
                Quota = modelQuota.Ready ? "ok" : modelQuota.Reason is "ok" or "" ? "unknown" : modelQuota.Reason,
                QuotaDetail = modelQuota.Detail,
                QuotaResetUtc = modelQuota.BlockingResetUtc,
                Session = occupancy.State,
                SessionClient = occupancy.Client,
                Evidence = !claude ? "none" : unverified ? "unverified" : superseded ? "superseded" : interrupted ? "interrupted" : "clear",
                // superseded 也带上这次中断的 uuid:状态机据此把它记为已处理(新会话之后删掉或读不到也不回头续)。
                InterruptionId = interrupted || superseded ? evidence!.LastAssistantUuid : null,
                InterruptedAtUtc = interrupted ? evidence!.LastAssistantAtUtc : null,
                LimitType = interrupted ? evidence!.LimitType : null,
                LimitResetsAtUtc = interrupted ? evidence!.LimitResetsAtUtc : null,
            });
        }

        // 顶层字段只是兼容汇总:任一项目放行即 Ready;具体启动谁由状态机逐项目决定。
        ClaudeProbeResult first = quota.Values.FirstOrDefault() ?? new ClaudeProbeResult();
        bool anyReady = admissions.Any(a => a.Admissible);
        return first with
        {
            Ready = anyReady,
            Reason = anyReady ? "ok" : admissions.Any(a => a.Quota == "limited") ? "limited" : first.Reason,
            Projects = admissions,
        };
    }
}
