# 红线覆盖矩阵(S10-O/P2+P3,2026-08-06 夜)

基线:443 → **465** 通过 / 0 失败 / 0 跳过。本矩阵把 `CLAUDE.md` 四节
(测试红线 / 会话生命周期 / 安全约束 / GUI 服务状态语义)中**可判定的断言**
逐条对照 `csharp/test/AiResume.Tests/` 的 443+8+14 个用例。

所有权说明:ADR-0003 后 C# Worker 只承担 AI Resume 自有职责,不镜像
cc-connect 内部状态机;现役 Node 链路(src/、test/*.js)的红线由 Node 测试
钉住,不在本矩阵判定范围内,仅在相关行注明。

图例:✅ 已覆盖 / 🟡 部分覆盖(本轮已补或记录缺口) / ⚪ 不适用或仅 Node 侧

---

## 一、测试红线(CLAUDE.md §测试红线)

| # | 断言 | 出处 | 判定 | 钉住它的用例 |
|---|---|---|---|---|
| R1 | 只有结构化 HTTP 408/504/gateway_timeout 才是 provider 超时 | §53 | ✅(本轮补 408) | `Http504_ReturnsGatewayTimeout`、**`Http408_ReturnsGatewayTimeout`(新)** |
| R2 | DNS/TCP/TLS/reset、进程消失、监控异常 → failed_local,绝不冒充 provider 故障 | §53 | ✅ | `NetworkException_ReturnsFailedLocal`、`TokenNeverLeaks_...`(network→failed_local)、`Adapter_probe_no_claude_maps_to_failed_local`、`ProcessGone_withProviderFailure_fails_local_not_succeeded`、`ProviderStartRejection_InternalClass_fails_local` |
| R3 | 用户停止 = cancelled,且不得触发 fallback/重放 | §53 | ✅ | `RunningCancel_childPending_until_gone_then_cancelled_runKey_released`、`SideEffectMarked_disables_fallback_then_failure_goes_failed_provider`、`RunStateMachineTests.Terminal_states_accept_no_outgoing_transition`(编排器本身无任何 fallback 路径) |
| R4 | 五种任务不设客户端总时限;静默 N 分钟不产生失败;heartbeatAt/lastOutputAt/silentSeconds 只进指标不进判定 | §53 | ✅ | `ProviderHang_three_cycles_stays_running_no_failure`(挂起三轮仍 running)、`Adapter_status_during_probe_is_silent`、`EventEnvelopeV1Tests.Deadline_ms_is_always_zero` |
| R5 | 子进程回收判据:父 agent PID + 子 PID + 5 秒内启动时间 + provider 命令签名全匹配;禁止只凭 PID 杀进程 | §54 | 🟡→已补判定函数单测;**父 PID 核验缺口见 D1** | **`ProcessVerifierTests` 6 例(新)**:PID 复用(启动时间差 3h)→Mismatched、签名不符→Mismatched、±5s 容差边界(4.9s 过 / 5.1s 拒)、特征缺失→Unverifiable、Unknown≠Gone;既有 `Pid_reuse_mismatched_is_reported_not_actionable`、`Cancel_mismatched_registration_is_removed_without_killing_process` |
| R6 | CIM/探测三态 found/gone/failed 区分;只有明确 gone 才能删登记;failed 保留并重试 | §54/§55 | ✅ | `Cancel_unverifiable_keeps_registry_and_does_not_terminate`、`RecoverAsync_keeps_unverifiable_fail_closed_and_reports`、`RecoverAsync_cleans_gone_registration_and_reports`、`Probe_unknown_is_unverifiable_fail_closed`、`ListerThrows_ReturnsUnverifiable_FailClosed` |
| R7 | 写 config 必须锁内读-改-写、只改自己字段;禁止锁外读旧快照后整体写回 | §55 | 🟡→已补+修实现 | **`ProductConfigStore_concurrent_updates_preserve_disjoint_fields`(新)**:两写者各改各字段交叉 50 轮,逐项不丢不重;既有 `ProductConfigStore_concurrent_saves_end_atomically`(Save 原子性)。**暴露并修复实现缺陷:锁预算原 3×20ms≈60ms,真实并发下必抛 IOException(见 D4)** |
| R8 | 原子写:临时文件 + 替换,磁盘上永远只有完整内容 | §54/§55 | ✅ | `Write_is_atomic_and_leaves_no_tmp`、`Config_write_is_atomic_and_leaves_no_tmp`、`Atomic_write_leaves_no_temp_file_after_success`、`SaveIfChanged_returns_false_when_no_change_and_no_temp_left` |
| R9 | 内容完整的 `.tmp-*` 残留应作为恢复候选 | §55 | ⚪ 记录 | 该语义源表现役 Node `checker-ai-child.json`;C# `ProductConfigStore` 的 WriteAtomic 失败即删 tmp、加载不读 tmp 候选。是否要把恢复候选语义引入 C# 属行为变更 → **待人工裁决(D2)**,P3 故障注入表已把两种 tmp 形态列入观测 |
| R10 | 测试绝不能对真实项目/真实会话启动 AI 修改运行 | §51 | ⚪ 流程约束 | 无法用断言钉住;由环境隔离保证(假 runKey/TempPath、`FEISHU_TEST_NO_AI`、本夜浸泡宿主三隔离) |

## 二、会话生命周期(CLAUDE.md §会话生命周期)

| # | 断言 | 判定 | 钉住它的用例 |
|---|---|---|---|
| S1 | 14 天归档 / 30 天删除只对闲聊与只读查询生效 | ✅ | `Classify_chat_follows_14_30_thresholds`、`Classify_query_uses_same_thresholds` |
| S2 | 项目工作会话**绝不**自动归档或删除(放置再久也在) | ✅ | `Classify_work_session_is_always_protected`(15 天与 **365 天**双断言,强于任务书要求的 60 天用例) |
| S3 | 会话列表读取失败时清理 fail-closed,不冒充空列表 | ✅ | `Cleanup_list_failure_fails_closed`、`Cleanup_mixed_sessions_routes_correctly` |
| S4 | Claude 归档同时移动 jsonl+artifact;Codex 走 app-server API | ⚪ Node 侧 | C# 不承接(会话文件操作归 cc-connect/Node);Node 侧 `test/session-manager.js` |
| S5 | GUI/飞书/自动清理共用同一套逻辑,禁止复制哈希/删除逻辑 | ✅(C# 侧) | C# 清理只经 `CcConnectSessionBridge.Classify`+`CleanupAsync` 一条路径(桥接 cc-connect admin API,无本地删除分支) |

## 三、安全约束(CLAUDE.md §安全约束)

| # | 断言 | 判定 | 钉住它的用例 |
|---|---|---|---|
| A1 | 非 owner 的查询/闲聊禁全部文件工具(不靠模式约束) | ✅ | `Resolve_viewer_has_no_file_tools`、`Resolve_unknown_user_gets_none`(C# 生成 cc-connect 授权映射时 viewer 工具集为空);Node 侧另有 `test/query-security.js`+`chat-security.js` e2e |
| A2 | feishuAuthOpenIds 为空 = 未锁定;移除最后一个 full 用户解锁需警告 | ✅ | `Resolve_empty_lists_means_unlocked_with_warning`、`Removing_last_owner_unlocks` |
| A3 | allow_from 为空 = 放行所有人:写配置端必须拒绝产出空 allow_from | ✅ | `Write_rejects_empty_allow_from_and_preserves_existing`、`Build_allow_from_empty_means_unset_allow_all` |
| A4 | 凭据绝不进仓库/日志/报告/异常消息 | ✅ | `Config_render_sanitized_never_contains_secret`、`CommandLineContainsAppSecret_DetailMustNotLeakSecret`、`DailyJsonFileLogger_redacts_injected_fake_secrets_zero_leak`、`SecretRedactor_*` 4 例、`TokenNeverLeaks_IntoFailureReasonsOrSnapshot`、`DpapiSecretStore_round_trip_and_no_plaintext_on_disk`、`凭据字段不进目标且报告不泄露` |
| A5 | 空白/畸形 open_id 永不匹配授权 | ✅ | `Resolve_blank_open_id_never_matches` |

## 四、GUI 服务状态语义(CLAUDE.md §GUI 服务状态语义)

该节主体描述现役 PowerShell/Node GUI(picker.ps1 / provider-health.js)行为,
由 Node 侧 `test/ai-providers.js`、`test/provider-live.js` 与 picker 自测钉住。
C# 侧承接的是其中的**额度取数与探测红线**:

| # | 断言 | 判定 | 钉住它的用例 |
|---|---|---|---|
| G1 | 「可用」只能来自真实最小请求成功;Key 已填/命令存在 ≠ 可用 | ⚪ Node 侧 | `test/ai-providers.js`(C# GUI 尚无 provider 状态面板) |
| G2 | Claude 探测区分未登录/额度/网络/模型不可用/未安装 | ✅(探测分类) | `ClaudeProbeTests`、`Adapter_probe_*` 分类映射 6 例 |
| G3 | oauth/usage 只读 token,绝不刷新、绝不写回;剩余寿命 <60s 视同过期 | ✅ | `ClaudeOAuthUsageProbeTests` 11+1 例(过期/无凭据/各失败分支);实现中不存在任何凭据写路径(只读 `ReadAccessToken`) |
| G4 | 探测不设客户端总时限;DNS/TCP/TLS/reset 归 failed_local | ✅ | `NetworkException_ReturnsFailedLocal`、`ThrowingHandler` 各异常分支 |
| G5 | 额度窗口映射不得把「未报告」当 0;越界钳制;无数据时 hasData=false | ✅ | `UsageSnapshotMapperTests` 12 例、`ClaudeUsageBlocksTests` 13 例 |

---

## 本轮新钉住的 8 个用例(443 → 451)

| 用例 | 钉住的红线 |
|---|---|
| `ProcessVerifierTests.Pid_reused_long_after_registration_is_mismatched_never_matched` | R5:PID 复用反例(同 PID、启动时间差 3h)绝不 Matched |
| `ProcessVerifierTests.Same_pid_but_different_command_signature_is_mismatched` | R5:签名是独立判据 |
| `ProcessVerifierTests.Match_within_tolerance_requires_time_and_signature_both` | R5:±5s 容差边界(4.9 过 / 5.1 拒) |
| `ProcessVerifierTests.Alive_but_feature_missing_is_unverifiable_fail_closed` | R5/R6:特征缺一 fail-closed |
| `ProcessVerifierTests.Probe_unknown_liveness_is_unverifiable_not_gone` | R6:查询失败 ≠ 进程消失 |
| `ProcessVerifierTests.Explicitly_gone_process_is_gone` | R6:三态基线 |
| `ClaudeOAuthUsageProbeTests.Http408_ReturnsGatewayTimeout` | R1:结构化超时是 408/504 两个,原来只钉了 504 |
| `ProductCatalogTests.ProductConfigStore_concurrent_updates_preserve_disjoint_fields` | R7:锁内读-改-写并发不丢更新 |

## 发现但未修的疑似缺陷(详见晨报)

- **D1(疑似,需人工判断)**:`ProcessVerifier.Verify` 只核验 启动时间+命令签名
  两项(加 PID 共三项),红线要求的**父 agent PID 未参与判定**。`parent_pid`
  已存于 `process_registry` 表、`ProcessSnapshotEntry` 也能读到父 PID,但判定
  函数没用。收紧判定属行为变更(可能影响断电恢复路径),未自行拍板。
- **D2(记录)**:「完整 `.tmp-*` 作恢复候选」语义仅存在于现役 Node;
  C# `ProductConfigStore` 未实现(失败即删 tmp、加载不读 tmp)。待人工裁决。
- **D4(已修,本轮点名)**:`ProductConfigStore.AcquireLock` 锁预算仅
  3×20ms≈60ms,GUI 与续跑引擎并发写配置的**真实场景**下必然抛 IOException,
  把正常锁竞争变成写失败。已加大到 20×25ms≈500ms;预算耗尽仍抛,
  fail-closed 语义不变(这是改实现,不是弱化断言——新并发用例修复前就是红的)。

---

## P3 故障注入结果(FaultInjectionTests 14 例,全绿)

对全部 C# 持久化状态构造七种「写到一半」形态,断言安全失败而非静默错误:

| 状态载体 | 空文件 | 截断 | 字段缺失/类型错 | BOM+CRLF | 目录不可写 | tmp 残留 |
|---|---|---|---|---|---|---|
| config.json(ProductConfigStore) | ✅→安全默认 | ✅→安全默认+原文件保留 | ✅→拒绝回安全默认 | ✅正常解析 | ✅明确报错无半写 | ✅绝不当配置读 |
| project-index.json(ProjectIndex) | ✅→空索引重扫 | ✅→空索引+原文件保留 | ✅单条损坏跳过 | ✅正常解析 | ✅静默 false(纯缓存,设计如此) | ⚪ 固定名 .tmp 原子替换无残留语义 |
| product_state 表(ProductStateStore) | ✅→idle 默认 | ✅→idle 默认 | ✅→idle 默认 | ⚪ DB 文本列不适用 | ⚪ SQLite 层 | ⚪ 事务写无 tmp |
| runs.db(SQLite 本体:runs/process_registry/run_events/outbox) | ✅→视同新建 | ✅垃圾库明确报 SqliteException,不在损坏文件上静默重建,原文件字节数不变 | ⚪ schema CHECK 约束兜底 | ⚪ | ⚪ | ⚪ WAL 由既有断电用例钉住 |

### 「哪些字段目前不是 fail-closed」清单(DoD 交付物)

- **D5(危险默认,待人工裁决)**:`ProductConfig.SkipPermissions` 类默认值为
  `true`。损坏/缺失配置回默认后,续跑命令会追加 `--dangerously-skip-permissions`
  (`ClaudeResumeRunner`)。与现役 PowerShell 默认一致,改 false 属行为变更;
  用例 `Known_gap_D5_...` 钉住现状,裁决后改默认值时该用例会变红提醒。
- **D6(有意偏离任务书期望表,已记录)**:任务书期望「截断 JSON → 拒绝加载」,
  现状是「容错回默认」(对齐现役 Get-CcuConfig catch 后给默认的语义,代码注释
  写明是有意为之)。除 D5 的 skipPermissions 外,所有默认值都在安全侧
  (armed/enabled/continuous 全 false)。用例钉住「回退值全部安全」+原文件保留。
- **D7(设计差异,非缺陷)**:项目索引写盘失败静默返回 false(纯缓存,丢缓存
  代价只是一次全量重扫),与 config 写失败必须报错的语义不同;两条用例分别钉住。
- **实测教训(入测试注释)**:Windows 目录只读属性不阻止在其中创建文件,
  「不可写目录」故障注入必须用 `icacls /deny` ACL 构造(首跑红过一次证实)。
- SQLite 垃圾库用例验证了**报错而非静默清空**:run 历史/登记表/事件队列在库损坏时
  不会被静默当成空库重建(若那样会吞掉全部状态),原文件原样保留待人工处置。
