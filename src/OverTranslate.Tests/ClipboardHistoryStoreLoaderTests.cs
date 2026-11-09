using OverTranslate.Services.ClipboardHistory;
using Xunit;

namespace OverTranslate.Tests;

public sealed class ClipboardHistoryStoreLoaderTests
{
    [Fact]
    public async Task TransientOpenFailureCanRecoverWithoutRestartAndKeepsSuccessfulStore()
    {
        var attempts = 0;
        var folder = Path.Combine(Path.GetTempPath(), "clipboard-open-" + Guid.NewGuid());
        var loader = new ClipboardHistoryStoreLoader(() =>
        {
            if (Interlocked.Increment(ref attempts) == 1) throw new IOException("fixture temporary folder failure");
            return new ClipboardHistoryStore(folder);
        });

        await Assert.ThrowsAsync<IOException>(() => loader.OpenAsync());
        var recovered = await loader.OpenAsync();
        Assert.Same(recovered, await loader.OpenAsync());
        Assert.Equal(2, attempts);
    }

    [Fact]
    public async Task ConcurrentReadersSharePendingOpenAndSuccessfulInstance()
    {
        var attempts = 0;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        var folder = Path.Combine(Path.GetTempPath(), "clipboard-open-" + Guid.NewGuid());
        var loader = new ClipboardHistoryStoreLoader(() =>
        {
            Interlocked.Increment(ref attempts);
            entered.SetResult();
            if (!release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("fixture release timed out");
            return new ClipboardHistoryStore(folder);
        });

        var first = loader.OpenAsync();
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var readers = await Task.WhenAll(Enumerable.Range(0, 32).Select(_ => Task.Run(() =>
            {
                var pending = loader.OpenAsync();
                Assert.Same(first, pending);
                return true;
            })));
            Assert.All(readers, Assert.True);
        }
        finally { release.Set(); }
        var store = await first;
        Assert.Same(store, await loader.OpenAsync());
        Assert.Equal(1, attempts);
    }
}
