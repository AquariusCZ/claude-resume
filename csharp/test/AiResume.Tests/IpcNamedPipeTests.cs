using System.Buffers.Binary;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AiResume.Core;
using AiResume.Core.Abstractions;
using AiResume.Core.Contracts;
using AiResume.Ipc;
using Xunit;

namespace AiResume.Tests;

/// <summary>
/// S2-C Named Pipe 出口门禁:ping round-trip、恶意帧五连后服务端存活、
/// 单实例互斥、并发客户端 correlation 不串、命令路由转发参数正确。
/// 全部离线;每个测试实例使用随机 pipe 名,互不干扰。
/// </summary>
public sealed class IpcNamedPipeTests : IAsyncDisposable
{
    private readonly string _pipeName;
    private readonly RecordingOrchestrator _orchestrator;
    private readonly NamedPipeServer _server;

    public IpcNamedPipeTests()
    {
        _pipeName = "airesume-test-" + Guid.NewGuid().ToString("N")[..12];
        _orchestrator = new RecordingOrchestrator();
        _server = new NamedPipeServer(_orchestrator, _orchestrator.ListRunsAsync, _pipeName);
        _server.StartAsync(CancellationToken.None).GetAwaiter().GetResult();
    }

    public async ValueTask DisposeAsync()
    {
        await _server.DisposeAsync();
    }

    // ---- 1. ping round-trip ----

    [Fact]
    public async Task Ping_round_trip_returns_pong_with_version_and_correlation()
    {
        var client = new NamedPipeClient(_pipeName);
        var request = new IpcEnvelope { Type = PipeProtocol.CommandPing, CorrelationId = "corr-ping-1" };

        IpcEnvelope response = await client.RequestAsync(request, Ct());

        Assert.Equal(PipeProtocol.ResponsePong, response.Type);
        Assert.Equal(request.CorrelationId, response.CorrelationId);
        Assert.Equal(PipeProtocol.Version, response.Payload!.Value.GetProperty("version").GetString());
    }

    // ---- 2-6. 恶意帧五连:每类恶意帧断连后服务端仍能服务新客户端 ----

    [Fact]
    public async Task Oversized_frame_disconnects_client_but_server_survives()
    {
        using var stream = await RawConnectAsync();
        var header = new byte[PipeProtocol.HeaderBytes];
        BinaryPrimitives.WriteInt32LittleEndian(header, PipeProtocol.MaxFrameBytes + 1);
        await stream.WriteAsync(header, Ct());
        await stream.FlushAsync();

        Assert.Equal(0, await ReadWithTimeoutAsync(stream)); // 服务端断连
        await AssertPingOkAsync();                           // 服务端仍存活
    }

    [Fact]
    public async Task Zero_length_frame_disconnects_client_but_server_survives()
    {
        using var stream = await RawConnectAsync();
        var header = new byte[PipeProtocol.HeaderBytes];
        BinaryPrimitives.WriteInt32LittleEndian(header, 0);
        await stream.WriteAsync(header, Ct());
        await stream.FlushAsync();

        Assert.Equal(0, await ReadWithTimeoutAsync(stream));
        await AssertPingOkAsync();
    }

    [Fact]
    public async Task Negative_length_frame_disconnects_client_but_server_survives()
    {
        using var stream = await RawConnectAsync();
        var header = new byte[PipeProtocol.HeaderBytes];
        BinaryPrimitives.WriteInt32LittleEndian(header, -1);
        await stream.WriteAsync(header, Ct());
        await stream.FlushAsync();

        Assert.Equal(0, await ReadWithTimeoutAsync(stream));
        await AssertPingOkAsync();
    }

    [Fact]
    public async Task Non_json_frame_disconnects_client_but_server_survives()
    {
        using var stream = await RawConnectAsync();
        await WriteFrameAsync(stream, """{"type": "ping", "correlationId": "c-broken", """);

        Assert.Equal(0, await ReadWithTimeoutAsync(stream));
        await AssertPingOkAsync();
    }

