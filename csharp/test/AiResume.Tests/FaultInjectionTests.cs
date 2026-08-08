using System.Text;
using AiResume.Core;
using AiResume.Storage;
using AiResume.Worker.Products;
using Microsoft.Data.Sqlite;
using Xunit;

namespace AiResume.Tests;

/// <summary>
/// S10-O/P3 故障注入:持久状态文件在各种「写到一半」形态下,程序是安全失败还是静默错误?
/// 覆盖 C# 侧全部持久化状态:产品配置(config.json)、项目索引(project-index.json)、
/// 布防周期状态(product_state 表)、run store/登记表/事件队列(runs.db 的
/// runs/process_registry/run_events/outbox 表,同库同损)。
///
/// 红线:损坏输入不得默认成危险值(armed=true / enabled=true / skipPermissions=true)。
/// 已知缺口(见 docs/RED-LINE-COVERAGE.md §P3 与晨报):
///  - D5 ProductConfig.SkipPermissions 类默认值为 true,损坏配置回默认 → 续跑会带
///    --dangerously-skip-permissions;与现役 PowerShell 默认一致,是否收紧待人工裁决,
///    本文件用用例钉住现状而非私自改动。
///  - D6 截断/损坏 JSON 目前是「容错回默认」而非「拒绝加载」(对齐现役 Get-CcuConfig
///    catch 后给默认的语义);除 skipPermissions 外默认值均为安全侧。
/// </summary>
[Collection(SqliteCollection.Name)]
public sealed class FaultInjectionTests : IDisposable
{
    private readonly List<string> _dirs = new();

    public void Dispose()
    {
        foreach (string dir in _dirs)
        {
            try
            {
                // 还原只读属性后再删,否则 Directory.Delete 会失败。
                foreach (string d in Directory.EnumerateDirectories(dir, "*", SearchOption.AllDirectories))
                {
                    File.SetAttributes(d, FileAttributes.Normal);
                }

                SqliteConnection.ClearAllPools();
                Directory.Delete(dir, recursive: true);
            }
            catch
            {
                // 清理失败不掩盖断言结果。
            }
        }
    }

    private string NewDir()
    {
        string dir = Path.Combine(Path.GetTempPath(), "s10o-fault-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        _dirs.Add(dir);
        return dir;
    }

    private static void WriteRaw(string path, string content) =>
        File.WriteAllText(path, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

    /// <summary>
    /// Windows 实测:目录的只读属性不阻止在其中创建文件(2026-08-06 本用例首跑证实),
    /// 构造真正不可写目录必须用 ACL deny;测试结束必须移除 deny 否则清理会失败。
    /// </summary>
    private static void DenyWrite(string dir)
    {
        using var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("icacls", $"\"{dir}\" /deny \"{Environment.UserName}:(OI)(CI)(W,WA,AD,DC)\"")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
        });
        p!.WaitForExit(10_000);
        Assert.Equal(0, p.ExitCode);
    }

