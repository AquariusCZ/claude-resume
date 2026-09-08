using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace AiResume.Worker.Resume;

/// <summary>
/// 受 ProcessSupervisor/Job 监督的短包装进程。使用 .NET ArgumentList，
/// 将供应商模型 ID、提示词作为参数数据传递，不经 cmd/PowerShell 解释。
/// 不初始化 Worker Host，不把凭据序列化进命令行。
/// </summary>
public sealed record AgentExecSpec(string Executable, IReadOnlyList<string> Arguments,
    string OutputPath, string ErrorPath);

public static class AgentExecCommand
{
    public static string Encode(AgentExecSpec spec) => Convert.ToBase64String(
        Encoding.UTF8.GetBytes(JsonSerializer.Serialize(spec)));

    public static async Task<int> RunAsync(string[] args)
    {
        if (args.Length != 2 || Environment.GetEnvironmentVariable("AI_RESUME_INTERNAL_RUN") != "1") return 2;
        try
        {
            AgentExecSpec? spec = JsonSerializer.Deserialize<AgentExecSpec>(Convert.FromBase64String(args[1]));
            if (spec is null || !Path.IsPathFullyQualified(spec.Executable) || !File.Exists(spec.Executable) ||
                !Path.IsPathFullyQualified(spec.OutputPath) || !Path.IsPathFullyQualified(spec.ErrorPath)) return 2;
            using var output = new FileStream(spec.OutputPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
            using var error = new FileStream(spec.ErrorPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
            var start = new ProcessStartInfo(spec.Executable)
            {
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true,
            };
            foreach (string argument in spec.Arguments) start.ArgumentList.Add(argument);
            using Process process = Process.Start(start) ?? throw new IOException("无法启动执行器");
            process.StandardInput.Close();
            Task stdout = process.StandardOutput.BaseStream.CopyToAsync(output);
            Task stderr = process.StandardError.BaseStream.CopyToAsync(error);
            try
            {
                Task closed = process.WaitForExitAsync();
                // 输出落盘故障必须立即收敛子进程，不能等阻塞的 stdout 管道自行结束。
                foreach (Task copy in new[] { stdout, stderr })
                    _ = copy.ContinueWith(_ =>
                    {
                        try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
                        catch (Exception) { /* Job 仍由监督器持有。 */ }
                    }, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
                await Task.WhenAll(stdout, stderr, closed).ConfigureAwait(false);
                await output.FlushAsync().ConfigureAwait(false);
                await error.FlushAsync().ConfigureAwait(false);
                await File.WriteAllTextAsync(spec.OutputPath + ".exit", process.ExitCode.ToString(System.Globalization.CultureInfo.InvariantCulture)).ConfigureAwait(false);
                return process.ExitCode;
            }
            finally
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                    await process.WaitForExitAsync().ConfigureAwait(false);
                }
            }
        }
        catch (Exception) { return 3; }
    }
}
