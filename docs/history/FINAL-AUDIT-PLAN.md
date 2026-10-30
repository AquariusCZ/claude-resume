# AI Resume 迁移全面审计计划 v1

> 面向:独立审计 AI(下称"审计者")。你与实施 AI 必须是不同主体,且不得阅读实施 AI 的对话历史——你的全部信息来源是本仓库、本计划和你自己执行的命令。
> 你的产出是一份审计报告;报告将由最终验证方交叉检查。**没有证据的结论视为未审计**;交叉检查发现报告与事实不符,整份报告作废。
> 你是审计者,不是修复者:只发现、只取证、只报告,不修复任何问题。

## 0. 审计纪律(硬性)

1. **只读**:不得修改工作树、不得 commit/push/revert、不得改任何配置。允许的写入仅限:运行构建/测试产生的 bin/obj/临时目录;以及 §3C 变异抽查所需的**一次性 git worktree**(用完必须 `git worktree remove --force` 删净)。
2. **锚定**:开工第一步记录——审计分支名、HEAD 完整 hash、`git status --short` 全文、`dotnet --version`、`node --version`、当前日期时间。报告中所有证据都隶属该快照;审计期间若发现 HEAD 变化,立即中止并报告。
3. **不采信自报**:仓库文档中所有"已完成/已验证/全绿"的说法(包括 `docs/STAGE-2-SPEC.md` §7 这类实现状态记录、commit message、`MIGRATION-DEBT.md` 状态列)一律视为**待核验主张**,不是事实。
4. **生产禁区**:`C:\Users\<you>\Desktop\claude-resume`(只读取证仓)与 `%LOCALAPPDATA%\ClaudeResume`(生产运行目录)只允许执行本计划 §3J 明确列出的只读取证命令;禁止任何其他访问,禁止读取 `config.json` 内容(只允许算哈希)。
5. **机密纪律**:审计过程中看到任何疑似真实机密,记录"位置+形状"(如 `文件:行 出现 32 位随机串,键名 xxx`),**绝不把值本身写进报告**。

## 1. 必读材料(按序,约 1 小时)

1. `CLAUDE.md` — 项目规则与红线总纲。
2. `docs/adr/0001-target-architecture.md`、`docs/adr/0002-run-lifecycle-contract.md` — 目标架构与运行契约的决策依据。
3. `docs/RUN-CONTRACT.md` — 运行语义唯一真身(§13 验收清单是 §3D 的对照表)。
4. `docs/EVENT-CONTRACTS.md`、`docs/STATE-OWNERSHIP.md` — 事件与状态归属契约。
5. `docs/STAGE-2-SPEC.md`、`docs/STAGE-2-EXTERNAL-PLAN.md` 及实施期间新增的 Stage 3/4/5 规格文档 — 实施方接到的任务书与其自报状态。
6. `docs/MIGRATION-DEBT.md`、`docs/MIGRATION-BASELINE.md`、`docs/RECOVERY-AUDIT-20260801.md` — 债务台账、基线事实、生产取证基线。

## 2. 审计范围判定(第一个审计动作)

用 `git log --oneline migration-recovery-20260801..s2-external`(及实际分支拓扑)列出实施方全部提交,按提交信息归类到 Stage/工作包,形成"**声称完成清单**"。本计划各节按该清单裁剪:声称做了的 → 全量审计;声称没做的 → 抽查确认真的没做(防止未记录的越界改动)。范围外发现的任何改动(Node `src/`、`test/`、根文档被实施方修改)单独成 finding。

## 3. 审计模块

### 3A. 仓库与提交卫生

- **范围红线**:逐 commit `git show --stat`,核对每个提交触碰的文件是否在其工作单允许范围内;越界改动逐条列出(文件、提交、是否有正当理由记录)。
- **产物污染**:`git ls-files csharp | grep -E "(bin|obj)/"` 必须为空;`git status` 中的未提交文件逐个判断性质(进行中的工作/垃圾/危险)。
- **机密扫描(工作树+新增历史)**:
  ```
  rg -i "sk-[a-z0-9]{16,}|app_secret|apiKey.*['\"][A-Za-z0-9]{20,}|BEGIN.*PRIVATE KEY" --glob '!node_modules'
  git log -p migration-recovery-20260801..HEAD | rg -i "(同上模式)"
  ```
  测试中的假机密必须显著是假的(如 fake-/test- 前缀);真实形状的 32+ 位随机串即使在测试里也算 P1。
