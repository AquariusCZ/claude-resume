using System.Diagnostics;
using AiResume.LarkCli;
using Xunit;

namespace AiResume.Tests;

/// <summary>
/// S3-A lark-cli 进程封装测试(全部离线)。
/// 假 CLI = 临时 .cmd 脚本经 cmd.exe /c 包装,可编程输出/退出码/挂起;Dispose 清理临时目录。
/// </summary>
public class LarkCliInvokerTests : IDisposable
{
    private readonly string _tempDir = TestTemp.NewDir("airesume-larkcli");

    public LarkCliInvokerTests()
    {
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_tempDir, recursive: true);
        }
        catch
        {
            // 清理失败不掩盖测试结论。
        }
    }

    private string WriteScript(string body)
    {
        var path = Path.Combine(_tempDir, "fake-" + Guid.NewGuid().ToString("N")[..8] + ".cmd");
        File.WriteAllText(path, body);
        return path;
    }

    private LarkCliInvoker NewInvoker(string scriptPath, TimeSpan? timeout = null, IEnumerable<string>? secrets = null)
        => new("cmd.exe", new[] { "/c", scriptPath }, timeout, secrets);

    [Fact]
    public async Task Invoke_SuccessEnvelope_ReturnsParsedData()
    {
        var script = WriteScript("@echo off\r\necho {\"ok\":true,\"data\":{\"x\":\"y\"}}\r\nexit /b 0");

        var result = await NewInvoker(script).InvokeAsync(new[] { "run" });
        
        Assert.Equal(0, result.ExitCode);
        Assert.NotNull(result.Envelope);
        Assert.True(result.Envelope!.Ok);
        using var envelope = result.Envelope;
        Assert.Equal("y", envelope.Data!.Value.GetProperty("x").GetString());
    }

    [Fact]
    public async Task Invoke_RecordedValidationErrorOnStderr_ParsesErrorEnvelope()
    {
        // lark-cli 1.0.81 本机录制:未知命令 exit=2,stdout 为空,错误信封在 stderr(含 _notice)。
        var script = WriteScript(
            "@echo off\r\necho {\"ok\":false,\"error\":{\"type\":\"validation\",\"subtype\":\"invalid_argument\",\"message\":\"unknown command for lark-cli\"},\"_notice\":{\"update\":{\"current\":\"1.0.81\",\"latest\":\"1.0.98\"}}} 1>&2\r\nexit /b 2");

        var result = await NewInvoker(script).InvokeAsync(new[] { "run" });

        Assert.Equal(2, result.ExitCode);
        Assert.Null(result.Envelope);
        using var error = result.ErrorEnvelope!;
        Assert.False(error.Ok);
        Assert.Equal("validation", error.ErrorType);
        Assert.Equal("invalid_argument", error.ErrorSubtype);
        Assert.Equal("unknown command for lark-cli", error.ErrorMessage);
    }

    [Fact]
    public async Task Invoke_ApiErrorWithCodeAndLogId_ParsesStructuredFields()
    {
        // 官方 errs/ERROR_CONTRACT.md 字段:code / log_id / retryable / retry_after_seconds。
        var script = WriteScript(
            "@echo off\r\necho {\"ok\":false,\"identity\":\"bot\",\"error\":{\"type\":\"api\",\"subtype\":\"rate_limit\",\"code\":230020,\"message\":\"rate limited\",\"log_id\":\"20261030-log\",\"retryable\":true,\"retry_after_seconds\":3}} 1>&2\r\nexit /b 1");

        var result = await NewInvoker(script).InvokeAsync(new[] { "run" });

        using var error = result.ErrorEnvelope!;
        Assert.Equal(230020, error.ErrorCode);
        Assert.Equal("20261030-log", error.LogId);
        Assert.True(error.Retryable);
        Assert.Equal(3, error.RetryAfterSeconds);
        Assert.Equal("bot", error.Identity);
    }

    [Fact]
    public async Task Invoke_Exit10_ThrowsHighRiskConfirmationWithHintFromStderr()
    {
        var script = WriteScript(
            "@echo off\r\necho {\"ok\":false,\"error\":{\"type\":\"confirmation\",\"message\":\"high risk write needs confirm\"}} 1>&2\r\nexit /b 10");

        var ex = await Assert.ThrowsAsync<LarkCliException>(() => NewInvoker(script).InvokeAsync(new[] { "run" }));

        Assert.Equal(LarkCliFailureKind.HighRiskConfirmationRequired, ex.Kind);
        Assert.Contains("high risk write needs confirm", ex.Stderr);
        Assert.Contains("high risk write needs confirm", ex.Message);
    }

    [Fact]
    public async Task Invoke_ArgumentsKeepNewlinesAndEnvironmentSecretsAreRedacted()
    {
        // 直接启动可执行文件(这里是 node)时参数原样到达;经 cmd.exe /c 会在第一个换行处截断(2026-10-30 复现)。
        const string echo = "const a=process.argv.slice(1);console.log(JSON.stringify({ok:true,data:{argv:a,app:process.env.LARKSUITE_CLI_APP_ID,secret:process.env.LARKSUITE_CLI_APP_SECRET}}))";
        var invoker = new LarkCliInvoker("node", new[] { "-e", echo, "--" }, environment: new Dictionary<string, string?>
        {
            ["LARKSUITE_CLI_APP_ID"] = "cli_test000000",
            ["LARKSUITE_CLI_APP_SECRET"] = "fake-app-secret-value",
        });

        var result = await invoker.InvokeAsync(new[] { "--text", "第一行\n第二行", "--idempotency-key", "0123456789abcdef" });

        using var envelope = result.Envelope!;
        string[] argv = envelope.Data!.Value.GetProperty("argv").EnumerateArray().Select(e => e.GetString()!).ToArray();
        Assert.Equal(new[] { "--text", "第一行\n第二行", "--idempotency-key", "0123456789abcdef" }, argv);
        Assert.Equal("cli_test000000", envelope.Data!.Value.GetProperty("app").GetString());
        Assert.Equal("[REDACTED]", envelope.Data!.Value.GetProperty("secret").GetString());
        Assert.DoesNotContain("fake-app-secret-value", result.Stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Invoke_Timeout_KillsProcessTreeAndThrows()
    {
        // 挂起 30 秒的 ping;超时 300ms 必须触发,且不能真的等 30 秒。
        var script = WriteScript("@echo off\r\nping -n 30 127.0.0.1 > NUL\r\necho {\"ok\":true}\r\nexit /b 0");
        var invoker = NewInvoker(script, TimeSpan.FromMilliseconds(300));

        var sw = Stopwatch.StartNew();
        var ex = await Assert.ThrowsAsync<LarkCliException>(() => invoker.InvokeAsync(new[] { "run" }));
        sw.Stop();

        Assert.Equal(LarkCliFailureKind.Timeout, ex.Kind);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(10), $"超时应约 300ms 触发,实际 {sw.Elapsed}");
    }

    [Fact]
    public async Task Invoke_Cancellation_ThrowsOperationCanceledAndKills()
    {
        var script = WriteScript("@echo off\r\nping -n 30 127.0.0.1 > NUL\r\nexit /b 0");
        var invoker = NewInvoker(script);
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => invoker.InvokeAsync(new[] { "run" }, cts.Token));
    }

    [Fact]
    public async Task Invoke_ZeroExitNonJson_ThrowsInvalidOutput()
    {
        var script = WriteScript("@echo off\r\necho hello world\r\nexit /b 0");

        var ex = await Assert.ThrowsAsync<LarkCliException>(() => NewInvoker(script).InvokeAsync(new[] { "run" }));

        Assert.Equal(LarkCliFailureKind.InvalidOutput, ex.Kind);
        Assert.Contains("hello world", ex.Stdout);
    }

    [Fact]
    public async Task Invoke_NonZeroNonJson_ReturnsResultWithExitCodeAndStderr()
    {
        var script = WriteScript("@echo off\r\necho boom 1>&2\r\nexit /b 3");

        var result = await NewInvoker(script).InvokeAsync(new[] { "run" });

        Assert.Equal(3, result.ExitCode);
        Assert.Null(result.Envelope);
        Assert.Contains("boom", result.Stderr);
    }

    [Fact]
    public void Redact_ReplacesKnownSecretsAndAuthUrlsKeepsNormalUrls()
    {
        var redactor = new LarkRedactor(new[] { "fake-super-secret-value-123456" });
        const string text =
            "token=fake-super-secret-value-123456 please open https://accounts.feishu.cn/authorize?code=abc&u=1 " +
            "or https://example.com/docs/1";

        var redacted = redactor.Redact(text);

        Assert.DoesNotContain("fake-super-secret-value-123456", redacted);
        Assert.Contains("[REDACTED-URL]", redacted);
        Assert.Contains("https://example.com/docs/1", redacted);
    }

    [Fact]
    public async Task Invoke_KnownSecretInOutput_IsRedactedBeforeReturned()
    {
        const string secret = "fake-live-token-abcdef";
        var script = WriteScript($"@echo off\r\necho {{\"ok\":true,\"data\":{{\"t\":\"{secret}\"}}}}\r\nexit /b 0");

        var result = await NewInvoker(script, secrets: new[] { secret }).InvokeAsync(new[] { "run" });

        Assert.DoesNotContain(secret, result.Stdout);
        Assert.NotNull(result.Envelope);
        using var envelope = result.Envelope;
        Assert.Equal("[REDACTED]", envelope.Data!.Value.GetProperty("t").GetString());
    }
}
