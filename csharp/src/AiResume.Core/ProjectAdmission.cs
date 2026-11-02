namespace AiResume.Core;

/// <summary>
/// 一个项目在一次取证里的准入事实。四个维度彼此独立，各自只回答一个问题：
/// <list type="bullet">
/// <item><b>Evidence(义务)</b>:这个会话需要续吗？Claude 看会话尾部的限额中断记录;
///   Codex 没有会话证据(<c>none</c>),沿用「观测到它的模型受限」的旧语义。</item>
/// <item><b>Quota(额度)</b>:这个项目的模型此刻有没有额度？同账号同模型的项目共用一份实时证据。</item>
/// <item><b>Session(占用)</b>:会话或同目录是否被别的客户端拿着？只挡这一个项目。</item>
/// <item>进程门禁(自己是否还有没确认退出的进程)是全局的，不在这里。</item>
/// </list>
/// 任何一维「未核实」都只导致等待，绝不放行。
/// </summary>
public sealed record ProjectAdmission
{
    public string Path { get; init; } = string.Empty;

    /// <summary>本项目续跑用的模型调用 ID。</summary>
    public string ModelId { get; init; } = string.Empty;

    /// <summary>
    /// ok / limited / unknown / provider-changed / identity-unverified / credentials-unavailable /
    /// model-unverified / skipped(本拍没有待续跑项目，没有发起额度请求)。
    /// </summary>
    public string Quota { get; init; } = "unknown";

    /// <summary>额度未放行的窗口或原因(如 <c>weekly_scoped:Fable</c>),只含窗口名与状态码。</summary>
    public string? QuotaDetail { get; init; }

    /// <summary>阻挡本项目的额度窗口预计重置时间(服务端值),仅用于展示与提醒。</summary>
    public DateTimeOffset? QuotaResetUtc { get; init; }

    /// <summary>idle / in-use(绑定会话在客户端打开)/ cwd-busy(同目录另一会话正在运行)/ unverified / n/a(Codex)。</summary>
    public string Session { get; init; } = "n/a";

    /// <summary>占用该会话的客户端入口(如 claude-desktop / cli),只用于提示文案。</summary>
    public string? SessionClient { get; init; }

    /// <summary>
    /// interrupted / clear / unverified(会话尾部读不出)/ superseded(中断之后同目录新开的会话有了真实应答:
    /// 用户已换会话接着干，不再自动续)/ none(无会话证据)。
    /// </summary>
    public string Evidence { get; init; } = "none";

    /// <summary>限额中断记录的 uuid;同一次中断 AI Resume 最多续一次。</summary>
    public string? InterruptionId { get; init; }

    public DateTimeOffset? InterruptedAtUtc { get; init; }

    /// <summary>中断记录里服务端给出的限额类型(five_hour / seven_day 等)与重置时间。</summary>
    public string? LimitType { get; init; }

    public DateTimeOffset? LimitResetsAtUtc { get; init; }

    /// <summary>额度、占用、证据三维都允许启动(是否「待续跑」由状态机结合本周期记录判定)。</summary>
    public bool Admissible => Quota == "ok" && Session is "idle" or "n/a" && Evidence is not ("unverified" or "superseded");
}
