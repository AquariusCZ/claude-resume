using System.Text.Json;
using System.Text.Json.Serialization;
using AiResume.Core;

namespace AiResume.Ipc;

/// <summary>
/// Ipc 层序列化选项:属性名大小写不敏感、枚举字符串化、RunId 以稳定字符串传输。
/// 不得向该选项添加任何机密字段;payload 契约禁止机密(见 docs/EVENT-CONTRACTS.md 第 14 节)。
/// </summary>
internal static class IpcJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        // camelCase 对齐 docs/EVENT-CONTRACTS.md 的 payload 字段名(contractVersion/requestId/runKey/...);
        // IpcEnvelope/IpcError 的显式 JsonPropertyName(snake_case)优先于命名策略,不受影响。
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter(), new RunIdJsonConverter() },
    };

    private sealed class RunIdJsonConverter : JsonConverter<RunId>
    {
        public override RunId Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
            => RunId.FromString(reader.GetString()!);

        public override void Write(Utf8JsonWriter writer, RunId value, JsonSerializerOptions options)
            => writer.WriteStringValue(value.ToString());
    }
}
