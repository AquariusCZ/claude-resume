namespace AiResume.Wrapper;

/// <summary>用户访问级别(映射 feishuAuthOpenIds 三态语义)。</summary>
public enum AccessLevel
{
    /// <summary>未授权:只能旁观,不得驱动任何任务。</summary>
    None,

    /// <summary>只读:查询/闲聊可,全部文件工具禁用(现役红线)。</summary>
    Viewer,

    /// <summary>可修改项目(对应 feishuAuthOpenIds full)。</summary>
    Owner,
}

/// <summary>
/// S6-B 授权映射结果:
/// - Level 为入口鉴权结论;
/// - FileToolsAllowed 是「非 owner 的查询/闲聊必须禁全部文件工具」红线的显式投影,
///   调用方必须在注入 provider 前校验此字段,不得只看 Level;
/// - Warning 承载「名单为空 = 未锁定(所有人可改)」的现役警告语义。
/// </summary>
public sealed record AuthDecision(AccessLevel Level, bool FileToolsAllowed, string? Warning);

/// <summary>
/// S6-B feishuAuthOpenIds → cc-connect 入口鉴权映射:
/// - cc-connect 无角色模型(S4-D 实证),授权由 wrapper 自有层承担;
/// - allow_from 只收敛「谁能进」,owner/viewer 三态由本映射判定;
/// - 空名单(owners 与 viewers 均空)= 未锁定,所有入站用户按 Owner 语义放行并携带警告,
///   与现役 `feishuAuthOpenIds` 为空的行为一致(AGENTS.md 安全约束)。
/// </summary>
public static class CcConnectAuthMapper
{
    /// <summary>
    /// 判定入站用户访问级别。owners 与 viewers 交集时 owner 优先(full 语义覆盖只读)。
    /// </summary>
    public static AuthDecision Resolve(string openId, IReadOnlyCollection<string> owners, IReadOnlyCollection<string> viewers)
    {
        ArgumentNullException.ThrowIfNull(owners);
        ArgumentNullException.ThrowIfNull(viewers);

        bool unlocked = owners.Count == 0 && viewers.Count == 0;
        string? warning = unlocked
            ? "授权名单为空=未锁定:所有用户均可修改项目(现役 feishuAuthOpenIds 空语义)。"
            : null;

        if (unlocked)
        {
            return new AuthDecision(AccessLevel.Owner, FileToolsAllowed: true, warning);
        }

        if (ContainsId(owners, openId))
        {
            return new AuthDecision(AccessLevel.Owner, FileToolsAllowed: true, warning);
        }

        if (ContainsId(viewers, openId))
        {
            // viewer:可查询/闲聊,但必须禁全部文件工具(plan 模式拦不住读,现役已实测)。
            return new AuthDecision(AccessLevel.Viewer, FileToolsAllowed: false, warning);
        }

        return new AuthDecision(AccessLevel.None, FileToolsAllowed: false, warning);
    }

    /// <summary>
    /// 生成 cc-connect `allow_from` 白名单:owners ∪ viewers 有序去重;
    /// 空名单 = 不设置(全允许,与「未锁定」语义一致)。
    /// </summary>
    public static IReadOnlyList<string> BuildAllowFrom(IReadOnlyCollection<string> owners, IReadOnlyCollection<string> viewers)
    {
        ArgumentNullException.ThrowIfNull(owners);
        ArgumentNullException.ThrowIfNull(viewers);

        var result = new List<string>(owners.Count + viewers.Count);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (string id in owners.Concat(viewers))
        {
            if (!string.IsNullOrWhiteSpace(id) && seen.Add(id))
            {
                result.Add(id);
            }
        }

        return result;
    }

    /// <summary>移除最后一个 full 用户(owner)会解锁:返回是否从锁定态变为未锁定。</summary>
    public static bool RemovingOwnerUnlocks(IReadOnlyCollection<string> owners, string removeId)
    {
        ArgumentNullException.ThrowIfNull(owners);
        return owners.Count == 1 && ContainsId(owners, removeId);
    }

    private static bool ContainsId(IReadOnlyCollection<string> ids, string openId) =>
        !string.IsNullOrWhiteSpace(openId) && ids.Contains(openId);
}
