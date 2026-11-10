using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using OverTranslate.Services.Library;
using OverTranslate.Views.Library;
using Xunit;

namespace OverTranslate.Tests;

public sealed partial class ScreenTranslateShellTests
{
    private static void VerifyLibraryQuickAccess()
    {
        var directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "library-quick-access-" + Guid.NewGuid());
        var store = new LibraryStore(System.IO.Path.Combine(directory, "library.json"));
        store.SavePrompt(new() { Title = "快捷唤出测试", Content = "仅用于测试的完整正文" });
        store.Update(state => state.Connections = [new() { Platform = "本地测试", Model = "fixture-model" }]);
        var copied = new List<string>();
        var window = new LibraryWindow(store, copy: copied.Add)
        {
            Left = -10000, Top = -10000, ShowActivated = false,
            WindowStartupLocation = WindowStartupLocation.Manual
        };
        var current = typeof(LibraryWindow).GetField("_current", BindingFlags.Static | BindingFlags.NonPublic)!;
        var previous = current.GetValue(null);
        current.SetValue(null, window);
        try
        {
            var header = (Grid)window.FindName("ManagementHeader");
            var tabs = (TabControl)window.FindName("Tabs");
            var search = (TextBox)window.FindName("PromptSearch");
            Assert.Equal(Visibility.Visible, header.Visibility);
            LibraryWindow.Toggle();
            Assert.True(window.IsVisible);
            Assert.Equal(Visibility.Collapsed, header.Visibility);
            Assert.Equal(WindowStyle.None, window.WindowStyle);
            AssertNoNativeCaption(window);
            Assert.False(((FrameworkElement)window.FindName("ManagementChrome")).IsVisible);
            Assert.Equal(0, System.Windows.Shell.WindowChrome.GetWindowChrome(window).CaptionHeight);
            Assert.Equal(ResizeMode.CanResize, window.ResizeMode);
            Assert.False(((TextBlock)window.FindName("ShortcutHint")).IsVisible);

            foreach (var scale in new[] { 1.0, 1.25, 1.5, 2.0 })
            {
                tabs.SelectedIndex = 0;
                Render(window, $"library-quick-prompts-{scale * 100:0}", 880, 580, scale);
                Assert.Equal(0, header.ActualHeight);
                Assert.True(tabs.TranslatePoint(new Point(), (UIElement)window.Content).Y <= 17,
                    "The collapsed management header must leave no empty band above the tabs.");
                foreach (var name in new[] { "PromptSearch", "PromptList", "PromptPreview", "PromptCopyButton" })
                    AssertInside((FrameworkElement)window.Content, (FrameworkElement)window.FindName(name));
                tabs.SelectedIndex = 1;
                Render(window, $"library-quick-models-{scale * 100:0}", 880, 580, scale);
                AssertInside((FrameworkElement)window.Content, (FrameworkElement)window.FindName("ModelSearch"));
                AssertInside((FrameworkElement)window.Content, (FrameworkElement)window.FindName("ModelCatalog"));
            }
            ApplyTheme(System.Windows.Application.Current, "Dark");
            Render(window, "library-quick-dark", 880, 580);
            ApplyTheme(System.Windows.Application.Current, "Light");

            tabs.SelectedIndex = 0;
            search.Text = "快捷";
            var selected = ((ListBox)window.FindName("PromptList")).SelectedItem;
            LibraryWindow.Open();
            Render(window, "library-management-header", 880, 580);
            Assert.True(header.IsVisible);
            Assert.Equal(WindowStyle.None, window.WindowStyle);
            AssertNoNativeCaption(window);
            Assert.True(((FrameworkElement)window.FindName("ManagementChrome")).IsVisible);
            VerifyWindowChromeInput(window);
            Assert.Equal(40, System.Windows.Shell.WindowChrome.GetWindowChrome(window).CaptionHeight);
            Assert.Equal("快捷", search.Text);
            Assert.Same(selected, ((ListBox)window.FindName("PromptList")).SelectedItem);

            var chrome = (FrameworkElement)window.FindName("ManagementChrome");
            var maximize = Visuals<System.Windows.Controls.Button>(chrome).Single(b => b.Name == "MaximizeButton");
            maximize.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
            PumpUntil(() => window.WindowState == WindowState.Maximized);
            AssertNoNativeCaption(window);
            maximize.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
            PumpUntil(() => window.WindowState == WindowState.Normal);
            Visuals<System.Windows.Controls.Button>(chrome).Single(b => b.Name == "MinimizeButton")
                .RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
            PumpUntil(() => window.WindowState == WindowState.Minimized);
            LibraryWindow.Open();
            Assert.Equal(WindowState.Normal, window.WindowState);
            Visuals<System.Windows.Controls.Button>(chrome).Single(b => b.Name == "CloseButton")
                .RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
            PumpUntil(() => !window.IsVisible);
            LibraryWindow.Open();
            Assert.Equal("快捷", search.Text);

            window.Hide();
            LibraryWindow.Toggle();
            Assert.Equal(Visibility.Collapsed, header.Visibility);
            Assert.Equal("快捷", search.Text);
            ((System.Windows.Controls.Button)window.FindName("PromptCopyButton"))
                .RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
            PumpUntil(() => !window.IsCopyPending);
            Assert.Equal("仅用于测试的完整正文", Assert.Single(copied));
            tabs.SelectedIndex = 1;
            window.Hide();
            window.ToggleVisibility();
            Assert.Equal(1, tabs.SelectedIndex);
            Assert.Equal(Visibility.Collapsed, header.Visibility);
            window.WindowState = WindowState.Minimized;
            LibraryWindow.Toggle();
            Assert.Equal(WindowState.Normal, window.WindowState);
            Assert.Equal(Visibility.Collapsed, header.Visibility);

            // 切换设置与快捷唤出时，资料库中未保存的正文仍须保留。
            typeof(LibraryWindow).GetMethod("NewPrompt_Click", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(window, [window, new RoutedEventArgs()]);
            ((TextBox)window.FindName("PromptBody")).Text = "unsaved-fixture";
            window.Hide();
            LibraryWindow.Toggle();
            Assert.Equal("unsaved-fixture", ((TextBox)window.FindName("PromptBody")).Text);
            Assert.Equal(Visibility.Visible, ((FrameworkElement)window.FindName("PromptEditor")).Visibility);
            Render(window, "library-shortcut-editor", 880, 580);
            AssertNoNativeCaption(window);
            var close = (System.Windows.Controls.Button)window.FindName("QuickCloseButton");
            Assert.True(close.IsVisible);
            Assert.Equal(32, close.ActualWidth);
            var root = (FrameworkElement)window.Content;
            var hit = root.InputHitTest(close.TranslatePoint(new Point(16, 16), root));
            Assert.True(ReferenceEquals(hit, close) ||
                (hit is System.Windows.DependencyObject dependency && IsDescendantOf(dependency, close)),
                $"The editor must not cover the borderless close button (hit: {(hit as FrameworkElement)?.Name}).");
            close.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
            Assert.False(window.IsVisible);
        }
        finally
        {
            ApplyTheme(System.Windows.Application.Current, "Light");
            current.SetValue(null, previous);
            window.CloseForTests();
            System.IO.Directory.Delete(directory, true);
        }

        var ocr = new OverTranslate.Views.Settings.ScreenTranslateSettingsWindow
        { WindowStartupLocation = WindowStartupLocation.Manual, Left = -10000, Top = -10000, ShowActivated = false };
        try
        {
            ocr.Show(); ocr.UpdateLayout();
            VerifyWindowChromeInput(ocr);
            Assert.Equal(WindowStyle.None, ocr.WindowStyle);
            AssertNoNativeCaption(ocr);
            Assert.Equal(40, System.Windows.Shell.WindowChrome.GetWindowChrome(ocr).CaptionHeight);
            Render(ocr, "ocr-settings-custom-chrome", 740, 760);
            Visuals<System.Windows.Controls.Button>((FrameworkElement)ocr.Content).Single(b => b.Name == "CloseButton")
                .RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
            PumpUntil(() => !ocr.IsVisible);
        }
        finally { ocr.Close(); }
    }

}
