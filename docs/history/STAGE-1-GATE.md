# Stage 1 原系统解耦门禁

> 冻结日期：2026-08-01。未经用户确认，本阶段不得增加新的产品行为、外部依赖、状态格式或安全规则。

> 目标运行协议补充:ADR-0002/`RUN-CONTRACT.md` 已接受。Stage 1 继续保持现役 Node 用户行为等价,但抽取出的接口不得把 legacy deadline、静默超时或网络错误自动 fallback 固化为未来 C# 必须继承的契约。

> 当前进度:**Stage 1 总门禁已于 2026-08-04 关闭**。S1-A 至 S1-G 六个边界与 D-001/D-002/D-003/D-004/D-006 门禁全部完成;两名独立只读总审查及两轮修复复核累计发现并关闭:accepted-before-spawn 取消、shutdown 锁保留、child registry 损坏/检查失败/backup-generation 前沿/写回指纹、完成通知同 eventId 跨进程互斥、项目 guide no-reparse、显式项目快照、`aiProxy` 脱敏、D-006 provenance、SDK `1.70.0`/`onReady` 契约和安装 generation READY 等问题。最终复核由两名新的相互独立只读代理完成并同意关闭,唯一新增 P2(`writeChildRegistry` 失败路径 tmp 残留)已修复并补 after-tmp 注入回归,其余观察登记为 D-009~D-012。完整离线回归、`provider-live`、`query-security`、`chat-security` 与生产三层只读取证均已通过。经用户授权,增量已按 13 个工作包提交到 `migration-recovery-20260801`(`0492200`..`39d6aee`,未 push),并已按部署方案 a 覆盖生产 AppDir;部署取证见 `RECOVERY-AUDIT-20260801.md` 第 8 节。

## 目标

在不改变生产配置、运行方式和用户可见行为的前提下，将现役 Node 飞书入口拆成六个可替换边界，使后续 C# Worker、cc-connect 和 lark-cli 可以逐项接管，而不是继续向 `feishu-agent.js` 追加职责。

## 必须交付的六个边界

1. `ChannelAdapter`：飞书入站事件、ACK、文本/卡片/图片发送；业务层不直接依赖飞书 SDK。
2. `AgentAdapter`：provider 的 run/resume/cancel/progress/terminal 结果；保留现役 CLI 与 session 语义。
3. `TaskOrchestrator`：现役兼容任务接纳、run key、并发、fallback、stop、terminal settle；不拥有飞书 SDK,并为后续 Start/Status/Cancel 适配保留明确边界。
4. `ConversationStore`：聊天、项目、query/work session 映射和生命周期；不拥有卡片或 provider 逻辑。
5. `AuthorizationPolicy`：owner/viewer/allowlist/owner-only profile 的纯决策；不做 I/O。
6. `CompletionAdmission`：Codex/Claude Code/Cline 完成事件准入、项目解析、幂等键和投递请求；不读取飞书密钥。

`feishu-agent.js` 是稳定入口,只加载并导出 `feishu-runtime.js`;后者是机械迁移的现役 legacy compatibility application shell,承载配置装配和现役启动生命周期。它不是第七个目标边界,将在 Stage 6/10/11 被目标链路替换或删除。Stage 1 不改变现役 JSON/marker 真身。

## 行为等价门禁

- 固定一组录制输入，覆盖文本消息、项目查询、项目修改、模型切换、停止、完成通知和拒绝路径。
- 新旧实现对每个输入产生等价的 ACK 时序、卡片/消息意图、状态变更、provider 调用序列和 terminal 结果。
- 同一 chat 严格保序，跨 chat 可并发；长任务不阻塞事件 ACK。
- 现有 Codex、Claude Code、Cline 完成通知准入保持；DeepSeek Copilot Chat 不新增完成边界。
- 动态项目发现保持，不绑定项目数量。

## 本阶段绑定债务

