using System.Text.Json;

namespace AiResume.Worker.Probes;

/// <summary>可显示的供应商模型记录；ID 保留原始大小写，不按名称猜测其它模型。</summary>
public sealed record ProviderModel(string Id, string Name);

public sealed record ProviderModelCatalog(
    IReadOnlyList<ProviderModel> Models,
    bool Verified,
    string Source,
    DateTimeOffset ObservedAtUtc,
    string? Problem = null)
{
    public static ProviderModelCatalog Unavailable(string problem) =>
        new(Array.Empty<ProviderModel>(), false, "未核实", DateTimeOffset.UtcNow, problem);
}

/// <summary>
/// 官方 OpenAI/Anthropic/DeepSeek 模型列表的共有 data[] 外壳。
/// 只保留白名单字段；未知形状、分页未完成或超出边界不能冒充完整目录。
/// </summary>
public static class ProviderModels
{
    public const int MaxBytes = 1024 * 1024;
    public const int MaxModels = 4096;

    public static ProviderModelCatalog Parse(string body, string source = "供应商实时返回")
    {
        try
        {
            using JsonDocument doc = JsonDocument.Parse(body, new JsonDocumentOptions { MaxDepth = 32 });
            JsonElement root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("data", out JsonElement data) || data.ValueKind != JsonValueKind.Array ||
                data.GetArrayLength() > MaxModels)
                return ProviderModelCatalog.Unavailable("模型目录格式无法核实");

            var models = new List<ProviderModel>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (JsonElement row in data.EnumerateArray())
            {
                if (row.ValueKind != JsonValueKind.Object ||
                    !row.TryGetProperty("id", out JsonElement idValue) || idValue.ValueKind != JsonValueKind.String ||
                    idValue.GetString() is not { } id || !IsSafeId(id))
                    return ProviderModelCatalog.Unavailable("模型目录包含无效 ID");
                if (row.TryGetProperty("disabled", out JsonElement disabled) && disabled.ValueKind == JsonValueKind.True)
                    continue;
                string name = row.TryGetProperty("display_name", out JsonElement display) &&
                              display.ValueKind == JsonValueKind.String && display.GetString() is { Length: > 0 and <= 200 } label &&
                              !label.Any(char.IsControl) ? label : id;
                if (seen.Add(id)) models.Add(new ProviderModel(id, name));
            }

            bool more = root.TryGetProperty("has_more", out JsonElement hasMore) && hasMore.ValueKind != JsonValueKind.False;
            return new ProviderModelCatalog(models, !more, source, DateTimeOffset.UtcNow,
                more ? "供应商目录尚有后续分页，暂不能布防" : null);
        }
        catch (JsonException)
        {
            return ProviderModelCatalog.Unavailable("模型目录响应无法解析");
        }
    }

    public static bool IsSafeId(string value) => value.Length is > 0 and <= 200 &&
        value.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.' or '/' or ':' or '@' or '+');

    public static async Task<string> ReadBodyAsync(HttpContent content, CancellationToken ct)
    {
        if (content.Headers.ContentLength > MaxBytes) throw new InvalidDataException("模型目录超过大小限制");
        using Stream stream = await content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        byte[] block = new byte[8192];
        int count;
        while ((count = await stream.ReadAsync(block, ct).ConfigureAwait(false)) > 0)
        {
            if (buffer.Length + count > MaxBytes) throw new InvalidDataException("模型目录超过大小限制");
            await buffer.WriteAsync(block.AsMemory(0, count), ct).ConfigureAwait(false);
        }
        return System.Text.Encoding.UTF8.GetString(buffer.ToArray());
    }
}
