# ADR-0001:AI Resume 目标架构(全量迁移)

- 状态:**Accepted**
- 日期:2026-08-01
- 决策者:用户(架构权威);OpenAI Codex 架构监督;DeepSeek V4-flash 为主要开发执行器(开发流程分工,不是产品运行时依赖或密钥写入仓库)
- 补充决策:AI 运行生命周期由 `docs/adr/0002-run-lifecycle-contract.md` 细化;冲突处以 ADR-0002 为准
- 关联:`docs/MIGRATION-BASELINE.md`、`docs/STATE-OWNERSHIP.md`、`docs/EVENT-CONTRACTS.md`、`docs/RUN-CONTRACT.md`、`docs/UPSTREAM-ARCHITECTURE-RESEARCH.md`

## 背景

现役 AI Resume v2(`src/package.json` 唯一版本真身)由 PowerShell 5.1 GUI/引擎 + Node `feishu-agent.js` + JSON/marker 状态构成;`feishu-agent.js` 单体同时处理长连接、鉴权、会话、卡片、图片、运行锁、健康探测与完成投递,缺陷集中在生命周期边界。2026-07-31 上游研究与 2026-08-01 阶段 0 基线确认:采用 cc-connect 与官方 lark-cli 作为目标组件,并以 .NET 10 重构产品内核与 GUI。

## 目标

1. .NET 10 C# Worker Service 独占:Windows 服务、项目发现、布防、额度、可靠进程/任务控制、产品状态。
2. C# WPF 等价迁移现有 GUI。
3. SQLite + WAL 为目标产品状态真身;DPAPI/Windows Credential Manager 保存长期密钥;Named Pipe 为本机 GUI/服务控制通道。
4. cc-connect 为唯一生产飞书消息入口与 Agent 会话编排真身。
5. lark-cli + 用户级 `lark-*` Skills 为飞书标准 OpenAPI 能力层,不消费生产入站。
6. C# 单文件 hook 替代 Node hook,保留 Codex/Claude Code/Cline 完成边界,不伪造 DeepSeek Copilot Chat 任务完成。
7. 生产永远单一飞书消费者;独立测试应用通过全部门禁后才一次性切换。

## 约束

- 现役生产链路(Node `feishu-agent.js`)在迁移完成前仍是唯一消费者;禁止与 cc-connect 双消费同一生产应用、禁止双写同一会话/任务状态。
- cc-connect 只能先用独立测试应用 + 测试项目做兼容性验证。
- 任意时刻每类状态只有一个 writer(见 `docs/STATE-OWNERSHIP.md`)。
- 机器当前无 .NET SDK(仅 8.0.25 runtime);按基线**阶段 2** 才安装固定 SDK(winget 官方源 `Microsoft.DotNet.SDK.10` `10.0.302`)。
- 版本/事实以 2026-08-01 `docs/MIGRATION-BASELINE.md` 为准;不得夸大已实现范围。
- DeepSeek V4-flash 开发分工不改变「密钥不写入仓库」红线。

## 决策

1. **采纳 .NET 10 C# 内核 + cc-connect 编排 + lark-cli 能力层 + C# WPF 的渐进迁移**(0-11 阶段,见 UPSTREAM 文档)。
2. **cc-connect 边界**:入站飞书事件、Agent 会话编排由其承担;其 codex/claude 子命令**可配置为 C# launcher wrapper**。
3. **C# Worker 强制补齐上游缺口**(cc-connect 1.4.1 审计结论):
   - launcher wrapper + Windows Job Object(进程树终止);
   - durable run registry(父/子 PID、runKey、启动时间、命令签名、fail-closed 回收);
   - stop barrier(终止宽限期;真实 close 才 `childPending=false` 并释放运行键)+ settle-once;
   - completion outbox(SQLite,WAL)承载本地完成事件投递;
   - 按 ADR-0002/RunContract 实现 Start/Status/Cancel;所有 AI 生成与健康探测不创建客户端总时长计时器,每 15-30 秒观察持久状态和进程存活性。
4. **wrapper 优先,不维护 Go 私有 fork**;若独立试点证明 wrapper 无法满足门禁,再形成**新 ADR** 由用户确认,不得静默改方向。
5. **cc-connect 配置由 C# ProjectCatalogBridge 确定性生成**;Management/Bridge 默认禁用;GUI 不直连 Management API;版本只以 `cc-connect --version` 确认,未知子命令不得在真实 config 目录试探。
6. **lark-cli 适配器契约**:parserKind=`envelope|catalog|ndjson|binary`;argv + shell:false;固定原生 exe,每次显式 profile 与 `--as bot|user`。后台自动化固定 bot profile 且 strict/risk control 开启;user 身份只用于用户明确授权且策略允许的操作。高风险 exit 10 不自动 `--yes`;send/reply 只有稳定 idempotency key 才重试;raw API 固定 allowlist;`event consume` 仅测试/诊断,不是生产入口。
7. **完成边界**:只维护 Codex、Claude Code、Cline 三个可靠边界;`DeepSeek V4 for Copilot Chat` 的 provider 回调不冒充整个 Copilot Agent 任务完成。
8. **状态真身**:SQLite+WAL 为产品状态真身,迁移方式与回滚真身见 `docs/STATE-OWNERSHIP.md`。

