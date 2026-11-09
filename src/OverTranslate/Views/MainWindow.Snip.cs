using System.Drawing;
using System.Windows;
using System.Windows.Interop;
using OverTranslate.Services;
using OverTranslate.Views.Capture;
using OverTranslate.Views.Realtime;

namespace OverTranslate.Views;

public partial class MainWindow
{
    private ScreenCaptureWindow? _snipCapture;
    private GlobalHotkey? _snipHotkey;
    private GlobalHotkey? _pinImageHotkey;
    private GlobalEscapeHook? _snipEscape;
    private SystemRecoveryYield? _snipRecovery;

    public void StartScreenshotFromShell(Window? shell = null)
    {
        if (_snipCapture is not null) { _snipCapture.Close(); return; }
        if (HasActiveSession) CloseAll("switch to screenshot");
        if (!RealtimeSessionController.Instance.TryYieldToCapture())
        { ShowTrayNotification("暂时无法截图", "请先完成持续翻译区域的选择。"); return; }
        Bitmap? screenshot = null;
        ScreenCaptureWindow? window = null;
        try
        {
            if (shell?.IsVisible == true) WindowScreenPresence.HideAndWaitForScreen(shell);
            var bounds = ScreenGeometry.VirtualDesktopBounds();
            screenshot = new Bitmap(bounds.Width, bounds.Height);
            using (var graphics = Graphics.FromImage(screenshot))
                graphics.CopyFromScreen(bounds.Left, bounds.Top, 0, 0, bounds.Size, CopyPixelOperation.SourceCopy);
            window = new ScreenCaptureWindow(screenshot, bounds, snippingTools: true);
            screenshot = null; // 截图窗口取得所有权，关闭时释放。
            _snipCapture = window;
            window.Closed += (_, _) => {
                if (!ReferenceEquals(_snipCapture, window)) return;
                _snipCapture = null;
                _snipEscape?.Dispose(); _snipEscape = null;
                _snipRecovery?.Dispose(); _snipRecovery = null;
                if (shell is not null && !_exitingApplication) { shell.Show(); }
                if (!_exitingApplication) RealtimeSessionController.Instance.ResumeAfterCapture();
            };
            // Show 可能泵送关闭事件；先安装钩子，让 Closed 始终能成对释放。
            _snipEscape = GlobalEscapeHook.Install(() => { if (ReferenceEquals(_snipCapture, window)) window.Close(); });
            _snipRecovery = SystemRecoveryYield.Install(ApplyScreenshotTopmost);
            window.Show();
            ApplyScreenshotTopmost();
        }
        catch (Exception)
        {
            window?.Close(); screenshot?.Dispose();
            if (shell is not null && !_exitingApplication) shell.Show();
            RealtimeSessionController.Instance.ResumeAfterCapture();
            ShowTrayNotification("截图失败", "无法读取当前屏幕，请稍后重试。");
        }
    }

    private void ApplyScreenshotTopmost()
    {
        if (_snipCapture is not { IsVisible: true } capture) return;
        capture.ApplySnipRecovery(_snipRecovery?.HasYielded == true, _snipRecovery?.RecoveryWindow ?? IntPtr.Zero);
    }

    public void PinClipboardImage()
    {
        // 全屏框选时只接受其自身工具栏，避免贴图激活后留下阻挡桌面的旧窗口。
        if (_snipCapture is not null || HasActiveSession) return;
        try
        {
            var image = System.Windows.Clipboard.GetImage();
            if (image is null) { ShowTrayNotification("没有可贴的图片", "请先复制一张图片，或使用截图工具栏中的“贴图”。"); return; }
            image.Freeze();
            var cursor = System.Windows.Forms.Cursor.Position;
            PinnedImageWindow.Open(image, cursor.X, cursor.Y);
        }
        catch (Exception) { ShowTrayNotification("贴图失败", "剪贴板暂时不可用或图片格式不受支持，请重新复制图片后重试。"); }
    }
}