- **提交信息真实性抽样**:随机抽 5 个 commit,核对 message 描述与实际 diff 是否一致(声称"加了 X 测试"就数得出来)。

### 3B. Node 现役基线未破坏(生产链路回归)

实施方的任何工作都不得破坏现役 Node 生产链路。在仓库根依次执行并记录结果:

```
node test/ai-providers.js && node test/stage1-recorded-equivalence.js && node test/channel-adapter.js
node test/task-orchestrator.js && node test/agent-adapter.js && node test/session-manager.js
node test/conversation-store.js && node test/routing.js && node test/card-flow.js && node test/session-pick.js
node test/authorization-policy.js && node test/menu-authorization.js && node test/concurrency.js
node test/image-send.js && node test/progress-image.js && node test/completion-events.js
node test/completion-hooks.js && node test/icon-asset.js && node test/config-isolation.js && node test/config-lock.js
powershell -NoProfile -ExecutionPolicy Bypass -File test/install-deploy.ps1
```

全部必须通过。任何失败 = P0(生产链路被实施改动破坏或测试基础设施被动过)。同时 `git diff migration-recovery-20260801..HEAD -- src/ test/` 应当为空或每一处改动都有明确授权记录。

### 3C. C# 构建与测试真实性

- **净构建**:`dotnet build-server shutdown` → 删除全部 bin/obj → `powershell -File csharp/build.ps1`。记录完整输出:必须 0 警告 0 错误;实测测试总数与文档声称数量对比,不符即 finding。
- **稳定性**:全量测试连续跑 3 遍,任何偶发失败记录 finding(附输出)。
- **测试质量抽样(反"刷绿")**:随机抽 ≥12 个测试方法(覆盖每个测试类至少 1 个),逐个读代码回答:断言是否真的检验了名字声称的行为?有没有只跑不断言、`Assert.True(true)`、断言被 try/catch 吞掉、`[Fact(Skip=...)]`?另外全局扫描:`rg "Skip\s*=" csharp/test` 与 `rg -A2 "catch" csharp/test | rg -B2 "^\s*}"`(空 catch)。
- **变异抽查(在一次性 worktree 中做,用完删净)**:逐个施加以下变异,每次变异后跑测试,**必须至少一个测试失败**;全绿即该处测试无效(P1):
  1. `RunStore.StartAsync` 删掉 runKey busy 检查;
  2. `RunStore.StartAsync` 删掉 requestId 幂等分支;
  3. ProcessSupervisor 三态核验中把 mismatched 也当 matched 处理;
  4. 日志/事件脱敏器删掉 `[REDACTED]` 置换;
  5. Ipc 帧长上限从 1 MiB 改为 1 GiB;
  6. TaskOrchestrator 删掉对 `Existing` 的检查(回归 0f35123 修的 bug);
  7. Cancel 的 terminal 状态直接允许 fallback(如有相关代码路径)。
- **测试隔离**:抽查测试是否把数据库/管道/文件放在临时目录;跑完测试后检查 `%TEMP%` 与仓库内有无残留进程/文件(`Get-Process dotnet` 数量回到基线)。

### 3D. 契约符合性(逐条对照 RUN-CONTRACT §13 + ADR-0002)

