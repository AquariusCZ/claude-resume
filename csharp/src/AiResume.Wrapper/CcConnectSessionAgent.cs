using System.Text.Json;

namespace AiResume.Wrapper;

/// <summary>当前会话绑定的 agent,与配置里的 agent 是否一致。</summary>
public sealed record SessionAgentState(
    string? ActiveSessionId,
    string? SessionAgent,
    string? ConfigAgent,
    bool Mismatch,
    string Summary);

/// <summary>
/// 「我换了 agent,为什么还是原来那个模型」的真正答案。
///
/// **cc-connect 把 agent 钉在会话上,不是钉在配置上。**
/// 会话文件(<c>~/.cc-connect/sessions/&lt;项目&gt;_&lt;哈希&gt;.json</c>)里每条会话都带一个
/// <c>agent_type</c>,那是**创建那一刻**从配置抄下来的;此后改配置、重启 cc-connect
/// 都不会回头改它。
///
/// 2026-08-08 实测:配置 <c>type = "codex"</c>,而用户正在聊的会话 s6 建于 04:16、
/// <c>agent_type = "claudecode"</c>。于是
/// <list type="bullet">
/// <item><c>/model</c> 列的是 Claude Code 的四个内置模型;</item>
/// <item><c>/provider list</c> 只剩 deepseek —— 声明了 <c>agent_types = ["codex"]</c> 的
///       provider 在 claudecode 会话里被过滤掉了。</item>
/// </list>
/// 面板当时只说「切换需重启 cc-connect」—— **那句话是错的**,重启不够,
/// 必须 <c>/new</c> 开一条新会话。一句半对的提示比没有提示更能把人带偏,
/// 因为它让人以为自己已经照做了。
/// </summary>
public static class CcConnectSessionAgent
{
    /// <summary>会话目录。与 cc-connect 的默认布局一致。</summary>
    public static string DefaultSessionsDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".cc-connect", "sessions");

    /// <summary>
    /// 纯判定。<paramref name="sessionAgent"/> 为 null 表示会话没记 agent
    /// (老会话/刚建的),那种情况**不报不一致** —— 没记就说明它跟着配置走。
    /// </summary>
    public static SessionAgentState Evaluate(
        string? activeSessionId, string? sessionAgent, string? configAgent)
    {
        bool mismatch =
            !string.IsNullOrWhiteSpace(sessionAgent) &&
            !string.IsNullOrWhiteSpace(configAgent) &&
            !sessionAgent.Equals(configAgent, StringComparison.OrdinalIgnoreCase);

        string summary = mismatch
            ? $"配置里的 agent 是「{configAgent}」,但当前会话建于「{sessionAgent}」—— " +
              "cc-connect 把 agent 钉在会话上,改配置和重启都不会改已有会话。" +
              "在聊天里发 /new 开一条新会话才会用上新 agent(provider 与模型列表也才会跟着变)。"
            : "当前会话与配置的 agent 一致。";

        return new SessionAgentState(activeSessionId, sessionAgent, configAgent, mismatch, summary);
    }

    /// <summary>
    /// 从会话文件读出 (活动会话 id, 它的 agent_type)。读不出来返回 (null, null) —— 不猜。
    /// </summary>
    public static (string? ActiveId, string? Agent) ReadActive(string sessionsJson)
    {
        try
        {
            using JsonDocument doc = JsonDocument.Parse(sessionsJson);
            JsonElement root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return (null, null);
            }

            string? activeId = root.TryGetProperty("active_session", out JsonElement a) &&
                               a.ValueKind == JsonValueKind.String
                ? a.GetString()
                : null;

            if (!root.TryGetProperty("sessions", out JsonElement sessions) ||
                sessions.ValueKind != JsonValueKind.Object)
            {
                return (activeId, null);
            }

            // active_session 缺失时退而求其次:取 updated_at 最新的那条。
            // 那是用户最后说话的地方,也就是他此刻问"为什么还是老模型"的那条。
            JsonElement? target = null;
            if (activeId is { Length: > 0 } &&
                sessions.TryGetProperty(activeId, out JsonElement byId))
            {
                target = byId;
            }
            else
            {
                string newest = string.Empty;
                foreach (JsonProperty s in sessions.EnumerateObject())
                {
                    if (s.Value.ValueKind != JsonValueKind.Object)
                    {
                        continue;
                    }

                    string updated = s.Value.TryGetProperty("updated_at", out JsonElement u) &&
                                     u.ValueKind == JsonValueKind.String
                        ? u.GetString() ?? string.Empty
                        : string.Empty;

                    if (string.CompareOrdinal(updated, newest) > 0)
                    {
                        newest = updated;
                        target = s.Value;
                        activeId ??= s.Name;
                    }
                }
            }

            if (target is not { } t)
            {
                return (activeId, null);
            }

            string? agent = t.TryGetProperty("agent_type", out JsonElement at) &&
                            at.ValueKind == JsonValueKind.String
                ? at.GetString()
                : null;

            return (activeId, string.IsNullOrWhiteSpace(agent) ? null : agent);
        }
        catch (JsonException)
        {
            return (null, null);
        }
    }

    /// <summary>读真实会话目录并与配置对账。任何异常都降级成「核对不了」,不报不一致。</summary>
    public static SessionAgentState Read(string? configAgent, string? sessionsDir = null)
    {
        string dir = sessionsDir ?? DefaultSessionsDir;
        try
        {
            if (!Directory.Exists(dir))
            {
                return Evaluate(null, null, configAgent);
            }

            // 一个项目一个文件;取最近改动的那个 —— 用户正在用的就是它。
            string? newest = Directory.EnumerateFiles(dir, "*.json")
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .FirstOrDefault();

            if (newest is null)
            {
                return Evaluate(null, null, configAgent);
            }

            (string? id, string? agent) = ReadActive(File.ReadAllText(newest));
            return Evaluate(id, agent, configAgent);
        }
        catch (Exception)
        {
            return Evaluate(null, null, configAgent);
        }
    }
}
