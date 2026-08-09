using Microsoft.Data.Sqlite;

namespace AiResume.Storage;

/// <summary>
/// SQLite 连接与迁移器。所有 Storage 组件经由此处打开连接,保证 WAL、busy_timeout
/// 与外键设置一致;迁移按 schema_version 单调推进,重复执行幂等。
/// </summary>
public static class StorageDatabase
{
    public const int CurrentSchemaVersion = 5;

    public static SqliteConnection Open(string databasePath)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
        }.ToString());
        connection.Open();
        Execute(connection, "PRAGMA journal_mode=WAL;");
        Execute(connection, "PRAGMA busy_timeout=5000;");
        Execute(connection, "PRAGMA foreign_keys=ON;");
        return connection;
    }

    /// <summary>应用全部迁移;可重复执行,已应用的版本跳过。</summary>
    public static void Migrate(string databasePath)
    {
        using var connection = Open(databasePath);
        using var tx = connection.BeginTransaction(System.Data.IsolationLevel.Serializable, deferred: false);
        Execute(connection, """
            CREATE TABLE IF NOT EXISTS schema_version (
                version INTEGER PRIMARY KEY,
                applied_at TEXT NOT NULL
            );
            """, tx);

        long applied = 0;
        using (var cmd = connection.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = "SELECT COALESCE(MAX(version), 0) FROM schema_version;";
            applied = (long)cmd.ExecuteScalar()!;
        }

        if (applied < 1)
        {
            ApplyV1(connection, tx);
            Execute(connection, "INSERT INTO schema_version(version, applied_at) VALUES (1, $now);", tx,
                ("$now", DateTimeOffset.UtcNow.ToString("o")));
        }

        if (applied < 2)
        {
            ApplyV2(connection, tx);
            Execute(connection, "INSERT INTO schema_version(version, applied_at) VALUES (2, $now);", tx,
                ("$now", DateTimeOffset.UtcNow.ToString("o")));
        }

        if (applied < 3)
        {
            ApplyV3(connection, tx);
            Execute(connection, "INSERT INTO schema_version(version, applied_at) VALUES (3, $now);", tx,
                ("$now", DateTimeOffset.UtcNow.ToString("o")));
        }

        if (applied < 4)
        {
            ApplyV4(connection, tx);
            Execute(connection, "INSERT INTO schema_version(version, applied_at) VALUES (4, $now);", tx,
                ("$now", DateTimeOffset.UtcNow.ToString("o")));
        }

        if (applied < 5)
        {
            ApplyV5(connection, tx);
            Execute(connection, "INSERT INTO schema_version(version, applied_at) VALUES (5, $now);", tx,
                ("$now", DateTimeOffset.UtcNow.ToString("o")));
        }

        tx.Commit();
    }

    private static void ApplyV1(SqliteConnection connection, SqliteTransaction tx)
    {
        Execute(connection, """
            CREATE TABLE runs (
                run_id TEXT PRIMARY KEY,
                request_id TEXT NOT NULL UNIQUE,
                run_key TEXT NOT NULL,
                task_kind TEXT NOT NULL,
                actor TEXT NULL,
                project_ref TEXT NULL,
                profile_id TEXT NOT NULL,
                session_ref_json TEXT NULL,
                cwd TEXT NULL,
                input_ref TEXT NOT NULL,
                credential_ref TEXT NULL,
                attempt_group_id TEXT NULL,
                parent_run_id TEXT NULL,
                fallback_policy TEXT NOT NULL,
                state TEXT NOT NULL,
                state_version INTEGER NOT NULL,
                seq INTEGER NOT NULL DEFAULT 0,
                terminal_reason TEXT NULL,
                side_effect_marked INTEGER NOT NULL DEFAULT 0,
                deadline_ms INTEGER NOT NULL DEFAULT 0 CHECK (deadline_ms = 0),
                cancel_command_id TEXT NULL,
                cancel_reason TEXT NULL,
                cancel_requested_at TEXT NULL,
                queued_at TEXT NOT NULL,
                terminal_at TEXT NULL,
                created_at TEXT NOT NULL,
                updated_at TEXT NOT NULL
            );
            CREATE INDEX idx_runs_run_key ON runs(run_key);
            CREATE INDEX idx_runs_state ON runs(state);

            CREATE TABLE run_events (
                run_id TEXT NOT NULL,
                seq INTEGER NOT NULL,
                envelope_json TEXT NOT NULL,
                created_at TEXT NOT NULL,
                PRIMARY KEY (run_id, seq)
            );

            CREATE TABLE outbox (
                outbox_id TEXT PRIMARY KEY,
                idempotency_key TEXT NOT NULL UNIQUE,
                envelope_json TEXT NOT NULL,
                state TEXT NOT NULL DEFAULT 'pending',
                attempts INTEGER NOT NULL DEFAULT 0,
                created_at TEXT NOT NULL,
                updated_at TEXT NOT NULL
            );

            CREATE TABLE process_registry (
                run_id TEXT PRIMARY KEY,
                parent_pid INTEGER NOT NULL,
                child_pid INTEGER NULL,
                job_id TEXT NULL,
                started_at TEXT NOT NULL,
                command_signature TEXT NOT NULL,
                updated_at TEXT NOT NULL
            );
            """, tx);
    }

    /// <summary>
    /// v2:runs 表补 error_class/error_code 列(RunSnapshot 错误持久化,S2-E 编排器需要)。
    /// 纯 ALTER ADD COLUMN,幂等由 schema_version 保证。
    /// </summary>
    private static void ApplyV2(SqliteConnection connection, SqliteTransaction tx)
    {
        Execute(connection, "ALTER TABLE runs ADD COLUMN error_class TEXT;", tx);
        Execute(connection, "ALTER TABLE runs ADD COLUMN error_code TEXT;", tx);
    }

    /// <summary>
    /// v3:product_state 表(S5-C 布防周期状态单行存储)。幂等由 schema_version 保证。
    /// </summary>
    private static void ApplyV3(SqliteConnection connection, SqliteTransaction tx)
    {
        Execute(connection, """
            CREATE TABLE product_state (
                id INTEGER PRIMARY KEY CHECK (id = 1),
                state_json TEXT NOT NULL,
                updated_at TEXT NOT NULL
            );
            """, tx);
    }

    /// <summary>
    /// v4:第一版最近权威额度快照。该版只有 provider 主键,无法区分账号;
    /// v5 会显式替换它。保留原始形状是为了让已部署 v4 能可靠升级。
    /// </summary>
    private static void ApplyV4(SqliteConnection connection, SqliteTransaction tx)
    {
        Execute(connection, """
            CREATE TABLE quota_snapshots (
                provider TEXT PRIMARY KEY,
                snapshot_json TEXT NOT NULL,
                updated_at TEXT NOT NULL
            );
            """, tx);
    }

    /// <summary>
    /// v5:额度快照按 provider + 不可逆账号指纹隔离,并记录服务端观察时间。
    /// v4 的旧行没有账号身份,无法证明属于当前登录账号;安全迁移只能丢弃,
    /// 不能把旧 Fable 状态错误承接给另一个账号。
    /// </summary>
    private static void ApplyV5(SqliteConnection connection, SqliteTransaction tx)
    {
        Execute(connection, """
            DROP TABLE IF EXISTS quota_snapshots;
            CREATE TABLE quota_snapshots (
                provider TEXT NOT NULL,
                credential_fingerprint TEXT NOT NULL,
                captured_at INTEGER NOT NULL,
                snapshot_json TEXT NOT NULL,
                updated_at TEXT NOT NULL,
                PRIMARY KEY (provider, credential_fingerprint)
            );
            """, tx);
    }

    public static void Execute(SqliteConnection connection, string sql, SqliteTransaction? tx = null,
        params (string Name, object Value)[] args)
    {
        using var cmd = connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        foreach (var (name, value) in args)
        {
            cmd.Parameters.AddWithValue(name, value);
        }

        cmd.ExecuteNonQuery();
    }
}
