using AiResume.Core;

namespace AiResume.Worker.Probes;

public sealed class ProviderResumeProbe(ProviderCatalogService providers, AiResume.Worker.Resume.ResumeSessionCatalog? sessions = null) : IClaudeUsageProbe
{
    private readonly AiResume.Worker.Resume.ResumeSessionCatalog _sessions = sessions ?? new();
    public Task<ClaudeProbeResult> ProbeAsync(string model, string workingDirectory, CancellationToken cancellationToken = default)
        => Task.FromResult(new ClaudeProbeResult { Reason = "target-unbound" });

    public async Task<ClaudeProbeResult> ProbeAsync(ProductConfig config, string workingDirectory, CancellationToken cancellationToken = default)
    {
        if (config.ResumeTarget is not { IsValid: true } target || target.ModelId != config.ResumeModel)
            return new ClaudeProbeResult { Reason = "target-unbound" };
        ClaudeProbeResult result = await providers.CheckAsync(target, cancellationToken).ConfigureAwait(false);
        if (result.Ready && target.Agent == "claudecode" &&
            config.Selected.Any(p => _sessions.IsClaudeSessionIdle(p.SessionId) != true))
            return result with { Ready = false, Reason = "session-in-use" };
        return result;
    }
}
