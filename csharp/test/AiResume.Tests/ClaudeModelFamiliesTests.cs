using AiResume.Core;
using Xunit;

namespace AiResume.Tests;

public sealed class ClaudeModelFamiliesTests
{
    [Theory]
    [InlineData("fable", "fable")]
    [InlineData("OPUS", "opus")]
    [InlineData("Sonnet", "sonnet")]
    [InlineData("haiku", "haiku")]
    [InlineData("claude-fable-5", "fable")]
    [InlineData("claude-opus-4-1-20250805", "opus")]
    [InlineData("claude-3-5-sonnet-20241022", "sonnet")]
    [InlineData("claude-3-haiku-20240307", "haiku")]
    public void 配置模型可规范化为唯一模型族(string configured, string expected)
    {
        Assert.True(ClaudeModelFamilies.TryNormalizeConfiguredModel(configured, out string actual));
        Assert.Equal(expected, actual);
    }

    [Theory]
    [InlineData("claude-preview-fable-5")]
    [InlineData("claude-opus-sonnet-5")]
    [InlineData("claude-3-5-sonnet")]
    [InlineData("claude-3-5-preview-20241022")]
    public void 非官方形状或歧义配置模型被拒绝(string configured)
    {
        Assert.False(ClaudeModelFamilies.TryNormalizeConfiguredModel(configured, out _));
    }
}
