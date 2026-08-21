using AiResume.Core;
using AiResume.Core.Abstractions;
using AiResume.Core.Contracts;
using AiResume.Ipc;
using AiResume.Storage;
using AiResume.Worker;
using AiResume.Worker.Fakes;
using AiResume.Worker.Logging;
using AiResume.Worker.Migration;
using AiResume.Worker.Orchestration;
using AiResume.Worker.Probes;
using AiResume.Worker.Products;
using AiResume.Worker.Quota;
using AiResume.Worker.Resume;
using AiResume.Worker.Supervision;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

// S9 数据迁移入口。在建 Host **之前**分支:迁移是一次性命令,不该起
// BackgroundService,也不该因为 Host 的 DI 装配失败而跑不起来。
if (args.Length > 0 && string.Equals(args[0], "migrate", StringComparison.OrdinalIgnoreCase))
{
    return MigrationCommand.Run(args);
}

// S10 生产切换预检:确认本机没有第二个飞书事件消费者。只读、不停任何进程。
if (args.Length > 0 && string.Equals(args[0], "preflight", StringComparison.OrdinalIgnoreCase))
{
    return PreflightCommand.Run();
}

// 开始菜单 + 开机自启快捷方式。Worker 自启是产品功能的一部分:
// 续跑编排跑在本进程里,没有启动入口就等于装了不会跑。
if (args.Length > 0 && string.Equals(args[0], "shortcuts", StringComparison.OrdinalIgnoreCase))
{
    return ShortcutCommand.Run(args);
}

// 把产物装到 %LOCALAPPDATA%\AI Resume\ 并把所有入口指向那里。
// 此前快捷方式与 Stop 钩子直指 bin\Debug,清 bin/换分支/改仓库名都会静默断掉。
if (args.Length > 0 &&
    (string.Equals(args[0], "install", StringComparison.OrdinalIgnoreCase) ||
     string.Equals(args[0], "uninstall", StringComparison.OrdinalIgnoreCase)))
{
    return InstallCommand.Run(args);
}

// S10 从现役 AppDir 导入飞书凭据到 DPAPI。切换用的是同一个飞书应用,凭据本来就在本机;
// 机器到机器搬运,值不显示、不入日志。做成命令而不是 GUI 按钮:它是一次性动作,
// 界面上常驻一个"导入"按钮只会让人以为需要反复点。
if (args.Length > 0 && string.Equals(args[0], "import-feishu", StringComparison.OrdinalIgnoreCase))
{
    return ImportFeishuCommand.Run();
}

// 凭据健康检查:用 DPAPI 里的凭据独立换一次 tenant_access_token,
// 区分「凭据失效」与「客户端坏了」。只打状态码与飞书错误码,不碰 secret 实值。
if (args.Length > 0 && string.Equals(args[0], "feishu-check", StringComparison.OrdinalIgnoreCase))
{
    return FeishuCheckCommand.Run();
}

// S10 生成 cc-connect 配置(凭据只从环境变量取,不经命令行、不进日志)。
if (args.Length > 0 && string.Equals(args[0], "cutover-config", StringComparison.OrdinalIgnoreCase))
{
    return CutoverConfigCommand.Run(args);
}

// S10-P 把 AI Resume 的项目清单同步进 cc-connect 的 /dir 历史。
if (args.Length > 0 && string.Equals(args[0], "sync-dirs", StringComparison.OrdinalIgnoreCase))
{
    return SyncDirsCommand.Run(args);
}

// 本地完成通知源的启用/禁用(B5 需要;GUI 那个开关无法自动化)。
if (args.Length > 0 && string.Equals(args[0], "notify", StringComparison.OrdinalIgnoreCase))
{
    return NotifyCommand.Run(args);
}

// **无法识别的子命令与帮助请求一律拦下,绝不落进 Host**。
//
// 原本这里没有兜底:任何拼错的子命令(实测 `--help`)都会一路穿过全部分支,
// 被 Host.CreateApplicationBuilder 当成配置参数吞掉,于是 Worker
// **静默地以常驻服务启动**——抢走生产的单实例互斥体、跑观测循环,
// 而调用者以为自己只是打了个帮助。2026-08-06 实测,误起的实例跑了 20 分钟才被发现。
//
// 判据是「首参是否以 `-` 开头」:子命令从不带前导横线,而 Host 的配置参数
// (如 `--Observation:IntervalSeconds=00:00:15`,PowerLossRecoveryTests 真在用)
// 一定带。所以只拦下**不带横线的未知首参**,再单独认掉 help 家族。
{
    const string Usage = "可用命令:install / uninstall / migrate / preflight / shortcuts / import-feishu / feishu-check / cutover-config / sync-dirs / notify\n"
        + "不带任何子命令运行才会以常驻服务启动(此时可传 --Section:Key=Value 形式的宿主配置)。";

    if (args.Length > 0 && (args[0] is "--help" or "-h" or "-?" or "/?"))
    {
        Console.WriteLine(Usage);
        return 0;
    }

    if (args.Length > 0 && !args[0].StartsWith('-'))
    {
        Console.Error.WriteLine($"未知命令:{args[0]}");
        Console.Error.WriteLine(Usage);
        return 2;
    }
}

