using System.Buffers.Binary;
using System.IO.Pipes;
using System.Text.Json;

namespace AiResume.Ipc;

/// <summary>
/// Named Pipe 客户端(Stage 2-C)。每次 RequestAsync 建立独立连接:
/// 发请求帧 → 读一帧应答 → 校验应答 correlationId 与请求一致 → 关闭连接。
/// 无共享状态,同一客户端实例可安全并发;多客户端各自携带独立 correlationId,响应不会串线。
/// 单次请求的等待受调用方 token 控制(传输级请求超时,属 RUN-CONTRACT §11 允许范围,绝非 AI run 总时限)。
/// </summary>
public sealed class NamedPipeClient
{
    private readonly string _pipeName;

    /// <summary>pipeName 缺省时使用当前用户 SID 派生的默认名。</summary>
    public NamedPipeClient(string? pipeName = null)
    {
        _pipeName = pipeName ?? PipeNaming.CurrentUserPipeName;
    }

    /// <summary>目标 pipe 名。</summary>
    public string PipeName => _pipeName;

    /// <summary>发送请求并等待应答;携带 requestTimeout 的单次请求超时便捷重载。</summary>
    public Task<IpcEnvelope> RequestAsync(IpcEnvelope request, TimeSpan requestTimeout, CancellationToken cancellationToken)
    {
        if (requestTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(requestTimeout), "requestTimeout 必须为正。");
        }

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(requestTimeout);
        return RequestAsync(request, cts.Token);
    }

    /// <summary>发送请求并等待应答;等待完全由调用方 token 控制。</summary>
    public async Task<IpcEnvelope> RequestAsync(IpcEnvelope request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        using var stream = new NamedPipeClientStream(".", _pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);

        await stream.ConnectAsync(cancellationToken);

        byte[] json = JsonSerializer.SerializeToUtf8Bytes(request, IpcJson.Options);
        byte[] frame = PipeFraming.Encode(json);
        await stream.WriteAsync(frame, cancellationToken);
        await stream.FlushAsync(cancellationToken);

        var header = new byte[PipeProtocol.HeaderBytes];
        int headerRead = await PipeFraming.ReadExactlyAsync(stream, header, cancellationToken);
        if (headerRead != PipeProtocol.HeaderBytes)
        {
            throw new IOException("对端在应答帧头之前关闭了连接。");
        }

        int length = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (length <= 0 || length > PipeProtocol.MaxFrameBytes)
        {
            throw new IOException($"对端返回了非法帧长度 {length}。");
        }

        var body = new byte[length];
        int bodyRead = await PipeFraming.ReadExactlyAsync(stream, body, cancellationToken);
        if (bodyRead != length)
        {
            throw new IOException("对端在应答帧体传输中关闭了连接。");
        }

        IpcEnvelope response;
        try
        {
            response = JsonSerializer.Deserialize<IpcEnvelope>(body, IpcJson.Options)
                       ?? throw new JsonException("应答帧为空信封。");
        }
        catch (JsonException ex)
        {
            throw new IOException("对端返回了无法解析的应答帧。", ex);
        }

        if (!string.Equals(response.CorrelationId, request.CorrelationId, StringComparison.Ordinal))
        {
            throw new IOException("应答的 correlationId 与请求不一致,检测到响应串线。");
        }

        return response;
    }
}
