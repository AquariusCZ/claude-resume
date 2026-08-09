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
