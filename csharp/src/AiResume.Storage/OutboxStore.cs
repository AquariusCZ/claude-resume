using System.Text.Json;
using AiResume.Core.Events;
using Microsoft.Data.Sqlite;

namespace AiResume.Storage;

/// <summary>
/// Completion outbox(SQLite,at-least-once)。幂等键唯一:同 idempotency_key 重复
/// enqueue 无副作用;投递失败只递增 attempts,绝不回写 AI run 的 terminal。
/// </summary>
public sealed class OutboxStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly string _databasePath;

    public OutboxStore(string databasePath)
    {
        _databasePath = databasePath;
    }

    /// <summary>幂等入队;返回条目 outboxId(已存在时返回既有条目的 id)。</summary>
    public Task<Guid> EnqueueAsync(EventEnvelopeV1 envelope, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        if (string.IsNullOrWhiteSpace(envelope.IdempotencyKey))
        {
            throw new ArgumentException("outbox 条目必须携带稳定 idempotency_key。", nameof(envelope));
        }

        cancellationToken.ThrowIfCancellationRequested();
        using var connection = StorageDatabase.Open(_databasePath);
        using var tx = connection.BeginTransaction(System.Data.IsolationLevel.Serializable, deferred: false);
        var outboxId = Guid.NewGuid();
        string now = DateTimeOffset.UtcNow.ToString("o");
        StorageDatabase.Execute(connection, """
            INSERT OR IGNORE INTO outbox (outbox_id, idempotency_key, envelope_json, state, attempts, created_at, updated_at)
            VALUES ($id, $key, $json, 'pending', 0, $now, $now);
            """, tx,
            ("$id", outboxId.ToString("D")),
            ("$key", envelope.IdempotencyKey),
            ("$json", JsonSerializer.Serialize(envelope, JsonOptions)),
            ("$now", now));

        using (var cmd = connection.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = "SELECT outbox_id FROM outbox WHERE idempotency_key = $key;";
            cmd.Parameters.AddWithValue("$key", envelope.IdempotencyKey);
            outboxId = Guid.Parse((string)cmd.ExecuteScalar()!);
        }

        tx.Commit();
        return Task.FromResult(outboxId);
    }

    /// <summary>投递回执:成功置 delivered;失败仅递增 attempts,条目保持可重试。</summary>
    public Task AckAsync(Guid outboxId, bool delivered, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var connection = StorageDatabase.Open(_databasePath);
        using var tx = connection.BeginTransaction(System.Data.IsolationLevel.Serializable, deferred: false);
        string now = DateTimeOffset.UtcNow.ToString("o");
        StorageDatabase.Execute(connection, delivered
                ? "UPDATE outbox SET state = 'delivered', updated_at = $now WHERE outbox_id = $id;"
                : "UPDATE outbox SET attempts = attempts + 1, updated_at = $now WHERE outbox_id = $id;",
            tx, ("$id", outboxId.ToString("D")), ("$now", now));
        tx.Commit();
        return Task.CompletedTask;
    }
}
