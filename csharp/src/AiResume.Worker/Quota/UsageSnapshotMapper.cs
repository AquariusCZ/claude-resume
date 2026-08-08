using AiResume.Core;

namespace AiResume.Worker.Quota;

/// <summary>
/// 把既有的 <see cref="ClaudeProbeResult"/>(S5-B 实现,已解析 <c>rate_limit_info</c> 的
/// <c>resetsAt</c> 与 <c>utilization</c>)映射成 cc-connect <c>UsageReport</c> 兼容的
/// <see cref="UsageSnapshot"/>,供 GUI 额度潮汐轴消费。
///
/// **刻意不新写一份 stream-json 解析器**:解析逻辑已在 <c>ClaudeCodeProbe.Classify</c> 中且有测试覆盖,
/// 再写一份会形成两条会漂移的代码路径。本类只做形状转换,不做任何解析。
/// </summary>
public static class UsageSnapshotMapper
{
    /// <summary>探测所用的 provider 标识,与 cc-connect <c>UsageReport.Provider</c> 取值一致。</summary>
    public const string ProviderName = "claudecode";

    /// <summary>
    /// 转换一次探测结果。<paramref name="now"/> 必须由调用方显式传入(用于计算
    /// <c>ResetAfterSeconds</c>),便于测试固定时刻。
    /// </summary>
    public static UsageSnapshot FromProbe(ClaudeProbeResult result, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(result);

        var windows = new List<UsageWindow>(2);
        AppendWindow(windows, "five_hour", UsageWindow.FiveHourSeconds,
            result.FiveHourResetUtc, result.FiveHourUtil, result, now);
        AppendWindow(windows, "seven_day", UsageWindow.SevenDaySeconds,
            result.SevenDayResetUtc, result.SevenDayUtil, result, now);

        bool limitReached = result.IsLimited;
        var bucket = new UsageBucket("Usage", !limitReached, limitReached, windows);

        // 窗口为空时仍返回 bucket:LimitReached 本身就是有效信息(限流但服务端未附窗口时尤其如此)。
        // HasData 会因此为 false,由 GUI 据 UnavailableReason 如实显示,禁止渲染成 0%。
        string? unavailable = windows.Count > 0 ? null : DescribeMissingWindows(result);

        return new UsageSnapshot(ProviderName, now, new[] { bucket }, unavailable);
    }

    private static void AppendWindow(
        List<UsageWindow> windows,
        string name,
        int windowSeconds,
        DateTimeOffset? resetUtc,
        double? utilization,
        ClaudeProbeResult result,
        DateTimeOffset now)
    {
        // 服务端两项都没下发 = 该窗口本次无数据,不构造空壳窗口(空壳会被前端误读成"已知且为 0")。
        if (resetUtc is null && utilization is null)
        {
            return;
        }

        long? resetAtUnix = resetUtc?.ToUnixTimeSeconds();

        int? resetAfterSeconds = null;
        if (resetAtUnix is { } unix)
        {
            // 先在 long 上做减法再截断:已过期的窗口显示 0 而不是负数。
            long delta = unix - now.ToUnixTimeSeconds();
            resetAfterSeconds = delta <= 0 ? 0 : (int)Math.Min(delta, int.MaxValue);
        }

        int? usedPercent = null;
        if (utilization is { } util)
        {
            usedPercent = Math.Clamp((int)Math.Round(util * 100), 0, 100);
        }

        windows.Add(new UsageWindow(
            name,
            DescribeStatus(result),
            windowSeconds,
            resetAtUnix,
            resetAfterSeconds,
            usedPercent));
    }

    /// <summary>
    /// 窗口级状态。
    /// **探测结果只带全局判定,不区分是哪个窗口被限流**,因此这里给的是探测级状态;
    /// 精确到窗口的限流事实以 <see cref="UsageBucket.LimitReached"/> 为准。
    /// </summary>
    private static string DescribeStatus(ClaudeProbeResult result)
    {
        if (result.IsLimited)
        {
            return "blocked";
        }

        return result.Ready ? "allowed" : string.Empty;
    }

    /// <summary>没有任何窗口时,说明为什么——GUI 据此如实显示,不得回退成"空闲"。</summary>
    private static string DescribeMissingWindows(ClaudeProbeResult result) => result.Reason switch
    {
        "ok" => "本次探测未下发限额窗口(用量未达服务端报告阈值)",
        "limited" => "已达用量上限,但服务端未附窗口信息",
        "auth" => "Claude 未登录或凭据无效",
        "billing" => "订阅或账单异常",
        "model_unavailable" => "探测所用模型不可用",
        "transient" => "网络异常,未能取得限额数据",
        "no-claude" => "未检测到 claude CLI",
        "timeout" => "探测超时",
        "spawn-failed" => "无法启动 claude 进程",
        "cancelled" => "探测已取消",
        _ => "探测未取得限额数据:" + result.Reason,
    };
}
