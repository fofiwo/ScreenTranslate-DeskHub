using OverTranslate.Services.ClipboardHistory;
using OverTranslate.Services;
using Xunit;

namespace OverTranslate.Tests;

public sealed class ClipboardHistoryStoreTests : IDisposable
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RawContentDeletionRemovesSavedContentAndItsRecoveryCopies(bool withImage)
    {
        var data = Path.Combine(_folder, "data");
        var backup = Path.Combine(_folder, "backup");
        var recovery = new InstalledDataRecovery(data, backup);
        var folder = Path.Combine(data, "clipboard-history");
        var store = new ClipboardHistoryStore(folder, recovery);
        var record = store.Add(new() { Text = "remove content", Html = "<b>remove content</b>", Rtf = @"{\rtf1 remove content}" },
            withImage ? [1, 2, 3] : null);

        store.Delete(record.Id);

        Assert.Empty(store.Read());
        Assert.Empty(Directory.EnumerateFiles(folder));
        Assert.Empty(Directory.EnumerateFiles(Path.Combine(backup, "clipboard-history")));
        Directory.Delete(data, true);
        recovery.Initialize(Path.Combine(_folder, "unused-legacy"));
        Assert.Empty(new ClipboardHistoryStore(folder).Read());
    }

    [Fact]
    public void LockedOrphanCleanupReportsFailureAndCanBeRetried()
    {
        var data = Path.Combine(_folder, "data");
        var recovery = new InstalledDataRecovery(data, Path.Combine(_folder, "backup"));
        var folder = Path.Combine(data, "clipboard-history");
        var store = new ClipboardHistoryStore(folder, recovery);
        var record = store.Add(new() { Text = "known orphan fixture" }, [1, 2, 3]);
        var image = Path.Combine(folder, record.Id + ".png");
        File.Delete(Path.Combine(folder, record.Id + ".clip"));
        store = new ClipboardHistoryStore(folder, recovery);
        using (new FileStream(image, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            Assert.Equal((0, 1), store.DeleteUnpinned());
            Assert.True(File.Exists(image));
        }
        Assert.Equal((0, 0), store.DeleteUnpinned());
        Assert.False(File.Exists(image));
    }

    [Fact]
    public void ClearingUnpinnedHistoryRemovesOrphansButKeepsPinnedUnreadableAndUnrelatedFiles()
    {
        var data = Path.Combine(_folder, "data");
        var backup = Path.Combine(_folder, "backup");
        var recovery = new InstalledDataRecovery(data, backup);
        var folder = Path.Combine(data, "clipboard-history");
        var backupFolder = Path.Combine(backup, "clipboard-history");
        var store = new ClipboardHistoryStore(folder, recovery);
        var pinned = store.Add(new() { Text = "keep pinned" }, [1, 2, 3]);
        store.SetPinned(pinned.Id, true);
        var orphan = store.Add(new() { Text = "orphan fixture" }, [4, 5, 6]);
        File.Delete(Path.Combine(folder, orphan.Id + ".clip"));
        var backupOnly = store.Add(new() { Text = "backup-only fixture" }, [7, 8, 9]);
        File.Delete(Path.Combine(folder, backupOnly.Id + ".clip"));
        File.Delete(Path.Combine(folder, backupOnly.Id + ".png"));
        var unreadable = new string('a', 64);
        File.WriteAllText(Path.Combine(folder, unreadable + ".clip"), "corrupt fixture");
        File.WriteAllBytes(Path.Combine(folder, unreadable + ".png"), [10]);
        var unrelated = Path.Combine(folder, "original.png");
        File.WriteAllBytes(unrelated, [11]);
        store = new ClipboardHistoryStore(folder, recovery);

        Assert.Equal((0, 0), store.DeleteUnpinned());

        Assert.Equal(pinned.Id, Assert.Single(store.Read()).Id);
        foreach (var id in new[] { orphan.Id, backupOnly.Id })
            foreach (var location in new[] { folder, backupFolder })
                foreach (var extension in new[] { ".clip", ".png" })
                    Assert.False(File.Exists(Path.Combine(location, id + extension)));
        Assert.True(File.Exists(Path.Combine(folder, pinned.Id + ".png")));
        Assert.True(File.Exists(Path.Combine(backupFolder, pinned.Id + ".png")));
        Assert.True(File.Exists(Path.Combine(folder, unreadable + ".clip")));
        Assert.True(File.Exists(Path.Combine(folder, unreadable + ".png")));
        Assert.Equal(new byte[] { 11 }, File.ReadAllBytes(unrelated));
    }

    [Theory]
    [InlineData(".clip")]
    [InlineData(".png")]
    public void ALockedRecoveryCopyKeepsTheWholeRecordAndDeletionCanBeRetried(string extension)
    {
        var data = Path.Combine(_folder, "data");
        var backup = Path.Combine(_folder, "backup");
        var recovery = new InstalledDataRecovery(data, backup);
        var folder = Path.Combine(data, "clipboard-history");
        var store = new ClipboardHistoryStore(folder, recovery);
        var original = Path.Combine(_folder, "original.png");
        Directory.CreateDirectory(_folder); File.WriteAllText(original, "keep original");
        var originalFolder = Path.Combine(_folder, "original-folder");
        Directory.CreateDirectory(originalFolder);
        File.WriteAllText(Path.Combine(originalFolder, "note.txt"), "keep folder contents");
        var record = store.Add(new() { Text = "recovery delete fixture", Files = [original, originalFolder] }, [1, 2, 3]);
        var backupFolder = Path.Combine(backup, "clipboard-history");
        using (new FileStream(Path.Combine(backupFolder, record.Id + extension), FileMode.Open, FileAccess.Read, FileShare.None))
        {
            Assert.Throws<IOException>(() => store.Delete(record.Id));
            Assert.Single(store.Read());
            Assert.Single(new ClipboardHistoryStore(folder).Read());
            Assert.True(File.Exists(Path.Combine(folder, record.Id + ".png")));
        }

        store.Delete(record.Id);

        Assert.Empty(store.Read());
        Assert.Empty(Directory.EnumerateFiles(folder));
        Assert.Empty(Directory.EnumerateFiles(backupFolder));
        Assert.Equal("keep original", File.ReadAllText(original));
        Assert.Equal("keep folder contents", File.ReadAllText(Path.Combine(originalFolder, "note.txt")));
        Directory.Delete(data, true);
        recovery.Initialize(Path.Combine(_folder, "unused-legacy"));
        Assert.Empty(new ClipboardHistoryStore(folder).Read());
    }

    [Fact]
    public void DeletingAPartiallyRemovedImageRecordCanBeRetried()
    {
        var store = new ClipboardHistoryStore(_folder);
        var record = store.Add(new() { Text = "partial delete fixture" }, [1, 2, 3]);
        File.Delete(Path.Combine(_folder, record.Id + ".clip"));

        store.Delete(record.Id);

        Assert.Empty(store.Read());
        Assert.False(File.Exists(Path.Combine(_folder, record.Id + ".png")));
        Assert.Empty(new ClipboardHistoryStore(_folder).Read());
    }

    [Fact]
    public void RecopiedPinnedRecordLeadsPinnedGroupAndOrdinaryRecordLeadsOrdinaryGroup()
    {
        var store = new ClipboardHistoryStore(_folder);
        var first = store.Add(new() { Text = "first", CopiedAt = DateTimeOffset.UtcNow.AddDays(-2) });
        var second = store.Add(new() { Text = "second", CopiedAt = DateTimeOffset.UtcNow.AddDays(-1) });
        var third = store.Add(new() { Text = "third" });
        var fourth = store.Add(new() { Text = "fourth" });
        store.SetPinned(first.Id, true); store.SetPinned(second.Id, true);
        store.MarkCopied(first.Id, DateTimeOffset.UtcNow.AddMinutes(1));
        store.MarkCopied(third.Id, DateTimeOffset.UtcNow.AddMinutes(2));
        Assert.Equal(new[] { first.Id, second.Id, third.Id, fourth.Id }, store.Read().Select(r => r.Id));
    }

    [Fact]
    public void BulkDeletePreservesPinsAndOriginalFilesAndCanRetryLockedRecords()
    {
        var store = new ClipboardHistoryStore(_folder);
        var pinned = store.Add(new() { Text = "pinned" }, [1, 2, 3]);
        store.SetPinned(pinned.Id, true);
        var original = Path.Combine(_folder, "original.txt");
        File.WriteAllText(original, "original");
        store.Add(new() { Files = [original] });
        var image = store.Add(new() { Text = "image" }, [4, 5, 6]);
        var locked = store.Add(new() { Text = "locked" });
        using (var handle = new FileStream(Path.Combine(_folder, locked.Id + ".clip"), FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            Assert.Equal((2, 1), store.DeleteUnpinned());
            Assert.Equal(2, new ClipboardHistoryStore(_folder).Read().Count);
        }
        Assert.False(File.Exists(Path.Combine(_folder, image.Id + ".png")));
        Assert.True(File.Exists(Path.Combine(_folder, pinned.Id + ".png")));
        Assert.Equal("original", File.ReadAllText(original));
        Assert.Equal((1, 0), store.DeleteUnpinned());
        Assert.Equal(pinned.Id, Assert.Single(new ClipboardHistoryStore(_folder).Read()).Id);
        Assert.Equal((0, 0), store.DeleteUnpinned());
        store.Delete(pinned.Id);
        Assert.Empty(new ClipboardHistoryStore(_folder).Read());
    }

    [Fact]
    public void UnchangedReadsReuseSnapshotAndCommittedMutationsInvalidateIt()
    {
        var store = new ClipboardHistoryStore(_folder);
        var first = store.Add(new() { Text = "snapshot A" });
        var snapshot = store.Read();
        Assert.Same(snapshot, store.Read());
        var second = store.Add(new() { Text = "snapshot B" });
        Assert.Single(snapshot);
        Assert.Equal(2, store.Read().Count);
        store.SetPinned(first.Id, true);
        Assert.Equal(first.Id, store.Read()[0].Id);
        store.SetPinned(first.Id, false);
        store.MarkCopied(first.Id, DateTimeOffset.UtcNow.AddMinutes(1));
        Assert.Equal(first.Id, store.Read()[0].Id);
        snapshot = store.Read();
        store.Delete(second.Id);
        Assert.Equal(2, snapshot.Count);
        Assert.Single(store.Read());
    }

    private readonly string _folder = Path.Combine(Path.GetTempPath(), "clipboard-store-" + Guid.NewGuid().ToString("N"));
    public void Dispose() { if (Directory.Exists(_folder)) Directory.Delete(_folder, true); }

    [Fact]
    public void HistoryAndPinsSurviveRestartWithoutAutomaticEviction()
    {
        var store = new ClipboardHistoryStore(_folder);
        var first = store.Add(new() { Text = "first", CopiedAt = DateTimeOffset.UtcNow.AddDays(-200) });
        store.SetPinned(first.Id, true);
        for (var i = 0; i < 1005; i++) store.Add(new() { Text = "record " + i });
        var reopened = new ClipboardHistoryStore(_folder);
        Assert.Equal(1006, reopened.Read().Count);
        Assert.True(reopened.Read().Single(i => i.Id == first.Id).IsPinned);
        Assert.DoesNotContain("first", File.ReadAllText(Path.Combine(_folder, first.Id + ".clip")));
        reopened.Delete(first.Id);
        Assert.DoesNotContain(new ClipboardHistoryStore(_folder).Read(), i => i.Id == first.Id);
    }

    [Fact]
    public void DuplicateCopiesKeepTheFirstTimestampAndPinButUpdateLastCopy()
    {
        var store = new ClipboardHistoryStore(_folder);
        var before = DateTimeOffset.UtcNow.AddHours(-1);
        var record = store.Add(new() { Text = "same", CopiedAt = before });
        store.SetPinned(record.Id, true);
        var now = DateTimeOffset.UtcNow;
        store.Add(new() { Text = "same", CopiedAt = now });
        var saved = Assert.Single(new ClipboardHistoryStore(_folder).Read());
        Assert.Equal(before, saved.CreatedAt);
        Assert.Equal(now, saved.CopiedAt);
        Assert.True(saved.IsPinned);
    }

    [Theory]
    [InlineData("https://example.com/path", "链接")]
    [InlineData("ordinary text", "文本")]
    [InlineData("const value = 42;\nconsole.log(value);", "代码")]
    public void ContentIsClassifiedAndFullTextIsSearchable(string text, string category)
    {
        var store = new ClipboardHistoryStore(_folder);
        var item = store.Add(new() { Text = text });
        Assert.Equal(category, item.Category);
        Assert.True(item.Matches("全部", text[^4..]));
        Assert.False(item.Matches("图片", ""));
        Assert.Equal("富文本", store.Add(new() { Text = "styled", Html = "<b>styled</b>" }).Category);
        Assert.Equal("文件", store.Add(new() { Files = [@"C:\example\report.xlsx"] }).Category);
    }

    [Fact]
    public void CorruptOrFutureRecordsAreReportedAndNeverOverwritten()
    {
        Directory.CreateDirectory(_folder);
        var path = Path.Combine(_folder, new string('a', 64) + ".clip");
        File.WriteAllText(path, "broken fixture");
        var store = new ClipboardHistoryStore(_folder);
        Assert.Empty(store.Read());
        Assert.Equal(1, store.UnreadableCount);
        store.Add(new() { Text = "new record" });
        Assert.Equal("broken fixture", File.ReadAllText(path));
        Assert.Throws<ArgumentException>(() => store.Delete("../outside"));
    }

    [Fact]
    public void AnUnknownSchemaCannotBeOverwrittenByCopyingTheSameContent()
    {
        var store = new ClipboardHistoryStore(_folder);
        var record = store.Add(new() { Text = "future schema fixture" });
        var path = Path.Combine(_folder, record.Id + ".clip");
        var future = LocalSecretStore.Protect(System.Text.Json.JsonSerializer.Serialize(record with { SchemaVersion = 2, PinnedAt = DateTimeOffset.UtcNow }));
        File.WriteAllText(path, future);
        var reopened = new ClipboardHistoryStore(_folder);
        Assert.Equal(1, reopened.UnreadableCount);
        Assert.Throws<InvalidDataException>(() => reopened.Add(new() { Text = record.Text }));
        Assert.Equal(future, File.ReadAllText(path));
    }

    [Fact]
    public void NullFieldsAreIsolatedInsteadOfCrashingSearch()
    {
        var store = new ClipboardHistoryStore(_folder);
        var record = store.Add(new() { Text = "malformed fixture" });
        File.WriteAllText(Path.Combine(_folder, record.Id + ".clip"), LocalSecretStore.Protect(
            System.Text.Json.JsonSerializer.Serialize(record with { Source = null! })));
        var reopened = new ClipboardHistoryStore(_folder);
        Assert.Empty(reopened.Read());
        Assert.Equal(1, reopened.UnreadableCount);
        Assert.Single(Directory.GetFiles(_folder, "*.clip"));
    }

    [Fact]
    public void ALockedImageCannotLeaveADeletedRecordWithAnUndeletableAttachment()
    {
        var store = new ClipboardHistoryStore(_folder);
        var record = store.Add(new() { Text = "image fixture" }, [1, 2, 3]);
        var path = Path.Combine(_folder, record.Id + ".png");
        using (var locked = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            Assert.Throws<IOException>(() => store.Delete(record.Id));
            Assert.Single(store.Read());
            Assert.Single(new ClipboardHistoryStore(_folder).Read());
        }
        store.Delete(record.Id);
        Assert.Empty(store.Read()); Assert.False(File.Exists(path));
    }

    [Fact]
    public void UnknownSchemaProtectionAlsoHandlesUppercaseWindowsFileNames()
    {
        var store = new ClipboardHistoryStore(_folder);
        var record = store.Add(new() { Text = "uppercase fixture" });
        var path = Path.Combine(_folder, record.Id + ".clip");
        var future = LocalSecretStore.Protect(System.Text.Json.JsonSerializer.Serialize(record with { SchemaVersion = 2 }));
        File.WriteAllText(path, future);
        File.Move(path, Path.Combine(_folder, record.Id.ToUpperInvariant() + ".clip"));
        var reopened = new ClipboardHistoryStore(_folder);
        Assert.Throws<InvalidDataException>(() => reopened.Add(new() { Text = record.Text }));
        Assert.Equal(future, File.ReadAllText(path));
    }

    [Fact]
    public void FailedMetadataCommitRollsBackOnlyItsNewUncommittedImage()
    {
        var store = new ClipboardHistoryStore(_folder);
        var record = store.Add(new() { Text = "rollback fixture" }, [1, 2, 3]);
        store.Delete(record.Id);
        Directory.CreateDirectory(Path.Combine(_folder, record.Id + ".clip"));
        var failure = Record.Exception(() => store.Add(new() { Text = record.Text }, [1, 2, 3]));
        Assert.True(failure is IOException or UnauthorizedAccessException);
        Assert.Empty(store.Read());
        Assert.False(File.Exists(Path.Combine(_folder, record.Id + ".png")));
    }

    [Fact]
    public void NullFileElementsAreIsolatedAndDeletingReferencesNeverDeletesOriginals()
    {
        var store = new ClipboardHistoryStore(_folder);
        var original = Path.Combine(_folder, "original.txt");
        Directory.CreateDirectory(_folder); File.WriteAllText(original, "fixture original");
        var record = store.Add(new() { Files = [original] });
        File.WriteAllText(Path.Combine(_folder, record.Id + ".clip"), LocalSecretStore.Protect(
            System.Text.Json.JsonSerializer.Serialize(record with { Files = [null!] })));
        Assert.Equal(1, new ClipboardHistoryStore(_folder).UnreadableCount);
        store.Delete(record.Id);
        Assert.Equal("fixture original", File.ReadAllText(original));
    }

    [Fact]
    public void AnUndecryptableRecordDoesNotPreventOtherHistoryFromLoading()
    {
        var store = new ClipboardHistoryStore(_folder);
        var record = store.Add(new() { Text = "decrypt failure fixture" });
        var good = store.Add(new() { Text = "still readable fixture" });
        var path = Path.Combine(_folder, record.Id + ".clip");
        File.WriteAllText(path, Convert.ToBase64String(new byte[] { 1, 2, 3, 4 }));
        var reopened = new ClipboardHistoryStore(_folder);
        Assert.Equal(good.Id, Assert.Single(reopened.Read()).Id);
        Assert.Equal(1, reopened.UnreadableCount);
        Assert.Throws<InvalidDataException>(() => reopened.Add(new() { Text = record.Text }));
    }

    [Fact]
    public void CopyingAnExistingItemUpdatesItsTimeWithoutLosingPinOrFirstTimestamp()
    {
        var store = new ClipboardHistoryStore(_folder);
        var before = DateTimeOffset.UtcNow.AddHours(-2);
        var record = store.Add(new() { Text = "recopy fixture", CopiedAt = before });
        store.SetPinned(record.Id, true);
        var now = DateTimeOffset.UtcNow;
        store.MarkCopied(record.Id, now);
        var saved = Assert.Single(new ClipboardHistoryStore(_folder).Read());
        Assert.Equal(now, saved.CopiedAt); Assert.Equal(before, saved.CreatedAt); Assert.True(saved.IsPinned);
    }
}
