# AI Resume v2 迁移进度总览

> **这是进度的唯一速查真身。** 每完成一个工作包就更新本文;详细规格见各 `docs/STAGE-*-SPEC.md`,
> 方向决策见 `docs/adr/`,债务见 `docs/MIGRATION-DEBT.md`。
> 最后更新:**2026-08-06**,HEAD `e4640e7`,分支 `s2-external`(未推远端)。

## 一、当前位置

```
Stage  0    1    2    3    4    5    6    7    8    9    10   11
       ██   ██   ██   ██   ██   ██   ▓▓   ▓▓   ██   ░░   ░░   ░░
       完成 完成 完成 完成 完成 完成 收敛 进行 完成 未开 未开 未开
                                     待重  中
```

- **已完成**:Stage 0-5、Stage 8
- **进行中**:Stage 7(GUI 重构)
- **待重新定义**:Stage 6(原 wrapper 路线经 ADR-0003 作废,需按新边界重写规格)
- **未开始**:Stage 9(数据迁移演练)、Stage 10(生产切换)、Stage 11(收尾)

**当前门禁状态**:build 0 警告 0 错误 · **342 测试全绿** · secrets gate 0 命中。

## 二、方向变更(重要)

2026-08-06 经 **ADR-0003** 修订产品定位,原因是四条实证:

1. wrapper 补丁面持续扩大(已 1087 行/5 类职责,且仍在长);
2. cc-connect 1.4.1 **已具备**我方正在重写的能力(`claude_usage.go` / `UsageReporter` / `/usage`)——
   **Stage 5-B 因此作废**;
3. 上游 1.5.0-beta.1/.2 对 D-014 八条问题**零修复**,重心是横向加平台,不能指望上游补基础设施;
4. `LimitReached` 在 cc-connect 中**只读取不消费**——「限额后排队续跑」是本产品不可替代的核心。

**新定位**:AI Resume = **本机 AI 工作台控制面 + 续跑引擎**,只做四件事:
① 限额后自动续跑编排 ② 动态项目发现 ③ 本地完成通知 ④ Windows 控制面 GUI。
飞书/会话/agent/停止那一整层交给 cc-connect **直接运行,不再包装**。

## 三、逐阶段明细

| Stage | 内容 | 状态 | 关键交付 / commit |
|---|---|---|---|
| 0 | 产品基线 | ✅ | 基线/状态所有权/事件契约/ADR-0001·0002 |
| 1 | 原系统解耦 | ✅ | 六边界抽取 + 录制事件等价;STAGE-1-GATE 通过 |
| 2 | C# 基础设施 | ✅ | Ipc/Storage/Supervisor/Orchestrator/Secrets/GUI 骨架(`7c1810f`…`0f35123`) |
| 3 | lark-cli 试点 | ✅ | 进程封装 + 场景验证(`63ecdb4`、`b93fbfb`) |
| 4 | cc-connect 试点 | ✅ | 实机验证 + D-014 债务登记(`5748199`…`4dd6993`) |
| 5 | 产品状态迁移 | ✅ | S5-A/B/C/D(`4c494c3`…`0de6e06`);**S5-B 已由 ADR-0003 作废** |
| **6** | 会话与任务迁移 | 🔶 **收敛待重定义** | S6-A/B/C 已提交(`74c633b`…`c0eeeed`);S6-D 场景 1/2/3/5 通过、4/6/7 暂停;wrapper 路线经 ADR-0003 大幅缩减 |
| **7** | GUI 迁移 | 🔶 **进行中** | S7-A 索引化(`236c0e2`)、S7-B WebView2 控制面(`f6e74ba`) |
| 8 | Hook 与部署 | ✅ | 注册表 + 4 适配器 + Hook 处理器 + GUI 开关(`9efce80`、`e4640e7`) |
| 9 | 数据迁移演练 | ⬜ | 范围已缩小(会话状态交还 cc-connect) |
| 10 | 生产切换 | ⬜ | 约束已放松(用户已停用现役、允许直接在生产改测) |
| 11 | 收尾 | ⬜ | — |

## 四、已达成的硬指标

| 指标 | 现役 | 重构后 | 倍数 |
|---|---|---|---|
| GUI 冷启动首帧 | 3701 ms | **407 ms** | 9.1x |
| 项目发现(1153 目录/2533 jsonl/639MB) | 2227 ms | **35-40 ms** | ~60x |
| ├ 其中:换 C# 同算法 | 2227 ms | 308 ms | 7.2x |
| └ 其中:索引热启 | 308 ms | 35-40 ms | 8.8x |
| C# 测试数 | — | **308** | — |
| 完成通知 provider | 3(硬编码) | **4 可开关**(+Codex 待接入) | — |

