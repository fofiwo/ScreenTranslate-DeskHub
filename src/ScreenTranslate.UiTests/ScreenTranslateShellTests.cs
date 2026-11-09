using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using OverTranslate.Services;
using OverTranslate.Models;
using OverTranslate.Views.Settings;
using OverTranslate.Views.Shell;
using OverTranslate.Views.Library;
using OverTranslate.Services.Library;
using Xunit;

namespace OverTranslate.Tests;

[CollectionDefinition("ScreenTranslate shell", DisableParallelization = true)]
public sealed class ScreenTranslateShellCollection;

[Collection("ScreenTranslate shell")]
public sealed partial class ScreenTranslateShellTests
{
    [Fact]
    public void ProductShellStartsWithCaptureAndKeepsAllDestinationsReachable()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
            // 只加载生产资源，避免首次消息泵触发 App.OnStartup 的单实例退出逻辑。
            var app = new System.Windows.Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            ShellWindow? shell = null;
            var libraryDirectory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "shell-library-" + Guid.NewGuid());
            try
            {
                // Load the real product resources, but never run App.OnStartup or desktop capture.
                foreach (var source in new[] { "Themes/LightTheme.xaml", "Themes/SharedStyles.xaml", "Resources/Strings.zh-Hans.xaml", "Themes/ProductStyles.xaml" })
                    app.Resources.MergedDictionaries.Add(new ResourceDictionary
                    {
                        Source = new Uri("pack://application:,,,/ScreenTranslate;component/" + source, UriKind.Absolute)
                    });
                app.Resources.MergedDictionaries[2] = new ResourceDictionary
                {
                    Source = new Uri("pack://application:,,,/ScreenTranslate;component/Resources/Strings.zh-Hans.xaml", UriKind.Absolute)
                };
                ApplyTheme(app, "Light");
                if (Environment.GetEnvironmentVariable("SCREEN_TRANSLATE_LIBRARY_QUICK_ACCESS_ONLY") == "1")
                {
                    VerifyLibraryQuickAccess();
                    return;
                }
                if (Environment.GetEnvironmentVariable("SCREEN_TRANSLATE_SYNC_SETUP_ONLY") == "1")
                {
                    VerifySyncSetupDialog();
                    return;
                }
                if (Environment.GetEnvironmentVariable("SCREEN_TRANSLATE_MULTITOOL_ONLY") == "1")
                {
                    VerifyShortcutHintsMatchBindings();
                    VerifyScreenshotToolActions();
                    VerifyMultitoolNavigation();
                    shell = new ShellWindow(() => new LibraryState(), () => { });
                    VerifyQuickToolSettings(shell);
                    VerifyDpiLayout(shell);
                    VerifyUpdateEntries();
                    return;
                }
                if (Environment.GetEnvironmentVariable("SCREEN_TRANSLATE_CLIPBOARD_INTERACTION_ONLY") == "1")
                {
                    VerifyClipboardClickPaste();
                    if (Environment.GetEnvironmentVariable("SCREEN_TRANSLATE_CLIPBOARD_NATIVE") == "1") VerifyNativeClipboardPaste();
                    return;
                }
                VerifySnippingTools();
                if (Environment.GetEnvironmentVariable("SCREEN_TRANSLATE_SNIP_ONLY") == "1") return;
                if (Environment.GetEnvironmentVariable("SCREEN_TRANSLATE_LAYOUT_ONLY") == "1")
                {
                    shell = new ShellWindow(() => new LibraryState(), () => { });
                    VerifyQuickToolSettings(shell);
                    VerifyShortcutRecording();
                    VerifyOcrSaveFailure();
                    VerifyDpiLayout(shell);
                    WithReviewLibrary((window, _, _) =>
                    {
                        Render(window, "library-tabs-organized", 880, 580);
                        var libraryTabs = (TabControl)window.FindName("Tabs");
                        libraryTabs.SelectedIndex = 1;
                        Render(window, "library-models-organized", 880, 580);
                        Assert.Equal(FontWeights.Normal, ((TextBox)window.FindName("ModelSearch")).FontWeight);
                    });
                    VerifySettingsMigrationReview();
                    return;
                }
                if (Environment.GetEnvironmentVariable("SCREEN_TRANSLATE_SETTINGS_REVIEW_ONLY") == "1")
                {
                    VerifySettingsMigrationReview();
                    return;
                }
                if (Environment.GetEnvironmentVariable("SCREEN_TRANSLATE_QUICK_TOOLS_ONLY") == "1")
                {
                    shell = new ShellWindow(() => new LibraryState(), () => { });
                    VerifyQuickToolSettings(shell);
                    VerifyShortcutRecording();
                    VerifyDeletedPromptEditorKeepsInput();
                    VerifyLanTransferSettings();
                    return;
                }
                if (Environment.GetEnvironmentVariable("SCREEN_TRANSLATE_UPDATE_ONLY") == "1")
                {
                    VerifyUpdateEntries();
                    return;
                }
                if (Environment.GetEnvironmentVariable("SCREEN_TRANSLATE_CLIPBOARD_PIN_ONLY") == "1")
                {
                    VerifyClipboardWindowPinToggle();
                    return;
                }
                if (Environment.GetEnvironmentVariable("SCREEN_TRANSLATE_CLIPBOARD_POPUP_ONLY") == "1")
                {
                    VerifySystemClipboardHistory();
                    return;
                }
                VerifyLibraryQuickAccess();
                VerifyShortcutHintsMatchBindings();
                VerifyScreenshotToolActions();
                VerifyMultitoolNavigation();
                LocalizationService.Apply("en");
                ThemeService.Apply("Dark");
                Assert.Equal("zh-Hans", LocalizationService.Current);
                Assert.Equal("Light", ThemeService.Current);
                Assert.Contains(app.Resources.MergedDictionaries, d => d.Source?.OriginalString.EndsWith("LightTheme.xaml") == true);
                var libraryStore = new LibraryStore(System.IO.Path.Combine(libraryDirectory, "library.json"));
                var openedLibrary = 0;
                shell = new ShellWindow(libraryStore.Read, () => openedLibrary++);
                VerifyLibraryLauncher(shell, libraryStore, libraryDirectory);
                ((System.Windows.Controls.Button)shell.FindName("LibraryBtn")).RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
                Assert.Equal(1, openedLibrary);
                using (var trayIcon = AppIconService.CreateTrayIcon())
                {
                    Assert.Equal(32, trayIcon.Width);
                    Assert.Equal(32, trayIcon.Height);
                }
                var host = (Border)shell.FindName("ContentHost");
                Assert.IsType<HomePage>(host.Child);
                Assert.True(Assert.IsType<RadioButton>(shell.FindName("TranslationNav")).IsChecked);
                Assert.IsType<RadioButton>(shell.FindName("ScreenshotNav"));
                Assert.Null(shell.FindName("ProfilesNav"));
                Assert.Null(shell.FindName("PinBtn"));
                Assert.NotNull(shell.FindName("OcrExtractBtn"));
                Assert.Null(Field(shell, "_realtimePage"));
                Assert.Null(Field(shell, "_settingsPage"));
                Assert.Null(Field(shell, "_preferencesPage"));
                Render(shell, "home");
                var home = Assert.IsType<HomePage>(host.Child);
                Assert.True(((System.Windows.Controls.Button)home.FindName("StartCaptureButton")).ActualHeight >= 44);
                Assert.IsType<string>(((ComboBox)home.FindName("SourceBox")).SelectedValue);
                Assert.IsType<TranslationProvider>(((ComboBox)home.FindName("ProviderBox")).SelectedValue);
                var target = (ComboBox)home.FindName("TargetBox");
                target.SelectedValue = "EN-US";
                Assert.Equal("EN-US", SettingsService.Instance.Current.TargetLanguage);
                target.SelectedValue = "ZH-HANS";
                var provider = (ComboBox)home.FindName("ProviderBox");
                provider.SelectedValue = TranslationProvider.Google;
                Assert.Equal(TranslationProvider.Google, SettingsService.Instance.Current.Provider);
                provider.SelectedValue = TranslationProvider.Microsoft;
                Render(shell, "home-minimum", 1100, 640);
                AssertCaptureOrder(home);

                // 显式设置本项验收的前提，不依赖上次测试输出目录里残留的偏好。
                SettingsService.Instance.Current.Realtime.CaptureMode = RealtimeCaptureMode.Window;
                shell.Navigate(ShellPage.Realtime);
                var realtime = Assert.IsType<OverTranslate.Views.Realtime.RealtimePage>(host.Child);
                Assert.True(((System.Windows.Controls.RadioButton)realtime.FindName("WindowModeRadio")).IsChecked);
                Assert.Null(realtime.FindName("BorderRandomRadio"));
                VerifyRealtimeBorderUpdates(realtime);

                shell.ShowOcrSettings();
                var preferences = Assert.IsType<ScreenTranslatePreferencesPage>(host.Child);
                var tabs = (TabControl)preferences.FindName("Tabs");
                Assert.Equal(0, tabs.SelectedIndex);
                var ocr = Assert.IsType<OcrSettingsPage>(((ContentControl)preferences.FindName("OcrHost")).Content);
                Assert.Null(Field(shell, "_settingsPage"));
                Render(shell, "ocr");
                var ocrProvider = (ComboBox)ocr.FindName("OcrProviderBox");
                Assert.NotNull(ocrProvider.SelectedItem);
                ocrProvider.SelectedIndex = 1;
                Assert.Equal(Visibility.Visible, ((FrameworkElement)ocr.FindName("HttpFields")).Visibility);
                Assert.Equal(Visibility.Collapsed, ((FrameworkElement)ocr.FindName("BaiduFields")).Visibility);
                Render(shell, "ocr-http");
                ocrProvider.SelectedIndex = 2;
                Assert.Equal(Visibility.Visible, ((FrameworkElement)ocr.FindName("BaiduFields")).Visibility);
                Assert.Equal(Visibility.Collapsed, ((FrameworkElement)ocr.FindName("HttpFields")).Visibility);
                Render(shell, "ocr-baidu");
                ocr.Reload();

                tabs.SelectedIndex = 1;
                Render(shell, "services");
                tabs.SelectedIndex = 3;
                var advanced = Assert.IsType<SettingsPage>(((ContentControl)preferences.FindName("AdvancedHost")).Content);
                var formatting = (System.Windows.Controls.CheckBox)advanced.FindName("PreserveFormattingCheckBox");
                var fallback = (System.Windows.Controls.CheckBox)advanced.FindName("AllowFallbackCheckBox");
                formatting.IsChecked = false;
                fallback.IsChecked = true;
                Assert.False(SettingsService.Instance.Current.OcrPreserveFormatting);
                Assert.True(SettingsService.Instance.Current.AllowFallbackServices);
                advanced.Reload();
                Assert.False(formatting.IsChecked);
                Assert.True(fallback.IsChecked);
                formatting.IsChecked = true;
                fallback.IsChecked = false;
                Assert.Equal(8, ((StackPanel)advanced.FindName("HotkeyRows")).Children.Count);
                Render(shell, "advanced");
                VerifyQuickToolSettings(shell);
                VerifySettingsMigrationReview();
                VerifyShortcutRecording();
                VerifyOcrSaveFailure();
                VerifyDpiLayout(shell);

                var control = new OverTranslate.Views.Realtime.RealtimeControlWindow(new System.Drawing.Rectangle(0, 0, 1920, 1080));
                try
                {
                    control.SetActualServices(new[] {
                        new TranslatedBlock("a", "译文", Rect.Empty) { ActualService = "备用服务" },
                        new TranslatedBlock("b", "原文", Rect.Empty) { ActualService = "失败服务", Untranslated = true },
                    });
                    var label = (TextBlock)control.FindName("ActualServicesText");
                    Assert.Equal("服务：备用服务", label.Text);
                    control.SetActualServices([]);
                    Assert.Equal("服务：等待翻译", label.Text);
                }
                finally { control.Close(); }

                shell.Navigate(ShellPage.Realtime);
                Assert.IsType<OverTranslate.Views.Realtime.RealtimePage>(host.Child);
                Render(shell, "continuous");
                shell.Navigate(ShellPage.About);
                Assert.IsType<ProductAboutPage>(host.Child);
                Render(shell, "about");
                shell.Navigate(ShellPage.Home);
                Assert.IsType<HomePage>(host.Child);
                ApplyTheme(app, "Light");
                Render(shell, "home-light");
                AssertCaptureOrder(Assert.IsType<HomePage>(host.Child));
                Render(shell, "home-light-minimum", 1100, 680);
                shell.ShowOcrSettings();
                ocrProvider.SelectedIndex = 0;
                Render(shell, "ocr-light");
                ocrProvider.SelectedIndex = 1;
                Render(shell, "ocr-http-light");
                ocrProvider.SelectedIndex = 2;
                Render(shell, "ocr-baidu-light", 1100, 680);
                ocr.Reload();
                tabs.SelectedIndex = 1;
                Render(shell, "services-light");
                shell.Navigate(ShellPage.Realtime);
                Render(shell, "continuous-light");
                VerifyHiddenShellReopens(shell);
                VerifySystemClipboardHistory();
                VerifyLibraryWindow();
                VerifyDeletedPromptEditorKeepsInput();
                shell.Close(); shell = null;
                Assert.Null(ShellWindow.Current);
            }
            catch (Exception ex) { failure = ex; }
            finally
            {
                shell?.Close(); app.Shutdown(); Dispatcher.CurrentDispatcher.InvokeShutdown();
                if (System.IO.Directory.Exists(libraryDirectory)) System.IO.Directory.Delete(libraryDirectory, true);
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(60)), "Product shell test timed out.");
        if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }

    private static void VerifyLibraryLauncher(ShellWindow shell, LibraryStore store, string directory)
    {
        var summary = (TextBlock)shell.FindName("LibrarySummaryText");
        var hotkey = (TextBlock)shell.FindName("LibraryHotkeyText");
        var entry = (System.Windows.Controls.Button)shell.FindName("LibraryBtn");
        Assert.Equal("提示词 0 · 连接 0", summary.Text);
        store.Update(s =>
        {
            s.Prompts = Enumerable.Range(0, 12).Select(i => new LibraryPrompt { Title = $"示例 {i}" }).ToList();
            s.Connections = Enumerable.Range(0, 12).Select(i => new LibraryConnection { Platform = $"本地 {i}" }).ToList();
        });
        typeof(Window).GetMethod("OnActivated", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(shell, [EventArgs.Empty]);
        Assert.Equal("提示词 12 · 连接 12", summary.Text);
        Render(shell, "shell-library-entry", 1220, 800);
        Render(shell, "shell-library-entry-minimum", 1100, 680);
        AssertInside((FrameworkElement)shell.Content, entry);
        AssertInside((FrameworkElement)shell.Content, summary);
        var root = (FrameworkElement)shell.Content;
        var summaryBounds = summary.TransformToAncestor(root).TransformBounds(new Rect(summary.RenderSize));
        var version = (TextBlock)shell.FindName("VersionText");
        var versionBounds = version.TransformToAncestor(root).TransformBounds(new Rect(version.RenderSize));
        Assert.True(summaryBounds.Bottom < versionBounds.Top, "Library statistics must stay above the sidebar footer.");
        var settings = SettingsService.Instance.Current;
        var enabled = settings.LibraryHotkeyEnabled;
        var display = settings.LibraryHotkeyDisplay;
        try
        {
            settings.LibraryHotkeyEnabled = true;
            settings.LibraryHotkeyDisplay = "Ctrl+Alt+Shift+OemOpenBrackets";
            shell.RefreshHotkeyHints();
            Render(shell, "shell-library-long-shortcut-minimum", 1100, 680);
            Assert.Equal("Ctrl+Alt+Shift+OemOpenBrackets", hotkey.Text);
            Assert.NotNull(entry.ToolTip);
            settings.LibraryHotkeyEnabled = false;
            shell.RefreshHotkeyHints();
            Assert.Equal("", hotkey.Text);
            Assert.True(entry.IsEnabled);
        }
        finally { settings.LibraryHotkeyEnabled = enabled; settings.LibraryHotkeyDisplay = display; shell.RefreshHotkeyHints(); }
        var path = System.IO.Path.Combine(directory, "library.json");
        var saved = System.IO.File.ReadAllBytes(path);
        try
        {
            foreach (var damaged in new[] { "{", "{\"SchemaVersion\":2}" })
            {
                System.IO.File.WriteAllText(path, damaged);
                shell.RefreshLibrarySummary();
                Assert.Equal("资料暂不可用，请打开检查", summary.Text);
                Assert.True(entry.IsEnabled);
            }
        }
        finally { System.IO.File.WriteAllBytes(path, saved); }
        store.Update(s => s.Prompts.RemoveAt(0));
        shell.RefreshLibrarySummary();
        Assert.Equal("提示词 11 · 连接 12", summary.Text);
    }

    private static void ApplyTheme(System.Windows.Application app, string theme)
    {
        app.Resources.MergedDictionaries[0] = new ResourceDictionary
        {
            Source = new Uri($"pack://application:,,,/ScreenTranslate;component/Themes/{theme}Theme.xaml", UriKind.Absolute)
        };
    }

    private static void VerifyHiddenShellReopens(ShellWindow shell)
    {
        shell.Show();
        shell.Navigate(ShellPage.Home);
        var host = (Border)shell.FindName("ContentHost");
        var entries = new (Type Owner, string Method, Type Page)[]
        {
            (typeof(OverTranslate.Views.MainWindow), "OnTrayLeftClick", typeof(HomePage)),
            (typeof(OverTranslate.Views.MainWindow), "OpenSettings", typeof(ScreenTranslatePreferencesPage)),
            (typeof(OverTranslate.App), "ShowOrActivateShell", typeof(ScreenTranslatePreferencesPage)),
        };
        foreach (var entry in entries)
        {
            // 独立 OCR 结束后窗口仍存活但隐藏，所有手动入口都应恢复同一窗口。
            shell.Hide();
            Assert.False(shell.IsVisible);
            entry.Owner.GetMethod(entry.Method, BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, null);
            Assert.Same(shell, ShellWindow.Current);
            Assert.True(shell.IsVisible, $"{entry.Method} must show the hidden shell.");
            Assert.IsType(entry.Page, host.Child);
        }

        shell.WindowState = WindowState.Minimized;
        ShellWindow.ShowOrActivate();
        Assert.True(shell.IsVisible);
        Assert.Equal(WindowState.Normal, shell.WindowState);
        Assert.IsType<ScreenTranslatePreferencesPage>(host.Child);
        shell.Hide();
        shell.Navigate(ShellPage.Home);
    }

    private static void VerifyQuickToolSettings(ShellWindow shell)
    {
        shell.ShowQuickToolSettings();
        var preferences = Assert.IsType<ScreenTranslatePreferencesPage>(((Border)shell.FindName("ContentHost")).Child);
        var tabs = (TabControl)preferences.FindName("Tabs");
        Assert.Equal("快捷键", ((TabItem)tabs.SelectedItem).Header);
        var quick = Assert.IsType<SettingsPage>(((ContentControl)preferences.FindName("QuickToolsHost")).Content);
        Assert.Equal(Visibility.Visible, ((FrameworkElement)quick.FindName("HotkeySection")).Visibility);
        Assert.Equal(Visibility.Collapsed, ((FrameworkElement)quick.FindName("AdvancedOptions")).Visibility);
        var quickRows = ((StackPanel)quick.FindName("HotkeyRows")).Children.Cast<Grid>().ToArray();
        Assert.Equal(8, quickRows.Length);
        Assert.All(quickRows, row => Assert.Equal(Visibility.Visible, row.Visibility));
        Assert.Contains(quickRows, row => row.Children.OfType<TextBox>().Any(box => Equals(box.Tag, HotkeyAction.ClipboardHistory)));
        Render(shell, "shortcuts", 1100, 680);
        Assert.Equal(FontWeights.Normal, ((TextBlock)quick.FindName("HotkeyStatus")).FontWeight);
        tabs.SelectedIndex = 3;
        var advanced = Assert.IsType<SettingsPage>(((ContentControl)preferences.FindName("AdvancedHost")).Content);
        Assert.Equal(Visibility.Collapsed, ((FrameworkElement)advanced.FindName("HotkeySection")).Visibility);
        Assert.Equal(Visibility.Visible, ((FrameworkElement)advanced.FindName("AdvancedOptions")).Visibility);
        Render(shell, "general", 1100, 680);
        tabs.SelectedIndex = 1;
        var services = Assert.IsType<SettingsPage>(((ContentControl)preferences.FindName("ServiceOptionsHost")).Content);
        Assert.Equal(Visibility.Visible, ((FrameworkElement)services.FindName("ServiceOptions")).Visibility);
        Assert.Equal(Visibility.Collapsed, ((FrameworkElement)services.FindName("AdvancedOptions")).Visibility);
        Render(shell, "services-organized", 1100, 680);
        shell.ShowOcrSettings();
        var ocr = Assert.IsType<OcrSettingsPage>(((ContentControl)preferences.FindName("OcrHost")).Content);
        Assert.Null(ocr.FindName("LiveOverlayBox"));
        var recognition = Assert.IsType<SettingsPage>(((ContentControl)ocr.FindName("RecognitionOptionsHost")).Content);
        Assert.Equal(Visibility.Visible, ((FrameworkElement)recognition.FindName("OcrOptions")).Visibility);
        Render(shell, "ocr-organized", 1100, 680);
        shell.Navigate(ShellPage.Home);
        var home = Assert.IsType<HomePage>(((Border)shell.FindName("ContentHost")).Child);
        var capture = Assert.IsType<SettingsPage>(((ContentControl)home.FindName("CaptureOptionsHost")).Content);
        Assert.Equal(Visibility.Visible, ((FrameworkElement)capture.FindName("CaptureOptions")).Visibility);
        Assert.Equal(Visibility.Collapsed, ((FrameworkElement)capture.FindName("OcrOptions")).Visibility);
        var overlay = (System.Windows.Controls.CheckBox)capture.FindName("LiveOverlayCheckBox");
        var oldOverlay = SettingsService.Instance.Current.LiveCaptureOverlay;
        try
        {
            overlay.IsChecked = false;
            Assert.Contains("冻结截图", ((TextBlock)home.FindName("StatusText")).Text);
            overlay.IsChecked = true;
            Assert.Contains("原位覆盖", ((TextBlock)home.FindName("StatusText")).Text);
        }
        finally { overlay.IsChecked = oldOverlay; }
        Render(shell, "home-organized", 1100, 680);
        shell.ShowQuickToolSettings();
        Assert.Same(quick, ((ContentControl)preferences.FindName("QuickToolsHost")).Content);
    }

    private static object? Field(object target, string name) => target.GetType()
        .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(target);

    private static void VerifyShortcutRecording()
    {
        var states = new List<bool>();
        bool saveSucceeds = false;
        var page = (SettingsPage)Activator.CreateInstance(typeof(SettingsPage),
            BindingFlags.Instance | BindingFlags.NonPublic, null, [new Action<bool>(states.Add), new Func<bool>(() => saveSucceeds)], null)!;
        using var source = new System.Windows.Interop.HwndSource(new System.Windows.Interop.HwndSourceParameters("ShortcutRecordingTests")
        { Width = 800, Height = 600, WindowStyle = 0 }) { RootVisual = page };
        var rows = (StackPanel)page.FindName("HotkeyRows");
        var box = rows.Children.Cast<Grid>().SelectMany(row => row.Children.OfType<TextBox>())
            .Single(text => Equals(text.Tag, HotkeyAction.OcrExtract));
        void Click() => box.RaiseEvent(new System.Windows.Input.MouseButtonEventArgs(System.Windows.Input.Mouse.PrimaryDevice, 0,
            System.Windows.Input.MouseButton.Left) { RoutedEvent = UIElement.PreviewMouseLeftButtonDownEvent });
        void Press(System.Windows.Input.Key key) => box.RaiseEvent(new System.Windows.Input.KeyEventArgs(
            System.Windows.Input.Keyboard.PrimaryDevice, source, 0, key) { RoutedEvent = System.Windows.Input.Keyboard.PreviewKeyDownEvent });
        var settings = SettingsService.Instance.Current;
        var oldCapture = (settings.HotkeyModifiers, settings.HotkeyVirtualKey, settings.HotkeyDisplay);
        var oldOcr = (settings.OcrHotkeyModifiers, settings.OcrHotkeyVirtualKey, settings.OcrHotkeyDisplay);
        try
        {
            settings.HotkeyModifiers = 0; settings.HotkeyVirtualKey = 0x77; settings.HotkeyDisplay = "F8";
            Press(System.Windows.Input.Key.Enter);
            Assert.Equal(new[] { true }, states);
            Press(System.Windows.Input.Key.F8);
            Assert.Contains("冲突", ((TextBlock)page.FindName("HotkeyStatus")).Text);
            Assert.Equal(new[] { true }, states);
            Press(System.Windows.Input.Key.F9);
            Assert.Equal(0x78u, settings.OcrHotkeyVirtualKey);
            Assert.Equal(new[] { true, false }, states);
            Assert.Contains("保存失败", ((TextBlock)page.FindName("SaveStatus")).Text);
            var retry = (System.Windows.Controls.Button)page.FindName("SaveRetryBtn");
            Assert.Equal(Visibility.Visible, retry.Visibility);
            saveSucceeds = true;
            retry.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
            Assert.Empty(((TextBlock)page.FindName("SaveStatus")).Text);
            Assert.Equal(Visibility.Collapsed, retry.Visibility);
            Press(System.Windows.Input.Key.Enter); Press(System.Windows.Input.Key.Escape);
            Assert.Equal(new[] { true, false, true, false }, states);
            Click(); Press(System.Windows.Input.Key.Tab);
            Assert.Equal(new[] { true, false, true, false, true, false }, states);
        }
        finally
        {
            (settings.HotkeyModifiers, settings.HotkeyVirtualKey, settings.HotkeyDisplay) = oldCapture;
            (settings.OcrHotkeyModifiers, settings.OcrHotkeyVirtualKey, settings.OcrHotkeyDisplay) = oldOcr;
        }
    }

    private static void VerifyOcrSaveFailure()
    {
        var settings = SettingsService.Instance.Current;
        var previous = settings.Ocr;
        bool succeeds = false;
        var page = new OcrSettingsPage(() => succeeds);
        try
        {
            ((ComboBox)page.FindName("OcrProviderBox")).SelectedIndex = 0;
            ((TextBox)page.FindName("TimeoutBox")).Text = "37";
            var save = typeof(OcrSettingsPage).GetMethod("SaveOptions", BindingFlags.NonPublic | BindingFlags.Instance)!;
            Assert.False((bool)save.Invoke(page, null)!);
            Assert.Same(previous, settings.Ocr);
            Assert.Contains("保存失败", ((TextBlock)page.FindName("StatusText")).Text);
            Assert.Equal("37", ((TextBox)page.FindName("TimeoutBox")).Text);
            typeof(OcrSettingsPage).GetMethod("ClearCredentials_Click", BindingFlags.NonPublic | BindingFlags.Instance)!
                .Invoke(page, [page, new RoutedEventArgs()]);
            Assert.Contains("清除凭据保存失败", ((TextBlock)page.FindName("StatusText")).Text);
            succeeds = true;
            Assert.True((bool)save.Invoke(page, null)!);
            Assert.Equal(37, settings.Ocr.TimeoutSeconds);
            Assert.Contains("已保存", ((TextBlock)page.FindName("StatusText")).Text);
        }
        finally { settings.Ocr = previous; page.Teardown(); }
    }

    private static void AssertCaptureOrder(HomePage home)
    {
        var source = (ComboBox)home.FindName("SourceBox");
        var start = (System.Windows.Controls.Button)home.FindName("StartCaptureButton");
        var sourceBottom = source.TransformToAncestor(home).Transform(new Point(0, source.ActualHeight)).Y;
        var startTop = start.TransformToAncestor(home).Transform(new Point()).Y;
        Assert.True(sourceBottom < startTop, "Choose the translation languages before starting capture.");
        Assert.True(start.ActualHeight >= 44);
        Assert.True(startTop + start.ActualHeight <= home.ActualHeight, "The primary action must be visible without scrolling.");
    }

    private static void VerifyDpiLayout(ShellWindow shell)
    {
        try
        {
            foreach (var scale in new[] { 1.0, 1.25, 1.5, 2.0 })
            {
                shell.Navigate(ShellPage.Home);
                var home = Assert.IsType<HomePage>(((Border)shell.FindName("ContentHost")).Child);
                Render(shell, $"home-dpi-{scale * 100:0}", 1100, 680, scale);
                AssertCaptureOrder(home);
                Assert.Equal(scale, VisualTreeHelper.GetDpi(home).DpiScaleX, 2);
                shell.ShowQuickToolSettings();
                Render(shell, $"shortcuts-dpi-{scale * 100:0}", 1100, 680, scale);
                var preferences = Assert.IsType<ScreenTranslatePreferencesPage>(((Border)shell.FindName("ContentHost")).Child);
                var tabs = (TabControl)preferences.FindName("Tabs");
                foreach (TabItem tab in tabs.Items)
                {
                    var bounds = tab.TransformToAncestor(tabs).TransformBounds(new Rect(tab.RenderSize));
                    Assert.True(bounds.Left >= 0 && bounds.Right <= tabs.ActualWidth + 1);
                    Assert.True(bounds.Top < 1, "Settings tabs must fit on one row at every DPI.");
                }
                var page = (SettingsPage)((ContentControl)preferences.FindName("QuickToolsHost")).Content;
                foreach (Grid row in ((StackPanel)page.FindName("HotkeyRows")).Children)
                {
                    var field = row.Children.OfType<TextBox>().Single();
                    var label = row.Children.OfType<System.Windows.Controls.CheckBox>().Single();
                    var labelRight = label.TransformToAncestor(row).Transform(new Point(label.ActualWidth, 0)).X;
                    var fieldLeft = field.TransformToAncestor(row).Transform(new Point()).X;
                    Assert.True(labelRight <= fieldLeft + 1, "Shortcut names must not overlap their recording fields.");
                }
            }
        }
        finally { SetVisualDpi((FrameworkElement)shell.Content, 1); }
    }

    private static void SetVisualDpi(Visual visual, double scale)
    {
        // 已显示窗口的内容根仍有 Window 父级；仅对真正的视觉树根更新 DPI。
        while (VisualTreeHelper.GetParent(visual) is Visual parent) visual = parent;
        VisualTreeHelper.SetRootDpi(visual, new DpiScale(scale, scale));
    }

    private static void Render(Window shell, string name, int width = 1220, int height = 760, double dpiScale = 1)
    {
        // Render the actual WPF tree as test evidence; this does not automate the user's desktop.
        if (shell is LibraryWindow) { shell.Width = width; shell.Height = height; }
        var root = (FrameworkElement)shell.Content;
        SetVisualDpi(root, dpiScale);
        root.Measure(new Size(width, height));
        root.Arrange(new Rect(0, 0, width, height));
        root.UpdateLayout();
        if (shell is LibraryWindow) Assert.True(root.ActualWidth <= width + 1 && root.ActualHeight <= height + 1, "The rendered library must fit the requested viewport, including after showing its HWND.");
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(width * dpiScale), (int)Math.Ceiling(height * dpiScale), 96 * dpiScale, 96 * dpiScale, PixelFormats.Pbgra32);
        bitmap.Render(root);
        var directory = System.IO.Path.Combine(AppContext.BaseDirectory, "ui-evidence");
        System.IO.Directory.CreateDirectory(directory);
        using var output = System.IO.File.Create(System.IO.Path.Combine(directory, name + ".png"));
        var png = new PngBitmapEncoder();
        png.Frames.Add(BitmapFrame.Create(bitmap));
        png.Save(output);
    }

    private sealed class CatalogHandler : System.Net.Http.HttpMessageHandler
    {
        protected override Task<System.Net.Http.HttpResponseMessage> SendAsync(System.Net.Http.HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.OK)
            { Content = new System.Net.Http.StringContent("{\"data\":[{\"id\":\"fresh-a\"},{\"id\":\"fresh-b\"}]}") });
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr window, int message, IntPtr wparam, IntPtr lparam);

    private static IEnumerable<T> Visuals<T>(DependencyObject parent) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T found) yield return found;
            foreach (var next in Visuals<T>(child)) yield return next;
        }
    }

    private static void VerifyLibraryWindow()
    {
        var directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "library-ui-" + Guid.NewGuid());
        LibraryWindow? window = null;
        try
        {
            var store = new LibraryStore(System.IO.Path.Combine(directory, "library.json"));
            store.SavePrompt(new() { Title = "开发助手", Description = "用于整理 WPF 开发任务", Content = "请检查这段 WPF 代码。\n保留完整换行。", Tags = ["开发", "WPF"] });
            var connection = store.SaveConnection(new() { Platform = "本地模型", Endpoint = "http://localhost:11434/v1", Model = "fixed-id", RemainingBalance = "本地", Note = "用于界面验收的虚构资料" }, fetchedModelIds: ["cached-a", "cached-b"]);
            var copied = new List<string>();
            var failModelCopy = false;
            window = new LibraryWindow(store, new LibraryHttp(new CatalogHandler()), value =>
            {
                if (failModelCopy) throw new InvalidOperationException("fixture clipboard busy");
                copied.Add(value);
            }) { WindowStartupLocation = WindowStartupLocation.Manual, Left = -10000, Top = -10000, ShowActivated = false };
            window.Show();
            Render(window, "library-prompts", 1120, 740);
            var tabs = (TabControl)window.FindName("Tabs");
            Assert.Equal(2, tabs.Items.Count);
            Assert.Equal(Dock.Top, tabs.TabStripPlacement);
            var promptTab = (TabItem)tabs.Items[0]; var modelTab = (TabItem)tabs.Items[1];
            var promptBounds = new Rect(promptTab.TranslatePoint(new Point(), tabs), promptTab.RenderSize);
            var modelBounds = new Rect(modelTab.TranslatePoint(new Point(), tabs), modelTab.RenderSize);
            Assert.True(Math.Min(promptBounds.Bottom, modelBounds.Bottom) - Math.Max(promptBounds.Top, modelBounds.Top)
                > Math.Min(promptBounds.Height, modelBounds.Height) * 0.8, "Tabs must occupy the same horizontal row.");
            Assert.True(promptBounds.Left < modelBounds.Left && promptBounds.Right < modelBounds.Right, "Prompt and model tabs must be ordered left to right.");
            var preview = (TextBox)window.FindName("PromptPreview");
            Assert.True(preview.IsReadOnly);
            Assert.Equal("请检查这段 WPF 代码。\n保留完整换行。", preview.Text);
            Assert.Empty(copied);
            var list = (ListBox)window.FindName("PromptList");
            var row = (ListBoxItem)list.ItemContainerGenerator.ContainerFromIndex(0);
            var title = Visuals<TextBlock>(row).First(x => x.Text == "开发助手");
            list.RaiseEvent(new System.Windows.Input.MouseButtonEventArgs(System.Windows.Input.Mouse.PrimaryDevice, 0, System.Windows.Input.MouseButton.Left) { RoutedEvent = UIElement.PreviewMouseLeftButtonUpEvent, Source = title });
            PumpUntil(() => !window.IsCopyPending);
            Assert.Equal("请检查这段 WPF 代码。\n保留完整换行。", copied.Last());
            Assert.Equal("已复制", ((System.Windows.Controls.Button)window.FindName("PromptCopyButton")).Content);
            Assert.Equal("已复制", ((TextBlock)window.FindName("CopyToastText")).Text);
            Assert.True(((System.Windows.Controls.Primitives.Popup)window.FindName("CopyToast")).IsOpen);
            var beforeViewing = copied.Count;
            preview.Select(0, 2);
            Assert.Equal(beforeViewing, copied.Count);
            var search = (TextBox)window.FindName("PromptSearch");
            search.Text = "不存在";
            Assert.Empty(((ListBox)window.FindName("PromptList")).Items);
            search.Text = "wpf";
            Assert.Single(((ListBox)window.FindName("PromptList")).Items);
            search.Text = "";
            void Click(string label) => Visuals<System.Windows.Controls.Button>((DependencyObject)window!.Content).First(b => Equals(b.Content, label)).RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
            Click("新建提示词");
            ((TextBox)window.FindName("PromptTitle")).Text = "新建验收";
            ((TextBox)window.FindName("PromptBody")).Text = "新增正文\n不应自动调用外部服务";
            ((TextBox)window.FindName("PromptTags")).Text = "验收，开发";
            Render(window, "library-prompt-editor", 1120, 740);
            ((System.Windows.Controls.Button)window.FindName("SaveButton")).RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
            PumpUntil(() => ((FrameworkElement)window.FindName("EditorLayer")).Visibility == Visibility.Collapsed);
            Assert.Equal(2, new LibraryStore(System.IO.Path.Combine(directory, "library.json")).Read().Prompts.Count);
            tabs.SelectedIndex = 1;
            Render(window, "library-models", 1120, 740);
            Click("复制模型 ID");
            PumpUntil(() => !window.IsCopyPending);
            Assert.Equal("fixed-id", copied.Last());
            var chooser = (ComboBox)window.FindName("ModelCatalog");
            chooser.IsDropDownOpen = true;
            window.UpdateLayout();
            var popup = Assert.IsType<System.Windows.Controls.Primitives.Popup>(chooser.Template.FindName("PART_Popup", chooser));
            Assert.Equal(2, chooser.Items.Count);
            popup.Child.Measure(new Size(500, 300));
            popup.Child.Arrange(new Rect(0, 0, 500, 300));
            popup.Child.UpdateLayout();
            PumpUntil(() => Visuals<TextBlock>(popup.Child).Any(text => text.Text == "cached-a"));
            Assert.Contains(Visuals<TextBlock>(popup.Child), text => text.Text == "cached-a");
            chooser.IsDropDownOpen = false;
            Assert.Equal(-1, chooser.SelectedIndex);
            chooser.SelectedIndex = 0;
            PumpUntil(() => !window.IsCopyPending);
            Assert.Equal("cached-a", copied.Last());
            Assert.Equal("已复制", ((TextBlock)window.FindName("CopyToastText")).Text);
            Assert.Equal(-1, chooser.SelectedIndex);
            chooser.SelectedIndex = 0;
            PumpUntil(() => !window.IsCopyPending);
            Assert.Equal(2, copied.Count(x => x == "cached-a"));
            Assert.Equal("fixed-id", store.Read().Connections.Single().Model);
            Click("复制模型 ID");
            PumpUntil(() => !window.IsCopyPending);
            Assert.Equal("cached-a", copied.Last());
            Assert.Equal("cached-a", ((TextBlock)window.FindName("CopyModelId")).Text);
            failModelCopy = true;
            chooser.SelectedIndex = 1;
            PumpUntil(() => !window.IsCopyPending);
            Assert.Contains("失败", ((TextBlock)window.FindName("Status")).Text);
            failModelCopy = false;
            Click("复制模型 ID");
            PumpUntil(() => !window.IsCopyPending);
            Assert.Equal("cached-a", copied.Last());
            Click("获取 / 更新列表");
            PumpUntil(() => store.Read().Connections.Single().ModelIds.Contains("fresh-b"));
            Assert.Equal(["fresh-a", "fresh-b"], new LibraryStore(System.IO.Path.Combine(directory, "library.json")).Read().Connections.Single().ModelIds);
            Assert.Equal("fixed-id", store.Read().Connections.Single().Model);
            Render(window, "library-models-updated", 1120, 740);
            Click("编辑");
            Assert.Empty(((PasswordBox)window.FindName("ModelKey")).Password);
            Render(window, "library-model-editor", 1120, 740);
            Click("取消");
            Render(window, "library-models-minimum", 880, 580);
            var modelRoot = (FrameworkElement)window.Content;
            foreach (var button in Visuals<System.Windows.Controls.Button>(modelRoot).Where(b => Equals(b.Content, "复制地址") || Equals(b.Content, "获取 / 更新列表") || Equals(b.Content, "新增连接资料"))) AssertInside(modelRoot, button);

            VerifyCancelledEditorCanFetchAgain(store);
            VerifyLibraryVisualStates(directory);
            window.Hide();
            var hwnd = new System.Windows.Interop.WindowInteropHelper(window).EnsureHandle();
            using (var hotkey = new GlobalHotkey(GlobalHotkey.LibraryWindowId))
            {
                hotkey.HotkeyPressed += (_, _) => window.ToggleVisibility();
                hotkey.Register(hwnd, 7, 0x87); // 测试专用Ctrl+Alt+Shift+F24，避免占用用户常用组合。
                Assert.True(hotkey.IsRegistered);
                SendMessage(hwnd, 0x0312, (IntPtr)GlobalHotkey.LibraryWindowId, IntPtr.Zero);
                Assert.True(window.IsVisible);
                PumpUntil(() => window.IsActive);
                var other = new Window { Width = 160, Height = 80, Left = -10000, Top = -10000,
                    WindowStartupLocation = WindowStartupLocation.Manual, ShowInTaskbar = false };
                try
                {
                    other.Show(); other.Activate();
                    PumpUntil(() => other.IsActive && !window.IsActive);
                    Assert.True(window.IsVisible);
                    SendMessage(hwnd, 0x0312, (IntPtr)GlobalHotkey.LibraryWindowId, IntPtr.Zero);
                    Assert.True(window.IsVisible, "A visible background library must be raised by the first shortcut, rather than hidden.");
                    PumpUntil(() => window.IsActive);
                }
                finally { other.Close(); }
                SendMessage(hwnd, 0x0312, (IntPtr)GlobalHotkey.LibraryWindowId, IntPtr.Zero);
                Assert.False(window.IsVisible);
                SendMessage(hwnd, 0x0312, (IntPtr)GlobalHotkey.LibraryWindowId, IntPtr.Zero);
                Assert.True(window.IsVisible);
                Assert.Equal(1, tabs.SelectedIndex);
                window.WindowState = WindowState.Minimized;
                SendMessage(hwnd, 0x0312, (IntPtr)GlobalHotkey.LibraryWindowId, IntPtr.Zero);
                Assert.Equal(WindowState.Normal, window.WindowState);
                Assert.True(window.IsVisible);
            }
            VerifyLibraryDragOrder(directory);
            VerifyLibraryConnectionEditor(directory);
            VerifyLibraryHttpPermission(directory);
            VerifyLibrarySaveFeedback(directory);
            VerifyLibraryCopyRetries(directory);
            VerifyPromptCopyAdversarial(directory);
            VerifyCompactLibrary(directory);
            VerifyLibraryTypography(directory);
            window.CloseForTests(); window = null;
            window = new LibraryWindow(new LibraryStore(System.IO.Path.Combine(directory, "library.json")), copy: copied.Add);
            Assert.Equal(1, ((TabControl)window.FindName("Tabs")).SelectedIndex);
            window.Left = -10000; window.Top = -10000; window.ShowActivated = false;
            window.Show(); window.UpdateLayout();
            Click("复制模型 ID");
            PumpUntil(() => !window.IsCopyPending);
            Assert.Equal("cached-a", copied.Last());
            Assert.Equal("cached-a", ((TextBlock)window.FindName("CopyModelId")).Text);
            store.SaveConnection(new() { Platform = "另一连接", Endpoint = "http://localhost:11435/v1", Model = "other-fixed" });
            ((TextBox)window.FindName("ModelSearch")).Text = "另一连接";
            window.UpdateLayout();
            Click("复制模型 ID");
            PumpUntil(() => !window.IsCopyPending);
            Assert.Equal("other-fixed", copied.Last());
            ((TextBox)window.FindName("ModelSearch")).Text = "";
            ((ListBox)window.FindName("ModelList")).SelectedIndex = 0;
            window.UpdateLayout();
            Click("复制模型 ID");
            PumpUntil(() => !window.IsCopyPending);
            Assert.Equal("cached-a", copied.Last());
        }
        finally { window?.CloseForTests(); if (System.IO.Directory.Exists(directory)) System.IO.Directory.Delete(directory, true); }
    }

    private sealed class PendingCatalogHandler : System.Net.Http.HttpMessageHandler
    {
        protected override async Task<System.Net.Http.HttpResponseMessage> SendAsync(System.Net.Http.HttpRequestMessage request, CancellationToken token)
        {
            await Task.Delay(Timeout.Infinite, token);
            throw new InvalidOperationException("Unreachable after cancellation.");
        }
    }

    private static void VerifyCancelledEditorCanFetchAgain(LibraryStore store)
    {
        var pending = new LibraryWindow(store, new LibraryHttp(new PendingCatalogHandler()), _ => { }) { WindowStartupLocation = WindowStartupLocation.Manual, Left = -10000, Top = -10000, ShowActivated = false };
        try
        {
            ((TabControl)pending.FindName("Tabs")).SelectedIndex = 1;
            Render(pending, "library-request-before-cancel", 1120, 740);
            void Click(string label) => Visuals<System.Windows.Controls.Button>((DependencyObject)pending.Content).First(b => Equals(b.Content, label)).RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
            Click("编辑");
            var fetch = (System.Windows.Controls.Button)pending.FindName("EditorFetchButton");
            fetch.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
            Assert.False(fetch.IsEnabled);
            Render(pending, "library-editor-loading", 880, 580);
            Click("取消");
            Click("编辑");
            Assert.True(fetch.IsEnabled, "A cancelled request must not disable the next editor's fetch button.");
        }
        finally { pending.CloseForTests(); }
    }

    private static void VerifyLibraryVisualStates(string directory)
    {
        var store = new LibraryStore(System.IO.Path.Combine(directory, "visual", "library.json"));
        var window = new LibraryWindow(store, new LibraryHttp(new CatalogHandler()), _ => { }) { WindowStartupLocation = WindowStartupLocation.Manual, Left = -10000, Top = -10000, ShowActivated = false };
        try
        {
            var root = (FrameworkElement)window.Content;
            var tabs = (TabControl)window.FindName("Tabs");
            void Click(string label) => Visuals<System.Windows.Controls.Button>(root).First(b => Equals(b.Content, label)).RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
            Render(window, "library-empty-prompts", 880, 580);
            Assert.Equal(Visibility.Visible, ((TextBlock)window.FindName("PromptEmpty")).Visibility);
            tabs.SelectedIndex = 1;
            Render(window, "library-empty-models", 880, 580);
            Assert.Equal(Visibility.Visible, ((TextBlock)window.FindName("ModelEmpty")).Visibility);
            tabs.SelectedIndex = 0;
            root.UpdateLayout();
            window.ShowAiSettings();
            Render(window, "library-ai-settings", 1120, 740);
            ((TextBox)window.FindName("AiEndpoint")).Text = "invalid-address";
            ((System.Windows.Controls.Button)window.FindName("SaveButton")).RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
            Assert.Contains("API", ((TextBlock)window.FindName("EditorStatus")).Text);
            Assert.Equal(((SolidColorBrush)window.FindResource("AppError")).Color, ((SolidColorBrush)((TextBlock)window.FindName("EditorStatus")).Foreground).Color);
            Render(window, "library-ai-error-minimum", 880, 580);
            AssertInside(root, (FrameworkElement)window.FindName("SaveButton"));
            Click("取消");

            var longDescription = string.Concat(Enumerable.Repeat("原有描述完整保留，不得被输入框截断。", 12));
            var longNote = string.Concat(Enumerable.Repeat("这是已经迁入的长备注，需要无损保存。", 36));
            store.SavePrompt(new() { Title = string.Concat(Enumerable.Repeat("长标题用于检查截断及完整预览", 8)), Description = longDescription,
                Content = "仅用于布局验收的虚构内容。\n" + new string('W', 500), Tags = ["长文本验收", new string('标', 80)] });
            typeof(LibraryWindow).GetMethod("RefreshViews", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, null);
            Render(window, "library-long-prompts-minimum", 880, 580);
            Click("编辑");
            Assert.Equal(longDescription, ((TextBox)window.FindName("PromptDescription")).Text);
            Render(window, "library-prompt-editor-minimum", 880, 580);
            AssertInside(root, (FrameworkElement)window.FindName("SaveButton"));
            ((System.Windows.Controls.Button)window.FindName("SaveButton")).RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
            PumpUntil(() => ((FrameworkElement)window.FindName("EditorLayer")).Visibility == Visibility.Collapsed);
            Assert.Equal(longDescription, store.Read().Prompts.Single().Description);

            store.SaveConnection(new() { Platform = string.Concat(Enumerable.Repeat("自定义长平台名称", 18)), Endpoint = "http://localhost/" + new string('a', 260), Model = new string('m', 230), Note = longNote,
                RemainingBalance = "余额为手动记录，不代表实时平台额度" }, fetchedModelIds: [new string('d', 280)]);
            typeof(LibraryWindow).GetMethod("RefreshViews", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, null);
            tabs.SelectedIndex = 1;
            Render(window, "library-long-models-minimum", 880, 580);
            Click("编辑");
            Assert.Equal(longNote, ((TextBox)window.FindName("NoteBox")).Text);
            Assert.Equal(230, ((TextBox)window.FindName("ModelBox")).Text.Length);
            Render(window, "library-long-model-editor-minimum", 880, 580);
            var platform = (ComboBox)window.FindName("PlatformBox");
            Assert.NotNull(platform.Template.FindName("PART_EditableTextBox", platform));
            Assert.Contains(Visuals<TextBlock>((ComboBox)window.FindName("TypeBox")), text => text.Text == "对话");
            AssertInside(root, (FrameworkElement)window.FindName("SaveButton"));
            ((System.Windows.Controls.Button)window.FindName("SaveButton")).RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
            PumpUntil(() => ((FrameworkElement)window.FindName("EditorLayer")).Visibility == Visibility.Collapsed);
            Assert.Equal(longNote, store.Read().Connections.Single().Note);
            Assert.Equal(230, store.Read().Connections.Single().Model.Length);
        }
        finally { window.CloseForTests(); }
    }

    private static void VerifyCompactLibrary(string directory)
    {
        var store = new LibraryStore(System.IO.Path.Combine(directory, "compact", "library.json"));
        store.Update(s =>
        {
            s.Prompts = Enumerable.Range(1, 36).Select(i => new LibraryPrompt
            {
                Title = $"{i:00} · 开发任务与资料整理",
                Description = "快速查找常用提示词，完整内容在右侧查看。",
                Content = $"第 {i} 条提示词完整正文。\n" + string.Concat(Enumerable.Repeat("这是可选择文字的多行正文。\n", 20)),
                Tags = ["开发", "常用"]
            }).ToList();
            s.Connections = Enumerable.Range(1, 12).Select(i => new LibraryConnection
            {
                Platform = $"本地服务 {i:00}", Endpoint = "http://localhost:11434/v1", Model = $"model-{i:00}",
                ModelIds = [$"model-{i:00}", "general-model"], Note = "仅用于界面布局的虚构数据。"
            }).ToList();
        });
        var copied = new List<string>();
        var failCopy = false;
        var window = new LibraryWindow(store, copy: value => { if (failCopy) throw new InvalidOperationException("Clipboard busy"); copied.Add(value); }) { WindowStartupLocation = WindowStartupLocation.Manual, Left = -10000, Top = -10000, ShowActivated = false };
        try
        {
            window.Show();
            var tabs = (TabControl)window.FindName("Tabs");
            tabs.SelectedIndex = 0;
            Render(window, "compact-prompts", 1120, 760);
            var list = (ListBox)window.FindName("PromptList");
            var preview = (TextBox)window.FindName("PromptPreview");
            var button = (System.Windows.Controls.Button)window.FindName("PromptCopyButton");
            var popup = (System.Windows.Controls.Primitives.Popup)window.FindName("CopyToast");
            var feedback = (TextBlock)window.FindName("PromptCopyFeedback");
            Assert.True(list.ActualHeight / 59 >= 7, "The default viewport must show at least seven compact rows.");
            Assert.Equal(30, list.Items.Count);
            var initialRow = (ListBoxItem)list.ItemContainerGenerator.ContainerFromIndex(0);
            Assert.Contains(Visuals<TextBlock>(initialRow), t => t.Text == "已复制 0 次");
            var bodyPreview = Visuals<TextBlock>(initialRow).Single(t => t.Text.StartsWith("第 1 条提示词完整正文。"));
            Assert.DoesNotContain('\n', bodyPreview.Text);
            Assert.DoesNotContain(Visuals<TextBlock>(initialRow), t => t.Text.StartsWith("快速查找常用提示词"));
            Assert.Empty(copied);
            list.SelectedIndex = 1;
            var secondPrompt = (LibraryPrompt)list.SelectedItem;
            window.UpdateLayout();
            Assert.StartsWith("第 2 条", preview.Text);
            Assert.Empty(copied);
            button.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
            PumpUntil(() => !window.IsCopyPending);
            Assert.StartsWith("第 2 条", copied.Last());
            Assert.Contains("02 ·", feedback.Text);
            Assert.Equal("已复制", button.Content);
            Assert.True(popup.IsOpen);
            Assert.Equal(1, secondPrompt.CopyCount);
            Assert.Same(secondPrompt, list.SelectedItem);
            PumpUntil(() => ReferenceEquals(secondPrompt, list.Items[0]));
            Assert.Same(secondPrompt, list.Items[0]);
            Assert.Equal(1, store.Read().Prompts.Single(p => p.Id == secondPrompt.Id).CopyCount);
            RenderCopyFeedback(popup, "compact-copy-feedback");
            Render(window, "compact-prompts-copied", 1120, 760);
            list.SelectedIndex = 2;
            var thirdPrompt = (LibraryPrompt)list.SelectedItem;
            window.UpdateLayout();
            Assert.False(popup.IsOpen);
            Assert.Equal("复制全文", button.Content);
            var row = (ListBoxItem)list.ItemContainerGenerator.ContainerFromIndex(2);
            var rowTitle = Visuals<TextBlock>(row).First(t => t.Text.StartsWith("03 ·"));
            list.RaiseEvent(new System.Windows.Input.MouseButtonEventArgs(System.Windows.Input.Mouse.PrimaryDevice, 0, System.Windows.Input.MouseButton.Left) { RoutedEvent = UIElement.PreviewMouseLeftButtonUpEvent, Source = rowTitle });
            PumpUntil(() => !window.IsCopyPending);
            Assert.Equal(2, copied.Count);
            Assert.StartsWith("第 3 条", copied.Last());
            PumpUntil(() => ReferenceEquals(secondPrompt, list.Items[0]));
            Assert.Same(secondPrompt, list.Items[0]);
            PumpUntil(() => ReferenceEquals(thirdPrompt, list.Items[1]));
            Assert.Same(thirdPrompt, list.Items[1]);
            Assert.Same(thirdPrompt, list.SelectedItem);
            Assert.Equal(1, thirdPrompt.CopyCount);
            Assert.Equal("已复制", ((TextBlock)window.FindName("CopyToastText")).Text);
            window.UpdateLayout();
            row = (ListBoxItem)list.ItemContainerGenerator.ContainerFromItem(thirdPrompt);
            row.RaiseEvent(new System.Windows.Input.KeyEventArgs(System.Windows.Input.Keyboard.PrimaryDevice,
                PresentationSource.FromVisual(window)!, 0, System.Windows.Input.Key.Enter) { RoutedEvent = UIElement.PreviewKeyDownEvent });
            PumpUntil(() => !window.IsCopyPending);
            Assert.Equal(3, copied.Count);
            Assert.Equal(2, thirdPrompt.CopyCount);
            PumpUntil(() => ReferenceEquals(thirdPrompt, list.Items[0]));
            Assert.Same(thirdPrompt, list.Items[0]);
            failCopy = true;
            button.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
            PumpUntil(() => !window.IsCopyPending);
            Assert.Equal(3, copied.Count);
            Assert.Equal(2, store.Read().Prompts.Single(p => p.Id == thirdPrompt.Id).CopyCount);
            Assert.Equal("重试复制", button.Content);
            Assert.Contains("03 ·", feedback.Text);
            Assert.Contains("失败", feedback.Text);
            Assert.Contains("失败", ((TextBlock)window.FindName("CopyToastText")).Text);
            Assert.Equal(((SolidColorBrush)window.FindResource("AppError")).Color, ((SolidColorBrush)feedback.Foreground).Color);
            Render(window, "compact-copy-failed-minimum", 880, 580);
            Assert.True(list.ActualHeight / 59 >= 4, "The minimum viewport must show at least four compact rows.");
            failCopy = false;
            button.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
            PumpUntil(() => !window.IsCopyPending);
            PumpUntil(() => !popup.IsOpen);
            Assert.Equal("复制全文", button.Content);
            Assert.DoesNotContain("已复制", ((TextBlock)window.FindName("Status")).Text);
            var rowCopy = Visuals<System.Windows.Controls.Button>((ListBoxItem)list.ItemContainerGenerator.ContainerFromIndex(2)).Single();
            Assert.True(window.IsVisible, "Copy feedback expiry must not hide the library window.");
            var copiedBeforeButton = copied.Count;
            list.RaiseEvent(new System.Windows.Input.MouseButtonEventArgs(System.Windows.Input.Mouse.PrimaryDevice, 0, System.Windows.Input.MouseButton.Left) { RoutedEvent = UIElement.PreviewMouseLeftButtonUpEvent, Source = rowCopy });
            PumpUntil(() => !window.IsCopyPending);
            Assert.Equal(copiedBeforeButton, copied.Count);
            rowCopy.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
            PumpUntil(() => !window.IsCopyPending);
            Assert.Equal(copiedBeforeButton + 1, copied.Count);
            Assert.Equal("已复制", rowCopy.Content);
            window.UpdateLayout();
            var copiedPrompt = (LibraryPrompt)list.SelectedItem;
            var copiedRow = (ListBoxItem)list.ItemContainerGenerator.ContainerFromItem(copiedPrompt);
            Assert.Contains(Visuals<TextBlock>(copiedRow), t => t.Text == $"已复制 {copiedPrompt.CopyCount} 次");
            ((TextBox)window.FindName("PromptSearch")).Text = "找不到的条目";
            window.UpdateLayout();
            Assert.Null(list.SelectedItem);
            Assert.Equal(Visibility.Collapsed, ((FrameworkElement)window.FindName("PromptDetail")).Visibility);
            ((TextBox)window.FindName("PromptSearch")).Text = "";
            window.UpdateLayout();
            Assert.NotNull(list.SelectedItem);
            Assert.True(window.IsVisible, "Searching must not hide the library window.");
            // 先完成选中项的布局请求，再驱动真实虚拟列表滚动。
            Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
            list.ScrollIntoView(list.Items[list.Items.Count - 1]);
            window.UpdateLayout();
            PumpUntil(() => list.Items.Count == 36);
            Assert.Equal(LibraryStore.FilterPrompts(store.Read(), "", []).Select(p => p.Id),
                list.Items.Cast<LibraryPrompt>().Select(p => p.Id));
            Render(window, "compact-prompts-minimum", 880, 580);
            AssertInside((FrameworkElement)window.Content, preview);
            AssertInside((FrameworkElement)window.Content, button);
            // 加载更多及最小窗口重排可能回收容器，先让待核验的行进入视口。
            list.ScrollIntoView(list.Items[31]);
            window.UpdateLayout();
            var visibleRow = Assert.IsType<ListBoxItem>(list.ItemContainerGenerator.ContainerFromIndex(31));
            AssertInside(visibleRow, Visuals<System.Windows.Controls.Button>(visibleRow).Single());
            AssertInside(visibleRow, Visuals<TextBlock>(visibleRow).Single(t => t.Text.StartsWith("已复制 ")));

            tabs.SelectedIndex = 1;
            Render(window, "compact-models", 1120, 760);
            var models = (ListBox)window.FindName("ModelList");
            Assert.Equal(12, models.Items.Count);
            Assert.True(models.ActualHeight / 59 >= 8,
                $"The default viewport must show at least eight model rows: list {models.RenderSize}, content {((FrameworkElement)window.Content).RenderSize}, window {window.ActualWidth} x {window.ActualHeight}.");
            models.SelectedIndex = 5;
            window.UpdateLayout();
            Assert.Equal("本地服务 06", ((FrameworkElement)window.FindName("ModelDetail")).DataContext is LibraryConnection c ? c.Platform : null);
            var search = (TextBox)window.FindName("ModelSearch");
            search.Text = "model-09";
            window.UpdateLayout();
            Assert.Single(models.Items);
            Assert.Equal("model-09", ((LibraryConnection)models.SelectedItem).Model);
            search.Text = "不存在";
            window.UpdateLayout();
            Assert.Empty(models.Items);
            Assert.Equal(Visibility.Collapsed, ((FrameworkElement)window.FindName("ModelDetail")).Visibility);
            search.Text = "";
            Render(window, "compact-models-minimum", 880, 580);
            AssertInside((FrameworkElement)window.Content, (FrameworkElement)window.FindName("ModelCatalog"));
        }
        finally { window.CloseForTests(); }
    }

    private static void RenderCopyFeedback(System.Windows.Controls.Primitives.Popup popup, string name)
    {
        var child = (FrameworkElement)popup.Child;
        child.Measure(new Size(360, 200));
        child.Arrange(new Rect(new Point(), child.DesiredSize));
        child.UpdateLayout();
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(child.ActualWidth), (int)Math.Ceiling(child.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(child);
        var directory = System.IO.Path.Combine(AppContext.BaseDirectory, "ui-evidence");
        using var output = System.IO.File.Create(System.IO.Path.Combine(directory, name + ".png"));
        var png = new PngBitmapEncoder();
        png.Frames.Add(BitmapFrame.Create(bitmap));
        png.Save(output);
    }

    private static void AssertInside(FrameworkElement root, FrameworkElement element)
    {
        var bounds = element.TransformToAncestor(root).TransformBounds(new Rect(element.RenderSize));
        Assert.True(bounds.Left >= 0 && bounds.Top >= 0 && bounds.Right <= root.ActualWidth + 1 && bounds.Bottom <= root.ActualHeight + 1,
            $"{element.Name} must remain visible inside the minimum-size window: bounds {bounds}, viewport {root.ActualWidth} x {root.ActualHeight}, desired {element.DesiredSize}, parent {(element.Parent as FrameworkElement)?.RenderSize}, DPI {VisualTreeHelper.GetDpi(element)}.");
    }

    private static void PumpUntil(Func<bool> condition)
    {
        var frame = new DispatcherFrame();
        var deadline = DateTime.UtcNow.AddSeconds(5);
        var timer = new DispatcherTimer(TimeSpan.FromMilliseconds(10), DispatcherPriority.Background, (_, _) =>
        { if (condition() || DateTime.UtcNow > deadline) frame.Continue = false; }, Dispatcher.CurrentDispatcher);
        if (!condition()) Dispatcher.PushFrame(frame);
        timer.Stop();
        Assert.True(condition(), "Library operation did not finish.");
    }
}
