using OverTranslate.Services;
using OverTranslate.Services.ClipboardHistory;
using Xunit;

namespace OverTranslate.Tests;

public sealed class ClipboardDeletionAdversarialTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "clipboard-delete-review-" + Guid.NewGuid().ToString("N"));
    private string Data => Path.Combine(_root, "data");
    private string Backup => Path.Combine(_root, "backup");
    private string History => Path.Combine(Data, "clipboard-history");
    private string BackupHistory => Path.Combine(Backup, "clipboard-history");

    [Theory]
    [InlineData(false, ".clip")]
    [InlineData(false, ".png")]
    [InlineData(true, ".clip")]
    [InlineData(true, ".png")]
    public void ReadOnlyFilesCannotCausePartialDeletion(bool inBackup, string extension)
    {
        var store = new ClipboardHistoryStore(History, new InstalledDataRecovery(Data, Backup));
        var record = store.Add(new() { Text = "read-only fixture" }, [1, 2, 3]);
        var paths = new[] { History, BackupHistory }.SelectMany(folder =>
            new[] { Path.Combine(folder, record.Id + ".clip"), Path.Combine(folder, record.Id + ".png") }).ToArray();
        var before = paths.ToDictionary(path => path, File.ReadAllBytes);
        var readOnly = Path.Combine(inBackup ? BackupHistory : History, record.Id + extension);
        File.SetAttributes(readOnly, File.GetAttributes(readOnly) | FileAttributes.ReadOnly);
        try
        {
            var error = Record.Exception(() => store.Delete(record.Id));
            Assert.True(error is IOException or UnauthorizedAccessException);
            foreach (var path in paths)
            {
                Assert.True(File.Exists(path), path);
                Assert.Equal(before[path], File.ReadAllBytes(path));
            }
            Assert.Single(store.Read());
        }
        finally { if (File.Exists(readOnly)) File.SetAttributes(readOnly, FileAttributes.Normal); }
        store.Delete(record.Id);
        Assert.All(paths, path => Assert.False(File.Exists(path)));
    }

    [Theory]
    [InlineData("pinned")]
    [InlineData("future")]
    [InlineData("corrupt")]
    public void RecoveryOnlyPinnedOrUnknownRecordsArePreserved(string kind)
    {
        var recovery = new InstalledDataRecovery(Data, Backup);
        var store = new ClipboardHistoryStore(History, recovery);
        var record = store.Add(new() { Text = "recovery-only fixture" }, [1, 2, 3]);
        if (kind == "pinned") store.SetPinned(record.Id, true);
        var backupRecord = Path.Combine(BackupHistory, record.Id + ".clip");
        if (kind == "future") File.WriteAllText(backupRecord, LocalSecretStore.Protect(
            System.Text.Json.JsonSerializer.Serialize(record with { SchemaVersion = 2 })));
        if (kind == "corrupt") File.WriteAllText(backupRecord, "corrupt fixture");
        File.Delete(Path.Combine(History, record.Id + ".clip"));
        File.Delete(Path.Combine(History, record.Id + ".png"));
        store = new ClipboardHistoryStore(History, recovery);

        var result = store.DeleteUnpinned();

        Assert.Equal(0, result.Deleted);
        Assert.True(File.Exists(backupRecord));
        Assert.True(File.Exists(Path.Combine(BackupHistory, record.Id + ".png")));
        Assert.Equal(kind == "pinned" ? 0 : 1, result.Failed);
    }

    [Fact]
    public void HashNamedSourceFilesSurviveClearingAndRestart()
    {
        Directory.CreateDirectory(History);
        var source = Path.Combine(History, new string('b', 64) + ".png");
        File.WriteAllBytes(source, [9, 8, 7]);
        var store = new ClipboardHistoryStore(History);
        store.Add(new() { Files = [source] });

        store.DeleteUnpinned();
        store.DeleteUnpinned();
        new ClipboardHistoryStore(History).DeleteUnpinned();

        Assert.Equal(new byte[] { 9, 8, 7 }, File.ReadAllBytes(source));
        Assert.Empty(store.Read());
    }

    [Fact]
    public void UnknownRecordIdsCannotClaimUnownedFiles()
    {
        Directory.CreateDirectory(History);
        var id = new string('c', 64);
        var source = Path.Combine(History, id + ".png");
        File.WriteAllBytes(source, [9, 8, 7]);
        var store = new ClipboardHistoryStore(History);

        store.Delete(id);

        Assert.Equal(new byte[] { 9, 8, 7 }, File.ReadAllBytes(source));
    }

    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
}
