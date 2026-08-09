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
        // “不是 limited”不等于“探测成功”。CLI 可能先收到部分 rate_limit_info,
        // 随后因网络/认证/进程错误失败;这种快照可展示窗口,但绝不能变成绿色可用。
        var bucket = new UsageBucket("Usage", result.Ready && !limitReached, limitReached, windows);

        // 窗口为空时仍返回 bucket:LimitReached 本身就是有效信息(限流但服务端未附窗口时尤其如此)。
        // HasData 会因此为 false,由 GUI 据 UnavailableReason 如实显示,禁止渲染成 0%。
        string? unavailable = windows.Count > 0
            ? DescribePartialObservation(result)
            : DescribeMissingWindows(result);

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
            DescribeStatus(result, usedPercent),
            windowSeconds,
            resetAtUnix,
            resetAfterSeconds,
            usedPercent));
    }

    /// <summary>
    /// 窗口级状态。CLI 探测的 <c>result.IsLimited</c> 只有全局语义,不知道究竟
    /// 是 5 小时、7 天还是按模型额度触发,因此不能把它复制到每个窗口。
    /// 只有窗口自己的 utilization 到 100% 才标 blocked;全局结论留在 bucket。
    /// </summary>
    private static string DescribeStatus(ClaudeProbeResult result, int? usedPercent)
    {
        if (usedPercent is >= 100)
        {
            return "blocked";
        }

        // 有低于 100% 的窗口读数,或者最小真实请求已经成功,都足以证明该窗口
        // 当前不是满额。只有 reset + 全局 limited 时保持未知,不猜是哪一窗触发。
        return usedPercent is not null || result.Ready ? "allowed" : string.Empty;
    }

    private static string? DescribePartialObservation(ClaudeProbeResult result)
    {
        if (result.Ready || result.IsLimited)
        {
            return null;
        }

        return result.Reason switch
        {
            "auth" => "Claude 未登录或凭据无效,仅取得部分窗口信息",
            "billing" => "订阅或账单异常,仅取得部分窗口信息",
            "model_unavailable" => "探测所用模型不可用,仅取得部分窗口信息",
            "transient" => "网络异常,仅取得部分窗口信息",
            "no-claude" => "未检测到 claude CLI,仅取得部分窗口信息",
            "timeout" => "探测超时,仅取得部分窗口信息",
            "spawn-failed" => "无法启动 claude 进程,仅取得部分窗口信息",
            "cancelled" => "探测已取消,仅取得部分窗口信息",
            _ => "探测失败,仅取得部分窗口信息:" + result.Reason,
        };
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
