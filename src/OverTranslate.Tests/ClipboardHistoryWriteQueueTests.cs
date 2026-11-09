using OverTranslate.Services.ClipboardHistory;
using Xunit;

namespace OverTranslate.Tests;

public sealed class ClipboardHistoryWriteQueueTests
{
    [Fact]
    public async Task SlowDiskPreservesAlreadyCapturedSnapshotsAndDrainWaitsForAll()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var saved = new List<string>();
        var queue = new ClipboardHistoryWriteQueue(async captured =>
        {
            if (captured.Record.Text == "A") { started.SetResult(); await release.Task; }
            saved.Add(captured.Record.Text);
        }, _ => { });
        queue.Enqueue(new(new() { Text = "A" }, null));
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        queue.Enqueue(new(new() { Text = "B" }, null));
        queue.Enqueue(new(new() { Text = "C" }, null));
        var drain = queue.DrainAsync();
        Assert.False(drain.IsCompleted);
        release.SetResult();
        await drain.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(new[] { "A", "B", "C" }, saved);
    }

    [Fact]
    public async Task FailedSaveIsReportedAndDoesNotBlockLaterSnapshots()
    {
        var saved = new List<string>(); var errors = new List<Exception?>();
        var queue = new ClipboardHistoryWriteQueue(captured =>
        {
            if (captured.Record.Text == "failure") throw new IOException("fixture disk full");
            saved.Add(captured.Record.Text); return Task.CompletedTask;
        }, errors.Add);
        queue.Enqueue(new(new() { Text = "failure" }, null));
        queue.Enqueue(new(new() { Text = "next" }, null));
        await queue.DrainAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(new[] { "next" }, saved);
        Assert.IsType<IOException>(errors[0]); Assert.Null(errors[1]);
    }
}
