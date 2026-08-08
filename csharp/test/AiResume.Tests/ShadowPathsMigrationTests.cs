using AiResume.Worker;
using Xunit;

namespace AiResume.Tests;

/// <summary>
/// 状态目录从 <c>ClaudeResumeShadow</c> 搬到 <c>AI Resume\state</c> 的迁移。
///
/// 为什么要搬:那个名字是影子运行期为了和现役 v1 的 ClaudeResume 目录并存才取的,
/// v1 退役后它既不影子也不属于 ClaudeResume,**只会让人当成旧系统残留而误删**
/// ——里面装着 DPAPI 加密的飞书凭据。
///
/// 全部用临时目录驱动,不碰用户真实的 %LOCALAPPDATA%。
/// </summary>
public sealed class ShadowPathsMigrationTests : IDisposable
{
    private readonly List<string> _dirs = new();

    public void Dispose()
    {
        foreach (string d in _dirs)
        {
            try
            {
                Directory.Delete(d, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }

    private string NewDir(string tag)
    {
        string p = Path.Combine(Path.GetTempPath(), $"airesume-shadowmig-{tag}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(p);
        _dirs.Add(p);
        return p;
    }

    [Fact]
    public void 旧目录内容整体搬到新位置()
    {
        string from = NewDir("from"), to = NewDir("to");
        File.WriteAllText(Path.Combine(from, "config.json"), "{}");
        File.WriteAllText(Path.Combine(from, "runs.db"), "db");
        Directory.CreateDirectory(Path.Combine(from, "secrets"));
        File.WriteAllText(Path.Combine(from, "secrets", "feishu-platform.bin"), "cipher");

        int moved = ShadowPaths.TryMigrateLegacy(from, to);

        Assert.Equal(3, moved);
        Assert.True(File.Exists(Path.Combine(to, "config.json")));
        Assert.True(File.Exists(Path.Combine(to, "runs.db")));
        // 凭据必须一起搬:DPAPI 按当前用户加密,换路径仍解得开。
        Assert.True(File.Exists(Path.Combine(to, "secrets", "feishu-platform.bin")));
        Assert.False(Directory.Exists(from));   // 搬空了就把空壳删掉
    }

    [Fact]
    public void 新位置已有同名项时不覆盖()
    {
        string from = NewDir("from"), to = NewDir("to");
        File.WriteAllText(Path.Combine(from, "config.json"), "旧");
        File.WriteAllText(Path.Combine(to, "config.json"), "新");

        int moved = ShadowPaths.TryMigrateLegacy(from, to);

        // 现役优先。反过来会用一份历史配置盖掉用户现在正在用的那份。
        Assert.Equal(0, moved);
        Assert.Equal("新", File.ReadAllText(Path.Combine(to, "config.json")));
        Assert.Equal("旧", File.ReadAllText(Path.Combine(from, "config.json")));
        Assert.True(Directory.Exists(from));    // 还有内容,不删
    }

    [Fact]
    public void 只搬没冲突的那部分()
    {
        string from = NewDir("from"), to = NewDir("to");
        File.WriteAllText(Path.Combine(from, "config.json"), "旧");
        File.WriteAllText(Path.Combine(from, "runs.db"), "旧库");
        File.WriteAllText(Path.Combine(to, "config.json"), "新");

        int moved = ShadowPaths.TryMigrateLegacy(from, to);

        // 逐项处理,不是"有冲突就整体放弃"。
        Assert.Equal(1, moved);
        Assert.Equal("旧库", File.ReadAllText(Path.Combine(to, "runs.db")));
        Assert.Equal("新", File.ReadAllText(Path.Combine(to, "config.json")));
    }

    [Fact]
    public void 旧目录不存在时无事发生()
    {
        string to = NewDir("to");
        string ghost = Path.Combine(Path.GetTempPath(), "airesume-nope-" + Guid.NewGuid().ToString("N"));

        Assert.Equal(0, ShadowPaths.TryMigrateLegacy(ghost, to));
    }

    [Fact]
    public void 新旧同一个目录时不自搬()
    {
        string d = NewDir("same");
        File.WriteAllText(Path.Combine(d, "x.json"), "1");

        Assert.Equal(0, ShadowPaths.TryMigrateLegacy(d, d));
        Assert.True(File.Exists(Path.Combine(d, "x.json")));
    }

    /// <summary>
    /// **回归:2026-08-08 真实事故。**
    ///
    /// EnsureRoot 曾无条件迁移,而 legacyRoot 取的是**真实**的
    /// %LOCALAPPDATA%\ClaudeResumeShadow。PowerLossRecoveryTests 会用
    /// AIRESUME_SHADOW_DIR 把 Worker 子进程隔离到临时目录 —— 那个子进程于是
    /// 把用户的生产状态(含 DPAPI 加密的飞书凭据)搬进了测试临时目录,
    /// 测试收尾时一并删掉。**凭据真的丢了,得回飞书控制台重取 app secret。**
    ///
    /// 显式指定状态根 = 调用方自己决定状态放哪,没有任何东西该被迁移过去。
    /// </summary>
    [Fact]
    public void 指定了AIRESUME_SHADOW_DIR就绝不迁移()
    {
        string legacy = NewDir("legacy-prod");
        string overridden = NewDir("overridden");
        File.WriteAllText(Path.Combine(legacy, "config.json"), "生产配置");
        Directory.CreateDirectory(Path.Combine(legacy, "secrets"));
        File.WriteAllText(Path.Combine(legacy, "secrets", "feishu-platform.bin"), "凭据");

        string? saved = Environment.GetEnvironmentVariable(ShadowPaths.EnvOverride);
        try
        {
            Environment.SetEnvironmentVariable(ShadowPaths.EnvOverride, overridden);
            Assert.True(ShadowPaths.IsOverridden);

            ShadowPaths.EnsureRoot();

            // 生产状态必须原封不动地留在原处。
            Assert.True(File.Exists(Path.Combine(legacy, "config.json")));
            Assert.True(File.Exists(Path.Combine(legacy, "secrets", "feishu-platform.bin")));
            Assert.False(File.Exists(Path.Combine(overridden, "config.json")));
        }
        finally
        {
            Environment.SetEnvironmentVariable(ShadowPaths.EnvOverride, saved);
        }
    }

    [Fact]
    public void 默认位置在安装目录的state子目录下()
    {
        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

        Assert.Equal(Path.Combine(local, "AI Resume", "state"), ShadowPaths.DefaultRoot);
        Assert.Equal(Path.Combine(local, "ClaudeResumeShadow"), ShadowPaths.LegacyRoot);
        // 卸载靠这个名字跳过状态目录,拼错就等于卸载会删掉凭据。
        Assert.Equal("state", ShadowPaths.StateFolder);
    }
}
