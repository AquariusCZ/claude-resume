using AiResume.Worker.Migration;
using System.Diagnostics;
using Xunit;

namespace AiResume.Tests;

public sealed class InstallUninstallTests
{
    [Fact]
    public void RunUninstallPreamble_DoesNotStopRuntimeWhenShortcutRemovalFails()
    {
        bool stopped = false;

        int result = InstallCommand.RunUninstallPreamble(
            "synthetic-target",
            () => 7,
            _ => stopped = true);

        Assert.Equal(7, result);
        Assert.False(stopped);
    }

    [Fact]
    public void RunUninstallPreamble_StopsRuntimeOnlyAfterEntrypointsAreRemoved()
    {
        var order = new List<string>();

        int result = InstallCommand.RunUninstallPreamble(
            "synthetic-target",
            () =>
            {
                order.Add("shortcuts");
                return 0;
            },
            _ => order.Add("runtime"));

        Assert.Equal(0, result);
        Assert.Equal(["shortcuts", "runtime"], order);
    }

    [Fact]
    public void WaitForUninstallHelperResult_PreservesRetiredPayloadWhenHelperCrashesWithoutSignal()
    {
        string target = TestTemp.NewDir("uninstall-helper-crash-target");
        string helperRoot = TestTemp.NewDir("uninstall-helper-crash-recovery");
        string retiredRoot = Path.Combine(helperRoot, "retired");
        Directory.CreateDirectory(retiredRoot);
        string source = Path.Combine(target, "payload.dll");
        string retired = Path.Combine(retiredRoot, "payload.dll");
        string signal = Path.Combine(helperRoot, "uninstall-result.txt");
        string crashScript = Path.Combine(helperRoot, "crash-after-move.ps1");
        File.WriteAllText(source, "recover-me");
        File.WriteAllText(
            crashScript,
            "param([string]$SourcePath, [string]$DestinationPath)\n" +
            "Move-Item -LiteralPath $SourcePath -Destination $DestinationPath\n" +
            "exit 23\n");

        var psi = new ProcessStartInfo("powershell.exe")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (string argument in new[]
        {
            "-NoProfile",
            "-NonInteractive",
            "-File",
            crashScript,
            source,
            retired,
        })
        {
            psi.ArgumentList.Add(argument);
        }

        using Process helper = Process.Start(psi)
            ?? throw new InvalidOperationException("无法启动故障注入子进程");
        InvalidOperationException error = Assert.Throws<InvalidOperationException>(() =>
            InstallCommand.WaitForUninstallHelperResult(
                helper, signal, helperRoot, TimeSpan.FromSeconds(10)));

        Assert.Contains("提前退出(23)", error.Message, StringComparison.Ordinal);
        Assert.Contains(helperRoot, error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(File.Exists(source));
        Assert.Equal("recover-me", File.ReadAllText(retired));
        Assert.False(File.Exists(signal));
        Assert.True(Directory.Exists(helperRoot));
    }

    [Fact]
    public void WaitForUninstallHelperResult_PreservesRecoveryDirectoryForInvalidSignal()
    {
        string helperRoot = TestTemp.NewDir("uninstall-helper-invalid-signal");
        string signal = Path.Combine(helperRoot, "uninstall-result.txt");
        string retired = Path.Combine(helperRoot, "retired", "payload.dll");
        string source = Path.Combine(helperRoot, "late-payload.dll");
        string delayedScript = Path.Combine(helperRoot, "move-after-delay.ps1");
        Directory.CreateDirectory(Path.GetDirectoryName(retired)!);
        File.WriteAllText(source, "must-not-move");
        File.WriteAllText(
            delayedScript,
            "param([string]$SourcePath, [string]$DestinationPath)\n" +
            "Start-Sleep -Seconds 2\n" +
            "Move-Item -LiteralPath $SourcePath -Destination $DestinationPath\n");
        File.WriteAllText(signal, "not-an-exit-code\n");

        var psi = new ProcessStartInfo("powershell.exe")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (string argument in new[]
        {
            "-NoProfile",
            "-NonInteractive",
            "-File",
            delayedScript,
            source,
            retired,
        })
        {
            psi.ArgumentList.Add(argument);
        }
        using Process helper = Process.Start(psi)
            ?? throw new InvalidOperationException("无法启动结果校验子进程");

        InvalidDataException error = Assert.Throws<InvalidDataException>(() =>
            InstallCommand.WaitForUninstallHelperResult(
                helper, signal, helperRoot, TimeSpan.FromSeconds(10)));

        Assert.Contains("无效结果", error.Message, StringComparison.Ordinal);
        Assert.Contains(helperRoot, error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.True(helper.HasExited);
        Thread.Sleep(2_200);
        Assert.Equal("must-not-move", File.ReadAllText(source));
        Assert.False(File.Exists(retired));
        Assert.True(Directory.Exists(helperRoot));
    }

    [Fact]
    public void WaitForUninstallHelperResult_StopsTimedOutHelperBeforeReturning()
    {
        string helperRoot = TestTemp.NewDir("uninstall-helper-timeout");
        string signal = Path.Combine(helperRoot, "uninstall-result.txt");
        string source = Path.Combine(helperRoot, "late-payload.dll");
        string retired = Path.Combine(helperRoot, "retired", "payload.dll");
        string delayedScript = Path.Combine(helperRoot, "move-after-delay.ps1");
        Directory.CreateDirectory(Path.GetDirectoryName(retired)!);
        File.WriteAllText(source, "must-not-move");
        File.WriteAllText(
            delayedScript,
            "param([string]$SourcePath, [string]$DestinationPath)\n" +
            "Start-Sleep -Seconds 2\n" +
            "Move-Item -LiteralPath $SourcePath -Destination $DestinationPath\n");

        var psi = new ProcessStartInfo("powershell.exe")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (string argument in new[]
        {
            "-NoProfile",
            "-NonInteractive",
            "-File",
            delayedScript,
            source,
            retired,
        })
        {
            psi.ArgumentList.Add(argument);
        }
        using Process helper = Process.Start(psi)
            ?? throw new InvalidOperationException("无法启动超时故障注入子进程");

        int result = InstallCommand.WaitForUninstallHelperResult(
            helper, signal, helperRoot, TimeSpan.FromMilliseconds(100));

        Assert.Equal(4, result);
        Assert.True(helper.HasExited);
        Thread.Sleep(2_200);
        Assert.Equal("must-not-move", File.ReadAllText(source));
        Assert.False(File.Exists(retired));
        Assert.True(Directory.Exists(helperRoot));
    }

    [Fact]
    public void WaitForUninstallHelperResult_StopsHelperWhenSignalCannotBeRead()
    {
        string helperRoot = TestTemp.NewDir("uninstall-helper-locked-signal");
        string signal = Path.Combine(helperRoot, "uninstall-result.txt");
        string source = Path.Combine(helperRoot, "late-payload.dll");
        string retired = Path.Combine(helperRoot, "retired", "payload.dll");
        string delayedScript = Path.Combine(helperRoot, "move-after-delay.ps1");
        Directory.CreateDirectory(Path.GetDirectoryName(retired)!);
        File.WriteAllText(source, "must-not-move");
        File.WriteAllText(
            delayedScript,
            "param([string]$SourcePath, [string]$DestinationPath)\n" +
            "Start-Sleep -Seconds 2\n" +
            "Move-Item -LiteralPath $SourcePath -Destination $DestinationPath\n");
        File.WriteAllText(signal, "0\ncomplete\n");

        var psi = new ProcessStartInfo("powershell.exe")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (string argument in new[]
        {
            "-NoProfile",
            "-NonInteractive",
            "-File",
            delayedScript,
            source,
            retired,
        })
        {
            psi.ArgumentList.Add(argument);
        }
        using Process helper = Process.Start(psi)
            ?? throw new InvalidOperationException("无法启动锁文件故障注入子进程");
        using FileStream lockStream = new(signal, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

        InvalidDataException error = Assert.Throws<InvalidDataException>(() =>
            InstallCommand.WaitForUninstallHelperResult(
                helper, signal, helperRoot, TimeSpan.FromSeconds(10)));

        Assert.Contains("结果无法读取", error.Message, StringComparison.Ordinal);
        Assert.Contains(helperRoot, error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.True(helper.HasExited);
        Thread.Sleep(2_200);
        Assert.Equal("must-not-move", File.ReadAllText(source));
        Assert.False(File.Exists(retired));
        Assert.True(Directory.Exists(helperRoot));
    }
}
