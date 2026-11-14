using System.IO;
using System.Net;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using OverTranslate.Services.ClipboardHistory;
using OverTranslate.Services.Lan;
using Xunit;

namespace OverTranslate.Tests;

[Collection("ScreenTranslate shell")]
public sealed class LanClipboardCaptureTests
{
    private static void OnSta(Func<Task> run)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
            Dispatcher.CurrentDispatcher.BeginInvoke(async () =>
            {
                try { await run(); } catch (Exception ex) { failure = ex; }
                finally { Dispatcher.CurrentDispatcher.InvokeShutdown(); }
            });
            Dispatcher.Run();
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(30)), "剪贴板同步夹具超时");
        if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }

    private static async Task Until(Func<bool> ready)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        while (!ready()) await Task.Delay(10, deadline.Token);
    }

    private static DataObject ImageData()
    {
        var bitmap = BitmapSource.Create(1, 1, 96, 96, PixelFormats.Bgra32, null, new byte[] { 1, 2, 3, 255 }, 4);
        bitmap.Freeze(); var data = new DataObject(); data.SetImage(bitmap); return data;
    }

    [Fact]
    public void Delayed_clipboard_capture_and_rejected_receive_retry_without_another_copy() => OnSta(async () =>
    {
        await using var host = new LanNode("fixture-host", () => "");
        await using var client = new LanNode("fixture-client", () => "");
        host.ConfirmPairAsync = (_, _) => Task.FromResult(true);
        var applied = 0; var reads = 0;
        host.ApplyClipboardAsync = (_, _) => ++applied == 1
            ? Task.FromException(new ExternalException("fixture busy")) : Task.CompletedTask;
        await host.ListenAsync(IPAddress.Loopback, 0);
        await client.ConnectAsync(IPAddress.Loopback, host.Port, host.PairCode);
        using var sync = new LanClipboardSync(Dispatcher.CurrentDispatcher, client, capture: () =>
        {
            if (++reads <= 4) throw new ExternalException("fixture delayed render");
            return Task.FromResult<LanClipboardPayload?>(new("image", [1]));
        }, sequence: () => 1, listen: false);
        sync.NotifyClipboardChanged();
        await Until(() => applied == 2);
        Assert.True(reads >= 6); Assert.True(client.IsConnected);
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void New_copy_supersedes_image_still_being_encoded(bool oldCaptureFails) => OnSta(async () =>
    {
        await using var host = new LanNode("fixture-host", () => "");
        await using var client = new LanNode("fixture-client", () => "");
        host.ConfirmPairAsync = (_, _) => Task.FromResult(true);
        var received = new List<byte>();
        host.ApplyClipboardAsync = (p, _) => { received.Add(p.Bytes[0]); return Task.CompletedTask; };
        await host.ListenAsync(IPAddress.Loopback, 0);
        await client.ConnectAsync(IPAddress.Loopback, host.Port, host.PairCode);
        var release = new TaskCompletionSource<LanClipboardPayload?>();
        var reads = 0; uint sequence = 1;
        using var sync = new LanClipboardSync(Dispatcher.CurrentDispatcher, client,
            () => ++reads == 1 ? release.Task : Task.FromResult<LanClipboardPayload?>(new("image", [2])), () => sequence, listen: false);
        sync.NotifyClipboardChanged(); await Until(() => reads == 1);
        sequence++; sync.NotifyClipboardChanged();
        if (oldCaptureFails) release.SetException(new ExternalException("fixture old capture failed"));
        else release.SetResult(new("image", [1]));
        await Until(() => received.Count > 0);
        Assert.Equal(new byte[] { 2 }, received);
        sequence++; sync.NotifyClipboardChanged(); await Until(() => received.Count == 2);
        Assert.Equal(new byte[] { 2, 2 }, received);
    });

    [Fact]
    public void Rendering_advances_sequence_without_another_notification_and_latest_image_is_still_sent() => OnSta(async () =>
    {
        await using var host = new LanNode("fixture-host", () => "");
        await using var client = new LanNode("fixture-client", () => "");
        host.ConfirmPairAsync = (_, _) => Task.FromResult(true);
        var received = new List<byte>();
        host.ApplyClipboardAsync = (p, _) => { received.Add(p.Bytes[0]); return Task.CompletedTask; };
        await host.ListenAsync(IPAddress.Loopback, 0);
        await client.ConnectAsync(IPAddress.Loopback, host.Port, host.PairCode);
        var reads = 0; uint sequence = 1;
        using var sync = new LanClipboardSync(Dispatcher.CurrentDispatcher, client, () =>
        {
            reads++; if (reads == 1) sequence++;
            return Task.FromResult<LanClipboardPayload?>(new("image", [(byte)reads]));
        }, () => sequence, listen: false);
        sync.NotifyClipboardChanged(); await Until(() => received.Count > 0);
        Assert.Equal(new byte[] { 2 }, received);
    });

    [Fact]
    public void Pause_cancels_capture_retries() => OnSta(async () =>
    {
        await using var host = new LanNode("fixture-host", () => "");
        await using var client = new LanNode("fixture-client", () => "");
        host.ConfirmPairAsync = (_, _) => Task.FromResult(true);
        await host.ListenAsync(IPAddress.Loopback, 0);
        await client.ConnectAsync(IPAddress.Loopback, host.Port, host.PairCode);
        var reads = 0;
        using var sync = new LanClipboardSync(Dispatcher.CurrentDispatcher, client,
            () => { reads++; throw new ExternalException("fixture busy"); }, () => 1, listen: false);
        sync.NotifyClipboardChanged(); await Until(() => reads > 0);
        sync.Enabled = false; var pausedAt = reads;
        await Task.Delay(400); Assert.Equal(pausedAt, reads);
    });

    [Fact]
    public void Empty_snapshot_retries_but_privacy_exclusion_does_not() => OnSta(async () =>
    {
        await Assert.ThrowsAsync<ExternalException>(() => LanClipboardSync.CaptureAsync(null));
        await Assert.ThrowsAsync<ExternalException>(() => LanClipboardSync.CaptureAsync(new DataObject()));
        var excluded = ImageData(); excluded.SetData("ExcludeClipboardContentFromMonitorProcessing", 1);
        Assert.Null(await LanClipboardSync.CaptureAsync(excluded));
    });

    [Fact]
    public void Png_and_bitmap_with_file_metadata_sync_pixels_without_reading_files() => OnSta(async () =>
    {
        var bitmap = (BitmapSource)ImageData().GetData(DataFormats.Bitmap);
        var png = new DataObject(); png.SetData("PNG", new MemoryStream(ClipboardContent.EncodeImage(bitmap)));
        var mixed = ImageData(); mixed.SetData(DataFormats.FileDrop, new[] { @"C:\fixture-must-not-be-opened.png" });
        foreach (var data in new[] { png, mixed })
        {
            var payload = await LanClipboardSync.CaptureAsync(data);
            Assert.NotNull(payload); Assert.Equal("image", payload.Kind);
            var image = LanClipboardSync.DecodeImage(payload.Bytes);
            var pixels = new byte[4]; image.CopyPixels(pixels, 4, 0);
            Assert.Equal(new byte[] { 1, 2, 3, 255 }, pixels);
        }
    });

    [Fact]
    public void Pending_copy_survives_reconnection_to_the_same_authenticated_peer() => OnSta(async () =>
    {
        await using var host = new LanNode("fixture-host", () => "");
        await using var client = new LanNode("fixture-client", () => "");
        host.ConfirmPairAsync = (_, _) => Task.FromResult(true);
        var received = new List<byte>();
        host.ApplyClipboardAsync = (p, _) => { received.Add(p.Bytes[0]); return Task.CompletedTask; };
        await host.ListenAsync(IPAddress.Loopback, 0);
        await client.ConnectAsync(IPAddress.Loopback, host.Port, host.PairCode);
        var key = client.ClipboardPeerKey;
        var reads = 0; var release = new TaskCompletionSource<LanClipboardPayload?>();
        using var sync = new LanClipboardSync(Dispatcher.CurrentDispatcher, client,
            () => { reads++; return reads == 1 ? release.Task : Task.FromResult<LanClipboardPayload?>(new("image", [1])); }, () => 1, listen: false);
        sync.NotifyClipboardChanged(); await Until(() => reads == 1);
        await client.StopAsync(); await Until(() => !host.IsConnected);
        release.SetResult(new("image", [1]));
        await Task.Delay(150);
        await client.ConnectAsync(IPAddress.Loopback, host.Port, host.PairCode);
        Assert.Equal(key, client.ClipboardPeerKey);
        await Until(() => received.Count > 0);
        Assert.Equal(new byte[] { 1 }, received);
    });

    [Fact]
    public void Unanswered_copy_recovers_after_timeout_and_reconnect_without_another_copy() => OnSta(async () =>
    {
        await using var host = new LanNode("fixture-host", () => "");
        await using var client = new LanNode("fixture-client", () => "");
        host.ConfirmPairAsync = (_, _) => Task.FromResult(true);
        var calls = 0;
        host.ApplyClipboardAsync = async (_, token) =>
        { if (Interlocked.Increment(ref calls) == 1) await Task.Delay(Timeout.InfiniteTimeSpan, token); };
        await host.ListenAsync(IPAddress.Loopback, 0);
        await client.ConnectAsync(IPAddress.Loopback, host.Port, host.PairCode);
        using var sync = new LanClipboardSync(Dispatcher.CurrentDispatcher, client,
            () => Task.FromResult<LanClipboardPayload?>(new("image", [1])), () => 1, listen: false);
        var success = false;
        sync.Notice += (_, message) => { if (message == "新复制的图片已同步。") success = true; };
        sync.NotifyClipboardChanged(); await Until(() => calls == 1);
        await Until(() => !client.IsConnected && !host.IsConnected);
        await client.ConnectAsync(IPAddress.Loopback, host.Port, host.PairCode);
        await Until(() => success); Assert.Equal(2, calls);
    });

    [Fact]
    public void Copy_prepared_for_previous_pairing_never_follows_a_different_peer() => OnSta(async () =>
    {
        await using var host = new LanNode("fixture-original", () => "");
        await using var other = new LanNode("fixture-other", () => "");
        await using var client = new LanNode("fixture-client", () => "");
        host.ConfirmPairAsync = other.ConfirmPairAsync = (_, _) => Task.FromResult(true);
        var received = new List<byte>();
        other.ApplyClipboardAsync = (p, _) => { received.Add(p.Bytes[0]); return Task.CompletedTask; };
        await host.ListenAsync(IPAddress.Loopback, 0); await other.ListenAsync(IPAddress.Loopback, 0);
        await client.ConnectAsync(IPAddress.Loopback, host.Port, host.PairCode);
        var reads = 0; uint sequence = 1; var cancelled = false;
        var release = new TaskCompletionSource<LanClipboardPayload?>();
        using var sync = new LanClipboardSync(Dispatcher.CurrentDispatcher, client,
            () => ++reads == 1 ? release.Task : Task.FromResult<LanClipboardPayload?>(new("image", [2])), () => sequence, listen: false);
        sync.Notice += (_, message) => { if (message.Contains("接收设备已变化")) cancelled = true; };
        sync.NotifyClipboardChanged(); await Until(() => reads == 1);
        await client.ConnectAsync(IPAddress.Loopback, other.Port, other.PairCode);
        release.SetResult(new("image", [1])); await Until(() => cancelled);
        Assert.Empty(received);
        sequence++; sync.NotifyClipboardChanged(); await Until(() => received.Count > 0);
        Assert.Equal(new byte[] { 2 }, received);
    });

    [Fact]
    public void Temporary_null_bitmap_is_retriable_and_file_only_content_stays_local() => OnSta(async () =>
    {
        var delayed = new DataObject(); delayed.SetData(DataFormats.Bitmap, new object());
        await Assert.ThrowsAsync<ExternalException>(() => LanClipboardSync.CaptureAsync(delayed));
        var files = new DataObject(); files.SetData(DataFormats.FileDrop, new[] { @"C:\fixture-never-read.png" });
        Assert.Null(await LanClipboardSync.CaptureAsync(files));
    });

    [Fact]
    public void Retry_budget_expires_and_does_not_keep_replaying_a_blocked_copy() => OnSta(async () =>
    {
        await using var host = new LanNode("fixture-host", () => "");
        await using var client = new LanNode("fixture-client", () => "");
        host.ConfirmPairAsync = (_, _) => Task.FromResult(true);
        await host.ListenAsync(IPAddress.Loopback, 0);
        await client.ConnectAsync(IPAddress.Loopback, host.Port, host.PairCode);
        var reads = 0; var ended = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var sync = new LanClipboardSync(Dispatcher.CurrentDispatcher, client,
            () => { reads++; throw new ExternalException("fixture stays busy"); }, () => 1, listen: false);
        sync.Notice += (_, message) => { if (message.Contains("自动重试已结束")) ended.TrySetResult(); };
        sync.NotifyClipboardChanged(); await ended.Task.WaitAsync(TimeSpan.FromSeconds(24));
        var finalReads = reads;
        Assert.True(finalReads > 4); await Task.Delay(250); Assert.Equal(finalReads, reads);
        Assert.True(client.IsConnected);
    });
}
