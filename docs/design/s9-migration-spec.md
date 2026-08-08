# S9 规格:AI Resume 自有状态迁移(现役 AppDir → shadow)

冻结日期 2026-08-06。对应 `docs/NEXT-STEPS.md` 的 Stage 9。
要求(原文):**幂等、可对账(数量/哈希)、原文件只备份不删**。

## 1. 迁移范围(已按证据收敛)

现役 AppDir 是 `%LOCALAPPDATA%\ClaudeResume\`,目标是 shadow 根 `%LOCALAPPDATA%\ClaudeResumeShadow\`。

| 源 | 目标 | 结论 |
|---|---|---|
| `config.json` 的 **14 个自有字段** | shadow `config.json`(`ProductConfig`) | 迁移 |
| `state.json` | SQLite `product_state`(`CheckerState`) | 迁移 |
| `completion-events-seen.json`(93 条) | — | **不迁移**,见 §2 |

### 1.1 config.json 白名单(**只读这 14 个键,其余一律不取值**)

`enabled` `armed` `armCycleId` `continuous` `selected` `customProjects` `hiddenProjects`
`projectHome` `probeIntervalMinutes` `probeModel` `resumeModel` `resumePrompt`
`skipPermissions` `dirtyGuard`

现役 config.json 有 47 个顶层键,其余 33 个含 `feishuAppSecret` / `feishuSecret` /
`openaiApiKey` / `deepseekApiKey` / `aiProxy` 等**凭据**。红线:凭据字段
**不读取、不迁移、不出现在对账报告里**——报告只允许出现「跳过 N 个非自有键」这个计数,
**连键名都不列**(键名本身足以泄露账号体系结构,且对账不需要它)。

`quotaRefreshMinutes` 是 shadow 新增字段,现役没有,**不在白名单**,保留目标侧默认值 15。

### 1.2 state.json → CheckerState 字段映射

现役 state.json 实测全文(2026-08-04 采样)字段与 `CheckerState` 同名 camelCase,直接映射:
`phase` `cycleId` `sawLimited` `lastProbeUtc` `limitedRefires`
`realFiveHourResetUtc` `realSevenDayResetUtc` `realResetProbedUtc` `realFiveHourUtil` `projectStatus`

**注意两处类型差异**:
- `realFiveHourResetUtc` / `realSevenDayResetUtc` 在现役是 **Unix 秒整数**(实测 `1785919800`),
  而 `CheckerState` 是 `DateTimeOffset?` → 必须 `DateTimeOffset.FromUnixTimeSeconds`。
- `realResetProbedUtc` 在现役是 **Unix 秒整数**(实测 `1786015384`),同上。
- `lastProbeUtc` 在现役是 **ISO 8601 字符串**(实测 `"2026-08-04T17:50:02.06+00:00"`)→ 直接 parse。
  同一文件里两种时间表示并存,不要统一处理。

现役独有、目标没有的字段(`targetId` `targetEndUtc` `firedForId`)**丢弃**,计入报告的
`droppedLegacyFields` 计数(这三个是现役定时器实现细节,目标用 CheckerCycle 重新推导)。

## 2. 为什么不迁移完成通知去重记录(实证结论,不是省事)

两侧 eventId 算法完全不同,legacy 的 93 条键在新系统里**一条也不会命中**:

| | 现役 `stableEventId`(src/completion-notify.js:82) | 目标 `ComputeEventId`(AiResume.Hook/Program.cs:61) |
|---|---|---|
| 摘要 | SHA1 of `JSON{source,sessionId,paths,transcriptMtime,assistant}` | SHA256 of `source\|sessionId\|cwd\|transcriptMtime` |
| 长度 | 40 hex | 16 hex(前 8 字节) |
| 前缀 | `<source>:` | 无 |
| source 名 | `claude` | `claudecode` |
| mtime | `Math.floor(mtimeMs)` 毫秒整数 | ISO 8601 `"o"` |

去重记录的作用是「同一事件重发时不重复投递」,而目标侧的去重真身是
`outbox.idempotency_key UNIQUE`,由新 hook 现场生成。迁移历史键既不会命中、
也不会挡住任何东西,只是往表里灌 93 行死数据。

**风险复核**:新 hook 只对切换后新产生的 Stop 事件触发,不回放历史,因此不存在
「切换后被历史任务的重复通知刷屏」的风险。故不迁移是安全的。

对账报告必须**显式记录这一项**为 `skipped`,并带上理由字符串,不能静默不提。

## 3. 组件契约

新增 `csharp/src/AiResume.Worker/Migration/ProductStateMigrator.cs`。

```csharp
public sealed record MigrationOptions(
    string LegacyAppDir,      // 现役 AppDir(目录)
    string ShadowRoot,        // shadow 根(目录)
    string DatabasePath,      // shadow SQLite 文件完整路径
    bool DryRun,              // true = 只读 + 出报告,不写任何目标、不做备份
    bool Force);              // true = 已迁移过也重跑

public sealed record MigrationItemResult(
    string Source,            // "config.json" / "state.json" / "completion-events-seen.json"
    string Status,            // "migrated" / "skipped" / "missing" / "failed"
    int Count,                // 实际迁移的字段/条目数
    string? SourceSha256,     // 源文件内容 SHA256(十六进制大写);缺失时 null
    string? BackupPath,       // 备份落点;DryRun 时 null
    string? Reason);          // skipped/failed 的理由

