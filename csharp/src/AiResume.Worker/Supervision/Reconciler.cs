using System.Text.Json;
using AiResume.Core;
using AiResume.Core.Contracts;
using AiResume.Storage;

namespace AiResume.Worker.Supervision;

/// <summary>对账视角的登记状态(只读报告,不含动作)。</summary>
public enum ReconcileVerdict
{
    /// <summary>非 terminal run 无登记:queued/starting 属正常 pre-spawn,running 属完整性缺口。</summary>
    NotRegistered,

    /// <summary>占位登记未补全(child_pid 未知,恢复流程无法核验)。</summary>
    Placeholder,

    /// <summary>进程存在且特征一致(启动时间 ±5s + 签名)。</summary>
    Matched,

    /// <summary>进程存在但特征明确不符(PID 复用等,禁止据此终止)。</summary>
    Mismatched,

    /// <summary>进程明确不存在(断电后 Job kill-on-close 子进程已死等)。</summary>
    Gone,

    /// <summary>查询失败或特征不可得 → fail-closed,不得据以清理。</summary>
    Unverifiable,
}

/// <summary>单项对账(每个非 terminal run:run ↔ registry ↔ 进程三方)。</summary>
public sealed record ReconcileRunItem(
    string RunId,
    string RunKey,
    string TaskKind,
    string State,
    bool RunKeyCanonical,
    string? RunKeyIssue,
    ReconcileVerdict Verdict,
    bool? ProcessAlive,
    string? Note);

/// <summary>孤儿登记:registry 有行但 runs 无此 run(或该 run 已 terminal),应清未清。</summary>
public sealed record OrphanRegistryItem(string RunId, int? ChildPid, string? StartedAt, string Note);

/// <summary>
/// 三方对账报告(runs ↔ process_registry ↔ 进程 liveness;只读,不修改任何状态)。
/// Status:
/// - consistent:三方一致,无需处置。
/// - attention:孤儿登记、占位未补全、进程 gone 待推进、Unverifiable/Mismatched 需复核。
/// - inconsistent:结构性缺口(running 无登记、runKey 非规范形)。
/// </summary>
public sealed record ReconcileReport(
    DateTimeOffset GeneratedAt,
    string DatabasePath,
    string Status,
    int ActiveRunCount,
    int RunKeyInvalidCount,
    int RegistryPlaceholderCount,
    int OrphanRegistryCount,
    IReadOnlyList<ReconcileRunItem> Runs,
    IReadOnlyList<OrphanRegistryItem> Orphans)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        // 枚举序列化为可读字符串(verdict/status 等),便于对账报告人工复核与机器消费。
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
    };

    /// <summary>结构化 JSON 输出(对账报告落盘/投递形状)。</summary>
    public string ToJson() => JsonSerializer.Serialize(this, JsonOptions);
}

/// <summary>
/// S5-D 对账器:只读三方对账(非 terminal run ↔ process_registry ↔ 进程 liveness),
/// runKey 规范形复验(D-011 复验),registry 完整性(孤儿/占位)检出,
/// 输出结构化 JSON 报告。对账不做任何写操作;恢复处置仍由 ProcessSupervisor.RecoverAsync 授权执行。
/// </summary>
public sealed class Reconciler
{
    private readonly string _databasePath;
    private readonly IProcessProbe _probe;
    private readonly IProcessRegistry _registry;

    public Reconciler(string databasePath, IProcessProbe? probe = null, IProcessRegistry? registry = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        _databasePath = databasePath;
        _probe = probe ?? new NativeProcessProbe();
        _registry = registry ?? new SqliteProcessRegistry(databasePath);
    }

