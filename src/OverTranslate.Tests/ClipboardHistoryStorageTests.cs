using OverTranslate.Services;
using OverTranslate.Services.ClipboardHistory;
using Xunit;

namespace OverTranslate.Tests;

public sealed class ClipboardHistoryStorageTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ScreenTranslate-storage-tests-" + Guid.NewGuid().ToString("N"));
    private string Source => Path.Combine(_root, "old", "clipboard-history");
    private string Target => Path.Combine(_root, "new", "clipboard-history");

    [Fact]
    public void SwitchingPreservesContentPinsImagesPlacementAndWritesToTheNewFolder()
    {
        var store = new ClipboardHistoryStore(Source);
        var record = store.Add(new() { Text = "directory fixture", Html = "<b>fixture</b>" }, [1, 2, 3]);
        store.SetPinned(record.Id, true);
        var placement = new ClipboardWindowPlacement(1, 2, 560, 700, "fixture-screen");
        store.SavePlacement(placement);
        var unreadable = Path.Combine(Source, new string('a', 64) + ".clip");
        File.WriteAllText(unreadable, "unreadable fixture");
        var originalBytes = File.ReadAllBytes(Path.Combine(Source, record.Id + ".clip"));
        var profile = Path.Combine(_root, "profile");
        var data = Path.Combine(_root, "old");
        var binding = ClipboardHistoryStorage.BindingPath(data, profile);

        store.Relocate(Target, folder => ClipboardHistoryStorage.SaveFolder(binding, folder));

        Assert.Equal(Target, store.Folder);
        Assert.Equal(Target, ClipboardHistoryStorage.ResolveFolder(data, profile));
        var reopened = new ClipboardHistoryStore(Target);
        Assert.True(Assert.Single(reopened.Read()).IsPinned);
        Assert.Equal(new byte[] { 1, 2, 3 }, reopened.ReadImage(record.Id));
        Assert.Equal(placement, reopened.ReadPlacement());
        Assert.Equal(1, reopened.UnreadableCount);
        Assert.Equal(originalBytes, File.ReadAllBytes(Path.Combine(Target, record.Id + ".clip")));
        var added = store.Add(new() { Text = "after switching" });
        Assert.True(File.Exists(Path.Combine(Target, added.Id + ".clip")));
        Assert.False(File.Exists(Path.Combine(Source, added.Id + ".clip")));
        Assert.True(File.Exists(Path.Combine(Source, record.Id + ".clip")));
    }

    [Fact]
    public void ExistingFilesAreNeverMergedOrOverwritten()
    {
        var store = new ClipboardHistoryStore(Source);
        store.Add(new() { Text = "source fixture" });
        Directory.CreateDirectory(Target);
        File.WriteAllText(Path.Combine(Target, "keep.txt"), "original fixture");
        var saved = false;
        Assert.Throws<IOException>(() => store.Relocate(Target, _ => saved = true));
        Assert.False(saved);
        Assert.Equal(Source, store.Folder);
        Assert.Equal("original fixture", File.ReadAllText(Path.Combine(Target, "keep.txt")));
    }

    [Fact]
    public void BindingFailureLeavesTheOriginalStoreUsable()
    {
        var store = new ClipboardHistoryStore(Source);
        var record = store.Add(new() { Text = "source fixture" });
        Assert.Throws<IOException>(() => store.Relocate(Target, _ => throw new IOException("fixture save failure")));
        Assert.Equal(Source, store.Folder);
        Assert.Equal(record.Id, Assert.Single(new ClipboardHistoryStore(Source).Read()).Id);
        var added = store.Add(new() { Text = "after failure" });
        Assert.True(File.Exists(Path.Combine(Source, added.Id + ".clip")));
    }

    [Fact]
    public void MissingTextFilesRemainInMemoryAndCanBeSavedAtTheNewLocation()
    {
        var store = new ClipboardHistoryStore(Source);
        var record = store.Add(new() { Text = "in-memory fixture" });
        File.Delete(Path.Combine(Source, record.Id + ".clip"));
        Assert.Equal(record.Text, Assert.Single(store.Read()).Text);
        Assert.Empty(new ClipboardHistoryStore(Source).Read());

        store.Relocate(Target, _ => { });

        Assert.Equal(record.Text, Assert.Single(new ClipboardHistoryStore(Target).Read()).Text);
    }

    [Fact]
    public void MissingImagesCannotBeSilentlyDroppedDuringMigration()
    {
        var store = new ClipboardHistoryStore(Source);
        var record = store.Add(new() { Text = "missing image fixture" }, [1, 2, 3]);
        File.Delete(Path.Combine(Source, record.Id + ".png"));
        Assert.Throws<IOException>(() => store.Relocate(Target, _ => { }));
        Assert.Equal(Source, store.Folder);
        Assert.False(Directory.Exists(Target));
    }

    [Fact]
    public void NewWritesAndDeletionsUseTheNewRecoveryCopy()
    {
        var store = new ClipboardHistoryStore(Source);
        var record = store.Add(new() { Text = "recovery fixture" }, [1, 2, 3]);
        var backupRoot = Path.Combine(_root, "backup");
        var recovery = new InstalledDataRecovery(Path.GetDirectoryName(Target)!, backupRoot);
        store.Relocate(Target, _ => { }, recovery);
        var backupFolder = Path.Combine(backupRoot, "clipboard-history");
        Assert.True(File.Exists(Path.Combine(backupFolder, record.Id + ".clip")));
        store.Delete(record.Id);
        Assert.Empty(Directory.EnumerateFiles(Target));
        Assert.Empty(Directory.EnumerateFiles(backupFolder));
        Assert.True(File.Exists(Path.Combine(Source, record.Id + ".clip")));
    }

    [Fact]
    public void NestedAndRelativeTargetsAreRejected()
    {
        var store = new ClipboardHistoryStore(Source);
        store.Add(new() { Text = "validation fixture" });
        Assert.Throws<ArgumentException>(() => store.Relocate("relative-folder", _ => { }));
        Assert.Throws<IOException>(() => store.Relocate(Path.Combine(Source, "nested"), _ => { }));
        Assert.Throws<IOException>(() => store.Relocate(Path.GetDirectoryName(Source)!, _ => { }));
    }

    [Fact]
    public void DefaultAndCustomBindingsRoundTripWithoutReadingAnyHistory()
    {
        var dataRoot = Path.Combine(_root, "profile");
        var state = Path.Combine(_root, "user-state");
        Assert.Equal(Path.Combine(dataRoot, "clipboard-history"), ClipboardHistoryStorage.ResolveFolder(dataRoot, state));
        var binding = ClipboardHistoryStorage.BindingPath(dataRoot, state);
        ClipboardHistoryStorage.SaveFolder(binding, Target);
        Assert.Equal(Target, ClipboardHistoryStorage.ResolveFolder(dataRoot, state));
        File.WriteAllText(binding, "");
        Assert.Throws<InvalidDataException>(() => ClipboardHistoryStorage.ResolveFolder(dataRoot, state));
    }

    [Fact]
    public void CustomFolderRecoveryNeverCopiesUnrelatedParentFiles()
    {
        var store = new ClipboardHistoryStore(Source);
        var record = store.Add(new() { Text = "restore fixture" });
        var parent = Path.GetDirectoryName(Target)!;
        var recovery = new InstalledDataRecovery(parent, Path.Combine(_root, "backup"));
        store.Relocate(Target, _ => { }, recovery);
        File.WriteAllText(Path.Combine(parent, "keep.txt"), "unrelated fixture");
        Directory.Delete(Target, true);
        recovery.RestoreFolder(Target);
        Assert.Equal(record.Id, Assert.Single(new ClipboardHistoryStore(Target, recovery).Read()).Id);
        Assert.Equal("unrelated fixture", File.ReadAllText(Path.Combine(parent, "keep.txt")));
        Assert.False(File.Exists(Path.Combine(recovery.BackupRoot, "keep.txt")));
    }

    [Fact]
    public void FailedFileCopyCannotPublishAPartialDirectoryOrChangeTheBinding()
    {
        var store = new ClipboardHistoryStore(Source);
        var record = store.Add(new() { Text = "locked file fixture" });
        using var locked = new FileStream(Path.Combine(Source, record.Id + ".clip"), FileMode.Open, FileAccess.Read, FileShare.None);
        var saved = false;
        Assert.Throws<IOException>(() => store.Relocate(Target, _ => saved = true));
        Assert.False(saved);
        Assert.False(Directory.Exists(Target));
        Assert.Equal(Source, store.Folder);
    }

    [Fact]
    public async Task CapturesAcceptedDuringTheSwitchWaitAndWriteToTheNewFolder()
    {
        var store = new ClipboardHistoryStore(Source);
        store.Add(new() { Text = "before switch" });
        using var committing = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var migration = Task.Run(() => store.Relocate(Target, _ =>
        {
            committing.Set();
            Assert.True(release.Wait(TimeSpan.FromSeconds(10)));
        }));
        Assert.True(committing.Wait(TimeSpan.FromSeconds(10)));
        var write = Task.Run(() => store.Add(new() { Text = "during switch" }));
        try { Assert.NotSame(write, await Task.WhenAny(write, Task.Delay(100))); }
        finally { release.Set(); }
        await migration;
        var added = await write;
        Assert.True(File.Exists(Path.Combine(Target, added.Id + ".clip")));
        Assert.False(File.Exists(Path.Combine(Source, added.Id + ".clip")));
    }

    [Fact]
    public void DirectoryBindingSurvivesReinstallRecovery()
    {
        var data = Path.Combine(_root, "profile");
        var state = Path.Combine(_root, "user-state");
        var recovery = new InstalledDataRecovery(data, Path.Combine(_root, "backup"));
        Directory.CreateDirectory(data);
        var binding = ClipboardHistoryStorage.BindingPath(data, state);
        ClipboardHistoryStorage.SaveFolder(binding, Target);
        Directory.Delete(data, true);
        recovery.Initialize(Path.Combine(_root, "unused-legacy"));
        Assert.Equal(Target, ClipboardHistoryStorage.ResolveFolder(data, state));
    }

    [Fact]
    public void SelectingADriveRootStillMapsHistoryToItsRecoveryCopy()
    {
        var driveRoot = Path.GetPathRoot(_root)!;
        var recovery = new InstalledDataRecovery(driveRoot, Path.Combine(_root, "backup"));
        Assert.Equal(Path.Combine(recovery.BackupRoot, "clipboard-history", "fixture.clip"),
            recovery.GetBackupPath(Path.Combine(driveRoot, "clipboard-history", "fixture.clip")));
    }

    [Fact]
    public void RecoveryFailureCannotCommitTheNewBinding()
    {
        var store = new ClipboardHistoryStore(Source);
        var record = store.Add(new() { Text = "backup failure fixture" });
        var blocked = Path.Combine(_root, "blocked-backup");
        File.WriteAllText(blocked, "file blocks directory creation");
        var recovery = new InstalledDataRecovery(Path.GetDirectoryName(Target)!, blocked);
        var saved = false;
        Assert.Throws<IOException>(() => store.Relocate(Target, _ => saved = true, recovery));
        Assert.False(saved);
        Assert.Equal(Source, store.Folder);
        Assert.Equal(record.Id, Assert.Single(store.Read()).Id);
        Assert.False(Directory.Exists(Target));
        File.Delete(blocked);
        store.Relocate(Target, _ => { }, recovery);
        Assert.Equal(Target, store.Folder);
    }

    [Fact]
    public void AnExistingEmptyTargetCanBeSelected()
    {
        var store = new ClipboardHistoryStore(Source);
        var record = store.Add(new() { Text = "empty target fixture" });
        Directory.CreateDirectory(Target);
        store.Relocate(Target, _ => { });
        Assert.Equal(record.Id, Assert.Single(new ClipboardHistoryStore(Target).Read()).Id);
    }

    [Fact]
    public void BindingCanBeSavedWhenTheOldDataLocationCannotBeUsedAsADirectory()
    {
        Directory.CreateDirectory(_root);
        var data = Path.Combine(_root, "blocked-old-data");
        File.WriteAllText(data, "file blocks directory creation");
        var profile = Path.Combine(_root, "user-state");
        var binding = ClipboardHistoryStorage.BindingPath(data, profile);
        ClipboardHistoryStorage.SaveFolder(binding, Target);
        Assert.Equal(Target, ClipboardHistoryStorage.ResolveFolder(data, profile));
        Assert.Equal("file blocks directory creation", File.ReadAllText(data));
    }

    [Fact]
    public void FailedBindingReplacementKeepsThePreviouslySavedDirectory()
    {
        var binding = ClipboardHistoryStorage.BindingPath(Path.Combine(_root, "profile"), Path.Combine(_root, "user-state"));
        ClipboardHistoryStorage.SaveFolder(binding, Source);
        using (new FileStream(binding, FileMode.Open, FileAccess.Read, FileShare.Read))
            Assert.Throws<UnauthorizedAccessException>(() => ClipboardHistoryStorage.SaveFolder(binding, Target));
        Assert.Equal(Source, File.ReadAllText(binding));
    }

    [Fact]
    public void CaseChangesInWindowsFileNamesDoNotDropImagesOrPlacement()
    {
        var store = new ClipboardHistoryStore(Source);
        var record = store.Add(new() { Text = "case fixture" }, [1, 2, 3]);
        var placement = new ClipboardWindowPlacement(1, 2, 560, 700, "fixture-screen");
        store.SavePlacement(placement);
        var image = Path.Combine(Source, record.Id + ".png");
        File.Move(image, image + ".rename");
        File.Move(image + ".rename", Path.Combine(Source, record.Id.ToUpperInvariant() + ".PNG"));
        var window = Path.Combine(Source, "window.json");
        File.Move(window, window + ".rename");
        File.Move(window + ".rename", Path.Combine(Source, "WINDOW.JSON"));
        store.Relocate(Target, _ => { });
        Assert.Equal(new byte[] { 1, 2, 3 }, store.ReadImage(record.Id));
        Assert.Equal(placement, store.ReadPlacement());
    }

    [Fact]
    public void AFailedBindingCanBeRetriedAtTheSameDestination()
    {
        var store = new ClipboardHistoryStore(Source);
        store.Add(new() { Text = "retry fixture" });
        Assert.Throws<IOException>(() => store.Relocate(Target, _ => throw new IOException("binding failure fixture")));
        store.Relocate(Target, _ => { });
        Assert.Equal(Target, store.Folder);
        Assert.Single(new ClipboardHistoryStore(Target).Read());
    }

    [Fact]
    public void AReusedDestinationRecoveryContainsOnlyTheCurrentSnapshot()
    {
        var recovery = new InstalledDataRecovery(Path.GetDirectoryName(Target)!, Path.Combine(_root, "backup"));
        var previous = new ClipboardHistoryStore(Target, recovery);
        previous.Add(new() { Text = "stale recovery fixture" });
        Directory.Delete(Target, true);
        var store = new ClipboardHistoryStore(Source);
        var record = store.Add(new() { Text = "current snapshot fixture" });
        store.Relocate(Target, _ => { }, recovery);
        Directory.Delete(Target, true);
        recovery.RestoreFolder(Target);
        Assert.Equal(record.Id, Assert.Single(new ClipboardHistoryStore(Target, recovery).Read()).Id);
    }

    [Fact]
    public void AnUnreadableBindingCannotSilentlySelectTheDefaultHistory()
    {
        var data = Path.Combine(_root, "profile");
        var state = Path.Combine(_root, "user-state");
        Directory.CreateDirectory(ClipboardHistoryStorage.BindingPath(data, state));
        Assert.Throws<UnauthorizedAccessException>(() => ClipboardHistoryStorage.ResolveFolder(data, state));
    }

    [Fact]
    public async Task UnavailableConfiguredStorageCanBeRetriedWithoutCachingAnEmptyHistory()
    {
        var data = Path.Combine(_root, "profile");
        var state = Path.Combine(_root, "user-state");
        ClipboardHistoryStorage.SaveFolder(ClipboardHistoryStorage.BindingPath(data, state), Target);
        var loader = new ClipboardHistoryStoreLoader(() => ClipboardHistoryStorage.Open(data, state));
        await Assert.ThrowsAsync<DirectoryNotFoundException>(() => loader.OpenAsync());
        var restored = new ClipboardHistoryStore(Target);
        var record = restored.Add(new() { Text = "reconnected storage fixture" });
        Assert.Equal(record.Id, Assert.Single((await loader.OpenAsync()).Read()).Id);
    }

    [Fact]
    public void OpeningConfiguredStorageRestoresOnlyItsOwnRecoveryAndRetainsFutureDeletes()
    {
        var data = Path.Combine(_root, "profile");
        var state = Path.Combine(_root, "user-state");
        var parent = Path.GetDirectoryName(Target)!;
        var recovery = new InstalledDataRecovery(parent, InstalledDataRecovery.RecoveryRoot(parent, state));
        var store = new ClipboardHistoryStore(Source);
        var record = store.Add(new() { Text = "startup recovery fixture" });
        store.Relocate(Target, folder => ClipboardHistoryStorage.SaveFolder(ClipboardHistoryStorage.BindingPath(data, state), folder), recovery);
        Directory.Delete(Target, true);
        var reopened = ClipboardHistoryStorage.Open(data, state);
        Assert.Equal(record.Id, Assert.Single(reopened.Read()).Id);
        reopened.Delete(record.Id);
        Directory.Delete(Target, true);
        Assert.Empty(ClipboardHistoryStorage.Open(data, state).Read());
    }

    [Fact]
    public void FailedSnapshotCopyLeavesTheLastCompleteRecoveryUntouched()
    {
        var backup = Path.Combine(_root, "backup");
        var previous = new ClipboardHistoryStore(Path.Combine(backup, "clipboard-history"));
        var oldRecord = previous.Add(new() { Text = "last complete snapshot fixture" });
        var current = new ClipboardHistoryStore(Target);
        var newRecord = current.Add(new() { Text = "new snapshot fixture" });
        var recovery = new InstalledDataRecovery(Path.GetDirectoryName(Target)!, backup);
        using (new FileStream(Path.Combine(Target, newRecord.Id + ".clip"), FileMode.Open, FileAccess.Read, FileShare.None))
            Assert.Throws<IOException>(() => recovery.ReplaceFolderBackup(Target));
        Assert.Equal(oldRecord.Id, Assert.Single(new ClipboardHistoryStore(previous.Folder).Read()).Id);
        recovery.ReplaceFolderBackup(Target);
        Assert.Equal(newRecord.Id, Assert.Single(new ClipboardHistoryStore(previous.Folder).Read()).Id);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }
}
