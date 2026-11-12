using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using OverTranslate.Services.Library;
using OverTranslate.Views.Realtime;
using OverTranslate.Views.Settings;
using OverTranslate.Views.Shell;
using Xunit;

namespace OverTranslate.Tests;

public sealed partial class ScreenTranslateShellTests
{
    private static void VerifyMultitoolNavigation()
    {
        var openedLibrary = 0;
        var shell = new ShellWindow(() => new LibraryState(), () => openedLibrary++);
        try
        {
            var translation = Assert.IsType<RadioButton>(shell.FindName("TranslationNav"));
            var screenshot = Assert.IsType<RadioButton>(shell.FindName("HomeNav"));
            var continuous = Assert.IsType<RadioButton>(shell.FindName("RealtimeNav"));
            var settings = Assert.IsType<RadioButton>(shell.FindName("SettingsNav"));
            var about = Assert.IsType<RadioButton>(shell.FindName("AboutNav"));
            var header = Assert.IsType<StackPanel>(shell.FindName("TranslationHeader"));
            var host = (Border)shell.FindName("ContentHost");
            var home = Assert.IsType<HomePage>(host.Child);
            var homeSelections = new[] { "SourceBox", "TargetBox", "ProviderBox" }
                .Select(name => ((ComboBox)home.FindName(name)).SelectedValue).ToArray();
            Assert.True(translation.IsChecked);
            Assert.True(screenshot.IsChecked);

            foreach (var scale in new[] { 1.0, 1.25, 1.5, 2.0 })
            {
                Render(shell, $"multitool-home-{scale * 100:0}", 1100, 680, scale);
                var scroll = Assert.IsType<ScrollViewer>(shell.FindName("ToolScroll"));
                Assert.Equal(ScrollBarVisibility.Auto, scroll.VerticalScrollBarVisibility);
                foreach (var name in new[] { "TranslationNav", "ScreenshotNav", "LibraryBtn", "ClipboardHistoryBtn", "CaptureTranslateBtn", "RealtimeQuickBtn", "MainWindowBtn", "QuickLibraryBtn", "QuickClipboardBtn", "ScreenshotBtn", "OcrExtractBtn", "PinImageBtn", "SettingsNav", "AboutNav" })
                {
                    var entry = (FrameworkElement)shell.FindName(name);
                    if (IsDescendantOf(entry, scroll))
                    {
                        scroll.ScrollToVerticalOffset(entry.TranslatePoint(new Point(), (UIElement)scroll.Content).Y);
                        shell.UpdateLayout();
                        AssertInside(scroll, entry);
                    }
                    AssertInside((FrameworkElement)shell.Content, entry);
                }
                foreach (var (button, label) in new[] { ("CaptureTranslateBtn", "CaptureHotkeyText"), ("RealtimeQuickBtn", "RealtimeHotkeyText"), ("MainWindowBtn", "WindowHotkeyText"), ("QuickLibraryBtn", "QuickLibraryHotkeyText"), ("QuickClipboardBtn", "QuickClipboardHotkeyText"), ("LibraryBtn", "LibraryHotkeyText"), ("ClipboardHistoryBtn", "ClipboardHotkeyText"), ("ScreenshotBtn", "ScreenshotHotkeyText"), ("OcrExtractBtn", "OcrHotkeyText"), ("PinImageBtn", "PinImageHotkeyText") })
                    Assert.Equal(((TextBlock)shell.FindName(label)).Text,
                        System.Windows.Automation.AutomationProperties.GetAcceleratorKey((Button)shell.FindName(button)));
            }

            continuous.IsChecked = true;
            var realtime = Assert.IsType<RealtimePage>(host.Child);
            var realtimeSelections = new[] { "SrcLangBox", "TgtLangBox", "ProviderBox" }
                .Select(name => ((ComboBox)realtime.FindName(name)).SelectedValue).ToArray();
            Assert.True(translation.IsChecked);
            Render(shell, "multitool-continuous", 1100, 680);
            ((Button)shell.FindName("LibraryBtn")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Equal(1, openedLibrary);
            ((Button)shell.FindName("QuickLibraryBtn")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Equal(2, openedLibrary);
            Assert.Same(realtime, host.Child);
            Assert.True(translation.IsChecked);

            settings.IsChecked = true;
            Assert.IsType<ScreenTranslatePreferencesPage>(host.Child);
            Assert.Equal(Visibility.Collapsed, header.Visibility);
            Assert.False(translation.IsChecked);
            Render(shell, "multitool-settings", 1100, 680);
            ((Button)shell.FindName("MainWindowBtn")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Same(realtime, host.Child);
            translation.IsChecked = true;
            Assert.Same(realtime, host.Child);
            Assert.True(continuous.IsChecked);
            Assert.Equal(Visibility.Visible, header.Visibility);
            Assert.Equal(realtimeSelections, new[] { "SrcLangBox", "TgtLangBox", "ProviderBox" }
                .Select(name => ((ComboBox)realtime.FindName(name)).SelectedValue).ToArray());

            screenshot.IsChecked = true;
            Assert.Same(home, host.Child);
            about.IsChecked = true;
            Assert.IsType<ProductAboutPage>(host.Child);
            Assert.False(translation.IsChecked);
            Assert.False(settings.IsChecked);
            Render(shell, "multitool-about", 1100, 680);
            shell.Navigate(ShellPage.Realtime);
            Assert.Same(realtime, host.Child);
            Assert.True(continuous.IsChecked);
            Assert.True(translation.IsChecked);
            Assert.False(about.IsChecked);
            shell.Navigate(ShellPage.Home);
            Assert.Same(home, host.Child);
            Assert.Equal(homeSelections, new[] { "SourceBox", "TargetBox", "ProviderBox" }
                .Select(name => ((ComboBox)home.FindName(name)).SelectedValue).ToArray());

            ((RadioButton)shell.FindName("ScreenshotNav")).IsChecked = true;
            var screenshotPage = Assert.IsType<ScreenshotToolsPage>(host.Child);
            Assert.Equal(Visibility.Collapsed, header.Visibility);
            Assert.False(translation.IsChecked);
            foreach (var scale in new[] { 1.0, 1.25, 1.5, 2.0 })
            {
                Render(shell, $"multitool-screenshot-{scale * 100:0}", 1100, 680, scale);
                foreach (var name in new[] { "StartScreenshotButton", "PinClipboardButton", "PickColorButton" })
                    AssertInside((FrameworkElement)shell.Content, (FrameworkElement)screenshotPage.FindName(name));
            }
            try
            {
                ApplyTheme(System.Windows.Application.Current, "Dark");
                Render(shell, "multitool-screenshot-dark", 1100, 680);
            }
            finally { ApplyTheme(System.Windows.Application.Current, "Light"); }
            ((Button)screenshotPage.FindName("ShortcutSettingsButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Equal(2, ((TabControl)Assert.IsType<ScreenTranslatePreferencesPage>(host.Child).FindName("Tabs")).SelectedIndex);
            shell.Navigate(ShellPage.Screenshot);
            Assert.Same(screenshotPage, host.Child);
            translation.IsChecked = true;
            Assert.Same(home, host.Child);

            foreach (var name in new[] { "ScreenshotHotkeyText", "OcrHotkeyText", "PinImageHotkeyText" })
                ((TextBlock)shell.FindName(name)).Text = "Ctrl+Alt+Shift+OemOpenBrackets";
            Render(shell, "multitool-long-shortcuts", 1100, 680);
            foreach (var name in new[] { "ScreenshotBtn", "OcrExtractBtn", "PinImageBtn" })
            {
                var entry = (Button)shell.FindName(name);
                var frame = (Border)entry.Template.FindName("Root", entry);
                var row = Assert.IsType<Grid>(entry.Content);
                var texts = row.Children.OfType<TextBlock>().ToArray();
                foreach (var text in texts) AssertInside(frame, text);
                Assert.True(texts[0].ActualWidth >= 45, "The action name must remain readable with long shortcuts.");
                Assert.True(texts[0].TranslatePoint(new Point(texts[0].ActualWidth, 0), frame).X
                    <= texts[1].TranslatePoint(new Point(), frame).X, "Name and shortcut must not overlap.");
                Assert.Equal(texts[1].Text, texts[1].ToolTip);
                Assert.True(ToolTipService.GetShowsToolTipOnKeyboardFocus(entry));
            }

            shell.Navigate(ShellPage.Realtime);
            shell.Navigate(ShellPage.Settings);
            shell.Close();
            shell = new ShellWindow(() => new LibraryState(), () => { });
            Assert.IsType<RealtimePage>(((Border)shell.FindName("ContentHost")).Child);
            shell.Navigate(ShellPage.Home);
            WithReviewLibrary((window, _, _) =>
            {
                Render(window, "multitool-library-prompts", 880, 580);
                ((TabControl)window.FindName("Tabs")).SelectedIndex = 1;
                Render(window, "multitool-library-models", 880, 580);
                AssertInside((FrameworkElement)window.Content, (FrameworkElement)window.FindName("ModelSearch"));
            });
        }
        finally { shell.Close(); }
    }

    private static bool IsDescendantOf(DependencyObject child, DependencyObject ancestor)
    {
        for (var current = VisualTreeHelper.GetParent(child); current is not null; current = VisualTreeHelper.GetParent(current))
            if (ReferenceEquals(current, ancestor)) return true;
        return false;
    }
}
