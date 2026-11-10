using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using OverTranslate.Models;
using OverTranslate.Views.Capture;
using Xunit;

namespace OverTranslate.Tests;

public sealed partial class ScreenTranslateShellTests
{
    private static void VerifySnipCopyAdversarial()
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var bytes = new byte[96 * 64 * 4];
        for (var i = 0; i < bytes.Length; i += 4)
        {
            bytes[i] = (byte)(i % 255); bytes[i + 1] = (byte)(i / 96 % 255);
            bytes[i + 2] = 90; bytes[i + 3] = 255;
        }
        var desktop = BitmapSource.Create(96, 64, 96, 96, PixelFormats.Bgra32, null, bytes, 96 * 4);
        desktop.Freeze();
        var source = new CroppedBitmap(desktop, new Int32Rect(7, 5, 64, 48)); source.Freeze();
        ScreenCaptureWindow Capture()
        {
            var capture = new ScreenCaptureWindow(new System.Drawing.Bitmap(96, 64), new System.Drawing.Rectangle(0, 0, 96, 64), snippingTools: true);
            typeof(ScreenCaptureWindow).GetField("_snipSource", flags)!.SetValue(capture, source);
            typeof(ScreenCaptureWindow).GetField("_hasSelection", flags)!.SetValue(capture, true);
            return capture;
        }
        object? Field(ScreenCaptureWindow capture, string name) => typeof(ScreenCaptureWindow).GetField(name, flags)!.GetValue(capture);
        void Complete(Task task) { PumpUntil(() => task.IsCompleted); task.GetAwaiter().GetResult(); }
        byte[] Pixels(BitmapSource image)
        {
            var normalized = new FormatConvertedBitmap(image, PixelFormats.Bgra32, null, 0);
            var result = new byte[image.PixelWidth * image.PixelHeight * 4];
            normalized.CopyPixels(result, image.PixelWidth * 4, 0); return result;
        }

        foreach (var annotated in new[] { false, true })
        {
            var capture = Capture();
            try
            {
                var document = (SnipDocument)Field(capture, "_snipDocument")!;
                if (annotated)
                {
                    document.Add(new(SnipTool.Pen, Colors.Red, 3, [new(5, 12), new(50, 12)]));
                    document.Add(new(SnipTool.Text, Colors.Blue, 3, [new(1, 15)], "中A"));
                    document.Add(new(SnipTool.Mosaic, Colors.Red, 3, [new(40, 25), new(62, 45)]));
                }
                var expected = document.Render(source);
                BitmapSource? copied = null;
                Complete(capture.CopySnipAsync(image => copied = image));
                Assert.True((bool)Field(capture, "_snipClosed")!);
                Assert.NotNull(copied); Assert.True(copied.IsFrozen);
                Assert.Equal(source.PixelWidth, copied.PixelWidth);
                Assert.Equal(source.PixelHeight, copied.PixelHeight);
                Assert.Equal(Pixels(expected), Pixels(copied));
            }
            finally { capture.Close(); }
        }

        var cancelled = Capture();
        using var started = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var writes = 0;
        byte[]? cancelledPixels = null;
        Task? pending = null;
        try
        {
            pending = cancelled.CopySnipAsync(image =>
            {
                Interlocked.Increment(ref writes); started.Set();
                if (!release.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException("fixture release");
                cancelledPixels = Pixels(image);
                throw new ExternalException("busy after cancellation fixture", unchecked((int)0x800401D0));
            });
            PumpUntil(() => started.IsSet);
            cancelled.Close();
            var queued = Capture();
            try
            {
                var queuedWrites = 0;
                var queuedTask = queued.CopySnipAsync(_ => queuedWrites++);
                queued.Close(); Complete(queuedTask);
                Assert.Equal(0, queuedWrites);
                Assert.False(pending.IsCompleted, "排队任务取消时，不应等待仍在写入的旧截图。");
            }
            finally { queued.Close(); }
            release.Set(); Complete(pending);
            Assert.True((bool)Field(cancelled, "_snipClosed")!);
            Assert.False((bool)Field(cancelled, "_snipWriting")!);
            Assert.Equal(1, writes);
            Assert.Equal(Pixels(source), cancelledPixels);
        }
        finally
        {
            release.Set();
            if (pending is not null) Complete(pending);
            cancelled.Close();
        }
    }
}
