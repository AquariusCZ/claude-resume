using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AiResume.Core;
using AiResume.Storage;
using AiResume.Worker.Probes;
using AiResume.Worker.Products;
using AiResume.Worker.Resume;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AiResume.Tests;

/// <summary>
/// ResumeEngine 单拍驱动测试。用假探测器/假运行器 + 固定可推进时钟,
/// 手动驱动 RunOnceAsync 验证 §5.2 的 10 条行为。不启动真实进程、不触碰真实 shadow 根。
/// </summary>
[Collection(SqliteCollection.Name)]
public sealed class ResumeEngineTests : IDisposable
{
    private readonly string _shadowRoot;
    private readonly string _stateDbPath;
    private readonly ProductConfigStore _configStore;
    private readonly ProductStateStore _stateStore;
    private readonly FakeProbe _probe;
    private readonly FakeRunner _runner;
    private readonly CheckerCycle _cycle;
    private readonly ResumeEngine _engine;
    private readonly DateTimeOffset _now;
    private DateTimeOffset _clock;

    public ResumeEngineTests()
    {
        // 每个测试独立临时目录,避免相互污染。
        _shadowRoot = Path.Combine(Path.GetTempPath(), "airesume-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_shadowRoot);
        // ProductStateStore 落 SQLite 而非 JSON,且**必须先迁移建表**——
        // 不建表时 Save 会抛 no such table: product_state(隐含前置条件)。
        _stateDbPath = Path.Combine(_shadowRoot, "shadow.db");
        StorageDatabase.Migrate(_stateDbPath);

        _configStore = new ProductConfigStore(_shadowRoot);
        _stateStore = new ProductStateStore(_stateDbPath);

        _now = new DateTimeOffset(2026, 8, 6, 12, 0, 0, TimeSpan.Zero);
        _clock = _now;
        _cycle = new CheckerCycle(_stateStore, () => _clock);

        _probe = new FakeProbe();
        _runner = new FakeRunner();
        _engine = new ResumeEngine(
            _configStore,
            _stateStore,
            _cycle,
            _probe,
            _runner,
            NullLogger<ResumeEngine>.Instance,
            TimeSpan.FromSeconds(30));
    }

    public void Dispose()
    {
        _engine.Dispose();
        try
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (Directory.Exists(_shadowRoot))
            {
                Directory.Delete(_shadowRoot, recursive: true);
            }
        }
        catch
        {
            // 清理失败忽略,不影响测试结果。
        }
    }

    /// <summary>构造已布防配置:Enabled=true、Armed=true、ArmCycleId 非空。</summary>
    private static ProductConfig ArmedConfig(string cycleId = "cycle-1", bool continuous = false)
    {
        var config = ProductConfig.CreateDefault();
        config.Enabled = true;
        config.Armed = true;
        config.ArmCycleId = cycleId;
        config.Continuous = continuous;
        config.Selected = new List<ProjectRef>
        {
            new() { Name = "proj-a", Path = @"C:\proj\a" },
            new() { Name = "proj-b", Path = @"C:\proj\b" },
            new() { Name = "proj-c", Path = @"C:\proj\c" },
        };
        return config;
    }

    /// <summary>把 state 的 LastProbeUtc 设为"刚刚",使 ShouldProbe 返回 false。</summary>
    private void SetLastProbeJustNow(CheckerState state)
    {
        state.LastProbeUtc = _clock;
        _stateStore.Save(state);
    }

    /// <summary>推进时钟使 ShouldProbe 必然返回 true(超过 15 分钟间隔)。</summary>
    private void AdvancePastProbeInterval()
    {
        _clock = _clock.AddMinutes(16);
    }

    [Fact]
    public async Task 未布防_不探测()
    {
        var config = ArmedConfig();
        config.Armed = false;
        _configStore.Save(config);
        _stateStore.Save(CheckerState.CreateDefault());

        await _engine.RunOnceAsync(CancellationToken.None);

        Assert.Equal(0, _probe.CallCount);
        Assert.Equal(0, _runner.CallCount);
    }

    [Fact]
    public async Task 布防但刚探测过_不探测()
    {
        var config = ArmedConfig();
        _configStore.Save(config);
        var state = CheckerState.CreateDefault();
        state.CycleId = config.ArmCycleId;
        SetLastProbeJustNow(state);

        await _engine.RunOnceAsync(CancellationToken.None);

        Assert.Equal(0, _probe.CallCount);
        Assert.Equal(0, _runner.CallCount);
    }

