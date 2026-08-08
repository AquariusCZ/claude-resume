# 过夜加固晨报(S10-O,2026-08-07 晨)

任务书:`docs/OVERNIGHT-HARDENING-BRIEF.md`;规则基线:`CLAUDE.md`(冲突以 CLAUDE.md 为准)。
分支:`s10-overnight-hardening`(起点 HEAD `41ec715`)。**未 push。**

---

## 1. 一句话结论

**程序比昨晚更稳了**:22 个新用例把此前只写在文档里的红线(回收四项判据、408 超时归类、
配置并发写、七种损坏形态)变成了会红的测试,并真实抓出 1 个实现缺陷(D4 锁预算)修掉;
依据是 443 → **465** 全绿且浸泡期间 IPC/进程行为无异常。但**常驻泄漏结论没拿到**
(样本点严重不足,见 §5),「崩了还能接着跑」的损坏路径已系统性钉住,「7×24 不涨内存」
仍是未验证命题。

## 2. 阶段表

| 阶段 | 状态 | 提交 | 证据位置 |
|---|---|---|---|
| P0 基线固化 | ✅ 完成 | `857949f` | 提交信息(443 全绿 / cc-connect PID 40996 / config SHA256 前12位 22337F1EF043 / HEAD 41ec715) |
| P1 隔离浸泡 | ⚠️ 部分(宿主全程在线,采样点不足) | `696ec60` | `docs/evidence/soak-20260806.csv`(8 点)、`docs/evidence/soak-launch.ps1`、`soak-sampler.ps1`、`csharp/src/AiResume.Worker/TestGcSampleHook.cs` |
| P2 红线覆盖矩阵+补测 | ✅ 完成 | `00642a9` | `docs/RED-LINE-COVERAGE.md`、`ProcessVerifierTests.cs`(新)、两处补例 |
| P3 故障注入 | ✅ 完成 | `28ed8b4` | `FaultInjectionTests.cs`(14 例)、RED-LINE-COVERAGE §P3 与 fail-closed 清单 |
| P4 文档同步 | ✅ 完成 | `c809b86` | `docs/P4-DOC-SYNC-TABLE.md` + 三份文档修订 |
| P5 凭据审计 | ✅ 完成 | `c21f0bc` | `docs/P5-CREDENTIAL-AUDIT-20260807.md`(零实值) |
| P6 收口+晨报 | ✅ 完成 | 本提交 | 本文件 + CSV 终态 |

## 3. 测试数变化:443 → 465(+22,0 失败 0 跳过)

| 新增 | 钉住的红线 |
|---|---|
| `ProcessVerifierTests` ×6 | 回收判据:PID 复用(启动时间差 3h)绝不 Matched;签名独立判据;±5s 容差边界(4.9 过 / 5.1 拒);特征缺失 fail-closed;查询失败(Unknown)≠进程消失(Gone) |
| `Http408_ReturnsGatewayTimeout` | 结构化超时是 408/504 两个,原来只钉了 504 |
| `ProductConfigStore_concurrent_updates_preserve_disjoint_fields` | 锁内读-改-写并发不丢更新(修复前为红,见 D4) |
| `FaultInjectionTests` ×14 | 七种「写到一半」形态 × 四类持久化状态:损坏配置绝不默认 armed/enabled/continuous=true;SQLite 垃圾库明确报错不静默重建;BOM/CRLF 正常解析;不可写目录报错不吞;tmp 残留绝不当配置读 |

## 4. 发现的缺陷

### 已修

- **D4 ProductConfigStore.AcquireLock 锁预算过短**(提交 `00642a9` 点名)。
  复现:两个 store 实例并发 `Update`(各 50 轮),修复前 3×20ms≈60ms 预算必抛
  IOException,把正常锁竞争变成写失败。修复:20×25ms≈500ms,耗尽仍抛,
  fail-closed 语义不变。**这是改实现让红变绿,不是弱化断言。**

### 已确认但未修(行为变更,按任务书 §4 停手)

- **D5 `ProductConfig.SkipPermissions` 默认 true**。损坏/缺失配置回默认后,
  续跑命令会追加 `--dangerously-skip-permissions`。复现:往 shadow config.json
  写任意垃圾 → `Load()` → `SkipPermissions==true`(用例 `Known_gap_D5_...` 钉住现状)。
  与现役 PowerShell 默认一致;改 false 属行为变更,需人裁决。
- **D6 截断 JSON 容错回默认而非拒绝加载**。任务书期望表写「拒绝加载」,现状是
  对齐现役 Get-CcuConfig 的 catch-默认(代码注释写明有意为之)。除 D5 外回退值
  均在安全侧。保留现状,记录差异。

### 疑似,需人工判断

