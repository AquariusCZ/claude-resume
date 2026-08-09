using AiResume.Worker.Notifications;
using Xunit;

namespace AiResume.Tests;

public sealed class HookCommandTests
{
    [Fact]
    public void Format_QuotesExecutableWithSpacesAndAppendsSource()
    {
        string command = HookCommand.Format(
            @"C:\Users\x\AppData\Local\AI Resume\AiResume.Hook.exe", "qoder");

        Assert.Equal(
            "\"C:\\Users\\x\\AppData\\Local\\AI Resume\\AiResume.Hook.exe\" qoder",
            command);
    }

    [Theory]
    [InlineData("\"C:\\A B\\AiResume.Hook.exe\" claudecode", "C:\\A B\\AiResume.Hook.exe")]
    [InlineData("C:\\A B\\AiResume.Hook.exe claudecode", "C:\\A B\\AiResume.Hook.exe")]
    public void ExtractExecutable_HandlesQuotedAndLegacyCommands(string command, string expected)
    {
        Assert.Equal(expected, HookCommand.ExtractExecutable(command));
    }

    [Theory]
    [InlineData("\"C:\\A B\\AiResume.Hook.exe\" claudecode", "claudecode", true)]
    [InlineData("C:\\A B\\AiResume.Hook.exe", "claudecode", true)]
    [InlineData("\"C:\\tools\\notify.exe\" --label AiResume.Hook.exe", "claudecode", false)]
    [InlineData("\"C:\\A B\\AiResume.Hook.exe\" qoder", "claudecode", false)]
    public void IsManaged_RequiresExecutablePositionAndExpectedSource(
        string command, string source, bool expected)
    {
        Assert.Equal(expected, HookCommand.IsManaged(command, "AiResume.Hook.exe", source));
    }
}
