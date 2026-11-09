using System.Windows;
using System.Windows.Automation;
using OverTranslate.Services;
using UserControl = System.Windows.Controls.UserControl;

namespace OverTranslate.Views.Shell;

public partial class ScreenshotToolsPage : UserControl
{
    private readonly Action _screenshot;
    private readonly Action _pin;
    private readonly Action _shortcuts;

    public ScreenshotToolsPage(Action screenshot, Action pin, Action shortcuts)
    {
        _screenshot = screenshot;
        _pin = pin;
        _shortcuts = shortcuts;
        InitializeComponent();
    }

    public void Reload(string screenshotShortcut, string pinShortcut, bool framing)
    {
        ScreenshotShortcutText.Text = screenshotShortcut;
        PinShortcutText.Text = pinShortcut;
        AutomationProperties.SetAcceleratorKey(StartScreenshotButton, screenshotShortcut);
        AutomationProperties.SetAcceleratorKey(PinClipboardButton, pinShortcut);
        StartScreenshotButton.IsEnabled = PickColorButton.IsEnabled = !framing;
        var reason = framing ? LocalizationService.Get("S.Shell.CaptureBlockedByRealtimeFraming") : null;
        StartScreenshotButton.ToolTip = PickColorButton.ToolTip = reason;
        AvailabilityText.Text = reason ?? "复制到剪贴板或贴到桌面；截图工具不会自动调用识别或翻译。";
    }

    private void Screenshot_Click(object sender, RoutedEventArgs e) => _screenshot();
    private void Pin_Click(object sender, RoutedEventArgs e) => _pin();
    private void Shortcuts_Click(object sender, RoutedEventArgs e) => _shortcuts();
}
