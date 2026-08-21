using AiResume.Worker.Probes;
using AiResume.Worker.Quota;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace AiResume.Tests;

public sealed class WorkerStartupContractTests
{
    [Fact]
    public void Recovery_bootstrap_is_a_serial_startup_gate()
    {
        string source = File.ReadAllText(FindRepositoryFile(
            Path.Combine("csharp", "src", "AiResume.Worker", "Program.cs")));

        Assert.Contains("options.ServicesStartConcurrently = false", source, StringComparison.Ordinal);

        int recovery = source.IndexOf("AddHostedService<ProcessRecoveryBootstrap>()", StringComparison.Ordinal);
        int transport = source.IndexOf("AddHostedService<TransportBootstrap>()", StringComparison.Ordinal);
        int observation = source.IndexOf("AddHostedService<ObservationWorker>()", StringComparison.Ordinal);
        int resume = source.IndexOf("GetRequiredService<ResumeEngine>()", StringComparison.Ordinal);

        Assert.True(recovery >= 0);
        Assert.True(transport > recovery);
        Assert.True(observation > recovery);
        Assert.True(resume > recovery);
    }

    [Fact]
    public void Resume_engine_uses_complete_quota_service_in_production()
    {
        string source = File.ReadAllText(FindRepositoryFile(
            Path.Combine("csharp", "src", "AiResume.Worker", "Program.cs")));
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddQuotaResumeAdmission();
        using ServiceProvider provider = services.BuildServiceProvider(
            new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });

        IClaudeUsageProbe admission = provider.GetRequiredService<IClaudeUsageProbe>();

        Assert.IsType<QuotaResumeProbe>(admission);
        Assert.Same(admission, provider.GetRequiredService<IClaudeUsageProbe>());
        Assert.Contains("builder.Services.AddQuotaResumeAdmission();", source, StringComparison.Ordinal);
        Assert.DoesNotContain("AddSingleton<IClaudeUsageProbe>", source, StringComparison.Ordinal);
        Assert.DoesNotContain("new ClaudeCodeProbe()", source, StringComparison.Ordinal);
    }

    private static string FindRepositoryFile(string relativePath)
    {
        DirectoryInfo? current = new(AppContext.BaseDirectory);
        while (current is not null)
        {
            string candidate = Path.Combine(current.FullName, relativePath);
            if (File.Exists(candidate))
            {
                return candidate;
            }

            current = current.Parent;
        }

        throw new FileNotFoundException($"找不到仓库文件:{relativePath}");
    }
}
