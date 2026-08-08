using AiResume.Storage;
using AiResume.Worker.Products;

namespace AiResume.Worker.Migration;

/// <summary>
/// <c>AiResume.Worker.exe migrate [--dry-run] [--force]</c> 的命令行外壳(S9)。
///
/// 只负责解析参数、组装 <see cref="ProductStateMigrator"/> 并把报告打成人类可读文本。
/// **报告里绝不出现凭据键名或值**——非自有键只给数量(见 S9 规格 §1.1)。
/// </summary>
public static class MigrationCommand
{
    /// <summary>现役 AI Resume 的 AppDir(只读来源,全程不写不删)。</summary>
    public static string DefaultLegacyAppDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ClaudeResume");

    public static int Run(string[] args)
    {
        bool dryRun = args.Any(a => string.Equals(a, "--dry-run", StringComparison.OrdinalIgnoreCase));
        bool force = args.Any(a => string.Equals(a, "--force", StringComparison.OrdinalIgnoreCase));

        var options = new MigrationOptions(
            LegacyAppDir: DefaultLegacyAppDir,
            ShadowRoot: ShadowPaths.Root,
            DatabasePath: ShadowPaths.RunDatabasePath,
            DryRun: dryRun,
            Force: force);

        Directory.CreateDirectory(ShadowPaths.Root);

        var migrator = new ProductStateMigrator(
            new ProductConfigStore(ShadowPaths.Root),
            new ProductStateStore(ShadowPaths.RunDatabasePath));

        MigrationReport report;
        try
        {
            report = migrator.Run(options);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"迁移失败:{ex.Message}");
            return 1;
        }

        Console.WriteLine(Format(report, options));
        return report.Success ? 0 : 1;
    }

    /// <summary>把报告渲染成对账用文本。列宽固定,便于和上一次运行逐行比对。</summary>
    public static string Format(MigrationReport report, MigrationOptions options)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine(report.DryRun
            ? $"迁移演练(dry-run) {report.StartedAt:O}"
            : $"迁移执行 {report.StartedAt:O}");
        sb.AppendLine($"  来源(只读){options.LegacyAppDir}");
        sb.AppendLine($"  目标      {options.ShadowRoot}");
        sb.AppendLine();

        foreach (MigrationItemResult item in report.Items)
        {
            string count = item.Status == "migrated" ? $"{item.Count} 字段" : "—";
            string sha = item.SourceSha256 is { Length: >= 8 } s ? $"sha256={s[..8]}…" : "sha256=—";
            sb.AppendLine($"  {item.Source,-30}{item.Status,-10}{count,-10}{sha}");
            if (!string.IsNullOrEmpty(item.Reason))
            {
                sb.AppendLine($"  {string.Empty,-30}└ {item.Reason}");
            }

            if (item.BackupPath is not null)
            {
                sb.AppendLine($"  {string.Empty,-30}└ 已备份 {item.BackupPath}");
            }
        }

        sb.AppendLine();
        sb.AppendLine($"  跳过非自有键 {report.SkippedNonOwnedKeys} 个(含全部凭据,未读取其值)");
        sb.AppendLine($"  丢弃目标无对应字段 {report.DroppedLegacyFields} 个");
        sb.AppendLine($"结论:{(report.Success ? "成功" : "存在失败项")}");
        if (report.DryRun)
        {
            sb.AppendLine("(演练模式:未写入任何目标、未备份、未记录迁移标记)");
        }

        return sb.ToString();
    }
}
