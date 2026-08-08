namespace AiResume.Core;

/// <summary>
/// 骨架级错误分类。七个枚举成员及其 wire code 是稳定契约:
/// transient/auth/quota/model_unavailable/config/internal/cancelled。
/// 后续工作包在 ProviderAdapter/Orchestrator 内把 RunContract 的 provider/local/cancelled
/// 证据归并到本枚举后写入 RunSnapshot.ErrorClass。
/// </summary>
public enum ErrorClass
{
    Transient,
    Auth,
    Quota,
    ModelUnavailable,
    Config,
    Internal,
    Cancelled,
}

public static class ErrorClassCodes
{
    public static string ToWireCode(this ErrorClass errorClass) => errorClass switch
    {
        ErrorClass.Transient => "transient",
        ErrorClass.Auth => "auth",
        ErrorClass.Quota => "quota",
        ErrorClass.ModelUnavailable => "model_unavailable",
        ErrorClass.Config => "config",
        ErrorClass.Internal => "internal",
        ErrorClass.Cancelled => "cancelled",
        _ => throw new ArgumentOutOfRangeException(nameof(errorClass), errorClass, "未知 ErrorClass。"),
    };

    public static bool TryFromWireCode(string? code, out ErrorClass errorClass)
    {
        switch (code)
        {
            case "transient": errorClass = ErrorClass.Transient; return true;
            case "auth": errorClass = ErrorClass.Auth; return true;
            case "quota": errorClass = ErrorClass.Quota; return true;
            case "model_unavailable": errorClass = ErrorClass.ModelUnavailable; return true;
            case "config": errorClass = ErrorClass.Config; return true;
            case "internal": errorClass = ErrorClass.Internal; return true;
            case "cancelled": errorClass = ErrorClass.Cancelled; return true;
            default: errorClass = default; return false;
        }
    }
}
