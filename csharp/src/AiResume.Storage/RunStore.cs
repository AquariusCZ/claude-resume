using System.Text.Json;
using AiResume.Core;
using AiResume.Core.Abstractions;
using AiResume.Core.Contracts;
using AiResume.Core.Events;
using Microsoft.Data.Sqlite;

namespace AiResume.Storage;

/// <summary>
/// SQLite+WAL RunStore(单 writer)。全部写入走 BEGIN IMMEDIATE 事务;
/// 幂等:同 requestId 重复 Start 返回既有状态,同 (run_id,seq) append 无副作用。
/// Cancel 只持久化命令并按状态机推进;真实 close 归 ProcessSupervisor(S2-D)。
/// </summary>
public sealed class RunStore : IRunStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly string _databasePath;

    public RunStore(string databasePath)
    {
        _databasePath = databasePath;
    }

    public async Task<StartResponse> StartAsync(StartRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.RequestId == Guid.Empty)
        {
            throw new ArgumentException("RequestId 不能为空。", nameof(request));
        }

        if (string.IsNullOrWhiteSpace(request.RunKey))
        {
            throw new ArgumentException("RunKey 必须由 RunKey.Create 生成,不能为空。", nameof(request));
        }

        cancellationToken.ThrowIfCancellationRequested();
        using var connection = StorageDatabase.Open(_databasePath);
        using var tx = connection.BeginTransaction(System.Data.IsolationLevel.Serializable, deferred: false);

        // 1) requestId 幂等:已接纳过则原样返回既有状态。
        using (var existing = Query(connection, tx,
            "SELECT run_id, state, state_version FROM runs WHERE request_id = $rid;",
            ("$rid", request.RequestId.ToString("D"))))
        {
            if (existing.Read())
            {
                var response = new StartResponse
                {
                    Accepted = true,
                    Existing = true,
                    RunId = RunId.FromString(existing.GetString(0)),
                    State = ParseState(existing.GetString(1)),
                    StateVersion = existing.GetInt64(2),
                };
                tx.Commit();
                return await Task.FromResult(response);
            }
        }

        // 2) runKey 并发所有权:存在非 terminal 同 key 运行即拒绝,返回占用者。
        using (var busy = Query(connection, tx,
            "SELECT run_id FROM runs WHERE run_key = $key AND state IN ('queued','starting','running') LIMIT 1;",
            ("$key", request.RunKey)))
        {
            if (busy.Read())
            {
                var response = new StartResponse
                {
                    Accepted = false,
                    Conflict = ConflictKind.RunKeyBusy,
                    OccupyingRunId = RunId.FromString(busy.GetString(0)),
                    State = RunState.Queued,
                };
                tx.Commit();
                return response;
            }
        }

        // 3) 持久接纳:queued 落库成功后才返回 accepted(写失败=异常上抛,调用方按 internal 拒绝)。
        var runId = RunId.New();
        string now = DateTimeOffset.UtcNow.ToString("o");
        StorageDatabase.Execute(connection, """
            INSERT INTO runs (
                run_id, request_id, run_key, task_kind, actor, project_ref, profile_id,
                session_ref_json, cwd, input_ref, credential_ref, attempt_group_id, parent_run_id,
                fallback_policy, state, state_version, queued_at, created_at, updated_at)
            VALUES (
                $run_id, $request_id, $run_key, $task_kind, $actor, $project_ref, $profile_id,
                $session_ref, $cwd, $input_ref, $credential_ref, $attempt_group, $parent_run,
                $fallback, 'queued', 1, $now, $now, $now);
            """, tx,
            ("$run_id", runId.ToString()),
            ("$request_id", request.RequestId.ToString("D")),
            ("$run_key", request.RunKey),
            ("$task_kind", request.TaskKind.ToWireCode()),
            ("$actor", (object?)request.Actor ?? DBNull.Value),
            ("$project_ref", (object?)request.ProjectRef ?? DBNull.Value),
            ("$profile_id", request.ProfileId),
            ("$session_ref", request.SessionRef is null ? DBNull.Value : JsonSerializer.Serialize(request.SessionRef, JsonOptions)),
            ("$cwd", (object?)request.Cwd ?? DBNull.Value),
            ("$input_ref", request.InputRef),
            ("$credential_ref", (object?)request.CredentialRef ?? DBNull.Value),
            ("$attempt_group", request.AttemptGroupId?.ToString("D") ?? (object)DBNull.Value),
            ("$parent_run", request.ParentRunId?.ToString("D") ?? (object)DBNull.Value),
            ("$fallback", request.FallbackPolicy == FallbackPolicy.ProviderExplicitOnce ? "provider_explicit_once" : "none"),
            ("$now", now));
        tx.Commit();

        return new StartResponse
        {
            Accepted = true,
            RunId = runId,
            State = RunState.Queued,
            StateVersion = 1,
        };
    }

    public async Task<RunSnapshot> StatusAsync(RunId runId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var connection = StorageDatabase.Open(_databasePath);
        using var reader = Query(connection, null, """
            SELECT run_id, request_id, run_key, task_kind, actor, attempt_group_id, parent_run_id,
                   profile_id, state, state_version, seq, terminal_reason, queued_at, terminal_at,
                   side_effect_marked, cancel_requested_at, error_class, error_code, fallback_policy
            FROM runs WHERE run_id = $run_id;
            """, ("$run_id", runId.ToString()));

        if (!reader.Read())
        {
            throw new KeyNotFoundException($"run {runId} 不存在。");
        }

        string? errorClassWire = reader.IsDBNull(16) ? null : reader.GetString(16);
        bool sideEffectsStarted = reader.GetInt64(14) != 0;

        // fallback 允许性 = 请求带 provider_explicit_once 且副作用尚未标记(D-002 语义)。
        bool fallbackAllowed = reader.GetString(18) == "provider_explicit_once" && !sideEffectsStarted;
        return await Task.FromResult(new RunSnapshot
        {
            RunId = RunId.FromString(reader.GetString(0)),
            RequestId = Guid.Parse(reader.GetString(1)),
            RunKey = reader.GetString(2),
            TaskKind = ParseTaskKind(reader.GetString(3)),
            Actor = reader.IsDBNull(4) ? null : reader.GetString(4),
            AttemptGroupId = reader.IsDBNull(5) ? null : Guid.Parse(reader.GetString(5)),
            ParentRunId = reader.IsDBNull(6) ? null : Guid.Parse(reader.GetString(6)),
            ProfileId = reader.GetString(7),
            State = ParseState(reader.GetString(8)),
            StateVersion = reader.GetInt64(9),
            Seq = reader.GetInt64(10),
            TerminalReason = reader.IsDBNull(11) ? null : reader.GetString(11),
            QueuedAt = reader.IsDBNull(12) ? null : DateTimeOffset.Parse(reader.GetString(12)),
            TerminalAt = reader.IsDBNull(13) ? null : DateTimeOffset.Parse(reader.GetString(13)),
            SideEffectsStarted = sideEffectsStarted,
            CancelRequestedAt = reader.IsDBNull(15) ? null : DateTimeOffset.Parse(reader.GetString(15)),
            ErrorClass = errorClassWire is null ? null : ParseErrorClass(errorClassWire),
            ErrorCode = reader.IsDBNull(17) ? null : reader.GetString(17),
            FallbackAllowed = fallbackAllowed,
        });
    }

    public async Task<CancelResponse> CancelAsync(CancelRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        using var connection = StorageDatabase.Open(_databasePath);
        using var tx = connection.BeginTransaction(System.Data.IsolationLevel.Serializable, deferred: false);

        string? state = null;
        long stateVersion = 0;
        string? cancelCommandId = null;
        string? cancelRequestedAt = null;
        using (var reader = Query(connection, tx,
            "SELECT state, state_version, cancel_command_id, cancel_requested_at FROM runs WHERE run_id = $run_id;",
            ("$run_id", request.RunId.ToString())))
        {
            if (!reader.Read())
            {
                throw new KeyNotFoundException($"run {request.RunId} 不存在。");
            }

            state = reader.GetString(0);
            stateVersion = reader.GetInt64(1);
            cancelCommandId = reader.IsDBNull(2) ? null : reader.GetString(2);
            cancelRequestedAt = reader.IsDBNull(3) ? null : reader.GetString(3);
        }

        RunState current = ParseState(state!);
        string commandId = request.CommandId.ToString("D");

        // 幂等重放:同 commandId 返回与首次一致的结果。
        if (cancelCommandId == commandId)
        {
            tx.Commit();
            return new CancelResponse
            {
                CommandId = request.CommandId,
                RunId = request.RunId,
                State = current,
                StateVersion = stateVersion,
                ChildPending = !RunStateMachine.IsTerminal(current),
                TerminationRequested = true,
                CancelRequestedAt = cancelRequestedAt is null ? null : DateTimeOffset.Parse(cancelRequestedAt),
            };
        }

        // 已 terminal:不再有可取消对象;不覆盖既有 cancel 记录。
        if (RunStateMachine.IsTerminal(current))
        {
            tx.Commit();
            return new CancelResponse
            {
                CommandId = request.CommandId,
                RunId = request.RunId,
                State = current,
                StateVersion = stateVersion,
                ChildPending = false,
                TerminationRequested = false,
                CancelRequestedAt = cancelRequestedAt is null ? null : DateTimeOffset.Parse(cancelRequestedAt),
            };
        }

        string now = DateTimeOffset.UtcNow.ToString("o");
        bool preSpawn = current == RunState.Queued;
        if (preSpawn)
        {
            // spawn 前取消可以直接进入 terminal cancelled(合法迁移 Queued→Cancelled)。
            StorageDatabase.Execute(connection, """
                UPDATE runs SET state = 'cancelled', state_version = state_version + 1,
                    terminal_reason = 'cancelled', terminal_at = $now,
                    cancel_command_id = $cmd, cancel_reason = $reason, cancel_requested_at = $now,
                    updated_at = $now
                WHERE run_id = $run_id;
                """, tx,
                ("$run_id", request.RunId.ToString()), ("$cmd", commandId),
                ("$reason", request.Reason.ToString().ToLowerInvariant()), ("$now", now));
        }
        else
        {
            // starting/running:只持久化终止请求;真实 close 前保持状态与运行键。
            StorageDatabase.Execute(connection, """
                UPDATE runs SET cancel_command_id = $cmd, cancel_reason = $reason,
                    cancel_requested_at = $now, updated_at = $now
                WHERE run_id = $run_id;
                """, tx,
                ("$run_id", request.RunId.ToString()), ("$cmd", commandId),
                ("$reason", request.Reason.ToString().ToLowerInvariant()), ("$now", now));
        }

        tx.Commit();
        return await Task.FromResult(new CancelResponse
        {
            CommandId = request.CommandId,
            RunId = request.RunId,
            State = preSpawn ? RunState.Cancelled : current,
            StateVersion = preSpawn ? stateVersion + 1 : stateVersion,
            ChildPending = !preSpawn,
            TerminationRequested = true,
            CancelRequestedAt = DateTimeOffset.Parse(now),
        });
    }

    /// <summary>幂等事件追加:同 (run_id,seq) 重复写入无副作用;返回本次是否真实插入。</summary>
    public bool TryAppendEvent(RunId runId, long seq, EventEnvelopeV1 envelope)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        using var connection = StorageDatabase.Open(_databasePath);
        using var tx = connection.BeginTransaction(System.Data.IsolationLevel.Serializable, deferred: false);
        using var cmd = connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT OR IGNORE INTO run_events (run_id, seq, envelope_json, created_at)
            VALUES ($run_id, $seq, $json, $now);
            """;
        cmd.Parameters.AddWithValue("$run_id", runId.ToString());
        cmd.Parameters.AddWithValue("$seq", seq);
        cmd.Parameters.AddWithValue("$json", JsonSerializer.Serialize(envelope, JsonOptions));
        cmd.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("o"));
        int inserted = cmd.ExecuteNonQuery();
        tx.Commit();
        return inserted == 1;
    }

    /// <summary>
    /// 状态推进(S2-E 编排器专用,具体类扩展,不属 IRunStore 契约):
    /// 事务内校验迁移合法性(非法返回 false 不动任何状态),成功则 state_version/seq 各 +1,
    /// terminal 时写入 terminal_reason/terminal_at/error_class/error_code;
    /// 提交后追加对应事件(run.started/run.state_changed/run.terminal),事件追加幂等且不阻塞状态推进。
    /// 事件序保证:run.started 只在 queued→starting 时产出,terminal 事件必然在其后。
    /// </summary>
    public async Task<bool> AdvanceStateAsync(RunId runId, RunState next, string? terminalReason = null,
        ErrorClass? errorClass = null, string? errorCode = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var connection = StorageDatabase.Open(_databasePath);
        using var tx = connection.BeginTransaction(System.Data.IsolationLevel.Serializable, deferred: false);

        string currentWire;
        long seq;
        using (var reader = Query(connection, tx,
            "SELECT state, seq FROM runs WHERE run_id = $run_id;",
            ("$run_id", runId.ToString())))
        {
            if (!reader.Read())
            {
                throw new KeyNotFoundException($"run {runId} 不存在。");
            }

            currentWire = reader.GetString(0);
            seq = reader.GetInt64(1);
        }

        RunState current = ParseState(currentWire);
        if (!RunStateMachine.CanTransition(current, next))
        {
            // 非法迁移(含重复推进/已 terminal):不动任何状态,幂等返回 false。
            return false;
        }

        long newSeq = seq + 1;
        string now = DateTimeOffset.UtcNow.ToString("o");
        if (RunStateMachine.IsTerminal(next))
        {
            StorageDatabase.Execute(connection, """
                UPDATE runs SET state = $state, state_version = state_version + 1, seq = $seq,
                    terminal_reason = $reason, terminal_at = $now, error_class = $error_class, error_code = $error_code,
                    updated_at = $now
                WHERE run_id = $run_id;
                """, tx,
                ("$state", next.ToWireCode()),
                ("$seq", newSeq),
                ("$reason", (object?)terminalReason ?? DBNull.Value),
                ("$now", now),
                ("$error_class", errorClass is null ? (object)DBNull.Value : errorClass.Value.ToWireCode()),
                ("$error_code", (object?)errorCode ?? DBNull.Value),
                ("$run_id", runId.ToString()));
        }
        else
        {
            StorageDatabase.Execute(connection, """
                UPDATE runs SET state = $state, state_version = state_version + 1, seq = $seq, updated_at = $now
                WHERE run_id = $run_id;
                """, tx,
                ("$state", next.ToWireCode()),
                ("$seq", newSeq),
                ("$now", now),
                ("$run_id", runId.ToString()));
        }

        tx.Commit();

        string eventType = (current, next) switch
        {
            (RunState.Queued, RunState.Starting) => "run.started",
            (_, RunState.Succeeded) or (_, RunState.FailedProvider) or (_, RunState.FailedLocal) or (_, RunState.Cancelled) => "run.terminal",
            _ => "run.state_changed",
        };
        var envelope = new EventEnvelopeV1
        {
            EventId = Guid.NewGuid(),
            Type = eventType,
            Source = "worker",
            Ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            IdempotencyKey = $"{runId}|{newSeq}",
            RunId = runId.Value,
            Seq = newSeq,
            Payload = next.ToWireCode(),
        };
        try
        {
            TryAppendEvent(runId, newSeq, envelope);
        }
        catch (Exception)
        {
            // 事件是投影,失败不阻塞状态推进;恢复期可重放。
        }

        return true;
    }

    /// <summary>非 terminal 的活动 run 清单(编排器观察循环用)。</summary>
    public IReadOnlyList<RunId> EnumerateActiveRuns()
    {
        var result = new List<RunId>();
        using var connection = StorageDatabase.Open(_databasePath);
        using var reader = Query(connection, null,
            "SELECT run_id FROM runs WHERE state IN ('queued','starting','running') ORDER BY created_at;");
        while (reader.Read())
        {
            result.Add(RunId.FromString(reader.GetString(0)));
        }

        return result;
    }

    /// <summary>标记副作用已开始(side_effect_marked=1);幂等。之后禁止 provider fallback。</summary>
    public void MarkSideEffects(RunId runId)
    {
        using var connection = StorageDatabase.Open(_databasePath);
        using var tx = connection.BeginTransaction(System.Data.IsolationLevel.Serializable, deferred: false);
        StorageDatabase.Execute(connection, """
            UPDATE runs SET side_effect_marked = 1, updated_at = $now
            WHERE run_id = $run_id;
            """, tx,
            ("$now", DateTimeOffset.UtcNow.ToString("o")),
            ("$run_id", runId.ToString()));
        tx.Commit();
    }

    /// <summary>
    /// provider 已失败但进程树尚未确认退出时持久化失败意图。运行保持 non-terminal，
    /// 因而 runKey 继续占用；首个失败胜出，用户取消一旦落盘则不再写入失败意图。
    /// </summary>
    public void RecordPendingFailure(RunId runId, ErrorClass errorClass, string errorCode)
    {
        using var connection = StorageDatabase.Open(_databasePath);
        using var tx = connection.BeginTransaction(System.Data.IsolationLevel.Serializable, deferred: false);
        StorageDatabase.Execute(connection, """
            UPDATE runs SET
                error_class = COALESCE(error_class, $error_class),
                error_code = COALESCE(error_code, $error_code),
                updated_at = $now
            WHERE run_id = $run_id
              AND state = 'running'
              AND cancel_requested_at IS NULL;
            """, tx,
            ("$error_class", errorClass.ToWireCode()),
            ("$error_code", errorCode),
            ("$now", DateTimeOffset.UtcNow.ToString("o")),
            ("$run_id", runId.ToString()));
        tx.Commit();
    }

    /// <summary>
    /// 进程树确认退出后的唯一终态提交点。取消标记、持久失败意图与当前 state
    /// 在同一个 IMMEDIATE 事务内读取并选择终态，确保先提交的用户取消不会在
    /// 读取与 terminal 写入之间被 provider 失败覆盖。
    /// </summary>
    public RunState SettleStoppedRun(
        RunId runId,
        ErrorClass? observedErrorClass,
        string? observedErrorCode,
        CancellationToken cancellationToken = default) =>
        SettleTerminal(
            runId,
            RunState.Running,
            observedErrorClass,
            observedErrorCode,
            allowSuccess: true,
            cancellationToken);

    /// <summary>
    /// starting 阶段失败的原子收尾；若用户取消已先提交，则取消优先于 provider
    /// 拒绝、进程启动失败或监督接管失败。
    /// </summary>
    public RunState SettleStartingFailure(
        RunId runId,
        ErrorClass errorClass,
        string errorCode,
        CancellationToken cancellationToken = default) =>
        SettleTerminal(
            runId,
            RunState.Starting,
            errorClass,
            errorCode,
            allowSuccess: false,
            cancellationToken);

    private RunState SettleTerminal(
        RunId runId,
        RunState expectedState,
        ErrorClass? observedErrorClass,
        string? observedErrorCode,
        bool allowSuccess,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var connection = StorageDatabase.Open(_databasePath);
        using var tx = connection.BeginTransaction(System.Data.IsolationLevel.Serializable, deferred: false);

        RunState current;
        long seq;
        bool cancelled;
        ErrorClass? storedErrorClass;
        string? storedErrorCode;
        using (var reader = Query(connection, tx, """
            SELECT state, seq, cancel_requested_at, error_class, error_code
            FROM runs WHERE run_id = $run_id;
            """, ("$run_id", runId.ToString())))
        {
            if (!reader.Read())
            {
                throw new KeyNotFoundException($"run {runId} 不存在。");
            }

            current = ParseState(reader.GetString(0));
            seq = reader.GetInt64(1);
            cancelled = !reader.IsDBNull(2);
            storedErrorClass = reader.IsDBNull(3) ? null : ParseErrorClass(reader.GetString(3));
            storedErrorCode = reader.IsDBNull(4) ? null : reader.GetString(4);
        }

        if (RunStateMachine.IsTerminal(current) || current != expectedState)
        {
            tx.Commit();
            return current;
        }

        ErrorClass? failureClass = storedErrorClass ?? observedErrorClass;
        string? failureCode = storedErrorCode ?? observedErrorCode;
        RunState next;
        string terminalReason;
        ErrorClass? terminalErrorClass;
        string? terminalErrorCode;
        if (cancelled)
        {
            next = RunState.Cancelled;
            terminalReason = "cancelled";
            terminalErrorClass = ErrorClass.Cancelled;
            terminalErrorCode = "user_stop";
        }
        else if (failureClass is not null)
        {
            next = failureClass is ErrorClass.Internal or ErrorClass.Config
                ? RunState.FailedLocal
                : RunState.FailedProvider;
            terminalReason = failureCode ?? "provider_failed";
            terminalErrorClass = failureClass;
            terminalErrorCode = failureCode ?? "provider_failed";
        }
        else
        {
            if (!allowSuccess)
            {
                throw new InvalidOperationException("starting 失败收尾必须提供 errorClass。");
            }

            next = RunState.Succeeded;
            terminalReason = "succeeded";
            terminalErrorClass = null;
            terminalErrorCode = null;
        }

        long newSeq = seq + 1;
        string now = DateTimeOffset.UtcNow.ToString("o");
        StorageDatabase.Execute(connection, """
            UPDATE runs SET state = $state, state_version = state_version + 1, seq = $seq,
                terminal_reason = $reason, terminal_at = $now, error_class = $error_class,
                error_code = $error_code, updated_at = $now
            WHERE run_id = $run_id;
            """, tx,
            ("$state", next.ToWireCode()),
            ("$seq", newSeq),
            ("$reason", terminalReason),
            ("$now", now),
            ("$error_class", terminalErrorClass is null
                ? (object)DBNull.Value
                : terminalErrorClass.Value.ToWireCode()),
            ("$error_code", (object?)terminalErrorCode ?? DBNull.Value),
            ("$run_id", runId.ToString()));
        tx.Commit();

        try
        {
            TryAppendEvent(runId, newSeq, new EventEnvelopeV1
            {
                EventId = Guid.NewGuid(),
                Type = "run.terminal",
                Source = "worker",
                Ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                IdempotencyKey = $"{runId}|{newSeq}",
                RunId = runId.Value,
                Seq = newSeq,
                Payload = next.ToWireCode(),
            });
        }
        catch (Exception)
        {
            // 事件是投影，失败不阻塞已经原子提交的终态。
        }

        return next;
    }

    private static SqliteDataReader Query(SqliteConnection connection, SqliteTransaction? tx, string sql,
        params (string Name, object Value)[] args)
    {
        var cmd = connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        foreach (var (name, value) in args)
        {
            cmd.Parameters.AddWithValue(name, value);
        }

        return cmd.ExecuteReader();
    }

    private static RunState ParseState(string wire) =>
        RunStateMachine.TryFromWireCode(wire, out RunState state)
            ? state
            : throw new InvalidOperationException($"runs 表中存在未知 state '{wire}'。");

    private static TaskKind ParseTaskKind(string wire) => wire switch
    {
        "chat" => TaskKind.Chat,
        "query" => TaskKind.Query,
        "modify" => TaskKind.Modify,
        "resume" => TaskKind.Resume,
        "probe" => TaskKind.Probe,
        _ => throw new InvalidOperationException($"runs 表中存在未知 task_kind '{wire}'。"),
    };

    private static ErrorClass ParseErrorClass(string wire) =>
        ErrorClassCodes.TryFromWireCode(wire, out ErrorClass errorClass)
            ? errorClass
            : throw new InvalidOperationException($"runs 表中存在未知 error_class '{wire}'。");
}