HostApplicationBuilder builder = Host.CreateApplicationBuilder(args);

// 恢复对账是所有后台服务的硬门禁。显式锁定串行启动，避免宿主配置把
// ServicesStartConcurrently 打开后让 IPC、观察或续跑引擎抢在 RecoverAsync 前运行。
builder.Services.Configure<HostOptions>(options => options.ServicesStartConcurrently = false);

// 状态目录与数据库迁移。EnsureRoot 会顺带把旧 ClaudeResumeShadow 的内容
// 搬到 %LOCALAPPDATA%\AI Resume\state(已存在同名项则跳过,绝不覆盖)。
ShadowPaths.EnsureRoot();
StorageDatabase.Migrate(ShadowPaths.RunDatabasePath);

builder.Services.AddSingleton(new RunStore(ShadowPaths.RunDatabasePath));
builder.Services.AddSingleton(new ProcessSupervisor(ShadowPaths.RunDatabasePath));
builder.Services.AddSingleton<IProcessSupervisor>(sp => sp.GetRequiredService<ProcessSupervisor>());
builder.Services.AddSingleton<IProviderAdapter, FakeProviderAdapter>();
builder.Services.AddSingleton<TaskOrchestrator>();
builder.Services.AddSingleton<ITaskOrchestrator>(sp => sp.GetRequiredService<TaskOrchestrator>());
builder.Services.AddSingleton<IHealthProbe, FakeHealthProbe>();

// 任何消费续跑/编排状态的后台服务启动前，先核验上次宿主留下的 process_registry。
// Gone/Mismatched 登记由既有 RecoverAsync 清理；Matched/Unverifiable 继续 fail-closed 保留。
builder.Services.AddHostedService<ProcessRecoveryBootstrap>();

// Named Pipe 服务端(S2-G):GUI 经 ping 探测 Worker;list-runs 返回活动 run 快照。
builder.Services.AddSingleton<ITransport>(sp =>
{
    var orchestrator = sp.GetRequiredService<ITaskOrchestrator>();
    var store = sp.GetRequiredService<RunStore>();
    return new NamedPipeTransport(orchestrator, async ct =>
    {
        var runs = new List<RunSnapshot>();
        foreach (RunId runId in store.EnumerateActiveRuns())
        {
            runs.Add(await orchestrator.StatusAsync(runId, ct));
        }

        return runs;
    });
});
builder.Services.AddHostedService<TransportBootstrap>();
builder.Services.AddHostedService<ObservationWorker>();
// S12 完成通知投递:钩子只写队列,在此之前没有任何消费者,
// 表现为「任务跑完了但什么通知都没有」。
builder.Services.AddHostedService<AiResume.Worker.Notifications.NotificationWorker>();

// S7-D 续跑引擎:限额后自动续跑是本产品唯一不可替代的核心(ADR-0003 §2.2)。
// 此前 CheckerCycle 状态机已完整实现却无人驱动,这里补上驱动者。
// 探测与续跑都经真实实现(不是 Fake):IProviderAdapter 那条 Fake 链路是 S2 编排器的,与此无关。
builder.Services.AddSingleton(new ProductConfigStore(ShadowPaths.Root));
builder.Services.AddSingleton(new ProductStateStore(ShadowPaths.RunDatabasePath));
builder.Services.AddQuotaResumeAdmission();
builder.Services.AddSingleton<CheckerCycle>(sp => new CheckerCycle(sp.GetRequiredService<ProductStateStore>()));
builder.Services.AddSingleton<IClaudeResumeRunner>(sp =>
    new ClaudeResumeRunner(sp.GetRequiredService<IProcessSupervisor>()));
builder.Services.AddSingleton<ResumeEngine>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<ResumeEngine>());
builder.Services.Configure<ObservationOptions>(
    builder.Configuration.GetSection(ObservationOptions.SectionName));

