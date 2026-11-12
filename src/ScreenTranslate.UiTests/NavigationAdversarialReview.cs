using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using OverTranslate.Services;
using OverTranslate.Services.Library;
using OverTranslate.Views.Shell;
using Xunit;

namespace OverTranslate.Tests;

public sealed partial class ScreenTranslateShellTests
{
    private static void VerifyQuickToolWindowMode()
    {
        var sessionState = new[] { "_instance", "_lastSize", "_lastWindowState", "_lastRailCollapsed", "_lastPage", "_translationMode" }
            .Select(name => typeof(ShellWindow).GetField(name,
                System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!)
            .Select(field => (Field: field, Value: field.GetValue(null))).ToArray();
        var app = System.Windows.Application.Current;
        var previousMain = app.MainWindow;
        var settings = SettingsService.Instance.Current;
        var previousRealtime = settings.Realtime;
        var main = new OverTranslate.Views.MainWindow();
        var shell = new ShellWindow(() => new LibraryState(), () => { })
        {
            Left = -10000, Top = -10000, ShowActivated = false,
            WindowStartupLocation = WindowStartupLocation.Manual
        };
        try
        {
            app.MainWindow = main;
            settings.Realtime = new Models.RealtimeSettings
            {
                CaptureMode = Models.RealtimeCaptureMode.Window,
                CaptureWindowProcess = "screentranslate-quick-tools-no-such-app"
            };
            Assert.Null(OverTranslate.Views.Realtime.RealtimeQuickStart.From(settings).Request);
            var header = (Grid)shell.FindName("WindowTitleBar");
            var quickControls = (FrameworkElement)shell.FindName("QuickWindowControls");
            Assert.Equal(Visibility.Visible, header.Visibility);
            ((Button)shell.FindName("RealtimeQuickBtn")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.IsType<OverTranslate.Views.Realtime.RealtimePage>(((Border)shell.FindName("ContentHost")).Child);
            var refusedTitle = header.Visibility;
            var refusedControls = quickControls.Visibility;

            ShellWindow.ShowOrActivateFromShortcut(ShellPage.Home);
            ((Button)shell.FindName("RealtimeQuickBtn")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Equal(Visibility.Collapsed, header.Visibility);
            Assert.Equal(Visibility.Visible, quickControls.Visibility);
            ((Button)shell.FindName("MainWindowBtn")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.True(refusedTitle == Visibility.Visible && refusedControls == Visibility.Collapsed
                && header.Visibility == Visibility.Visible && quickControls.Visibility == Visibility.Collapsed,
                $"Normal-window refusal must preserve its title bar (title={refusedTitle}, quick controls={refusedControls}); "
                + $"opening the main window must restore it (title={header.Visibility}, quick controls={quickControls.Visibility}).");
            Assert.Equal(Visibility.Visible, header.Visibility);
            Assert.Equal(Visibility.Collapsed, quickControls.Visibility);
            typeof(OverTranslate.Views.MainWindow).GetMethod("OnRealtimePauseHotkeyPressed",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                .Invoke(main, [null, EventArgs.Empty]);
            Assert.Equal(Visibility.Collapsed, header.Visibility);
            Assert.Equal(Visibility.Visible, quickControls.Visibility);

            typeof(OverTranslate.Views.MainWindow).GetProperty(nameof(main.HotkeyRegistrationFailures))!
                .SetValue(main, Enum.GetValues<HotkeyAction>());
            shell.RefreshHotkeyHints();
            Render(shell, "quick-tools-registration-failures");
            foreach (var button in ((StackPanel)shell.FindName("QuickToolsPanel")).Children.OfType<Button>())
                Assert.Empty(((Grid)button.Content).Children.OfType<TextBlock>().Last().Text);
        }
        finally
        {
            settings.Realtime = previousRealtime;
            app.MainWindow = previousMain;
            // 隐藏宿主没有启动应用生命周期；关闭测试实例时不能退出整个应用。
            typeof(OverTranslate.Views.MainWindow).GetField("_exitingApplication",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.SetValue(main, true);
            main.Close();
            shell.Close();
            foreach (var state in sessionState) state.Field.SetValue(null, state.Value);
        }
    }

    private static void VerifyScreenshotToolActions()
    {
        var captures = 0;
        var pins = 0;
        var page = new ScreenshotToolsPage(() => captures++, () => pins++, () => { });
        page.Reload("Ctrl+Alt+Q", "Ctrl+Alt+I", false);
        foreach (var name in new[] { "StartScreenshotButton", "PickColorButton", "PinClipboardButton" })
            ((Button)page.FindName(name)).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.Equal(2, captures);
        Assert.Equal(1, pins);
        page.Reload("", "", true);
        Assert.False(((Button)page.FindName("StartScreenshotButton")).IsEnabled);
        Assert.False(((Button)page.FindName("PickColorButton")).IsEnabled);
        Assert.Equal("请先完成持续翻译区域框选。", ((Button)page.FindName("PickColorButton")).ToolTip);
        Assert.True(((Button)page.FindName("PinClipboardButton")).IsEnabled);
        page.Reload("Ctrl+Alt+Q", "Ctrl+Alt+I", false);
        Assert.True(((Button)page.FindName("StartScreenshotButton")).IsEnabled);
        Assert.Null(((Button)page.FindName("StartScreenshotButton")).ToolTip);
        Assert.Equal("Ctrl+Alt+Q", AutomationProperties.GetAcceleratorKey((Button)page.FindName("StartScreenshotButton")));
    }

    private static void VerifyShortcutHintsMatchBindings()
    {
        var settings = SettingsService.Instance.Current;
        var original = (settings.OcrHotkeyModifiers, settings.OcrHotkeyVirtualKey,
            settings.OcrHotkeyDisplay, settings.OcrHotkeyEnabled);
        var shell = new ShellWindow(() => new LibraryState(), () => { });
        try
        {
            Render(shell, "shortcut-sync");
            settings.OcrHotkeyEnabled = true;
            settings.OcrHotkeyModifiers = settings.HotkeyModifiers;
            settings.OcrHotkeyVirtualKey = settings.HotkeyVirtualKey;
            settings.OcrHotkeyDisplay = settings.HotkeyDisplay;
            Assert.False(HotkeyBindings.Resolve(settings).Single(b => b.Action == HotkeyAction.OcrExtract).IsActive);
            shell.RefreshHotkeyHints();
            Assert.Empty(((TextBlock)shell.FindName("OcrHotkeyText")).Text);
            Assert.True(((Button)shell.FindName("OcrExtractBtn")).IsEnabled);
            Assert.Equal(Visibility.Visible, ((Button)shell.FindName("OcrExtractBtn")).Visibility);
            Assert.Empty(AutomationProperties.GetAcceleratorKey((Button)shell.FindName("OcrExtractBtn")));

            settings.OcrHotkeyEnabled = false;
            shell.RefreshHotkeyHints();
            Assert.Empty(((TextBlock)shell.FindName("OcrHotkeyText")).Text);
            Assert.Equal(Visibility.Collapsed, ((Button)shell.FindName("OcrExtractBtn")).Visibility);

            foreach (var (action, prefix) in new[] {
                (HotkeyAction.TranslationWindow, "TranslationWindowHotkey"), (HotkeyAction.RealtimePause, "RealtimePauseHotkey"),
                (HotkeyAction.LibraryWindow, "LibraryHotkey"), (HotkeyAction.ClipboardHistory, "ClipboardHotkey"),
                (HotkeyAction.Screenshot, "ScreenshotHotkey"), (HotkeyAction.PinImage, "PinImageHotkey") })
            {
                var enabled = settings.GetType().GetProperty(prefix + "Enabled")!;
                var display = settings.GetType().GetProperty(prefix + "Display")!;
                var previousEnabled = enabled.GetValue(settings);
                var previousDisplay = display.GetValue(settings);
                var button = ((StackPanel)shell.FindName("QuickToolsPanel")).Children.OfType<Button>()
                    .Single(b => Equals(b.Tag, action.ToString()));
                try
                {
                    enabled.SetValue(settings, false);
                    shell.RefreshHotkeyHints();
                    Assert.Equal(Visibility.Collapsed, button.Visibility);
                    enabled.SetValue(settings, true);
                    display.SetValue(settings, "Ctrl+Alt+Shift+F12");
                    shell.RefreshHotkeyHints();
                    Assert.Equal(Visibility.Visible, button.Visibility);
                    var active = HotkeyBindings.Resolve(settings).Single(b => b.Action == action).IsActive;
                    shell.UpdateLayout();
                    PumpUntil(() => AutomationProperties.GetAcceleratorKey(button) == (active ? "Ctrl+Alt+Shift+F12" : ""));
                    Assert.Equal(active ? "Ctrl+Alt+Shift+F12" : "", AutomationProperties.GetAcceleratorKey(button));
                }
                finally
                {
                    enabled.SetValue(settings, previousEnabled);
                    display.SetValue(settings, previousDisplay);
                    shell.RefreshHotkeyHints();
                }
            }
            Assert.Equal(Visibility.Visible, ((Button)shell.FindName("CaptureTranslateBtn")).Visibility);
            settings.OcrHotkeyEnabled = true;
            shell.RefreshHotkeyHints();
            Assert.Equal(Visibility.Visible, ((Button)shell.FindName("OcrExtractBtn")).Visibility);
        }
        finally
        {
            (settings.OcrHotkeyModifiers, settings.OcrHotkeyVirtualKey,
                settings.OcrHotkeyDisplay, settings.OcrHotkeyEnabled) = original;
            shell.RefreshHotkeyHints();
            shell.Close();
        }
    }
}
