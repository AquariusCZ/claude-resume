using System.Diagnostics;
using AiResume.Ipc;
using AiResume.Worker.Migration;
using Xunit;

namespace AiResume.Tests;

public sealed class InstallWorkerLauncherTests
{
    [Theory]
    [InlineData(42, 42, true)]
    [InlineData(41, 42, false)]
    public void MatchesWorkerIdentity_BindsPongToStartedProcess(
        int actualProcessId, int expectedProcessId, bool expected)
    {
        var ping = new WorkerPingInfo(PipeProtocol.Version, actualProcessId);

        Assert.Equal(expected, InstallCommand.MatchesWorkerIdentity(ping, expectedProcessId));
    }

    [Fact]
    public void MatchesWorkerIdentity_RejectsLegacyPongWithoutProcessIdWhenPidExpected()
    {
        var ping = new WorkerPingInfo(PipeProtocol.Version, null);

        Assert.False(InstallCommand.MatchesWorkerIdentity(ping, 42));
    }

    [Fact]
    public void StartInstalledWorker_UsesHiddenInstalledExecutable()
    {
        string root = TestTemp.NewDir("installed-worker");
        try
        {
            string worker = Path.Combine(root, "AiResume.Worker.exe");
            File.WriteAllBytes(worker, []);
            ProcessStartInfo? captured = null;

            bool ok = InstallCommand.StartInstalledWorker(worker, root, psi =>
            {
                captured = psi;
                return true;
            });

            Assert.True(ok);
            Assert.NotNull(captured);
            Assert.Equal(worker, captured.FileName);
            Assert.Equal(root, captured.WorkingDirectory);
            Assert.False(captured.UseShellExecute);
            Assert.True(captured.CreateNoWindow);
        }
        finally
        {
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }

    [Fact]
    public void WaitForWorkerReady_DoesNotAcceptProcessLivenessWithoutPipePong()
    {
        int pauses = 0;

        bool ready = InstallCommand.WaitForWorkerReady(
            hasExited: () => false,
            pipeReady: () => false,
            maxAttempts: 3,
            pause: () => pauses++);

        Assert.False(ready);
        Assert.Equal(3, pauses);
    }

    [Fact]
    public void WaitForWorkerReady_AcceptsDelayedPipePongWhileProcessLives()
    {
        int probes = 0;

        bool ready = InstallCommand.WaitForWorkerReady(
            hasExited: () => false,
            pipeReady: () => ++probes == 3,
            maxAttempts: 5);

        Assert.True(ready);
        Assert.Equal(3, probes);
    }

    [Fact]
    public void WaitForWorkerReady_FailsImmediatelyWhenProcessExits()
    {
        int pipeProbes = 0;

        bool ready = InstallCommand.WaitForWorkerReady(
            hasExited: () => true,
            pipeReady: () => { pipeProbes++; return true; },
            maxAttempts: 5);

        Assert.False(ready);
        Assert.Equal(0, pipeProbes);
    }

    [Fact]
    public void ActivateInstalledVersion_DoesNotTouchEntrypointsBeforeWorkerReady()
    {
        bool shortcutsTouched = false;
        bool hooksTouched = false;

        InstallCommand.ActivationResult result = InstallCommand.ActivateInstalledVersion(
            startWorker: () => false,
            installShortcuts: () => { shortcutsTouched = true; return 0; },
            reconcileHooks: () => { hooksTouched = true; return true; });

        Assert.False(result.WorkerReady);
        Assert.False(shortcutsTouched);
        Assert.False(hooksTouched);
    }

    [Fact]
    public void ActivateInstalledVersion_CommitsEntrypointsInReadinessOrder()
    {
        var calls = new List<string>();

        InstallCommand.ActivationResult result = InstallCommand.ActivateInstalledVersion(
            startWorker: () => { calls.Add("worker"); return true; },
            installShortcuts: () => { calls.Add("shortcuts"); return 0; },
            reconcileHooks: () => { calls.Add("hooks"); return true; });

        Assert.True(result.WorkerReady);
        Assert.True(result.HooksOk);
        Assert.Equal(["worker", "shortcuts", "hooks"], calls);
    }
}
