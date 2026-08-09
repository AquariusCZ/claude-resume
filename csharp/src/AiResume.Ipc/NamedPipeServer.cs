using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Text.Json;
using AiResume.Core;
using AiResume.Core.Abstractions;
using AiResume.Core.Contracts;
using AiResume.Core.Events;

namespace AiResume.Ipc;

/// <summary>
/// Named Pipe 服务端(Stage 2-C,对应规格 §3.6)。
/// 决策说明:
/// - 单实例:命名互斥体(Local 会话作用域,pipe 名派生)在构造时获取;第二实例构造即抛含明确文案的异常。
/// - 并发:accept 循环为每个客户端创建独立 <see cref="NamedPipeServerStream"/> 实例与处理 Task;
///   同一连接内由单一读循环按接收顺序逐帧应答(写操作再经每连接写锁保护,避免外部广播交错)。
/// - 恶意帧:长度非法(0/负/超限)或 JSON 解析失败 → 立即断开该客户端;未知 envelopeVersion →
///   先回结构化错误帧再断开;以上均不影响服务端本体,继续接受新连接。
/// - 超时:本服务不创建任何 AI run 总时限;cancellationToken 仅表示调用方中止本次操作。
/// </summary>
public sealed class NamedPipeServer : IAsyncDisposable
{
    private readonly string _pipeName;
    private readonly string _mutexName;
    private readonly ITaskOrchestrator _orchestrator;
    private readonly Func<CancellationToken, Task<IReadOnlyList<RunSnapshot>>> _listRuns;
    private readonly ConcurrentDictionary<long, ClientConnection> _connections = new();
    private readonly CancellationTokenSource _stopCts = new();
    private Mutex? _mutex;
    private bool _mutexOwned;
    private Task? _acceptLoop;
    private long _nextConnectionId;
    private int _started;

    /// <summary>
    /// 构造即尝试获取单实例互斥体;pipeName 缺省时使用当前用户 SID 派生的默认名。
    /// </summary>
    public NamedPipeServer(
        ITaskOrchestrator orchestrator,
        Func<CancellationToken, Task<IReadOnlyList<RunSnapshot>>> listRuns,
        string? pipeName = null)
    {
        _orchestrator = orchestrator ?? throw new ArgumentNullException(nameof(orchestrator));
        _listRuns = listRuns ?? throw new ArgumentNullException(nameof(listRuns));
        _pipeName = pipeName ?? PipeNaming.CurrentUserPipeName;
        _mutexName = @"Local\" + _pipeName + "-mutex";
        AcquireSingleInstanceMutex();
    }

    /// <summary>本服务监听的 pipe 名。</summary>
    public string PipeName => _pipeName;

    /// <summary>
    /// 启动 accept 循环。可重复调用(幂等);不等待任何客户端连接。
    /// </summary>
    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (Interlocked.Exchange(ref _started, 1) != 0)
        {
            return Task.CompletedTask;
        }

