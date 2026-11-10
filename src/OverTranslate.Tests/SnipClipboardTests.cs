using System.Runtime.InteropServices;
using OverTranslate.Views.Capture;
using Xunit;

namespace OverTranslate.Tests;

public class SnipClipboardTests
{
    [Fact]
    public async Task QueuedCopyCanCancelBeforeTheActiveWriterFinishes()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        var active = SnipClipboard.RunStaAsync(() => SnipClipboard.WriteAsync(() =>
        {
            entered.SetResult();
            if (!release.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException("fixture write");
        }, CancellationToken.None));
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            using var cancellation = new CancellationTokenSource();
            var writes = 0;
            var queued = SnipClipboard.RunStaAsync(() => SnipClipboard.WriteAsync(() => writes++, cancellation.Token));
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => queued.WaitAsync(TimeSpan.FromSeconds(2)));
            Assert.Equal(0, writes);
            Assert.False(active.IsCompleted);
        }
        finally { release.Set(); await active; }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancelledInFlightCopyCannotOverwriteTheNextCopy(bool backgroundNext)
    {
        using var cancellation = new CancellationTokenSource();
        using var release = new ManualResetEventSlim();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var clipboard = 0;
        var first = SnipClipboard.RunStaAsync(() => SnipClipboard.WriteAsync(() =>
        {
            started.SetResult();
            if (!release.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException("fixture write");
            Volatile.Write(ref clipboard, 1);
        }, cancellation.Token));
        Task? second = null;
        try
        {
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            cancellation.Cancel();
            Task Next() => SnipClipboard.WriteAsync(() => Volatile.Write(ref clipboard, 2), CancellationToken.None);
            second = backgroundNext ? SnipClipboard.RunStaAsync(Next) : Next();
            await Task.WhenAny(second, Task.Delay(200));
            release.Set();
            await Task.WhenAll(first, second);
            Assert.Equal(2, Volatile.Read(ref clipboard));
        }
        finally
        {
            release.Set();
            await first;
            if (second is not null) await second;
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WorkerExitsAfterSuccessOrFailureWithoutAUiContext(bool fail)
    {
        for (var i = 0; i < 4; i++)
        {
            Thread? worker = null;
            Task Run() => Task.Run(() => SnipClipboard.RunStaAsync(async () =>
            {
                worker = Thread.CurrentThread;
                await Task.Delay(1);
                Assert.Same(worker, Thread.CurrentThread);
                if (fail) throw new InvalidOperationException("fixture");
            }));
            if (fail) await Assert.ThrowsAsync<InvalidOperationException>(Run);
            else await Run();
            Assert.NotNull(worker);
            Assert.False(worker.IsAlive, "完成或失败后不能留下消息泵线程。");
        }
    }

    [Fact]
    public async Task AlreadyCancelledClipboardNeverWrites()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var attempts = 0;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => SnipClipboard.RunStaAsync(() =>
            SnipClipboard.WriteAsync(() => attempts++, cancellation.Token)));
        Assert.Equal(0, attempts);
    }

    [Fact]
    public async Task BusyClipboardRetriesOnTheSameStaThread()
    {
        var attempts = 0;
        await SnipClipboard.RunStaAsync(async () =>
        {
            var thread = Environment.CurrentManagedThreadId;
            await SnipClipboard.WriteAsync(() =>
            {
                Assert.Equal(ApartmentState.STA, Thread.CurrentThread.GetApartmentState());
                Assert.Equal(thread, Environment.CurrentManagedThreadId);
                if (++attempts == 1) throw new ExternalException("busy fixture", unchecked((int)0x800401D0));
            }, CancellationToken.None);
        });
        Assert.Equal(2, attempts);
    }

    [Fact]
    public async Task WorkerFailureReachesTheCaller()
    {
        var error = new InvalidOperationException("fixture");
        Assert.Same(error, await Assert.ThrowsAsync<InvalidOperationException>(() =>
            SnipClipboard.RunStaAsync(() => SnipClipboard.WriteAsync(() => throw error, CancellationToken.None))));
        await SnipClipboard.RunStaAsync(() => SnipClipboard.WriteAsync(() => { }, CancellationToken.None)).WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task CancelDuringBusyClipboardStopsFurtherWrites()
    {
        using var cancellation = new CancellationTokenSource();
        var attempts = 0;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => SnipClipboard.RunStaAsync(() =>
            SnipClipboard.WriteAsync(() =>
            {
                attempts++;
                cancellation.Cancel();
                throw new ExternalException("busy fixture", unchecked((int)0x800401D0));
            }, cancellation.Token)));
        Assert.Equal(1, attempts);
    }
}
