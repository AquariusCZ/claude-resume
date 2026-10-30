# Stage 5 规格:产品状态迁移(C# shadow,不接管生产)

> **⚠️ 部分作废 2026-08-06(ADR-0003)**:**S5-B 真实 Claude 限额探测(`ClaudeCodeProbe`,17 测试)作废**——cc-connect 1.4.1 原生已具备该能力(`agent/claudecode/claude_usage.go` + `core.UsageReporter` + `/usage`),改为消费上游 `UsageReport`。S5-A/C/D 不受影响。当时的验收结论在其历史语境下仍成立,此处只记录后续方向变更。
>
> 状态:规格 v1,2026-08-05 冻结(用户确认按计划推进后冻结);S5-A/S5-B/S5-C/S5-D 均完成 2026-08-05 提交(见 §7.1-§7.4),Stage 5 完成。
> 依据:`docs/UPSTREAM-ARCHITECTURE-RESEARCH.md`(2026-08-01 计划,阶段 5:产品状态迁移)+ `docs/adr/0002-run-lifecycle-contract.md` + `docs/RUN-CONTRACT.md` + `docs/STATE-OWNERSHIP.md`。冲突时以上述文档为准并回报,不得自行取舍。

## 1. 目标与范围

- **目标**:把现役产品状态能力迁至 C# shadow 实现——项目发现、provider 健康(Claude 限额探测)、布防周期状态机、RunStore/进程登记对账;验证 15-30 秒观察、静默不失败、断电恢复、PID 复用、CIM 失败 fail-closed 全部成立;**旧系统(PowerShell/Node)仍是唯一生产 writer,本阶段零生产写入**。
- **不迁移(范围外)**:不替换现役 `checker.ps1`/`provider-health.js` 生产消费;不双写任何生产状态;不安装服务/计划任务/开机项;不触碰生产 AppDir;正式任务编排(chat/query/modify)与 cc-connect 会话迁移属 Stage 6。
- **产出**:① C# shadow 产品状态组件(项目发现/健康探测/布防周期/对账);② 断电真 kill 恢复验证(关闭 S2 遗留门禁);③ 对账报告(JSON,复验 D-007/D-009/D-011);④ 阶段报告与文档同步。

## 2. 外部前提(任一未满足不得开工对应工作包)

1. **Stage 2 骨架全绿**:102 测试通过,六组件契约冻结(已满足,2026-08-05 复核)。
2. **shadow 目录独立**:`AIRESUME_SHADOW_DIR` 覆盖(默认 `%LOCALAPPDATA%\ClaudeResumeShadow`),全部新状态落 shadow 目录,测试一律用临时目录。
3. **本机 Claude Code CLI**:真探测冒烟(可选)复用本机登录态,与现役 `Test-ClaudeReady` 相同;不读生产 config.json、不写生产状态。

## 3. 契约要点(与现役语义对齐,不得改写)

