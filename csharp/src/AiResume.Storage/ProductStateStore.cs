using System.Text.Json;
using AiResume.Core;

namespace AiResume.Storage;

/// <summary>
/// S5-C 布防周期状态持久化(SQLite 单行表 product_state,事务写,迁移器幂等)。
/// GUI/诊断读取可容错回默认;Worker 使用严格读取,避免损坏状态丢失 RunId 安全门禁。
/// 写入为完整状态对象原子替换(无锁外读旧快照整体写回问题)。只操作 shadow 数据库,
/// 绝不触碰生产状态。
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
            return LoadStrict();
        }
        catch (Exception)
        {
            // GUI/诊断边界允许降级展示;安全敏感的 Worker 必须调用 LoadStrict。
            return CheckerState.CreateDefault();
        }
    }

    /// <summary>
    /// 安全敏感读取。v6 迁移保证默认行存在;无行、空值、损坏或数据库不可读时抛出,
    /// 调用方必须停止本次状态机推进,不能把未知状态当成空状态继续续跑。
    /// </summary>
    public CheckerState LoadStrict()
    {
        using var connection = StorageDatabase.Open(_databasePath);
        using var cmd = connection.CreateCommand();
        cmd.CommandText = TableSql;
        return DeserializeStoredState(cmd.ExecuteScalar());
    }

    /// <summary>单行 UPSERT 事务写;任何时刻磁盘上要么旧完整状态要么新完整状态。</summary>
    public void Save(CheckerState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        ValidateState(state);

        string json = JsonSerializer.Serialize(state, CheckerState.JsonOptions);
        using var connection = StorageDatabase.Open(_databasePath);
        using var tx = connection.BeginTransaction(System.Data.IsolationLevel.Serializable, deferred: false);
        StorageDatabase.Execute(connection, """
            INSERT INTO product_state(id, state_json, updated_at) VALUES (1, $json, $now)
            ON CONFLICT(id) DO UPDATE SET state_json = $json, updated_at = $now;
            """, tx, ("$json", json), ("$now", DateTimeOffset.UtcNow.ToString("o")));
        tx.Commit();
    }

    /// <summary>SQLite 写事务内重读并只修改负责字段,避免跨窗口整体快照互相覆盖。</summary>
    public CheckerState Update(Action<CheckerState> mutate)
    {
        ArgumentNullException.ThrowIfNull(mutate);

        using var connection = StorageDatabase.Open(_databasePath);
        using var tx = connection.BeginTransaction(System.Data.IsolationLevel.Serializable, deferred: false);
        using var read = connection.CreateCommand();
        read.Transaction = tx;
        read.CommandText = TableSql;
        CheckerState state = DeserializeStoredState(read.ExecuteScalar());

        mutate(state);
        ValidateState(state);
        string updated = JsonSerializer.Serialize(state, CheckerState.JsonOptions);
        StorageDatabase.Execute(connection, """
            INSERT INTO product_state(id, state_json, updated_at) VALUES (1, $json, $now)
            ON CONFLICT(id) DO UPDATE SET state_json = $json, updated_at = $now;
            """, tx, ("$json", updated), ("$now", DateTimeOffset.UtcNow.ToString("o")));
        tx.Commit();
        return state;
    }

    private static CheckerState DeserializeStoredState(object? stored)
    {
        if (stored is null || stored == DBNull.Value)
        {
            throw new InvalidDataException("product_state 默认行不存在。");
        }

        if (stored is not string json || string.IsNullOrWhiteSpace(json))
        {
            throw new InvalidDataException("product_state.state_json 为空或类型无效。");
        }

        CheckerState state = JsonSerializer.Deserialize<CheckerState>(json, CheckerState.JsonOptions)
            ?? throw new InvalidDataException("product_state.state_json 反序列化为空。");
        ValidateState(state);
        return state;
    }

    private static void ValidateState(CheckerState state)
    {
        if (state.Phase is null || state.Phase is not (
                CheckerState.PhaseIdle or
                CheckerState.PhaseWaiting or
                CheckerState.PhaseResuming or
                CheckerState.PhaseDone or
                CheckerState.PhaseBlocked))
        {
            throw new InvalidDataException("product_state.phase 无效。");
        }

        if (state.CycleId is null ||
            state.ActiveRunId is null ||
            state.ActiveProjectPath is null ||
            state.PendingCancellationRunId is null ||
            state.PendingCancellationProjectPath is null ||
            state.PendingCancellationCycleId is null)
        {
            throw new InvalidDataException("product_state 关键字符串字段不能为 null。");
        }

        bool hasActiveRun = !string.IsNullOrEmpty(state.ActiveRunId);
        bool hasActiveProject = !string.IsNullOrEmpty(state.ActiveProjectPath);
        if (hasActiveRun != hasActiveProject)
        {
            throw new InvalidDataException("product_state 活动 RunId 元数据不完整。");
        }

        if (hasActiveRun)
        {
            if (!Guid.TryParse(state.ActiveRunId, out _))
            {
                throw new InvalidDataException("product_state 活动 RunId 与项目路径不匹配。");
            }
        }

        bool hasPendingRun = !string.IsNullOrEmpty(state.PendingCancellationRunId);
        bool hasPendingMetadata = !string.IsNullOrEmpty(state.PendingCancellationProjectPath) ||
            !string.IsNullOrEmpty(state.PendingCancellationCycleId);
        if (hasPendingRun != hasPendingMetadata ||
            (hasPendingRun &&
                (!Guid.TryParse(state.PendingCancellationRunId, out _) ||
                 string.IsNullOrEmpty(state.PendingCancellationProjectPath) ||
                 string.IsNullOrEmpty(state.PendingCancellationCycleId))))
        {
            throw new InvalidDataException("product_state 待终止 RunId 元数据不完整。");
        }

        if (hasActiveRun && hasPendingRun &&
            !string.Equals(state.ActiveRunId, state.PendingCancellationRunId, StringComparison.Ordinal))
        {
            throw new InvalidDataException("product_state 活动 RunId 与待终止 RunId 不一致。");
        }

        if (state.LimitedRefires < 0 ||
            state.ProjectStatus?.Any(kv => string.IsNullOrEmpty(kv.Key) || kv.Value is null) == true)
        {
            throw new InvalidDataException("product_state 项目状态或计数无效。");
        }
    }
}
