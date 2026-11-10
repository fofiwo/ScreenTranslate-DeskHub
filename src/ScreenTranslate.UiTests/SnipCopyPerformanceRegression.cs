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
        var source = BitmapSource.Create(3840, 2160, 96, 96, PixelFormats.Bgra32, null, new byte[3840 * 2160 * 4], 3840 * 4);
        source.Freeze();
        using var release = new ManualResetEventSlim();
        Task? pending = null;
        try
        {
            capture.Show();
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
        }
        finally
        {
            release.Set();
            if (pending is not null) { PumpUntil(() => pending.IsCompleted); pending.GetAwaiter().GetResult(); }
            capture.Close();
        }
    }
}
