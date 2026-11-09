using OverTranslate.Services.ClipboardHistory;
using OverTranslate.Services;
using Xunit;

namespace OverTranslate.Tests;

public sealed class ClipboardHistoryStoreTests : IDisposable
{
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
