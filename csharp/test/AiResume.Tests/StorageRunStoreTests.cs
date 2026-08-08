using AiResume.Core;
using AiResume.Core.Contracts;
using AiResume.Core.Events;
using AiResume.Storage;
using Microsoft.Data.Sqlite;
using Xunit;

namespace AiResume.Tests;

/// <summary>
/// S2-B Storage 出口门禁:WAL/迁移幂等、Start/append/outbox 幂等 ×100、
/// runKey 并发所有权、锁竞争 busy 重试、Cancel 状态机语义。
/// </summary>
[Collection(SqliteCollection.Name)]
public sealed class StorageRunStoreTests : IDisposable
{
    private readonly string _dir;
    private readonly string _dbPath;

    public StorageRunStoreTests()
    {
        _dir = TestTemp.NewDir("airesume-storage-tests");
        Directory.CreateDirectory(_dir);
        _dbPath = Path.Combine(_dir, "runs.db");
        StorageDatabase.Migrate(_dbPath);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); } catch { /* 测试临时目录残留可容忍 */ }
    }

    private static StartRequest NewStart(string runKey, Guid? requestId = null) => new()
    {
        RequestId = requestId ?? Guid.NewGuid(),
        RunKey = runKey,
        TaskKind = TaskKind.Query,
        ProfileId = "profile-a",
        InputRef = "input-ref-1",
    };

    [Fact]
    public void Migrator_is_idempotent_and_wal_is_active()
    {
        StorageDatabase.Migrate(_dbPath);
        StorageDatabase.Migrate(_dbPath);

        using var connection = StorageDatabase.Open(_dbPath);
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "PRAGMA journal_mode;";
        Assert.Equal("wal", (string)cmd.ExecuteScalar()!);

        cmd.CommandText = "SELECT COUNT(*) FROM schema_version;";
        Assert.Equal(StorageDatabase.CurrentSchemaVersion, (long)cmd.ExecuteScalar()!);
    }

    [Fact]
    public async Task Start_with_same_request_id_100_times_keeps_one_row()
    {
        var store = new RunStore(_dbPath);
        var requestId = Guid.NewGuid();
        var first = await store.StartAsync(NewStart("query|c:\\p1|ou_1", requestId), CancellationToken.None);
        Assert.True(first.Accepted);
        Assert.False(first.Existing);

        for (int i = 0; i < 99; i++)
        {
            var replay = await store.StartAsync(NewStart("query|c:\\p1|ou_1", requestId), CancellationToken.None);
            Assert.True(replay.Accepted);
            Assert.True(replay.Existing);
            Assert.Equal(first.RunId, replay.RunId);
        }

        Assert.Equal(1L, CountRows("runs"));
    }

    [Fact]
    public void Append_event_same_run_and_seq_100_times_keeps_one_row()
    {
        var store = new RunStore(_dbPath);
        var runId = RunId.New();
        var envelope = new EventEnvelopeV1
        {
            EventId = Guid.NewGuid(),
            Type = "task.progress",
            Source = "worker",
            Ts = 1,
            IdempotencyKey = runId + "|7",
            RunId = runId.Value,
            Seq = 7,
        };

        int inserted = 0;
        for (int i = 0; i < 100; i++)
        {
            if (store.TryAppendEvent(runId, 7, envelope)) inserted++;
        }

        Assert.Equal(1, inserted);
        Assert.Equal(1L, CountRows("run_events"));
    }

    [Fact]
    public async Task Outbox_same_idempotency_key_100_times_keeps_one_row()
    {
        var outbox = new OutboxStore(_dbPath);
        var envelope = new EventEnvelopeV1
        {
            EventId = Guid.NewGuid(),
            Type = "outbox.delivery",
            Source = "worker",
            Ts = 1,
            IdempotencyKey = "completion:evt-42",
        };

        var firstId = await outbox.EnqueueAsync(envelope, CancellationToken.None);
        for (int i = 0; i < 99; i++)
        {
            Assert.Equal(firstId, await outbox.EnqueueAsync(envelope, CancellationToken.None));
        }

        Assert.Equal(1L, CountRows("outbox"));

        // 失败回执只递增 attempts,成功置 delivered;都不影响幂等键唯一性。
        await outbox.AckAsync(firstId, delivered: false, CancellationToken.None);
        await outbox.AckAsync(firstId, delivered: true, CancellationToken.None);
        using var connection = StorageDatabase.Open(_dbPath);
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT state, attempts FROM outbox;";
        using var reader = cmd.ExecuteReader();
        Assert.True(reader.Read());
        Assert.Equal("delivered", reader.GetString(0));
        Assert.Equal(1L, reader.GetInt64(1));
    }

    [Fact]
    public async Task Run_key_busy_rejects_and_pre_spawn_cancel_releases()
    {
        var store = new RunStore(_dbPath);
        var first = await store.StartAsync(NewStart("modify|c:\\p2|ou_1"), CancellationToken.None);
        Assert.True(first.Accepted);

        var second = await store.StartAsync(NewStart("modify|c:\\p2|ou_1"), CancellationToken.None);
        Assert.False(second.Accepted);
        Assert.Equal(ConflictKind.RunKeyBusy, second.Conflict);
        Assert.Equal(first.RunId, second.OccupyingRunId);

        var cancel = await store.CancelAsync(new CancelRequest
        {
            CommandId = Guid.NewGuid(),
            RunId = first.RunId,
            Reason = CancelReason.UserStop,
        }, CancellationToken.None);
        Assert.Equal(RunState.Cancelled, cancel.State);
        Assert.False(cancel.ChildPending);
        Assert.True(cancel.TerminationRequested);

        var third = await store.StartAsync(NewStart("modify|c:\\p2|ou_1"), CancellationToken.None);
        Assert.True(third.Accepted);
    }

    [Fact]
    public async Task Cancel_of_running_keeps_state_and_child_pending_and_replays_idempotently()
    {
        var store = new RunStore(_dbPath);
        var start = await store.StartAsync(NewStart("chat|c:\\p3|ou_2"), CancellationToken.None);
        using (var connection = StorageDatabase.Open(_dbPath))
        {
            StorageDatabase.Execute(connection,
                "UPDATE runs SET state = 'running', state_version = 3 WHERE run_id = $id;",
                null, ("$id", start.RunId.ToString()));
        }

        var commandId = Guid.NewGuid();
        var request = new CancelRequest { CommandId = commandId, RunId = start.RunId, Reason = CancelReason.Disarm };
        var first = await store.CancelAsync(request, CancellationToken.None);
        Assert.Equal(RunState.Running, first.State);
        Assert.True(first.ChildPending);
        Assert.True(first.TerminationRequested);

        var replay = await store.CancelAsync(request, CancellationToken.None);
        Assert.Equal(first.State, replay.State);
        Assert.True(replay.ChildPending);
        Assert.True(replay.TerminationRequested);

        var snapshot = await store.StatusAsync(start.RunId, CancellationToken.None);
        Assert.Equal(RunState.Running, snapshot.State);
        Assert.NotNull(snapshot.CancelRequestedAt);
    }

    [Fact]
    public async Task Writer_lock_contention_retries_via_busy_timeout_without_losing_rows()
    {
        var store = new RunStore(_dbPath);

        using var blocker = StorageDatabase.Open(_dbPath);
        var blockerTx = blocker.BeginTransaction(System.Data.IsolationLevel.Serializable, deferred: false);
        StorageDatabase.Execute(blocker, """
            INSERT INTO run_events (run_id, seq, envelope_json, created_at)
            VALUES ('blocker', 1, '{}', 'now');
            """, blockerTx);

        // 写锁被占期间发起 Start;busy_timeout 内应等待而非失败。
        var startTask = Task.Run(() => store.StartAsync(NewStart("probe|c:\\p4|ou_3"), CancellationToken.None));
        await Task.Delay(400);
        Assert.False(startTask.IsCompleted, "写锁未释放时 Start 不应提前完成或失败");
        blockerTx.Commit();

        var response = await startTask;
        Assert.True(response.Accepted);
        Assert.Equal(1L, CountRows("runs"));
        Assert.Equal(1L, CountRows("run_events"));
    }

    private long CountRows(string table)
    {
        using var connection = StorageDatabase.Open(_dbPath);
        using var cmd = connection.CreateCommand();
        cmd.CommandText = $"SELECT COUNT(*) FROM {table};";
        return (long)cmd.ExecuteScalar()!;
    }
}
