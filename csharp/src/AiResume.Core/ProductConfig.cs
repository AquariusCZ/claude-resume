using System.Text.Json;

namespace AiResume.Core;

/// <summary>
/// 项目引用(selected/customProjects 元素)。字段名与现役 config.json 对齐(name/path)。
/// </summary>
public sealed class ProjectRef
{
    public string Name { get; set; } = string.Empty;

    public string Path { get; set; } = string.Empty;
}

/// <summary>
/// shadow 产品配置(Stage 5 子集)。字段名与现役 %LOCALAPPDATA%\ClaudeResume\config.json
/// 对齐(camelCase),System.Text.Json 往返(反序列化大小写不敏感,兼容 PowerShell 写出的配置)。
///
/// 所有权:生产 config.json 由旧系统(PowerShell GUI + Node)经 config.json.write.lock 唯一写入;
/// C# shadow 只读写自己的 shadow config(ProductConfigStore),绝不触碰生产 AppDir。
/// </summary>
public sealed class ProductConfig
{
    /// <summary>JSON 序列化选项(全解决方案统一使用,避免各组件选项漂移)。</summary>
    public static JsonSerializerOptions JsonOptions { get; } = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    public bool Enabled { get; set; }

    public bool Armed { get; set; }

    public string ArmCycleId { get; set; } = string.Empty;

    public bool Continuous { get; set; }

    public List<ProjectRef> Selected { get; set; } = new();

    public List<ProjectRef> CustomProjects { get; set; } = new();

    public List<string> HiddenProjects { get; set; } = new();

    public string ProjectHome { get; set; } = string.Empty;

    public int ProbeIntervalMinutes { get; set; } = 15;

    /// <summary>
    /// GUI 额度面板的自动刷新间隔(分钟,默认 15,与 ccusage 之类监视器的常用节奏一致)。
    /// 只影响界面刷新,不影响续跑引擎的探测节奏(那个由 <see cref="ProbeIntervalMinutes"/> 决定)。
    /// </summary>
    public int QuotaRefreshMinutes { get; set; } = 15;

    public string ProbeModel { get; set; } = "haiku";

    public string ResumeModel { get; set; } = string.Empty;

    public string ResumePrompt { get; set; } = "continue";

    /// <summary>
    /// 续跑时是否给 Claude Code 追加 <c>--dangerously-skip-permissions</c>。
    ///
    /// **默认必须是 false(fail-closed)**:这是安全相关开关,配置文件损坏或字段缺失时
    /// 反序列化会回落到这里的初始值。原本默认 true —— 一个被截断的 config.json
    /// 会让后台续跑静默地以"跳过全部权限确认"运行,比直接失败危险得多(S10-O/D5)。
    /// 正常路径不受影响:保存时整个对象都会被序列化,该字段总是显式写出。
    /// 代价是配置损坏时无人值守续跑会卡在权限确认上 —— 卡住是安全的失败。
    /// </summary>
    public bool SkipPermissions { get; set; }

    /// <summary>cc-connect 项目绑定的 agent 类型。见 CcConnectAgents.Supported。</summary>
    public string CcConnectAgent { get; set; } = "claudecode";

    public string DirtyGuard { get; set; } = "stash";

    public static ProductConfig CreateDefault() => new();
}
