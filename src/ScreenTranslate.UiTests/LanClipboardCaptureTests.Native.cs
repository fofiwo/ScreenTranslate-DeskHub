using System.Diagnostics;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using OverTranslate.Services;
using OverTranslate.Services.ClipboardHistory;
using OverTranslate.Services.Lan;
using Xunit;

namespace OverTranslate.Tests;

public sealed partial class LanClipboardCaptureTests
{
    [NativeClipboardFact]
    public void Real_receive_verifies_text_image_origin_and_survives_busy_clipboard() => OnSta(async () =>
    {
        await using var host = new LanNode("fixture-native-host", () => "");
        await using var client = new LanNode("fixture-native-client", () => "");
        using var sync = new LanClipboardSync(Dispatcher.CurrentDispatcher, host, listen: false);
        using var relay = new LanClipboardRelay(); relay.Add(host);
        host.ApplyClipboardAsync = sync.ApplyAsync;
        host.ConfirmPairAsync = (_, _) => Task.FromResult(true);
        await host.ListenAsync(IPAddress.Loopback, 0);
        await client.ConnectAsync(IPAddress.Loopback, host.Port, host.PairCode);
        using var blocker = new HookThread("LAN clipboard busy fixture"); blocker.Start();
        var locked = false;
        blocker.Invoke(() => locked = NativeOpenClipboard(IntPtr.Zero));
        Assert.True(locked);
        var id = Guid.NewGuid().ToString("N");
        var text = new LanClipboardPayload("text", Encoding.UTF8.GetBytes("LAN 合成文本 🧪"));
        try
        {
            var pending = client.SendClipboardAsync(text, id);
            var responsive = Stopwatch.StartNew();
            await Task.Delay(60);
            Assert.True(responsive.Elapsed < TimeSpan.FromMilliseconds(500), "接收重试阻塞了 UI 消息泵");
            Assert.False(await pending.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.True(host.IsConnected); Assert.True(client.IsConnected);
        }
        finally { blocker.Invoke(() => NativeCloseClipboard()); }
        Assert.True(await client.SendClipboardAsync(text, id));
        Assert.Equal("LAN 合成文本 🧪", System.Windows.Clipboard.GetText());
        var data = System.Windows.Clipboard.GetDataObject()!;
        Assert.Equal("来自 fixture-native-client", LanClipboardSync.DescribeOrigin(data.GetData(LanClipboardSync.OriginFormat)));
        Assert.Null(await LanClipboardSync.CaptureAsync(data));

        Assert.False(await client.SendClipboardAsync(new("text", [0xff])));
        Assert.False(await client.SendClipboardAsync(new("text", Encoding.UTF8.GetBytes("bad\0text"))));
        Assert.Equal("LAN 合成文本 🧪", System.Windows.Clipboard.GetText());
        blocker.Invoke(() => locked = NativeOpenClipboard(IntPtr.Zero));
        Assert.True(locked);
        try
        {
            var stale = client.SendClipboardAsync(new("text", Encoding.UTF8.GetBytes("must not arrive")));
            await Task.Delay(60);
            sync.Enabled = false; sync.Enabled = true;
            Assert.False(await stale.WaitAsync(TimeSpan.FromSeconds(5)));
        }
        finally { blocker.Invoke(() => NativeCloseClipboard()); }
        Assert.Equal("LAN 合成文本 🧪", System.Windows.Clipboard.GetText());

        var bitmap = BitmapSource.Create(3, 2, 96, 96, PixelFormats.Bgra32, null,
            Enumerable.Repeat(new byte[] { 17, 29, 43, 255 }, 6).SelectMany(x => x).ToArray(), 12);
        bitmap.Freeze();
        Assert.True(await client.SendClipboardAsync(new("image", ClipboardContent.EncodeImage(bitmap))));
        // 发布者的 STA 和 HWND 已退出；另一进程仍须能读取可粘贴的位图。
        var start = new ProcessStartInfo("powershell.exe")
            { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add("-NoProfile"); start.ArgumentList.Add("-STA"); start.ArgumentList.Add("-Command");
        start.ArgumentList.Add("Add-Type -AssemblyName PresentationCore; $b=[System.Windows.Clipboard]::GetImage(); if($null -eq $b -or $b.PixelWidth -ne 3 -or $b.PixelHeight -ne 2){exit 3}; $c=New-Object System.Windows.Media.Imaging.FormatConvertedBitmap($b,[System.Windows.Media.PixelFormats]::Bgr32,$null,0); $p=New-Object byte[] 24; $c.CopyPixels($p,12,0); for($i=0;$i -lt 24;$i+=4){if($p[$i] -ne 17 -or $p[$i+1] -ne 29 -or $p[$i+2] -ne 43){exit 4}}; 'PASS'");
        using var reader = Process.Start(start)!;
        await reader.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(8));
        Assert.True(reader.ExitCode == 0, $"reader exit={reader.ExitCode}: {await reader.StandardError.ReadToEndAsync()}");
        Assert.Contains("PASS", await reader.StandardOutput.ReadToEndAsync());
        Assert.True(host.IsConnected);
    });

    [DllImport("user32.dll", EntryPoint = "OpenClipboard")] private static extern bool NativeOpenClipboard(IntPtr owner);
    [DllImport("user32.dll", EntryPoint = "CloseClipboard")] private static extern bool NativeCloseClipboard();
}

internal sealed class NativeClipboardFactAttribute : FactAttribute
{
    public NativeClipboardFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("SCREEN_TRANSLATE_LAN_CLIPBOARD_NATIVE") != "1")
            Skip = "设置 SCREEN_TRANSLATE_LAN_CLIPBOARD_NATIVE=1 后使用合成内容读写真实系统剪贴板。";
    }
}