public sealed record MigrationReport(
    bool DryRun,
    DateTimeOffset StartedAt,
    IReadOnlyList<MigrationItemResult> Items,
    int SkippedNonOwnedKeys,      // config.json 里被白名单挡掉的顶层键数量(只给数量)
    int DroppedLegacyFields,      // state.json 里目标没有的字段数量
    bool Success);                // 无 failed 项即 true

public sealed class ProductStateMigrator
{
    public ProductStateMigrator(ProductConfigStore configStore, ProductStateStore stateStore);
    public MigrationReport Run(MigrationOptions options);
}
```

### 3.1 幂等

shadow 根下写标记 `migration-state.json`:`{"completedAt":"...","sourceHashes":{"config.json":"...","state.json":"..."}}`。

- 标记存在 **且** 源文件哈希与记录一致 → 全部项目返回 `skipped` / reason=`"已迁移且源未变化"`,不写目标;
- 源文件哈希变了 → 正常迁移并更新标记(源确实变了,重迁是对的);
- `Force=true` → 无条件迁移;
- `DryRun=true` → **绝不写标记、绝不备份、绝不写目标**,只出报告。

### 3.2 备份(原文件只备份不删)

非 DryRun 且确实要写目标时,先把每个源文件复制到
`<ShadowRoot>\migration-backup\<yyyyMMdd-HHmmss>\<原文件名>`。
**绝不删除、绝不修改现役 AppDir 里的任何文件**(现役 AppDir 全程只读)。
备份 `completion-events-seen.json` 也要做(它虽不迁移,但属于"迁移演练时的现场快照")。

### 3.3 写目标的方式

- config:`configStore.Update(cfg => { ...只赋白名单里的 14 个字段... })`
  —— **必须用 Update 不能用 Save**,锁内重读只改本次负责字段(GUI/引擎可能并发写)。
- state:`stateStore.Save(checkerState)`(product_state 表整行就是这一份状态,整体写正确)。

### 3.4 容错

- 源文件不存在 → 该项 `missing`,不算失败(全新安装本来就没有);
- 源文件 JSON 损坏 → 该项 `failed` + reason,**其余项继续迁移**,整体 `Success=false`;
- 单个字段类型不符(如 `probeIntervalMinutes` 是字符串)→ 跳过该字段、其余照迁,计入 `DroppedLegacyFields`。

## 4. 入口

`AiResume.Worker.exe migrate [--dry-run] [--force]`:在 `Program.cs` 建 Host **之前**分支处理,
打印人类可读报告后 `return`。不进 Host、不起 BackgroundService。
退出码:`Success` → 0,否则 1。

报告输出示例(**不得出现任何凭据键名或值**):
```
迁移演练(dry-run) 2026-08-06T05:40:00Z
  config.json                   migrated  14 字段   sha256=A1B2…  跳过非自有键 33 个
  state.json                    migrated  10 字段   sha256=C3D4…  丢弃现役独有字段 3 个
  completion-events-seen.json   skipped   —         两侧 eventId 算法不同,迁移无意义(见 S9 规格 §2)
结论:成功
```

## 5. 测试要求

`csharp/test/AiResume.Tests/ProductStateMigratorTests.cs`,`[Collection(SqliteCollection.Name)]`。
全部用临时目录构造假的 legacy AppDir,**绝不触碰真实 `%LOCALAPPDATA%\ClaudeResume`**。

必须覆盖:
1. 14 个白名单字段全部正确迁入 shadow config;
2. **凭据字段不进目标**:legacy config 里放 `"feishuAppSecret":"S3CRET"` 等,
   断言 shadow config.json 全文**不包含**该字符串,且报告序列化后也不包含;
3. `SkippedNonOwnedKeys` 计数正确;
4. state.json 的 **Unix 秒整数**字段正确转成 `DateTimeOffset`(取一个已知秒值断言);
5. state.json 的 **ISO 字符串** `lastProbeUtc` 正确 parse;
6. 现役独有字段计入 `DroppedLegacyFields` 且不报错;
7. 幂等:连跑两次,第二次全部 `skipped`,且 shadow 内容与第一次完全一致;
8. 源变化后重跑会真正重迁;
9. `Force=true` 无视标记;
10. `DryRun=true` 不产生 shadow config、不产生标记、不产生备份目录;
11. 备份文件内容与源逐字节一致,且**源文件仍然存在**(只备份不删);
12. config.json 损坏时该项 `failed`、state.json 仍然迁移成功、整体 `Success=false`;
13. 源文件全部缺失时 config/state 为 `missing`、`Success=true`;
14. `completion-events-seen.json` **恒为 `skipped`**(含源文件不存在时)且 reason 非空。

> 本规格初稿的 13/14 两条自相矛盾(13 说"三项均 missing",14 说"恒为 skipped"),
> 实现按 14 执行并被测试抓出。**14 是对的**:不迁移它的理由是两侧 eventId 算法不同,
> 与文件在不在无关——文件存在时也照样跳过,不能因为这次恰好缺失就改口报 `missing`。

隐含前置(照抄否则必然失败):
- `ProductConfigStore(shadowRoot)` 收**目录**;`ProductStateStore(databasePath)` 收**文件完整路径**,
  且用前必须 `AiResume.Storage.StorageDatabase.Migrate(dbPath)`;
- `CheckerState.ProjectStatus` 是 `Dictionary<string,string>?`,可为 null;
- `ProductConfig.Selected` / `CustomProjects` 元素是 `ProjectRef{Name,Path}`,`HiddenProjects` 是 `List<string>`;
- `SqliteConnection.ClearAllPools()` 在 Dispose 里调,再递归删临时目录并吞异常。
