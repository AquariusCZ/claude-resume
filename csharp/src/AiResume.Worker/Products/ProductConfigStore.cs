using System.Text;
using System.Text.Json;
using System.Threading;
using AiResume.Core;

namespace AiResume.Worker.Products;

/// <summary>
/// shadow 产品配置读写(产品状态迁移 S5-A)。
///
/// 锁语义对齐现役 config.json.write.lock:独占打开(FileShare.None)覆盖整个读-改-写事务,
/// 先写临时文件再原子替换(File.Move overwrite),任何时刻磁盘上要么旧完整内容要么新完整内容。
/// 读失败/损坏容错回默认值(现役 Get-CcuConfig 同样 catch 后给默认);写失败抛异常由调用方处置。
/// 只操作 shadow 目录(config.json),绝不触碰生产 AppDir。
/// </summary>
public sealed class ProductConfigStore
{
    private readonly string _configPath;

    public ProductConfigStore(string shadowRoot)
    {
        ArgumentNullException.ThrowIfNull(shadowRoot);
        _configPath = Path.Combine(shadowRoot, "config.json");
    }

    public string ConfigPath => _configPath;

    public ProductConfig Load() => LoadUnlocked();

    /// <summary>
    /// 在与 <see cref="Update"/> 相同的配置锁内读取快照。用于需要把配置与其它状态边界
    /// 组合成一致视图的调用方，避免读到一次提交中间的半成品。
    /// </summary>
    public T ReadLocked<T>(Func<ProductConfig, T> read)
    {
        ArgumentNullException.ThrowIfNull(read);
        EnsureDirectory();

        using var lockFs = AcquireLock(_configPath + ".write.lock");
        return read(LoadUnlocked());
    }

    /// <summary>
    /// **锁内**读-改-写:独占锁 → 重新读最新配置 → 只改本次负责的字段 → 原子替换。
    ///
    /// 这是本类唯一安全的修改入口。<see cref="Save"/> 写的是调用方在锁外读到的整份快照,
    /// 当 GUI(布防/项目增删)与 Worker 续跑引擎(周期结束解除布防)同时写配置时,
    /// 后写的一方会把对方的字段整体覆盖回旧值。项目约定对此有明确红线:
    /// 「在锁内重新读取最新配置后只修改本次负责字段,禁止锁外读旧快照后整体写回」。
    ///
    /// 返回写入后的配置对象,调用方可直接用于构造应答,无需再读一次。
    /// </summary>
    public ProductConfig Update(Action<ProductConfig> mutate)
    {
        ArgumentNullException.ThrowIfNull(mutate);
        EnsureDirectory();

        using var lockFs = AcquireLock(_configPath + ".write.lock");
        ProductConfig config = LoadUnlocked();
        mutate(config);
        WriteAtomic(config);
        return config;
    }

    private ProductConfig LoadUnlocked()
    {
        if (!File.Exists(_configPath))
        {
            return ProductConfig.CreateDefault();
        }

        try
        {
            using var fs = new FileStream(_configPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(fs, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            string json = reader.ReadToEnd();
            if (string.IsNullOrWhiteSpace(json))
            {
                return ProductConfig.CreateDefault();
            }

            return JsonSerializer.Deserialize<ProductConfig>(json, ProductConfig.JsonOptions) ?? ProductConfig.CreateDefault();
        }
        catch (Exception)
        {
            // 读失败/损坏:容错回默认值,不抛(配置缺失不应阻断探测/观察)。
            return ProductConfig.CreateDefault();
        }
    }

    /// <summary>
    /// 独占锁内整体写回(锁文件与目标分离:目标经临时文件 + File.Move overwrite 原子替换)。
    ///
    /// **只适用于调用方确实拥有整份配置的场景**(如首次写入、测试构造)。
    /// 与并发写方共享配置时请改用 <see cref="Update"/>——它在锁内重读,不会覆盖对方字段。
    /// </summary>
    public void Save(ProductConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);

        EnsureDirectory();
        using (var lockFs = AcquireLock(_configPath + ".write.lock"))
        {
            WriteAtomic(config);
        }
    }

    private void EnsureDirectory()
    {
        string? dir = Path.GetDirectoryName(_configPath);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }
    }

    /// <summary>写临时文件后原子替换。调用方必须已持有 .write.lock。</summary>
    private void WriteAtomic(ProductConfig config)
    {
        string json = JsonSerializer.Serialize(config, ProductConfig.JsonOptions);
        string tmp = _configPath + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            File.WriteAllText(tmp, json, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            File.Move(tmp, _configPath, overwrite: true);
        }
        finally
        {
            try
            {
                if (File.Exists(tmp))
                {
                    File.Delete(tmp);
                }
            }
            catch (Exception)
            {
                // 临时文件清理失败不掩盖写结果;残留 tmp 可安全忽略。
            }
        }
    }

    /// <summary>独占获取写锁;被并发占用时重试(间隔 25ms,总预算约 500ms,
    /// 对齐现役 PowerShell Get-CcuConfig 的锁尝试语义),仍失败则抛 IOException 由调用方 fail-closed。
    /// S10-O/P2:预算原为 3×20ms≈60ms——GUI 与续跑引擎同时写配置的真实并发场景下
    /// 必然抛错(实测并发用例暴露),把正常竞争变成写失败;加大预算只改竞争容忍度,
    /// 不改 fail-closed 语义:预算耗尽仍抛,绝不锁外读旧快照整体写回。</summary>
    private static FileStream AcquireLock(string lockPath)
    {
        IOException? last = null;
        for (int attempt = 0; attempt < 20; attempt++)
        {
            try
            {
                return new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException ex)
            {
                last = ex;
                if (attempt < 19)
                {
                    Thread.Sleep(25);
                }
            }
        }

        throw new IOException($"无法获取产品配置写锁:{lockPath}(20 次尝试均被占用)", last);
    }
}