- D-001：孤儿进程三态核验和未核验 PID 禁止终止。**Stage 1 稳定化已于 2026-08-02 通过并经总审查加固**：非法/缺失元数据、CIM/监控异常、主登记检查失败、登记损坏和写盘失败均保留磁盘真身与运行锁；`.bak` 仅在主文件明确缺失时可恢复,完整 generation 必须严格晚于主文件与全部损坏候选；写回前重验主文件指纹,损坏时锁存全局 AI 启动阻断；shutdown 只请求终止并等待真实 close/error 释放锁；只有 matched 可终止。Stage 5 durable registry + Job Object 最终替代仍待完成。
- D-002：malformed provider 活动 fail-closed，禁止 fallback 重放。**Stage 1 稳定化已于 2026-08-02 通过**：Claude/DeepSeek stream-json 与 Codex JSONL 的 malformed/unknown activity 单调标记副作用；录制 retryable 429 和 TaskOrchestrator 均证明只运行首个 provider；Stage 4/6 目标链路仍需复验。
- D-003：malformed owner 配置不披露项目。**Stage 1 稳定化已于 2026-08-02 通过并经总审查加固**：每个消息/卡片/菜单事件只以入口配置快照做授权与项目发现；项目发现缓存按 `hiddenProjects/customProjects` 指纹隔离；`ConversationStore.activeProject(chatId, projects)` 显式列表未命中即返回 null，旧 session/旧卡不得恢复隐藏或移除项目路径；入口 `none` 后磁盘升级为 owner 也不能在同一事件绑定通知 chat。Stage 6 目标权限链路仍需复验。
- D-004：测试 config/state/home 显式隔离，生产配置字节不变。**已于 2026-08-01 关闭并于 2026-08-02 加固**：全入口迁移、no-reparse/marker/canary 回归通过；顶层或嵌套 `aiProxy` 一律清空，认证代理 URL 不写临时 JSON；query guide junction canary 在 provider 前 fail-closed。
- D-006：**已于 2026-08-02 关闭 Stage 1 门禁并完成审查加固**。移动前先用旧入口录制 10 个固定场景,随后将旧入口逐字节复制为 `feishu-runtime.js` 并把 `feishu-agent.js` 收口为 14 行稳定 wrapper;fixture 现在自带移动前 SHA256 与真实生成时间并锁定重录，只有入口仍匹配该移动前 SHA 且显式授权时才能 record。移动后的 runtime 可继续接受安全修复，但不得改写移动前 provenance。

## 总审查修复

- TaskOrchestrator 的 reservation 是可取消 token；用户在接纳后、飞书提示/进度 I/O 完成前停止时，后续所有 pre-spawn 边界都返回不可 fallback 的 `cancelled`。
- shutdown 先关闭新接纳，活动 child 的 `running` 锁保留到真实 close/error；退出时不把 kill 请求当成进程已结束。
- completion 队列把 JSON/schema 永久错误隔离后继续后续事件；seen 状态先发布不可覆盖 generation，canonical 损坏仍可恢复去重。
- completion 同 eventId 的跨进程重查、飞书发送和 seen 持久化在同一锁内完成；稳定 UUID 保留崩溃重试幂等。
- `AI_GUIDE.md` 读取逐组件拒绝 reparse，验证 realpath containment 与已打开文件句柄；失败在 provider 启动前形成本地终态。
- `install.ps1` 固定并验证 `@larksuiteoapi/node-sdk@1.70.0` 原生 `onReady` 契约；只在旧 PID 全部明确 gone、重启后恰有一个稳定 agent 且 BOOT/READY 都携带本次安装 generation 时报告成功。

这些债务分成独立工作包，不能与六边界抽取混成一个实现任务。

## 明确范围外

- 不接入或启动 cc-connect、lark-cli、C#、SQLite、Named Pipe。
- 不部署、不重启生产机器人、不修改生产配置。
- 不改变卡片文案、GUI、模型目录或用户权限产品语义。
- 不重建整个旧测试框架，不为临时目录实现通用文件系统安全库。
- 不修复未绑定到本阶段且不是本次改动引入的其他历史问题；它们进入 `MIGRATION-DEBT.md`。

## 成本与停止条件

- 每个工作包最多一次 DeepSeek 主实现和一次修正。
- 单个工作包原则上不超过 4 个生产文件、4 个测试文件或 800 行净变更；超过必须重新拆分。
- 针对性测试在实现期间运行；完整离线回归只在 Stage 1 总门禁运行一次；真 API 冒烟只运行一次。
- 两次实现仍未通过同一门禁，立即停止并重新评估边界，不启动第三轮。
- 工作包完成后由 Codex 审查增量并运行针对性测试，不为每个工作包启动独立子审查。
- 六个边界和绑定债务全部实现、基础验证完成后，只启动一轮两个相互独立的 Stage 1 总审查；审查仅覆盖 Stage 1 总增量、绑定债务和本文件门禁。范围外发现只登记，不阻断 Stage 1。

## 完成证据

- 六个边界及其单元/契约测试存在，入口文件不再实现这些核心职责。
- `test/stage1-recorded-equivalence.js` 默认只读比较移动前 fixture；fixture 冻结 `preMoveSourceSha256=D2F7E63C...0960D` 与 `generatedAt=2026-08-01T18:51:29Z`。只有入口仍等于该移动前 SHA 且显式设置 `D006_ALLOW_PREMOVE_RECORD=1 --record` 才能重录；测试强制临时 config/state/home 与 `FEISHU_TEST_NO_AI=1`。
- 录制事件等价测试、现有离线测试、provider live smoke、query/chat security 按项目规则通过。
- 两个独立只读审查没有发现本次增量的未关闭 P0/P1 或门禁缺口。
- 门禁评审期间生产配置哈希不变、无部署、无生产进程切换；门禁关闭后经用户单独授权才执行提交与部署方案 a，取证见 `RECOVERY-AUDIT-20260801.md` 第 8 节。
