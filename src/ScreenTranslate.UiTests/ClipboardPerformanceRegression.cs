using System.Diagnostics;
using System.Reflection;
using System.Windows.Controls;
using OverTranslate.Services.ClipboardHistory;
using OverTranslate.Views.ClipboardHistory;
using Xunit;

namespace OverTranslate.Tests;

public sealed partial class ScreenTranslateShellTests
{
    private static void VerifyClipboardRapidToggle()
    {
        var folder = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "clipboard-toggle-" + Guid.NewGuid());
        var store = new ClipboardHistoryStore(folder);
        var records = (Dictionary<string, ClipboardRecord>)typeof(ClipboardHistoryStore)
            .GetField("_records", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(store)!;
        for (var i = 0; i < 10000; i++)
            records.Add(i.ToString("x64"), new() { Id = i.ToString("x64"), Text = "toggle fixture " + i });
        var copies = 0;
        var window = new ClipboardHistoryWindow(store, _ => { copies++; return Task.CompletedTask; },
            settings: new(), saveSettings: () => true,
            syncActivities: new OverTranslate.Services.Lan.SyncActivityStore(System.IO.Path.Combine(folder, "activity.json")))
        { Left = -10000, Top = -10000, ShowActivated = false };
        window.SetWindowPinned(true);
        try
        {
            window.ShowPanel(); PumpUntil(() => !window.IsRefreshing); window.UpdateLayout();
            var list = (ListBox)window.FindName("HistoryList");
            var source = list.ItemsSource;
            var notifications = 0;
            foreach (ClipboardRecordView view in list.Items)
                view.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(ClipboardRecordView.TimeLabel)) notifications++; };
            var watch = Stopwatch.StartNew();
            for (var i = 0; i < 20; i++)
            {
                window.Dismiss(); window.ShowPanel(); window.UpdateLayout();
                Assert.True(list.IsEnabled, "无变化的历史重新打开后应能立即浏览和复制");
                Assert.Same(source, list.ItemsSource);
            }
            watch.Stop();
            System.IO.Directory.CreateDirectory("artifacts");
            System.IO.File.WriteAllText("artifacts/clipboard-toggle-performance.txt",
                $"Synthetic records=10000; reopenCount=20; elapsed={watch.ElapsedMilliseconds}ms; notifications={notifications}");
            Assert.InRange(notifications, 1, 999);
            var copy = window.CopySelectedAsync();
            Assert.Equal(1, copies);
            PumpUntil(() => copy.IsCompleted); copy.GetAwaiter().GetResult();
            window.ShowPanel(); PumpUntil(() => !window.IsRefreshing); window.UpdateLayout();
            var scroll = Visuals<ScrollViewer>(list).First();
            scroll.ScrollToVerticalOffset(50); window.UpdateLayout();
            Assert.True(scroll.VerticalOffset > 0);
            var gate = typeof(ClipboardHistoryStore).GetField("_gate", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(store)!;
            using var entered = new ManualResetEventSlim();
            using var release = new ManualResetEventSlim();
            var writer = Task.Run(() => { lock (gate) { entered.Set(); release.Wait(TimeSpan.FromSeconds(5)); } });
            Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
            Task? firstCopy = null, secondCopy = null;
            string? latestId = null;
            try
            {
                firstCopy = window.CopySelectedAsync();
                Assert.Equal(2, copies);
                window.ShowPanel();
                Assert.False(window.IsBusy, "复制成功后的历史写盘不能阻止再次打开并复制");
                list.SelectedIndex = (list.SelectedIndex + 1) % list.Items.Count;
                latestId = window.SelectedRecord!.Id;
                secondCopy = window.CopySelectedAsync();
                Assert.Equal(3, copies);
            }
            finally
            {
                release.Set(); writer.GetAwaiter().GetResult();
                foreach (var pending in new[] { firstCopy, secondCopy }.OfType<Task>())
                { PumpUntil(() => pending.IsCompleted); pending.GetAwaiter().GetResult(); }
            }
            window.ShowPanel(); PumpUntil(() => !window.IsRefreshing);
            Assert.Equal(latestId, window.SelectedRecord!.Id);
        }
        finally
        {
            window.Close();
            if (System.IO.Directory.Exists(folder)) System.IO.Directory.Delete(folder, true);
        }
    }

    private static void VerifyClipboardPerformance()
    {
        VerifyClipboardRapidToggle();
        VerifyClipboardStoreOpenRecovery();
        VerifyBackgroundClipboardImageCapture();
        VerifySlowCaptureCoalescesAndDrainsLatest();
        var store = new ClipboardHistoryStore(System.IO.Path.Combine(System.IO.Path.GetTempPath(), Guid.NewGuid().ToString("N")));
        // 仅注入虚构内存记录，避免性能夹具读取个人历史或产生万次磁盘写入。
        var records = (Dictionary<string, ClipboardRecord>)typeof(ClipboardHistoryStore)
            .GetField("_records", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(store)!;
        for (var i = 0; i < 10000; i++)
            records.Add(i.ToString(), new() { Id = i.ToString(), Text = "fixture " + i + new string('x', 2048) });
        var copyCount = 0;
        var window = new ClipboardHistoryWindow(store, _ => { copyCount++; return Task.CompletedTask; });
        try
        {
            var load = Stopwatch.StartNew();
            window.RefreshViews();
            var list = (ListBox)window.FindName("HistoryList");
            PumpUntil(() => list.Items.Count == 10000);
            load.Stop();
            var original = list.ItemsSource;
            window.RefreshViews();
            PumpUntil(() => !window.IsRefreshing);
            Assert.True(ReferenceEquals(original, list.ItemsSource), "未变化的列表不应重建");

            window.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);

            var gate = typeof(ClipboardHistoryStore).GetField("_gate", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(store)!;
            using var entered = new ManualResetEventSlim();
            var writer = Task.Run(() => { lock (gate) { entered.Set(); Thread.Sleep(500); } });
            entered.Wait();
            var watch = Stopwatch.StartNew();
            window.RefreshViews();
            var inputHandled = false;
            // 在输入真正得到响应时计时；消息泵随后等待 Background 定时器的时间不属于输入延迟。
            window.Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Input, () => { watch.Stop(); inputHandled = true; });
            PumpUntil(() => inputHandled);
            Assert.True(watch.ElapsedMilliseconds < 150, $"刷新阻塞 UI {watch.ElapsedMilliseconds} ms");
            writer.GetAwaiter().GetResult();
            PumpUntil(() => !window.IsRefreshing);
            var query = (TextBox)window.FindName("SearchBox");
            query.Text = "fixture 123x";
            query.Text = "fixture 9876x";
            var staleCopy = window.CopySelectedAsync();
            Assert.True(staleCopy.IsCompletedSuccessfully);
            Assert.Equal(0, copyCount);
            var search = Stopwatch.StartNew();
            PumpUntil(() => !window.IsRefreshing);
            search.Stop();
            System.IO.Directory.CreateDirectory("artifacts");
            System.IO.File.WriteAllText("artifacts/clipboard-performance.txt",
                $"Synthetic records=10000, charsPerRecord~2060; initialRefresh={load.ElapsedMilliseconds}ms; inputWhileStoreLocked={watch.ElapsedMilliseconds}ms; latestSearch={search.ElapsedMilliseconds}ms (includes 100ms debounce)");
            Assert.Equal("9876", Assert.IsType<ClipboardRecordView>(Assert.Single(list.Items.Cast<object>())).Record.Id);
        }
        finally { window.Close(); }
    }

    private static void VerifyClipboardStoreOpenRecovery()
    {
        var root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "clipboard-open-recovery-" + Guid.NewGuid());
        var attempts = 0;
        var loader = new ClipboardHistoryStoreLoader(() =>
        {
            if (Interlocked.Increment(ref attempts) == 1) throw new System.IO.IOException("fixture temporary folder failure");
            return new ClipboardHistoryStore(root);
        });
        var ready = loader.OpenAsync();
        PumpUntil(() => ready.IsCompleted);
        Assert.Throws<System.IO.IOException>(() => ready.GetAwaiter().GetResult());
        using var runtime = new ClipboardHistoryRuntime(System.Windows.Threading.Dispatcher.CurrentDispatcher,
            ready, capture: () => new(new() { Text = "fixture after folder recovery" }, null),
            sequence: () => 1, listen: false, openStoreAsync: loader.OpenAsync);
        try
        {
            runtime.TrackCapture();
            var drain = runtime.StopAndDrainAsync();
            PumpUntil(() => drain.IsCompleted);
            drain.GetAwaiter().GetResult();
            var reopened = loader.OpenAsync().GetAwaiter().GetResult();
            Assert.Equal("fixture after folder recovery", Assert.Single(reopened.Read()).Text);
            Assert.Equal(2, attempts);
        }
        finally { if (System.IO.Directory.Exists(root)) System.IO.Directory.Delete(root, true); }
    }

    private static void VerifyBackgroundClipboardImageCapture()
    {
        const int width = 2048, height = 2048, stride = width * 4;
        var pixels = new byte[stride * height];
        new Random(42).NextBytes(pixels);
        var bitmap = System.Windows.Media.Imaging.BitmapSource.Create(width, height, 96, 96,
            System.Windows.Media.PixelFormats.Bgra32, null, pixels, stride);
        var data = new System.Windows.DataObject(); data.SetImage(bitmap); data.SetText("image fixture");
        // 清空前面其它窗口留下的布局队列，避免把无关 Render 优先级任务归因于本次压缩。
        System.Windows.Threading.Dispatcher.CurrentDispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        var watch = Stopwatch.StartNew();
        var capture = ClipboardContent.CaptureAsync(data, "fixture");
        var preparationMs = watch.ElapsedMilliseconds;
        var handled = false;
        System.Windows.Threading.Dispatcher.CurrentDispatcher.BeginInvoke(
            System.Windows.Threading.DispatcherPriority.Input, () => { watch.Stop(); handled = true; });
        PumpUntil(() => handled);
        Assert.True(watch.ElapsedMilliseconds < 150, $"图片压缩阻塞 UI {watch.ElapsedMilliseconds} ms（同步读取准备 {preparationMs} ms）");
        PumpUntil(() => capture.IsCompleted);
        var result = capture.GetAwaiter().GetResult()!;
        Assert.Equal("image fixture", result.Record.Text);
        var decode = Task.Run(() => OverTranslate.Services.Lan.LanClipboardSync.DecodeImage(result.Png!));
        PumpUntil(() => decode.IsCompleted);
        var decoded = decode.GetAwaiter().GetResult();
        Assert.True(decoded.IsFrozen);
        Assert.Equal(width, decoded.PixelWidth); Assert.Equal(height, decoded.PixelHeight);
        var restored = new byte[pixels.Length]; decoded.CopyPixels(restored, stride, 0);
        Assert.Equal(pixels, restored);
        var excluded = new System.Windows.DataObject(); excluded.SetImage(bitmap);
        excluded.SetData("ExcludeClipboardContentFromMonitorProcessing", 1);
        Assert.Null(ClipboardContent.CaptureAsync(excluded, "fixture").GetAwaiter().GetResult());
    }

    private static void VerifySlowCaptureCoalescesAndDrainsLatest()
    {
        var root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "capture-burst-" + Guid.NewGuid());
        var store = new ClipboardHistoryStore(root);
        var release = new TaskCompletionSource<CapturedClipboard?>();
        var reads = 0; uint sequence = 1;
        using var runtime = new ClipboardHistoryRuntime(System.Windows.Threading.Dispatcher.CurrentDispatcher,
            Task.FromResult(store), sequence: () => sequence, listen: false, captureAsync: () =>
            ++reads == 1 ? release.Task : Task.FromResult<CapturedClipboard?>(new(new() { Text = "latest fixture" }, null)));
        try
        {
            runtime.TrackCapture();
            for (var i = 0; i < 100; i++) { sequence++; runtime.TrackCapture(); }
            Assert.Equal(1, reads);
            var stopping = runtime.StopAndDrainAsync();
            Assert.False(stopping.IsCompleted);
            release.SetResult(new(new() { Text = "stale fixture" }, null));
            PumpUntil(() => stopping.IsCompleted); stopping.GetAwaiter().GetResult();
            Assert.Equal(2, reads);
            Assert.Equal("latest fixture", Assert.Single(store.Read()).Text);
        }
        finally { if (System.IO.Directory.Exists(root)) System.IO.Directory.Delete(root, true); }
    }
}
