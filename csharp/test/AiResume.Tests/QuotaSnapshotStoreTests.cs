using AiResume.Storage;
using AiResume.Worker.Quota;
using Xunit;

namespace AiResume.Tests;

public sealed class QuotaSnapshotStoreTests
{
    [Fact]
    public void 同账号快照按CapturedAt新者胜且账号间隔离()
    {
        string path = TestTemp.NewFile("quota-store", ".db");
        var store = new QuotaSnapshotStore(path);
        UsageSnapshot older = Snapshot(DateTimeOffset.Parse("2026-08-09T10:00:00Z"), 30);
        UsageSnapshot newer = Snapshot(DateTimeOffset.Parse("2026-08-09T11:00:00Z"), 80);
        UsageSnapshot other = Snapshot(DateTimeOffset.Parse("2026-08-09T09:00:00Z"), 10);

        Assert.True(store.TrySave(newer, "account-a"));
        Assert.True(store.TrySave(older, "account-a"));
        Assert.True(store.TrySave(other, "account-b"));

        Assert.Equal(80, Scoped(store.Load("claudecode", "account-a")!).UsedPercent);
        Assert.Equal(10, Scoped(store.Load("claudecode", "account-b")!).UsedPercent);
        Assert.Null(store.Load("claudecode", "account-c"));
    }

    [Fact]
    public void 未归因限流事实可经SQLite往返保留()
    {
        string path = TestTemp.NewFile("quota-unattributed", ".db");
        var store = new QuotaSnapshotStore(path);
        UsageSnapshot snapshot = Snapshot(DateTimeOffset.Parse("2026-08-09T11:00:00Z"), 100);
        UsageBucket bucket = Assert.Single(snapshot.Buckets);
        snapshot = snapshot with
        {
            Buckets = new[]
            {
                bucket with { UnattributedLimitReached = true },
            },
        };

        Assert.True(store.TrySave(snapshot, "account-a"));

        UsageBucket loaded = Assert.Single(store.Load("claudecode", "account-a")!.Buckets);
        Assert.True(loaded.LimitReached);
        Assert.True(loaded.UnattributedLimitReached);
    }

