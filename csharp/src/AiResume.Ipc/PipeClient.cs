using System.Buffers.Binary;
using System.IO.Pipes;
using System.Text.Json;

namespace AiResume.Ipc;

public sealed record WorkerPingInfo(string Version, int? ProcessId);

/// <summary>
/// Named Pipe 客户端(Stage 2-G,GUI 探测 Worker 用)。
///
/// 帧协议与服务端完全一致:4 字节小端长度 + UTF-8 JSON(IpcEnvelope)。
/// - PingAsync:连接 → 发 ping 帧 → 读应答帧 → 返回 pong payload 的 version。
/// - 单帧超时是传输层特性(默认 3 秒),绝不构成 AI run 总时限。
/// - Worker 未运行时连接失败(IOException/TimeoutException),由 GUI 显示为"未运行"。
/// - 恶意/超长/未知应答帧一律视为不可信,返回 null,不抛协议细节。
/// </summary>
public sealed class PipeClient : IDisposable
{
    private readonly string _pipeName;
    private readonly TimeSpan _timeout;
    private NamedPipeClientStream? _stream;

    public PipeClient(string pipeName, TimeSpan? timeout = null)
    {
        if (string.IsNullOrWhiteSpace(pipeName))
        {
            throw new ArgumentException("pipe 名不能为空。", nameof(pipeName));
        }

        _pipeName = pipeName;
        _timeout = timeout ?? TimeSpan.FromSeconds(3);
    }

    /// <summary>发送 ping 并返回 Worker 应答的协议版本;连接/解析失败返回 null。</summary>
    public async Task<string?> PingAsync(CancellationToken cancellationToken)
        => (await PingIdentityAsync(cancellationToken))?.Version;

    /// <summary>发送 ping 并返回协议版本与响应 Worker 的 PID。</summary>
    public async Task<WorkerPingInfo?> PingIdentityAsync(CancellationToken cancellationToken)
    {
        await EnsureConnectedAsync(cancellationToken);

        var request = new IpcEnvelope
        {
            Type = PipeProtocol.CommandPing,
            CorrelationId = Guid.NewGuid().ToString("N"),
        };
        byte[] frame = PipeFraming.Encode(JsonSerializer.SerializeToUtf8Bytes(request, IpcJson.Options));
        await _stream!.WriteAsync(frame, cancellationToken);
        await _stream.FlushAsync(cancellationToken);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(_timeout);
        try
        {
            var header = new byte[PipeProtocol.HeaderBytes];
            int headerRead = await PipeFraming.ReadExactlyAsync(_stream, header, cts.Token);
            if (headerRead < PipeProtocol.HeaderBytes)
            {
                return null; // 半帧/对端关闭。
            }

            int length = BinaryPrimitives.ReadInt32LittleEndian(header);
            if (length <= 0 || length > PipeProtocol.MaxFrameBytes)
            {
                return null; // 非法长度:帧不可信,不继续解析。
            }

            var body = new byte[length];
            int bodyRead = await PipeFraming.ReadExactlyAsync(_stream, body, cts.Token);
            if (bodyRead != length)
            {
                return null; // 帧体不完整。
            }

            IpcEnvelope? response;
            try
            {
                response = JsonSerializer.Deserialize<IpcEnvelope>(body, IpcJson.Options);
            }
            catch (JsonException)
            {
                return null;
            }

            if (response is null || response.Type != PipeProtocol.ResponsePong)
            {
                return null; // 非 pong(如 error 帧)视为探测失败。
            }

            if (response.Payload is not JsonElement payload ||
                !payload.TryGetProperty("version", out JsonElement versionElement) ||
                versionElement.GetString() is not { } version)
            {
                return null;
            }

            int? processId = payload.TryGetProperty("processId", out JsonElement pidElement) &&
                             pidElement.TryGetInt32(out int parsedPid)
                ? parsedPid
                : null;
            return new WorkerPingInfo(version, processId);
        }
        catch (OperationCanceledException)
        {
            // 超时:连接可能残留半帧,关闭以便下次重建。
            _stream.Dispose();
            _stream = null;
            return null;
        }
    }

    private async Task EnsureConnectedAsync(CancellationToken cancellationToken)
    {
        if (_stream is not null)
        {
            return;
        }

        var stream = new NamedPipeClientStream(".", _pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        try
        {
            await stream.ConnectAsync(_timeout, cancellationToken);
        }
        catch
        {
            stream.Dispose();
            throw;
        }

        _stream = stream;
    }

    public void Dispose()
    {
        _stream?.Dispose();
        _stream = null;
    }
}
