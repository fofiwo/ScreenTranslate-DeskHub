using System.IO.Compression;
using System.Net;
using OverTranslate.Services.Lan;
using Xunit;

namespace OverTranslate.Tests;

public sealed class LanBundleTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "lan-bundle-test-" + Guid.NewGuid().ToString("N"));
    public LanBundleTests() => Directory.CreateDirectory(_root);
    public void Dispose() => Directory.Delete(_root, true);

    [Fact]
    public async Task Folder_and_empty_directories_roundtrip_without_overwriting()
    {
        var source = Path.Combine(_root, "project");
        Directory.CreateDirectory(Path.Combine(source, "empty"));
        await File.WriteAllTextAsync(Path.Combine(source, "note.txt"), "fixture");
        using var bundle = await LanBundle.CreateAsync([source], default);
        var target = Path.Combine(_root, "receive");
        var first = await LanBundle.ExtractAsync(bundle.Path, target, "batch", default);
        var second = await LanBundle.ExtractAsync(bundle.Path, target, "batch", default);
        Assert.NotEqual(first, second);
        Assert.Equal("fixture", await File.ReadAllTextAsync(Path.Combine(first, "project", "note.txt")));
        Assert.True(Directory.Exists(Path.Combine(first, "project", "empty")));
    }

    [Theory]
    [InlineData("../escape.txt")]
    [InlineData("/absolute.txt")]
    [InlineData("dir/CON.txt")]
    [InlineData("dir\\escape.txt")]
    [InlineData("dir/file:stream")]
    public async Task Unsafe_archive_never_leaves_partial_destination(string name)
    {
        var zip = Path.Combine(_root, "bad.zip");
        using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create))
            using (var writer = new StreamWriter(archive.CreateEntry(name).Open())) writer.Write("fixture");
        var target = Path.Combine(_root, "target");
        await Assert.ThrowsAsync<InvalidDataException>(() => LanBundle.ExtractAsync(zip, target, "batch", default));
        Assert.Empty(Directory.GetFileSystemEntries(target));
    }

    [Fact]
    public async Task Offer_wait_does_not_block_clipboard_and_rejection_writes_no_files()
    {
        var target = Path.Combine(_root, "receive");
        await using var host = new LanNode("fixture-A", () => target);
        await using var client = new LanNode("fixture-B", () => target);
        host.ConfirmPairAsync = (_, _) => Task.FromResult(true);
        var offered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var answer = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        host.ConfirmFilesAsync = (_, _) => { offered.TrySetResult(); return answer.Task; };
        host.ApplyClipboardAsync = (_, _) => Task.CompletedTask;
        await host.ListenAsync(IPAddress.Loopback, 0);
        await client.ConnectAsync(IPAddress.Loopback, host.Port, host.PairCode);
        var source = Path.Combine(_root, "note.txt");
        await File.WriteAllTextAsync(source, "fixture");
        var sending = client.SendBundleAsync([source]);
        await offered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(await client.SendClipboardAsync(new("text", [1])).WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.False(Directory.Exists(target));
        answer.SetResult(null);
        Assert.False(await sending.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.True(host.IsConnected && client.IsConnected);
        host.ConfirmFilesAsync = (_, _) => Task.FromResult<string?>(target);
        Assert.True(await client.SendBundleAsync([source]).WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Single(Directory.GetFiles(target, "note.txt", SearchOption.AllDirectories));
    }

    [Fact]
    public void Activity_survives_restart_bounds_history_and_interrupts_unfinished_work()
    {
        var path = Path.Combine(_root, "activity.json");
        var journal = new SyncActivityStore(path);
        for (var i = 0; i < 120; i++)
            journal.Update(new(i.ToString(), SyncKind.Clipboard, false, "fixture", SyncState.Completed));
        journal.Update(new("pending", SyncKind.Files, true, "fixture", SyncState.Waiting));
        var reopened = new SyncActivityStore(path);
        Assert.Equal(100, reopened.Snapshot().Count);
        Assert.Equal(SyncState.Interrupted, reopened.Snapshot().First().State);
        Assert.DoesNotContain("Bytes", File.ReadAllText(path));
    }

    [Fact]
    public async Task Cancelling_offer_stops_prompt_without_writing_payload_and_persists_status()
    {
        var destination = Path.Combine(_root, "receive");
        var journalPath = Path.Combine(_root, "sender.json");
        await using var host = new LanNode("fixture-A", () => destination);
        await using var client = new LanNode("fixture-B", () => destination) { Activities = new(journalPath) };
        host.ConfirmPairAsync = (_, _) => Task.FromResult(true);
        var offered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var dismissed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        host.ConfirmFilesAsync = async (_, token) =>
        {
            offered.TrySetResult();
            try { await Task.Delay(Timeout.InfiniteTimeSpan, token); return null; }
            finally { dismissed.TrySetResult(); }
        };
        await host.ListenAsync(IPAddress.Loopback, 0);
        await client.ConnectAsync(IPAddress.Loopback, host.Port, host.PairCode);
        var source = Path.Combine(_root, "fixture.txt");
        await File.WriteAllTextAsync(source, "synthetic content must not enter the journal");
        using var cancel = new CancellationTokenSource();
        var sending = client.SendBundleAsync([source], cancel.Token);
        await offered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => sending);
        await dismissed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(Directory.Exists(destination));
        Assert.Equal(SyncState.Cancelled, new SyncActivityStore(journalPath).Snapshot().Single().State);
        Assert.DoesNotContain("synthetic content", await File.ReadAllTextAsync(journalPath));
    }

    [Fact]
    public async Task Bidirectional_folder_batches_complete_and_keep_independent_receipts()
    {
        var source = Path.Combine(_root, "project");
        Directory.CreateDirectory(source);
        await File.WriteAllTextAsync(Path.Combine(source, "note.txt"), "fixture");
        var a = Path.Combine(_root, "a"); var b = Path.Combine(_root, "b");
        await using var host = new LanNode("fixture-A", () => a);
        await using var client = new LanNode("fixture-B", () => b);
        host.ConfirmPairAsync = (_, _) => Task.FromResult(true);
        host.ConfirmFilesAsync = (_, _) => Task.FromResult<string?>(a);
        client.ConfirmFilesAsync = (_, _) => Task.FromResult<string?>(b);
        await host.ListenAsync(IPAddress.Loopback, 0);
        await client.ConnectAsync(IPAddress.Loopback, host.Port, host.PairCode);
        var results = await Task.WhenAll(host.SendBundleAsync([source]), client.SendBundleAsync([source]))
            .WaitAsync(TimeSpan.FromSeconds(10));
        Assert.All(results, Assert.True);
        Assert.Single(Directory.GetFiles(a, "note.txt", SearchOption.AllDirectories));
        Assert.Single(Directory.GetFiles(b, "note.txt", SearchOption.AllDirectories));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Archive_links_and_case_collisions_are_rejected(bool link)
    {
        var zip = Path.Combine(_root, "bad.zip");
        using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create))
        {
            var entry = archive.CreateEntry("project/note.txt");
            if (link) entry.ExternalAttributes = unchecked((int)0xA1FF0000);
            else archive.CreateEntry("PROJECT/NOTE.TXT");
        }
        await Assert.ThrowsAsync<InvalidDataException>(() => LanBundle.ExtractAsync(zip, Path.Combine(_root, "target"), "batch", default));
        Assert.Empty(Directory.GetFileSystemEntries(Path.Combine(_root, "target")));
    }

    [Fact]
    public void Journal_storage_failure_is_visible_and_does_not_break_sync()
    {
        var blocker = Path.Combine(_root, "not-a-folder");
        File.WriteAllText(blocker, "fixture");
        var journal = new SyncActivityStore(Path.Combine(blocker, "activity.json"));
        journal.Update(new("fixture", SyncKind.Library, false, "fixture", SyncState.Completed));
        Assert.Contains("保存失败", journal.StorageWarning);
        Assert.Single(journal.Snapshot());
    }

    [Fact]
    public async Task Legacy_direct_file_cannot_bypass_receiver_confirmation()
    {
        var target = Path.Combine(_root, "received");
        await using var host = new LanNode("fixture-A", () => target) { Activities = new(Path.Combine(_root, "activity.json")) };
        await using var client = new LanNode("fixture-B", () => target);
        host.ConfirmPairAsync = (_, _) => Task.FromResult(true);
        host.ConfirmFilesAsync = (_, _) => throw new InvalidOperationException("legacy must not prompt or save");
        host.ApplyClipboardAsync = (_, _) => Task.CompletedTask;
        await host.ListenAsync(IPAddress.Loopback, 0);
        await client.ConnectAsync(IPAddress.Loopback, host.Port, host.PairCode);
        var source = Path.Combine(_root, "fixture.txt");
        await File.WriteAllTextAsync(source, "fixture");
        await Assert.ThrowsAsync<IOException>(() => client.SendFileAsync(source));
        Assert.False(Directory.Exists(target));
        Assert.True(await client.SendClipboardAsync(new("text", [1])));
        Assert.Equal(SyncIssue.Unsupported, host.Activities.Snapshot().Single(x => x.Kind == SyncKind.Files).Issue);
    }
}
