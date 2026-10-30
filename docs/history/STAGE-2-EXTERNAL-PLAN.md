# Stage 2 外部实施工作单 v1(S2-C ~ S2-G)

> 面向:受用户委托执行实现工作的 AI(下称"实施者")。
> 你的产出将由独立验证方逐包验收;自报"完成"不作为验收依据,一切以可复跑的构建、测试和 diff 为准。
> 规格唯一真身:`docs/STAGE-2-SPEC.md`(必读)+ `docs/adr/0002-run-lifecycle-contract.md` + `docs/RUN-CONTRACT.md`(运行语义)+ `docs/EVENT-CONTRACTS.md`(事件语义)。本工作单与上述文档冲突时,以上述文档为准并在报告中指出冲突,不得自行取舍。

## 0. 开工前必读与自检

1. 通读本文件、`docs/STAGE-2-SPEC.md` 全文、`docs/RUN-CONTRACT.md` 第 2-4 节。
2. 确认基线:在仓库根执行 `powershell -NoProfile -ExecutionPolicy Bypass -File csharp\build.ps1`,当前必须全绿(build -warnaserror 0 警告 + 全部测试通过)。基线不绿就停下报告,不要动手。
3. 工作分支:从 `migration-recovery-20260801` 创建并切换到 `s2-external` 分支;全部工作在该分支进行。

## 1. 全局红线(违反任何一条 = 该包整体拒收)

- 只允许改动 `csharp/` 目录内文件;`src/`(Node)、`test/`(Node)、`docs/`、根目录文件一律只读。
- `AiResume.Core` 的既有接口与类型**已冻结**:不得改签名、不得删成员。确需变更时停下,在报告中给出理由与建议,等待决定。
- 不新增任何 NuGet 包(现有:Microsoft.Extensions.Hosting、Microsoft.Data.Sqlite、SQLitePCLRaw.bundle_e_sqlite3、xunit 系)。需要新依赖时停下报告。
- 任何 API/字段不得引入总时限语义(deadline/timeout/静默阈值);`deadline_ms` 只能恒 0。取消只能来自显式 Cancel。
- 绝不触碰:`C:\Users\<you>\Desktop\claude-resume`(只读取证仓)、`%LOCALAPPDATA%\ClaudeResume`(生产运行目录)、生产 `config.json`、任何飞书应用凭据。
- 机密(密钥/token/secret 实值)不得出现在代码、测试数据、日志输出或提交信息中;测试用的假机密必须显著是假的(如 `fake-secret-for-test`)。
- 不 push、不改远端、不装系统服务/计划任务/开机项。
- 每包一个 commit(中文说明,首行 `feat: S2-X <简述>(外部实施)`),测试全绿后才允许提交;不得把多个包混进一个 commit。

## 2. 环境已知雷区(前人真实踩过,直接采用结论)

- 若解决方案级 `dotnet restore/build` 出现管道类失败或诡异挂起:先 `dotnet build-server shutdown`,再用 `dotnet build csharp/AiResume.sln -m:1` 单节点构建。**禁止**因此自建 NuGet 离线源、写包下载脚本或改全局 MSBuild 配置。
- 构建后可能残留 dotnet 子进程锁住 `obj/`;清理用 `Get-Process dotnet | Stop-Process -Force`(taskkill 对这批进程会报 Unspecified error)。
- `bin/`、`obj/` 已被 `csharp/.gitignore` 排除,不得提交构建产物。
- 测试一律把数据库/管道/文件放进 `Path.GetTempPath()` 下的独立子目录并在 Dispose 清理;SQLite 清理前先 `SqliteConnection.ClearAllPools()`。
- 单元测试禁止依赖真实网络、真实飞书、真实 AI CLI。

## 3. 工作包(严格按顺序;完成一包→提交→报告→等验收结论,再进下一包)

### S2-C Named Pipe 传输(Ipc)

