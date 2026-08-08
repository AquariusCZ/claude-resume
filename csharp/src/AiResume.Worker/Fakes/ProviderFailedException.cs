using AiResume.Core;

namespace AiResume.Worker.Fakes;

/// <summary>
/// provider 明确失败(服务端/进程级结构化失败)。
/// 骨架级约定(Stage 2):FakeProviderAdapter 在脚本到达 Fail 步时于 StatusAsync 抛出;
/// 编排器捕获并推进 failed_provider/failed_local,且检查副作用标记决定是否允许 fallback。
/// 真实适配(Stage 4/5)改为解析 provider 结构化输出,不依赖异常通道。
/// </summary>
public sealed class ProviderFailedException : Exception
{
    public ErrorClass ErrorClass { get; }

    public string ErrorCode { get; }

    public ProviderFailedException(ErrorClass errorClass, string errorCode, string message)
        : base(message)
    {
        ErrorClass = errorClass;
        ErrorCode = errorCode;
    }
}
