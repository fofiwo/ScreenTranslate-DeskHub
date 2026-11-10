using System.Drawing;
using OverTranslate.Services.Realtime.Capture;
using Xunit;

namespace OverTranslate.Tests;

/// <summary>
/// Ending a session must not wait for the capture to let go — 2.7.0 on Windows 10 is what that wait
/// cost. Which thread the capture objects are closed on is the backend's own business now: each
/// closes them on its frame thread, so the release only has to keep the wait off the caller.
/// </summary>
public class RealtimeCaptureReleaseTests
{
    [Fact]
    public async Task TheReleaseDoesNotRunOnTheCallersThread()
    {
        var capture = new FakeCapture(() => { });
        var callerThread = 0;
        // 独立调用线程不会在 await 后被线程池复用为释放线程。
        var watch = Task.Factory.StartNew(() =>
        {
            callerThread = Environment.CurrentManagedThreadId;
            return RealtimeCaptureRelease.Release(capture);
        }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default).Unwrap();

        await watch.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(capture.Started.Task.IsCompletedSuccessfully);
        Assert.NotEqual(callerThread, capture.DisposedOnThread);
    }

    [Fact]
    public async Task AReleaseThatNeverReturnsDoesNotHoldTheCaller()
    {
        using var never = new ManualResetEventSlim(false);
        var capture = new FakeCapture(() => never.Wait());

        var watch = RealtimeCaptureRelease.Release(
            capture, release => Task.Run(release), TimeSpan.FromMilliseconds(50));

        // Returned at once; the release is still running elsewhere.
        try
        {
            Assert.False(watch.IsCompleted);
            await capture.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally { never.Set(); await watch.WaitAsync(TimeSpan.FromSeconds(5)); }
    }

    [Fact]
    public async Task AFailingReleaseIsContained()
    {
        var capture = new FakeCapture(() => throw new InvalidOperationException("device lost"));

        var watch = RealtimeCaptureRelease.Release(capture);

        await watch.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(capture.Started.Task.IsCompletedSuccessfully);
    }

    [Fact]
    public async Task NothingToReleaseIsDoneAtOnce()
    {
        await RealtimeCaptureRelease.Release(null, _ => throw new InvalidOperationException(), TimeSpan.FromSeconds(5));
    }

    private sealed class FakeCapture(Action onDispose) : IRealtimeCaptureBackend
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int DisposedOnThread { get; private set; }

        public string Name => "Fake";

        public Bitmap? GrabRegion(Rectangle screenBounds) => null;
        public CaptureMiss DescribeMiss(Rectangle screenBounds) => CaptureMiss.Waiting;

        public string DescribeActivity() => "fake";

        public string DescribeSteps() => "grab=[idle]";

        public void Dispose()
        {
            DisposedOnThread = Environment.CurrentManagedThreadId;
            Started.SetResult();
            onDispose();
        }
    }
}
