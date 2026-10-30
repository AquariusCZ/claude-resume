# S7-D 续跑引擎驱动 + 布防交互

> 冻结于 2026-08-06。实现方按本文写代码;本文与代码冲突以本文为准,发现规格错误先改规格。

## 0. 上游盘点结论(为什么自研)

按全局「上游优先」规则,动手前的三层盘点:

| 层 | 结论 |
|---|---|
| **本仓库** | `CheckerCycle`(状态机,13 测试)、`ProductConfigStore`、`ProductStateStore`、`ClaudeCodeProbe`、`ProcessSupervisor`(登记+Job Object+恢复)**全部已存在且有测试**。缺的**只是把它们串起来的驱动者**——`CheckerCycle` 在生产代码中零引用。 |
| **已依赖上游** | cc-connect **不做限额续跑**:ADR-0003 实证 4 已确认它只读取 `LimitReached` 不消费。这是本产品唯一不可替代的核心,上游没有。 |
| **生态** | 无通用方案(依赖 Claude CLI 的私有限额语义与本机项目布局)。 |

**因此本包不新写任何状态机、登记表或探测器,只写驱动者与运行器。** 新写重复实现即违规。

## 1. 目标

让「布防 → 定时探测 → 观测到限流 → 额度恢复 → 按队列逐项目续跑 → 完成/连续」这条链路在 C# Worker 中真实运行,并由 GUI 控制布防。

**顺序红线**:引擎(§2/§3)必须先于 GUI 布防按钮(§4)可用。**绝不允许出现"按钮写了状态但没人消费"的假布防。**

## 2. `ClaudeResumeRunner`(任务 A)

文件:`csharp/src/AiResume.Worker/Resume/ClaudeResumeRunner.cs`,命名空间 `AiResume.Worker.Resume`。

语义移植自现役 `src/lib.ps1` 的 `Invoke-ClaudeResume`。

### 2.1 结果类型(同文件内定义)

```csharp
public sealed record ResumeRunResult
{
    public string ProjectPath { get; init; } = string.Empty;
    public string Status { get; init; } = "error";
    public int? ExitCode { get; init; }
    public bool Limited { get; init; }
    public bool ResultOk { get; init; }
    public long OutputBytes { get; init; }
}
```

`Status` 取值(与现役一致):`success` / `limited` / `stopped` / `no-claude` / `prompt-multiline` / `launch-error` / `registry-error` / `exit-<N>` / `exit-null`。

### 2.2 API

```csharp
public sealed class ClaudeResumeRunner
{
    public ClaudeResumeRunner(IProcessSupervisor supervisor, string? claudeCommand = null);
    public async Task<ResumeRunResult> RunAsync(ProjectRef project, ProductConfig config, CancellationToken cancellationToken);
}
```

### 2.3 行为

1. **前置校验**(任一不过立即返回,不 spawn):
   - `claudeCommand` 为绝对路径且文件不存在 → `no-claude`;
   - 项目目录不存在 → `no-claude`;
   - `config.ResumePrompt` 含 `\r` 或 `\n` → **`prompt-multiline`**。
     *为什么单列一类*:`cmd /c` 会在首个换行处截断 `-p` 参数(见 `docs/LESSONS.md`),
     静默截断等于跑了一个和用户意图不同的提示词,必须显式失败而不是将就。
2. **命令**(经 `cmd.exe` 重定向到临时文件;`ProcessSupervisor` 不做输出重定向):
   ```
   /c ""<claude>" --continue -p "<prompt>" --output-format stream-json --verbose[ --model "<model>"][ --dangerously-skip-permissions] > "<out>" 2> "<err>""
   ```
   - `--model` 仅在 `config.ResumeModel` 非空白时追加;
   - `--dangerously-skip-permissions` 仅在 `config.SkipPermissions` 为 true 时追加;
   - 引号规则照抄 `ClaudeCodeProbe.BuildArguments`:整条命令首尾引号包裹,内层各自带引号,重定向符在引号外。
3. **启动**:构造 `ProcessStartRequest`:
   - `RunId` = `RunId.New()`(若无该工厂则 `new RunId(Guid.NewGuid())`,以实际类型为准);
   - `FileName = "cmd.exe"`,`Arguments` 同上,`WorkingDirectory = project.Path`;
   - **`Environment` 必须含 `["AI_RESUME_INTERNAL_RUN"] = "1"`** —— 红线:AI Resume 自己启动的进程必须打此标记,否则 Claude Code 的 Stop hook 会被 `AiResume.Hook` 当成用户任务完成,每次续跑伪造一条通知;
   - `CommandSignature = "cmd.exe"`。
   调用 `supervisor.StartAsync`;`Started == false` → 按 `ErrorCode` 归类:含 `registry` → `registry-error`,其余 → `launch-error`。
