namespace AiResume.Worker;

/// <summary>
/// S10-O/P1 测试钩子:仅 AIRESUME_TEST_GC_SAMPLE=1 时注册(浸泡常驻开销观测用)。
/// .NET (Core) 进程不向 ".NET CLR Memory" Windows 性能计数器发布实例,
/// 外部拿不到 GC 堆大小;浸泡判据又需要它,于是由宿主内部自报:
/// 每 5 分钟把 GC.GetTotalMemory 追加一行到 shadow 目录的 gc-samples.csv
/// (时间戳,字节数),采样器读最后一列并入浸泡 CSV。
/// 生产不设置该变量,钩子不注册,不改变生产行为(先例:AIRESUME_TEST_AUTO_PROBE)。
/// 写文件失败只记日志,绝不影响宿主运行(观测钩子不得反噬被观测对象)。
/// </summary>
public sealed class TestGcSampleHook : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(5);

    private readonly ILogger<TestGcSampleHook> _logger;
    private readonly string _csvPath;

    public TestGcSampleHook(ILogger<TestGcSampleHook> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _csvPath = Path.Combine(ShadowPaths.Root, "gc-samples.csv");
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                long bytes = GC.GetTotalMemory(forceFullCollection: false);
                Directory.CreateDirectory(ShadowPaths.Root);
                File.AppendAllText(_csvPath,
                    DateTimeOffset.Now.ToString("yyyy-MM-ddTHH:mm:sszzz") + "," + bytes + Environment.NewLine);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "test.gc.sample.write_failed");
            }

            try
            {
                await Task.Delay(Interval, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }
}
