using AiResume.Worker.Probes;
using Microsoft.Extensions.DependencyInjection;

namespace AiResume.Worker.Quota;

/// <summary>生产续跑额度门禁的唯一 DI 注册入口。</summary>
public static class QuotaResumeServiceCollectionExtensions
{
    public static IServiceCollection AddQuotaResumeAdmission(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddSingleton<QuotaService>();
        services.AddSingleton<IClaudeUsageProbe, QuotaResumeProbe>();
        return services;
    }
}