4. **等待与增量读取**:每 500ms 轮询 `supervisor.StatusAsync(runId, ct)`:
   - 每轮从上次偏移增量读取 out/err(用 `FileShare.ReadWrite` 打开,claude 仍在写);
   - `Liveness == Gone` → 退出循环;
   - `Liveness == Unknown` → **继续等待,不得判失败**(fail-closed:监控异常不等于任务失败);
   - `cancellationToken` 取消 → `supervisor.CancelAsync`,`Status = stopped`,退出循环;
   - **不设任务总时限**(RunContract:续跑无客户端总时限)。
5. **权威重扫**:进程结束后 300ms,读取 out+err **全文**再判一次。
   *为什么*:流式输出会把一行拆成多个 chunk 落盘,增量读取的逐行匹配会漏判(现役已踩)。
   重扫只做两项结构化判定:
   - 任一行同时匹配 `"type"\s*:\s*"result"` 与 `"is_error"\s*:\s*false` → `ResultOk = true`;
   - 任一行匹配 `"status"\s*:\s*"(blocked|rejected|limited|exceeded)"` → `Limited = true`。
6. **判定顺序**(status 尚未被置为 `stopped` 时):
   1. `ResultOk == true` → `success`;
   2. 否则 `Limited == true` → `limited`;
   3. 否则 `exit-<ExitCode>`(取不到退出码写 `exit-null`)。
   > **`ResultOk` 必须压过 `Limited`**:一次成功的运行可能在正文里*谈论*限流;而真被限流的运行永远不会以 `is_error:false` 收尾。顺序反了会把成功误判成限流,进而错误地回到等待。
   > 退出码只作兜底:`ProcessStatus` 不提供退出码时按 `exit-null` 处理,不得据此判成功。
7. **清理**:`finally` 中删除 out/err 临时文件,删除失败忽略。`OutputBytes` = 两文件长度之和(删除前取)。
8. 全流程**不得抛异常**给调用方(取消除外):任何意外异常归 `launch-error`。

## 3. `ResumeEngine`(任务 B)

文件:`csharp/src/AiResume.Worker/Resume/ResumeEngine.cs`,命名空间 `AiResume.Worker.Resume`。
实现 `Microsoft.Extensions.Hosting.BackgroundService`。

### 3.1 构造

```csharp
public ResumeEngine(
    ProductConfigStore configStore,
    ProductStateStore stateStore,
    CheckerCycle cycle,
    ClaudeCodeProbe probe,
    ClaudeResumeRunner runner,
    ILogger<ResumeEngine> logger,
    TimeSpan? tickInterval = null)   // 默认 30 秒
```

### 3.2 主循环(`ExecuteAsync`)

每 `tickInterval` 一拍,每拍:

1. `config = configStore.Load()`;若 `!config.Enabled || !config.Armed || string.IsNullOrEmpty(config.ArmCycleId)` → 本拍空转(不写任何状态,不打日志刷屏);
2. `state = stateStore.Load()`;
3. `if (!cycle.Initialize(config, state)) continue;`
4. `if (!cycle.ShouldProbe(config, state)) continue;`
5. `if (!cycle.MarkProbeAttempt(config, state)) continue;`(周期失效)
6. `probe = await probe.ProbeAsync(config.ProbeModel, ShadowPaths.Root, ct)` —— 探测工作目录固定 shadow 根,不落进用户项目;
7. 分派:
   - `probe.IsLimited` → `cycle.OnLimited(config, state, probe)`;
   - `probe.Ready` → `decision = cycle.OnReady(config, state, probe)`;`decision == StartResuming` → `await RunResumeRoundAsync(config, state, ct)`;
   - 否则 → `cycle.OnNotReady(config, state, probe)`。
8. 任何一拍内的异常都必须 catch 并记日志后继续下一拍——**引擎绝不因单拍异常退出**;`OperationCanceledException` 且 `ct` 已取消时正常退出。

### 3.3 续跑一轮(`RunResumeRoundAsync`)

按 `config.Selected` 的**原有顺序**逐个:

1. 每个项目开始前 **重新 `configStore.Load()`**,并 `cycle.TestCycleActive(freshConfig, state.CycleId)`;不活跃 → 立即 return(用户中途解除布防必须当拍生效);
2. `ct.IsCancellationRequested` → return;
3. `result = await runner.RunAsync(project, freshConfig, ct)`;
4. `outcome = cycle.ApplyProjectResult(freshConfig, state, project.Path, result.Status)`;
5. 分支:`Continue` / `MarkedError` → 下一个;`BackToWaiting` / `CycleSuperseded` → **立即 return**(不继续后续项目)。
6. 全部项目走完后:`kind = cycle.Complete(freshConfig, state)`:
   - `Disarmed` → **写回配置解除布防**:`config.Armed = false; config.ArmCycleId = string.Empty; configStore.Save(config)`;
   - `Continuous` → 保持布防(`Complete` 已把 state 收尾);
   - `Superseded` → 不写配置。

### 3.4 日志

每个决策点一行结构化日志(`resume.tick.*` / `resume.probe.*` / `resume.project.*` / `resume.cycle.*`),
含 `cycleId`、`phase`、`project`、`status`。**不得记录任何凭据或 claude 输出正文**。

## 4. GUI 布防交互(监督者实现,不委托)

