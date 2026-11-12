using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using OverTranslate.Models;
using OverTranslate.Services.ClipboardHistory;
using OverTranslate.Services.Lan;
using OverTranslate.Views.ClipboardHistory;
using Xunit;
using DataObject = System.Windows.DataObject;

namespace OverTranslate.Tests;

public sealed partial class ScreenTranslateShellTests
{
    private static void VerifyClipboardDeviceSource()
    {
        var root = Path.Combine(Path.GetTempPath(), "clipboard-source-ui-" + Guid.NewGuid().ToString("N"));
        ClipboardHistoryWindow? window = null;
        try
        {
            var data = new DataObject(); data.SetText("Synthetic remote clipboard content");
            data.SetData(LanClipboardSync.OriginFormat, "fixture-laptop");
            var captured = ClipboardContent.Capture(data, "MouseWithoutBorders.exe")!;
            Assert.Equal("来自 fixture-laptop", captured.Record.Source);
            var store = new ClipboardHistoryStore(Path.Combine(root, "history"));
            store.Add(captured.Record);
            store.Add(captured.Record with { Source = "MouseWithoutBorders.exe", CopiedAt = captured.Record.CopiedAt.AddMilliseconds(500) });
            Assert.Equal(captured.Record.Source, Assert.Single(store.Read()).Source);
            var imageData = new DataObject();
            var bitmap = BitmapSource.Create(1, 1, 96, 96, PixelFormats.Bgra32, null, new byte[] { 0, 0, 255, 255 }, 4);
            bitmap.Freeze(); imageData.SetImage(bitmap);
            imageData.SetData(LanClipboardSync.OriginFormat, "fixture-image-PC");
            var image = ClipboardContent.Capture(imageData, "fixture.exe")!;
            Assert.Equal("来自 fixture-image-PC", image.Record.Source); Assert.NotNull(image.Png);
            window = new ClipboardHistoryWindow(store, _ => Task.CompletedTask, () => Task.FromResult(false),
                new FixtureClipboardHistory(), settings: new AppSettings(), saveSettings: () => true,
                syncActivities: new SyncActivityStore(Path.Combine(root, "activity.json")))
                { Left = -10000, Top = -10000, ShowActivated = false };
            window.SetWindowPinned(true);
            window.Show(); window.UpdateLayout();
            // 模拟 CI 中的焦点切换，来源展示检查必须在失焦后仍可执行。
            SendMessage(new System.Windows.Interop.WindowInteropHelper(window).Handle, 0x0006, IntPtr.Zero, IntPtr.Zero);
            window.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.Background);
            var list = (ListBox)window.FindName("HistoryList");
            PumpUntil(() => list.Items.Count == 1 && Visuals<TextBlock>(list).Any(t => t.Text == captured.Record.Source));
            var source = Visuals<TextBlock>(list).Single(t => t.Text == captured.Record.Source);
            Assert.True(source.IsVisible); Assert.Equal(captured.Record.Source, source.ToolTip);
            var preview = Visuals<TextBlock>(list).Single(t => t.Text == captured.Record.Text);
            Assert.True(source.TranslatePoint(new Point(), list).Y > preview.TranslatePoint(new Point(), list).Y);
            Render(window, "clipboard-source-light", 560, 700);
            list.SelectedIndex = 0; window.ShowPreview();
            var detail = (TextBlock)window.FindName("DetailSource");
            Assert.Equal(captured.Record.Source, detail.Text); Assert.True(detail.IsVisible);
            Render(window, "clipboard-source-preview", 560, 700);
            ((Border)window.FindName("PreviewPanel")).Visibility = Visibility.Collapsed;
            var longOrigin = "fixture-" + new string('x', 72);
            store.Add(captured.Record with { Source = "来自 " + longOrigin, CopiedAt = DateTimeOffset.UtcNow.AddSeconds(1) });
            ((TextBox)window.FindName("SearchBox")).Text = longOrigin;
            PumpUntil(() => list.Items.Count == 1 && Visuals<TextBlock>(list).Any(t => t.Text == "来自 " + longOrigin));
            window.Width = 460; window.Height = 400; window.UpdateLayout();
            var longSource = Visuals<TextBlock>(list).Single(t => t.Text == "来自 " + longOrigin);
            AssertInside((FrameworkElement)window.Content, longSource);
            Assert.Equal(TextTrimming.CharacterEllipsis, longSource.TextTrimming);
            ApplyTheme(System.Windows.Application.Current, "Dark");
            Render(window, "clipboard-source-minimum-dark", 460, 400);

            var relayData = new DataObject(); relayData.SetText("Synthetic clipboard without device metadata");
            var relay = ClipboardContent.Capture(relayData, "MouseWithoutBorders.exe")!;
            store.Add(relay.Record);
            ((TextBox)window.FindName("SearchBox")).Text = relay.Record.Text;
            PumpUntil(() => list.Items.Count == 1 && Visuals<TextBlock>(list).Any(t => t.Text == relay.Record.SourceLabel));
            var relaySource = Visuals<TextBlock>(list).Single(t => t.Text == relay.Record.SourceLabel);
            Assert.True(relaySource.IsVisible); AssertInside((FrameworkElement)window.Content, relaySource);
            Assert.Equal(relay.Record.SourceLabel, relaySource.ToolTip);
            list.SelectedIndex = 0; window.ShowPreview();
            Assert.Equal(relay.Record.SourceLabel, detail.Text); Assert.True(detail.IsVisible);
        }
        finally
        {
            window?.Close(); ApplyTheme(System.Windows.Application.Current, "Light");
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }
}
