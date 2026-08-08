using AiResume.Core;
using AiResume.Storage;
using Microsoft.Data.Sqlite;

namespace AiResume.Worker.Supervision;

/// <summary>process_registry 表行。</summary>
public sealed record ProcessRegistryEntry(
    RunId RunId,
    int ParentPid,
    int? ChildPid,
    string? JobId,
    DateTimeOffset StartedAt,
    string CommandSignature,
    DateTimeOffset UpdatedAt);

/// <summary>
/// durable registry 访问接口。注入点:测试用 FakeRegistry 模拟"首次登记失败"。
/// 写入契约(安全关键):InsertPlaceholder 必须先于 spawn 且事务提交成功;
/// Complete 在 spawn 后补全 child_pid/真实启动时间/签名;Delete 为失败路径清理。
/// </summary>
public interface IProcessRegistry
{
    void InsertPlaceholder(RunId runId, int parentPid, string jobId, string commandSignature);

    void Complete(RunId runId, int childPid, DateTimeOffset startedAt, string commandSignature);

    ProcessRegistryEntry? Get(RunId runId);

    void Delete(RunId runId);

    IReadOnlyList<ProcessRegistryEntry> EnumerateAll();
}

/// <summary>
/// SQLite 实现:复用 S2-B 的 process_registry 表与 StorageDatabase(WAL + busy_timeout)。
/// 所有写走 BEGIN IMMEDIATE 单 writer 事务;占位行 child_pid/job_id 允许为 NULL(schema 兼容)。
/// </summary>
public sealed class SqliteProcessRegistry : IProcessRegistry
{
    private readonly string _databasePath;

    public SqliteProcessRegistry(string databasePath)
    {
        _databasePath = databasePath;
    }

    public void InsertPlaceholder(RunId runId, int parentPid, string jobId, string commandSignature)
    {
        using var connection = StorageDatabase.Open(_databasePath);
        using var tx = connection.BeginTransaction(System.Data.IsolationLevel.Serializable, deferred: false);
        string now = DateTimeOffset.UtcNow.ToString("o");
        StorageDatabase.Execute(connection, """
            INSERT INTO process_registry (run_id, parent_pid, child_pid, job_id, started_at, command_signature, updated_at)
            VALUES ($run_id, $parent_pid, NULL, $job_id, $started_at, $signature, $updated_at);
            """, tx,
            ("$run_id", runId.ToString()),
            ("$parent_pid", parentPid),
            ("$job_id", jobId),
            ("$started_at", now),
            ("$signature", commandSignature),
            ("$updated_at", now));
        tx.Commit();
    }

    public void Complete(RunId runId, int childPid, DateTimeOffset startedAt, string commandSignature)
    {
        using var connection = StorageDatabase.Open(_databasePath);
        using var tx = connection.BeginTransaction(System.Data.IsolationLevel.Serializable, deferred: false);
        StorageDatabase.Execute(connection, """
            UPDATE process_registry
            SET child_pid = $child_pid, started_at = $started_at, command_signature = $signature, updated_at = $updated_at
            WHERE run_id = $run_id;
            """, tx,
            ("$child_pid", childPid),
            ("$started_at", startedAt.ToString("o")),
            ("$signature", commandSignature),
            ("$updated_at", DateTimeOffset.UtcNow.ToString("o")),
            ("$run_id", runId.ToString()));
        tx.Commit();
    }

    public ProcessRegistryEntry? Get(RunId runId)
    {
        using var connection = StorageDatabase.Open(_databasePath);
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT run_id, parent_pid, child_pid, job_id, started_at, command_signature, updated_at
            FROM process_registry WHERE run_id = $run_id;
            """;
        cmd.Parameters.AddWithValue("$run_id", runId.ToString());
        using var reader = cmd.ExecuteReader();
        if (!reader.Read())
        {
            return null;
        }

        return new ProcessRegistryEntry(
            RunId.FromString(reader.GetString(0)),
            reader.GetInt32(1),
            reader.IsDBNull(2) ? null : reader.GetInt32(2),
            reader.IsDBNull(3) ? null : reader.GetString(3),
            DateTimeOffset.Parse(reader.GetString(4)),
            reader.GetString(5),
            DateTimeOffset.Parse(reader.GetString(6)));
    }

    public void Delete(RunId runId)
    {
        using var connection = StorageDatabase.Open(_databasePath);
        using var tx = connection.BeginTransaction(System.Data.IsolationLevel.Serializable, deferred: false);
        StorageDatabase.Execute(connection, "DELETE FROM process_registry WHERE run_id = $run_id;", tx,
            ("$run_id", runId.ToString()));
        tx.Commit();
    }

    public IReadOnlyList<ProcessRegistryEntry> EnumerateAll()
    {
        var result = new List<ProcessRegistryEntry>();
        using var connection = StorageDatabase.Open(_databasePath);
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT run_id, parent_pid, child_pid, job_id, started_at, command_signature, updated_at
            FROM process_registry;
            """;
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            result.Add(new ProcessRegistryEntry(
                RunId.FromString(reader.GetString(0)),
                reader.GetInt32(1),
                reader.IsDBNull(2) ? null : reader.GetInt32(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                DateTimeOffset.Parse(reader.GetString(4)),
                reader.GetString(5),
                DateTimeOffset.Parse(reader.GetString(6))));
        }

        return result;
    }
}