    [Fact]
    public async Task Unknown_envelope_version_gets_error_frame_then_disconnects()
    {
        using var stream = await RawConnectAsync();
        await WriteFrameAsync(stream, """
            {"envelopeVersion": "2", "type": "ping", "correlationId": "c-unknown-ver"}
            """);

        // 先收到结构化错误帧(回带 correlationId),随后连接被断开。
        (bool ok, string json) = await ReadFrameWithTimeoutAsync(stream);
        Assert.True(ok, "未知版本应收到错误帧");
        using var doc = JsonDocument.Parse(json);
        Assert.Equal(PipeProtocol.ResponseError, doc.RootElement.GetProperty("type").GetString());
        Assert.Equal("c-unknown-ver", doc.RootElement.GetProperty("correlationId").GetString());
        Assert.Equal(
            PipeProtocol.ErrorUnsupportedEnvelopeVersion,
            doc.RootElement.GetProperty("payload").GetProperty("code").GetString());

        Assert.Equal(0, await ReadWithTimeoutAsync(stream)); // 随后断开
        await AssertPingOkAsync();
    }

    // ---- 7. 单实例互斥 ----

    [Fact]
    public void Second_server_on_same_pipe_throws_clear_single_instance_exception()
    {
        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() =>
            new NamedPipeServer(_orchestrator, _ => Task.FromResult<IReadOnlyList<RunSnapshot>>(Array.Empty<RunSnapshot>()), _pipeName));