- 允许范围:`csharp/src/AiResume.Ipc/**`、`csharp/test/AiResume.Tests/Ipc*.cs`。
- 实现(对应规格 §3.6):
  - pipe 名:`airesume-<当前用户SID的SHA256前16位>`;服务端用命名互斥体保证单实例,第二实例构造时抛出含明确文案的异常。
  - 帧格式:4 字节小端长度前缀 + UTF-8 JSON;单帧上限 1 MiB;长度非法(0、负、超限)或 JSON 解析失败 → 立即断开该客户端连接,服务端本体必须存活并继续接受新连接。
  - 信封校验:顶层 `envelopeVersion` 必须为 `"1"`,未知版本拒绝(回错误帧后断开)。
  - 命令路由:`ping`(回 pong+版本)、`start`/`status`/`cancel`(转发到注入的 `ITaskOrchestrator`)、`list-runs`(转发到注入的查询委托);未知命令回结构化错误。
  - 并发:多客户端并发处理;同一连接内按接收顺序逐帧应答;应答携带请求的 `correlationId`。
- 测试(全部离线):ping round-trip;恶意帧五连(超长/零长/负长/非 JSON/未知版本)后服务端仍能服务新客户端;单实例互斥;两个并发客户端各自 correlation 应答不串;`start` 转发参数正确到 fake orchestrator。
- 完成标准:`csharp\build.ps1` 全绿;新增测试 ≥6 项。

### S2-D ProcessSupervisor(进程监督)

- 允许范围:`csharp/src/AiResume.Worker/Supervision/**`(新建目录)、`csharp/test/AiResume.Tests/Supervision*.cs`;实现 `IProcessSupervisor` 接口。
- 实现(对应规格 §3.3;这是全项目安全关键包,写注释说明每个决策):
  - launcher:启动子进程前创建 Windows Job Object(`JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE`),子进程及其后代全部入 Job;P/Invoke 自己封装,不引第三方包。
  - durable registry:复用 S2-B 的 `process_registry` 表;**先落盘登记(事务提交成功)才允许 spawn**,首次登记失败必须放弃启动并返回 internal 错误。
  - 登记字段:run_id、parent_pid(当前进程)、child_pid、job_id(GUID)、started_at(进程真实启动时间)、command_signature(可执行路径+参数的 SHA256)。
  - 回收核验三态:matched(PID 存在且启动时间在 ±5 秒容差内且命令签名一致)/ mismatched(PID 存在但特征不符)/ unverifiable(查询失败)。**只有 matched 允许终止**;mismatched 只删登记;unverifiable 保留登记不动作。
  - 终止:优先关闭 Job 句柄(kill-on-close),再核验进程确实退出;未确认退出前返回 childPending=true,不得删登记。
  - 崩溃恢复:提供 `RecoverAsync()`——重启后遍历登记表逐项三态核验并按上述规则处置,产出结构化恢复报告(每项:run_id、verdict、action)。
- 测试:用无害的长命令(如 `cmd /c ping -n 30 127.0.0.1 > NUL`)真实验证——spawn 后登记存在;终止后进程树 0 残留(含孙进程,可用 `cmd /c start /b` 造孙);伪造登记(错误启动时间/签名)时拒绝终止;unverifiable(不存在的 PID 但先写入合法结构)保留登记;RecoverAsync 对 gone 进程清理登记。
- 完成标准:`csharp\build.ps1` 全绿;新增测试 ≥6 项;测试自身不留任何残留进程(finally 里兜底清理)。

### S2-E Orchestrator 装配与端到端契约

- 允许范围:`csharp/src/AiResume.Worker/**`(除 Supervision 已有文件外可新建)、`csharp/src/AiResume.Core/` **仅允许新增文件**(如 FakeProvider 所需的新纯类型,不改既有)、`csharp/test/AiResume.Tests/Orchestrat*.cs`。
- 实现(对应规格 §3.2/3.4/3.5):
  - `TaskOrchestrator : ITaskOrchestrator`:组合 RunStore(S2-B)+ ProcessSupervisor(S2-D)+ IProviderAdapter。Start=RunStore 持久接纳→queued;推进 queued→starting→running 由编排器驱动 provider;terminal 只能来自 provider 明确结果/本地明确失败/显式 Cancel;settle-once:真实退出才释放 runKey(直接依赖 RunStore 的状态与 process_registry)。
  - `FakeProviderAdapter`:可编程脚本(按序产出 progress 事件、最终成功/失败/挂起),用于测试;放 Worker 项目,不放 Core。
  - `FakeHealthProbe`:固定返回注入的健康状态。
  - Worker 装配:Generic Host 注册全部组件;观察循环每 20 秒读 RunStore 活动 run + 进程存活性,只更新观察字段,**任何静默/耗时指标不得触发状态变更**。
  - 事件:状态变更经 `RunStore.TryAppendEvent` 落 `run_events`(seq 单调,Started 必须先于 terminal)。
