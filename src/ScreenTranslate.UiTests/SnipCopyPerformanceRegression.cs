using System.Diagnostics;
using System.Reflection;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using OverTranslate.Views.Capture;
using Xunit;

namespace OverTranslate.Tests;

public sealed partial class ScreenTranslateShellTests
{
    private static void VerifySnipCopyResponsiveness()
    {
        using var pixels = new System.Drawing.Bitmap(32, 24);
        var capture = new ScreenCaptureWindow((System.Drawing.Bitmap)pixels.Clone(), new System.Drawing.Rectangle(0, 0, 32, 24), snippingTools: true);
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var host = new OverTranslate.Views.MainWindow();
        var hostType = host.GetType();
        var refreshHooks = hostType.GetMethod("RefreshScreenshotHooks", flags)!;
        object? HostField(string name) => hostType.GetField(name, flags)!.GetValue(host);
        hostType.GetField("_snipCapture", flags)!.SetValue(host, capture);
        capture.IsVisibleChanged += (_, _) => refreshHooks.Invoke(host, null);
        var source = BitmapSource.Create(3840, 2160, 96, 96, PixelFormats.Bgra32, null, new byte[3840 * 2160 * 4], 3840 * 4);
        source.Freeze();
        using var release = new ManualResetEventSlim();
        Task? pending = null;
        try
        {
            capture.Show();
            Assert.NotNull(HostField("_snipEscape"));
            Assert.NotNull(HostField("_snipRecovery"));
            typeof(ScreenCaptureWindow).GetField("_snipSource", flags)!.SetValue(capture, source);
            typeof(ScreenCaptureWindow).GetField("_hasSelection", flags)!.SetValue(capture, true);
            typeof(ScreenCaptureWindow).GetField("_selectionWpfRect", flags)!.SetValue(capture, new Rect(0, 0, 32, 24));
            var uiThread = Environment.CurrentManagedThreadId;
            var writeThread = uiThread;
            var apartment = ApartmentState.Unknown;
            BitmapSource? copied = null;
            var watch = Stopwatch.StartNew();
            var copy = capture.CopySnipOnDoubleClickAsync(new Point(10, 10), null, image =>
            {
                writeThread = Environment.CurrentManagedThreadId;
                apartment = Thread.CurrentThread.GetApartmentState();
                Thread.Sleep(250); // 模拟系统剪贴板或监听程序缓慢，不改写用户剪贴板。
                if (!release.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException("fixture heartbeat");
                copied = image;
            });
            pending = copy;
            var blocked = watch.ElapsedMilliseconds;
            var duplicateWrites = 0;
            var duplicate = capture.CopySnipOnDoubleClickAsync(new Point(10, 10), null, _ => duplicateWrites++);
            Assert.True(duplicate.IsCompletedSuccessfully);
            Assert.False(duplicate.GetAwaiter().GetResult()); Assert.Equal(0, duplicateWrites);
            Assert.False(capture.IsVisible, "双击确认后应立即隐藏截图界面，不等待慢速剪贴板写入。");
            Assert.Null(HostField("_snipEscape"));
            Assert.Null(HostField("_snipRecovery"));
            Assert.False((bool)typeof(ScreenCaptureWindow).GetField("_snipClosed", flags)!.GetValue(capture)!,
                "后台复制完成前保留窗口和选区，避免关闭时取消写入。");
            var heartbeat = false;
            var completedAtHeartbeat = true;
            // 队列先繁忙 350ms，不能把稍后已完成的后台任务误判成 UI 同步复制。
            capture.Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Send, new Action(() => Thread.Sleep(350)));
            capture.Dispatcher.BeginInvoke(() =>
            {
                completedAtHeartbeat = copy.IsCompleted;
                heartbeat = true; release.Set();
            });
            PumpUntil(() => heartbeat);
            PumpUntil(() => copy.IsCompleted);
            Assert.True(copy.GetAwaiter().GetResult());
            Console.WriteLine($"snip-copy: UI blocked={blocked}ms, total={watch.ElapsedMilliseconds}ms");
            Assert.True(blocked < 150, $"Double-click blocked the UI for {blocked}ms.");
            Assert.False(completedAtHeartbeat, "The dispatcher must respond while the clipboard is busy.");
            Assert.NotEqual(uiThread, writeThread);
            Assert.Equal(ApartmentState.STA, apartment);
            Assert.NotNull(copied);
            Assert.Same(source, copied);
            Assert.True(copied.IsFrozen);
            Assert.Equal(3840, copied.PixelWidth);
            Assert.Equal(2160, copied.PixelHeight);
            Assert.True((bool)typeof(ScreenCaptureWindow).GetField("_snipClosed", flags)!.GetValue(capture)!);

            // 恢复显示的截图重新取得 Esc 与系统恢复钩子，下一次隐藏仍须释放。
            var restored = new ScreenCaptureWindow(new System.Drawing.Bitmap(32, 24), new System.Drawing.Rectangle(0, 0, 32, 24), snippingTools: true);
            hostType.GetField("_snipCapture", flags)!.SetValue(host, restored);
            restored.IsVisibleChanged += (_, _) => refreshHooks.Invoke(host, null);
            try
            {
                restored.Show();
                Assert.NotNull(HostField("_snipEscape"));
                restored.Hide();
                Assert.Null(HostField("_snipEscape"));
                restored.Show();
                Assert.NotNull(HostField("_snipEscape"));
                Assert.NotNull(HostField("_snipRecovery"));
            }
            finally { restored.Close(); }
        }
        finally
        {
            release.Set();
            if (pending is not null) { PumpUntil(() => pending.IsCompleted); pending.GetAwaiter().GetResult(); }
            capture.Close();
            hostType.GetField("_exitingApplication", flags)!.SetValue(host, true);
            host.Close();
        }
    }
}
