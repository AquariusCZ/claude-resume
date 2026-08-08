namespace AiResume.Core;

/// <summary>
/// 运行稳定标识。由 Worker 生成并在 Start 持久接纳前确定,一经落库不可变。
/// </summary>
public readonly record struct RunId(Guid Value)
{
    public static RunId New() => new(Guid.NewGuid());

    public static RunId FromString(string value) => new(Guid.Parse(value));

    public override string ToString() => Value.ToString("D");
}
