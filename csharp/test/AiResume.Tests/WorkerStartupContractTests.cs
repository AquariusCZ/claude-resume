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
