using System.Buffers.Binary;
using System.IO.Pipes;
using System.Text.Json;
using AiResume.Core;
using AiResume.Core.Abstractions;
using AiResume.Core.Contracts;
using AiResume.Ipc;
using Xunit;

namespace AiResume.Tests;

/// <summary>
/// S2-G 客户端出口门禁:真 NamedPipeServer round-trip 返回协议版本;
/// 无服务端/服务端已停止 → 短超时连接失败;错误帧/零长/超长/半头/半 body 应答
/// 一律收敛为 null,不向 GUI 泄漏协议细节。单帧超时仅属传输层,绝不构成 AI run 总时限。
/// </summary>
public sealed class IpcPipeClientTests
{
    private const int ShortTimeoutMs = 300;

    [Fact]
    public async Task Ping_WithLiveServer_ReturnsProtocolVersion()
    {
        string pipeName = UniquePipeName();
        await using var server = new NamedPipeServer(
            new FakeOrchestrator(), EmptyRuns, pipeName);
        await server.StartAsync(CancellationToken.None);

        using var client = new PipeClient(pipeName, TimeSpan.FromSeconds(2));
        string? version = await client.PingAsync(CancellationToken.None);

        Assert.Equal(PipeProtocol.Version, version);
    }

    [Fact]
    public async Task Ping_NoServer_ThrowsTimeout()
    {
        using var client = new PipeClient(UniquePipeName(), TimeSpan.FromMilliseconds(ShortTimeoutMs));
        await Assert.ThrowsAsync<TimeoutException>(() => client.PingAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Ping_AfterServerStopped_ThrowsTimeout()
    {
        string pipeName = UniquePipeName();
        var server = new NamedPipeServer(new FakeOrchestrator(), EmptyRuns, pipeName);
        await server.StartAsync(CancellationToken.None);
        await server.DisposeAsync();

        using var client = new PipeClient(pipeName, TimeSpan.FromMilliseconds(ShortTimeoutMs));
        await Assert.ThrowsAsync<TimeoutException>(() => client.PingAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Ping_ErrorResponse_ReturnsNull()
    {
        string pipeName = UniquePipeName();
        using var rawServer = new NamedPipeServerStream(
            pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        Task serverTask = AcceptOnceAsync(rawServer, stream => WriteFrameAsync(stream, new IpcEnvelope
        {
            Type = PipeProtocol.ResponseError,
        }));

        using var client = new PipeClient(pipeName, TimeSpan.FromSeconds(2));
        string? version = await client.PingAsync(CancellationToken.None);

        Assert.Null(version);
        await serverTask;
    }

    [Fact]
    public async Task Ping_ZeroLengthFrame_ReturnsNull()
    {
        string pipeName = UniquePipeName();
        using var rawServer = new NamedPipeServerStream(
            pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        Task serverTask = AcceptOnceAsync(rawServer, async stream =>
        {
            await ReadRequestFrameAsync(stream);
            var header = new byte[PipeProtocol.HeaderBytes];
            BinaryPrimitives.WriteInt32LittleEndian(header, 0);
            await stream.WriteAsync(header);
        });

        using var client = new PipeClient(pipeName, TimeSpan.FromSeconds(2));
        string? version = await client.PingAsync(CancellationToken.None);

        Assert.Null(version);
        await serverTask;
    }

    [Fact]
    public async Task Ping_OversizedLength_ReturnsNull()
    {
        string pipeName = UniquePipeName();
        using var rawServer = new NamedPipeServerStream(
            pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        Task serverTask = AcceptOnceAsync(rawServer, async stream =>
        {
            await ReadRequestFrameAsync(stream);
            var header = new byte[PipeProtocol.HeaderBytes];
            BinaryPrimitives.WriteInt32LittleEndian(header, PipeProtocol.MaxFrameBytes + 1);
            await stream.WriteAsync(header);
        });

        using var client = new PipeClient(pipeName, TimeSpan.FromSeconds(2));
        string? version = await client.PingAsync(CancellationToken.None);

        Assert.Null(version);
        await serverTask;
    }

    [Fact]
    public async Task Ping_HalfHeaderThenClose_ReturnsNull()
    {
        string pipeName = UniquePipeName();
        using var rawServer = new NamedPipeServerStream(
            pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        Task serverTask = AcceptOnceAsync(rawServer, async stream =>
        {
            await ReadRequestFrameAsync(stream);
            await stream.WriteAsync(new byte[] { 0, 1 }); // 半截头部后关闭。
        });

        using var client = new PipeClient(pipeName, TimeSpan.FromSeconds(2));
        string? version = await client.PingAsync(CancellationToken.None);

        Assert.Null(version);
        await serverTask;
    }

    [Fact]
    public async Task Ping_HalfBodyThenClose_ReturnsNull()
    {
        string pipeName = UniquePipeName();
        using var rawServer = new NamedPipeServerStream(
            pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        Task serverTask = AcceptOnceAsync(rawServer, async stream =>
        {
            await ReadRequestFrameAsync(stream);
            var header = new byte[PipeProtocol.HeaderBytes];
            BinaryPrimitives.WriteInt32LittleEndian(header, 64);
            await stream.WriteAsync(header);
            await stream.WriteAsync(new byte[16]); // 声明的 body 64 字节,只给 16 字节后关闭。
        });

        using var client = new PipeClient(pipeName, TimeSpan.FromSeconds(2));
        string? version = await client.PingAsync(CancellationToken.None);

        Assert.Null(version);
        await serverTask;
    }

    private static string UniquePipeName() => "airesume-test-" + Guid.NewGuid().ToString("N");

    private static Task<IReadOnlyList<RunSnapshot>> EmptyRuns(CancellationToken ct)
        => Task.FromResult<IReadOnlyList<RunSnapshot>>(Array.Empty<RunSnapshot>());

    private static async Task AcceptOnceAsync(NamedPipeServerStream server, Func<NamedPipeServerStream, Task> handle)
    {
        await server.WaitForConnectionAsync();
        try
        {
            await handle(server);
        }
        finally
        {
            server.Dispose();
        }
    }

    private static async Task ReadRequestFrameAsync(Stream stream)
    {
        var header = new byte[PipeProtocol.HeaderBytes];
        int headerRead = await PipeFraming.ReadExactlyAsync(stream, header, CancellationToken.None);
        Assert.Equal(PipeProtocol.HeaderBytes, headerRead);
        int length = BinaryPrimitives.ReadInt32LittleEndian(header);
        Assert.InRange(length, 1, PipeProtocol.MaxFrameBytes);
        var body = new byte[length];
        await PipeFraming.ReadExactlyAsync(stream, body, CancellationToken.None);
    }

    private static async Task WriteFrameAsync(NamedPipeServerStream stream, IpcEnvelope envelope)
    {
        await ReadRequestFrameAsync(stream);
        byte[] frame = PipeFraming.Encode(JsonSerializer.SerializeToUtf8Bytes(envelope, IpcJson.Options));
        await stream.WriteAsync(frame);
        await stream.FlushAsync();
    }

    /// <summary>ping 不触发编排命令;任何编排调用都应失败并暴露测试设计错误。</summary>
    private sealed class FakeOrchestrator : ITaskOrchestrator
    {
        public Task<StartResponse> StartAsync(StartRequest request, CancellationToken cancellationToken)
            => throw new NotSupportedException("ping 不触发编排命令。");

        public Task<RunSnapshot> StatusAsync(RunId runId, CancellationToken cancellationToken)
            => throw new NotSupportedException("ping 不触发编排命令。");

        public Task<CancelResponse> CancelAsync(CancelRequest request, CancellationToken cancellationToken)
            => throw new NotSupportedException("ping 不触发编排命令。");
    }
}
