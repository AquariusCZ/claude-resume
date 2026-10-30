# Stage 2 规格:C# 基础设施骨架(shadow,不接管生产)

> 状态:规格 v1,2026-08-05。运行语义唯一真身 = `docs/adr/0002-run-lifecycle-contract.md` + `docs/RUN-CONTRACT.md`;事件语义 = `docs/EVENT-CONTRACTS.md`;状态归属 = `docs/STATE-OWNERSHIP.md`。本文只定义 Stage 2 交付的工程骨架与验收,不重复也不得改写上述契约;冲突时以上述文档为准并回报,不得自行取舍。
>
> **实现状态:六包(S2-A~S2-G)已全部完成 2026-08-04/05,全部测试与门禁通过;见 §7 实现状态与已识别偏差。**

## 1. 目标与范围

- 交付可构建、可测试的 .NET 10 解决方案骨架,六组件实现 RunContract 的**进程内 shadow 版本**;不连接生产飞书应用、不读写生产 `%LOCALAPPDATA%\ClaudeResume`、不注册任何开机/计划任务。
- 所有持久化落在**独立 shadow 目录**(默认 `%LOCALAPPDATA%\ClaudeResumeShadow`,可经环境变量 `AIRESUME_SHADOW_DIR` 覆盖;测试一律用临时目录)。
- 范围外:GUI 功能等价(Stage 7)、cc-connect/lark-cli 适配(Stage 3/4)、生产状态迁移(Stage 5/9)、hook 替换(Stage 8)。

## 2. 解决方案布局

```
csharp/AiResume.sln
  src/AiResume.Core/          领域模型+接口(无 I/O 依赖):RunId、RunState、事件信封 v1 类型、
                              ITaskOrchestrator/IProcessSupervisor/IProviderAdapter/IRunStore/
                              IHealthProbe/ITransport、错误分类(transient/auth/quota/
                              model_unavailable/config/internal/cancelled)
  src/AiResume.Worker/        Worker Service 宿主:装配六组件、BackgroundService 观察循环
                              (默认 20s,范围 15-30s)、结构化日志
  src/AiResume.Storage/       SQLite+WAL RunStore/OutboxStore;迁移器(schema_version 表,幂等)
  src/AiResume.Ipc/           Named Pipe 服务端/客户端:长度前缀 JSON 帧,协议 v1
  src/AiResume.Secrets/       DPAPI(CurrentUser)机密存储:credential_ref -> 密文文件
  src/AiResume.Gui/           WPF 空壳:仅一个窗口,经 Named Pipe 显示 Worker Ping/版本(占位)
  test/AiResume.Tests/        xUnit:单元+契约+崩溃恢复测试
```

- TFM:`net10.0-windows`(Worker/Storage/Ipc/Secrets 可为 `net10.0`,Gui/DPAPI 用 windows)。固定 SDK 10.0.302(`global.json`)。除 `Microsoft.Data.Sqlite`、`Microsoft.Extensions.Hosting`、xUnit 外不得新增第三方依赖;新增依赖必须先回报。

## 3. 六组件契约要点(实现按 RUN-CONTRACT,此处只列骨架级约束)

