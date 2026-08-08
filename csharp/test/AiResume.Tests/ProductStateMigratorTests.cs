using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using AiResume.Core;
using AiResume.Storage;
using AiResume.Worker.Migration;
using AiResume.Worker.Products;
using Microsoft.Data.Sqlite;
using Xunit;

namespace AiResume.Tests;

[Collection(SqliteCollection.Name)]
public class ProductStateMigratorTests : IDisposable
{
    private readonly string _tempRoot;
    private readonly string _legacyAppDir;
    private readonly string _shadowRoot;
    private readonly string _dbPath;

    public ProductStateMigratorTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "AiResumeMigratorTests_" + Guid.NewGuid().ToString("N"));
        _legacyAppDir = Path.Combine(_tempRoot, "legacy");
        _shadowRoot = Path.Combine(_tempRoot, "shadow");
        _dbPath = Path.Combine(_tempRoot, "shadow", "state.db");
        Directory.CreateDirectory(_legacyAppDir);
        Directory.CreateDirectory(_shadowRoot);
        AiResume.Storage.StorageDatabase.Migrate(_dbPath);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try
        {
            if (Directory.Exists(_tempRoot))
            {
                Directory.Delete(_tempRoot, recursive: true);
            }
        }
        catch
        {
            // 清理失败不阻塞测试
        }
    }

    private ProductStateMigrator CreateMigrator()
    {
        return new ProductStateMigrator(
            new ProductConfigStore(_shadowRoot),
            new ProductStateStore(_dbPath));
    }

    private static string J(string raw) => System.Text.Json.JsonEncodedText.Encode(raw).ToString();

    private static string WriteLegacyConfig(string legacyDir, string json)
    {
        string path = Path.Combine(legacyDir, "config.json");
        File.WriteAllText(path, json);
        return path;
    }

    private static string WriteLegacyState(string legacyDir, string json)
    {
        string path = Path.Combine(legacyDir, "state.json");
        File.WriteAllText(path, json);
        return path;
    }

    private static string WriteLegacyCompletion(string legacyDir, string json)
    {
        string path = Path.Combine(legacyDir, "completion-events-seen.json");
        File.WriteAllText(path, json);
        return path;
    }

    private static string BuildLegacyConfigJson()
    {
        // 14 个白名单字段 + 若干凭据字段(必须被跳过且不泄露)
        return $$"""
            {
              "enabled": true,
              "armed": true,
              "armCycleId": "cycle-2026-08-04",
              "continuous": false,
              "selected": [
                { "name": "alpha", "path": "{{J(@"C:\proj\alpha")}}" },
                { "name": "beta", "path": "{{J(@"C:\proj\beta")}}" }
              ],
              "customProjects": [
                { "name": "custom1", "path": "{{J(@"C:\custom\one")}}" }
              ],
              "hiddenProjects": ["hidden1", "hidden2"],
              "projectHome": "{{J(@"C:\projects")}}",
              "probeIntervalMinutes": 30,
              "probeModel": "claude-sonnet-4-20250514",
              "resumeModel": "claude-opus-4-20250514",
              "resumePrompt": "continue from where you left off",
              "skipPermissions": false,
              "dirtyGuard": "stash",
              "feishuAppSecret": "S3CRET_FEISHU_APP",
              "feishuSecret": "S3CRET_FEISHU",
              "openaiApiKey": "sk-S3CRET_OPENAI",
              "deepseekApiKey": "S3CRET_DEEPSEEK",
              "aiProxy": "http://proxy.invalid:8080",
              "quotaRefreshMinutes": 5,
              "someOtherKey": "someValue"
            }
            """;
    }

    private static string BuildLegacyStateJson()
    {
        // Unix 秒整数:1785919800 = 2026-08-04T17:30:00Z (约)
        // 1786015384 = 2026-08-05T20:43:04Z (约)
        return $$"""
            {
              "phase": "waiting",
              "cycleId": "cycle-2026-08-04",
              "sawLimited": true,
              "lastProbeUtc": "2026-08-04T17:50:02.06+00:00",
              "limitedRefires": 3,
              "realFiveHourResetUtc": 1785919800,
              "realSevenDayResetUtc": 1786015384,
              "realResetProbedUtc": 1786015384,
              "realFiveHourUtil": 0.75,
              "projectStatus": {
                "{{J(@"C:\proj\alpha")}}": "success",
                "{{J(@"C:\proj\beta")}}": "error"
              },
              "targetId": "legacy-target-1",
              "targetEndUtc": "2026-08-04T18:00:00Z",
              "firedForId": "legacy-fired-1"
            }
            """;
    }

    private static string BuildLegacyCompletionJson()
    {
        return """{"event1":"hash1","event2":"hash2"}""";
    }

    private static string ReadShadowConfigText(string shadowRoot)
    {
        string path = Path.Combine(shadowRoot, "config.json");
        return File.Exists(path) ? File.ReadAllText(path) : string.Empty;
    }

    private static string SerializeReport(MigrationReport report)
    {
        return JsonSerializer.Serialize(report, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = true
        });
    }

    [Fact]
    public void 白名单14字段全部迁入shadow配置()
    {
        // Arrange
        WriteLegacyConfig(_legacyAppDir, BuildLegacyConfigJson());
        WriteLegacyState(_legacyAppDir, BuildLegacyStateJson());
        WriteLegacyCompletion(_legacyAppDir, BuildLegacyCompletionJson());
        var migrator = CreateMigrator();

        // Act
        MigrationReport report = migrator.Run(new MigrationOptions(
            _legacyAppDir, _shadowRoot, _dbPath, DryRun: false, Force: false));

        // Assert
        Assert.True(report.Success);
        var configItem = report.Items.Single(i => i.Source == "config.json");
        Assert.Equal("migrated", configItem.Status);
        Assert.Equal(14, configItem.Count);

        var configStore = new ProductConfigStore(_shadowRoot);
        ProductConfig cfg = configStore.Load();
        Assert.True(cfg.Enabled);
        Assert.True(cfg.Armed);
        Assert.Equal("cycle-2026-08-04", cfg.ArmCycleId);
        Assert.False(cfg.Continuous);
        Assert.Equal(2, cfg.Selected.Count);
        Assert.Equal("alpha", cfg.Selected[0].Name);
        Assert.Equal(@"C:\proj\alpha", cfg.Selected[0].Path);
        Assert.Equal("beta", cfg.Selected[1].Name);
        Assert.Equal(@"C:\proj\beta", cfg.Selected[1].Path);
        Assert.Single(cfg.CustomProjects);
        Assert.Equal("custom1", cfg.CustomProjects[0].Name);
        Assert.Equal(@"C:\custom\one", cfg.CustomProjects[0].Path);
        Assert.Equal(new List<string> { "hidden1", "hidden2" }, cfg.HiddenProjects);
        Assert.Equal(@"C:\projects", cfg.ProjectHome);
        Assert.Equal(30, cfg.ProbeIntervalMinutes);
        Assert.Equal("claude-sonnet-4-20250514", cfg.ProbeModel);
        Assert.Equal("claude-opus-4-20250514", cfg.ResumeModel);
        Assert.Equal("continue from where you left off", cfg.ResumePrompt);
        Assert.False(cfg.SkipPermissions);
        Assert.Equal("stash", cfg.DirtyGuard);
        // quotaRefreshMinutes 不在白名单,保留默认值 15
        Assert.Equal(15, cfg.QuotaRefreshMinutes);
    }

    [Fact]
    public void 凭据字段不进目标且报告不泄露()
    {
        // Arrange
        WriteLegacyConfig(_legacyAppDir, BuildLegacyConfigJson());
        WriteLegacyState(_legacyAppDir, BuildLegacyStateJson());
        WriteLegacyCompletion(_legacyAppDir, BuildLegacyCompletionJson());
        var migrator = CreateMigrator();

        // Act
        MigrationReport report = migrator.Run(new MigrationOptions(
            _legacyAppDir, _shadowRoot, _dbPath, DryRun: false, Force: false));

        // Assert: shadow config.json 全文不含任何凭据值
        string shadowConfigText = ReadShadowConfigText(_shadowRoot);
        Assert.DoesNotContain("S3CRET_FEISHU_APP", shadowConfigText);
        Assert.DoesNotContain("S3CRET_FEISHU", shadowConfigText);
        Assert.DoesNotContain("sk-S3CRET_OPENAI", shadowConfigText);
        Assert.DoesNotContain("S3CRET_DEEPSEEK", shadowConfigText);
        Assert.DoesNotContain("proxy.invalid", shadowConfigText);

        // Assert: 报告序列化后也不含任何凭据值
        string reportJson = SerializeReport(report);
        Assert.DoesNotContain("S3CRET_FEISHU_APP", reportJson);
        Assert.DoesNotContain("S3CRET_FEISHU", reportJson);
        Assert.DoesNotContain("sk-S3CRET_OPENAI", reportJson);
        Assert.DoesNotContain("S3CRET_DEEPSEEK", reportJson);
        Assert.DoesNotContain("proxy.invalid", reportJson);

        // 报告里也不得出现凭据键名
        Assert.DoesNotContain("feishuAppSecret", reportJson);
        Assert.DoesNotContain("feishuSecret", reportJson);
        Assert.DoesNotContain("openaiApiKey", reportJson);
        Assert.DoesNotContain("deepseekApiKey", reportJson);
        Assert.DoesNotContain("aiProxy", reportJson);
    }

    [Fact]
    public void SkippedNonOwnedKeys计数正确()
    {
        // Arrange
        WriteLegacyConfig(_legacyAppDir, BuildLegacyConfigJson());
        WriteLegacyState(_legacyAppDir, BuildLegacyStateJson());
        WriteLegacyCompletion(_legacyAppDir, BuildLegacyCompletionJson());
        var migrator = CreateMigrator();

        // Act
        MigrationReport report = migrator.Run(new MigrationOptions(
            _legacyAppDir, _shadowRoot, _dbPath, DryRun: false, Force: false));

        // Assert: fixture 有 21 个顶层键 - 14 个白名单 = 7 个非自有键。
        // (真实生产 config.json 是 47 - 14 = 33,但那是现场数据,不是本 fixture 的数字。)
        Assert.Equal(7, report.SkippedNonOwnedKeys);
    }

    [Fact]
    public void Unix秒整数正确转为DateTimeOffset()
    {
        // Arrange
        WriteLegacyConfig(_legacyAppDir, BuildLegacyConfigJson());
        WriteLegacyState(_legacyAppDir, BuildLegacyStateJson());
        WriteLegacyCompletion(_legacyAppDir, BuildLegacyCompletionJson());
        var migrator = CreateMigrator();

        // Act
        MigrationReport report = migrator.Run(new MigrationOptions(
            _legacyAppDir, _shadowRoot, _dbPath, DryRun: false, Force: false));

        // Assert
        Assert.True(report.Success);
        var stateStore = new ProductStateStore(_dbPath);
        CheckerState state = stateStore.Load();

        // 1785919800 = 2026-08-04T17:30:00Z
        DateTimeOffset expectedFiveHour = DateTimeOffset.FromUnixTimeSeconds(1785919800);
        Assert.NotNull(state.RealFiveHourResetUtc);
        Assert.Equal(expectedFiveHour, state.RealFiveHourResetUtc.Value);

        // 1786015384 = 2026-08-05T20:43:04Z
        DateTimeOffset expectedSevenDay = DateTimeOffset.FromUnixTimeSeconds(1786015384);
        Assert.NotNull(state.RealSevenDayResetUtc);
        Assert.Equal(expectedSevenDay, state.RealSevenDayResetUtc.Value);

        Assert.NotNull(state.RealResetProbedUtc);
        Assert.Equal(expectedSevenDay, state.RealResetProbedUtc.Value);
    }

    [Fact]
    public void ISO字符串lastProbeUtc正确解析()
    {
        // Arrange
        WriteLegacyConfig(_legacyAppDir, BuildLegacyConfigJson());
        WriteLegacyState(_legacyAppDir, BuildLegacyStateJson());
        WriteLegacyCompletion(_legacyAppDir, BuildLegacyCompletionJson());
        var migrator = CreateMigrator();

        // Act
        MigrationReport report = migrator.Run(new MigrationOptions(
            _legacyAppDir, _shadowRoot, _dbPath, DryRun: false, Force: false));

        // Assert
        Assert.True(report.Success);
        var stateStore = new ProductStateStore(_dbPath);
        CheckerState state = stateStore.Load();

        Assert.NotNull(state.LastProbeUtc);
        DateTimeOffset expected = DateTimeOffset.Parse("2026-08-04T17:50:02.06+00:00");
        Assert.Equal(expected, state.LastProbeUtc.Value);
    }

    [Fact]
    public void 现役独有字段计入DroppedLegacyFields且不报错()
    {
        // Arrange
        WriteLegacyConfig(_legacyAppDir, BuildLegacyConfigJson());
        WriteLegacyState(_legacyAppDir, BuildLegacyStateJson());
        WriteLegacyCompletion(_legacyAppDir, BuildLegacyCompletionJson());
        var migrator = CreateMigrator();

        // Act
        MigrationReport report = migrator.Run(new MigrationOptions(
            _legacyAppDir, _shadowRoot, _dbPath, DryRun: false, Force: false));

        // Assert: targetId/targetEndUtc/firedForId 三个字段被丢弃
        Assert.True(report.Success);
        Assert.Equal(3, report.DroppedLegacyFields);

        var stateItem = report.Items.Single(i => i.Source == "state.json");
        Assert.Equal("migrated", stateItem.Status);
        // 10 个目标字段成功迁移
        Assert.Equal(10, stateItem.Count);
    }

    [Fact]
    public void 幂等_连跑两次第二次全部skipped且内容一致()
    {
        // Arrange
        WriteLegacyConfig(_legacyAppDir, BuildLegacyConfigJson());
        WriteLegacyState(_legacyAppDir, BuildLegacyStateJson());
        WriteLegacyCompletion(_legacyAppDir, BuildLegacyCompletionJson());
        var migrator = CreateMigrator();

        // Act: 第一次迁移
        MigrationReport firstReport = migrator.Run(new MigrationOptions(
            _legacyAppDir, _shadowRoot, _dbPath, DryRun: false, Force: false));
        Assert.True(firstReport.Success);

        string shadowConfigAfterFirst = ReadShadowConfigText(_shadowRoot);
        var stateStore = new ProductStateStore(_dbPath);
        CheckerState stateAfterFirst = stateStore.Load();
        string stateJsonAfterFirst = JsonSerializer.Serialize(stateAfterFirst, CheckerState.JsonOptions);

        // Act: 第二次迁移(源未变化)
        MigrationReport secondReport = migrator.Run(new MigrationOptions(
            _legacyAppDir, _shadowRoot, _dbPath, DryRun: false, Force: false));

        // Assert: 全部 skipped
        Assert.True(secondReport.Success);
        Assert.All(secondReport.Items, item => Assert.Equal("skipped", item.Status));
        Assert.Equal("已迁移且源未变化", secondReport.Items.Single(i => i.Source == "config.json").Reason);
        Assert.Equal("已迁移且源未变化", secondReport.Items.Single(i => i.Source == "state.json").Reason);

        // 内容与第一次完全一致
        string shadowConfigAfterSecond = ReadShadowConfigText(_shadowRoot);
        Assert.Equal(shadowConfigAfterFirst, shadowConfigAfterSecond);

        CheckerState stateAfterSecond = stateStore.Load();
        string stateJsonAfterSecond = JsonSerializer.Serialize(stateAfterSecond, CheckerState.JsonOptions);
        Assert.Equal(stateJsonAfterFirst, stateJsonAfterSecond);
    }

    [Fact]
    public void 源变化后重跑会真正重迁()
    {
        // Arrange
        WriteLegacyConfig(_legacyAppDir, BuildLegacyConfigJson());
        WriteLegacyState(_legacyAppDir, BuildLegacyStateJson());
        WriteLegacyCompletion(_legacyAppDir, BuildLegacyCompletionJson());
        var migrator = CreateMigrator();

        // 第一次迁移
        MigrationReport firstReport = migrator.Run(new MigrationOptions(
            _legacyAppDir, _shadowRoot, _dbPath, DryRun: false, Force: false));
        Assert.True(firstReport.Success);

        // 修改源 config.json(改一个白名单字段值)
        string modifiedConfig = BuildLegacyConfigJson().Replace("\"probeIntervalMinutes\": 30", "\"probeIntervalMinutes\": 45");
        WriteLegacyConfig(_legacyAppDir, modifiedConfig);

        // Act: 第二次迁移(源已变化)
        MigrationReport secondReport = migrator.Run(new MigrationOptions(
            _legacyAppDir, _shadowRoot, _dbPath, DryRun: false, Force: false));

        // Assert: 真正重迁
        Assert.True(secondReport.Success);
        var configItem = secondReport.Items.Single(i => i.Source == "config.json");
        Assert.Equal("migrated", configItem.Status);
        Assert.Equal(14, configItem.Count);

        var configStore = new ProductConfigStore(_shadowRoot);
        ProductConfig cfg = configStore.Load();
        Assert.Equal(45, cfg.ProbeIntervalMinutes);
    }

    [Fact]
    public void Force_true无视标记强制重迁()
    {
        // Arrange
        WriteLegacyConfig(_legacyAppDir, BuildLegacyConfigJson());
        WriteLegacyState(_legacyAppDir, BuildLegacyStateJson());
        WriteLegacyCompletion(_legacyAppDir, BuildLegacyCompletionJson());
        var migrator = CreateMigrator();

        // 第一次迁移
        MigrationReport firstReport = migrator.Run(new MigrationOptions(
            _legacyAppDir, _shadowRoot, _dbPath, DryRun: false, Force: false));
        Assert.True(firstReport.Success);

        // Act: Force=true 重跑(源未变化)
        MigrationReport secondReport = migrator.Run(new MigrationOptions(
            _legacyAppDir, _shadowRoot, _dbPath, DryRun: false, Force: true));

        // Assert: 无条件重迁
        Assert.True(secondReport.Success);
        var configItem = secondReport.Items.Single(i => i.Source == "config.json");
        Assert.Equal("migrated", configItem.Status);
        var stateItem = secondReport.Items.Single(i => i.Source == "state.json");
        Assert.Equal("migrated", stateItem.Status);
    }

    [Fact]
    public void DryRun_true不产生任何目标产物()
    {
        // Arrange
        WriteLegacyConfig(_legacyAppDir, BuildLegacyConfigJson());
        WriteLegacyState(_legacyAppDir, BuildLegacyStateJson());
        WriteLegacyCompletion(_legacyAppDir, BuildLegacyCompletionJson());
        var migrator = CreateMigrator();

        // Act
        MigrationReport report = migrator.Run(new MigrationOptions(
            _legacyAppDir, _shadowRoot, _dbPath, DryRun: true, Force: false));

        // Assert: 报告正确
        Assert.True(report.DryRun);
        Assert.True(report.Success);
        var configItem = report.Items.Single(i => i.Source == "config.json");
        Assert.Equal("migrated", configItem.Status);
        Assert.Equal(14, configItem.Count);
        Assert.Null(configItem.BackupPath);

        // 不产生 shadow config
        Assert.False(File.Exists(Path.Combine(_shadowRoot, "config.json")));

        // 不产生标记
        Assert.False(File.Exists(Path.Combine(_shadowRoot, "migration-state.json")));

        // 不产生备份目录
        Assert.False(Directory.Exists(Path.Combine(_shadowRoot, "migration-backup")));

        // 不写 state 数据库(表存在但无数据)
        var stateStore = new ProductStateStore(_dbPath);
        CheckerState state = stateStore.Load();
        Assert.Equal(CheckerState.PhaseIdle, state.Phase);
        Assert.Null(state.LastProbeUtc);
    }

    [Fact]
    public void 备份文件与源逐字节一致且源文件仍然存在()
    {
        // Arrange
        string configJson = BuildLegacyConfigJson();
        string stateJson = BuildLegacyStateJson();
        string completionJson = BuildLegacyCompletionJson();
        WriteLegacyConfig(_legacyAppDir, configJson);
        WriteLegacyState(_legacyAppDir, stateJson);
        WriteLegacyCompletion(_legacyAppDir, completionJson);
        var migrator = CreateMigrator();

        // Act
        MigrationReport report = migrator.Run(new MigrationOptions(
            _legacyAppDir, _shadowRoot, _dbPath, DryRun: false, Force: false));

        // Assert: 备份存在且逐字节一致
        Assert.True(report.Success);
        string backupDir = Path.Combine(_shadowRoot, "migration-backup");
        Assert.True(Directory.Exists(backupDir));
        string[] backupFiles = Directory.GetFiles(backupDir, "*", SearchOption.AllDirectories);
        Assert.Equal(3, backupFiles.Length);

        string configBackup = backupFiles.Single(f => Path.GetFileName(f) == "config.json");
        Assert.Equal(configJson, File.ReadAllText(configBackup));

        string stateBackup = backupFiles.Single(f => Path.GetFileName(f) == "state.json");
        Assert.Equal(stateJson, File.ReadAllText(stateBackup));

        string completionBackup = backupFiles.Single(f => Path.GetFileName(f) == "completion-events-seen.json");
        Assert.Equal(completionJson, File.ReadAllText(completionBackup));

        // 源文件仍然存在(只备份不删)
        Assert.True(File.Exists(Path.Combine(_legacyAppDir, "config.json")));
        Assert.True(File.Exists(Path.Combine(_legacyAppDir, "state.json")));
        Assert.True(File.Exists(Path.Combine(_legacyAppDir, "completion-events-seen.json")));
    }

    [Fact]
    public void config损坏时该项failed_state仍迁移成功_整体Success为false()
    {
        // Arrange
        WriteLegacyConfig(_legacyAppDir, "{ this is not valid json !!!");
        WriteLegacyState(_legacyAppDir, BuildLegacyStateJson());
        WriteLegacyCompletion(_legacyAppDir, BuildLegacyCompletionJson());
        var migrator = CreateMigrator();

        // Act
        MigrationReport report = migrator.Run(new MigrationOptions(
            _legacyAppDir, _shadowRoot, _dbPath, DryRun: false, Force: false));

        // Assert
        Assert.False(report.Success);
        var configItem = report.Items.Single(i => i.Source == "config.json");
        Assert.Equal("failed", configItem.Status);
        Assert.False(string.IsNullOrEmpty(configItem.Reason));

        var stateItem = report.Items.Single(i => i.Source == "state.json");
        Assert.Equal("migrated", stateItem.Status);
        Assert.Equal(10, stateItem.Count);

        // state 仍然成功写入
        var stateStore = new ProductStateStore(_dbPath);
        CheckerState state = stateStore.Load();
        Assert.Equal("waiting", state.Phase);
    }

    [Fact]
    public void 源文件全部缺失时不算失败()
    {
        // Arrange: 不写任何源文件
        var migrator = CreateMigrator();

        // Act
        MigrationReport report = migrator.Run(new MigrationOptions(
            _legacyAppDir, _shadowRoot, _dbPath, DryRun: false, Force: false));

        // Assert
        Assert.True(report.Success);
        // config/state 缺失 → missing;但 completion-events-seen 恒为 skipped
        // ——它不迁移的理由是"两侧 eventId 算法不同",与文件在不在无关,
        // 文件存在时也一样跳过,所以不能因为这次恰好不存在就改口说 missing。
        Assert.Equal("missing", report.Items.Single(i => i.Source == "config.json").Status);
        Assert.Equal("missing", report.Items.Single(i => i.Source == "state.json").Status);
        Assert.Equal("skipped", report.Items.Single(i => i.Source == "completion-events-seen.json").Status);
        Assert.All(report.Items, item => Assert.Null(item.SourceSha256));
        Assert.Equal(0, report.SkippedNonOwnedKeys);
        Assert.Equal(0, report.DroppedLegacyFields);
    }

    [Fact]
    public void completion_events_seen恒为skipped且reason非空()
    {
        // Arrange
        WriteLegacyConfig(_legacyAppDir, BuildLegacyConfigJson());
        WriteLegacyState(_legacyAppDir, BuildLegacyStateJson());
        WriteLegacyCompletion(_legacyAppDir, BuildLegacyCompletionJson());
        var migrator = CreateMigrator();

        // Act: 实跑
        MigrationReport report = migrator.Run(new MigrationOptions(
            _legacyAppDir, _shadowRoot, _dbPath, DryRun: false, Force: false));

        // Assert
        Assert.True(report.Success);
        var completionItem = report.Items.Single(i => i.Source == "completion-events-seen.json");
        Assert.Equal("skipped", completionItem.Status);
        Assert.False(string.IsNullOrEmpty(completionItem.Reason));
        Assert.Contains("eventId", completionItem.Reason);

        // 再验证 DryRun 下也是 skipped
        MigrationReport dryRunReport = migrator.Run(new MigrationOptions(
            _legacyAppDir, _shadowRoot, _dbPath, DryRun: true, Force: false));
        var dryRunCompletionItem = dryRunReport.Items.Single(i => i.Source == "completion-events-seen.json");
        Assert.Equal("skipped", dryRunCompletionItem.Status);
        Assert.False(string.IsNullOrEmpty(dryRunCompletionItem.Reason));
    }
}