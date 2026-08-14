using AiResume.Worker.Migration;
using Xunit;

namespace AiResume.Tests;

public sealed class InstallTargetSafetyTests
{
    [Fact]
    public void ValidateInstallTarget_RejectsUserProfileRoot()
    {
        Assert.Throws<InvalidOperationException>(() =>
            InstallCommand.ValidateInstallTarget(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)));
    }

    [Fact]
    public void ValidateInstallTarget_RejectsNonEmptyUnownedDirectory()
    {
        string target = TestTemp.NewDir("install-unowned");
        File.WriteAllText(Path.Combine(target, "personal.txt"), "keep");

        Assert.Throws<InvalidOperationException>(() => InstallCommand.ValidateInstallTarget(target));
    }

    [Fact]
    public void ValidateInstallTarget_AllowsEmptyAndStateOnlyDirectories()
    {
        string empty = TestTemp.NewDir("install-empty");
        Assert.Equal(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(empty)),
            InstallCommand.ValidateInstallTarget(empty));

        string stateOnly = TestTemp.NewDir("install-state-only");
        Directory.CreateDirectory(Path.Combine(stateOnly, "state"));
        Assert.Equal(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(stateOnly)),
            InstallCommand.ValidateInstallTarget(stateOnly));
    }

    [Fact]
    public void ValidateInstallTarget_AllowsExactPreservedRootMarkerButNotLookalike()
    {
        string target = TestTemp.NewDir("install-preserved-root");
        File.WriteAllText(Path.Combine(target, "personal.txt"), "keep");
        File.WriteAllText(Path.Combine(target, ".ai-resume-preserved-root"), "lookalike");
        Assert.Throws<InvalidOperationException>(() => InstallCommand.ValidateInstallTarget(target));

        InstallCommand.WritePreservedRootMarker(target);
        Assert.Equal(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(target)),
            InstallCommand.ValidateInstallTarget(target));
        Assert.Throws<InvalidOperationException>(() => InstallCommand.ValidateUninstallTarget(target));
    }

    [Fact]
    public void ValidateInstallTarget_RejectsExtendedPathAliasOfProtectedRoot()
    {
        string localAppData = Environment.GetFolderPath(
            Environment.SpecialFolder.LocalApplicationData);
        string extendedAlias = @"\\?\" + localAppData;

        Assert.Throws<InvalidOperationException>(() =>
            InstallCommand.ValidateInstallTarget(extendedAlias));
    }

    [Fact]
    public void ValidateUninstallTarget_RequiresExactOwnershipMarkerAndManifest()
    {
        string target = TestTemp.NewDir("uninstall-unowned");
        File.WriteAllText(Path.Combine(target, ".ai-resume-install-root"), "lookalike");

        Assert.Throws<InvalidOperationException>(() => InstallCommand.ValidateUninstallTarget(target));

        File.WriteAllText(Path.Combine(target, "AiResume.Gui.exe"), "gui");
        InstallCommand.WritePayloadManifest(target);
        InstallCommand.WriteOwnershipMarker(target);
        Assert.Equal(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(target)),
            InstallCommand.ValidateUninstallTarget(target));
    }

    [Fact]
    public void ValidateInstallTarget_AllowsPreMarkerRecognizedRuntimeUpgrade()
    {
        string target = TestTemp.NewDir("install-legacy-runtime");
        File.WriteAllText(Path.Combine(target, "AiResume.Gui.exe"), "gui");
        File.WriteAllText(Path.Combine(target, "AiResume.Worker.exe"), "worker");
        File.WriteAllText(Path.Combine(target, "AiResume.Hook.exe"), "hook");

        Assert.Equal(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(target)),
            InstallCommand.ValidateInstallTarget(target, [
                "AiResume.Gui.exe",
                "AiResume.Worker.exe",
                "AiResume.Hook.exe"
            ]));
    }

    [Fact]
    public void ValidateInstallTarget_RejectsPreMarkerRuntimeMixedWithUserData()
    {
        string target = TestTemp.NewDir("install-legacy-runtime-mixed");
        File.WriteAllText(Path.Combine(target, "AiResume.Gui.exe"), "gui");
        File.WriteAllText(Path.Combine(target, "AiResume.Worker.exe"), "worker");
        File.WriteAllText(Path.Combine(target, "AiResume.Hook.exe"), "hook");
        File.WriteAllText(Path.Combine(target, "personal.txt"), "keep");

        Assert.Throws<InvalidOperationException>(() =>
            InstallCommand.ValidateInstallTarget(target, [
                "AiResume.Gui.exe",
                "AiResume.Worker.exe",
                "AiResume.Hook.exe"
            ]));
    }

    [Fact]
    public void PayloadManifest_RemovesOnlyObsoleteOwnedFiles()
    {
        string target = TestTemp.NewDir("install-manifest-target");
        File.WriteAllText(Path.Combine(target, "current.dll"), "old-current");
        File.WriteAllText(Path.Combine(target, "obsolete.dll"), "old-obsolete");
        InstallCommand.WritePayloadManifest(target);
        File.WriteAllText(Path.Combine(target, "personal.txt"), "keep");

        string stage = TestTemp.NewDir("install-manifest-stage");
        File.WriteAllText(Path.Combine(stage, "current.dll"), "new-current");
        InstallCommand.WritePayloadManifest(stage);

        IReadOnlyList<string> obsolete = InstallCommand.FindObsoletePayload(target, stage);
        Assert.Equal(new[] { "obsolete.dll" }, obsolete, StringComparer.OrdinalIgnoreCase);

        InstallCommand.DeleteObsoletePayload(target, obsolete);
        Assert.True(File.Exists(Path.Combine(target, "current.dll")));
        Assert.False(File.Exists(Path.Combine(target, "obsolete.dll")));
        Assert.True(File.Exists(Path.Combine(target, "personal.txt")));
    }

    [Fact]
    public void PayloadManifest_OnlyExcludesRootOwnershipMarker()
    {
        string target = TestTemp.NewDir("install-nested-marker");
        Directory.CreateDirectory(Path.Combine(target, "archive"));
        File.WriteAllText(Path.Combine(target, ".ai-resume-install-root"), "root marker");
        File.WriteAllText(Path.Combine(target, "archive", ".ai-resume-install-root"), "payload");

        InstallCommand.WritePayloadManifest(target);

        string[] manifest = File.ReadAllLines(Path.Combine(target, ".ai-resume-install-manifest"));
        Assert.Contains(
            Path.Combine("archive", ".ai-resume-install-root"),
            manifest,
            StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain(
            ".ai-resume-install-root",
            manifest,
            StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void PayloadHashes_AcceptExactCopyAndRejectChangedOrMissingFiles()
    {
        string stage = TestTemp.NewDir("install-hash-stage");
        string target = TestTemp.NewDir("install-hash-target");
        Directory.CreateDirectory(Path.Combine(stage, "nested"));
        Directory.CreateDirectory(Path.Combine(target, "nested"));
        File.WriteAllText(Path.Combine(stage, "root.dll"), "root-v1");
        File.WriteAllText(Path.Combine(stage, "nested", "payload.dll"), "nested-v1");
        File.Copy(Path.Combine(stage, "root.dll"), Path.Combine(target, "root.dll"));
        File.Copy(
            Path.Combine(stage, "nested", "payload.dll"),
            Path.Combine(target, "nested", "payload.dll"));

        IReadOnlyDictionary<string, string> hashes = InstallCommand.CapturePayloadHashes(stage);

        InstallCommand.VerifyPayloadHashes(target, hashes);

        File.WriteAllText(Path.Combine(target, "nested", "payload.dll"), "nested-v2");
        InvalidDataException changed = Assert.Throws<InvalidDataException>(() =>
            InstallCommand.VerifyPayloadHashes(target, hashes));
        Assert.Contains("nested", changed.Message, StringComparison.OrdinalIgnoreCase);

        File.Copy(
            Path.Combine(stage, "nested", "payload.dll"),
            Path.Combine(target, "nested", "payload.dll"),
            overwrite: true);
        File.Delete(Path.Combine(target, "root.dll"));
        InvalidDataException missing = Assert.Throws<InvalidDataException>(() =>
            InstallCommand.VerifyPayloadHashes(target, hashes));
        Assert.Contains("root.dll", missing.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task InstallOperationLease_SerializesEquivalentTargetPaths()
    {
        string target = TestTemp.NewDir("install-operation-lock");
        using var acquired = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        Task holder = Task.Run(() =>
        {
            using IDisposable lease = InstallCommand.AcquireOperationLease(
                target, TimeSpan.FromSeconds(5));
            acquired.Set();
            release.Wait(TimeSpan.FromSeconds(5));
        });

        Assert.True(acquired.Wait(TimeSpan.FromSeconds(5)));
        try
        {
            await Assert.ThrowsAsync<TimeoutException>(() => Task.Run(() =>
            {
                using IDisposable lease = InstallCommand.AcquireOperationLease(
                    target + Path.DirectorySeparatorChar,
                    TimeSpan.FromMilliseconds(100));
            }));
        }
        finally
        {
            release.Set();
            await holder;
        }

        using IDisposable reacquired = InstallCommand.AcquireOperationLease(
            target, TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task UninstallHelperHandoff_KeepsInstallersBlockedUntilHelperFinishes()
    {
        string target = TestTemp.NewDir("install-operation-handoff");
        var handoffReady = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var helperAcquired = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var gateReleased = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var releaseOperation = new ManualResetEventSlim();
        using var releaseGate = new ManualResetEventSlim();
        using var releaseHelper = new ManualResetEventSlim();
        Task handoffOwner = Task.Run(() =>
        {
            using InstallCommand.OperationHandoffLease handoff =
                InstallCommand.AcquireOperationHandoffLease(target, TimeSpan.FromSeconds(5));
            handoffReady.SetResult(true);
            releaseOperation.Wait(TimeSpan.FromSeconds(5));
            handoff.ReleaseOperation();
            releaseGate.Wait(TimeSpan.FromSeconds(5));
            handoff.ReleaseGate();
            gateReleased.SetResult(true);
        });

        await handoffReady.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Task helper = Task.Run(() =>
        {
            using IDisposable lease = InstallCommand.AcquireTransferredOperationLease(
                target, TimeSpan.FromSeconds(5));
            helperAcquired.SetResult(true);
            releaseHelper.Wait(TimeSpan.FromSeconds(5));
        });

        releaseOperation.Set();
        await helperAcquired.Task.WaitAsync(TimeSpan.FromSeconds(5));
        try
        {
            await Assert.ThrowsAsync<TimeoutException>(() => Task.Run(() =>
            {
                using IDisposable lease = InstallCommand.AcquireOperationLease(
                    target, TimeSpan.FromMilliseconds(100));
            }));

            releaseGate.Set();
            await gateReleased.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await Assert.ThrowsAsync<TimeoutException>(() => Task.Run(() =>
            {
                using IDisposable lease = InstallCommand.AcquireOperationLease(
                    target, TimeSpan.FromMilliseconds(100));
            }));
        }
        finally
        {
            releaseHelper.Set();
            releaseOperation.Set();
            releaseGate.Set();
            await Task.WhenAll(helper, handoffOwner);
        }

        using IDisposable reacquired = InstallCommand.AcquireOperationLease(
            target, TimeSpan.FromSeconds(1));
    }

    [Fact]
    public void ValidateUninstallTarget_RejectsManifestPathTraversal()
    {
        string target = TestTemp.NewDir("uninstall-bad-manifest");
        InstallCommand.WriteOwnershipMarker(target);
        File.WriteAllText(Path.Combine(target, ".ai-resume-install-manifest"), "..\\personal.txt\n");

        Assert.Throws<InvalidOperationException>(() => InstallCommand.ValidateUninstallTarget(target));
    }

    [Fact]
    public void UninstallHelper_StagesOnlyManifestOwnedPayload()
    {
        string target = TestTemp.NewDir("uninstall-helper-source");
        string helper = TestTemp.NewDir("uninstall-helper-stage");
        File.WriteAllText(Path.Combine(target, "AiResume.Gui.exe"), "gui");
        File.WriteAllText(Path.Combine(target, "AiResume.Worker.exe"), "worker");
        File.WriteAllText(Path.Combine(target, "AiResume.Hook.exe"), "hook");
        File.WriteAllText(Path.Combine(target, "personal.txt"), "keep");
        InstallCommand.WritePayloadManifest(target);
        // personal.txt 代表由旧安装清单拥有的文件；真正未知的文件在写清单后创建。
        File.WriteAllText(Path.Combine(target, "unknown.txt"), "unknown");

        InstallCommand.StageUninstallHelper(target, helper);

        Assert.True(File.Exists(Path.Combine(helper, "AiResume.Worker.exe")));
        Assert.True(File.Exists(Path.Combine(helper, "personal.txt")));
        Assert.False(File.Exists(Path.Combine(helper, "unknown.txt")));
    }

    [Fact]
    public void FreshInstallRollback_RemovesPayloadCreatedEmptyDirectoriesAndAllowsRetry()
    {
        string stage = TestTemp.NewDir("rollback-stage");
        string backup = TestTemp.NewDir("rollback-backup");
        string target = TestTemp.NewDir("rollback-target");
        Directory.CreateDirectory(Path.Combine(stage, "wwwroot"));
        Directory.CreateDirectory(Path.Combine(target, "wwwroot"));
        File.WriteAllText(Path.Combine(stage, "wwwroot", "index.html"), "new");
        File.WriteAllText(Path.Combine(target, "wwwroot", "index.html"), "new");

        bool restored = InstallCommand.RollbackRuntime(
            stage, backup, target, restartWorker: false, Array.Empty<string>(), _ => { });

        Assert.True(restored);
        Assert.False(Directory.Exists(Path.Combine(target, "wwwroot")));
        Assert.Equal(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(target)),
            InstallCommand.ValidateInstallTarget(target));
    }

    [Fact]
    public void RollbackPathValidationFailure_ReturnsFalseAndLeavesRecoveryMaterials()
    {
        string stage = TestTemp.NewDir("rollback-invalid-stage");
        string backup = TestTemp.NewDir("rollback-invalid-backup");
        string target = TestTemp.NewDir("rollback-invalid-target");
        File.WriteAllText(Path.Combine(stage, "payload.dll"), "new");
        File.WriteAllText(Path.Combine(backup, "payload.dll"), "old");

        bool restored = InstallCommand.RollbackRuntime(
            stage,
            backup,
            target,
            restartWorker: false,
            Array.Empty<string>(),
            _ => { },
            (_, _) => throw new InvalidDataException("reparse"));

        Assert.False(restored);
        Assert.True(Directory.Exists(stage));
        Assert.True(Directory.Exists(backup));
        Assert.Equal("new", File.ReadAllText(Path.Combine(stage, "payload.dll")));
        Assert.Equal("old", File.ReadAllText(Path.Combine(backup, "payload.dll")));
    }
}
