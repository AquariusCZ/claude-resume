using System.Text.Json;

namespace AiResume.Wrapper;

/// <summary>会话类别(对齐 session-manager.js 的 kind 分类)。</summary>
public enum CcSessionKind
{
    /// <summary>飞书闲聊:参与 14/30 天自动清理。</summary>
    Chat,

    /// <summary>只读查询:参与 14/30 天自动清理。</summary>
    Query,

    /// <summary>项目工作会话:绝不自动归档或删除(现役红线)。</summary>
    Work,
}

/// <summary>cc-connect 会话引用(session_key=platform:chat_id:user_id 稳定路由,S4-B 实证)。</summary>
public sealed record CcSessionRef(
    string Project,
    string SessionKey,
    CcSessionKind Kind,
    DateTimeOffset LastUserActivity,
    int HistoryLength = 0);

/// <summary>
/// cc-connect 管理 API 会话端点抽象(S4-D 实证:GET /api/v1/projects/{project}/sessions/{session},
/// 管理端口 token 认证;端点无正式文档,实现必须固定已验证形状,升级后复验)。
/// </summary>
public interface ICcConnectSessionClient
{
    /// <summary>列出项目会话;网络/解析失败必须抛异常(fail-closed,不得冒充空列表)。</summary>
    Task<IReadOnlyList<CcSessionRef>> ListSessionsAsync(string project, CancellationToken cancellationToken = default);

    /// <summary>读取单个会话(照抄 S4 实证形状:version/sessions/active_session/user_sessions/user_meta/history)。</summary>
    Task<JsonDocument?> GetSessionAsync(string project, string sessionKey, CancellationToken cancellationToken = default);

    /// <summary>归档会话(cc-connect 无原生归档语义时由实现方记录影子归档)。</summary>
    Task ArchiveSessionAsync(string project, string sessionKey, CancellationToken cancellationToken = default);

    /// <summary>删除会话(仅允许 chat/query;work 调用方必须先行拦截)。</summary>
    Task DeleteSessionAsync(string project, string sessionKey, CancellationToken cancellationToken = default);
}

/// <summary>清理策略(对齐 session-manager.js config() 的下限与缺省)。</summary>
public sealed record CleanupPolicy(int ArchiveDays = 14, int DeleteDays = 30, int IntervalHours = 6)
{
    public static CleanupPolicy Normalize(int archiveDays, int deleteDays, int intervalHours) => new(
        ArchiveDays: Math.Max(1, archiveDays),
        DeleteDays: Math.Max(1, deleteDays),
        IntervalHours: Math.Max(1, intervalHours));
}

/// <summary>单次清理动作。</summary>
public enum CleanupAction
{
    /// <summary>未到阈值,保留。</summary>
    Keep,

    /// <summary>达到归档阈值(未达删除阈值):归档。</summary>
    Archive,

    /// <summary>达到删除阈值:永久删除。</summary>
    Delete,

    /// <summary>项目工作会话:跳过(绝不自动清理)。</summary>
    ProtectedWork,
}

/// <summary>清理结果汇总。</summary>
public sealed record CleanupSummary(int Archived, int Deleted, int Skipped, int Protected)
{
    public static CleanupSummary Empty => new(0, 0, 0, 0);
}

/// <summary>
/// S6-B cc-connect 会话生命周期桥(对齐 `src/session-manager.js`):
/// - 清理只看 chat/query,按 LastUserActivity(对应现役 updatedAt)计算年龄;
/// - age ≥ deleteDays → 永久删除;archiveDays ≤ age &lt; deleteDays → 归档;否则保留(与 cleanup() 的
///   if/else if 顺序逐项一致);
/// - 项目工作会话(work)绝不自动归档或删除,只能由用户在 GUI「会话」窗口手动操作;
/// - 读取失败 fail-closed:不得以空列表冒充「没有会话」而继续清理(现役红线);
/// - 6 小时扫描节奏由调用方按 <see cref="CleanupPolicy.IntervalHours"/> 调度,桥本身不内建定时器。
/// </summary>
public sealed class CcConnectSessionBridge
{
    private readonly ICcConnectSessionClient _client;
    private readonly CleanupPolicy _policy;

    public CcConnectSessionBridge(ICcConnectSessionClient client, CleanupPolicy? policy = null)
    {
        ArgumentNullException.ThrowIfNull(client);
        _client = client;
        _policy = policy ?? new CleanupPolicy();
    }

    public CleanupPolicy Policy => _policy;

    /// <summary>
    /// 读取单个会话(照抄 S4 实证形状);端点报不存在返回 null,
    /// 网络/解析失败直接抛出(fail-closed,不得吞成 null 冒充不存在)。
    /// </summary>
    public Task<JsonDocument?> GetSessionAsyncOrThrow(string project, string sessionKey, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(project);
        ArgumentException.ThrowIfNullOrEmpty(sessionKey);
        return _client.GetSessionAsync(project, sessionKey, cancellationToken);
    }

    /// <summary>纯判定:单会话在当前时间与策略下的清理动作(可单测,不发请求)。</summary>
    public CleanupAction Classify(CcSessionRef session, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (session.Kind == CcSessionKind.Work)
        {
            // 项目工作会话绝不自动归档或删除(AGENTS.md 会话生命周期红线)。
            return CleanupAction.ProtectedWork;
        }

        TimeSpan age = now - session.LastUserActivity;
        if (age >= TimeSpan.FromDays(_policy.DeleteDays))
        {
            return CleanupAction.Delete;
        }

        if (age >= TimeSpan.FromDays(_policy.ArchiveDays))
        {
            return CleanupAction.Archive;
        }

        return CleanupAction.Keep;
    }

    /// <summary>
    /// 扫描项目全部会话并执行清理;任一会话读取失败即整体抛出(fail-closed),
    /// 已执行的幂等动作不回滚(cc-connect 侧删除/归档天然幂等)。
    /// </summary>
    public async Task<CleanupSummary> RunCleanupAsync(string project, DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(project);
        IReadOnlyList<CcSessionRef> sessions = await _client.ListSessionsAsync(project, cancellationToken).ConfigureAwait(false);

        int archived = 0, deleted = 0, skipped = 0, protectedCount = 0;
        foreach (CcSessionRef session in sessions)
        {
            cancellationToken.ThrowIfCancellationRequested();
            switch (Classify(session, now))
            {
                case CleanupAction.Delete:
                    await _client.DeleteSessionAsync(project, session.SessionKey, cancellationToken).ConfigureAwait(false);
                    deleted++;
                    break;

                case CleanupAction.Archive:
                    await _client.ArchiveSessionAsync(project, session.SessionKey, cancellationToken).ConfigureAwait(false);
                    archived++;
                    break;

                case CleanupAction.ProtectedWork:
                    protectedCount++;
                    break;

                default:
                    skipped++;
                    break;
            }
        }

        return new CleanupSummary(archived, deleted, skipped, protectedCount);
    }
}