`ControlPlaneBridge` 加 `arm.get` / `arm.set`;前端项目行加多选、按钮接真实动作。本节不在委托范围内。

## 5. 测试要求

xUnit,`net10.0-windows`,命名空间 `AiResume.Tests`。

### 5.1 `ClaudeResumeRunnerTests`(任务 C)

用**假 supervisor**(实现 `IProcessSupervisor`)+ 真临时目录。假 supervisor 在 `StartAsync` 时按注入的脚本把预置文本写进重定向目标文件,并让随后的 `StatusAsync` 返回 `Gone`。

必须覆盖:

1. 输出含 `{"type":"result","is_error":false}` → `success`;
2. 输出含 `"status":"blocked"` 但**也**含 `is_error:false` → **`success`**(顺序红线:ResultOk 压过 Limited);
3. 只含 `"status":"limited"` → `limited`;
4. 两者都无 → `exit-<N>` 或 `exit-null` 形状(以 `StartsWith("exit-")` 断言);
5. `config.ResumePrompt` 含 `\n` → `prompt-multiline`,且 **`StartAsync` 从未被调用**(用假 supervisor 的调用计数断言);
6. 项目目录不存在 → `no-claude`,且 `StartAsync` 未被调用;
7. `StartAsync` 返回 `Started=false` 且 `ErrorCode` 含 `registry` → `registry-error`;其它 ErrorCode → `launch-error`;
8. **`AI_RESUME_INTERNAL_RUN=1` 必须出现在 `ProcessStartRequest.Environment` 中**(假 supervisor 记录收到的 request 后断言);
9. `config.SkipPermissions=false` 时参数**不含** `--dangerously-skip-permissions`;为 true 时含;
10. `config.ResumeModel` 为空白时参数不含 `--model`;非空时含;
11. `Liveness` 先返回一次 `Unknown` 再返回 `Gone` → 仍能正常完成(不得把 Unknown 判成失败);
12. 取消令牌在运行中触发 → `stopped` 且 `CancelAsync` 被调用。

### 5.2 `ResumeEngineTests`(任务 D)

用假 probe 与假 runner(经接口或可注入委托;若 `ClaudeCodeProbe`/`ClaudeResumeRunner` 是密封类,**在任务 B 中为它们各提取一个最小接口** `IClaudeUsageProbe` / `IClaudeResumeRunner` 并让引擎依赖接口)。手动驱动单拍(把 `ExecuteAsync` 的单拍逻辑提取成 `internal async Task TickAsync(CancellationToken)` 以便测试直接调用)。

必须覆盖:

1. 未布防(`Armed=false`)→ 不探测(假 probe 调用计数为 0);
2. 布防但 `ShouldProbe` 为 false(刚探测过)→ 不探测;
3. 探测 `limited` → state `SawLimited=true`、`Phase=waiting`,且**不触发续跑**(假 runner 计数 0);
4. 未观测到限流时探测 `ready` → 保持布防、**不续跑**(现役"布防先于限流"语义);
5. 观测到限流后再探测 `ready` → 触发续跑,假 runner 按 `Selected` 顺序被调用;
6. 某项目返回 `limited` → 该轮**中止**,后续项目不被调用;
7. 一轮全部成功且 `Continuous=false` → 配置被写回 `Armed=false` 且 `ArmCycleId` 清空;
8. 一轮全部成功且 `Continuous=true` → 仍保持 `Armed=true`;
9. 续跑途中配置被改为解除布防 → 剩余项目不再执行;
10. 假 probe 抛异常 → 单拍吞掉异常并记日志,引擎不退出(再驱动一拍仍正常工作)。

**隐含约束(必读)**:
- `CheckerCycle` 用注入时钟,测试必须注入固定/可推进的时钟,**禁止依赖真实时间**;
- `ShouldProbe` 在 `LastProbeUtc == null` 时**必定**返回 true,构造"不该探测"的用例必须先把 `LastProbeUtc` 设成刚刚;
- `CheckerCycle.Initialize` 在 `state.CycleId == config.ArmCycleId` 时返回 true 且**不重置** state,构造"新周期"用例要给不同的 `ArmCycleId`;
- 两个存储的构造参数**不同**:`ProductConfigStore(string shadowRoot)` 收目录,
  `ProductStateStore(string databasePath)` 收**数据库文件路径**。测试各传临时路径,**不得触碰真实 shadow 根**。
- `RunId` 是 `readonly record struct RunId(Guid Value)`,新建用 `RunId.New()`。

## 6. 红线

- **不新写状态机/登记表/探测器**;续跑进程必须经 `ProcessSupervisor`,不得裸 `Process.Start`。
- 续跑与探测进程必须带 `AI_RESUME_INTERNAL_RUN=1`。
- 续跑不设客户端总时限;`Unknown` 存活状态不得判失败。
- 测试禁止启动真实 `claude`、禁止触碰真实 shadow 根与用户目录。
- 凭据实值零进代码/日志/测试。
- 注释用中文,风格对齐仓库现有 C# 文件。
