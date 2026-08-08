namespace AiResume.Worker.Quota;

/// <summary>
/// provider 中立的配额快照,形状对齐 cc-connect <c>core.UsageReport</c>
/// (<c>Provider</c> / <c>Buckets</c> / <c>Windows{UsedPercent,WindowSeconds,ResetAfterSeconds,ResetAtUnix}</c>)。
///
/// **为什么由 AI Resume 自取而非消费 cc-connect(ADR-0003 §2.2 的记录在案偏离)**:
/// cc-connect 的 <c>claudecode.GetUsage</c> 走 PTY 驱动 Claude Code TUI 再抓屏解析,
/// 依赖 <c>creack/pty</c>;该库 <c>pty_unsupported.go</c> 的构建约束
/// (<c>!linux &amp;&amp; !darwin &amp;&amp; ...</c>)命中 Windows,<c>open()</c> 直接返回 <c>ErrUnsupported</c>,
/// 且其管理 API 全表无 usage 端点。故该通道在本产品目标平台上不可用,
/// 而限额后续跑是本产品不可替代的核心,不能建在不通的通道上。
/// 形状保持兼容,是为了将来上游可用时能无痛切换取数实现。
/// </summary>
public sealed record UsageSnapshot(
    string Provider,
    DateTimeOffset CapturedAt,
    IReadOnlyList<UsageBucket> Buckets,
    string? UnavailableReason)
{
    /// <summary>是否拿到了任何一条服务端下发的限额窗口。</summary>
    public bool HasData => Buckets.Count > 0 && Buckets.Any(b => b.Windows.Count > 0);

    /// <summary>构造一个"取不到数据"的快照。GUI 据此如实显示不可用,禁止伪造进度。</summary>
    public static UsageSnapshot Unavailable(string provider, DateTimeOffset capturedAt, string reason) =>
        new(provider, capturedAt, Array.Empty<UsageBucket>(), reason);
}

/// <summary>一组逻辑配额(对齐 cc-connect 的 <c>UsageBucket</c>)。</summary>
public sealed record UsageBucket(
    string Name,
    bool Allowed,
    bool LimitReached,
    IReadOnlyList<UsageWindow> Windows);

/// <summary>
/// 单个配额窗口。
///
/// **字段可得性不对称,这是服务端行为不是缺陷**:<c>ResetAtUnix</c> 常态下发;
/// <c>UsedPercent</c> 仅在服务端下发 <c>utilization</c> 时才有值(实测低用量时缺席)。
/// 因此 <c>UsedPercent</c> 为 <c>null</c> 表示"未报告",**不得当成 0 渲染**。
/// </summary>
public sealed record UsageWindow(
    string Name,
    string Status,
    int WindowSeconds,
    long? ResetAtUnix,
    int? ResetAfterSeconds,
    int? UsedPercent)
{
    /// <summary>Claude 5 小时窗口的秒数。</summary>
    public const int FiveHourSeconds = 5 * 60 * 60;

    /// <summary>Claude 7 天窗口的秒数。</summary>
    public const int SevenDaySeconds = 7 * 24 * 60 * 60;

    /// <summary>
    /// 由 <c>ResetAtUnix - WindowSeconds</c> 推导的窗口起点。
    /// **这是推导值不是服务端下发值**,调用方展示时须标注为推导。
    /// 缺 <c>ResetAtUnix</c> 或窗口长度未知时返回 <c>null</c>。
    /// </summary>
    public DateTimeOffset? DerivedWindowStart =>
        ResetAtUnix is { } reset && WindowSeconds > 0
            ? DateTimeOffset.FromUnixTimeSeconds(reset - WindowSeconds)
            : null;
}

/// <summary>Claude 探测失败的归类。红线:失败时额度区不得回退成"空闲"。</summary>
public enum ClaudeProbeFailure
{
    /// <summary>探测成功,不是失败。</summary>
    None = 0,

    /// <summary>已达用量/速率上限。</summary>
    Limited,

    /// <summary>未登录或凭据无效。</summary>
    Auth,

    /// <summary>订阅、账单或余额问题。</summary>
    Billing,

    /// <summary>模型不存在或不可用。</summary>
    ModelUnavailable,

    /// <summary>DNS/TCP/TLS/超时等网络类失败。</summary>
    Transient,

    /// <summary>未安装 claude CLI 或无法启动。</summary>
    NotInstalled,

    /// <summary>无法归类。</summary>
    Unknown,
}