    private static void RemoveDeny(string dir)
    {
        try
        {
            using var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("icacls", $"\"{dir}\" /remove:d \"{Environment.UserName}\"")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
            });
            p!.WaitForExit(10_000);
        }
        catch
        {
            // 移除失败由 Dispose 的目录清理兼容兜底(失败不掩盖断言)。
        }
    }

    // ================================================================
    // 一、产品配置 config.json(ProductConfigStore)
    // ================================================================

    [Fact]
    public void Config_empty_file_is_treated_as_missing_with_safe_defaults()
    {
        // 形态:文件为空(0 字节)→ 视同不存在,不抛未捕获异常。
        string dir = NewDir();
        WriteRaw(Path.Combine(dir, "config.json"), string.Empty);

        ProductConfig config = new ProductConfigStore(dir).Load();

        Assert.False(config.Armed, "空文件不得带出 armed=true");
        Assert.False(config.Enabled, "空文件不得带出 enabled=true");
        Assert.False(config.Continuous, "空文件不得带出 continuous=true");
    }

    [Fact]
    public void Config_truncated_json_falls_back_to_safe_defaults_and_keeps_file()
    {
        // 形态:JSON 截断在中间。现役语义(Get-CcuConfig 对齐):容错回默认而非抛;
        // 本用例钉住「回退值全部落在安全侧」且原文件原样保留(加载是只读路径)。
        string dir = NewDir();
        string path = Path.Combine(dir, "config.json");
        string truncated = """{"armed":true,"enabled":true,"selected":[{"name":"x","path":"C:\\""";
        WriteRaw(path, truncated);

        ProductConfig config = new ProductConfigStore(dir).Load();

        Assert.False(config.Armed, "截断配置不得解析成 armed=true(危险值红线)");
        Assert.False(config.Enabled, "截断配置不得解析成 enabled=true");
        Assert.Equal(truncated, File.ReadAllText(path)); // 原文件保留,未被静默清空/重写
    }

    [Fact]
    public void Config_wrong_field_types_rejected_safely_not_dangerous_defaults()
    {
        // 形态:合法 JSON 但字段类型不对 → 明确拒绝(反序列化抛 → 容错),
        // 绝不把 "yes" 之类当成 true。
        string dir = NewDir();
        WriteRaw(Path.Combine(dir, "config.json"),
            """{"armed":"yes","enabled":1,"probeIntervalMinutes":"abc"}""");

        ProductConfig config = new ProductConfigStore(dir).Load();

        Assert.False(config.Armed);
        Assert.False(config.Enabled);
    }

    [Fact]
    public void Config_valid_json_proves_parser_is_not_just_returning_defaults()
    {
        // 防假绿对照:合法内容必须真的被解析(armed:true 能读出来),
        // 否则上面几条「损坏→false」用例可能是空转。
        string dir = NewDir();
        WriteRaw(Path.Combine(dir, "config.json"),
            """{"armed":true,"enabled":false,"probeModel":"opus"}""");

        ProductConfig config = new ProductConfigStore(dir).Load();

        Assert.True(config.Armed);
        Assert.Equal("opus", config.ProbeModel);
    }

    [Fact]
    public void Config_utf8_bom_and_crlf_parses_normally()
    {
        // 形态:UTF-8 BOM + CRLF 混排 → 正常解析。
        string dir = NewDir();
        string json = "{\r\n  \"armed\": false,\r\n  \"probeModel\": \"sonnet\"\r\n}";
        File.WriteAllText(Path.Combine(dir, "config.json"), json,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));

        ProductConfig config = new ProductConfigStore(dir).Load();

        Assert.Equal("sonnet", config.ProbeModel);
        Assert.False(config.Armed);
    }

    [Fact]
    public void Config_unwritable_directory_save_fails_loudly_and_creates_nothing()
    {
        // 形态:目录不可写(ACL deny)→ 明确报错,不得吞掉;且不得留下半写产物。
        string dir = NewDir();
        DenyWrite(dir);
        try
        {
            var store = new ProductConfigStore(dir);
            Assert.ThrowsAny<Exception>(() => store.Save(ProductConfig.CreateDefault()));
            Assert.False(File.Exists(Path.Combine(dir, "config.json")),
                "不可写目录里不得产生配置文件");
        }
        finally
        {
            RemoveDeny(dir);
        }
    }

    [Fact]
    public void Config_tmp_residue_complete_or_truncated_never_read_as_config()
    {
        // 形态:.tmp-* 残留。C# 侧当前无「tmp 恢复候选」语义(D2),但红线底线是:
        // 无论 tmp 内容完整还是截断,都绝不能被当成配置读进来(尤其不能带出 armed=true)。
        string dir = NewDir();
        WriteRaw(Path.Combine(dir, "config.json"), """{"armed":false}""");
        WriteRaw(Path.Combine(dir, "config.json.tmp-complete"), """{"armed":true}""");
        WriteRaw(Path.Combine(dir, "config.json.tmp-truncated"), """{"armed":tr""");

        ProductConfig config = new ProductConfigStore(dir).Load();

        Assert.False(config.Armed, "tmp 残留(完整或截断)都不得被当成配置加载");
    }

    [Fact]
    public void Known_gap_D5_corrupted_config_defaults_skipPermissions_true_pins_current()
    {
        // D5 已裁决(2026-08-06,用户确认改 fail-closed):损坏配置回默认后
        // SkipPermissions 必须是 false,续跑不得追加 --dangerously-skip-permissions。
        //
        // 原缺口:类默认值是 true,于是一个被截断的 config.json 会让无人值守的后台续跑
        // **静默地以跳过全部权限确认运行**。改为 false 后,同样的损坏配置会让续跑卡在
        // 权限确认上——卡住是安全的失败,静默全权限不是。
        string dir = NewDir();
        WriteRaw(Path.Combine(dir, "config.json"), "<<<garbage>>>");

        ProductConfig config = new ProductConfigStore(dir).Load();

        Assert.False(config.SkipPermissions,
            "损坏配置必须 fail-closed:skipPermissions 回默认应为 false");
    }

    // ================================================================
    // 二、项目索引 project-index.json(ProjectIndex)
    // ================================================================

    [Fact]
    public void Index_empty_or_truncated_or_bad_version_all_yield_empty_index()
    {
        // 索引是纯缓存:任何损坏形态 → 空索引(触发全量重扫),不得抛、不得带出脏条目。
        string dir = NewDir();
        DateTimeOffset probeTime = new(2026, 8, 6, 0, 0, 0, TimeSpan.Zero);
    
        string emptyPath = Path.Combine(dir, "empty.json");
        WriteRaw(emptyPath, string.Empty);
        Assert.False(ProjectIndex.Load(emptyPath).TryGet("C:\\x", probeTime, out _));
    
        string truncatedPath = Path.Combine(dir, "truncated.json");
        WriteRaw(truncatedPath, "{\"Version\":1,\"Entries\":[{\"SessionDir\":\"C:\\x");
        Assert.False(ProjectIndex.Load(truncatedPath).TryGet("C:\\x", probeTime, out _));
    
        string badVersionPath = Path.Combine(dir, "badversion.json");
        WriteRaw(badVersionPath, """{"Version":999,"Entries":[]}""");
        Assert.False(ProjectIndex.Load(badVersionPath).TryGet("C:\\x", probeTime, out _));
    
        // 对照:损坏文件原样保留(加载是只读路径,不得静默清空)。
        Assert.Equal(string.Empty, File.ReadAllText(emptyPath));
    }
    
    [Fact]
    public void Index_bom_crlf_parses_and_single_corrupt_entry_is_skipped_not_fatal()
    {
        string dir = NewDir();
        string path = Path.Combine(dir, "index.json");
        DateTimeOffset probeTime = new(2026, 8, 6, 0, 0, 0, TimeSpan.Zero);
        // 第一条缺 SessionDir(损坏),第二条合法:BOM+CRLF 下整体仍要可用。
        string json = "{\r\n\"Version\": 1,\r\n\"Entries\": [\r\n" +
                      "{\"DirWriteUtc\": \"2026-08-06T00:00:00Z\"},\r\n" +
                      "{\"SessionDir\": \"C:\\\\proj\", \"DirWriteUtc\": \"2026-08-06T00:00:00Z\", " +
                      "\"JsonlPath\": null, \"JsonlWriteUtc\": \"2026-08-06T00:00:00Z\", \"Cwd\": \"C:\\\\proj\"}\r\n]}";
        File.WriteAllText(path, json, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
    
        ProjectIndex index = ProjectIndex.Load(path);
    
        Assert.True(index.TryGet("C:\\proj", probeTime, out ProjectIndexEntry entry),
            "BOM+CRLF 的合法条目必须可命中");
        Assert.Equal("C:\\proj", entry.Cwd);
    }

    [Fact]
    public void Index_unwritable_dir_save_fails_silently_by_design_cache_only()
    {
        // 设计语义:索引写盘失败静默返回 false(纯缓存,不阻断发现流程)。
        // 本用例钉住「不抛异常、返回 false」,与 config 的「必须报错」语义不同,
        // 差异已列入 fail-closed 清单说明。
        string dir = NewDir();
        var index = new ProjectIndex();
        index.Put(new ProjectIndexEntry("C:\\x", DateTimeOffset.UtcNow, null, DateTimeOffset.MinValue, null));
        DenyWrite(dir);
        try
        {
            bool wrote = index.SaveIfChanged(Path.Combine(dir, "index.json"));
            Assert.False(wrote, "不可写目录下索引写盘应静默失败并返回 false(缓存语义)");
        }
        finally
        {
            RemoveDeny(dir);
        }
    }

    // ================================================================
    // 三、布防周期状态 product_state(ProductStateStore)
    // ================================================================

    [Fact]
    public void ProductState_corrupted_or_empty_json_yields_idle_default()
    {
        // 损坏/空 state_json → 默认状态(phase=idle)。idle 是安全侧:
        // 不布防、不续跑;cycleId 为空使任何旧周期自然失效。
        string dir = NewDir();
        string dbPath = Path.Combine(dir, "runs.db");
        StorageDatabase.Migrate(dbPath);

        using (var connection = StorageDatabase.Open(dbPath))
        {
            StorageDatabase.Execute(connection,
                "INSERT INTO product_state(id, state_json, updated_at) VALUES (1, $json, $now);",
                null, ("$json", """{"phase":"resuming","cycleId":"""), ("$now", DateTimeOffset.UtcNow.ToString("o")));
        }

        CheckerState state = new ProductStateStore(dbPath).Load();
        Assert.Equal(CheckerState.PhaseIdle, state.Phase);
        Assert.Equal(string.Empty, state.CycleId);

        // 空字符串形态。
        using (var connection = StorageDatabase.Open(dbPath))
        {
            StorageDatabase.Execute(connection,
                "UPDATE product_state SET state_json = '' WHERE id = 1;");
        }

        CheckerState emptyState = new ProductStateStore(dbPath).Load();
        Assert.Equal(CheckerState.PhaseIdle, emptyState.Phase);
    }

    // ================================================================
    // 四、SQLite 库文件本体(runs / process_registry / run_events / outbox 同库)
    // ================================================================

    [Fact]
    public void Sqlite_zero_byte_file_is_treated_as_new_and_migrates_cleanly()
    {
        // 形态:0 字节库文件 → 视同不存在,迁移正常建表(不得抛未捕获异常)。
        string dir = NewDir();
        string dbPath = Path.Combine(dir, "runs.db");
        File.WriteAllBytes(dbPath, Array.Empty<byte>());

        StorageDatabase.Migrate(dbPath);

        using var connection = StorageDatabase.Open(dbPath);
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='runs';";
        Assert.Equal(1L, (long)cmd.ExecuteScalar()!);
    }

    [Fact]
    public void Sqlite_garbage_file_fails_loudly_not_silently_empty()
    {
        // 形态:库文件被截断/写花(非 0 字节垃圾)→ 必须明确报错,
        // 绝不静默当成空库在损坏文件之上重建(那会吞掉全部 run 历史)。
        string dir = NewDir();
        string dbPath = Path.Combine(dir, "runs.db");
        byte[] garbage = new byte[4096];
        new Random(42).NextBytes(garbage);
        File.WriteAllBytes(dbPath, garbage);
        long lengthBefore = new FileInfo(dbPath).Length;

        Assert.ThrowsAny<SqliteException>(() => StorageDatabase.Migrate(dbPath));
        Assert.Equal(lengthBefore, new FileInfo(dbPath).Length); // 原文件未被覆盖重建
    }
}
