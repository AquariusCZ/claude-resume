using System.Text.Json;
using AiResume.Worker.Quota;
using Xunit;

namespace AiResume.Tests;

/// <summary>
/// 从 <c>limits[].scope</c> 取模型显示名。
///
/// 不取的话面板只能写"按模型额度已用尽"这种含糊话 —— 用户看完还是不知道
/// **到底是哪个模型**跑不动了,而名字明明就在响应里
/// (实测 <c>{"model":{"id":null,"display_name":"Fable"},"surface":null}</c>)。
/// </summary>
public sealed class ScopeModelTests
{
    private static JsonElement? Parse(string json)
        => JsonDocument.Parse(json).RootElement;

    [Fact]
    public void 取出真实响应里的模型名()
    {
        // 照抄 2026-08-08 实测形状。
        JsonElement? scope = Parse("""
            {"model":{"id":null,"display_name":"Fable"},"surface":null}
            """);

        Assert.Equal("Fable", ClaudeOAuthUsageProbe.ReadScopeModel(scope));
    }

    [Theory]
    [InlineData("null")]                                   // scope 整个为 null
    [InlineData("{}")]                                     // 没有 model
    [InlineData("""{"model":null}""")]
    [InlineData("""{"model":{}}""")]                       // 没有 display_name
    [InlineData("""{"model":{"display_name":null}}""")]
    [InlineData("""{"model":{"display_name":""}}""")]
    [InlineData("""{"model":{"display_name":"   "}}""")]
    [InlineData("""{"model":{"display_name":123}}""")]     // 类型不对
    [InlineData("""{"model":"Fable"}""")]                  // 形状变了
    public void 取不到就返回空串而不是猜(string json)
    {
        Assert.Equal(string.Empty, ClaudeOAuthUsageProbe.ReadScopeModel(Parse(json)));
    }

    [Fact]
    public void scope为null时返回空串()
    {
        Assert.Equal(string.Empty, ClaudeOAuthUsageProbe.ReadScopeModel(null));
    }

    [Fact]
    public void 模型名里的冒号被剥掉()
    {
        // 冒号是我们拼窗口名(weekly_scoped:Fable)的分隔符,
        // 出现在模型名里会把前端的 split(':') 弄乱。
        JsonElement? scope = Parse("""{"model":{"display_name":"Opus:1m"}}""");

        Assert.Equal("Opus1m", ClaudeOAuthUsageProbe.ReadScopeModel(scope));
    }

    [Fact]
    public void 首尾空白被去掉()
    {
        JsonElement? scope = Parse("""{"model":{"display_name":"  Sonnet  "}}""");

        Assert.Equal("Sonnet", ClaudeOAuthUsageProbe.ReadScopeModel(scope));
    }
}
