using AiResume.Worker.Migration;
using Xunit;

namespace AiResume.Tests;

public sealed class ShortcutCommandTests
{
    [Fact]
    public void Run_RejectsMissingWorkerBeforeCreatingEntrypoints()
    {
        string root = TestTemp.NewDir("shortcut-missing-worker");
        string gui = Path.Combine(root, "AiResume.Gui.exe");
        string worker = Path.Combine(root, "missing-worker.exe");
        File.WriteAllText(gui, "synthetic");

        int result = ShortcutCommand.Run(
            ["shortcuts", "--gui", gui, "--worker", worker, "--icon", Path.Combine(root, "icon.ico")]);

        Assert.Equal(1, result);
    }

    [Fact]
    public void CommitStagedShortcuts_RestoresEveryDestinationWhenMiddleCommitFails()
    {
        string root = TestTemp.NewDir("shortcut-transaction");
        try
        {
            var staged = new List<(string Staged, string Destination)>();
            for (int i = 0; i < 3; i++)
            {
                string source = Path.Combine(root, $"new-{i}.lnk");
                string destination = Path.Combine(root, $"live-{i}.lnk");
                File.WriteAllText(source, $"new-{i}");
                File.WriteAllText(destination, $"old-{i}");
                staged.Add((source, destination));
            }

            int moves = 0;
            Assert.Throws<IOException>(() => ShortcutCommand.CommitStagedShortcuts(
                staged,
                (source, destination, overwrite) =>
                {
                    moves++;
                    if (moves == 2)
                    {
                        throw new IOException("synthetic commit failure");
                    }
                    File.Move(source, destination, overwrite);
                }));

            Assert.Equal("old-0", File.ReadAllText(staged[0].Destination));
            Assert.Equal("old-1", File.ReadAllText(staged[1].Destination));
            Assert.Equal("old-2", File.ReadAllText(staged[2].Destination));
            Assert.Empty(Directory.GetFiles(root, "*.airesume-backup-*"));
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
    public void CommitStagedShortcuts_DeletesNewDestinationWhenRollbackHasNoPreviousFile()
    {
        string root = TestTemp.NewDir("shortcut-clean-install");
        try
        {
            string firstSource = Path.Combine(root, "new-0.lnk");
            string secondSource = Path.Combine(root, "new-1.lnk");
            string firstDestination = Path.Combine(root, "live-0.lnk");
            string secondDestination = Path.Combine(root, "live-1.lnk");
            File.WriteAllText(firstSource, "new-0");
            File.WriteAllText(secondSource, "new-1");
            int moves = 0;

            Assert.Throws<IOException>(() => ShortcutCommand.CommitStagedShortcuts(
                [(firstSource, firstDestination), (secondSource, secondDestination)],
                (source, destination, overwrite) =>
                {
                    if (++moves == 2)
                    {
                        throw new IOException("synthetic commit failure");
                    }
                    File.Move(source, destination, overwrite);
                }));

            Assert.False(File.Exists(firstDestination));
            Assert.False(File.Exists(secondDestination));
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
    public void CommitStagedShortcuts_PreservesRecoveryMaterialWhenRollbackIsIncomplete()
    {
        string root = TestTemp.NewDir("shortcut-incomplete-rollback");
        string source = Path.Combine(root, "new.lnk");
        string destination = Path.Combine(root, "live.lnk");
        File.WriteAllText(source, "new");
        File.WriteAllText(destination, "old");

        int moves = 0;
        InvalidOperationException error = Assert.Throws<InvalidOperationException>(() =>
            ShortcutCommand.CommitStagedShortcuts(
                [(source, destination)],
                (from, to, overwrite) =>
                {
                    if (++moves == 1)
                    {
                        File.Move(from, to, overwrite);
                        throw new IOException("synthetic commit failure after replacement");
                    }
                },
                (from, to, overwrite) =>
                {
                    if (overwrite && from.Contains(".airesume-backup-", StringComparison.Ordinal))
                    {
                        throw new IOException("synthetic rollback failure");
                    }
                    File.Copy(from, to, overwrite);
                }));

        string backup = Assert.Single(Directory.GetFiles(root, "*.airesume-backup-*"));
        Assert.Equal("old", File.ReadAllText(backup));
        Assert.Contains(Path.GetFullPath(backup), error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void RemoveShortcutsTransaction_RestoresAllEntrypointsWhenDeleteFails()
    {
        string root = TestTemp.NewDir("shortcut-remove-transaction");
        string[] destinations =
        [
            Path.Combine(root, "start.lnk"),
            Path.Combine(root, "startup.lnk"),
            Path.Combine(root, "desktop.lnk"),
        ];
        for (int i = 0; i < destinations.Length; i++)
        {
            File.WriteAllText(destinations[i], $"old-{i}");
        }

        int deletes = 0;
        Assert.Throws<IOException>(() => ShortcutCommand.RemoveShortcutsTransaction(
            destinations,
            deleteFile: path =>
            {
                if (++deletes == 2)
                {
                    throw new IOException("synthetic removal failure");
                }
                File.Delete(path);
            }));

        for (int i = 0; i < destinations.Length; i++)
        {
            Assert.Equal($"old-{i}", File.ReadAllText(destinations[i]));
        }
        Assert.Empty(Directory.GetFiles(root, "*.airesume-backup-*"));
    }

    [Fact]
    public void RemoveShortcutsTransaction_PreservesBackupWhenRollbackFails()
    {
        string root = TestTemp.NewDir("shortcut-remove-incomplete");
        string destination = Path.Combine(root, "start.lnk");
        File.WriteAllText(destination, "old");
        int copies = 0;

        InvalidOperationException error = Assert.Throws<InvalidOperationException>(() =>
            ShortcutCommand.RemoveShortcutsTransaction(
                [destination],
                (from, to, overwrite) =>
                {
                    if (++copies > 1)
                    {
                        throw new IOException("synthetic restore failure");
                    }
                    File.Copy(from, to, overwrite);
                },
                _ => throw new IOException("synthetic removal failure")));

        string backup = Assert.Single(Directory.GetFiles(root, "*.airesume-backup-*"));
        Assert.Equal("old", File.ReadAllText(backup));
        Assert.Contains(Path.GetFullPath(backup), error.Message, StringComparison.Ordinal);
    }
}