## 替代方案(评估)

| 方案 | 结论 | 原因 |
|---|---|---|
| 保持现状,仅 JS 增量重构 | 拒绝(作为回退保留) | 无法满足可靠进程控制、数据库状态、单一编排内核目标;`feishu-agent.js` 单体边界已反复出缺陷 |
| 直接整体替换为 cc-connect(不做 C# 内核) | 拒绝 | 1.4.1 无 stop barrier/durable child registry/completion outbox;Session 遗漏 ActiveProvider/LastUserActivity;done reaction 不可靠;Management API 空 token 无认证且 /config 返回原始凭据;需 C# Worker 补齐 |
| 维护 Go 私有 fork 修补 cc-connect | 拒绝(当前) | 维护成本与升级漂移;wrapper 优先方案足以验证;若试点证伪再走新 ADR |
| C# Worker + cc-connect 编排 + lark-cli 能力层(采纳) | **采纳** | 职责清晰:产品状态/进程/额度/投递归 C#,消息与 Agent 会话编排归 cc-connect,OpenAPI 能力归 lark-cli;单一消费者一次性切换 |
| 保留手写飞书 SDK 请求 | 拒绝 | 官方 lark-cli/命令存在时优先调用/封装,不重复手写同类 SDK 请求 |

## C# 与 cc-connect 边界

| 职责 | 归属 |
|---|---|
| Windows 服务生命周期、项目发现、布防、额度、可靠进程/任务控制、产品状态(SQLite/WAL)、完成 outbox、健康探测线路 | C# Worker |
| 飞书长连接入站、Agent 会话编排、消息/卡片输出 | cc-connect(唯一生产消费者) |
| 飞书 OpenAPI 能力(文档/日历/任务/消息等) | lark-cli + 用户级 `lark-*` Skills(能力层,不消费生产入站;AI Resume 会话回复仍归 cc-connect) |
| 本机控制面(GUI ↔ 服务) | Named Pipe;GUI 不直连 cc-connect Management API |
| 长期密钥 | DPAPI/Windows Credential Manager;C# Worker 独占 |
| 本地完成边界 | C# 单文件 hook(Codex/Claude Code/Cline) |

## 安全

- cc-connect Management API 监听所有网卡、空 token 无认证、/status 暴露 bridge token、/config 返回原始凭据 → **生产禁用**,GUI 不直连;配置由 ProjectCatalogBridge 确定性生成,Management/Bridge 默认禁用。
- 密钥/令牌只存 C# 机密存储;事件契约禁止机密内联(EVENT-CONTRACTS 第 8 节);仓库/文档/日志不得出现 sk-、app_secret、token 实值。
- 非 owner 工具隔离语义保留(查询/闲聊禁全部文件工具),迁移后由 C# Worker 配置等价执行。
- 生产单一消费者;测试应用凭据隔离;所有密钥/端点/代理配置变化用指纹立即作废旧健康线路。

## 可观测性

- 事件/命令统一信封 + 幂等键(EVENT-CONTRACTS);outbox 提供投递记录与重试可观测。
- 结构化日志(本地时间)替代现役混合文本日志;进程登记/回收全程可审计。
- ProviderHealthChanged 携带快照 TTL 与配置指纹,线路决策可审计。
- 门禁指标:完整离线矩阵、GUI 四模式、provider-live 两次、query/chat security canary,均以 `docs/MIGRATION-BASELINE.md` 记录为准。

## 回滚

- 每类状态切换前生成回滚真身(STATE-OWNERSHIP 矩阵);回滚先停新消费者再起旧消费者,保持单 writer。
- 阶段 11 前保留 Node 链路文件与 JSON 快照;整体回退必须用户确认(ADR 机制)。
- 迁移改动不 commit/push/deploy,直到对应阶段门禁通过。

## 后果

正面:

- 状态事务化(SQLite+WAL)、进程树可控(Job Object)、完成投递可追(outbox)、单一编排真身(cc-connect)、官方能力层(lark-cli)、本机控制通道(Named Pipe)。
- 长期可维护:不再把长连接/权限/卡片/运行/健康堆在同一进程。

负面/代价:

- 迁移周期长(0-11),期间 Node 现役链路继续维护;
- 需先申请独立测试应用授权与测试项目;
- cc-connect 缺口必须由 C# Worker 补齐,不能依赖上游;
- 阶段 2 前机器无 SDK,需按固定版本安装。
