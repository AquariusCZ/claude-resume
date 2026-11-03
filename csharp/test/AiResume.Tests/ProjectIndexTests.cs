using AiResume.Core;
using AiResume.Worker.Products;
using Xunit;

namespace AiResume.Tests;

/// <summary>S7-A 项目发现索引化测试:覆盖索引首次构建、命中复用、增量更新、删除、空结果缓存、损坏容错与原子写。</summary>
public sealed class ProjectIndexTests : IDisposable
{
    private readonly string _root = CreateTempRoot();
    private readonly string _discoveryRoot;
    private readonly string _fakeTemp = @"Z:\nonexistent-temp";
    private readonly string _fakeAppDir;
    private readonly string _indexPath;

    public ProjectIndexTests()
    {
        _discoveryRoot = Path.Combine(_root, "claude-projects");
        _fakeAppDir = Path.Combine(_root, "fake-appdir");
        _indexPath = Path.Combine(_root, "shadow", "project-index.json");
        Directory.CreateDirectory(_discoveryRoot);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (Exception)
        {
            // 清理失败不掩盖断言结果。
        }
    }

    private static string CreateTempRoot()
    {
        string dir = TestTemp.NewDir("s7a");
        Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>构造一个会话目录 + jsonl(含 cwd 行,可指定时间)。</summary>
    private string AddSession(string sessionName, string cwd, DateTimeOffset? mtime = null, string? extraLine = null)
    {
        string dir = Path.Combine(_discoveryRoot, sessionName);
        Directory.CreateDirectory(dir);
        string file = Path.Combine(dir, "cwd-" + Guid.NewGuid().ToString("N") + ".jsonl");
        string firstLine = "{\"type\":\"system\",\"cwd\":\"" + cwd.Replace("\\", "\\\\") + "\"}";
        string content = extraLine is null ? firstLine + "\n" : firstLine + "\n" + extraLine + "\n";
        File.WriteAllText(file, content);
        if (mtime.HasValue)
        {
            File.SetLastWriteTimeUtc(file, mtime.Value.UtcDateTime);
        }

        return file;
    }

    private static ProductConfig Config(Action<ProductConfig>? mutate = null)
    {
        var cfg = ProductConfig.CreateDefault();
        mutate?.Invoke(cfg);
        return cfg;
    }

    /// <summary>修改会话目录 mtime 以触发增量重解析。</summary>
    private static void TouchDir(string dir, DateTimeOffset mtime)
    {
        Directory.SetLastWriteTimeUtc(dir, mtime.UtcDateTime);
    }

    // ---- 1. 首次运行 ----

    [Fact]
    public void First_run_creates_index_file_with_correct_results()
    {
        string project = Path.Combine(_root, "repo-a");
        Directory.CreateDirectory(project);
        AddSession("s1", project, DateTimeOffset.UtcNow.AddMinutes(-10));

        var catalog = new ProjectCatalog(tempDir: _fakeTemp, productionAppDir: _fakeAppDir, indexPath: _indexPath);
        List<ProjectEntry> list = catalog.Discover(Config(), _discoveryRoot);

        ProjectEntry entry = Assert.Single(list);
        Assert.Equal("repo-a", entry.Name);
        Assert.Equal(project, entry.Path);
        Assert.True(File.Exists(_indexPath), "首次运行后索引文件应已创建");
    }

    // ---- 2. 二次运行命中索引 ----

    [Fact]
    public void Second_run_hits_index_without_reading_jsonl()
    {
        string project = Path.Combine(_root, "repo-b");
        Directory.CreateDirectory(project);
        string jsonl = AddSession("s1", project, DateTimeOffset.UtcNow.AddMinutes(-5));

        var catalog = new ProjectCatalog(tempDir: _fakeTemp, productionAppDir: _fakeAppDir, indexPath: _indexPath);
        List<ProjectEntry> first = catalog.Discover(Config(), _discoveryRoot);
        Assert.Single(first);

        // 破坏 jsonl 内容但保持目录 mtime 不变:若二次运行仍读文件,结果会变化。
        File.WriteAllText(jsonl, "not valid json at all\n");
        // 确保目录 mtime 未被写入操作改变(显式恢复)。
        TouchDir(Path.Combine(_discoveryRoot, "s1"), DateTimeOffset.UtcNow.AddMinutes(-5));

        List<ProjectEntry> second = catalog.Discover(Config(), _discoveryRoot);

        Assert.Single(second);
        Assert.Equal(first[0].Path, second[0].Path);
        Assert.Equal(first[0].LastWriteUtc, second[0].LastWriteUtc);
        // 命中索引时同样带着发现它的那份转录(队列行据此读临时工作区的会话名)。
        Assert.Equal(jsonl, first[0].TranscriptPath);
        Assert.Equal(jsonl, second[0].TranscriptPath);
    }

    // ---- 3. 增量更新 ----

    [Fact]
    public void Incremental_update_reparses_only_changed_dir()
    {
        string projectA = Path.Combine(_root, "repo-a");
        string projectB = Path.Combine(_root, "repo-b");
        Directory.CreateDirectory(projectA);
        Directory.CreateDirectory(projectB);
        AddSession("s-a", projectA, DateTimeOffset.UtcNow.AddHours(-2));
        AddSession("s-b", projectB, DateTimeOffset.UtcNow.AddHours(-1));

        var catalog = new ProjectCatalog(tempDir: _fakeTemp, productionAppDir: _fakeAppDir, indexPath: _indexPath);
        List<ProjectEntry> first = catalog.Discover(Config(), _discoveryRoot);
        Assert.Equal(2, first.Count);

        // 仅改动 s-b:新增 jsonl 并更新目录 mtime。
        string newProjectB = Path.Combine(_root, "repo-b-new");
        Directory.CreateDirectory(newProjectB);
        AddSession("s-b", newProjectB, DateTimeOffset.UtcNow);
        TouchDir(Path.Combine(_discoveryRoot, "s-b"), DateTimeOffset.UtcNow);

        // ProjectCatalog 有 3 秒内存缓存,不清除则第二次调用直接返回旧结果,看不到文件系统改动。
        catalog.ClearCache();
        List<ProjectEntry> second = catalog.Discover(Config(), _discoveryRoot);

        Assert.Equal(2, second.Count);
        Assert.Contains(second, e => e.Path == projectA);
        Assert.Contains(second, e => e.Path == newProjectB);
        Assert.DoesNotContain(second, e => e.Path == projectB);
    }

    // ---- 4. 目录删除 ----

    [Fact]
    public void Deleted_dir_disappears_and_index_entry_removed()
    {
        string projectA = Path.Combine(_root, "repo-a");
        string projectB = Path.Combine(_root, "repo-b");
        Directory.CreateDirectory(projectA);
        Directory.CreateDirectory(projectB);
        AddSession("s-a", projectA, DateTimeOffset.UtcNow.AddHours(-2));
        AddSession("s-b", projectB, DateTimeOffset.UtcNow.AddHours(-1));

        var catalog = new ProjectCatalog(tempDir: _fakeTemp, productionAppDir: _fakeAppDir, indexPath: _indexPath);
        List<ProjectEntry> first = catalog.Discover(Config(), _discoveryRoot);
        Assert.Equal(2, first.Count);

        // 删除 s-b 目录。
        Directory.Delete(Path.Combine(_discoveryRoot, "s-b"), recursive: true);

        // 绕开 3 秒内存缓存,强制重新发现。
        catalog.ClearCache();
        List<ProjectEntry> second = catalog.Discover(Config(), _discoveryRoot);

        ProjectEntry entry = Assert.Single(second);
        Assert.Equal(projectA, entry.Path);

        // 索引条目应被移除:重新加载索引,不应包含 s-b。
        var index = ProjectIndex.Load(_indexPath);
        Assert.False(index.TryGet(Path.Combine(_discoveryRoot, "s-b"), DateTimeOffset.MinValue, out _));
    }

    // ---- 4b. 续写已有会话:目录 mtime 不变 ----

    [Fact]
    public void Appending_to_an_older_session_moves_latest_even_when_dir_mtime_is_unchanged()
    {
        // 同一目录:先有 A,后来 fork 出 B(索引记成 B),之后又接着写 A——追加不改目录 mtime。
        string project = Path.Combine(_root, "repo-fork");
        Directory.CreateDirectory(project);
        string a = AddSession("s1", project, DateTimeOffset.UtcNow.AddHours(-3));
        string b = AddSession("s1", project, DateTimeOffset.UtcNow.AddHours(-2));
        string sessionDir = Path.Combine(_discoveryRoot, "s1");
        DateTimeOffset dirMtime = DateTimeOffset.UtcNow.AddHours(-2);
        TouchDir(sessionDir, dirMtime);

        var catalog = new ProjectCatalog(tempDir: _fakeTemp, productionAppDir: _fakeAppDir, indexPath: _indexPath);
        Assert.Equal(b, Assert.Single(catalog.Discover(Config(), _discoveryRoot)).TranscriptPath);

        File.AppendAllText(a, "{\"type\":\"user\",\"message\":{\"role\":\"user\",\"content\":\"x\"}}\n");
        File.SetLastWriteTimeUtc(a, DateTime.UtcNow.AddMinutes(-1));
        TouchDir(sessionDir, dirMtime);

        // 重开窗口(新实例读持久化的索引):最新那份是 A,「最近会话」时间是 A 的。
        var reopened = new ProjectCatalog(tempDir: _fakeTemp, productionAppDir: _fakeAppDir, indexPath: _indexPath);
        ProjectEntry entry = Assert.Single(reopened.Discover(Config(), _discoveryRoot));
        Assert.Equal(a, entry.TranscriptPath);
        Assert.Equal(File.GetLastWriteTimeUtc(a), entry.LastWriteUtc.UtcDateTime);
        Assert.Equal(sessionDir, reopened.SessionDirectoryOf(project));
    }

    [Fact]
    public void Same_latest_file_reuses_cached_cwd_but_reports_fresh_time()
    {
        string project = Path.Combine(_root, "repo-append");
        Directory.CreateDirectory(project);
        string jsonl = AddSession("s1", project, DateTimeOffset.UtcNow.AddHours(-1));
        var catalog = new ProjectCatalog(tempDir: _fakeTemp, productionAppDir: _fakeAppDir, indexPath: _indexPath);
        Assert.Single(catalog.Discover(Config(), _discoveryRoot));

        // 内容换成读不出 cwd 的东西:还是索引里那一份就不再读头部(否则项目会消失),时间则取列目录时的新值。
        File.WriteAllText(jsonl, "not valid json at all\n");
        catalog.ClearCache();
        ProjectEntry entry = Assert.Single(catalog.Discover(Config(), _discoveryRoot));
        Assert.Equal(project, entry.Path);
        Assert.Equal(File.GetLastWriteTimeUtc(jsonl), entry.LastWriteUtc.UtcDateTime);

        // 什么都没变:索引条目不标脏,不重写索引文件。
        DateTime written = File.GetLastWriteTimeUtc(_indexPath);
        File.SetLastWriteTimeUtc(_indexPath, written.AddMinutes(-5));
        catalog.ClearCache();
        Assert.Single(catalog.Discover(Config(), _discoveryRoot));
        Assert.Equal(written.AddMinutes(-5), File.GetLastWriteTimeUtc(_indexPath));
    }

    // ---- 5. 空结果缓存 ----

    [Fact]
    public void Empty_result_is_cached_and_not_retried()
    {
        // 无 jsonl 的目录。
        string emptyDir = Path.Combine(_discoveryRoot, "empty-session");
        Directory.CreateDirectory(emptyDir);
        TouchDir(emptyDir, DateTimeOffset.UtcNow.AddMinutes(-10));

        var catalog = new ProjectCatalog(tempDir: _fakeTemp, productionAppDir: _fakeAppDir, indexPath: _indexPath);
        List<ProjectEntry> first = catalog.Discover(Config(), _discoveryRoot);
        Assert.Empty(first);

        // 二次运行:目录 mtime 未变,应命中空结果缓存,不产生任何项目。
        catalog.ClearCache();
        List<ProjectEntry> second = catalog.Discover(Config(), _discoveryRoot);
        Assert.Empty(second);

        // 索引中应存在空条目(JsonlPath 为 null)。
        // 注意:必须用目录的**实际** mtime 查询——重新构造 DateTimeOffset.UtcNow.AddMinutes(-10)
        // 与 TouchDir 那次调用相差若干毫秒,精确比较必然失配。
        var index = ProjectIndex.Load(_indexPath);
        DateTimeOffset actualDirMtime = Directory.GetLastWriteTimeUtc(emptyDir);
        Assert.True(index.TryGet(emptyDir, actualDirMtime, out ProjectIndexEntry entry));
        Assert.Null(entry.JsonlPath);
        Assert.Null(entry.Cwd);
    }

    // ---- 6. 索引损坏容错 ----

    [Fact]
    public void Corrupt_index_falls_back_to_full_scan_and_rebuilds()
    {
        string project = Path.Combine(_root, "repo-corrupt");
        Directory.CreateDirectory(project);
        AddSession("s1", project, DateTimeOffset.UtcNow.AddMinutes(-3));

        // 写入非法 JSON。
        Directory.CreateDirectory(Path.GetDirectoryName(_indexPath)!);
        File.WriteAllText(_indexPath, "{broken json!!");

        var catalog = new ProjectCatalog(tempDir: _fakeTemp, productionAppDir: _fakeAppDir, indexPath: _indexPath);
        List<ProjectEntry> list = catalog.Discover(Config(), _discoveryRoot);

        ProjectEntry entry = Assert.Single(list);
        Assert.Equal(project, entry.Path);

        // 索引应被重建为合法 JSON。AddSession 设置的是 jsonl 文件的 mtime,
        // 索引键用的是**会话目录**的 mtime,必须读回实际值再查。
        var index = ProjectIndex.Load(_indexPath);
        string sessionDir = Path.Combine(_discoveryRoot, "s1");
        DateTimeOffset actualDirMtime = Directory.GetLastWriteTimeUtc(sessionDir);
        Assert.True(index.TryGet(sessionDir, actualDirMtime, out _));
    }

    [Fact]
    public void Wrong_version_index_falls_back_to_full_scan()
    {
        string project = Path.Combine(_root, "repo-version");
        Directory.CreateDirectory(project);
        AddSession("s1", project, DateTimeOffset.UtcNow.AddMinutes(-3));

        // 写入错误 Version 的合法 JSON。
        Directory.CreateDirectory(Path.GetDirectoryName(_indexPath)!);
        File.WriteAllText(_indexPath, """{"Version":999,"Entries":[]}""");

        var catalog = new ProjectCatalog(tempDir: _fakeTemp, productionAppDir: _fakeAppDir, indexPath: _indexPath);
        List<ProjectEntry> list = catalog.Discover(Config(), _discoveryRoot);

        ProjectEntry entry = Assert.Single(list);
        Assert.Equal(project, entry.Path);
    }

    // ---- 8. 原子写 ----

    [Fact]
    public void SaveIfChanged_returns_false_when_no_change_and_no_temp_left()
    {
        string project = Path.Combine(_root, "repo-atomic");
        Directory.CreateDirectory(project);
        AddSession("s1", project, DateTimeOffset.UtcNow.AddMinutes(-3));

        var catalog = new ProjectCatalog(tempDir: _fakeTemp, productionAppDir: _fakeAppDir, indexPath: _indexPath);
        catalog.Discover(Config(), _discoveryRoot);
        Assert.True(File.Exists(_indexPath));

        // 无变化时 SaveIfChanged 应返回 false 且不写盘。
        var index = ProjectIndex.Load(_indexPath);
        bool saved = index.SaveIfChanged(_indexPath);
        Assert.False(saved);

        // 不应残留临时文件。
        string? tmpFile = Directory.GetFiles(Path.GetDirectoryName(_indexPath)!, "*.tmp").FirstOrDefault();
        Assert.Null(tmpFile);
    }

    [Fact]
    public void Atomic_write_leaves_no_temp_file_after_success()
    {
        string project = Path.Combine(_root, "repo-atomic2");
        Directory.CreateDirectory(project);
        AddSession("s1", project, DateTimeOffset.UtcNow.AddMinutes(-3));

        var catalog = new ProjectCatalog(tempDir: _fakeTemp, productionAppDir: _fakeAppDir, indexPath: _indexPath);
        catalog.Discover(Config(), _discoveryRoot);

        // 索引写入成功后不应残留 .tmp 文件。
        string? tmpFile = Directory.GetFiles(Path.GetDirectoryName(_indexPath)!, "*.tmp").FirstOrDefault();
        Assert.Null(tmpFile);
    }
}