- 测试:happy path(start→running→succeeded,事件序合法);取消 pre-spawn→cancelled 无进程;取消 running→childPending 直到 fake close→cancelled 且 runKey 释放;provider 挂起时观察循环不判失败(模拟 3 个周期静默,状态仍 running);同 runKey 第二个 start 被拒;side_effect_marked 后模拟 provider 失败,断言不会出现第二次 provider 调用(fallback 禁止)。
- 完成标准:`csharp\build.ps1` 全绿;新增测试 ≥6 项。

### S2-F 机密与日志脱敏

- 允许范围:`csharp/src/AiResume.Secrets/**`、`csharp/src/AiResume.Worker/Logging/**`(新建)、`csharp/test/AiResume.Tests/Secrets*.cs`。
- 实现(对应规格 §3.7/3.8):
  - `DpapiSecretStore`:`Set(ref, value)`/`TryGet(ref)`/`Delete(ref)`;值经 `ProtectedData.Protect(CurrentUser)` 存 `<shadowDir>\secrets\<ref>.bin`;ref 只允许 `[a-z0-9-]`,非法拒绝;文件不存在返回 false 不抛。
  - 日志脱敏器:单行 JSON 日志写入器 + 脱敏层——键名含 secret/password/token/authorization/cookie(不区分大小写)的值一律 `[REDACTED]`;另接受"已知机密值"集合做全文置换;Error 对象只取 message。
  - shadow 目录:默认 `%LOCALAPPDATA%\ClaudeResumeShadow`,环境变量 `AIRESUME_SHADOW_DIR` 可覆盖;测试必须覆盖到临时目录。
- 测试:round-trip;密文文件确实不含明文(读原始字节断言);跨 ref 隔离;非法 ref 拒绝;脱敏器对嵌套对象/循环引用/Error/known-value 全部不泄漏;日志文件逐行是合法 JSON。
- 完成标准:`csharp\build.ps1` 全绿;新增测试 ≥5 项;全仓 `rg -i "sk-|app_secret"` 仅命中文档与既有脱敏代码注释。

### S2-G WPF 空壳

- 允许范围:`csharp/src/AiResume.Gui/**`。
- 实现:主窗口显示 Worker 连接状态——启动时经 S2-C 客户端向 pipe 发 `ping`,显示"已连接(版本 x)/未连接";一个"刷新"按钮重发 ping。无其他功能,不做样式。
- 测试:GUI 不强制自动化测试;但 ping 客户端逻辑必须抽成可测类并有 1 项单元测试(fake transport)。
- 完成标准:`csharp\build.ps1` 全绿;`dotnet build` 出的 exe 可启动不崩(人工点开一次即可)。

## 4. 每包完成后的报告格式(给用户转交验证方)

```
包:S2-X
commit:<hash>
build.ps1 输出末 6 行:<粘贴>
新增/修改文件:<清单>
设计决策与偏离:<无,或逐条说明>
自测未覆盖的风险:<诚实列出>
```

## 5. 验证方将做什么(实施者知悉即可)

逐包:diff 全量审查(对照红线与规格逐条)、独立复跑 build.ps1、对安全关键路径(S2-D 三态核验、S2-F 脱敏)做对抗性构造、抽查测试断言是否真实(非跑通即过)。任何 P1 问题 → 退回该包修复,最多两轮;两轮不过验证方直接接管修复。

## 6. 停止条件

遇到以下情况立即停止并报告,不要自行绕过:基线不绿、需改冻结接口、需新依赖、构建工具链异常无法用 §2 的手段解决、任何对红线的疑问。
