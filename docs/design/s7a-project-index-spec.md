# S7-A 规格:项目发现索引化(消除 O(全部历史会话) 扫描)

> 依据:`docs/design/gui-startup-profile.md`(实测根因)、ADR-0003 §5.4(首帧不得依赖 I/O)、§6.1(不在现役修复,转为新实现的设计约束)。
> 现状:`csharp/src/AiResume.Worker/Products/ProjectCatalog.cs` 复刻了现役全量扫描算法,只有 3 秒内存缓存,**未解决复杂度问题**。

## 1. 问题

`ProjectCatalog.Discover` 每次冷调用都遍历发现根下**全部**会话目录,对每个目录枚举 `*.jsonl`、按 mtime 排序、打开最新文件读 64 KiB、逐行找 `"cwd"` 并 `JsonDocument.Parse`。

本机实测规模:**1153 个会话目录 / 2533 个 jsonl / 639 MB,产出仅 13 个项目,PowerShell 等价实现耗时 2227 ms**。成本随**历史会话总量**增长,而非项目数;会话文件只增不减,故持续恶化。

## 2. 目标

在**完全不改变 `Discover` 现有可观察语义**的前提下,把冷调用(索引已存在且大部分目录未变)的耗时降到 **< 100 ms**。

### 2.1 必须保持不变的语义(回归红线)

以下行为已由现有 15 个 S5-A 测试锁定,**不得改变**:

1. 排除规则:`hiddenProjects`(全路径小写精确匹配)、生产 AppDir、系统 temp、`^[a-z]:\windows`;
2. cwd 提取:最新 jsonl 的前 64 KiB / 前 60 行内首个可解析出 `cwd` 字符串的行;
3. 按路径去重(保 `LastWriteUtc` 最新)、按 `LastWriteUtc` 降序排序;
4. `Name` 取路径末段;
5. `customProjects` 追加:存在、未排除、未重复才加,`LastWriteUtc = DateTimeOffset.MinValue`,**不参与重排**;
6. 3 秒内存缓存 + 配置指纹(hidden/custom/projectHome),指纹变化立即重算;
7. 发现根不可读时容错继续处理 custom;
8. 只读会话元数据,不写任何生产状态,不读生产 `config.json`。

## 3. 设计

新增 `ProjectIndex`(建议 `csharp/src/AiResume.Worker/Products/ProjectIndex.cs`),供 `ProjectCatalog` 内部使用。

### 3.1 索引条目

以**会话目录**为 key,记录:

| 字段 | 说明 |
|---|---|
| `SessionDir` | 会话目录全路径(key,大小写不敏感) |
| `DirWriteUtc` | 该会话目录自身的 `LastWriteTimeUtc`——**增量判定依据** |
| `JsonlPath` | 上次判定的最新 jsonl 全路径(可为 null,表示该目录当时无 jsonl) |
| `JsonlWriteUtc` | 该 jsonl 的 `LastWriteTimeUtc`(即现有 `ProjectEntry.LastWriteUtc` 的来源) |
| `Cwd` | 解析出的 cwd(可为 null,表示解析失败/无 cwd——**空结果同样要缓存,避免每次重试**) |

### 3.2 增量算法

```
枚举发现根下的目录(只取目录名与 DirWriteUtc,不进入目录内部)
对每个目录:
  命中索引 且 DirWriteUtc 未变  -> 直接复用索引中的 JsonlWriteUtc / Cwd,不做任何文件 I/O
  未命中 或 DirWriteUtc 已变    -> 走原有全量路径(枚举 jsonl / 选最新 / 读头部 / 解析 cwd),写回索引
索引中存在但本次枚举未出现的目录 -> 从索引移除(目录已删)
```

排除规则、去重、排序、custom 追加**在索引之后照原逻辑执行**——索引只缓存「目录 → (jsonl mtime, cwd)」这一层昂贵的 I/O,不缓存策略结果。这样 `hiddenProjects` 等配置变化无需失效索引。

### 3.3 持久化

- 位置:经现有 `ShadowPaths` 提供的 shadow 目录(**不得写生产 AppDir**),文件名 `project-index.json`。
- 写入:**临时文件 + flush + 原子替换**(与仓库既有原子写实践一致),避免半写。
- 读取失败、JSON 损坏、版本号不匹配:**静默降级为全量扫描并重建索引**,不抛异常、不阻断发现。
- 索引带 `Version` 字段(当前 `1`),便于后续演进。
- 写入时机:仅在本次发现有任何条目新增/更新/移除时才落盘;无变化则不写。

### 3.4 构造与注入

`ProjectCatalog` 现有构造参数保持兼容(全部可选)。新增一个可选参数用于注入索引文件路径,**测试必须能指定临时路径**,不得写用户真实目录。索引功能须可关闭(用于对照测试)。

## 4. 测试要求(新增,置于 `csharp/test/AiResume.Tests/`)

1. **首次运行**:无索引文件 → 全量扫描 → 结果正确 → 索引文件已创建;
2. **二次运行命中索引**:目录未变 → 结果与首次**完全一致**,且**不发生 jsonl 文件读取**(用可计数的读取钩子或注入的文件访问计数器断言 I/O 次数为 0);
3. **增量更新**:仅改动一个会话目录(新增 jsonl 使其 `DirWriteUtc` 变化)→ 只有该目录被重新解析,其余复用;
4. **目录删除**:索引中存在的目录被删除 → 结果中消失且索引条目被移除;
5. **空结果缓存**:无 jsonl / cwd 解析失败的目录,二次运行不重复尝试读取;
6. **索引损坏容错**:写入非法 JSON / 错误 Version → 不抛异常,结果与全量扫描一致,索引被重建;
7. **语义回归**:§2.1 全部 8 条逐条覆盖(排除规则、去重保最新、排序、custom 追加不重排、指纹失效、发现根不可读容错);
8. **原子写**:索引写入过程中不留下半写文件(可断言临时文件在成功后不存在)。

## 5. 约束

- 目标框架 `net10.0-windows`,与现有项目一致;**不得新增任何 NuGet 依赖**(仅用 BCL,JSON 用 `System.Text.Json`)。
- 不得改动 `ProjectEntry` 的公开形状。
- 不得引入线程不安全:`Discover` 现有 `lock (_gate)` 语义保持,索引读写同样要线程安全。
- 代码注释沿用仓库既有中文风格;命名与既有文件一致。
- 全部文件必须完整,不得省略或用「...」占位。

## 6. 交付物

1. `csharp/src/AiResume.Worker/Products/ProjectIndex.cs`(新增)
2. `csharp/src/AiResume.Worker/Products/ProjectCatalog.cs`(修改:接入索引,保持语义)
3. `csharp/test/AiResume.Tests/ProjectIndexTests.cs`(新增,覆盖 §4 全部 8 组)