- **项目发现**:customProjects 显式名单 + projectHome 动态发现 + hiddenProjects 过滤;发现不依赖当前项目数量;3 秒缓存 + 配置指纹。
- **Claude 限额探测**:`claude -p ready --model <probeModel> --max-turns 1 --output-format stream-json` 真实最小调用;`rate_limit_event` 携带服务端精确 `resetsAt`(unix 秒)与 `utilization`;只在 blocked 或利用率越过 ~0.75 时出现;ready/reason 五态(fail-closed:探测异常不误判可用)。
- **布防周期**:cycleId = `armCycleId`(布防时生成 GUID,解除/重布防变化);state 带 cycleId,周期变化即失效;phase=idle/waiting/resuming/done;sawLimited/limitedRefires(≥6 判误分类死循环防护);探测节奏:可用 → probeIntervalMinutes(默认 15,≥2 校验),限流 → 4 分钟;`realFiveHourResetUtc`/`realSevenDayResetUtc` 只取服务端精确值,禁止本地估算。
- **无总时限**:探测与观察不设客户端总时限;静默不触发失败;15-30 秒观察周期(默认 20 秒)沿用 ObservationWorker。
- **对账**:runs(非 terminal + childPending)↔ process_registry ↔ 真实进程三方一致;RunKey 一律由规范函数生成(D-011 复验);registry 写回前指纹复核、失败路径清理(D-009 语义在 C# 由 SQLite 事务原子写替代)。

## 4. 工作包(每包 ≤4 生产文件/4 测试文件/800 行净变更)

### S5-A 项目发现与 shadow 产品配置

- `ProductConfig`(Core 纯模型):enabled/armed/armCycleId/continuous/selected/customProjects/hiddenProjects/projectHome/probeIntervalMinutes/probeModel/resumeModel/resumePrompt/skipPermissions/dirtyGuard;JSON 往返(System.Text.Json)。
- `ProductConfigStore`(Worker):shadow `config.json` 读写;OpenOrCreate 独占锁 + 临时文件 + 原子替换;读失败容错(默认值)。
- `ProjectCatalog`(Worker):custom + 动态发现 + hidden 过滤 + 指纹缓存(3 秒)。
- 测试:发现/自定义/隐藏/空 projectHome/目录不可读/缓存指纹失效。

### S5-B 真实 Claude 限额探测(ProviderAdapter)

- `ClaudeProbeResult`(Core 契约):ready/reason/fiveHourResetUtc/sevenDayResetUtc/fiveHourUtil/sevenDayUtil/outputBytes。
- `ClaudeCodeProbe`(Worker):临时文件重定向启动 `claude -p ready`;解析 `rate_limit_event`(扁平 JSON 段)/`result`;错误分类(no-claude/spawn 失败/timeout/网络类→failed_local;服务端结构化→failed_provider);输出脱敏后丢弃,不落日志。
- `ClaudeCodeProviderAdapter`(Worker,IProviderAdapter 实现):probe taskKind 专用路径(显式拒绝非 probe),为 Stage 6 正式任务预留 Start/Status/Cancel 骨架。
- 测试:假 claude 脚本模拟 blocked/可用/无 rate_limit/崩溃/超时/乱码输出,验证五态解析与分类;不真跑 AI。

### S5-C 布防周期状态机(shadow)

- `CheckerState`(Core 契约):phase/cycleId/sawLimited/lastProbeUtc/limitedRefires/realFiveHourResetUtc/realSevenDayResetUtc/realResetProbedUtc/realFiveHourUtil/projectStatus。
- `ProductStateStore`(Storage):SQLite 表 `product_state`(单行,事务写,迁移器幂等)。
- `CheckerCycle`(Worker,纯状态机,注入时钟/存储/探测委托):ShouldProbe(cadence)/OnLimited/OnReady/OnNotReady/Initialize/Complete(disarm|continuous|superseded)/TestCycleActive;限流节奏 4 分钟;refire ≥6 防护;周期变化即停。
- 测试:状态机全路径/周期隔离/节奏/refire 防护/完成语义/持久化 round-trip。

### S5-D 断电恢复 + 对账报告 + 门禁

- `Reconciler` + `ReconcileReport`(Worker):三方对账(非 terminal run ↔ registry ↔ 进程 liveness);runKey 规范形复验;registry 完整性;输出结构化 JSON 报告。
- Worker 测试钩子(Program.cs 最小改动):环境变量 `AIRESUME_TEST_AUTO_PROBE=1` 时宿主启动后自动 Start 一个 fake probe run 并打日志。
- 测试 `PowerLossRecoveryTests`:真实子进程宿主 + shadow 临时目录 + fake run 运行中 `Process.Kill(true)` → 重启宿主 → 验证 WAL 可读、run 状态恢复、registry 核验处置、无半写。
- 测试补强:PID 复用(伪造登记指向异时/异签名进程→Mismatched 不终止)、CIM 失败注入(Probe Unknown→Unverifiable 保留 fail-closed);复核 SupervisionInjectionTests 既有覆盖,缺口补齐。

## 5. 出口门禁(阶段总门禁,`csharp/build.ps1` 一键 + 手工复核)

- `dotnet build -warnaserror` + `dotnet test` 全绿(现有 102 + 新增,预计 130±15);build.ps1 独立复跑确认。
- 断电真 kill 恢复验证通过(PowerLossRecoveryTests 全绿,WAL 无损坏,registry 处置符合 RUN-CONTRACT §9)。
- 对账报告输出:runs/registry/进程三方一致;D-007(断电恢复+对账)、D-009(registry 对账)、D-011(RunKey 规范复验)关闭条件逐条核对。
- 全仓凭据 0 命中(rg 实值 + 形状);shadow 资产全部在仓库外/临时目录;生产 AppDir 零读写(测试断言不触碰)。
- 旧系统仍是唯一生产 writer:生产 node PID(28228)未动、checker 计划任务未动、生产日志无新写入。
- 文档同步:`docs/ARCHITECTURE.md`(Stage 5 状态)、`AI_GUIDE.md`(首行 project-tour 时间标记)、`docs/MIGRATION-BASELINE.md`(门禁/基线更新)、`docs/MIGRATION-DEBT.md`(D-001/D-007/D-009/D-011 关闭或进展)、`docs/STAGE-5-SPEC.md` §7 实现报告。
- 阶段报告:已跑测试清单与结果、对账报告摘要、文档同步情况、剩余风险。

## 6. 禁止事项(违反 = 阶段整体拒收)

- 凭据实值进仓库/日志/测试输出/commit;不读生产 AppDir `config.json` 或任何密钥;不写生产任何状态文件。
- 不安装服务/计划任务/开机项;不自动 `--yes` 绕过高风险确认;不真跑修改类 AI 任务。
- 不新增第三方依赖(超出 Stage 2 固定集);不复制失控工作区代码;Node/PowerShell 生产文件本阶段零改动。
- 需改冻结接口/新增依赖/工具链异常/基线不绿 → 立即停止报告,不得自行绕过。

## 7. 报告格式(每包完成后提交,照 S4 格式)

```
包:S5-X
commit:<hash>
build.ps1 输出末 6 行:<粘贴>
新增/修改文件:<清单>
设计决策与偏离:<无,或逐条说明>
自测未覆盖的风险:<诚实列出>
```

### 7.1 S5-A 项目发现与 shadow 产品配置(完成,2026-08-05 提交)

```
包:S5-A
commit:`feat: S5-A 项目发现与 shadow 产品配置`(见 git log,单 commit)
build.ps1 输出末 6 行:
==> dotnet test
已通过! - 失败:     0,通过:   117,已跳过:     0,总计:   117,持续时.. 1 s - AiResume.Tests.dll (net10.0)
==> secrets gate (S2-F)
==> scan-secrets: rg not found, using PowerShell fallback
==> OK: secrets gate passed (0 credential-shaped hits)
==> OK: build, tests and secrets gate passed
新增/修改文件:
- csharp/src/AiResume.Core/ProductConfig.cs(新建,Core 纯模型 + JSON 选项,见规格 §4 S5-A)
- csharp/src/AiResume.Worker/Products/ProductConfigStore.cs(新建,shadow config.json 读写)
- csharp/src/AiResume.Worker/Products/ProjectCatalog.cs(新建,项目发现)
- csharp/test/AiResume.Tests/ProductCatalogTests.cs(新建,15 测试)
- docs/STAGE-5-SPEC.md(本段报告与状态行)
设计决策与偏离:无规格偏离。实现细节三处对齐现役语义:① custom 追加时动态已发现同路径保留动态条目(custom name 不覆盖),与 feishu-runtime.js discoverProjects 一致;② 写锁获取 3 次重试 + 20ms 间隔,与 Get-CcuConfig 锁尝试语义一致;③ 测试经构造参数注入 temp/AppDir 边界,避免系统 temp 语义干扰断言。
自测未覆盖的风险:未对真实 ~/.claude/projects 做冒烟(只读夹具验证发现语义);跨进程锁竞争未测(Stage 9 对账演练补);真实配置指纹轮换由 S5-D 门禁抽查。
```

### 7.2 S5-B 真实 Claude 限额探测(完成,2026-08-05 提交)

```
包:S5-B
commit:`feat: S5-B 真实 Claude 限额探测(ClaudeProbeResult/ClaudeCodeProbe/Adapter + 17 测试)`(见 git log,单 commit)
build.ps1 输出末 6 行:
==> dotnet test
已通过! - 失败:     0,通过:   134,已跳过:     0,总计:   134,持续时.. 2 s - AiResume.Tests.dll (net10.0)
==> secrets gate (S2-F)
==> scan-secrets: rg not found, using PowerShell fallback
==> OK: secrets gate passed (0 credential-shaped hits)
==> OK: build, tests and secrets gate passed
新增/修改文件:
- csharp/src/AiResume.Core/ClaudeProbeResult.cs(新建,探测结果契约,见规格 §4 S5-B)
- csharp/src/AiResume.Worker/Probes/ClaudeCodeProbe.cs(新建,claude -p ready 真实最小调用 + 五态解析)
- csharp/src/AiResume.Worker/Probes/ClaudeCodeProviderAdapter.cs(新建,IProviderAdapter probe 专用路径)
- csharp/test/AiResume.Tests/ClaudeProbeTests.cs(新建,17 测试:假 claude 脚本,不真跑 AI)
- docs/STAGE-5-SPEC.md(本段报告与状态行)
设计决策与偏离:
- ProviderStartRequest 无 TaskKind 字段(冻结接口不改),probe 任务以 ProfileId="probe" 标记判别(与 S5-D 钩子/S5-C CheckerCycle 约定),其余 profile 显式拒绝(ErrorCode=probe_only_adapter)。
- probe 的 StartAsync 不阻塞调用方:后台执行探测,StatusAsync 轮询;探测进行中返回静默指标(观察循环不得判失败)。探测与 Orchestrator 进程段解耦:probe 不经 Orchestrator 驱动时(CheckerCycle 直接用 ClaudeCodeProbe)无过早 succeeded 风险;经 Orchestrator 驱动时探测需在观察周期前完成(S5-D 假脚本验证),真实慢探测的编排接入属 Stage 6。
- 错误分类:服务端结构化(limited/billing/auth/model_unavailable)→ Quota/Auth/ModelUnavailable → failed_provider;本地类(no-claude/spawn-failed/timeout/transient/exit-*/unknown)→ Internal → failed_local(与 RUN-CONTRACT 一致)。
自测未覆盖的风险:未对真实 claude 命令冒烟(本机登录态未动用,与现役 Test-ClaudeReady 相同的命令形状);cmd 引号/重定向细节以假脚本验证;timeout 杀进程树在真实长进程上未验证(假脚本 ping 已覆盖)。
```

### 7.3 S5-C 布防周期状态机(完成,2026-08-05 提交)

```
包:S5-C
commit:`feat: S5-C 布防周期状态机(CheckerState/ProductStateStore/CheckerCycle + 23 测试)`(见 git log,单 commit)
build.ps1 输出末 6 行:
==> dotnet test
已通过! - 失败:     0,通过:   157,已跳过:     0,总计:   157,持续时.. 2 s - AiResume.Tests.dll (net10.0)
==> secrets gate (S2-F)
==> scan-secrets: rg not found, using PowerShell fallback
==> OK: secrets gate passed (0 credential-shaped hits)
==> OK: build, tests and secrets gate passed
新增/修改文件:
- csharp/src/AiResume.Core/CheckerState.cs(新建,布防周期状态契约,见规格 §4 S5-C)
- csharp/src/AiResume.Storage/ProductStateStore.cs(新建,SQLite product_state 单行事务写)
- csharp/src/AiResume.Storage/StorageDatabase.cs(修改,v3 迁移 product_state 表,CurrentSchemaVersion=3)
- csharp/src/AiResume.Worker/Products/CheckerCycle.cs(新建,纯状态机:注入时钟/存储)
- csharp/test/AiResume.Tests/CheckerCycleTests.cs(新建,23 测试)
- csharp/test/AiResume.Tests/StorageRunStoreTests.cs(修改,schema_version 断言改引 CurrentSchemaVersion)
- docs/STAGE-5-SPEC.md(本段报告与状态行)
设计决策与偏离:
- shadow 阶段零生产写入:Complete 只判定完成语义(Disarmed/Continuous/Superseded),解除布防的 config 写由现役 checker 完成;状态周期校验(TestCycleActive)前置到每次操作入口,周期失效不修改内存状态、不落盘(fail-closed)。
- realReset 字段只覆盖服务端下发值(低利用率探测不清零好值),与现役 Save-RealResetFromProbe 一致。
- Microsoft.Data.Sqlite 连接池默认开启,测试清理需先 ClearAllPools 再删目录(仅测试代码处理,存储组件行为未变)。
自测未覆盖的风险:与现役 config/状态文件的真实互操作未测(Stage 9 对账演练补);checker 每 2 分钟 tick 的宿主驱动循环(S5-D 钩子覆盖单次驱动);限流 4 分钟节奏与 refire 计数在跨进程多实例并发下未测。
```

### 7.4 S5-D 断电恢复 + 对账报告 + 门禁(完成,2026-08-05 提交)

```
包:S5-D
commit:`feat: S5-D 断电恢复+对账报告(Reconciler/ProcessVerifier/钩子 + PowerLossRecoveryTests)`(见 git log,单 commit)
build.ps1 输出末 6 行:
==> dotnet test
已通过! - 失败:     0,通过:   177,已跳过:     0,总计:   177,持续时间: 21 s - AiResume.Tests.dll (net10.0)
==> secrets gate (S2-F)
==> scan-secrets: rg not found, using PowerShell fallback
==> OK: secrets gate passed (0 credential-shaped hits)
==> OK: build, tests and secrets gate passed
新增/修改文件:
- csharp/src/AiResume.Worker/Supervision/Reconciler.cs(新建,三方对账:非 terminal run ↔ process_registry ↔ 进程 liveness;runKey 规范形复验;结构化 JSON 报告)
- csharp/src/AiResume.Worker/Supervision/ProcessVerifier.cs(新建,提取的公共核验函数:启动时间 ±5s 容差 + exe 签名 SHA256 → Matched/Mismatched/Gone/Unverifiable 四态)
- csharp/src/AiResume.Worker/Supervision/ProcessSupervisor.cs(修改,核验逻辑改为复用 ProcessVerifier,行为不变)
- csharp/src/AiResume.Worker/Program.cs(修改,AIRESUME_TEST_AUTO_PROBE=1 测试钩子:自动 Start fake probe run 并打结构化日志;生产不设该变量,默认关闭)
- csharp/test/AiResume.Tests/ReconcilerTests.cs(新建,18 测试:含 PID 复用 Mismatched 不终止、CIM 注入 Unverifiable fail-closed、非法 runKey 计入 RunKeyInvalidCount)
- csharp/test/AiResume.Tests/PowerLossRecoveryTests.cs(新建,2 测试:真实子进程宿主 + shadow 临时目录,run 运行中整树硬杀 → WAL 无损坏/无半写/状态恢复/对账 Gone 处置/重启观察循环驱动至 terminal)
- docs/STAGE-5-SPEC.md(本段报告与状态行)、docs/MIGRATION-DEBT.md(D-001/D-007/D-009/D-011 关闭)、docs/ARCHITECTURE.md、AI_GUIDE.md 首行、docs/MIGRATION-BASELINE.md
设计决策与偏离:
- 钩子 ProfileId="probe" 与 S5-B/S5-C 约定一致;Start 失败仅记日志不阻止宿主启动(钩子不影响生产路径)。
- 宿主进程回收用类 Dispose 的 PID 清单 + GetProcessById 重取句柄:测试方法内 using Process 会提前 Dispose 句柄,再 Kill 抛 ObjectDisposedException 被吞 → 宿主残留继承 testhost stdout 句柄使 vstest 等管道 EOF 永久挂起(调试期实测踩坑,已固化为测试代码注释)。
- 宿主日志按日滚动文件名是 worker-yyyyMMdd.log(扩展名 .log);日志 marker 断言按 *.log 扫描。
- seq 语义:runs.seq 初始 0,每次状态推进 +1(与 state_version 同步),queued→starting→running 后 seq=2/state_version=3,断电断言阈值按此。
- 对账器只读不改状态;Gone 登记的清理授权归 ProcessSupervisor.RecoverAsync(RUN-CONTRACT §9:只有明确 Gone 才清登记)。
自测未覆盖的风险:断电模拟是进程树硬杀而非真断电(文件系统级差异由 SQLite WAL 保证吸收,未做磁盘级断电演练);重启宿主后由观察循环驱动至 terminal 用 15 秒间隔假脚本验证,真实长任务跨断电恢复属 Stage 6+;多宿主并发对账未测(NamedPipe 单实例互斥拒绝第二宿主,已在 S2-G 覆盖)。
```

## 8. 验证方将做什么(知悉即可)

逐包:diff 审查(对照规格与红线)、独立复跑 build.ps1、抽查对账报告与断电恢复日志形状、确认生产零接触与凭据零泄漏。
