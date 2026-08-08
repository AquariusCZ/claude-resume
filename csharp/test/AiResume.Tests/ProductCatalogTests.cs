using AiResume.Core;
using AiResume.Worker.Products;
using Xunit;

namespace AiResume.Tests;

/// <summary>S5-A 项目发现与 shadow 产品配置测试。</summary>
public sealed class ProductCatalogTests : IDisposable
{
    private readonly string _root = CreateTempRoot();
    private readonly string _discoveryRoot;
    private readonly string _fakeTemp = @"Z:\nonexistent-temp";
    private readonly string _fakeAppDir;

    public ProductCatalogTests()
    {
        _discoveryRoot = Path.Combine(_root, "claude-projects");
        _fakeAppDir = Path.Combine(_root, "fake-appdir");
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
        string dir = Path.Combine(Path.GetTempPath(), "s5a-" + Guid.NewGuid().ToString("N"));
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

    [Fact]
    public void Dynamic_discovery_finds_cwd_from_latest_jsonl()
    {
        string project = Path.Combine(_root, "repo-a");
        Directory.CreateDirectory(project);
        AddSession("abc", project, DateTimeOffset.UtcNow.AddMinutes(-10));

        var catalog = new ProjectCatalog(tempDir: _fakeTemp, productionAppDir: _fakeAppDir);
        List<ProjectEntry> list = catalog.Discover(Config(), _discoveryRoot);

        ProjectEntry entry = Assert.Single(list);
        Assert.Equal("repo-a", entry.Name);
        Assert.Equal(project, entry.Path);
    }

    /// <summary>
    /// S7-C 回归:shadow 根及其子目录不得被当成用户项目。
    /// 额度探测把 claude 的工作目录设在 shadow 根,会在 ~/.claude/projects 下留下会话;
    /// 不排除的话 ClaudeResumeShadow 会冒进续跑队列(实测已发生)。
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Shadow_root_is_excluded_from_discovery(bool useSubdirectory)
    {
        string shadowRoot = Path.Combine(_root, "fake-shadow");
        string cwd = useSubdirectory ? Path.Combine(shadowRoot, "probe") : shadowRoot;
        Directory.CreateDirectory(cwd);

        string realProject = Path.Combine(_root, "repo-real");
        Directory.CreateDirectory(realProject);

        AddSession("shadow-sess", cwd, DateTimeOffset.UtcNow);
        AddSession("real-sess", realProject, DateTimeOffset.UtcNow.AddMinutes(-5));

        var catalog = new ProjectCatalog(
            tempDir: _fakeTemp, productionAppDir: _fakeAppDir, shadowRoot: shadowRoot);
        List<ProjectEntry> list = catalog.Discover(Config(), _discoveryRoot);

        ProjectEntry entry = Assert.Single(list);
        Assert.Equal(realProject, entry.Path);
    }

    [Fact]
    public void Newest_jsonl_in_session_dir_wins()
    {
        string oldProject = Path.Combine(_root, "repo-old");
        string newProject = Path.Combine(_root, "repo-new");
        Directory.CreateDirectory(oldProject);
        Directory.CreateDirectory(newProject);
        AddSession("sess", oldProject, DateTimeOffset.UtcNow.AddDays(-1));
        AddSession("sess", newProject, DateTimeOffset.UtcNow);

        var catalog = new ProjectCatalog(tempDir: _fakeTemp, productionAppDir: _fakeAppDir);
        List<ProjectEntry> list = catalog.Discover(Config(), _discoveryRoot);

        ProjectEntry entry = Assert.Single(list);
        Assert.Equal(newProject, entry.Path);
    }

    [Fact]
    public void Results_sorted_by_recency_descending()
    {
        string oldProject = Path.Combine(_root, "repo-old");
        string newProject = Path.Combine(_root, "repo-new");
        Directory.CreateDirectory(oldProject);
        Directory.CreateDirectory(newProject);
        AddSession("a-old", oldProject, DateTimeOffset.UtcNow.AddDays(-2));
        AddSession("b-new", newProject, DateTimeOffset.UtcNow.AddHours(-1));

        var catalog = new ProjectCatalog(tempDir: _fakeTemp, productionAppDir: _fakeAppDir);
        List<ProjectEntry> list = catalog.Discover(Config(), _discoveryRoot);

        Assert.Equal(2, list.Count);
        Assert.Equal(newProject, list[0].Path);
        Assert.Equal(oldProject, list[1].Path);
    }

    [Fact]
    public void Same_cwd_from_multiple_sessions_is_deduplicated()
    {
        string project = Path.Combine(_root, "repo-shared");
        Directory.CreateDirectory(project);
        AddSession("sess-1", project, DateTimeOffset.UtcNow.AddHours(-3));
        AddSession("sess-2", project, DateTimeOffset.UtcNow.AddHours(-1));

        var catalog = new ProjectCatalog(tempDir: _fakeTemp, productionAppDir: _fakeAppDir);
        List<ProjectEntry> list = catalog.Discover(Config(), _discoveryRoot);

        Assert.Single(list);
    }

    [Fact]
    public void Hidden_projects_are_excluded()
    {
        string secret = Path.Combine(_root, "secret-repo");
        Directory.CreateDirectory(secret);
        AddSession("s1", secret);
        string ok = Path.Combine(_root, "ok-repo");
        Directory.CreateDirectory(ok);
        AddSession("s2", ok, DateTimeOffset.UtcNow.AddHours(1));

        var catalog = new ProjectCatalog(tempDir: _fakeTemp, productionAppDir: _fakeAppDir);
        List<ProjectEntry> list = catalog.Discover(Config(c => c.HiddenProjects.Add(secret)), _discoveryRoot);

        ProjectEntry entry = Assert.Single(list);
        Assert.Equal("ok-repo", entry.Name);
    }

    [Fact]
    public void Missing_cwd_directory_is_skipped()
    {
        AddSession("s1", Path.Combine(_root, "does-not-exist"));
        AddSession("s2", Path.Combine(_root, "exists-repo"), DateTimeOffset.UtcNow.AddHours(1));
        Directory.CreateDirectory(Path.Combine(_root, "exists-repo"));

        var catalog = new ProjectCatalog(tempDir: _fakeTemp, productionAppDir: _fakeAppDir);
        List<ProjectEntry> list = catalog.Discover(Config(), _discoveryRoot);

        ProjectEntry entry = Assert.Single(list);
        Assert.Equal("exists-repo", entry.Name);
    }

    [Fact]
    public void Custom_projects_are_appended_with_name_fallback_and_dedup()
    {
        string custom = Path.Combine(_root, "custom-repo");
        Directory.CreateDirectory(custom);
        AddSession("s1", custom, DateTimeOffset.UtcNow.AddHours(-1));

        string alsoDiscovered = Path.Combine(_root, "discovered-repo");
        Directory.CreateDirectory(alsoDiscovered);
        AddSession("s2", alsoDiscovered, DateTimeOffset.UtcNow);

        var catalog = new ProjectCatalog(tempDir: _fakeTemp, productionAppDir: _fakeAppDir);
        List<ProjectEntry> list = catalog.Discover(Config(c =>
        {
            c.CustomProjects.Add(new ProjectRef { Name = "我的自定义", Path = custom });
            c.CustomProjects.Add(new ProjectRef { Path = Path.Combine(_root, "missing-custom") });
        }), _discoveryRoot);

        Assert.Equal(2, list.Count); // custom 与 discovered 去重;缺失 custom 不收录
        // 现役语义:动态已发现同路径时保留动态条目(自定义 name 不覆盖);缺失 custom 不收录。
        Assert.Contains(list, e => e.Path == custom);
        Assert.Contains(list, e => e.Path == alsoDiscovered);
    }

    [Fact]
    public void Missing_discovery_root_returns_only_custom()
    {
        string custom = Path.Combine(_root, "custom-only");
        Directory.CreateDirectory(custom);

        var catalog = new ProjectCatalog(tempDir: _fakeTemp, productionAppDir: _fakeAppDir);
        List<ProjectEntry> list = catalog.Discover(Config(c => c.CustomProjects.Add(new ProjectRef { Path = custom })), _discoveryRoot);

        ProjectEntry entry = Assert.Single(list);
        Assert.Equal(custom, entry.Path);
    }

    [Fact]
    public void Cache_serves_within_window_and_fingerprint_change_recomputes()
    {
        string project = Path.Combine(_root, "cached-repo");
        Directory.CreateDirectory(project);
        AddSession("s1", project);

        var catalog = new ProjectCatalog(tempDir: _fakeTemp, productionAppDir: _fakeAppDir);
        List<ProjectEntry> first = catalog.Discover(Config(), _discoveryRoot);
        Assert.Single(first);

        // 3 秒内新增会话:同指纹命中缓存,结果不变。
        string late = Path.Combine(_root, "late-repo");
        Directory.CreateDirectory(late);
        AddSession("s2", late, DateTimeOffset.UtcNow.AddMinutes(1));
        List<ProjectEntry> cached = catalog.Discover(Config(), _discoveryRoot);
        Assert.Single(cached);
        Assert.Equal(first[0].Path, cached[0].Path);

        // 指纹变化(custom 改变):立即重算。
        var cfg2 = Config(c => c.CustomProjects.Add(new ProjectRef { Path = Path.Combine(_root, "custom-now") }));
        Directory.CreateDirectory(Path.Combine(_root, "custom-now"));
        List<ProjectEntry> recomputed = catalog.Discover(cfg2, _discoveryRoot);
        Assert.Equal(3, recomputed.Count);
    }

    [Fact]
    public void Real_temp_dir_cwd_is_excluded_unless_temp_boundary_injected()
    {
        string project = Path.Combine(_root, "temp-project");
        Directory.CreateDirectory(project);
        AddSession("s1", project);

        // 注入真实 temp 边界:测试项目位于系统 temp 下 → 被排除。
        var strict = new ProjectCatalog(tempDir: Path.GetTempPath(), productionAppDir: _fakeAppDir);
        Assert.Empty(strict.Discover(Config(), _discoveryRoot));

        // 注入假 temp 边界:不被排除。
        var loose = new ProjectCatalog(tempDir: _fakeTemp, productionAppDir: _fakeAppDir);
        Assert.Single(loose.Discover(Config(), _discoveryRoot));
    }

    [Fact]
    public void Production_appdir_cwd_is_excluded()
    {
        string project = Path.Combine(_root, "appdir-project");
        Directory.CreateDirectory(project);
        AddSession("s1", project);

        var catalog = new ProjectCatalog(tempDir: _fakeTemp, productionAppDir: project);
        Assert.Empty(catalog.Discover(Config(), _discoveryRoot));
    }

    // ---- ProductConfig JSON 往返 ----

    [Fact]
    public void ProductConfig_json_roundtrip_preserves_all_fields()
    {
        var cfg = Config(c =>
        {
            c.Enabled = true;
            c.ArmCycleId = "abc123";
            c.Selected.Add(new ProjectRef { Name = "A", Path = @"C:\Repo\A" });
            c.CustomProjects.Add(new ProjectRef { Name = "B", Path = @"C:\Repo\B" });
            c.HiddenProjects.Add(@"C:\Repo\H");
            c.ProbeIntervalMinutes = 7;
            c.ProbeModel = "sonnet";
            c.ResumePrompt = "继续";
        });

        string json = System.Text.Json.JsonSerializer.Serialize(cfg, ProductConfig.JsonOptions);
        var round = System.Text.Json.JsonSerializer.Deserialize<ProductConfig>(json, ProductConfig.JsonOptions)!;

        Assert.True(round.Enabled);
        Assert.Equal("abc123", round.ArmCycleId);
        Assert.Equal("A", Assert.Single(round.Selected).Name);
        Assert.Equal(@"C:\Repo\B", Assert.Single(round.CustomProjects).Path);
        Assert.Equal(@"C:\Repo\H", Assert.Single(round.HiddenProjects));
        Assert.Equal(7, round.ProbeIntervalMinutes);
        Assert.Equal("sonnet", round.ProbeModel);
        Assert.Equal("继续", round.ResumePrompt);
    }

    [Fact]
    public void ProductConfig_deserialization_is_case_insensitive_with_defaults()
    {
        string json = """{"ENABLED":true,"probeModel":"opus"}""";
        var cfg = System.Text.Json.JsonSerializer.Deserialize<ProductConfig>(json, ProductConfig.JsonOptions)!;

        Assert.True(cfg.Enabled);
        Assert.Equal("opus", cfg.ProbeModel);
        Assert.Equal(15, cfg.ProbeIntervalMinutes);
        Assert.False(cfg.Armed);
    }

    [Fact]
    public void ProductConfigStore_save_load_roundtrip_and_corruption_fallback()
    {
        string shadowRoot = Path.Combine(_root, "shadow");
        var store = new ProductConfigStore(shadowRoot);

        // 未创建 → 默认。
        Assert.False(store.Load().Enabled);

        var cfg = Config(c => c.Enabled = true);
        store.Save(cfg);
        Assert.True(store.Load().Enabled);

        // 损坏文件 → 默认(容错)。
        File.WriteAllText(store.ConfigPath, "{broken json!!");
        Assert.False(store.Load().Enabled);
        Assert.Equal(15, store.Load().ProbeIntervalMinutes);
    }

    [Fact]
    public void ProductConfigStore_concurrent_saves_end_atomically()
    {
        string shadowRoot = Path.Combine(_root, "shadow-conc");
        var storeA = new ProductConfigStore(shadowRoot);
        var storeB = new ProductConfigStore(shadowRoot);

        Parallel.Invoke(
            () => storeA.Save(Config(c => c.Enabled = true)),
            () => storeB.Save(Config(c => c.Armed = true)));

        ProductConfig final = storeA.Load();
        // 最终必须是两个完整对象之一(原子替换,无混合)。
        bool exactlyA = final.Enabled && !final.Armed;
        bool exactlyB = final.Armed && !final.Enabled;
        Assert.True(exactlyA || exactlyB, $"最终状态必须是 A 或 B 完整对象,实际 enabled={final.Enabled}, armed={final.Armed}");
    }

    /// <summary>
    /// S10-O/P2 补:锁内读-改-写的并发红线。对应事故「锁外读旧快照后整体写回」:
    /// GUI(布防/项目增删)与续跑引擎(周期结束解除布防)同时写配置时,
    /// 后写者不得把对方字段覆盖回旧值。两个写者各自只改自己负责的字段,
    /// 交叉多轮后两边字段都必须完整保留——丢任何一条即为整体写回 bug。
    /// </summary>
    [Fact]
    public void ProductConfigStore_concurrent_updates_preserve_disjoint_fields()
    {
        string shadowRoot = Path.Combine(_root, "shadow-update-conc");
        var storeGui = new ProductConfigStore(shadowRoot);
        var storeEngine = new ProductConfigStore(shadowRoot);

        // 预置一个与两边都无关的字段,验证它也不被任何一方抹掉。
        storeGui.Update(c => c.ProbeModel = "opus");

        const int rounds = 50;
        Parallel.Invoke(
            () =>
            {
                for (int i = 0; i < rounds; i++)
                {
                    int seq = i;
                    storeGui.Update(c =>
                    {
                        c.Selected.Add(new ProjectRef { Name = "sel-" + seq, Path = "C:\\sel\\" + seq });
                    });
                }
            },
            () =>
            {
                for (int i = 0; i < rounds; i++)
                {
                    int seq = i;
                    storeEngine.Update(c =>
                    {
                        c.HiddenProjects.Add("hidden-" + seq);
                    });
                }
            });

        ProductConfig final = storeGui.Load();
        Assert.Equal(rounds, final.Selected.Count);
        Assert.Equal(rounds, final.HiddenProjects.Count);
        Assert.Equal("opus", final.ProbeModel);
        // 逐项不丢不重(丢更新的最直接指纹)。
        Assert.Equal(rounds, final.Selected.Select(p => p.Name).Distinct().Count());
        Assert.Equal(rounds, final.HiddenProjects.Distinct().Count());
    }

    // ---- 保留区:包管理器与应用安装目录 ----

    [Fact]
    public void WinGet包目录属于保留区不得当成项目()
    {
        // 实测现场:续跑队列第 3 位混进了
        // %LOCALAPPDATA%\Microsoft\WinGet\Packages\CodeZeno.ClaudeCodeUsageMonitor_…8wekyb3d8bbwe
        // —— 因为发现是按 AI 会话的 cwd 历史推的,在哪跑过就把哪当项目。
        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string wingetPkg = Path.Combine(local, "Microsoft", "WinGet", "Packages", "Some.Package_8wekyb3d8bbwe");

        var catalog = new ProjectCatalog(
            tempDir: _fakeTemp, productionAppDir: _fakeAppDir,
            indexPath: Path.Combine(_root, "idx.json"), shadowRoot: Path.Combine(_root, "shadow"));

        Assert.True(catalog.IsReserved(wingetPkg));
    }

    [Theory]
    [InlineData("ProgramFiles")]
    [InlineData("ProgramData")]
    public void 应用安装根目录属于保留区(string envName)
    {
        string? root = Environment.GetEnvironmentVariable(envName);

        // 这两个变量在 Windows 上一定存在;缺失说明测试环境本身不对,应当失败而不是静默跳过。
        Assert.False(string.IsNullOrWhiteSpace(root), $"本机缺少环境变量 {envName}");

        var catalog = new ProjectCatalog(
            tempDir: _fakeTemp, productionAppDir: _fakeAppDir,
            indexPath: Path.Combine(_root, "idx.json"), shadowRoot: Path.Combine(_root, "shadow"));

        Assert.True(catalog.IsReserved(Path.Combine(root!, "SomeVendor", "SomeApp")));
    }

    [Fact]
    public void 桌面下的普通项目不属于保留区()
    {
        // 反向用例:保留区扩大后不能误伤真项目,否则用户的项目会凭空消失。
        var catalog = new ProjectCatalog(
            tempDir: _fakeTemp, productionAppDir: _fakeAppDir,
            indexPath: Path.Combine(_root, "idx.json"), shadowRoot: Path.Combine(_root, "shadow"));

        string desktopProject = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "my-project");

        Assert.False(catalog.IsReserved(desktopProject));
    }
}
