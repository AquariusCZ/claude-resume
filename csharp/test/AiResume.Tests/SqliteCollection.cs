using Xunit;

namespace AiResume.Tests;

/// <summary>
/// 所有会碰 SQLite 的测试类共用的串行集合。
///
/// **为什么必须串行**:这些类在 Dispose 里调用 <c>SqliteConnection.ClearAllPools()</c>
/// 来释放文件句柄以便删除临时目录,而该方法是**进程级全局操作**——
/// 它会关掉整个进程内所有池化连接,不区分是谁的。
/// xUnit 默认按类并行,于是 A 类清池的瞬间 B 类正在 Open,B 就会以
/// <c>SqliteException</c> 挂掉。表现为随机某个用例失败、单独重跑又通过。
///
/// 该问题是 2026-08-06 新增测试类后并行度上升才暴露的,此前一直潜伏。
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class SqliteCollection
{
    public const string Name = "sqlite-serial";
}