    [Fact]
    public async Task 探测limited_进入等待且不续跑()
    {
        var config = ArmedConfig();
        _configStore.Save(config);
        var state = CheckerState.CreateDefault();
        state.CycleId = config.ArmCycleId;
        _stateStore.Save(state);

        _probe.Results.Enqueue(new ClaudeProbeResult { Ready = false, Reason = "limited" });

        await _engine.RunOnceAsync(CancellationToken.None);

        var saved = _stateStore.Load();
        Assert.True(saved.SawLimited);
        Assert.Equal(CheckerState.PhaseWaiting, saved.Phase);
        Assert.Equal(0, _runner.CallCount);
    }

    [Fact]
    public async Task 未观测到限流时探测ready_保持布防不续跑()
    {
        var config = ArmedConfig();
        _configStore.Save(config);
        var state = CheckerState.CreateDefault();
        state.CycleId = config.ArmCycleId;
        _stateStore.Save(state);

        _probe.Results.Enqueue(new ClaudeProbeResult { Ready = true, Reason = "ok" });

        await _engine.RunOnceAsync(CancellationToken.None);

        var saved = _stateStore.Load();
        Assert.False(saved.SawLimited);
        Assert.Equal(CheckerState.PhaseWaiting, saved.Phase);
        Assert.Equal(0, _runner.CallCount);
        // 布防保持。
        var freshConfig = _configStore.Load();
        Assert.True(freshConfig.Armed);
        Assert.Equal(config.ArmCycleId, freshConfig.ArmCycleId);
    }

    [Fact]
    public async Task 观测到限流后再探测ready_按顺序触发续跑()
    {
        var config = ArmedConfig();
        _configStore.Save(config);
        var state = CheckerState.CreateDefault();
        state.CycleId = config.ArmCycleId;
        state.SawLimited = true;
        state.Phase = CheckerState.PhaseWaiting;
        _stateStore.Save(state);

        _probe.Results.Enqueue(new ClaudeProbeResult { Ready = true, Reason = "ok" });
        _runner.Results["success"] = new ResumeRunResult { Status = "success" };

        await _engine.RunOnceAsync(CancellationToken.None);

        Assert.Equal(3, _runner.CallCount);
        Assert.Equal(
            config.Selected.Select(p => p.Path).ToList(),
            _runner.CalledPaths);
    }

    [Fact]
    public async Task 某项目返回limited_本轮中止后续不执行()
    {
        var config = ArmedConfig();
        _configStore.Save(config);
        var state = CheckerState.CreateDefault();
        state.CycleId = config.ArmCycleId;
        state.SawLimited = true;
        state.Phase = CheckerState.PhaseWaiting;
        _stateStore.Save(state);

        _probe.Results.Enqueue(new ClaudeProbeResult { Ready = true, Reason = "ok" });
        _runner.Results["success"] = new ResumeRunResult { Status = "success" };
        _runner.Results["limited"] = new ResumeRunResult { Status = "limited" };
        // 第二个项目返回 limited。
        _runner.ResultByPath[config.Selected[1].Path] = "limited";

        await _engine.RunOnceAsync(CancellationToken.None);

        Assert.Equal(2, _runner.CallCount);
        Assert.Equal(
            new[] { config.Selected[0].Path, config.Selected[1].Path },
            _runner.CalledPaths);
    }

    [Fact]
    public async Task 一轮全部成功且非连续_解除布防清空周期()
    {
        var config = ArmedConfig(continuous: false);
        _configStore.Save(config);
        var state = CheckerState.CreateDefault();
        state.CycleId = config.ArmCycleId;
        state.SawLimited = true;
        state.Phase = CheckerState.PhaseWaiting;
        _stateStore.Save(state);

        _probe.Results.Enqueue(new ClaudeProbeResult { Ready = true, Reason = "ok" });
        _runner.Results["success"] = new ResumeRunResult { Status = "success" };

        await _engine.RunOnceAsync(CancellationToken.None);

        var freshConfig = _configStore.Load();
        Assert.False(freshConfig.Armed);
        Assert.Equal(string.Empty, freshConfig.ArmCycleId);
    }

    [Fact]
    public async Task 一轮全部成功且连续_保持布防()
    {
        var config = ArmedConfig(continuous: true);
        _configStore.Save(config);
        var state = CheckerState.CreateDefault();
        state.CycleId = config.ArmCycleId;
        state.SawLimited = true;
        state.Phase = CheckerState.PhaseWaiting;
        _stateStore.Save(state);

        _probe.Results.Enqueue(new ClaudeProbeResult { Ready = true, Reason = "ok" });
        _runner.Results["success"] = new ResumeRunResult { Status = "success" };

        await _engine.RunOnceAsync(CancellationToken.None);

        var freshConfig = _configStore.Load();
        Assert.True(freshConfig.Armed);
        Assert.Equal(config.ArmCycleId, freshConfig.ArmCycleId);
    }

