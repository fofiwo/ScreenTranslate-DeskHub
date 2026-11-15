using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using OverTranslate.Views.Capture;
using OverTranslate.Services;
using Xunit;

namespace OverTranslate.Tests;

public sealed partial class ScreenTranslateShellTests
{
    private static void VerifySnipImagePublication()
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        const int width = 3840, height = 2160;
        var pixels = new byte[width * height * 4];
        new Random(17).NextBytes(pixels);
        for (var i = 3; i < pixels.Length; i += 4) pixels[i] = 255;
        var samples = new List<double>();
        using var reader = new HookThread("Screenshot paste regression");
        reader.Start();
        HwndSource? listener = null;
        TaskCompletionSource<double>? pasted = null;
        Stopwatch? pasteTimer = null;
        byte marker = 0;
        var reading = false;
        async void ReadPublishedImage()
        {
            if (reading || pasted is null || pasted.Task.IsCompleted) return;
            reading = true;
            try
            {
                for (var attempt = 0; attempt < 50 && !pasted.Task.IsCompleted; attempt++)
                {
                    try
                    {
                        BitmapSource? actual;
                        if (!SnipOpenClipboard(IntPtr.Zero)) throw new COMException("paste reader busy", unchecked((int)0x800401D0));
                        try
                        {
                            var bitmap = SnipGetClipboardData(2);
                            actual = bitmap == IntPtr.Zero ? null : System.Windows.Interop.Imaging.CreateBitmapSourceFromHBitmap(bitmap, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                            actual?.Freeze();
                        }
                        finally { SnipCloseClipboard(); }
                        if (actual?.PixelWidth == width && actual.PixelHeight == height)
                        {
                            var first = new byte[4];
                            new FormatConvertedBitmap(actual, PixelFormats.Bgr32, null, 0).CopyPixels(new Int32Rect(0, 0, 1, 1), first, 4, 0);
                            if (first[0] == marker) { pasted.TrySetResult(pasteTimer!.Elapsed.TotalMilliseconds); return; }
                        }
                    }
                    catch (ExternalException) { }
                    await Task.Delay(10);
                }
                pasted.TrySetException(new TimeoutException("未读到本次合成截图。"));
            }
            catch (Exception ex) { pasted.TrySetException(ex); }
            finally { reading = false; }
        }
        reader.Invoke(() =>
        {
            listener = new HwndSource(new HwndSourceParameters("Screenshot paste test") { ParentWindow = new IntPtr(-3), WindowStyle = 0 });
            listener.AddHook((IntPtr window, int message, IntPtr wParam, IntPtr lParam, ref bool handled) =>
            {
                if (message == 0x031D) listener.Dispatcher.BeginInvoke(new Action(ReadPublishedImage));
                return IntPtr.Zero;
            });
            Assert.True(SnipAddClipboardFormatListener(listener.Handle));
        });
        try
        {
            for (var i = 0; i < 6; i++)
            {
                pixels[0] = (byte)(i + 1);
                var image = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, width * 4);
                image.Freeze();
                var capture = new ScreenCaptureWindow(new System.Drawing.Bitmap(32, 24), new System.Drawing.Rectangle(0, 0, 32, 24), snippingTools: true);
                try
                {
                    capture.Show();
                    typeof(ScreenCaptureWindow).GetField("_snipSource", flags)!.SetValue(capture, image);
                    // 模拟框选停顿，避免连续发布 4K 图片把外部监听程序的上一轮处理算进下一次截图。
                    var selectionPause = Task.Delay(1500);
                    PumpUntil(() => selectionPause.IsCompleted);
                    var timer = new Stopwatch();
                    var received = new TaskCompletionSource<double>(TaskCreationOptions.RunContinuationsAsynchronously);
                    reader.Invoke(() => { pasted = received; marker = pixels[0]; pasteTimer = timer; });
                    timer.Start();
                    var copy = capture.CopySnipAsync();
                    Assert.False(capture.IsVisible);
                    PumpUntil(() => copy.IsCompleted);
                    copy.GetAwaiter().GetResult();
                    var elapsed = timer.Elapsed.TotalMilliseconds;
                    Assert.True((bool)typeof(ScreenCaptureWindow).GetField("_snipClosed", flags)!.GetValue(capture)!, "真实剪贴板复制失败，不能以恢复窗口代替成功。");
                    PumpUntil(() => received.Task.IsCompleted);
                    var available = received.Task.GetAwaiter().GetResult();
                    Console.WriteLine($"snip-native 4K sample={i}: paste-ready={available:F1}ms, copy-complete={elapsed:F1}ms");
                    if (i > 0) samples.Add(available);
                }
                finally { capture.Close(); }
            }
        }
        finally { reader.Invoke(() => { SnipRemoveClipboardFormatListener(listener!.Handle); listener.Dispose(); }); }
        samples.Sort();
        // 合成 4K 热路径回归预算；不把此机器的中位数当作所有硬件的 SLA。
        Assert.True(samples[2] < 200, $"4K 截图到独立 STA 读取图片的中位数 {samples[2]:F1}ms 超过 200ms。");
    }

    private static void VerifySnipImageCompatibility()
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var bytes = new byte[17 * 9 * 4];
        new Random(23).NextBytes(bytes);
        for (var i = 3; i < bytes.Length; i += 4) bytes[i] = 128;
        var original = BitmapSource.Create(17, 9, 144, 144, PixelFormats.Bgra32, null, bytes, 17 * 4);
        original.Freeze();
        string? expectedHash = null;
        byte[] Rgb(BitmapSource bitmap)
        {
            var result = new byte[bitmap.PixelWidth * bitmap.PixelHeight * 3];
            new FormatConvertedBitmap(bitmap, PixelFormats.Bgr24, null, 0).CopyPixels(result, bitmap.PixelWidth * 3, 0);
            return result;
        }
        foreach (var format in new[] { PixelFormats.Bgr24, PixelFormats.Bgr32, PixelFormats.Bgra32, PixelFormats.Pbgra32, PixelFormats.Gray8 })
        {
            var source = new CroppedBitmap(new FormatConvertedBitmap(original, format, null, 0), new Int32Rect(1, 1, 13, 7));
            source.Freeze();
            var capture = new ScreenCaptureWindow(new System.Drawing.Bitmap(32, 24), new System.Drawing.Rectangle(0, 0, 32, 24), snippingTools: true);
            try
            {
                capture.Show();
                typeof(ScreenCaptureWindow).GetField("_snipSource", flags)!.SetValue(capture, source);
                var selectionPause = Task.Delay(1500);
                PumpUntil(() => selectionPause.IsCompleted);
                var copy = capture.CopySnipAsync();
                PumpUntil(() => copy.IsCompleted); copy.GetAwaiter().GetResult();
                Assert.True((bool)typeof(ScreenCaptureWindow).GetField("_snipClosed", flags)!.GetValue(capture)!, $"真实复制失败：{format}");
                var expected = Rgb(source);
                expectedHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(expected));
                // 发布线程和截图 HWND 均已退出；独立 STA 仍须读取全图，且历史编码可正常消费。
                var read = SnipClipboard.RunStaAsync(async () =>
                {
                    for (var attempt = 0; ; attempt++)
                    {
                        try
                        {
                            var content = await OverTranslate.Services.ClipboardHistory.ClipboardContent.CaptureAsync(System.Windows.Clipboard.GetDataObject()!, "fixture.exe");
                            Assert.NotNull(content?.Png);
                            var decoded = OverTranslate.Services.ClipboardHistory.ClipboardContent.DecodeImage(content.Png, false);
                            Assert.Equal(13, decoded.PixelWidth); Assert.Equal(7, decoded.PixelHeight);
                            Assert.Equal(expected, Rgb(decoded));
                            using var formsImage = System.Windows.Forms.Clipboard.GetImage();
                            // WinForms 会把部分剪贴板占用返回为 null；此处验兼容性，首次可读时限由独立原生消费者验证。
                            if (formsImage is null && attempt < 4) { await Task.Delay(20); continue; }
                            Assert.NotNull(formsImage);
                            Assert.Equal(13, formsImage.Width); Assert.Equal(7, formsImage.Height);
                            for (var y = 0; y < 7; y++) for (var x = 0; x < 13; x++)
                            {
                                var color = ((System.Drawing.Bitmap)formsImage).GetPixel(x, y);
                                var offset = (y * 13 + x) * 3;
                                Assert.Equal(expected[offset], color.B);
                                Assert.Equal(expected[offset + 1], color.G);
                                Assert.Equal(expected[offset + 2], color.R);
                            }
                            break;
                        }
                        catch (ExternalException) when (attempt < 4) { await Task.Delay(20); }
                    }
                });
                PumpUntil(() => read.IsCompleted); read.GetAwaiter().GetResult();
            }
            finally { capture.Close(); }
        }

        var start = new ProcessStartInfo("powershell.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in new[] { "-NoProfile", "-NonInteractive", "-STA", "-Command" }) start.ArgumentList.Add(argument);
        start.ArgumentList.Add("$ErrorActionPreference='Stop'; Add-Type -AssemblyName PresentationCore,WindowsBase; " +
            "$image=[System.Windows.Clipboard]::GetImage(); if ($null -eq $image -or $image.PixelWidth -ne 13 -or $image.PixelHeight -ne 7) { throw 'clipboard dimensions' }; " +
            "$rgb=New-Object System.Windows.Media.Imaging.FormatConvertedBitmap; $rgb.BeginInit(); $rgb.Source=$image; $rgb.DestinationFormat=[System.Windows.Media.PixelFormats]::Bgr24; $rgb.EndInit(); " +
            "$pixels=New-Object byte[] 273; $rgb.CopyPixels($pixels,39,0); $hash=[BitConverter]::ToString([Security.Cryptography.SHA256]::Create().ComputeHash($pixels)).Replace('-',''); " +
            $"if ($hash -ne '{expectedHash}') {{ throw 'clipboard pixels' }}; 'independent-process RGB passed'");
        using (var consumer = Process.Start(start)!)
        {
            var output = consumer.StandardOutput.ReadToEndAsync(); var errors = consumer.StandardError.ReadToEndAsync();
            var exited = consumer.WaitForExitAsync();
            PumpUntil(() => exited.IsCompleted);
            Assert.True(consumer.ExitCode == 0, errors.GetAwaiter().GetResult());
            Assert.Contains("independent-process RGB passed", output.GetAwaiter().GetResult());
        }

        using var owner = new HwndSource(new HwndSourceParameters("Screenshot failure test") { ParentWindow = new IntPtr(-3), WindowStyle = 0 });
        using var blocker = new HookThread("Screenshot clipboard blocker");
        blocker.Start();
        HwndSource? blockingWindow = null;
        blocker.Invoke(() =>
        {
            blockingWindow = new HwndSource(new HwndSourceParameters("Other clipboard owner") { ParentWindow = new IntPtr(-3), WindowStyle = 0 });
            Assert.True(SnipOpenClipboard(blockingWindow.Handle));
        });
        try
        {
            using var process = Process.GetCurrentProcess();
            var before = SnipGetGuiResources(process.Handle, 0);
            for (var i = 0; i < 20; i++)
            {
                var error = Assert.Throws<COMException>(() => ScreenshotClipboardImage.SetImage(original, owner.Handle));
                Assert.Equal(unchecked((int)0x800401D0), error.ErrorCode);
            }
            Assert.True(SnipGetGuiResources(process.Handle, 0) <= before + 1, "写入被占用时不能泄漏 GDI 位图。");
        }
        finally { blocker.Invoke(() => { SnipCloseClipboard(); blockingWindow!.Dispose(); }); }
        var ownerHandle = owner.Handle;
        var recovered = SnipClipboard.RunStaAsync(() => SnipClipboard.WriteImageAsync(original, CancellationToken.None, owner: ownerHandle));
        PumpUntil(() => recovered.IsCompleted); recovered.GetAwaiter().GetResult();
        var snapshot = ScreenshotClipboardImage.ReadImage();
        Assert.NotNull(snapshot);
        ScreenshotClipboardImage.SetImage(new FormatConvertedBitmap(original, PixelFormats.Gray8, null, 0), ownerHandle);
        Assert.Equal(Rgb(original), Rgb(snapshot));
        Console.WriteLine("snip-native: cropped/odd-size RGB, premultiplied and grayscale images, WPF/WinForms/history, owner shutdown and busy-write GDI cleanup passed");
    }

    [DllImport("user32.dll", EntryPoint = "AddClipboardFormatListener")] private static extern bool SnipAddClipboardFormatListener(IntPtr window);
    [DllImport("user32.dll", EntryPoint = "RemoveClipboardFormatListener")] private static extern bool SnipRemoveClipboardFormatListener(IntPtr window);
    [DllImport("user32.dll", EntryPoint = "OpenClipboard")] private static extern bool SnipOpenClipboard(IntPtr window);
    [DllImport("user32.dll", EntryPoint = "CloseClipboard")] private static extern bool SnipCloseClipboard();
    [DllImport("user32.dll", EntryPoint = "GetGuiResources")] private static extern uint SnipGetGuiResources(IntPtr process, uint flags);
    [DllImport("user32.dll", EntryPoint = "GetClipboardData")] private static extern IntPtr SnipGetClipboardData(uint format);
}
