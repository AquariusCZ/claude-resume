namespace AiResume.Worker;

/// <summary>
/// Worker 观察周期配置。合法范围 15-30 秒,默认 20 秒(RUN-CONTRACT 不变量 2)。
/// 周期只驱动持久状态与进程存活性观察,绝不构成 AI run 总时限。
/// </summary>
public sealed class ObservationOptions
{
    public const string SectionName = "Observation";

    public const double MinSeconds = 15;
    public const double MaxSeconds = 30;

    public TimeSpan Interval { get; set; } = TimeSpan.FromSeconds(20);

    public void Validate()
    {
        if (Interval < TimeSpan.FromSeconds(MinSeconds) || Interval > TimeSpan.FromSeconds(MaxSeconds))
        {
            throw new ArgumentOutOfRangeException(
                nameof(Interval),
                Interval,
                $"观察周期必须在 {MinSeconds}-{MaxSeconds} 秒之间,当前为 {Interval.TotalSeconds} 秒。");
        }
    }
}