    [Fact]
    public async Task 续跑途中解除布防_剩余项目不再执行()
    {
        var config = ArmedConfig();
        _configStore.Save(config);
        var state = CheckerState.CreateDefault();
        state.CycleId = config.ArmCycleId;
        state.SawLimited = true;
        state.Phase = CheckerState.PhaseWaiting;
        _stateStore.Save(state);

        _probe.Results.Enqueue(new ClaudeProbeResult { Ready = true, Reason = "ok" });
        _runner.Results["success"] = new ResumeRunResult { Status = "success" };

        // 第一个项目**执行期间**把配置改为解除布防。
        // 注意 BeforeRun 在记账之前触发,所以第一次调用时 CallCount 仍为 0;
        // 若写成 == 1,解除会发生在第二个项目已进入 RunAsync 之后,引擎届时已无从拦截。
        _runner.BeforeRun = () =>
        {
            if (_runner.CallCount == 0)
            {
                var fresh = _configStore.Load();
                fresh.Armed = false;
                fresh.ArmCycleId = string.Empty;
                _configStore.Save(fresh);
            }
        };

        await _engine.RunOnceAsync(CancellationToken.None);

        Assert.Equal(1, _runner.CallCount);
        Assert.Equal(new[] { config.Selected[0].Path }, _runner.CalledPaths);
    }

    [Fact]
    public async Task 假probe抛异常_单拍吞掉引擎不退出()
    {
        var config = ArmedConfig();
        _configStore.Save(config);
        var state = CheckerState.CreateDefault();
        state.CycleId = config.ArmCycleId;
        // 必须先观测到过限流,第二拍的 ready 才会触发续跑("布防先于限流"语义)。
        state.SawLimited = true;
        state.Phase = CheckerState.PhaseWaiting;
        _stateStore.Save(state);

        _probe.ThrowOnNext = true;

        // 第一拍:探测抛异常,必须被吞掉且不改变任何周期状态。
        await _engine.RunOnceAsync(CancellationToken.None);
        Assert.Equal(1, _probe.CallCount);
        Assert.Equal(0, _runner.CallCount);

        // 第二拍:MarkProbeAttempt 已把 LastProbeUtc 设成第一拍时刻,
        // 不推进时钟的话 ShouldProbe 会直接返回 false,第二拍将空转。
        _clock = _clock.AddMinutes(30);
        _probe.Results.Enqueue(new ClaudeProbeResult { Ready = true, Reason = "ok" });
        _runner.Results["success"] = new ResumeRunResult { Status = "success" };
        await _engine.RunOnceAsync(CancellationToken.None);

        // 引擎在异常后仍然正常工作:又探测一次,并按队列跑完三个项目。
        Assert.Equal(2, _probe.CallCount);
        Assert.Equal(3, _runner.CallCount);
    }

    /// <summary>假探测器:按注入序列返回结果,记录调用次数,可配置抛异常。</summary>
    private sealed class FakeProbe : IClaudeUsageProbe
    {
        public Queue<ClaudeProbeResult> Results { get; } = new();
        public int CallCount { get; private set; }
        public bool ThrowOnNext { get; set; }

        public Task<ClaudeProbeResult> ProbeAsync(string model, string workingDirectory, CancellationToken cancellationToken)
        {
            CallCount++;
            if (ThrowOnNext)
            {
                ThrowOnNext = false;
                throw new InvalidOperationException("模拟探测异常");
            }

            var result = Results.Count > 0 ? Results.Dequeue() : new ClaudeProbeResult { Ready = false, Reason = "unknown" };
            return Task.FromResult(result);
        }
    }

    /// <summary>假运行器:记录调用顺序,按项目路径或状态返回注入结果。</summary>
    private sealed class FakeRunner : IClaudeResumeRunner
    {
        public List<string> CalledPaths { get; } = new();
        public int CallCount => CalledPaths.Count;
        public Dictionary<string, ResumeRunResult> Results { get; } = new();
        public Dictionary<string, string> ResultByPath { get; } = new();
        public Action? BeforeRun { get; set; }

        public Task<ResumeRunResult> RunAsync(ProjectRef project, ProductConfig config, CancellationToken cancellationToken)
        {
            BeforeRun?.Invoke();
            CalledPaths.Add(project.Path);

            string status;
            if (ResultByPath.TryGetValue(project.Path, out var pathStatus))
            {
                status = pathStatus;
            }
            else
            {
                status = "success";
            }

            if (Results.TryGetValue(status, out var result))
            {
                return Task.FromResult(result);
            }

            return Task.FromResult(new ResumeRunResult { Status = status });
        }
    }
}