// 结构化单行 JSON 文件日志(按日滚动);全路径经 SecretRedactor 脱敏(S2-F)。
//
// 先 ClearProviders 再按需加回:Host.CreateApplicationBuilder 在 Windows 上默认挂 EventLog 提供程序,
// 它需要 System.Diagnostics.EventLog.dll。该程序集不一定随宿主复制到每个输出目录
// (S5-D 恢复测试从测试输出目录拉起宿主时就缺),而 ILogger<T> 一被解析就会实例化**全部**
// 提供程序,于是宿主在 DI 解析阶段直接 FileNotFoundException 崩溃、连日志都来不及写。
// 本产品不往 Windows 事件日志写任何东西,这个依赖纯属负担。
builder.Logging.ClearProviders();
builder.Logging.AddSimpleConsole();
builder.Logging.AddProvider(new DailyJsonFileLoggerProvider(ShadowPaths.LogsDirectory));

// S5-D 测试钩子:仅 AIRESUME_TEST_AUTO_PROBE=1 时启用(断电真 kill 恢复验证用;
// 生产不设置该变量,钩子默认关闭,不改变生产行为)。
if (Environment.GetEnvironmentVariable("AIRESUME_TEST_AUTO_PROBE") == "1")
{
    builder.Services.AddHostedService<TestAutoProbeHook>();
}

// S10-O/P1 浸泡观测钩子:仅 AIRESUME_TEST_GC_SAMPLE=1 时启用(常驻开销浸泡用;
// .NET Core 不发布 ".NET CLR Memory" 性能计数器实例,GC 堆只能宿主内自报)。
// 生产不设置该变量,钩子不注册,不改变生产行为。
if (Environment.GetEnvironmentVariable("AIRESUME_TEST_GC_SAMPLE") == "1")
{
    builder.Services.AddHostedService<TestGcSampleHook>();
}

await builder.Build().RunAsync();
return 0;

/// <summary>随宿主生命周期启动/停止 Named Pipe 服务端。</summary>
internal sealed class TransportBootstrap : IHostedService
{
    private readonly ITransport _transport;

    public TransportBootstrap(ITransport transport)
    {
        _transport = transport;
    }

    public Task StartAsync(CancellationToken cancellationToken) => _transport.StartAsync(cancellationToken);

    public Task StopAsync(CancellationToken cancellationToken) => _transport.CancelAsync(cancellationToken);
}

/// <summary>宿主启动门禁：先完成进程登记恢复，再开放 IPC、观察与续跑服务。</summary>
internal sealed class ProcessRecoveryBootstrap : IHostedService
{
    private readonly ProcessSupervisor _supervisor;
    private readonly ILogger<ProcessRecoveryBootstrap> _logger;

    public ProcessRecoveryBootstrap(
        ProcessSupervisor supervisor,
        ILogger<ProcessRecoveryBootstrap> logger)
    {
        _supervisor = supervisor;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        RecoveryReport report = await _supervisor.RecoverAsync(cancellationToken).ConfigureAwait(false);
        int removed = report.Items.Count(item => item.Action == RecoveryAction.RemoveRegistry);
        int blocked = report.Items.Count(item => item.Action == RecoveryAction.KeepFailClosed);
        _logger.LogInformation(
            "process.recovery.completed total={Total} removed={Removed} failClosed={FailClosed}",
            report.Items.Count,
            removed,
            blocked);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

/// <summary>
/// S5-D 测试钩子:宿主启动后自动 Start 一个 fake probe run 并打日志
/// (供 PowerLossRecoveryTests 在 run 运行中硬杀宿主验证断电恢复)。
/// runKey 一律经 RunKey.Create 规范生成(D-011);ProfileId="probe" 与 S5-B
/// ClaudeCodeProviderAdapter 的 probe 判别约定一致。Start 失败仅记录日志,
/// 不阻止宿主启动(钩子不得影响生产路径)。
/// </summary>
internal sealed class TestAutoProbeHook : IHostedService
{
    private readonly TaskOrchestrator _orchestrator;
    private readonly ILogger<TestAutoProbeHook> _logger;

    public TestAutoProbeHook(TaskOrchestrator orchestrator, ILogger<TestAutoProbeHook> logger)
    {
        _orchestrator = orchestrator ?? throw new ArgumentNullException(nameof(orchestrator));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            var response = await _orchestrator.StartAsync(new StartRequest
            {
                ContractVersion = StartRequest.ContractVersionValue,
                RequestId = Guid.NewGuid(),
                RunKey = RunKey.Create(TaskKind.Probe,
                    Path.Combine(Path.GetTempPath(), "s5d-auto-probe"), null),
                TaskKind = TaskKind.Probe,
                Actor = "test",
                ProfileId = ClaudeCodeProviderAdapter.ProbeProfileId,
                Cwd = Path.GetTempPath(),
                InputRef = "auto-probe",
            }, cancellationToken).ConfigureAwait(false);
            _logger.LogInformation(
                "test.auto.probe.started runId={RunId} state={State} accepted={Accepted}",
                response.RunId, response.State, response.Accepted);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "test.auto.probe.start_failed");
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
