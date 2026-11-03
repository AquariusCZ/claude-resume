using AiResume.Worker.Products;

namespace AiResume.Worker.BudgetGate;

/// <summary>
/// 预检 mod 数据的看护(ADR-0008 §C):mod API 没有删除文件的能力,心跳与日志只会越积越多,由这里按保留期清理;
/// config.json 被删或改坏时按持久化的意图重写(mod 读不到 config.json 会回落到只记录,界面就会显示"未生效")。
/// 每小时一拍,单拍异常只记日志。
/// </summary>
public sealed class BudgetGateJanitor : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromHours(1);
    public static readonly TimeSpan LogRetention = TimeSpan.FromDays(30);
    public static readonly TimeSpan HeartbeatRetention = TimeSpan.FromDays(7);

    private readonly BudgetGateControl _control;
    private readonly ILogger<BudgetGateJanitor> _logger;
    private readonly Func<DateTimeOffset> _clock;

    public BudgetGateJanitor(ProductConfigStore store, ILogger<BudgetGateJanitor> logger)
        : this(new BudgetGateControl(store, BudgetGatePaths.Default, new ClaudePluginDirs()), logger)
    {
    }

    public BudgetGateJanitor(BudgetGateControl control, ILogger<BudgetGateJanitor> logger, Func<DateTimeOffset>? clock = null)
    {
        _control = control;
        _logger = logger;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                SweepOnce();
            }
            catch (Exception ex)
            {
                _logger.LogWarning("budget.janitor.error error={Error}", ex.GetType().Name);
            }

            try
            {
                await Task.Delay(Interval, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }

    /// <summary>
    /// 清理一轮;返回删掉的文件数。顺带两件看护:组织策略开始禁止旁加载时撤下登记(否则新会话一启动就退出),
    /// 以及删掉进程中途退出留下的半截临时文件(config.json / 快照的原子替换中间件)。
    /// </summary>
    public int SweepOnce()
    {
        DateTimeOffset now = _clock();
        try
        {
            if (_control.EnforcePolicy())
                _logger.LogWarning("budget.policy.unregistered reason=sideload-blocked-or-unverified");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or InvalidOperationException)
        {
            // settings.json 正被别的进程改写:这一拍不撤,下一拍再查;清理照常进行。
            _logger.LogWarning("budget.policy.check.failed error={Error}", ex.GetType().Name);
        }

        int removed = Sweep(_control.Paths.LogsDir, "*.jsonl", now - LogRetention)
                    + Sweep(_control.Paths.SessionsDir, "*.json", now - HeartbeatRetention)
                    + Sweep(_control.Paths.DataDir, "*.tmp-*", now - TempRetention);
        if (_control.HealConfig())
            _logger.LogInformation("budget.config.healed");
        if (removed > 0)
            _logger.LogInformation("budget.janitor.swept removed={Removed}", removed);
        return removed;
    }

    private static readonly TimeSpan TempRetention = TimeSpan.FromHours(1);

    private static int Sweep(string dir, string pattern, DateTimeOffset cutoff)
    {
        if (!Directory.Exists(dir))
            return 0;
        int removed = 0;
        foreach (FileInfo file in new DirectoryInfo(dir).EnumerateFiles(pattern))
        {
            if (file.LastWriteTimeUtc >= cutoff.UtcDateTime)
                continue;
            try
            {
                file.Delete();
                removed += 1;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // 正被某个会话的 mod 写着:下一拍再删
            }
        }

        return removed;
    }
}
