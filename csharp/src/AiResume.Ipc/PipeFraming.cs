using System.Buffers.Binary;

namespace AiResume.Ipc;

/// <summary>
/// 帧编解码与流读取辅助(内部)。
/// 帧 = 4 字节小端长度前缀 + UTF-8 JSON 载荷;载荷长度必须在 1..MaxFrameBytes 之间。
/// 非法长度(0/负/超限)由调用方视为协议违规并断开连接。
/// </summary>
internal static class PipeFraming
{
    /// <summary>把 JSON 载荷编码为完整帧。</summary>
    public static byte[] Encode(ReadOnlySpan<byte> jsonPayload)
    {
        if (jsonPayload.Length <= 0 || jsonPayload.Length > PipeProtocol.MaxFrameBytes)
        {
            throw new ArgumentOutOfRangeException(nameof(jsonPayload), "载荷长度必须在 1..MaxFrameBytes 之间。");
        }

        var frame = new byte[PipeProtocol.HeaderBytes + jsonPayload.Length];
        BinaryPrimitives.WriteInt32LittleEndian(frame, jsonPayload.Length);
        jsonPayload.CopyTo(frame.AsSpan(PipeProtocol.HeaderBytes));
        return frame;
    }

    /// <summary>从流中精确读取 buffer 长度字节;EOF 提前返回已读字节数。</summary>
    public static async Task<int> ReadExactlyAsync(Stream stream, Memory<byte> buffer, CancellationToken cancellationToken)
    {
        int total = 0;
        while (total < buffer.Length)
        {
            int read = await stream.ReadAsync(buffer[total..], cancellationToken);
            if (read == 0)
            {
                break;
            }

            total += read;
        }

        return total;
    }
}