    [Fact]
    public void 同一CapturedAt后写的无Scoped快照可以清除旧Fable()
    {
        string path = TestTemp.NewFile("quota-store-tie", ".db");
        var store = new QuotaSnapshotStore(path);
        DateTimeOffset capturedAt = DateTimeOffset.Parse("2026-08-09T11:00:00.1234567Z");
        UsageSnapshot scoped = Snapshot(capturedAt, 100);
        UsageSnapshot cleared = scoped with
        {
            Buckets = new[]
            {
                new UsageBucket("Usage", true, false, new[]
                {
                    new UsageWindow(
                        "seven_day", "allowed", UsageWindow.SevenDaySeconds,
                        capturedAt.AddDays(1).ToUnixTimeSeconds(), 86400, 50),
                }),
            },
        };

        Assert.True(store.TrySave(scoped, "account-a"));
        Assert.True(store.TrySave(cleared, "account-a"));

        Assert.DoesNotContain(
            Assert.Single(store.Load("claudecode", "account-a")!.Buckets).Windows,
            window => window.Name.StartsWith("weekly_scoped", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void 较旧低读数晚提交不会覆盖同Reset较新满额读数()
    {
        string path = TestTemp.NewFile("quota-store-old-explicit", ".db");
        var store = new QuotaSnapshotStore(path);
        DateTimeOffset newerTime = DateTimeOffset.Parse("2026-08-09T11:01:00Z");
        DateTimeOffset olderTime = newerTime.AddMinutes(-1);
        long resetAt = newerTime.AddDays(1).ToUnixTimeSeconds();
        UsageSnapshot newer = ScopedSnapshot(newerTime, resetAt, 100);
        UsageSnapshot older = ScopedSnapshot(olderTime, resetAt, 99);

        Assert.True(store.TryUpdate("claudecode", "account-a", _ => newer, out _));
        Assert.True(store.TryUpdate(
            "claudecode", "account-a",
            previous => QuotaService.MergeSparseObservation(older, previous, newerTime), out _));

        UsageSnapshot final = store.Load("claudecode", "account-a")!;
        Assert.Equal(100, Scoped(final).UsedPercent);
        Assert.Equal("blocked", Scoped(final).Status);
        Assert.Equal(newerTime, final.CapturedAt);
    }

    [Fact]
    public void 较旧Reset晚提交不会覆盖较新Reset代次()
    {
        string path = TestTemp.NewFile("quota-store-old-generation", ".db");
        var firstStore = new QuotaSnapshotStore(path);
        var delayedStore = new QuotaSnapshotStore(path);
        DateTimeOffset newerTime = DateTimeOffset.Parse("2026-08-09T11:01:00Z");
        DateTimeOffset olderTime = newerTime.AddMinutes(-1);
        long oldReset = newerTime.AddHours(1).ToUnixTimeSeconds();
        long newReset = newerTime.AddDays(1).ToUnixTimeSeconds();
        UsageSnapshot newer = ScopedSnapshot(newerTime, newReset, 12);
        UsageSnapshot older = ScopedSnapshot(olderTime, oldReset, 100);

        Assert.True(firstStore.TryUpdate("claudecode", "account-a", _ => newer, out _));
        Assert.True(delayedStore.TryUpdate(
            "claudecode", "account-a",
            previous => QuotaService.MergeSparseObservation(older, previous, newerTime), out _));

        UsageSnapshot finalSnapshot = firstStore.Load("claudecode", "account-a")!;
        UsageBucket finalBucket = Assert.Single(finalSnapshot.Buckets);
        UsageWindow final = Scoped(finalSnapshot);
        Assert.Equal(newReset, final.ResetAtUnix);
        Assert.Equal(12, final.UsedPercent);
        Assert.True(final.CarriedForward);
        Assert.False(finalBucket.LimitReached);
        Assert.False(finalBucket.Allowed);
    }

    [Fact]
    public void 两边都无Reset时较旧低读数不得覆盖较新满额读数()
    {
        string path = TestTemp.NewFile("quota-store-resetless-old", ".db");
        var firstStore = new QuotaSnapshotStore(path);
        var delayedStore = new QuotaSnapshotStore(path);
        DateTimeOffset newerTime = DateTimeOffset.Parse("2026-08-09T11:01:00Z");
        DateTimeOffset olderTime = newerTime.AddMinutes(-1);
        UsageSnapshot newer = ResetlessSnapshot(newerTime, 100);
        UsageSnapshot older = ResetlessSnapshot(olderTime, 20);

        Assert.True(firstStore.TryUpdate("claudecode", "account-a", _ => newer, out _));
        Assert.True(delayedStore.TryUpdate(
            "claudecode", "account-a",
            previous => QuotaService.MergeSparseObservation(older, previous, newerTime), out _));

        UsageSnapshot final = firstStore.Load("claudecode", "account-a")!;
        UsageBucket bucket = Assert.Single(final.Buckets);
        UsageWindow window = Scoped(final);
        Assert.Equal(100, window.UsedPercent);
        Assert.Equal("blocked", window.Status);
        Assert.True(bucket.LimitReached);
        Assert.False(bucket.Allowed);
        Assert.Equal(newerTime, final.CapturedAt);
    }

    [Fact]
    public void 存储失败提供脱敏诊断但不抛异常()
    {
        string directoryInsteadOfDatabase = TestTemp.NewDir("quota-store-failure");
        Directory.CreateDirectory(directoryInsteadOfDatabase);
        var store = new QuotaSnapshotStore(directoryInsteadOfDatabase);

        Assert.False(store.TrySave(
            Snapshot(DateTimeOffset.Parse("2026-08-09T11:00:00Z"), 50), "account-a"));
        Assert.NotNull(store.LastFailure);
        Assert.Contains("额度快照写入失败", store.LastFailure, StringComparison.Ordinal);
        Assert.DoesNotContain(directoryInsteadOfDatabase, store.LastFailure, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void 可解析但结构损坏的JSON按无快照处理()
    {
        string path = TestTemp.NewFile("quota-corrupt", ".db");
        StorageDatabase.Migrate(path);
        using (var connection = StorageDatabase.Open(path))
        {
            StorageDatabase.Execute(connection, """
                INSERT INTO quota_snapshots(
                    provider, credential_fingerprint, captured_at, snapshot_json, updated_at)
                VALUES ('claudecode', 'account-a', 1,
                    '{"Provider":"claudecode","CapturedAt":"2026-08-09T10:00:00Z","Buckets":null}',
                    '2026-08-09T10:00:00Z');
                """);
        }

        var store = new QuotaSnapshotStore(path);
        Assert.Null(store.Load("claudecode", "account-a"));
        Assert.Contains("InvalidSnapshot", store.LastFailure, StringComparison.Ordinal);
    }

    [Fact]
    public void 旧快照缺少EvidenceSource时默认Unknown且不得升格()
    {
        string path = TestTemp.NewFile("quota-legacy-source", ".db");
        StorageDatabase.Migrate(path);
        using (var connection = StorageDatabase.Open(path))
        {
            StorageDatabase.Execute(connection, """
                INSERT INTO quota_snapshots(
                    provider, credential_fingerprint, captured_at, snapshot_json, updated_at)
                VALUES ('claudecode', 'account-a', 1,
                    '{"Provider":"claudecode","CapturedAt":"2026-08-09T10:00:00Z","Buckets":[{"Name":"Usage","Allowed":true,"LimitReached":false,"Windows":[{"Name":"seven_day","Status":"allowed","WindowSeconds":604800,"ResetAtUnix":1786356000,"ResetAfterSeconds":86400,"UsedPercent":50,"CarriedForward":false,"Identity":null}]}],"UnavailableReason":null}',
                    '2026-08-09T10:00:00Z');
                """);
        }

        var store = new QuotaSnapshotStore(path);
        UsageSnapshot loaded = Assert.IsType<UsageSnapshot>(
            store.Load("claudecode", "account-a"));

        Assert.Equal(UsageEvidenceSource.Unknown, loaded.EvidenceSource);
    }

    [Fact]
    public void EvidenceSource写入SQLite后可原样读取()
    {
        string path = TestTemp.NewFile("quota-source-roundtrip", ".db");
        var store = new QuotaSnapshotStore(path);
        UsageSnapshot oauth = Snapshot(
            DateTimeOffset.Parse("2026-08-09T11:00:00Z"), 50) with
        {
            EvidenceSource = UsageEvidenceSource.OAuth,
        };

        Assert.True(store.TrySave(oauth, "account-a"));

        Assert.Equal(
            UsageEvidenceSource.OAuth,
            store.Load("claudecode", "account-a")!.EvidenceSource);
    }

    [Fact]
    public void 读成功不掩盖写失败且后续写成功会清除诊断()
    {
        string path = TestTemp.NewFile("quota-diagnostics", ".db");
        var store = new QuotaSnapshotStore(path);
        UsageSnapshot invalid = Snapshot(DateTimeOffset.Parse("2026-08-09T11:00:00Z"), 50) with
        {
            Buckets = null!,
        };

        Assert.False(store.TrySave(invalid, "account-a"));
        Assert.Null(store.Load("claudecode", "account-a"));
        Assert.Contains("写入失败", store.LastFailure, StringComparison.Ordinal);

        Assert.True(store.TrySave(Snapshot(DateTimeOffset.Parse("2026-08-09T12:00:00Z"), 60), "account-a"));
        Assert.Null(store.LastFailure);
    }

    [Fact]
    public void V3数据库可单调迁移到当前QuotaSnapshots表()
    {
        string path = TestTemp.NewFile("quota-v3", ".db");
        using (var connection = StorageDatabase.Open(path))
        {
            StorageDatabase.Execute(connection, """
                CREATE TABLE schema_version (version INTEGER PRIMARY KEY, applied_at TEXT NOT NULL);
                INSERT INTO schema_version(version, applied_at) VALUES
                    (1, '2026-08-01T00:00:00Z'),
                    (2, '2026-08-01T00:00:00Z'),
                    (3, '2026-08-01T00:00:00Z');
                """);
        }

        StorageDatabase.Migrate(path);

        using var migrated = StorageDatabase.Open(path);
        using var command = migrated.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM pragma_table_info('quota_snapshots');";
        Assert.Equal(5L, (long)command.ExecuteScalar()!);
    }

    [Fact]
    public void 已部署V4三列表升级V5并丢弃无法归属账号的旧行()
    {
        string path = TestTemp.NewFile("quota-v4", ".db");
        using (var connection = StorageDatabase.Open(path))
        {
            StorageDatabase.Execute(connection, """
                CREATE TABLE schema_version (version INTEGER PRIMARY KEY, applied_at TEXT NOT NULL);
                INSERT INTO schema_version(version, applied_at) VALUES
                    (1, '2026-08-01T00:00:00Z'),
                    (2, '2026-08-01T00:00:00Z'),
                    (3, '2026-08-01T00:00:00Z'),
                    (4, '2026-08-09T00:00:00Z');
                CREATE TABLE quota_snapshots (
                    provider TEXT PRIMARY KEY,
                    snapshot_json TEXT NOT NULL,
                    updated_at TEXT NOT NULL
                );
                INSERT INTO quota_snapshots(provider, snapshot_json, updated_at)
                VALUES ('claudecode', '{}', '2026-08-09T00:00:00Z');
                """);
        }

        StorageDatabase.Migrate(path);

        using (var migrated = StorageDatabase.Open(path))
        {
            using var version = migrated.CreateCommand();
            version.CommandText = "SELECT MAX(version) FROM schema_version;";
            Assert.Equal((long)StorageDatabase.CurrentSchemaVersion, (long)version.ExecuteScalar()!);
            using var columns = migrated.CreateCommand();
            columns.CommandText = "SELECT COUNT(*) FROM pragma_table_info('quota_snapshots');";
            Assert.Equal(5L, (long)columns.ExecuteScalar()!);
            using var rows = migrated.CreateCommand();
            rows.CommandText = "SELECT COUNT(*) FROM quota_snapshots;";
            Assert.Equal(0L, (long)rows.ExecuteScalar()!);
        }

        var store = new QuotaSnapshotStore(path);
        UsageSnapshot snapshot = Snapshot(DateTimeOffset.Parse("2026-08-09T12:00:00Z"), 70);
        Assert.True(store.TrySave(snapshot, "account-a"));
        Assert.Equal(70, Scoped(store.Load("claudecode", "account-a")!).UsedPercent);
    }

    [Fact]
    public async Task 并发稀疏事务不能用旧承接值覆盖具体新读数()
    {
        string path = TestTemp.NewFile("quota-atomic-merge", ".db");
        DateTimeOffset now = DateTimeOffset.Parse("2026-08-09T12:00:00Z");
        long resetAt = now.AddDays(1).ToUnixTimeSeconds();
        var firstStore = new QuotaSnapshotStore(path);
        var secondStore = new QuotaSnapshotStore(path);
        Assert.True(firstStore.TrySave(ScopedSnapshot(now.AddHours(-2), resetAt, 10), "account-a"));

        UsageSnapshot sparseLater = new(
            "claudecode",
            now.AddMinutes(2),
            new[]
            {
                new UsageBucket("Usage", true, false, new[]
                {
                    new UsageWindow("seven_day", "allowed", UsageWindow.SevenDaySeconds,
                        resetAt, 86400, 40),
                }),
            },
            null);
        UsageSnapshot explicitEarlier = ScopedSnapshot(now.AddMinutes(1), resetAt, 90);
        using var sparseInsideTransaction = new ManualResetEventSlim(false);
        using var releaseSparse = new ManualResetEventSlim(false);
        using var explicitStarted = new ManualResetEventSlim(false);

        Task<bool> sparseTask = Task.Run(() => secondStore.TryUpdate(
            "claudecode",
            "account-a",
            previous =>
            {
                sparseInsideTransaction.Set();
                Assert.True(releaseSparse.Wait(TimeSpan.FromSeconds(5)));
                return QuotaService.MergeSparseObservation(sparseLater, previous, now);
            },
            out _));
        Assert.True(sparseInsideTransaction.Wait(TimeSpan.FromSeconds(5)));

        Task<bool> explicitTask = Task.Run(() =>
        {
            explicitStarted.Set();
            return firstStore.TryUpdate(
                "claudecode",
                "account-a",
                previous => QuotaService.MergeSparseObservation(explicitEarlier, previous, now),
                out _);
        });
        Assert.True(explicitStarted.Wait(TimeSpan.FromSeconds(5)));
        releaseSparse.Set();

        Assert.True(await sparseTask);
        Assert.True(await explicitTask);
        UsageSnapshot final = firstStore.Load("claudecode", "account-a")!;
        Assert.Equal(90, Scoped(final).UsedPercent);
        Assert.Equal(sparseLater.CapturedAt, final.CapturedAt);
    }

    private static UsageSnapshot Snapshot(DateTimeOffset capturedAt, int used) => new(
        "claudecode",
        capturedAt,
        new[]
        {
            new UsageBucket("Usage", used < 100, used >= 100, new[]
            {
                new UsageWindow(
                    "weekly_scoped:Fable", used >= 100 ? "blocked" : "allowed",
                    UsageWindow.SevenDaySeconds, capturedAt.AddDays(1).ToUnixTimeSeconds(), 86400, used),
            }),
        },
        null);

    private static UsageSnapshot ScopedSnapshot(DateTimeOffset capturedAt, long resetAt, int used) => new(
        "claudecode",
        capturedAt,
        new[]
        {
            new UsageBucket("Usage", used < 100, used >= 100, new[]
            {
                new UsageWindow(
                    "weekly_scoped:Fable", used >= 100 ? "blocked" : "allowed",
                    UsageWindow.SevenDaySeconds, resetAt,
                    (int)Math.Max(0, resetAt - capturedAt.ToUnixTimeSeconds()), used),
            }),
        },
        null);

    private static UsageSnapshot ResetlessSnapshot(DateTimeOffset capturedAt, int used) => new(
        "claudecode",
        capturedAt,
        new[]
        {
            new UsageBucket("Usage", used < 100, used >= 100, new[]
            {
                new UsageWindow(
                    "weekly_scoped:Fable", used >= 100 ? "blocked" : "allowed",
                    UsageWindow.SevenDaySeconds, null, null, used),
            }),
        },
        null);

    private static UsageWindow Scoped(UsageSnapshot snapshot) =>
        Assert.Single(Assert.Single(snapshot.Buckets).Windows, window =>
            window.Name.StartsWith("weekly_scoped", StringComparison.OrdinalIgnoreCase));
}
