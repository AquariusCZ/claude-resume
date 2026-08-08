# S8-D/E/F 规格:hook 处理器与既有 provider 适配器

> 承接 `docs/design/s8-notification-registry-spec.md`。S8-A/B/C 已交付注册表与 Qoder/OpenCode 适配器;
> 本规格补齐三件:① 各 provider hook 实际指向的**处理器可执行文件**;② ClaudeCode 适配器;③ Cline 适配器。
> Codex 适配器不在本包(其 `notify` 链式包装语义复杂,破坏用户配置风险高,单独立包)。

## S8-D 处理器可执行文件 `AiResume.Hook`

新建控制台项目 `csharp/src/AiResume.Hook`(`net10.0-windows`,`OutputType=Exe`,引用 `AiResume.Worker`)。
它就是 §S8-A 中 `Enable(hookCommand)` 所指向的目标——**取代原批处理方案**(批处理无法可靠解析 JSON)。

### 调用形态

```
AiResume.Hook.exe <source>       # source: qoder | opencode | claudecode | cline
```
事件负载从 **stdin** 读入(JSON);部分 provider 另经环境变量提供上下文。

### 处理流程(顺序不可调换)

1. **绝不阻断宿主**:整个 `Main` 包在 try/catch 中,**任何异常都以 exit code 0 结束**;
   诊断信息只写 stderr,不写 stdout(stdout 可能被宿主当作协议数据解析)。
2. **`stop_hook_active` 闸门**:stdin JSON 顶层若存在布尔 `stop_hook_active` 且为 `true`,
   立即 exit 0 且**不写任何事件**。这是 Qoder/ClaudeCode 的防循环要求。
3. **内部运行抑制**:环境变量 `AI_RESUME_INTERNAL_RUN == "1"` 时立即 exit 0 且不写事件
   (AI Resume 自己发起的探测/续跑不得触发通知,沿用现役语义)。
4. 提取字段(缺失即为 null,不报错):
   - `sessionId`:stdin 的 `session_id`/`sessionId`,回退环境变量 `QODER_SESSION_ID`;
   - `cwd`:stdin 的 `cwd`,回退 `QODER_CWD`;
   - `transcriptPath`:stdin 的 `transcript_path`/`transcriptPath`。
5. **稳定事件 ID**(去重基础):对 `source + sessionId + cwd + transcriptPath 的最后写入时间`
   取 SHA256 前 16 位十六进制。同一次任务结束重复触发必须得到相同 id。
   transcript 文件不存在时该分量记为空串。
6. **写事件队列**:落 `ShadowPaths.Root\completion-events\<eventId>.json`,内容至少包含
   `{ eventId, source, sessionId, cwd, transcriptPath, atUtc }`。
   **同 id 已存在则不重写**(幂等)。临时文件 + 原子替换。

### 约束

- 仅用 BCL 与 `System.Text.Json`;不新增 NuGet。
- 不读生产 AppDir,不读任何密钥;stdout 保持干净。
- 单文件实现即可(`Program.cs`),逻辑抽成可测试的静态方法(见测试要求)。

### 测试要求

`HookHandlerTests`:`stop_hook_active=true` 不产出事件;`AI_RESUME_INTERNAL_RUN=1` 不产出事件;
正常负载产出事件且字段正确;**同一负载两次调用得到同一 eventId 且只留一个文件**;
stdin 为空或非法 JSON 时不抛异常且不产出事件;字段缺失时不抛异常。
全部使用临时目录,禁止触碰真实 shadow 目录。

## S8-E `ClaudeCodeNotificationAdapter`

与已交付的 `QoderNotificationAdapter` **结构同构**:配置文件 `~/.claude/settings.json`,
同样是 `hooks.Stop` 数组,同样的合并/精确移除/备份/原子写要求。差异仅在默认路径与 `Kind`。
所有权标记沿用命令中包含固定文件名的判定方式(常量 `MarkerFileName = "AiResume.Hook.exe"`)。

## S8-F `ClineNotificationAdapter`

Cline 的边界是 hooks 目录下的 `TaskComplete.ps1` 脚本文件(不是 JSON 配置)。语义照现役实现:

- 构造可注入 hooks 目录(默认 `%USERPROFILE%\Documents\Cline\Hooks`,测试须能指定临时路径);
- **所有权标记**:脚本首行注释含 `AI Resume managed completion hook`;
- `Enable`:若 `TaskComplete.ps1` 已存在且**不含**标记,先复制为 `TaskComplete.ai-resume-previous.ps1`
  (保留用户原有 hook);随后写入 wrapper 脚本(UTF-8 **带 BOM**,CRLF 换行);
- wrapper 行为:读 stdin → 若 previous 脚本存在则先执行它并透传其退出码与输出;
  previous 返回 JSON 中 `cancel` 为 true 时**不**调用我方处理器;否则调用
  `AiResume.Hook.exe cline` 并把 stdin 传入;最终输出 previous 的输出或 `{"cancel":false}`;
- `Disable`:若当前脚本含我方标记,则用 previous 备份还原(存在时)或删除脚本;不含标记则不动。

### 测试要求

未安装/已安装探测;`Enable` 保留用户原脚本为 previous;重复 `Enable` 幂等;
`Disable` 能还原用户原脚本;对**不含标记**的既有脚本执行 `Disable` 时不得删除它。
全部使用临时目录。

## 实现记录(2026-08-06)

S8-D/E/F 与 GUI 接入已完成。交付:

| 件 | 文件 | 说明 |
|---|---|---|
| S8-D | `csharp/src/AiResume.Hook/`(csproj + Program.cs) | 独立控制台 exe;逻辑抽为 `ShouldSuppress`/`ComputeEventId`/`TryWriteEvent` 三个静态方法以便单测 |
| S8-E | `ClaudeCodeNotificationAdapter.cs` | 与 Qoder 同构,标记 `AiResume.Hook.exe` |
| S8-F | `ClineNotificationAdapter.cs` | 脚本文件型;previous 备份/还原;UTF-8 BOM + CRLF |
| 装配 | `NotificationRegistry` 默认集合 | 装配 4 个适配器(Codex 除外) |
| GUI | `ControlPlaneBridge` + `wwwroot/index.html` | `notifications.list` / `notifications.setEnabled`;面板改为真实开关,未安装项禁用 |

**Codex 适配器仍未实现**,原因不是遗漏而是风险:其 `notify` 是 TOML 单行数组且现役实现需要
**链式包装用户既有 notify**(`--previous-notify`),还要识别 Codex Desktop wrapper、拒绝 batch 链。
误改会破坏用户的 Codex 配置,须单独立包并配套更充分的回归。GUI 上该项显示为「待接入」。

**hookCommand 形态**:`<AppContext.BaseDirectory>\AiResume.Hook.exe <source>`,
source 取 `claudecode` / `cline` / `qoder` / `opencode`;exe 不存在时 GUI 侧报错而非静默写入无效配置。

### 委托与审查记录

本阶段 8 个 deepseek 任务(4 实现 + 4 测试)。监督者修正 4 处,其中 2 处属**规格/参考导致**而非编码失误:

1. 批处理解析 JSON 的选型错误(见 S8-A/B/C 提交说明)——规格前提错误;
2. 照 Qoder 测试改写 ClaudeCode 测试时,`TestHookCommand` 常量被原样照抄,
   不含新适配器的标记 `AiResume.Hook.exe`,导致 3 个断言全灭——**参考实现里与身份相关的常量必须逐个点名要求替换**;
3. `out` 变量声明在 lambda 内却在其外使用(作用域错误);
4. 缺 `using System.Text.Json.Nodes`。
