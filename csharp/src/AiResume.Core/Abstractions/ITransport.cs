using AiResume.Core.Contracts;
using AiResume.Core.Events;

namespace AiResume.Core.Abstractions;

/// <summary>
/// 跨进程传输(Named Pipe,Stage 2-C 实现)。生命周期仍遵循 Start/Status/Cancel 模式,
/// 单帧请求的超时属于传输层(不得成为 AI run 总时限)。
/// </summary>
public interface ITransport
{
    Task StartAsync(CancellationToken cancellationToken);

    Task<TransportStatus> StatusAsync(CancellationToken cancellationToken);

    Task CancelAsync(CancellationToken cancellationToken);

    Task SendAsync(EventEnvelopeV1 envelope, CancellationToken cancellationToken);
}