    public ReconcileReport Reconcile()
    {
        List<ActiveRunRow> runs = LoadActiveRuns();
        IReadOnlyList<ProcessRegistryEntry> registryRows = _registry.EnumerateAll();
        HashSet<string> knownRunIds = LoadAllRunIds();
        HashSet<string> activeRunIds = runs.Select(r => r.RunId).ToHashSet();

        var items = new List<ReconcileRunItem>();
        int runKeyInvalid = 0;
        int placeholders = 0;
        bool inconsistent = false;
        bool attention = false;

        foreach (ActiveRunRow run in runs)
        {
            bool canonical = TryValidateRunKey(run.RunKey, out string? runKeyIssue);
            if (!canonical)
            {
                runKeyInvalid++;
                inconsistent = true;
            }

            ProcessRegistryEntry? entry = registryRows.FirstOrDefault(r => r.RunId.ToString() == run.RunId);
            ReconcileVerdict verdict;
            bool? processAlive = null;
            string? note = null;

            if (entry is null)
            {
                verdict = ReconcileVerdict.NotRegistered;
                if (run.State == "running")
                {
                    // running 无登记 = 结构性缺口(进程可能泄漏且无法核验)。
                    inconsistent = true;
                    note = "running 无登记(完整性缺口)";
                }
                else
                {
                    note = "pre-spawn 未登记(正常)";
                }
            }
            else if (entry.ChildPid is null)
            {
                // 占位未补全:无法核验进程(fail-closed 视角)。
                verdict = ReconcileVerdict.Placeholder;
                placeholders++;
                attention = true;
                note = "占位登记未补全(child_pid 未知)";
            }
            else
            {
                ProcessProbeResult probe = _probe.Probe(entry.ChildPid.Value);
                processAlive = probe.Liveness == ProcessLiveness.Alive;
                verdict = probe.Liveness switch
                {
                    ProcessLiveness.Alive => ProcessVerifier.Verify(entry, probe) switch
                    {
                        ProcessVerdict.Matched => ReconcileVerdict.Matched,
                        ProcessVerdict.Mismatched => ReconcileVerdict.Mismatched,
                        _ => ReconcileVerdict.Unverifiable,
                    },
                    ProcessLiveness.Gone => ReconcileVerdict.Gone,
                    _ => ReconcileVerdict.Unverifiable,
                };

                if (verdict == ReconcileVerdict.Mismatched)
                {
                    attention = true;
                    note = "登记特征与进程不符(PID 复用或损坏登记,禁止终止)";
                }
                else if (verdict == ReconcileVerdict.Unverifiable)
                {
                    attention = true;
                    note = "进程查询失败/特征不可得(fail-closed 保留)";
                }
                else if (verdict == ReconcileVerdict.Gone)
                {
                    // 进程明确消失:run 状态未推进(断电等),需恢复流程处置。
                    attention = true;
                    note = "进程已退出但 run 未推进(恢复流程处置)";
                }
            }

            items.Add(new ReconcileRunItem(
                run.RunId, run.RunKey, run.TaskKind, run.State,
                canonical, runKeyIssue, verdict, processAlive, note));
        }

        var orphans = new List<OrphanRegistryItem>();
        foreach (ProcessRegistryEntry entry in registryRows)
        {
            if (activeRunIds.Contains(entry.RunId.ToString()))
            {
                continue;
            }

            string note = knownRunIds.Contains(entry.RunId.ToString())
                ? "run 已 terminal 但登记未清理"
                : "runs 表无此 run(孤儿登记)";
            orphans.Add(new OrphanRegistryItem(
                entry.RunId.ToString(), entry.ChildPid, entry.StartedAt.ToString("o"), note));
            attention = true;
        }

        string status = inconsistent ? "inconsistent" : attention ? "attention" : "consistent";
        return new ReconcileReport(
            DateTimeOffset.UtcNow, _databasePath, status,
            runs.Count, runKeyInvalid, placeholders, orphans.Count, items, orphans);
    }

    /// <summary>
    /// runKey 规范形复验(D-011):runKey 必须由 RunKey.Create 生成,即
    /// 三段式 kind|normalizedPath|openId,kind 为合法 wire code,路径段为规范形
    /// (NormalizeProjectPath 幂等:小写、统一分隔符、无尾分隔符)。
    /// </summary>
    private static bool TryValidateRunKey(string runKey, out string? issue)
    {
        issue = null;
        if (string.IsNullOrWhiteSpace(runKey))
        {
            issue = "empty";
            return false;
        }

        string[] parts = runKey.Split('|');
        if (parts.Length != 3)
        {
            issue = "segment_count_not_3";
            return false;
        }

        if (!IsKnownTaskKind(parts[0]))
        {
            issue = "unknown_task_kind:" + parts[0];
            return false;
        }

        if (parts[1].Length == 0)
        {
            issue = "empty_project_path";
            return false;
        }

        string normalized;
        try
        {
            normalized = RunKey.NormalizeProjectPath(parts[1]);
        }
        catch (ArgumentException)
        {
            issue = "invalid_project_path";
            return false;
        }

        if (!string.Equals(normalized, parts[1], StringComparison.Ordinal))
        {
            issue = "non_canonical_project_path";
            return false;
        }

        return true;
    }

    private static bool IsKnownTaskKind(string kind) =>
        kind is "chat" or "query" or "modify" or "resume" or "probe";

    private sealed record ActiveRunRow(string RunId, string RunKey, string TaskKind, string State);

    private List<ActiveRunRow> LoadActiveRuns()
    {
        var result = new List<ActiveRunRow>();
        using var connection = StorageDatabase.Open(_databasePath);
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT run_id, run_key, task_kind, state FROM runs
            WHERE state IN ('queued','starting','running') ORDER BY created_at;
            """;
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            result.Add(new ActiveRunRow(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3)));
        }

        return result;
    }

    private HashSet<string> LoadAllRunIds()
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using var connection = StorageDatabase.Open(_databasePath);
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT run_id FROM runs;";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            result.Add(reader.GetString(0));
        }

        return result;
    }
}
