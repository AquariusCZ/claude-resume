using System.Text;
using System.Text.Json;
using AiResume.Core;
using AiResume.Core.Contracts;
using AiResume.Storage;
using AiResume.Worker.Probes;
using AiResume.Worker.Resume;
using AiResume.Worker.Supervision;
using Xunit;

namespace AiResume.Tests;

[Collection(SqliteCollection.Name)]
public sealed class ProviderExecutionTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("test-only-bearer")]
    public void 静态连接保留原Provider且秘密只在环境中(string? bearer)
    {
        string root = TestTemp.NewDir("provider-plan");
        string project = TestTemp.NewDir("provider-project");
        string session = Guid.NewGuid().ToString("D");
        string sessions = Path.Combine(root, "sessions");
        Directory.CreateDirectory(sessions);
        File.WriteAllText(Path.Combine(sessions, "rollout-" + session + ".jsonl"), JsonSerializer.Serialize(
            new { type = "session_meta", payload = new { id = session, cwd = project, source = "cli" } }));
        string configPath = Path.Combine(root, "config.toml");
        File.WriteAllText(configPath, "[model_providers.relay]\nname=\"relay\"\nbase_url=\"https://test.invalid/v1\"\n");
        string managedPath = Path.Combine(root, "managed_config.toml");
        File.WriteAllText(managedPath, "# isolated existing managed layer\n");
        var credentials = new CodexProviderCredentials("https://test.invalid/v1", bearer, "test-model", "responses",
            new Dictionary<string, string>(), new Dictionary<string, string> { ["X-Api-Key"] = "test-only-header" },
            "none", null, null, "relay", false, false);
        var registry = new ProviderRegistry(_ => null, root, () => credentials);
        ProviderConnection connection = registry.Find("codex-active")!;
        var config = ProductConfig.CreateDefault();
        config.ResumeModel = "test-model";
        config.ResumeTarget = new() { ProviderId = connection.Id, ProviderIdentity = connection.Identity, Agent = "codex", ModelId = "test-model" };
        config.ResumePrompt = "continue\nquotes \" and $(literal)";
        var planner = new ProviderExecutionPlan(registry, new ResumeSessionCatalog(root, sessions),
            _ => new AgentCommand(Path.Combine(root, "never-run.exe"), Array.Empty<string>()),
            (_, _) => new[] { configPath, managedPath }, root, () => true);
        using (ResumeExecutionPlan plan = planner.Create(new ProjectRef { Path = project, SessionId = session }, config))
        {
            Assert.Contains("model_provider=\"relay\"", plan.Arguments);
            Assert.Equal(root, plan.Environment["CODEX_HOME"]);
            Assert.Contains(session, plan.Arguments);
            Assert.Contains(config.ResumePrompt, plan.Arguments);
            Assert.DoesNotContain("test-only-header", string.Join(" ", plan.Arguments), StringComparison.Ordinal);
            Assert.DoesNotContain("test-only-bearer", string.Join(" ", plan.Arguments), StringComparison.Ordinal);
            Assert.Equal("test-only-header", plan.Environment["AI_RESUME_HEADER_0"]);
            Assert.Equal(bearer is not null, plan.Environment.ContainsKey("AI_RESUME_PROVIDER_KEY"));
            Assert.Throws<IOException>(() => File.WriteAllText(configPath, "changed"));
            Assert.Throws<IOException>(() => File.WriteAllText(managedPath, "model_provider=\"other\""));
        }
        File.WriteAllText(configPath, "released");
        File.Delete(managedPath);
        Assert.Throws<InvalidOperationException>(() => planner.Create(new ProjectRef { Path = project, SessionId = session }, config));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(7)]
    public async Task 隔离假程序只有输出收敛后才记录实际退出码(int exitCode)
    {
        string root = TestTemp.NewDir("agent-helper");
        string database = Path.Combine(root, "runs.db");
        StorageDatabase.Migrate(database);
        using var supervisor = new ProcessSupervisor(database);
        string output = Path.Combine(root, "stdout");
        string error = Path.Combine(root, "stderr");
        string script = "[Console]::Out.Write('fixture-output'); [Console]::Error.Write('fixture-error'); exit " + exitCode;
        var spec = new AgentExecSpec(Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe"),
            new[] { "-NoProfile", "-NonInteractive", "-EncodedCommand", Convert.ToBase64String(Encoding.Unicode.GetBytes(script)) }, output, error);
        RunId run = RunId.New();
        ProcessStartResult started = await supervisor.StartAsync(new ProcessStartRequest
        {
            RunId = run, FileName = Path.Combine(AppContext.BaseDirectory, "AiResume.Worker.exe"),
            Arguments = "internal-agent-run " + AgentExecCommand.Encode(spec), WorkingDirectory = root,
            CommandSignature = "AiResume.Worker.exe", Environment = new Dictionary<string, string?> { ["AI_RESUME_INTERNAL_RUN"] = "1" },
        }, default);
        Assert.True(started.Started);
        Assert.Null(started.ErrorCode);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        while ((await supervisor.StatusAsync(run, timeout.Token)).Liveness != ProcessLiveness.Gone)
            await Task.Delay(50, timeout.Token);
        Assert.Equal("fixture-output", File.ReadAllText(output));
        Assert.Equal("fixture-error", File.ReadAllText(error));
        Assert.Equal(exitCode.ToString(), File.ReadAllText(output + ".exit"));
    }
}