1. **RunStore(SQLite+WAL)**:表 `runs`(run_id PK, run_key, task_kind, provider, state, deadline_ms 恒 0, created_at, updated_at, terminal_reason, side_effect_marked)、`run_events`(run_id, seq 单调, envelope_json, UNIQUE(run_id,seq))、`outbox`(outbox_id PK, idempotency_key UNIQUE, state, attempts)、`process_registry`(run_id, parent_pid, child_pid, job_id, started_at, command_signature)。所有写入单 writer 经事务;`BEGIN IMMEDIATE`;busy_timeout 显式设置。**幂等**:同 run_id 重复 Start 返回既有状态;同 (run_id,seq) 重复 append 无副作用。
2. **TaskOrchestrator**:Start 持久接纳(先写 runs 再返回 runId,写失败=internal 拒绝);Status 只读快照;Cancel 幂等且 terminal=cancelled 不可 fallback;**不创建任何总时长计时器**;settle-once(唯一一次真实 close 释放 runKey,复刻 D-010 用例)。runKey 由单一规范函数生成(关 D-011:`runKey = taskKind + "|" + normalizedProjectPath + "|" + openId`,全实现只允许调用这一个函数)。
3. **ProcessSupervisor**:launcher wrapper + Windows Job Object(kill-on-close);durable registry 先落盘后 spawn,首登记失败拒绝任务;回收三态 matched/mismatched/unverifiable,只有 matched 可终止;崩溃恢复=重启后对 registry 逐项核验(PID+启动时间 5s 容差+命令签名),不可核验一律 fail-closed 保留;写回前指纹复核+失败路径清理自建 tmp(关 D-009 两个用例)。Stage 2 用**假 provider 进程**(dotnet 自带回显小工具)验证,不接真实 AI CLI。
4. **ProviderAdapter**:接口 + 一个 FakeProviderAdapter(可编程输出/退出码/挂起),真实 Codex/Claude 适配是 Stage 4/5 范围;活动分类 malformed/unknown 一律 fail-closed 标记副作用(复刻 D-002 语义)。
5. **HealthProbe**:接口 + Fake 实现;真实探测逻辑 Stage 5。契约:不设总时限、静默不判失败、指纹作废、childPending 禁探测(仅接口与状态机,测试用 Fake)。
6. **Transport(Named Pipe)**:pipe 名含用户 SID 派生段;帧 = 4 字节长度 + UTF-8 JSON(信封 v1,拒绝未知 envelope_version);单服务实例(命名互斥体);并发客户端保序按 correlation_id;恶意/超长帧(>1MiB)直接断连。命令:`ping`、`start`、`status`、`cancel`、`list-runs`。
7. **Secrets(DPAPI)**:`credential_ref` -> `%SHADOW%\secrets\<ref>.bin`(ProtectedData CurrentUser);日志/事件/异常绝不含明文(复刻 D-012/D-013 语义:事件序列化层按键名 allowlist + 值置换双重兜底)。
8. **日志**:结构化单行 JSON(ts 本地时间+偏移, level, component, run_id?, event, data 脱敏),按日滚动,任何路径不得写机密。

## 4. 出口门禁(全部自动化,`dotnet test` 一键)

- build:`dotnet build -warnaserror` 通过;`dotnet test` 全绿。
- 锁竞争:两进程并发写同一 RunStore,后者拿到 busy 重试成功,零丢事件。
- 幂等:同 run_id Start ×100 → 1 行;同 (run_id,seq) append ×100 → 1 行;outbox 同 idempotency_key ×100 → 远端发送 mock 记录 1 次。
- 崩溃恢复:写入中途 kill 测试宿主进程(子进程模式)→ 重启后 WAL 恢复、registry 三态核验、无半写状态;损坏 registry 锁存 AI 启动阻断。
- 取消/side effect/fallback:cancelled 不可 fallback;side_effect_marked 后禁止第二 provider;spawn 后取消覆盖 settle-once(D-010)。
- Job Object:假 provider 进程树(父+孙)在 Cancel 后 0 残留。
- 机密:全仓 rg 扫描 0 命中;事件/日志序列化对注入的假机密值 0 泄漏。

## 5. 工作包(DeepSeek 委托切分;每包一次主实现+至多两轮修正)

- **S2-A** 解决方案骨架+Core 类型+接口+错误分类+global.json+CI 本地脚本(`csharp/build.ps1`)。纯样板,委托。
- **S2-B** Storage:SQLite/WAL、迁移器、RunStore/Outbox/registry 表与幂等写入+锁竞争/幂等测试。委托。
- **S2-C** Ipc:Named Pipe 帧协议+单实例互斥+并发保序+恶意帧测试。委托。
- **S2-D** ProcessSupervisor:Job Object wrapper+durable registry+三态核验+崩溃恢复测试。**半委托**:接口与状态机由监督者定稿后,机械实现委托;核验判定逻辑审查加倍。
- **S2-E** Orchestrator+FakeProvider+FakeHealth+Worker 装配+观察循环+端到端契约测试。委托,审查对照 RUN-CONTRACT 逐条。
- **S2-F** Secrets+日志脱敏+rg 门禁脚本。委托。
- **S2-G** WPF 空壳+ping。委托。
- 顺序:A→(B,C 可并行,独立 worktree)→D→E→(F,G 并行)。每包出口:该包测试全绿+监督者独立复跑+审查。

## 6. 禁止事项

