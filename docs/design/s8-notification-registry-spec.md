# S8 规格:完成通知可配置注册表(5 provider)

> 依据:ADR-0003 §3(完成通知改为用户可开关的适配器注册表)、`CLAUDE.md` 完成通知红线。
> 现役是 Node `install-completion-hooks.js` 硬编码三个 provider;本阶段用 C# 重做为注册表并新增两个。

## 1. 准入红线(不可放宽)

**只接受代表「整个 agent 任务结束」的边界。** 代表单次模型请求/流式分片结束的回调一律拒绝
(`DeepSeek V4 for Copilot Chat` 因此被排除,不得加入注册表)。

## 2. 五个 provider 的真实边界(已核实)

| Kind | 边界机制 | 配置位置 | 关键约束 |
|---|---|---|---|
| `ClaudeCode` | `Stop` hook | `~/.claude/settings.json` → `hooks.Stop` | 现役已有,语义沿用 |
| `Codex` | `notify` | Codex 配置 | 仅顶层持久化 thread 入队;ephemeral/subagent 拒绝 |
| `Cline` | `TaskComplete` | Cline 配置 | 现役已有 |
| `Qoder` | `Stop` hook | `~/.qoder/settings.json` → `hooks.Stop` | **脚本必须检查 stdin 的 `stop_hook_active`,为 true 时立即 exit 0**,否则触发「阻断→重试→再阻断」无限循环 |
| `OpenCode` | 插件 `session.idle` 事件 | `~/.config/opencode/plugins/*.ts` | 插件导出 async 函数,返回 hooks 对象 |

Qoder 的 hook 配置支持用户级/项目级**多级合并且不覆盖**,与现役 Claude Code 合并语义一致。
Qoder 向脚本提供 stdin JSON(`session_id`/`cwd`/`hook_event_name`/`transcript_path`)
与环境变量(`QODER_SESSION_ID`/`QODER_CWD`)。

## 3. 接口契约(三个并行任务共用,签名不得更改)

```csharp
namespace AiResume.Worker.Notifications;

public enum NotificationProviderKind { ClaudeCode, Codex, Cline, Qoder, OpenCode }

/// <summary>只读探测结果。</summary>
public sealed record NotificationProviderStatus(
    NotificationProviderKind Kind,
    string DisplayName,
    bool IsInstalled,        // 本机检测到该工具(配置目录存在)
    bool IsEnabled,          // AI Resume 的通知钩子已安装
    string? ConfigPath,      // 实际配置文件路径(不存在时为 null)
    string? Detail);         // 人类可读说明/异常原因

/// <summary>单个 provider 的适配器。所有实现必须满足 §4 的安全要求。</summary>
public interface INotificationAdapter
{
    NotificationProviderKind Kind { get; }
    string DisplayName { get; }

    /// <summary>只读探测,任何异常都转成 Detail 文本,绝不抛出。</summary>
    NotificationProviderStatus Probe();

    /// <summary>启用:合并写入,保留用户既有配置。已启用时为幂等空操作。</summary>
    void Enable(string hookCommand);

    /// <summary>停用:只移除 AI Resume 自己写入的条目,保留其余。未启用时为幂等空操作。</summary>
    void Disable();
}
```

注册表(单独文件):

```csharp
public sealed class NotificationRegistry
{
    public NotificationRegistry(IEnumerable<INotificationAdapter>? adapters = null);
    public IReadOnlyList<NotificationProviderStatus> ProbeAll();   // 逐个探测,单个失败不影响其余
    public void SetEnabled(NotificationProviderKind kind, bool enabled, string hookCommand);
}
```

## 4. 安全要求(违反即拒收)

1. **绝不覆盖用户既有配置**:`Enable` 必须读取现有 JSON、合并、原子写回;
   同一文件中其他 hook/字段必须逐字保留(包括未知字段与顺序无关的内容)。
2. **可识别所有权**:AI Resume 写入的条目必须带稳定标记(如命令路径中的固定文件名),
   使 `Disable` 能精确移除自己的条目而不误删他人的。
3. **原子写**:临时文件 + flush + 替换;写入前对原文件做一次备份(`.bak`,覆盖式)。
4. **写入失败不得留下半成品**;失败时抛出携带原因的异常由调用方处理。
5. **不写生产 AppDir**;不读任何密钥。
6. `Probe` 全程只读。

## 5. 交付物(三个并行任务)

- **任务 A**:`NotificationRegistry.cs` + `NotificationProviderStatus`/`INotificationAdapter`
  (契约定义 + 注册表实现 + 默认适配器装配)。
- **任务 B**:`QoderNotificationAdapter.cs` —— `~/.qoder/settings.json` 的 `hooks.Stop` 合并写入/移除;
  同时产出 hook 脚本内容常量(**必须包含 `stop_hook_active` 检查并 exit 0**)。
- **任务 C**:`OpenCodeNotificationAdapter.cs` —— 在 `~/.config/opencode/plugins/` 下写入
  独立插件文件 `airesume-notify.ts`(独立文件,不与用户插件同名冲突),监听 `session.idle`;
  `Disable` 删除该文件。

三者互不依赖,均依赖 §3 契约。既有三个 provider(ClaudeCode/Codex/Cline)的适配器留待后续包,
本次只需在注册表中留出装配位。

## 6. 测试要求

每个适配器至少覆盖:未安装时 `Probe` 返回 `IsInstalled=false` 且不抛异常;
`Enable` 后既有配置逐字保留、`IsEnabled=true`;重复 `Enable` 幂等;
`Disable` 只移除自己的条目、保留他人条目;配置文件损坏时不抛异常且不破坏原文件。
所有测试使用系统临时目录,**禁止触碰用户真实 `~/.qoder`、`~/.config/opencode`**。