        _acceptLoop = AcceptLoopAsync();
        return Task.CompletedTask;
    }

    /// <summary>只读运行状态快照(传输层状态,不触发任何 run 状态变更)。</summary>
    public Task<TransportStatus> StatusAsync(CancellationToken cancellationToken)
        => Task.FromResult(new TransportStatus
        {
            Running = true,
            ConnectedClients = _connections.Count,
            ProtocolVersion = PipeProtocol.Version,
        });

    /// <summary>
    /// 停止 accept 循环并断开全部活动连接。已接受连接的处理 Task 随后退出;幂等。
    /// </summary>
    public async Task StopAsync()
    {
        _stopCts.Cancel();
        foreach (var connection in _connections.Values)
        {
            try
            {
                connection.Stream.Dispose();
            }
            catch (Exception)
            {
                // 已断开的连接忽略。
            }
        }

        if (_acceptLoop is not null)
        {
            try
            {
                await _acceptLoop;
            }
            catch (Exception)
            {
                // 取消路径的异常不向上传播。
            }
        }
    }

    /// <summary>
    /// 向全部已连接客户端广播一帧事件(供 ITransport.SendAsync 使用)。
    /// 单个客户端写失败不影响其余客户端;广播不参与任何 run 状态机。
    /// </summary>
    public async Task BroadcastAsync(EventEnvelopeV1 envelope, CancellationToken cancellationToken)
    {
        IpcEnvelope frame = new()
        {
            Type = envelope.Type,
            CorrelationId = envelope.CorrelationId,
            Payload = JsonSerializer.SerializeToElement(envelope, IpcJson.Options),
        };

        foreach (var connection in _connections.Values)
        {
            try
            {
                await WriteFrameAsync(connection, frame, cancellationToken);
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException)
            {
                // 该客户端已断开或正在停止:跳过,不影响广播本身。
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        _stopCts.Dispose();
        if (_mutexOwned && _mutex is not null)
        {
            try
            {
                _mutex.ReleaseMutex();
            }
            catch (ApplicationException)
            {
                // 所有权已在异常路径释放:忽略。
            }

            _mutex.Dispose();
            _mutex = null;
        }
    }

    private void AcquireSingleInstanceMutex()
    {
        _mutex = new Mutex(initiallyOwned: false, _mutexName, out bool createdNew);
        if (!createdNew)
        {
            _mutex.Dispose();
            _mutex = null;
            throw new InvalidOperationException(
                $"Named Pipe 单实例互斥体已存在({_mutexName});另一个 AI Resume Worker 实例已在运行,本实例拒绝启动。");
        }

        try
        {
            // 刚创建的互斥体必然立即获得所有权;AbandonedMutexException 表示前一持有者异常终止,
            // 此时所有权已转移给当前线程,继续视为持有。
            _mutex.WaitOne(0);
            _mutexOwned = true;
        }
        catch (AbandonedMutexException)
        {
            _mutexOwned = true;
        }
    }

    private async Task AcceptLoopAsync()
    {
        while (!_stopCts.IsCancellationRequested)
        {
            var stream = new NamedPipeServerStream(
                _pipeName,
                PipeDirection.InOut,
                NamedPipeServerStream.MaxAllowedServerInstances,
                PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous);

            try
            {
                await stream.WaitForConnectionAsync(_stopCts.Token);
            }
            catch (OperationCanceledException)
            {
                stream.Dispose();
                break;
            }
            catch (IOException)
            {
                stream.Dispose();
                continue;
            }
            catch (ObjectDisposedException)
            {
                stream.Dispose();
                break;
            }

            long id = Interlocked.Increment(ref _nextConnectionId);
            var connection = new ClientConnection(stream);
            _connections[id] = connection;
            _ = Task.Run(() => HandleConnectionAsync(id, connection));
        }
    }

    private async Task HandleConnectionAsync(long id, ClientConnection connection)
    {
        var stream = connection.Stream;
        try
        {
            while (!_stopCts.IsCancellationRequested)
            {
                // 1) 长度头。
                var header = new byte[PipeProtocol.HeaderBytes];
                int headerRead = await PipeFraming.ReadExactlyAsync(stream, header, _stopCts.Token);
                if (headerRead == 0)
                {
                    break; // 对端正常关闭。
                }

                if (headerRead < PipeProtocol.HeaderBytes)
                {
                    break; // 半帧:长度头不完整,断连。
                }

                int length = BinaryPrimitives.ReadInt32LittleEndian(header);
                if (length <= 0 || length > PipeProtocol.MaxFrameBytes)
                {
                    // 非法长度:长度不可信,无法安全应答,立即断开。
                    break;
                }

                // 2) 帧体。
                var body = new byte[length];
                int bodyRead = await PipeFraming.ReadExactlyAsync(stream, body, _stopCts.Token);
                if (bodyRead != length)
                {
                    break; // 帧体不完整,断连。
                }

                // 3) JSON 解析;失败即断连(非 JSON 无法取得 correlationId 与 type)。
                IpcEnvelope? request;
                try
                {
                    request = JsonSerializer.Deserialize<IpcEnvelope>(body, IpcJson.Options);
                }
                catch (JsonException)
                {
                    break;
                }

                if (request is null)
                {
                    break;
                }

                // 4) 信封版本校验:未知版本回错误帧后断开。
                if (request.EnvelopeVersion != PipeProtocol.Version)
                {
                    await WriteFrameAsync(connection, ErrorFrame(request, PipeProtocol.ErrorUnsupportedEnvelopeVersion,
                        $"envelopeVersion \"{request.EnvelopeVersion}\" 不受支持,仅接受 \"{PipeProtocol.Version}\"。"), _stopCts.Token);
                    break;
                }

                // 5) 路由并按接收顺序应答(单循环串行,天然保序)。
                IpcEnvelope response = await RouteAsync(request, _stopCts.Token);
                await WriteFrameAsync(connection, response, _stopCts.Token);
            }
        }
        catch (OperationCanceledException)
        {
            // 服务停止中。
        }
        catch (IOException)
        {
            // 对端断开。
        }
        catch (ObjectDisposedException)
        {
            // 服务停止已释放流。
        }
        finally
        {
            _connections.TryRemove(id, out _);
            stream.Dispose();
        }
    }

    private async Task<IpcEnvelope> RouteAsync(IpcEnvelope request, CancellationToken cancellationToken)
    {
        try
        {
            switch (request.Type)
            {
                case PipeProtocol.CommandPing:
                    return new IpcEnvelope
                    {
                        Type = PipeProtocol.ResponsePong,
                        CorrelationId = request.CorrelationId,
                        Payload = JsonSerializer.SerializeToElement(
                            new { version = PipeProtocol.Version, processId = Environment.ProcessId }, IpcJson.Options),
                    };

                case PipeProtocol.CommandStart:
                {
                    StartRequest? start = DeserializePayload<StartRequest>(request);
                    if (start is null)
                    {
                        return ErrorFrame(request, PipeProtocol.ErrorMalformedPayload, "start 的 payload 无法解析为 StartRequest。");
                    }

                    StartResponse response = await _orchestrator.StartAsync(start, cancellationToken);
                    return ResponseFrame(request, PipeProtocol.ResponseStarted, response);
                }

                case PipeProtocol.CommandStatus:
                {
                    IpcStatusPayload? payload = DeserializePayload<IpcStatusPayload>(request);
                    if (payload is null || !Guid.TryParse(payload.RunId, out Guid runIdValue))
                    {
                        return ErrorFrame(request, PipeProtocol.ErrorMalformedPayload, "status 的 payload 缺少合法 runId。");
                    }

                    RunSnapshot snapshot = await _orchestrator.StatusAsync(new RunId(runIdValue), cancellationToken);
                    return ResponseFrame(request, PipeProtocol.ResponseStatus, snapshot);
                }

                case PipeProtocol.CommandCancel:
                {
                    CancelRequest? cancel = DeserializePayload<CancelRequest>(request);
                    if (cancel is null)
                    {
                        return ErrorFrame(request, PipeProtocol.ErrorMalformedPayload, "cancel 的 payload 无法解析为 CancelRequest。");
                    }

                    CancelResponse response = await _orchestrator.CancelAsync(cancel, cancellationToken);
                    return ResponseFrame(request, PipeProtocol.ResponseCancelled, response);
                }

                case PipeProtocol.CommandListRuns:
                {
                    IReadOnlyList<RunSnapshot> runs = await _listRuns(cancellationToken);
                    return ResponseFrame(request, PipeProtocol.ResponseRuns, runs);
                }

                default:
                    return ErrorFrame(request, PipeProtocol.ErrorUnknownCommand,
                        $"未知命令 \"{request.Type}\",支持:{PipeProtocol.CommandPing}/{PipeProtocol.CommandStart}/{PipeProtocol.CommandStatus}/{PipeProtocol.CommandCancel}/{PipeProtocol.CommandListRuns}。");
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw; // 调用方取消,连接将随取消退出。
        }
        catch (Exception ex)
        {
            // 注入委托异常:回结构化错误,服务端本体保持存活继续服务。
            return ErrorFrame(request, PipeProtocol.ErrorInternal, $"命令执行失败:{ex.Message}");
        }
    }

    private static T? DeserializePayload<T>(IpcEnvelope envelope)
        where T : class
    {
        if (envelope.Payload is not { } payload)
        {
            return null;
        }

        try
        {
            return payload.Deserialize<T>(IpcJson.Options);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static IpcEnvelope ResponseFrame(IpcEnvelope request, string type, object? payload)
        => new()
        {
            Type = type,
            CorrelationId = request.CorrelationId,
            Payload = payload is null ? null : JsonSerializer.SerializeToElement(payload, IpcJson.Options),
        };

    private static IpcEnvelope ErrorFrame(IpcEnvelope request, string code, string message)
        => new()
        {
            Type = PipeProtocol.ResponseError,
            CorrelationId = request.CorrelationId,
            Payload = JsonSerializer.SerializeToElement(new IpcError { Code = code, Message = message }, IpcJson.Options),
        };

    private static async Task WriteFrameAsync(ClientConnection connection, IpcEnvelope envelope, CancellationToken cancellationToken)
    {
        byte[] json = JsonSerializer.SerializeToUtf8Bytes(envelope, IpcJson.Options);
        byte[] frame = PipeFraming.Encode(json);
        await connection.WriteLock.WaitAsync(cancellationToken);
        try
        {
            await connection.Stream.WriteAsync(frame, cancellationToken);
            await connection.Stream.FlushAsync(cancellationToken);
        }
        finally
        {
            connection.WriteLock.Release();
        }
    }

    private sealed class ClientConnection
    {
        public ClientConnection(NamedPipeServerStream stream)
        {
            Stream = stream;
        }

        public NamedPipeServerStream Stream { get; }

        /// <summary>每连接写锁:同一连接内应答/广播不交错,保持帧序。</summary>
        public SemaphoreSlim WriteLock { get; } = new(1, 1);
    }
}
