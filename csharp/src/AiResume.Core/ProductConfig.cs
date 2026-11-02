using System.Text.Json;

namespace AiResume.Core;

/// <summary>
/// 项目引用(selected/customProjects 元素)。字段名与现役 config.json 对齐(name/path)。
/// </summary>
public sealed class ProjectRef
{
    public string Name { get; set; } = string.Empty;

    public string Path { get; set; } = string.Empty;

    /// <summary>布防时固定的原生会话 UUID，执行时禁止用全局 --last 猜测。</summary>
    public string SessionId { get; set; } = string.Empty;

    /// <summary>
    /// 本项目续跑用的模型调用 ID,布防时冻结。默认跟随该会话最近一次真实应答的模型，
    /// 用户可逐项目改选;多个项目可以指向同一个模型(共用同一份额度证据)。
    /// 空 = 旧配置，沿用 <see cref="ResumeTarget.ModelId"/>。
    /// </summary>
    public string ModelId { get; set; } = string.Empty;

    public string ModelName { get; set; } = string.Empty;

    /// <summary>session(跟随会话)/ user(手选)/ default(会话模型不在目录时退回默认模型)。</summary>
    public string ModelSource { get; set; } = string.Empty;
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

    /// <summary>布防时固定的供应商、账号和实际模型；旧配置缺失时需重新布防。</summary>
    public ResumeTarget? ResumeTarget { get; set; }

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

    /// <summary>
    /// 用户想开着的完成通知源(NotificationProviderKind 的名字)。
    ///
    /// **这是"意图",不是"现状"。** 现状要去 <c>~/.claude</c>、<c>~/.codex</c> 这些
    /// 别人家的配置里探;意图只能由我们自己记。
    ///
    /// 不记的后果实测过:<c>install → uninstall → install</c>,第二次安装返回成功、
    /// 打印"入口已全部指向安装目录",而五个通知源全是关的 ——
    /// 因为重指那一步的判据是"当前已启用的才重指",卸载刚把它们全关掉,
    /// 于是循环体一次都没进,退出码 0(2026-08-08 第二轮审计 B3)。
    /// 现状被自己的上一步清空了,就再也没有东西能告诉安装程序该恢复什么。
    /// </summary>
    public List<string> NotifySources { get; set; } = new();

    /// <summary>
    /// <see cref="NotifySources"/> 是否被记录过(界面开关、<c>notify enable/disable</c>、install 对账都会置 true)。
    /// 用来区分「从未记录」的旧配置与「用户把通知源全关了」的空列表:后者要求投递端丢弃一切钩子事件。
    /// </summary>
    public bool NotifyIntentRecorded { get; set; }

    /// <summary>
    /// 飞书通知的收件人 open_id(明文，唯一真身)。
    ///
    /// 它不是机密，而是 lark-cli 所登录应用下的用户标识:由控制面「飞书通知」设置，
    /// 或从 <c>lark-cli auth list</c> 识别(同一应用下的 open_id 才有效，open_id 按应用隔离)。
    /// Worker 以 S4U 计划任务跑在会话 0,用户范围 DPAPI 在那里会间歇失败数小时(2026-09/10 日志实证),
    /// 所以收件人必须是明文配置，投递路径不得为了它去解任何密文。
    /// </summary>
    public string NotifyRecipientOpenId { get; set; } = string.Empty;

    public string DirtyGuard { get; set; } = "stash";

    public static ProductConfig CreateDefault() => new();
}
