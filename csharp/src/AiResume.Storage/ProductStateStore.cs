using System.Text.Json;
using AiResume.Core;

namespace AiResume.Storage;

/// <summary>
/// S5-C 布防周期状态持久化(SQLite 单行表 product_state,事务写,迁移器幂等)。
/// 语义对齐现役 Set-CcuState(checker.ps1):损坏/缺失容错回默认;写入为完整状态对象
/// 原子替换(无锁外读旧快照整体写回问题)。只操作 shadow 数据库,绝不触碰生产状态。
/// </summary>
public sealed class ProductStateStore
{
    private const string TableSql = "SELECT state_json FROM product_state WHERE id = 1;";
    private readonly string _databasePath;

    public ProductStateStore(string databasePath)
    {
        ArgumentNullException.ThrowIfNull(databasePath);
        _databasePath = databasePath;
    }

    public string DatabasePath => _databasePath;

    public CheckerState Load()
    {
        try
        {
            using var connection = StorageDatabase.Open(_databasePath);
            using var cmd = connection.CreateCommand();
            cmd.CommandText = TableSql;
            string? json = cmd.ExecuteScalar() as string;
            if (string.IsNullOrWhiteSpace(json))
            {
                return CheckerState.CreateDefault();
            }

            return JsonSerializer.Deserialize<CheckerState>(json, CheckerState.JsonOptions) ?? CheckerState.CreateDefault();
        }
        catch (Exception)
        {
            // 读失败/表缺失/损坏:容错回默认(状态缺失不应阻断探测/观察)。
            return CheckerState.CreateDefault();
        }
    }

    /// <summary>单行 UPSERT 事务写;任何时刻磁盘上要么旧完整状态要么新完整状态。</summary>
    public void Save(CheckerState state)
    {
        ArgumentNullException.ThrowIfNull(state);

        string json = JsonSerializer.Serialize(state, CheckerState.JsonOptions);
        using var connection = StorageDatabase.Open(_databasePath);
        using var tx = connection.BeginTransaction(System.Data.IsolationLevel.Serializable, deferred: false);
        StorageDatabase.Execute(connection, """
            INSERT INTO product_state(id, state_json, updated_at) VALUES (1, $json, $now)
            ON CONFLICT(id) DO UPDATE SET state_json = $json, updated_at = $now;
            """, tx, ("$json", json), ("$now", DateTimeOffset.UtcNow.ToString("o")));
        tx.Commit();
    }
}
