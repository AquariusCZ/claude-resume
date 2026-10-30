# S8-G 规格:Codex 通知适配器(链式包装)

> 这是 Stage 8 中**唯一被单独立包**的适配器。原因不是工作量,而是风险:Codex 的 `notify` 是
> TOML 单行数组,且必须**链式包装用户既有 notify**,误改会破坏在用配置。语义严格照现役
> `src/install-completion-hooks.js` 的 `wrapNotify` / `mergeCodexNotify` / `installCodex` 复刻。
>
> **现役修订(2026-08-13):** 本文件保留 Stage 8 的设计背景,现役行为以
> `docs/COMPLETION-NOTIFICATIONS.md` 与 C# 代码为准。外层 `notify` 必须用 Tomlyn 按 TOML
> 解析,因为合法配置可混用 basic/literal string；`--previous-notify` 的字符串值才是 JSON。
> 所有权也已收紧为首个 exe + `codex` 参数的完整托管命令形状,不再按“任意元素含文件名”判断。

## 1. 配置位置与形状

- 文件:活动 Codex home 下的 `config.toml`(显式参数 → `AI_RESUME_CODEX_HOME` → `CODEX_HOME` → `%USERPROFILE%\.codex`;构造函数可注入,测试须能指定临时路径)。
- 目标键:`notify`,**必须是位于首个 `[section]` 之前(顶部区)的单行 TOML 数组**,
  形如 `notify = ["C:\\path\\AiResume.Hook.exe", "codex"]`。
- 外层数组用 Tomlyn 解析并要求每项都是字符串；写回使用 `System.Text.Json` 序列化字符串数组,
  因其输出是合法 TOML basic-string 数组子集。`--previous-notify` 对应元素内部仍保存 JSON 数组文本。
- 旧配置清理只在 TOML 语句边界识别键；多行字符串、数组和内联表内部形似
  `notify = [...]` 的文本是用户值内容,必须保留。当前 Codex Desktop/app-server 不能假设
  会热加载后来写入的配置,写入后需重启已运行客户端再验证真实事件。

## 2. 我方命令的形状

```
[ <hookExe 完整路径>, "codex" ]                                    # 无既有 notify
[ <hookExe>, "codex", "--previous-notify", "<既有命令的 JSON 文本>" ]  # 包装既有 notify
```

所有权判定:当前数组层的首个可执行文件名精确为 `MarkerFileName`(常量
`"AiResume.Hook.exe"`,大小写不敏感),且下一项为 `"codex"`;普通参数文本中偶然出现文件名不算所有权。

## 3. 合并算法(逐条复刻,顺序不可变)

给定既有数组 `existing` 与我方 `hookExe`:

1. **刷新已托管链**:若 `existing` 是我方命令(含标记),或其 `--previous-notify` 链中**任一层**
   是我方命令,则只更新该层的 exe 路径,其余原样保留 → 返回刷新后的数组。
   (递归下探 `--previous-notify` 的 JSON,深度上限 8,超限视为不可处理并抛异常。)
2. 若 `existing` 已包含标记但不匹配上述结构 → 原样返回(幂等,不重复包装)。
3. **Codex Desktop wrapper 特判**:若 `existing[0]` 的文件名(小写)为
   `codex-computer-use.exe` 或 `cod-use.exe`:
   - 若它已有 `--previous-notify`,则把**我方命令包到那一层的内部**(即包装它原有的 previous);
   - 否则在末尾追加 `--previous-notify <我方命令 JSON>`。
   **不得把 Desktop wrapper 本身塞进我方的 previous**——那会改变 Codex 桌面端的调用链。
4. 其余情况:用我方命令包装 `existing`,即 `[hookExe, "codex", "--previous-notify", JSON(existing)]`。

**硬性拒绝**:若待包装的既有命令其 `[0]` 以 `.cmd` 或 `.bat` 结尾,**抛异常并拒绝安装**,
消息说明「批处理 notify 链无法安全包装,请保留既有 notify 或改用可执行文件」。
(现役同样拒绝:批处理的参数转义与退出码传递不可靠。)

## 4. 文件写入规则

- **只操作顶部区**(首个 `[section]` 之前的内容),section 内的同名键一律不动。
- 顶部区不存在 `notify` 行 → 在首个表头之前插入一行(当前实现置于文件首行,语义仍属顶部区)。
- 存在**单行数组**形式 → 原地替换该行的数组部分,保留行内前后缀(缩进、行尾注释)。
- 存在 `notify =` 但**不是单行数组**(多行、非数组) → **抛异常拒绝**,不猜测不改写。
- 按配置路径获取跨进程命名互斥体；基于锁内读取的原文生成候选，提交前再次逐字比较。
- 临时文件 flush 后用 `File.Replace` 原子替换，`config.toml.bak` 是实际被替换版本的提交见证；若它不等于读取快照，恢复外部版本、保留 `.conflict-*` 并抛异常。换行统一为 CRLF。

## 5. 接口

实现 `INotificationAdapter`(定义在 `NotificationRegistry.cs`),`Kind = NotificationProviderKind.Codex`,
`DisplayName = "Codex"`。

- `Probe()`:`~/.codex` 目录存在即 `IsInstalled=true`;`IsEnabled` = 顶部区 `notify` 数组含标记;
  文件不存在/TOML 无法解析时不抛异常,记入 `Detail`。
- `Enable(hookCommand)`:`hookCommand` 形如 `"<exe路径> codex"`(空格分隔),
  取其 exe 路径部分参与数组构造;按 §3 合并、§4 写入。
- `Disable()`:把我方那一层从链中摘除并**把它的 `--previous-notify` 内容提升上来**
  (即还原用户原有 notify);若我方是最外层且无 previous,则删除整行 `notify`。
  不含标记时不做任何事。

## 6. 测试要求(`CodexNotificationAdapterTests`)

1. 目录不存在 → `IsInstalled=false`,不抛异常;
2. 无 `notify` 行 → `Enable` 后顶部区出现我方数组,`IsEnabled=true`;
3. **包装既有 notify**:预置 `notify = ["C:\\tools\\my-notify.exe"]`,`Enable` 后我方在最外层
   且 `--previous-notify` 内容等于原数组;`Disable` 后**完整还原**为原数组;
4. 幂等:连续两次 `Enable`,不产生嵌套两层我方命令;
5. 刷新:预置我方旧路径的数组,`Enable` 新路径后只有路径被更新,`--previous-notify` 链原样;
6. **Desktop wrapper**:预置 `notify = ["C:\\x\\codex-computer-use.exe"]`,`Enable` 后
   wrapper 仍在 `[0]`,我方出现在其 `--previous-notify` 中;
7. **批处理拒绝**:预置 `notify = ["C:\\x\\legacy.cmd"]`,`Enable` 抛异常且**配置文件未被修改**;
8. **非单行数组拒绝**:预置多行 `notify`,`Enable` 抛异常且文件未被修改;
9. **section 内同名键不受影响**:预置顶部 `notify` 与 `[profiles.x]` 段内的 `notify`,
   操作后段内那个逐字未变;
10. `Enable` 生成 `.bak`。

全部使用系统临时目录,**禁止触碰真实 `%USERPROFILE%\.codex`**。
