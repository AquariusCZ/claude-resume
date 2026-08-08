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
    public async Task Invoke_ErrorEnvelope_ParsesErrorFields()
    {
        var script = WriteScript(
            "@echo off\r\necho {\"ok\":false,\"error\":{\"type\":\"scope\",\"subtype\":\"missing\",\"message\":\"missing scope\",\"hint\":\"run lark-cli auth\"}}\r\nexit /b 1");

        var result = await NewInvoker(script).InvokeAsync(new[] { "run" });

        Assert.Equal(1, result.ExitCode);
        Assert.NotNull(result.Envelope);
        Assert.False(result.Envelope!.Ok);
        Assert.Equal("scope", result.Envelope.ErrorType);
        Assert.Equal("missing", result.Envelope.ErrorSubtype);
        Assert.Equal("missing scope", result.Envelope.ErrorMessage);
        Assert.Equal("run lark-cli auth", result.Envelope.ErrorHint);
    }

    [Fact]
    public async Task Invoke_Exit10_ThrowsHighRiskConfirmationWithHint()
    {
        var script = WriteScript(
            "@echo off\r\necho {\"ok\":false,\"error\":{\"type\":\"confirm\",\"message\":\"high risk write needs confirm\"}}\r\nexit /b 10");

        var ex = await Assert.ThrowsAsync<LarkCliException>(() => NewInvoker(script).InvokeAsync(new[] { "run" }));

        Assert.Equal(LarkCliFailureKind.HighRiskConfirmationRequired, ex.Kind);
        Assert.Contains("high risk write needs confirm", ex.Stdout);
        Assert.Contains("high risk write needs confirm", ex.Message);
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