- **D1 回收判定缺父 PID 核验**。红线要求「父 agent PID + PID + 启动时间 + 命令签名」
  四项全匹配;`ProcessVerifier.Verify` 实际只核三项,`parent_pid` 存于登记表但未参与
  判定。复现:读 `csharp/src/AiResume.Worker/Supervision/ProcessVerifier.cs`。
  收紧判定可能影响断电恢复路径,未自行拍板。
- **D2 「完整 .tmp-* 作恢复候选」语义 C# 未实现**(仅现役 Node 有)。P3 已钉住
  底线(tmp 绝不被当配置读),候选语义是否引入属行为变更。

## 5. 我没做到什么(诚实清单)

1. **P1 浸泡样本点只有 8 个,任务书要求 ≥60**。原因链:P1 启动后宿主占用仓库 bin
   DLL 导致 `dotnet test` 无法重编译,两次停宿主改从 `%TEMP%` 副本重启
   (PID 36820→38512),有效连续浸泡仅约 35 分钟,5 分钟间隔只攒下 8 点。
   **因此「句柄/私有字节不得单调上升」这条验收我没有数据支撑**,8 点内 handles
   326→362 缓升、private 12-17MB 波动,不足以定性。浸泡脚本已入库,重跑一晚即得。
2. **GC 堆列是替代方案**:任务书要的性能计数器实例 .NET Core 根本不发布(实测
   计数器类别里没有任何 Worker 实例),改用宿主内 `TestGcSampleHook`
   (`AIRESUME_TEST_GC_SAMPLE=1` 门控)自报 `GC.GetTotalMemory(false)`——
   它不含非托管/碎片,与真实私有堆有偏差。
3. **浸泡 status 列打的是错误路径**:用不存在的假 runId 查状态(红线禁止对真实
   项目起 AI 运行,shadow 里没有真 run)。所以浸泡只证明「空闲不泄漏、IPC 不僵死」,
   不证明带载行为。
4. **D1/D2/D5/D6 均未修**(理由见 §4),只钉现状与记录。
5. **P5 L5(run-20260804.log 第 335 行凭据形命中)未定性**——看内容可能把实值
   带进上下文,按红线止于「位置+模式」,等人看。
6. **STAGE-11-GATE 表里计划任务/注册表/启动项三行未逐项实测**(超出 P4 时间盒)。
7. **B 段冒烟(飞书手工六项)无法执行**,只有 owner 能做。
8. **P2 优先清单里「非 owner 禁文件工具」的 C# 侧只复核了既有用例**(viewer 工具集
   为空已钉住),没有新增 e2e——真实 e2e 在 Node 侧 `query-security.js`,本夜未跑
   (读生产配置基线的测试,按「不在过夜任务里动生产配置」保守跳过)。

## 6. 不变量核对

| 项 | P0 | P6(晨) | 一致 |
|---|---|---|---|
| cc-connect.exe PID | 40996(2026-08-06 11:17:52) | 40996(同一 CreationDate) | ✅ |
| config.toml SHA256 前12位 | 22337F1EF043 | 22337F1EF043 | ✅ |
| 全量测试 | 443/0/0 | 465/0/0 | ✅(只增不减) |
| git status | 干净 | 干净(P6 提交后) | ✅ |
| 红线触碰 | — | 未启停 cc-connect;未写 ~/.cc-connect/config.toml(仅读项目名行与哈希);未起任何 AI 写运行;报告零凭据实值;浸泡宿主设了 `AIRESUME_TEST_PIPE_SUFFIX=soak8f3k`;ClaudeResume/ClaudeResumeShadow 只读 | ✅ |

## 7. 给人的下一步建议

**必须做**
1. 裁决 D5:`SkipPermissions` 损坏默认 true 是否改为 false(影响所有续跑的命令形态);
2. 人工查看 `%LOCALAPPDATA%\ClaudeResume\logs\run-20260804.log` 第 335 行(P5 L5),
   定性后决定是否给 Node run 日志补脱敏;
3. 清理 `~/.cc-connect\config.toml.bak-before-repair`(全量明文副本,修复已确认)。

**建议做**
4. 重跑一晚浸泡(脚本现成:`docs/evidence/soak-launch.ps1` + `soak-sampler.ps1`,
   把 `IntervalSeconds` 调到 60 可一晚攒 480 点),补齐常驻泄漏结论;
5. 裁决 D1(回收判定加父 PID 核验)——若收紧,需同步断电恢复路径用例;
6. 按 P5 报告第五节顺序清理 TEMP pilot 目录与 150+ 个 s5d 残留;
7. 冒烟 A6-A8 走 Web 聊天桥重验(计划已修订,见 STAGE-10-SMOKE-PLAN §0.5)。

**可选**
8. D2 的 .tmp 恢复候选语义是否引入 C#;
9. NEXT-STEPS/MIGRATION-PROGRESS 整体刷新(neat-freak 收尾);
10. `s10-overnight-hardening` 分支合回主线(合并前建议人工过一遍 22 个新用例)。
