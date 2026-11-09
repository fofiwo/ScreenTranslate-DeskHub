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
            settings.OcrHotkeyEnabled = true;
            settings.OcrHotkeyModifiers = settings.HotkeyModifiers;
            settings.OcrHotkeyVirtualKey = settings.HotkeyVirtualKey;
            settings.OcrHotkeyDisplay = settings.HotkeyDisplay;
            Assert.False(HotkeyBindings.Resolve(settings).Single(b => b.Action == HotkeyAction.OcrExtract).IsActive);
            shell.RefreshHotkeyHints();
            Assert.Empty(((TextBlock)shell.FindName("OcrHotkeyText")).Text);
            Assert.True(((Button)shell.FindName("OcrExtractBtn")).IsEnabled);
            Assert.Empty(AutomationProperties.GetAcceleratorKey((Button)shell.FindName("OcrExtractBtn")));

            settings.OcrHotkeyEnabled = false;
            shell.RefreshHotkeyHints();
            Assert.Empty(((TextBlock)shell.FindName("OcrHotkeyText")).Text);
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
