using System.Text;
using System.Text.Json;
using AiResume.Secrets;
using AiResume.Worker.Logging;
using Microsoft.Extensions.Logging;
using Xunit;

namespace AiResume.Tests;

/// <summary>
/// S2-F 出口门禁(规格 §3.7/§3.8/§4):
/// DPAPI 机密存储(round-trip/明文不进磁盘/路径穿越拒绝/幂等删除)、
/// 脱敏双重兜底(键名 allowlist + 文本形状扫描)、结构化日志 0 泄漏。
/// </summary>
public sealed class SecretsTests : IDisposable
{
    private readonly string _dir;

    public SecretsTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "airesume-secrets-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* 测试临时目录残留可容忍 */ }
    }

    // ---------- DPAPI 机密存储 ----------

    [Fact]
    public async Task DpapiSecretStore_round_trip_and_no_plaintext_on_disk()
    {
        var store = new DpapiSecretStore(_dir);
        // 假值长度刻意低于 scan-secrets.ps1 阈值(20),但高于脱敏模式下限(12)。
        string secret = "sk-live-0123456789ab";
        byte[] plaintext = Encoding.UTF8.GetBytes(secret);

        await store.SaveAsync("openai-primary", plaintext, CancellationToken.None);
        byte[] loaded = await store.LoadAsync("openai-primary", CancellationToken.None);

        Assert.Equal(plaintext, loaded);

        // 密文文件必须存在,且字节序列中不含明文(DPAPI 密文随机化)。
        string file = Path.Combine(_dir, "secrets", "openai-primary.bin");
        Assert.True(File.Exists(file));
        byte[] cipher = File.ReadAllBytes(file);
        Assert.False(ContainsSequence(cipher, plaintext), "密文文件不得包含明文字节序列。");
    }

    [Theory]
    [InlineData("../evil")]
    [InlineData("..\\evil")]
    [InlineData("a/b")]
    [InlineData("a\\b")]
    [InlineData("")]
    [InlineData(".")]
    [InlineData("..")]
    public async Task DpapiSecretStore_rejects_unsafe_credential_refs(string credentialRef)
    {
        var store = new DpapiSecretStore(_dir);
        await Assert.ThrowsAsync<ArgumentException>(() =>
            store.SaveAsync(credentialRef, new byte[] { 1 }, CancellationToken.None));
    }

    [Fact]
    public async Task DpapiSecretStore_load_missing_throws_key_not_found()
    {
        var store = new DpapiSecretStore(_dir);
        await Assert.ThrowsAsync<KeyNotFoundException>(() =>
            store.LoadAsync("missing-ref", CancellationToken.None));
    }

    [Fact]
    public async Task DpapiSecretStore_delete_is_idempotent_then_load_missing()
    {
        var store = new DpapiSecretStore(_dir);
        await store.SaveAsync("temp-ref", new byte[] { 1, 2, 3 }, CancellationToken.None);

        await store.DeleteAsync("temp-ref", CancellationToken.None);
        await store.DeleteAsync("temp-ref", CancellationToken.None); // 幂等

        await Assert.ThrowsAsync<KeyNotFoundException>(() =>
            store.LoadAsync("temp-ref", CancellationToken.None));
    }

    // ---------- 脱敏双重兜底 ----------

    [Theory]
    [InlineData("apiKey")]
    [InlineData("openaiApiKey")]
    [InlineData("app_secret")]
    [InlineData("accessToken")]
    [InlineData("mySecret")]
    [InlineData("password")]
    [InlineData("credentialRef")]
    [InlineData("authorization")]
    public void SecretRedactor_marks_sensitive_keys(string key)
    {
        Assert.True(SecretRedactor.IsSensitiveKey(key));
        object redacted = SecretRedactor.RedactValue(key, "sk-supersecret-ab")!;
        Assert.Equal($"[redacted:{key}]", redacted);
    }

    [Fact]
    public void SecretRedactor_keeps_safe_keys_untouched()
    {
        Assert.False(SecretRedactor.IsSensitiveKey("runId"));
        Assert.False(SecretRedactor.IsSensitiveKey("state"));
        Assert.False(SecretRedactor.IsSensitiveKey("event"));
        Assert.Equal("hello", SecretRedactor.RedactValue("runId", "hello"));
    }

    [Fact]
    public void SecretRedactor_redacts_known_shapes_in_free_text()
    {
        Assert.Equal("key=[redacted] end", SecretRedactor.RedactText("key=sk-abc1234567890123 end"));
        Assert.Equal("h=[redacted]", SecretRedactor.RedactText("h=Bearer abcdefghijklmnop123456"));
        Assert.Equal("[redacted]", SecretRedactor.RedactText(
            "eyJtesttesttesttesttest.eyJzdWIiOiIxMjM0NTY3ODkwIn0.signature12345678"));
        Assert.Equal("普通文本不受影响", SecretRedactor.RedactText("普通文本不受影响"));
    }

    [Fact]
    public void SecretRedactor_serializeSafe_redacts_nested_keys_and_shapes()
    {
        var payload = new Dictionary<string, object?>
        {
            ["apiKey"] = "sk-live-abcdefghij",
            ["runId"] = "run-123",
            ["nested"] = new Dictionary<string, object?>
            {
                ["appSecret"] = "supersecret-value-1234567890abcdef",
                ["message"] = "status sk-live-abcdefghij",
            },
        };

        string json = SecretRedactor.SerializeSafe(payload);

        Assert.DoesNotContain("sk-live-abcdefghij", json);
        Assert.DoesNotContain("supersecret-value-1234567890abcdef", json);
        Assert.Contains("[redacted:apiKey]", json);
        Assert.Contains("[redacted:appSecret]", json);
        Assert.Contains("run-123", json);
    }

    // ---------- 结构化日志(单行 JSON + 0 泄漏) ----------

    [Fact]
    public void DailyJsonFileLogger_emits_single_line_json_with_contract_fields()
    {
        string logsDir = Path.Combine(_dir, "logs");
        using var provider = new DailyJsonFileLoggerProvider(logsDir);
        ILogger logger = provider.CreateLogger("AiResume.Worker.TestComponent");

        logger.LogInformation("cycle runCount={RunCount} childPending={ChildPending}", 3, 1);

        string file = Directory.GetFiles(logsDir, "worker-*.log").Single();
        string[] lines = File.ReadAllLines(file);
        Assert.Single(lines);

        using JsonDocument doc = JsonDocument.Parse(lines[0]);
        JsonElement root = doc.RootElement;
        Assert.True(root.TryGetProperty("ts", out _));
        Assert.True(root.TryGetProperty("run_id", out _));
        Assert.Equal("information", root.GetProperty("level").GetString());
        Assert.Equal("testcomponent", root.GetProperty("component").GetString());
        Assert.Equal("log", root.GetProperty("event").GetString());
        Assert.Contains("runCount=3", root.GetProperty("data").GetString());
    }

    [Fact]
    public void DailyJsonFileLogger_redacts_injected_fake_secrets_zero_leak()
    {
        string logsDir = Path.Combine(_dir, "logs");
        using var provider = new DailyJsonFileLoggerProvider(logsDir);
        ILogger logger = provider.CreateLogger("AiResume.Worker.TestComponent");

        // 注入假机密形状:结构参数 + 自由文本 + 异常 message 三路。
        string fakeKey = "sk-testsecret-ab";
        logger.LogInformation("provider status key={Key} body={Body}", fakeKey, $"calling {fakeKey}");
        try
        {
            throw new InvalidOperationException($"failed with {fakeKey} and Bearer abcdefghijklmnop123456");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "provider error");
        }

        string file = Directory.GetFiles(logsDir, "worker-*.log").Single();
        string content = File.ReadAllText(file);

        Assert.DoesNotContain(fakeKey, content);
        Assert.DoesNotContain("abcdefghijklmnop123456", content);
        Assert.Contains("[redacted]", content);
    }

    private static bool ContainsSequence(byte[] haystack, byte[] needle)
    {
        for (int i = 0; i <= haystack.Length - needle.Length; i++)
        {
            bool match = true;
            for (int j = 0; j < needle.Length; j++)
            {
                if (haystack[i + j] != needle[j])
                {
                    match = false;
                    break;
                }
            }

            if (match)
            {
                return true;
            }
        }

        return false;
    }
}
