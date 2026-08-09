using System.Text.Json;
using AiResume.Storage;
using Microsoft.Data.Sqlite;

namespace AiResume.Worker.Quota;

/// <summary>
/// 最近一次权威额度快照的 SQLite 存储。内容只有窗口名、百分比和重置时间,
/// 不含 OAuth token 或任何 provider 凭据。
/// </summary>
public sealed class QuotaSnapshotStore
{
    private readonly string _databasePath;
    private string? _lastReadFailure;
    private string? _lastWriteFailure;

    public QuotaSnapshotStore(string databasePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        _databasePath = databasePath;
    }

    /// <summary>最近一次脱敏存储故障;不含路径、SQL、凭据或快照正文。</summary>
    public string? LastFailure
    {
        get
        {
            string? read = Volatile.Read(ref _lastReadFailure);
            string? write = Volatile.Read(ref _lastWriteFailure);
            return read is null ? write : write is null ? read : $"{read}；{write}";
        }
    }

    public UsageSnapshot? Load(string provider, string credentialFingerprint)
    {
        if (provider.Length == 0 || credentialFingerprint.Length == 0)
        {
            return null;
        }

        try
        {
            StorageDatabase.Migrate(_databasePath);
            using var connection = StorageDatabase.Open(_databasePath);
            return ReadSnapshot(connection, transaction: null, provider, credentialFingerprint);
        }
        catch (Exception ex)
        {
            // 快照只是显示连续性的补充证据。存储缺失/损坏不能阻断实时探测。
            RecordFailure(ref _lastReadFailure, "读取", ex);
            return null;
        }
    }

    public bool TrySave(UsageSnapshot snapshot, string credentialFingerprint)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (credentialFingerprint.Length == 0 || !IsStructurallyValid(snapshot, snapshot.Provider))
        {
            Volatile.Write(ref _lastWriteFailure, "额度快照写入失败（InvalidSnapshot）");
            return false;
        }

        try
        {
            StorageDatabase.Migrate(_databasePath);
            string json = JsonSerializer.Serialize(snapshot);
            using var connection = StorageDatabase.Open(_databasePath);
            using var transaction = connection.BeginTransaction(
                System.Data.IsolationLevel.Serializable, deferred: false);
            StorageDatabase.Execute(connection, """
                INSERT INTO quota_snapshots(
                    provider, credential_fingerprint, captured_at, snapshot_json, updated_at)
                VALUES ($provider, $fingerprint, $capturedAt, $json, $updatedAt)
                ON CONFLICT(provider, credential_fingerprint) DO UPDATE SET
                    captured_at = $capturedAt,
                    snapshot_json = $json,
                    updated_at = $updatedAt
                WHERE excluded.captured_at >= quota_snapshots.captured_at;
                """, transaction,
                ("$provider", snapshot.Provider),
                ("$fingerprint", credentialFingerprint),
                ("$capturedAt", snapshot.CapturedAt.UtcDateTime.Ticks),
                ("$json", json),
                ("$updatedAt", DateTimeOffset.UtcNow.ToString("o")));
            transaction.Commit();
            Volatile.Write(ref _lastWriteFailure, null);
            return true;
        }
        catch (Exception ex)
        {
            RecordFailure(ref _lastWriteFailure, "写入", ex);
            return false;
        }
    }

    /// <summary>
    /// 在同一个 SQLite IMMEDIATE 事务内读取当前账号快照、合并并写回。
    /// 这是跨 GUI/Worker 进程的 compare-and-merge 边界:后到事务必定看到先提交事务的窗口,
    /// 不会把较新的具体值用基于旧快照生成的 carried 值覆盖。
    /// </summary>
    public bool TryUpdate(
        string provider,
        string credentialFingerprint,
        Func<UsageSnapshot?, UsageSnapshot> update,
        out UsageSnapshot? updatedSnapshot)
    {
        updatedSnapshot = null;
        ArgumentNullException.ThrowIfNull(update);
        if (provider.Length == 0 || credentialFingerprint.Length == 0)
        {
            Volatile.Write(ref _lastWriteFailure, "额度快照写入失败（InvalidIdentity）");
            return false;
        }

        try
        {
            StorageDatabase.Migrate(_databasePath);
            using var connection = StorageDatabase.Open(_databasePath);
            using var transaction = connection.BeginTransaction(
                System.Data.IsolationLevel.Serializable, deferred: false);
            UsageSnapshot? previous = ReadSnapshot(
                connection, transaction, provider, credentialFingerprint);
            UsageSnapshot candidate = update(previous);
            if (!IsStructurallyValid(candidate, provider))
            {
                Volatile.Write(ref _lastWriteFailure, "额度快照写入失败（InvalidSnapshot）");
                return false;
            }

            string json = JsonSerializer.Serialize(candidate);
            StorageDatabase.Execute(connection, """
                INSERT INTO quota_snapshots(
                    provider, credential_fingerprint, captured_at, snapshot_json, updated_at)
                VALUES ($provider, $fingerprint, $capturedAt, $json, $updatedAt)
                ON CONFLICT(provider, credential_fingerprint) DO UPDATE SET
                    captured_at = $capturedAt,
                    snapshot_json = $json,
                    updated_at = $updatedAt;
                """, transaction,
                ("$provider", candidate.Provider),
                ("$fingerprint", credentialFingerprint),
                ("$capturedAt", candidate.CapturedAt.UtcDateTime.Ticks),
                ("$json", json),
                ("$updatedAt", DateTimeOffset.UtcNow.ToString("o")));
            transaction.Commit();
            updatedSnapshot = candidate;
            Volatile.Write(ref _lastReadFailure, null);
            Volatile.Write(ref _lastWriteFailure, null);
            return true;
        }
        catch (Exception ex)
        {
            RecordFailure(ref _lastWriteFailure, "写入", ex);
            return false;
        }
    }

    private UsageSnapshot? ReadSnapshot(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string provider,
        string credentialFingerprint)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT snapshot_json
            FROM quota_snapshots
            WHERE provider = $provider AND credential_fingerprint = $fingerprint;
            """;
        command.Parameters.AddWithValue("$provider", provider);
        command.Parameters.AddWithValue("$fingerprint", credentialFingerprint);
        string? json = command.ExecuteScalar() as string;
        UsageSnapshot? snapshot = string.IsNullOrWhiteSpace(json)
            ? null
            : JsonSerializer.Deserialize<UsageSnapshot>(json);
        if (snapshot is not null && !IsStructurallyValid(snapshot, provider))
        {
            Volatile.Write(ref _lastReadFailure, "额度快照读取失败（InvalidSnapshot）");
            return null;
        }

        Volatile.Write(ref _lastReadFailure, null);
        return snapshot;
    }

    private static void RecordFailure(ref string? target, string operation, Exception exception) => Volatile.Write(
        ref target,
        $"额度快照{operation}失败（{exception.GetType().Name}）");

    private static bool IsStructurallyValid(UsageSnapshot? snapshot, string provider)
    {
        if (snapshot is null ||
            !snapshot.Provider.Equals(provider, StringComparison.Ordinal) ||
            snapshot.Buckets is null)
        {
            return false;
        }

        return snapshot.Buckets.All(bucket =>
            bucket is not null &&
            bucket.Windows is not null &&
            bucket.Windows.All(window => window is not null && window.Name is not null));
    }
}
