using System.Diagnostics;

namespace AiResume.LarkCli;

/// <summary>
/// 最小 lark-cli 进程封装(S3-A)。
/// 契约:命令构造、进程启动、超时/取消、stdout/stderr 捕获、envelope JSON 解析、
/// exit 10 高风险确认提示原样透传、输出脱敏、结构化错误分类。
/// 不引入任何第三方依赖;不接触凭据实值(凭据由 lark-cli 自身管理)。
/// 任何路径下的文本输出都先脱敏再对外可见;envelope 从脱敏后文本解析,
/// 保证日志/异常/结果中不会出现机密形状。
/// </summary>
public sealed class LarkCliInvoker
{
    private readonly string _fileName;
    private readonly string[] _wrapperArgs;
    private readonly TimeSpan _timeout;
    private readonly LarkRedactor _redactor;

    /// <param name="fileName">可执行文件;生产为 "lark-cli"(经 PATH),测试可注入 cmd.exe 等。</param>
    /// <param name="wrapperArgs">启动包装参数(测试用 cmd.exe /c 脚本);生产一般为空。</param>
    /// <param name="timeout">单次调用超时;超时后终止整个进程树并抛 Timeout。</param>
    /// <param name="knownSecrets">已知机密值集合,用于输出全文置换。</param>
    public LarkCliInvoker(
        string fileName,
        string[]? wrapperArgs = null,
        TimeSpan? timeout = null,
        IEnumerable<string>? knownSecrets = null)
    {
        _fileName = fileName;
        _wrapperArgs = wrapperArgs ?? Array.Empty<string>();
        _timeout = timeout ?? TimeSpan.FromSeconds(15);
        _redactor = new LarkRedactor(knownSecrets);
    }

    /// <summary>执行一次 lark-cli 调用并返回结构化结果。取消/超时/exit 10 以异常表达。</summary>
    public async Task<LarkCliResult> InvokeAsync(IReadOnlyList<string> args, CancellationToken cancellationToken = default)
    {
        var psi = new ProcessStartInfo(_fileName)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var w in _wrapperArgs)
        {
            psi.ArgumentList.Add(w);
        }

        foreach (var a in args)
        {
            psi.ArgumentList.Add(a);
        }

        using var proc = Process.Start(psi)
            ?? throw new InvalidOperationException("lark-cli 进程启动失败(未安装或不可用)。");

        // 立即开始读流,避免输出超过管道缓冲导致子进程阻塞(经典死锁)。
        var stdoutTask = ReadAllAsync(proc.StandardOutput);
        var stderrTask = ReadAllAsync(proc.StandardError);

        // 取消:终止整个进程树(含 lark-cli 派生的子进程)。
        using var killOnCancel = cancellationToken.Register(static state =>
        {
            try
            {
                ((Process)state!).Kill(entireProcessTree: true);
            }
            catch
            {
                // 已退出或句柄失效:忽略。
            }
        }, proc);

        var exitTask = proc.WaitForExitAsync(CancellationToken.None);
        var timeoutTask = Task.Delay(_timeout);
        var completed = await Task.WhenAny(exitTask, timeoutTask);

        if (completed == timeoutTask)
        {
            try
            {
                proc.Kill(entireProcessTree: true);
            }
            catch
            {
                // 已退出:忽略。
            }

            await exitTask; // 等待真实退出后再报告。
            var timedOutStdout = await ReadSafeAsync(stdoutTask);
            var timedOutStderr = await ReadSafeAsync(stderrTask);
            throw new LarkCliException(
                LarkCliFailureKind.Timeout,
                $"lark-cli 在 {_timeout.TotalSeconds:0.#}s 内未完成,进程树已终止。",
                _redactor.Redact(timedOutStdout),
                _redactor.Redact(timedOutStderr));
        }

        // exitTask 完成:正常退出或取消导致的终止。取消优先于其他结果。
        cancellationToken.ThrowIfCancellationRequested();

        var stdout = await ReadSafeAsync(stdoutTask);
        var stderr = await ReadSafeAsync(stderrTask);

        // 所有文本先脱敏;envelope 从脱敏后文本解析(替换值仍是合法 JSON 字符串)。
        stdout = _redactor.Redact(stdout);
        stderr = _redactor.Redact(stderr);

        var envelope = LarkEnvelope.TryParse(stdout);
        if (proc.ExitCode == 10)
        {
            // 高风险写操作未确认:exit 10 阻断,原样透传提示,绝不自动 --yes。
            throw new LarkCliException(
                LarkCliFailureKind.HighRiskConfirmationRequired,
                envelope?.ErrorMessage ?? "高风险操作需要显式确认(lark-cli exit 10)。",
                stdout,
                stderr);
        }

        if (proc.ExitCode == 0 && envelope is null)
        {
            // lark-cli 契约:成功时 stdout 必须是 JSON envelope;非法即失败。
            throw new LarkCliException(
                LarkCliFailureKind.InvalidOutput,
                "退出码 0 但 stdout 不是合法 JSON envelope。",
                stdout,
                stderr);
        }

        return new LarkCliResult(proc.ExitCode, stdout, stderr, envelope);
    }

    private static async Task<string> ReadAllAsync(StreamReader reader)
    {
        try
        {
            return await reader.ReadToEndAsync().ConfigureAwait(false);
        }
        catch
        {
            return string.Empty;
        }
    }

    private static async Task<string> ReadSafeAsync(Task<string> task)
    {
        try
        {
            return await task.ConfigureAwait(false);
        }
        catch
        {
            return string.Empty;
        }
    }
}