        Assert.Contains("单实例互斥体已存在", ex.Message);
        Assert.Contains(_pipeName, ex.Message);
    }

    // ---- 8. 并发客户端 correlation 不串 ----

    [Fact]
    public async Task Concurrent_clients_do_not_cross_correlations()
    {
        var clientA = new NamedPipeClient(_pipeName);
        var clientB = new NamedPipeClient(_pipeName);

        Task taskA = Task.Run(async () =>
        {
            for (int i = 0; i < 5; i++)
            {
                var request = new IpcEnvelope { Type = PipeProtocol.CommandPing, CorrelationId = $"A-{i}" };
                IpcEnvelope response = await clientA.RequestAsync(request, Ct());
                Assert.Equal($"A-{i}", response.CorrelationId);
                Assert.Equal(PipeProtocol.ResponsePong, response.Type);
            }
        });
        Task taskB = Task.Run(async () =>
        {
            for (int i = 0; i < 5; i++)
            {
                var request = new IpcEnvelope { Type = PipeProtocol.CommandPing, CorrelationId = $"B-{i}" };
                IpcEnvelope response = await clientB.RequestAsync(request, Ct());
                Assert.Equal($"B-{i}", response.CorrelationId);
                Assert.Equal(PipeProtocol.ResponsePong, response.Type);
            }
        });

        await Task.WhenAll(taskA, taskB);
    }

    // ---- 9. start 转发参数正确 ----

    [Fact]
    public async Task Start_forwards_full_parameters_to_orchestrator()
    {
        var client = new NamedPipeClient(_pipeName);
        Guid requestId = Guid.NewGuid();
        Guid attemptGroupId = Guid.NewGuid();
        Guid parentRunId = Guid.NewGuid();
        var start = new StartRequest
        {
            RequestId = requestId,
            RunKey = "modify|c:\\projects\\p1|ou_7",
            TaskKind = TaskKind.Modify,
            Actor = "ou_7",
            ProjectRef = "c:\\projects\\p1",
            ProfileId = "profile-sol",
            Cwd = "c:\\projects\\p1",
            InputRef = "input://ref-1",
            CredentialRef = "cred-1",
            AttemptGroupId = attemptGroupId,
            ParentRunId = parentRunId,
            FallbackPolicy = FallbackPolicy.ProviderExplicitOnce,
        };

        var request = new IpcEnvelope
        {
            Type = PipeProtocol.CommandStart,
            CorrelationId = "corr-start-1",
            Payload = JsonSerializer.SerializeToElement(start, IpcJson.Options),
        };

        IpcEnvelope response = await client.RequestAsync(request, Ct());

        Assert.Equal(PipeProtocol.ResponseStarted, response.Type);
        Assert.Equal("corr-start-1", response.CorrelationId);
        Assert.True(_orchestrator.LastStartRequested);
        StartRequest received = _orchestrator.LastStart!;
        Assert.Equal(StartRequest.ContractVersionValue, received.ContractVersion);
        Assert.Equal(requestId, received.RequestId);
        Assert.Equal("modify|c:\\projects\\p1|ou_7", received.RunKey);
        Assert.Equal(TaskKind.Modify, received.TaskKind);
        Assert.Equal("ou_7", received.Actor);
        Assert.Equal("c:\\projects\\p1", received.ProjectRef);
        Assert.Equal("profile-sol", received.ProfileId);
        Assert.Equal("c:\\projects\\p1", received.Cwd);
        Assert.Equal("input://ref-1", received.InputRef);
        Assert.Equal("cred-1", received.CredentialRef);
        Assert.Equal(attemptGroupId, received.AttemptGroupId);
        Assert.Equal(parentRunId, received.ParentRunId);
        Assert.Equal(FallbackPolicy.ProviderExplicitOnce, received.FallbackPolicy);
    }

    // ---- 10. status/cancel/list-runs 路由 ----

    [Fact]
    public async Task Status_cancel_list_runs_route_to_injected_handlers()
    {
        var client = new NamedPipeClient(_pipeName);
        Guid runIdValue = Guid.NewGuid();

        // status
        var statusRequest = new IpcEnvelope
        {
            Type = PipeProtocol.CommandStatus,
            CorrelationId = "corr-status-1",
            Payload = JsonSerializer.SerializeToElement(new IpcStatusPayload { RunId = runIdValue.ToString("D") }, IpcJson.Options),
        };
        IpcEnvelope statusResponse = await client.RequestAsync(statusRequest, Ct());
        Assert.Equal(PipeProtocol.ResponseStatus, statusResponse.Type);
        Assert.Equal(new RunId(runIdValue), _orchestrator.StatusCalls.Single());
        Assert.Equal(
            _orchestrator.Snapshot.RunId.ToString(),
            statusResponse.Payload!.Value.GetProperty("runId").GetString());

        // cancel
        Guid commandId = Guid.NewGuid();
        var cancelRequest = new IpcEnvelope
        {
            Type = PipeProtocol.CommandCancel,
            CorrelationId = "corr-cancel-1",
            Payload = JsonSerializer.SerializeToElement(new CancelRequest
            {
                CommandId = commandId,
                RunId = new RunId(runIdValue),
                RequestedBy = "gui",
                Reason = CancelReason.UserStop,
            }, IpcJson.Options),
        };
        IpcEnvelope cancelResponse = await client.RequestAsync(cancelRequest, Ct());
        Assert.Equal(PipeProtocol.ResponseCancelled, cancelResponse.Type);
        CancelRequest receivedCancel = _orchestrator.CancelCalls.Single();
        Assert.Equal(commandId, receivedCancel.CommandId);
        Assert.Equal(new RunId(runIdValue), receivedCancel.RunId);
        Assert.Equal("gui", receivedCancel.RequestedBy);
        Assert.Equal(CancelReason.UserStop, receivedCancel.Reason);

        // list-runs
        var listRequest = new IpcEnvelope { Type = PipeProtocol.CommandListRuns, CorrelationId = "corr-list-1" };
        IpcEnvelope listResponse = await client.RequestAsync(listRequest, Ct());
        Assert.Equal(PipeProtocol.ResponseRuns, listResponse.Type);
        Assert.True(_orchestrator.ListRunsCalled);
        JsonElement[] runs = listResponse.Payload!.Value.EnumerateArray().ToArray();
        Assert.Single(runs);
        Assert.Equal(_orchestrator.Snapshot.RunId.ToString(), runs[0].GetProperty("runId").GetString());
    }

    // ---- 11. 未知命令回结构化错误且连接存活 ----

    [Fact]
    public async Task Unknown_command_returns_structured_error_and_connection_survives()
    {
        var client = new NamedPipeClient(_pipeName);
        var request = new IpcEnvelope { Type = "frobnicate", CorrelationId = "c-unknown-cmd" };

        IpcEnvelope response = await client.RequestAsync(request, Ct());

        Assert.Equal(PipeProtocol.ResponseError, response.Type);
        Assert.Equal("c-unknown-cmd", response.CorrelationId);
        Assert.Equal(PipeProtocol.ErrorUnknownCommand, response.Payload!.Value.GetProperty("code").GetString());

        // 连接仍可继续服务。
        IpcEnvelope ping = await client.RequestAsync(
            new IpcEnvelope { Type = PipeProtocol.CommandPing, CorrelationId = "c-after-unknown" }, Ct());
        Assert.Equal(PipeProtocol.ResponsePong, ping.Type);
    }

    // ---- 12. pipe 名派生 ----

    [Fact]
    public void Pipe_name_is_deterministic_sid_sha256_prefix16()
    {
        const string sidA = "S-1-5-21-111-222-333-1001";
        string name = PipeNaming.ComputePipeName(sidA);

        Assert.StartsWith(PipeProtocol.PipeNamePrefix, name);
        Assert.Equal(16, name.Length - PipeProtocol.PipeNamePrefix.Length);
        Assert.Equal(name, PipeNaming.ComputePipeName(sidA)); // 确定性

        byte[] digest = SHA256.HashData(Encoding.UTF8.GetBytes(sidA));
        string expected = Convert.ToHexString(digest).ToLowerInvariant()[..16];
        Assert.Equal(PipeProtocol.PipeNamePrefix + expected, name);

        Assert.NotEqual(name, PipeNaming.ComputePipeName("S-1-5-21-111-222-333-1002")); // 不同 SID 不同名
    }

    // ---- helpers ----

    private static CancellationToken Ct() => new CancellationTokenSource(TimeSpan.FromSeconds(10)).Token;

    private async Task<NamedPipeClientStream> RawConnectAsync()
    {
        var stream = new NamedPipeClientStream(".", _pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await stream.ConnectAsync(Ct());
        return stream;
    }

    private async Task AssertPingOkAsync()
    {
        var client = new NamedPipeClient(_pipeName);
        IpcEnvelope response = await client.RequestAsync(
            new IpcEnvelope { Type = PipeProtocol.CommandPing, CorrelationId = "c-probe" }, Ct());
        Assert.Equal(PipeProtocol.ResponsePong, response.Type);
        Assert.Equal("c-probe", response.CorrelationId);
    }

    private static async Task WriteFrameAsync(Stream stream, string json)
    {
        byte[] payload = Encoding.UTF8.GetBytes(json);
        var frame = new byte[PipeProtocol.HeaderBytes + payload.Length];
        BinaryPrimitives.WriteInt32LittleEndian(frame, payload.Length);
        payload.CopyTo(frame, PipeProtocol.HeaderBytes);
        await stream.WriteAsync(frame, Ct());
        await stream.FlushAsync();
    }

    /// <summary>读一帧(长度头 + body);EOF/超时返回 (false, ""),超时抛错。</summary>
    private static async Task<(bool Ok, string Json)> ReadFrameWithTimeoutAsync(Stream stream)
    {
        var header = new byte[PipeProtocol.HeaderBytes];
        int headerRead = await ReadWithTimeoutAsync(stream, header);
        if (headerRead != PipeProtocol.HeaderBytes)
        {
            return (false, string.Empty);
        }

        int length = BinaryPrimitives.ReadInt32LittleEndian(header);
        var body = new byte[length];
        int bodyRead = await ReadWithTimeoutAsync(stream, body);
        if (bodyRead != length)
        {
            return (false, string.Empty);
        }

        return (true, Encoding.UTF8.GetString(body));
    }

    /// <summary>读最多 buffer.Length 字节;EOF 返回 0;超时抛 OperationCanceledException。</summary>
    private static async Task<int> ReadWithTimeoutAsync(Stream stream, byte[]? buffer = null)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var target = buffer ?? new byte[1];
        return await stream.ReadAsync(target, cts.Token);
    }

    /// <summary>记录调用参数的可编程 orchestrator(测试替身,不涉任何真实运行状态)。</summary>
    private sealed class RecordingOrchestrator : ITaskOrchestrator
    {
        public bool LastStartRequested { get; private set; }
        public StartRequest? LastStart { get; private set; }
        public List<RunId> StatusCalls { get; } = [];
        public List<CancelRequest> CancelCalls { get; } = [];
        public bool ListRunsCalled { get; private set; }
        public RunSnapshot Snapshot { get; } = new() { RunId = RunId.New(), State = RunState.Running, StateVersion = 3 };

        public Task<StartResponse> StartAsync(StartRequest request, CancellationToken cancellationToken)
        {
            LastStartRequested = true;
            LastStart = request;
            return Task.FromResult(new StartResponse
            {
                Accepted = true,
                RunId = RunId.New(),
                State = RunState.Queued,
                StateVersion = 1,
            });
        }

        public Task<RunSnapshot> StatusAsync(RunId runId, CancellationToken cancellationToken)
        {
            StatusCalls.Add(runId);
            return Task.FromResult(Snapshot);
        }

        public Task<CancelResponse> CancelAsync(CancelRequest request, CancellationToken cancellationToken)
        {
            CancelCalls.Add(request);
            return Task.FromResult(new CancelResponse
            {
                CommandId = request.CommandId,
                RunId = request.RunId,
                State = RunState.Cancelled,
                StateVersion = 2,
                ChildPending = false,
                TerminationRequested = true,
            });
        }

        public Task<IReadOnlyList<RunSnapshot>> ListRunsAsync(CancellationToken cancellationToken)
        {
            ListRunsCalled = true;
            return Task.FromResult<IReadOnlyList<RunSnapshot>>(new[] { Snapshot });
        }
    }
}
