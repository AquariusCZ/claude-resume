using AiResume.Core.Abstractions;
using AiResume.Core.Contracts;
using AiResume.Core.Events;

namespace AiResume.Ipc;

/// <summary>
/// ITransport 的 Named Pipe 实现(Stage 2-C)。
/// Start/Status/Cancel 管理服务端生命周期;SendAsync 向全部已连接客户端广播事件帧。
/// 本实现不拥有任何运行状态:命令均转发到注入的 ITaskOrchestrator 与查询委托。
/// </summary>
public sealed class NamedPipeTransport : ITransport
{
    private readonly ITaskOrchestrator _orchestrator;
    private readonly Func<CancellationToken, Task<IReadOnlyList<RunSnapshot>>> _listRuns;
    private NamedPipeServer? _server;

    public NamedPipeTransport(
        ITaskOrchestrator orchestrator,
        Func<CancellationToken, Task<IReadOnlyList<RunSnapshot>>> listRuns)
    {
        _orchestrator = orchestrator ?? throw new ArgumentNullException(nameof(orchestrator));
        _listRuns = listRuns ?? throw new ArgumentNullException(nameof(listRuns));
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (_server is not null)
        {
            return;
        }

        var server = new NamedPipeServer(_orchestrator, _listRuns);
        await server.StartAsync(cancellationToken);
        _server = server;
    }

    public Task<TransportStatus> StatusAsync(CancellationToken cancellationToken)
        => _server is null
            ? Task.FromResult(new TransportStatus { Running = false, ConnectedClients = 0, ProtocolVersion = PipeProtocol.Version })
            : _server.StatusAsync(cancellationToken);

    public async Task CancelAsync(CancellationToken cancellationToken)
    {
        if (_server is not null)
        {
            await _server.DisposeAsync();
            _server = null;
        }
    }

    public async Task SendAsync(EventEnvelopeV1 envelope, CancellationToken cancellationToken)
    {
        if (_server is null)
        {
            throw new InvalidOperationException("Transport 尚未启动,无法发送事件。");
        }

        await _server.BroadcastAsync(envelope, cancellationToken);
    }
}
