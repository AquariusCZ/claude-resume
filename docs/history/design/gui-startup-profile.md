# GUI 启动性能剖析(实测,2026-08-06)

> 结论先行:**现役 GUI 的 3701ms 冷启动,主因不是 PowerShell/WPF,而是项目发现的全量扫描。换技术栈最多省 7-10%。**
> 本文是 Stage 7 的输入依据;任何 GUI 方案都必须先解决这里定位的问题,否则换栈后依然慢。

## 1. 分段实测

测量方法:`powershell -NoProfile` 分段基准 + `picker.ps1 -RenderTo`(离屏渲染,该模式禁止真实探测,故不含 provider 探测开销)。

| 阶段 | 耗时 | 占比 |
|---|---|---|
| PowerShell 5.1 冷启动(空进程) | 162 ms | 4.4% |
| 加载 WPF 程序集(`PresentationFramework`) | 138 ms | 3.7% |
| 解析 picker.ps1 内联 XAML | 244 ms | 6.6% |
| **`Get-ClaudeProjects` 项目发现** | **2227 ms** | **60.2%** |
| 其余(配置读取、日志尾读、UI 绑定、渲染) | ~930 ms | ~25% |
| **合计(实测总启动)** | **3701 ms** | 100% |

> 「PowerShell + WPF + XAML」三项合计仅 **544 ms**。即使换成编译型栈把这三项归零,也只能把 3701ms 降到约 3157ms。

## 2. 根因:项目发现是 O(全部历史会话) 而非 O(项目数)

`src/lib.ps1:256` `Get-ClaudeProjects` 的实现:遍历 `~/.claude/projects/` 下**每一个**会话目录 → 枚举其 `*.jsonl` 并按 `LastWriteTime` 排序 → 读取最新文件前 60 行 → 逐行正则匹配 `"cwd"` 后 `ConvertFrom-Json` → 两次 `Test-Path`(项目存在性 + `.git`)。

本机实测规模:

| 指标 | 实测值 |
|---|---|
| `~/.claude/projects/` 会话目录数 | **1153** |
| `*.jsonl` 文件总数 | **2533** |
| jsonl 总体积 | **639.2 MB** |
| 最终发现的项目数 | **13** |
| 耗时 | **2227 ms** |

**为了得到 13 个项目,扫描了 1153 个目录并解析了上千个 JSON 文件。**

三个叠加的缺陷:

1. **无缓存**:每次启动全量重扫,不利用上次结果与目录 `mtime`。
2. **复杂度错配**:成本随**历史会话总量**增长,而非随**项目数**增长。
3. **同步阻塞**:在 UI 线程上完成,首帧必须等它。

## 3. 该问题会持续恶化

Claude Code 的会话文件只增不减(现役 `session-manager.js` 的 14/30 天清理只针对**飞书 scratch 会话**,不触碰 `~/.claude/projects/` 的工作会话——这是既定策略,不应改动)。因此 639 MB / 1153 目录会继续增长,启动时间随之线性恶化。**用户感知到的「越用越卡」有客观来源。**

## 4. 解法(与技术栈无关,任何方案都必须做)

按收益排序:

1. **索引 + 增量更新**(收益最大):把「项目 ↔ 最近会话」索引持久化(迁移方向的 SQLite 正是合适载体),启动只读索引;后台按目录 `mtime` 增量校正。预期把 2227 ms 降到个位数毫秒。
2. **首帧不依赖 I/O**:窗口骨架立即渲染,项目列表/provider 状态/额度均以占位态出现并异步填充。这是 ADR-0003 §5.4 已确立的硬约束。
3. **发现逻辑降复杂度**:即使无索引,也应先按目录 `mtime` 排序并早停,而不是无条件全扫 1153 个目录。
4. 换栈带来的 544 ms 属于附带收益,**不是主要目标**。

## 5. 对 Stage 7 选型的影响(2026-08-06 实测后修正)

> **本节初版结论「技术栈与启动性能基本无关、换栈最多省 7-10%」是错的,现予更正。**
> 初版只统计了 UI 框架开销(544 ms),遗漏了**业务逻辑本身在 PowerShell 中的解释执行代价**。

实测对照(同一算法、同一发现根、1153 个会话目录):

| 实现 | 项目发现耗时 |
|---|---|
| PowerShell 5.1 `Get-ClaudeProjects` | **2227 ms** |
| C# `ProjectCatalog`(无索引,同样全量扫描) | **308 ms** |
| C# `ProjectCatalog`(索引热启) | **35-40 ms** |

即:**同样的全量扫描,C# 比 PowerShell 快约 7.2 倍**;在此基础上索引再提速 8.8 倍。

因此换栈的可省时间应重算:

| 来源 | 可省 |
|---|---|
| 启动框架(PS + WPF + XAML → 编译型) | 544 ms |
| 项目发现(PS 解释执行 → C#) | 1919 ms |
| 索引化 | 268 ms |
| **合计** | **~2731 ms(占 3701 ms 的 74%)** |

**修正后的结论**:
1. 换栈到 C# 有**真实且显著**的性能收益,主要来自业务逻辑而非 UI 框架;
2. 但**索引化仍然必须做**——否则 308 ms 依旧不够,且会随历史会话增长持续恶化(复杂度问题不因换语言而消失);
3. **首帧不依赖 I/O 同样必须做**;
4. 三者叠加后 <500 ms 的目标可达。

WebView2 与 WPF 之间的选择**仍然与性能无关**,那一项的依据是视觉表达力与可维护性(ADR-0003 §5)。

## 6. 复现方法

```powershell
# 总启动
Measure-Command { powershell -NoProfile -ExecutionPolicy Bypass -File src\picker.ps1 -RenderTo out.png }

# 项目发现单项
. src\lib.ps1; $script:ClaudeProjectsRoot="$env:USERPROFILE\.claude\projects"
Measure-Command { Get-ClaudeProjects }

# 规模
(Get-ChildItem "$env:USERPROFILE\.claude\projects" -Directory).Count
(Get-ChildItem "$env:USERPROFILE\.claude\projects" -Filter *.jsonl -Recurse).Count
```