- **无总时限**:`rg -i "timeout|deadline|Task.Delay|CancelAfter" csharp/src` 逐个命中分类:传输级单请求超时(允许,RUN-CONTRACT §11)/观察指标(允许)/**运行生命周期计时(P0)**。确认 `EventEnvelopeV1.DeadlineMs` 恒 0、数据库 `deadline_ms` 有 CHECK=0 约束。
- **状态机**:`RunStateMachine` 迁移表与 RUN-CONTRACT §4 完全一致;`rg "state\s*=\s*'(succeeded|failed|cancelled)"` 找出所有直接改状态的 SQL/代码路径,确认全部经过状态机校验。
- **幂等四件套**:重复 Start(requestId)、重复事件(run_id,seq)、outbox(idempotency_key)、重复 Cancel(commandId)——各自读实现+确认有对应测试+确认测试断言"重复 N 次只有 1 次副作用"。
- **settle-once 与 runKey 释放**:读编排器 close 路径,确认 runKey 只在真实 close/gone 核验后释放,childPending 期间禁止释放/禁止新探测。
- **fallback 红线**:cancelled 不可 fallback;side_effect_marked 后禁止第二 provider;只有 provider_explicit_once 允许一次。逐条找到实现与测试。
- **偏差登记核真**:文档记录的每一条"已识别偏差"(如 STAGE-2-SPEC §7.2):(a) 代码里确如描述;(b) 承诺的守护测试存在且有效;(c) 有归属的后续 Stage。再反向扫一遍:规格逐条要求 vs 实现,找**未登记**的偏差(这比已登记的更重要)。

### 3E. 安全审计

- 运行仓库自带 secrets 门禁脚本(如 `scan-secrets.ps1`)+ 自己的独立模式扫描(§3A)。
- **DPAPI**:读密文文件原始字节确认无明文;ref 白名单(`[a-z0-9-]`)拒绝路径穿越;异常信息不含明文。
- **脱敏器**:构造嵌套对象/循环引用/Error 对象/已知机密值四类输入验证全部 `[REDACTED]`(可在 worktree 中加临时探针测试,用完删)。
- **Named Pipe**:恶意帧五类(超长/零长/负长/非 JSON/未知版本)后服务端存活;检查 `NamedPipeServerStream` 是否设置了 ACL(PipeSecurity 限制当前用户)——未设置则同机其他用户可连,记 P2 并要求登记债务。
- **ProcessSupervisor**:只有 matched 可终止;unverifiable/损坏登记 fail-closed;伪造登记(错启动时间/错签名)注入测试存在且有效。
- **非 owner 隔离不回归**:确认实施改动没有触碰 Node 侧查询/闲聊工具隔离(§3B 的 `git diff` 已覆盖;此处复核 `authorization-policy`/`menu-authorization` 测试通过)。

### 3F. Stage 3 审计(lark-cli 封装,如声称完成)

- 封装保真:用户/bot 身份、scope、risk level、exit 10 高风险确认、结构化错误、脱敏——逐项在封装代码中找到对应处理;**不得发现自动 `--yes`**、不得发现 `event consume` 被当生产入口。
- 试点范围:所有调用目标必须是测试应用/测试资源;`rg "cli_xxxxxxxxxxxxxxxx"`(生产 app id)在 C#/试点配置中必须零命中。
- 声称的场景验证:要求证据(命令输出留档、日志文件);无证据的"验证完成"降级为"未验证主张"并记 finding。

### 3G. Stage 4 审计(cc-connect 试点,如声称完成)——本节从严

- **单消费者铁律**:定位 cc-connect 的安装与全部配置文件;确认其配置的飞书凭据只能是测试应用(`cli_xxxxxxxxxxxxxxxx`);`rg` 生产 app id 零命中;检查当前进程列表——cc-connect 若在运行,确认其连接目标;生产应用在整个试点期间必须只有 Node agent 一个消费者(生产日志无双回复/重复消费痕迹)。
- **Management API 安全**:确认试点配置禁用/未暴露 management/bridge(上游已知无认证);若启用过,查绑定地址与 token,记 finding。
- **试点声称的功能验证(停止/进度/图片/崩溃恢复)**:逐项要求证据链——测试项目的会话记录、cc-connect 日志、时间戳可对上;无证据 = "未验证主张"(P1,因为 Stage 6/10 的前提依赖这些结论)。
- **测试项目隔离**:试点只能对测试项目跑;检查测试项目 git log 有无 AI 写入,确认没有真实工作项目被试点触碰。

### 3H. Stage 5 审计(产品状态 shadow,如声称完成/进行中)——零生产写是生死线

- **零生产写**:`rg "ClaudeResume(?!Shadow)" csharp/src --pcre2` 逐个命中分类;任何对生产 AppDir 的**写**路径 = P0;读路径必须是明确设计的只读迁移预演且有登记。
- **shadow 目录纪律**:所有持久化落 `AIRESUME_SHADOW_DIR`/`ClaudeResumeShadow`;测试用临时目录。
- **探测边界**:Claude/额度探测代码必须带 `AI_RESUME_INTERNAL_RUN=1` 标记、必须是最小请求、不得有无界重试循环(读重试逻辑,确认有退避与上限或由用户动作触发)。
- **单写者**:shadow 组件不得写 Node 侧任何状态文件(`config.json`/`state.json`/`checker-ai-child.json`/`feishu-*.json`);`rg` 这些文件名于 csharp/ 下,写路径零命中。

### 3I. 文档与台账真实性

- `MIGRATION-DEBT.md` 每条状态(尤其"closed/mitigation")抽证:关闭条件里的证据是否真的存在(测试名、commit、行号)。
- 实施方对权威文档(SPEC/ARCHITECTURE/AI_GUIDE/DEBT)的每次修改:内容是否与实证一致;有无"把目标写成现役"的冒充(CLAUDE.md 明令禁止)。
- `AI_GUIDE.md` 的 project-tour 时间标记是否随大改刷新。

### 3J. 生产环境现场核验(只读,仅限以下命令)

```
# 唯一生产 agent 与运行时长
Get-CimInstance Win32_Process -Filter "Name='node.exe'" | ? { $_.CommandLine -match 'ClaudeResume\\feishu-agent\.js' } | Select ProcessId,CreationDate
# cc-connect 进程存在性与命令行(判断连接目标)
Get-CimInstance Win32_Process | ? { $_.CommandLine -match 'cc-connect' } | Select ProcessId,CommandLine
# 生产 config 只取哈希(绝不读内容)
(Get-FileHash "$env:LOCALAPPDATA\ClaudeResume\config.json" -Algorithm SHA256).Hash
# 生产日志尾部只查健康标记与异常(不外传内容)
Get-Content "$env:LOCALAPPDATA\ClaudeResume\logs\feishu-stdout.log" -Tail 40 | Select-String "READY|error|双|重复"
# 取证仓完好
git -C C:\Users\<you>\Desktop\claude-resume status --short ; git -C C:\Users\<you>\Desktop\claude-resume stash list
```

通过标准:恰 1 个生产 agent 且启动时间连续无异常重启风暴;取证仓干净、stash 完好、HEAD `6826704`;审计期间 config 哈希不变。

## 4. 报告格式(硬性)

```
# 审计报告
锚定: branch / HEAD / 日期 / dotnet / node 版本 / 审计耗时
声称完成清单: (§2 的产出)
未审计项与原因: (诚实列出,含无法执行的检查)

## 分节结论
每节: PASS / PASS-with-findings / FAIL
每个 PASS 必须附 ≥1 条本人执行的验证证据(命令+输出摘录);无证据的 PASS 视为未审计。

## Findings
| ID | 严重度 | 节 | 一句话结论 | 证据(文件:行 或 命令+输出) | 复现步骤 | 建议 |
严重度: P0=阻断(生产风险/契约违背/测试造假) P1=必修 P2=应修 P3=观察

## 变异抽查记录
每个变异: 位置 / 预期失败测试 / 实际结果

## 机器可读摘要(报告末尾,JSON)
{ "head": "...", "sections": {"3A":"pass", ...}, "findings": [{"id":"F-01","severity":"P1","file":"...","line":123,"claim":"..."}], "testCount": N, "mutations": {"total":7,"caught":N} }
```

## 5. 交叉检查协议(最终验证方执行,审计者知悉即可)

1. 锚定一致性:同 HEAD 复跑净构建+全量测试,计数与报告比对。
2. 证据抽样:随机复核 ≥30% findings 的证据 + 每节至少 1 条 PASS 证据。
3. 逆信任抽查:对报告评价**最干净**的两个节做独立对抗检查(含 ≥2 个报告未用过的新变异)。
4. 生产核验独立重做 §3J。
5. 判定:抽样零出入 → 采纳报告;任一证据不实 → 报告作废退回;确认的 P0/P1 → 退实施方修复后增量复审。

## 6. 审计者禁止事项

- 不修复、不美化、不"顺手改进";发现即报告。
- 不与实施 AI 沟通、不读其对话历史。
- 不把任何机密值、生产配置内容写进报告。
- 不因文档写着"已验证"而跳过验证;也不因赶时间降低抽样量——抽样量不足时如实写进"未审计项"。
