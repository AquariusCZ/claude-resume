using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;

namespace AiResume.Worker.Notifications;

/// <summary>支持的 AI 编程工具通知提供程序类型。</summary>
public enum NotificationProviderKind
{
    ClaudeCode,
    Codex,
    Cline,
    Qoder,
    OpenCode
}

/// <summary>只读探测结果。</summary>
public sealed record NotificationProviderStatus(
    NotificationProviderKind Kind,
    string DisplayName,
    bool IsInstalled,        // 本机检测到该工具(配置目录存在)
    bool IsEnabled,          // AI Resume 的通知钩子已安装
    string? ConfigPath,      // 实际配置文件路径(不存在时为 null)
    string? Detail);         // 人类可读说明/异常原因

/// <summary>单个 provider 的适配器。所有实现必须满足安全要求。</summary>
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

/// <summary>
/// 通知提供程序注册表。
/// 负责管理所有适配器的探测、启用和停用操作。
/// 线程安全:所有公开方法均通过锁保证并发安全。
/// </summary>
public sealed class NotificationRegistry
{
    private readonly object _lock = new();
    private readonly IReadOnlyDictionary<NotificationProviderKind, INotificationAdapter> _adapters;

    /// <summary>
    /// 初始化注册表。
    /// </summary>
    /// <param name="adapters">适配器集合;为 null 时使用内置默认集合。</param>
    public NotificationRegistry(IEnumerable<INotificationAdapter>? adapters = null)
    {
        // 默认集合装配全部已实现的适配器(均为无参可构造,内部各自解析默认配置路径)。
        // 5 个 provider 均已核实具备「整个 agent 任务结束」的可靠边界(ADR-0003 §3)。
        var adapterList = adapters?.ToList() ?? new List<INotificationAdapter>
        {
            new ClaudeCodeNotificationAdapter(),
            new CodexNotificationAdapter(),
            new ClineNotificationAdapter(),
            new QoderNotificationAdapter(),
            new OpenCodeNotificationAdapter(),
        };

        _adapters = new ReadOnlyDictionary<NotificationProviderKind, INotificationAdapter>(
            adapterList.ToDictionary(a => a.Kind));
    }

    /// <summary>
    /// 逐个探测所有适配器。
    /// 任一适配器抛异常时,该项降级为 IsInstalled=false 且 Detail 记录异常消息,不影响其余适配器。
    /// </summary>
    /// <returns>所有适配器的探测结果列表。</returns>
    public IReadOnlyList<NotificationProviderStatus> ProbeAll()
    {
        lock (_lock)
        {
            var results = new List<NotificationProviderStatus>(_adapters.Count);
            foreach (var adapter in _adapters.Values)
            {
                try
                {
                    results.Add(adapter.Probe());
                }
                catch (Exception ex)
                {
                    // 降级处理:标记为未安装,记录异常信息
                    results.Add(new NotificationProviderStatus(
                        adapter.Kind,
                        adapter.DisplayName,
                        IsInstalled: false,
                        IsEnabled: false,
                        ConfigPath: null,
                        Detail: $"探测异常: {ex.Message}"));
                }
            }
            return results;
        }
    }

    /// <summary>
    /// 启用或停用指定类型的适配器。
    /// </summary>
    /// <param name="kind">适配器类型。</param>
    /// <param name="enabled">true 为启用,false 为停用。</param>
    /// <param name="hookCommand">启用时使用的 hook 命令;停用时忽略。</param>
    /// <exception cref="InvalidOperationException">找不到对应类型的适配器时抛出。</exception>
    public void SetEnabled(NotificationProviderKind kind, bool enabled, string hookCommand)
    {
        lock (_lock)
        {
            if (!_adapters.TryGetValue(kind, out var adapter))
            {
                throw new InvalidOperationException($"未注册的适配器类型: {kind}");
            }

            if (enabled)
            {
                adapter.Enable(hookCommand);
            }
            else
            {
                adapter.Disable();
            }
        }
    }
}