## 五、Stage 7 剩余工作

- [x] **S7-C 额度接真实数据**(2026-08-06)——数据源改为自取,原因见 ADR-0003 §2.3 修订
- [x] **S7-D 续跑引擎驱动 + 布防交互**(2026-08-06)——补上 `CheckerCycle` 缺失的驱动者,
      GUI 布防写配置、Worker 引擎消费,核心链路首次真实跑通
- [x] **S7-E Claude design 重设计 + 双额度窗口 + provider 按计费模型分类**(2026-08-06)
- [ ] 「立即续跑」跨进程触发(GUI → Worker 需经 Named Pipe,现为禁用并标注原因)
- [ ] 会话管理入口(需先确认哪些仍属 AI Resume 职责)

### 额度显示的设计约束(实测得出)

服务端 `rate_limit_event` 的**字段可得性不对称**:`resetsAt`/`rateLimitType` 常态下发,
`utilization` 仅在用量较高时下发,且**单次调用未必两个窗口都给**。因此:

- 量尺表示**窗口已流逝的时间**(起点由 `resetsAt - windowSeconds` 推导并标注),不是用量;
- 用量百分比作独立读数,未下发时写「用量未报告」,**不得渲染成 0%**;
- 某窗口本次未下发时写「本次未下发」,不画条。

**不同 provider 的计费模型不同,不能套同一个 UI**:Claude 订阅有滚动窗口(可续跑),
DeepSeek 是 API 按量计费(**没有窗口**,不会"到点恢复"),中转 Codex 拿不到上游额度。
给没有窗口概念的 provider 画进度条 = 凭空发明数据。

## 六、明确未做的事(非遗漏,是判断)

| 项 | 原因 |
|---|---|
| **Codex 通知适配器** | `notify` 为 TOML 单行数组,需链式包装用户既有 notify(`--previous-notify`)、识别 Desktop wrapper、拒绝 batch 链;误改会破坏在用配置,须单独立包并配套回归 |
| **现役 GUI 性能修复** | 用户已停用现役(ADR-0003 §6.1),投入无收益;分析转为新实现的设计约束 |
| **S6-D 场景 4/6/7** | 验证目标随 ADR-0003 边界变化,需重新定义后再执行 |
| **升级 cc-connect 到 1.5.0-beta** | 实证对 D-014 零修复,且 beta 引入稳定性风险;维持锁定 1.4.1 commit `5d4c96dd` |
| **消费 cc-connect 的 `UsageReport`** | **上游有、但 Windows 上不可用**(2026-08-06 查证):`GetUsage` 依赖 `creack/pty`,`pty_unsupported.go` 的构建约束命中 Windows、`open()` 返回 `ErrUnsupported`;管理 API 全表亦无 usage 端点。故恢复启用 S5-B `ClaudeCodeProbe` 并映射成 `UsageSnapshot`(同形状,便于将来切回)。**这是「上游有 ≠ 能用」的实证案例,已写入跨 AI 全局规则** |
| **自研「推理强度(reasoning effort)」统一抽象** | **cc-connect 1.4.1 已具备**(2026-08-06 查证):`agent/{claudecode,codex,cursor,gemini,kimi,opencode,acp}` 共 7 个适配器读取 `opts["reasoning_effort"]`,取值归一化为 `low/medium(med)/high`,Codex 侧落为 `-c model_reasoning_effort=...`;`core/engine.go` 暴露 `/reasoning`(别名 `/effort`)聊天命令,并在回复页脚显示当前 effort。**按 ADR-0003 直接使用,不再自研**——这是继 `UsageReport` 之后第二个「上游已有、我方险些重写」的能力 |

## 七、开放债务(节选,全表见 MIGRATION-DEBT.md)

| ID | 级别 | 状态 |
|---|---|---|
| D-005 | P2 | open — 测试隔离 helper 的 TOCTOU 残留 |
| D-008 | P1 | open — 生产切换前须证明单消费者(Stage 10) |
| D-013 | P1 | mitigation — 仅剩用户重置生产 app secret 后关闭 |
| D-014 | P1 | open — 已重定性为「适配而非改造」 |
| D-015 | P2 | 部分关闭 — 飞书长连接为集群模式,风险是**随机分流**而非重复回复 |

## 八、怎么查进度

1. **看本文**——每个工作包完成后更新;
2. `git log --oneline` —— 每个工作包一次 commit,消息含实测数据与偏离说明;
3. 各 `docs/STAGE-*-SPEC.md` 的 §7 —— 逐包实现报告与证据;
4. `docs/design/` —— 设计稿、性能剖析、规格与实现记录。
