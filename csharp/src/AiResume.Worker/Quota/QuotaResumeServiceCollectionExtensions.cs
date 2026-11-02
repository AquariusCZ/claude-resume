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
        services.AddSingleton<QuotaResumeProbe>();
        services.AddSingleton<ProviderRegistry>();
        // 会话证据与占用由同一个目录实例提供(会话文件路径缓存共享):取证、执行计划与最终门禁看到的是同一套事实。
        services.AddSingleton<AiResume.Worker.Resume.ResumeSessionCatalog>();
        services.AddSingleton<AiResume.Worker.Resume.IProjectSessionInspector>(sp =>
            sp.GetRequiredService<AiResume.Worker.Resume.ResumeSessionCatalog>());
        services.AddSingleton(sp => new ProviderCatalogService(sp.GetRequiredService<ProviderRegistry>(),
            quota: sp.GetRequiredService<QuotaService>(), quotaProbe: sp.GetRequiredService<QuotaResumeProbe>()));
        services.AddSingleton<IClaudeUsageProbe, ProviderResumeProbe>();
        return services;
    }
}
