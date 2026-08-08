# Stage 6 规格 v2:cc-connect 直接运行与边界收敛

> **v2 于 2026-08-06 重写。** v1(「wrapper 接管会话/授权/turn」)已由 ADR-0003 作废,
> 原文见 git 历史。本文是 Stage 6 的现行真身。

## 1. 目标(与 v1 的根本区别)

| | v1(已作废) | v2(现行) |
|---|---|---|
| cc-connect 的地位 | AI Resume 的 sidecar,由 wrapper 包装并改造用法 | **直接运行的唯一飞书/会话/agent 真身**,AI Resume 适配它 |
| wrapper 的职责 | 会话生命周期 + 授权 + turn 状态映射 + 进程编排 | **只剩进程编排 + 配置生成 + 单消费者守卫** |
| 验收方式 | wrapper 能否镜像现役全部行为 | cc-connect 能否**在本机以单消费者姿态稳定运行** |

Stage 6 完成的判据不再是"wrapper 补齐了多少能力",而是:
**cc-connect 能被确定性地配置、启动、停止、崩溃后重启,且启动前能证明本机没有第二个飞书事件消费者。**

## 2. 处置清单(2026-08-06 按证据修订)

ADR-0003 §4 给出的处置在本阶段逐项复核。**三项与原判断不符,以证据为准**:

| 组件 | ADR-0003 原判 | 本阶段结论 | 证据 |
|---|---|---|---|
| `CcConnectRunMapper`(229 行) | 大幅缩减 | ✅ **已删除** | AI Resume 的续跑引擎跑**自己**的 `claude --continue` 进程(S7-D),从不消费 cc-connect 的 turn 事件;全仓除自身测试外零引用 |
| `CcConnectSessionBridge`(171 行) | 作废(交还上游) | 🔶 **保留,重新定性** | 上游 `cc-connect sessions prune` 只做**去重/清空**(`Remove duplicate sessions per chat`),**没有按年龄的归档/删除**。14/30 天保留策略在上游无对应物,交还即等于功能消失 |
| `CcConnectAuthMapper`(97 行) | 重估(改用原生 `allow_from`) | 🔶 **保留,缩小职责** | cc-connect 的 `isAdmin` 只门禁**特权命令**(`core/engine.go:1177`),没有"非 owner 禁全部文件工具"这层;而后者是 CLAUDE.md 的安全红线。`allow_from` 只收敛「谁能进」 |
| `CcConnectSupervisor`(426 行) | 保留并简化 | ✅ **保留 + 新增守卫职责** | 见 §3 |
| `CcConnectConfigGenerator`(164 行) | 保留 | ✅ 保留 | 确定性生成仓库外 config.toml 仍有效 |

> **教训**:"交还上游"必须先确认上游真的做这件事。本阶段三项里有两项的原判断建立在
> 未经查证的假设上。同类错误已写入全局「上游优先」规则。

## 3. 单消费者守卫(本阶段新增,D-008/D-015 的关闭路径)

### 3.1 为什么必须有

飞书长连接是**集群模式**:同一应用有多个客户端在线时,事件**随机投递给其中一个**,不是广播。
两个消费者同时在线的表现**不是"重复回复"**(那反而好发现),而是**用户消息被随机截走**——
一半消息石沉大海,且没有任何错误日志。

### 3.2 契约

`AiResume.Wrapper.SingleConsumerGuard`:

```csharp
ConsumerGuardResult Check(bool feishuPlatformConfigured);
// Verdict ∈ { Clear, Conflict, Unverifiable };CanStart 仅 Clear 为 true
```

判定顺序:

1. **未声明飞书平台 → 直接 Clear**,不枚举进程(bridge-only 冒烟不该为无意义核验付代价);
2. **枚举器读不到命令行 → Unverifiable**。现役 node agent 只能靠命令行里的 `feishu-agent.js` 识别;
   看不到命令行就无法排除它,此时报 Clear 等于**凭空担保**;
3. 枚举抛异常或返回 null → **Unverifiable**(fail-closed);
4. 命中 `feishu-agent.js` → `legacy-node-agent`;进程名为 `cc-connect` 且非自身 → `cc-connect`;
5. 无命中 → Clear。

**安全约束**:结果里的 `Detail` **只由进程名与固定文案拼成,绝不放原始命令行**——
命令行可能带飞书 `app_secret`,一旦进入结果就会流进日志与界面。

### 3.3 接入点

`CcConnectSupervisor.StartAsync` 在 **spawn 之前**调用守卫,不通过即抛异常且不启动任何进程。
是否声明飞书平台由 `DeclaresFeishuPlatform(configPath)` 判定,**刻意保守**:
读不到文件或解析不确定一律按"已声明"处理,宁可多核验一次。

### 3.4 已知缺口(生产切换前必须补)

仓库现有的进程枚举手段(`System.Diagnostics`、`NativeProcessProbe` 的 Toolhelp32)
**都拿不到命令行**,因此默认枚举器 `DiagnosticsRunningProcessLister.ProvidesCommandLine` 为 `false`,
守卫会一律判 `Unverifiable`。

> **这不是缺陷而是如实声明。** Stage 10 生产切换前必须提供能读命令行的枚举器
> (CIM `Win32_Process` 或等价手段),那正是 **D-008「证明单消费者」的关闭条件**。
> 在此之前,任何"cc-connect 已安全接管飞书"的说法都不成立。

## 4. 本阶段不做的事

| 项 | 原因 |
|---|---|
| 让 cc-connect 接生产飞书应用 | 属 Stage 10,且需 §3.4 缺口先关闭 |
| S6-D 场景 4/6/7 复跑 | 验证目标随边界变化;停止通道已由 S6-D 实证锁定为 bridge,不再需要 wrapper 侧映射 |
| 会话 14/30 天清理的实际接线 | 保留了实现,但是否启用取决于 Stage 7-E「会话管理入口」的职责划分 |

## 5. 门禁

- `csharp\build.ps1` 全绿(build 0 警告 · 测试全绿 · secrets gate 0 命中);
- 单消费者守卫覆盖:未声明飞书直接放行、读不到命令行判无法核验、
  现役 agent 命中冲突、自身 PID 不算冲突、多冲突按 PID 排序、
  **`Detail` 不含命令行中的机密子串**;
- `CcConnectSupervisor` 覆盖:冲突时 spawn 前拒绝、无法核验时拒绝、未声明飞书时正常启动;
- 全程零生产接触:不启动接生产飞书应用的 cc-connect,不读生产 `config.json`。
