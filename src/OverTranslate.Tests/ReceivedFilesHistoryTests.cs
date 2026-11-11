using System.Text.Json;
using OverTranslate.Services.Lan;
using Xunit;

namespace OverTranslate.Tests;

public sealed class ReceivedFilesHistoryTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "received-history-" + Guid.NewGuid().ToString("N"));
    public ReceivedFilesHistoryTests() => Directory.CreateDirectory(_root);
    public void Dispose() => Directory.Delete(_root, true);

    [Fact]
    public void Receipts_survive_other_activity_eviction_restart_and_repeated_load()
    {
        var path = Path.Combine(_root, "activity.json");
        var journal = new SyncActivityStore(path);
        for (var i = 0; i < 125; i++) journal.Update(new("receipt-" + i, SyncKind.Files, true, "fixture", SyncState.Completed, Folder: _root));
        for (var i = 0; i < 150; i++) journal.Update(new("clipboard-" + i, SyncKind.Clipboard, false, "fixture", SyncState.Completed));
        Assert.Equal(100, journal.Snapshot().Count); Assert.Equal(125, journal.ReceivedSnapshot().Count);
        Assert.Contains(journal.ReceivedSnapshot(), x => x.Id == "receipt-0");
        Assert.Equal(125, new SyncActivityStore(path).ReceivedSnapshot().Count);
        Assert.Equal(125, new SyncActivityStore(path).ReceivedSnapshot().Count);
        Assert.Equal(125, File.ReadLines(Path.ChangeExtension(path, ".received.jsonl")).Count(x => !string.IsNullOrWhiteSpace(x)));
    }

    [Fact]
    public void Legacy_receipts_migrate_once_and_ignore_incomplete_or_outgoing_records()
    {
        var path = Path.Combine(_root, "legacy.json");
        SyncActivity[] legacy = [new("received", SyncKind.Files, true, "fixture", SyncState.Completed, Folder: _root),
            new("outgoing", SyncKind.Files, false, "fixture", SyncState.Completed, Folder: _root),
            new("failed", SyncKind.Files, true, "fixture", SyncState.Failed, Folder: _root)];
        File.WriteAllText(path, JsonSerializer.Serialize(legacy));
        Assert.Equal("received", Assert.Single(new SyncActivityStore(path).ReceivedSnapshot()).Id);
        Assert.Single(new SyncActivityStore(path).ReceivedSnapshot());
        Assert.Single(File.ReadLines(Path.ChangeExtension(path, ".received.jsonl")).Where(x => !string.IsNullOrWhiteSpace(x)));
    }

    [Fact]
    public void Truncated_record_does_not_hide_valid_records_or_later_appends()
    {
        var path = Path.Combine(_root, "activity.json");
        var journal = new SyncActivityStore(path);
        journal.Update(new("first", SyncKind.Files, true, "fixture", SyncState.Completed, Folder: _root));
        File.AppendAllText(Path.ChangeExtension(path, ".received.jsonl"), "\n{broken");
        var reopened = new SyncActivityStore(path);
        Assert.Single(reopened.ReceivedSnapshot()); Assert.Contains("部分", reopened.ReceivedStorageWarning);
        reopened.Update(new("second", SyncKind.Files, true, "fixture", SyncState.Completed, Folder: _root));
        Assert.Equal(2, new SyncActivityStore(path).ReceivedSnapshot().Count);
    }

    [Fact]
    public void Archive_write_failure_is_visible_and_same_receipt_can_be_retried()
    {
        var path = Path.Combine(_root, "activity.json");
        var archive = Path.ChangeExtension(path, ".received.jsonl");
        Directory.CreateDirectory(archive);
        var journal = new SyncActivityStore(path);
        var receipt = new SyncActivity("first", SyncKind.Files, true, "fixture", SyncState.Completed, Folder: _root);
        journal.Update(receipt);
        Assert.Contains("保存失败", journal.ReceivedStorageWarning); Assert.Single(journal.ReceivedSnapshot());
        Directory.Delete(archive);
        journal.Update(receipt);
        Assert.Equal("", journal.ReceivedStorageWarning);
        Assert.Single(new SyncActivityStore(path).ReceivedSnapshot());
    }
}