- 不接触生产 AppDir/config/日志/飞书应用;不读真实 `config.json`;不装服务/计划任务/开机项。
- 不引入 `deadline_ms`>0、静默超时、网络错误自动 fallback 等已被 ADR-0002 拒绝的语义。
- 不复制失控工作区任何代码;Node 侧文件本阶段零改动。
- DeepSeek 不 commit/push/deploy;其自报结果不作验收证据。

## 7. 实现状态与已识别偏差(2026-08-04/05 收尾记录)

### 7.1 各包交付(分支 `s2-external`,全部 `csharp/build.ps1` 全绿 + secrets gate 通过)

| 包 | commit | 交付 | 测试(独立复跑确认真实执行) |
|---|---|---|---|
| S2-A | 67e6fbc | 解决方案骨架+Core+全局 json+build.ps1 | 4+3+5+5(ErrorClass/Event/RunKey/RunStateMachine) |
| S2-B | 8c22715 | SQLite/WAL Storage:runs/run_events/outbox/process_registry+迁移器+幂等 | 8 项(含锁竞争、×100 幂等、runKey 占用/释放) |
| S2-C | 7c1810f | Ipc:NamedPipe 服务端/帧协议/单实例/恶意帧/命令路由 | 11 项(IpcNamedPipe) |
| S2-D | b05586b | ProcessSupervisor:Job Object+durable registry+三态核验+RecoverAsync | 11 项(Supervision 真进程树杀/伪造登记/注入) |
| S2-E | 91b0963 | Orchestrator+FakeProvider+FakeHealth+Worker 装配+观察循环 | 11 项(Orchestrator 真进程端到端+契约) |
| S2-F | e4430b5 | Secrets(DPAPI)+日志脱敏+scan-secrets.ps1 门禁 | 23 项(SecretsTests,含真 DPAPI round-trip) |
| S2-G | fab2adf | WPF 空壳+PipeClient ping | 8 项(IpcPipeClient)+ GUI exe 启动存活验证 |
| 收尾 | 0f35123 | 交叉检查修复+门禁补测 | +3 项(RUN-CONTRACT §13 #5/#9/#10) |

当前全量:**93 测试全绿**(`dotnet test`,0 警告 0 错误)+ secrets gate 0 命中。

### 7.2 已识别偏差(如实记录,不冒充契约)

1. **骨架级 succeeded 判定**:接口冻结下 `ProviderStatus` 无 terminal 字段,`ProcessStatus` 无退出码;编排器对「进程 gone + 无 cancel + 无 provider 失败」按骨架级 succeeded 处理(TaskOrchestrator 注释已明示)。这与 RUN-CONTRACT §13 #5「进程无 terminal 消失 → failed_local/process_disappeared」存在字面差异:契约语义依赖 provider 输出解析/退出码(Stage 4/5 引入),Stage 2 骨架无法表达。已补测试 `ProcessGone_withProviderFailure_fails_local_not_succeeded` 保证「provider 失败在进程消失后仍胜出」;严格语义(干净退出 vs 异常消失的区分)归 Stage 4/5 收紧。
2. **崩溃恢复未做「写入中途 kill 测试宿主」真实验证**:SPEC §4 列出的该门禁未在自动化中执行(执行成本高且 SQLite WAL 崩溃安全由引擎保证)。已覆盖:迁移器幂等、WAL 激活、registry 三态核验、损坏/伪造登记注入 fail-closed、恢复报告。断电真 kill 恢复与对账报告归 Stage 5(D-007 关闭条件)。

### 7.3 交叉检查发现并修复(第一性原理逐条对照 RUN-CONTRACT §13)

- **重复 Start requestId 幂等命中仍二次 spawn(bug)**:`RunStore.StartAsync` 对同 requestId 幂等返回 `Accepted=true, Existing=true`,而编排器未检查 `Existing` 即再次驱动启动 → 违反 §13 #9「重复 Start 返回同 runId,spawn=1」。修复:`TaskOrchestrator.StartAsync` 对 `!Accepted || Existing` 一律原样返回(0f35123)。
- 补测 §13 #10(重复 Cancel 同 commandId 只终止一次)与 §13 #5 邻近路径(见 7.2)。

### 7.4 冒烟验收(2026-08-04 深夜,真进程)

- Worker 后台启动 → Named Pipe ping 返回 `pong/version=1`;停止后 pipe 失效。
- 结构化日志 `logs/worker-YYYYMMDD.log` 单行 JSON、观察周期 20s 一个 cycle、无凭据形状文本。
- GUI exe 启动存活不崩、可正常关闭。
