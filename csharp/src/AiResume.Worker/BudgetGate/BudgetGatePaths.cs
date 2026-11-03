namespace AiResume.Worker.BudgetGate;

/// <summary>
/// 子代理额度预检(ADR-0008)的固定位置:mod 随安装 payload 放在安装目录的 <c>mods\airesume-budget-gate\</c>,
/// 数据放在状态根的 <c>budget-gate\</c>(随 <see cref="ShadowPaths.Root"/>,测试经 <c>AIRESUME_SHADOW_DIR</c> 隔离)。
///
/// 数据目录必须在 AI Resume 已经建好的 state 下面:Desktop 是 MSIX 包,它拉起的 Claude Code 在
/// <c>%LOCALAPPDATA%</c> 下新建的顶层目录会被重定向进包私有目录,包外的 AI Resume 看不到(2026-11-02 实测);
/// 写进已存在的真实目录则落在真实位置。所以 mod 只在 state 存在时写,也绝不自己创建 <c>AI Resume</c>。
/// </summary>
public sealed record BudgetGatePaths(string ModDir, string DataDir)
{
    public const string ModName = "airesume-budget-gate";

    /// <summary>安装目录(或开发时的输出目录)里的 mod 位置。</summary>
    public static string ModDirUnder(string installRoot) => Path.Combine(installRoot, "mods", ModName);

    /// <summary>状态根下的数据目录(mod 默认写 <c>%LOCALAPPDATA%\AI Resume\state\budget-gate</c>,与默认状态根一致)。</summary>
    public static string DataDirUnder(string stateRoot) => Path.Combine(stateRoot, "budget-gate");

    /// <summary>当前进程看到的默认位置:mod 在本程序所在目录下,数据在状态根下。</summary>
    public static BudgetGatePaths Default => new(ModDirUnder(AppContext.BaseDirectory), DataDirUnder(ShadowPaths.Root));

    public string ConfigFile => Path.Combine(DataDir, "config.json");
    public string SnapshotFile => Path.Combine(DataDir, "quota-snapshot.json");
    public string SessionsDir => Path.Combine(DataDir, "sessions");
    public string LogsDir => Path.Combine(DataDir, "logs");
    public string CalibrationFile => Path.Combine(DataDir, "calibration.json");

    /// <summary>mod 能被 Claude Code 加载所需的最少文件。</summary>
    public static readonly string[] RequiredModFiles =
    [
        Path.Combine(".claude-plugin", "plugin.json"),
        Path.Combine("hooks", "hooks.json"),
        Path.Combine("hooks", "register.ts"),
        Path.Combine("core", "engine.ts"),
    ];
}
