using System.Windows;
using System.Windows.Threading;
using NLog;
using OverTranslate.Views.Capture;
using OverTranslate.Views.ClipboardHistory;
using OverTranslate.Views.Library;

namespace OverTranslate.Services;

internal static class ShortcutUiWarmup
{
    internal static bool ShouldSkip => System.Windows.Application.Current.Dispatcher.HasShutdownStarted
        || System.Windows.Application.Current.MainWindow is Views.MainWindow { IsCapturing: true };

    internal static async Task RunAsync()
    {
        foreach (var prepare in new Func<Task>[]
        {
            () => { PrepareCapture(); return SnipClipboard.PrepareAsync(); },
            () => ClipboardHistoryWindow.PrepareForShortcutAsync(Services.ClipboardHistory.ClipboardHistoryRuntime.StoreAsync),
            () => LibraryWindow.PrepareForShortcutAsync(() => new Services.Library.LibraryStore(Services.Library.LibraryStore.DefaultPath))
        })
        {
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            if (ShouldSkip) return;
            try { await prepare(); }
            catch (Exception) { LogManager.GetCurrentClassLogger().Warn("Shortcut UI preparation failed; normal opening will retry."); }
        }
    }

    internal static void PrepareCapture()
    {
        using var bitmap = new System.Drawing.Bitmap(32, 32);
        _ = BitmapInterop.ToBitmapSource(bitmap);
        var window = new ScreenCaptureWindow(bitmap, new(0, 0, 32, 32), snippingTools: true);
        try { PrepareLayout(window); }
        finally { window.Close(); }
    }

    internal static void PrepareLayout(Window window)
    {
        // 只实例化离屏控件和模板，不创建 HWND、抢焦点或截取桌面。
        var root = (FrameworkElement)window.Content;
        var size = new System.Windows.Size(window.Width, window.Height);
        root.Measure(size);
        root.Arrange(new Rect(size));
        root.UpdateLayout();
    }
}
