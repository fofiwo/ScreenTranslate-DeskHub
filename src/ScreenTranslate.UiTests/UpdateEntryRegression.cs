using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using OverTranslate.Services;
using OverTranslate.Services.Library;
using OverTranslate.Views.Shell;
using Velopack;
using Velopack.Sources;
using Velopack.Locators;
using Xunit;

namespace OverTranslate.Tests;

public sealed partial class ScreenTranslateShellTests
{
    private static void VerifyUpdateEntries()
    {
        VerifyUpdateRefresh();
        var state = typeof(UpdateNotifier).GetProperty(nameof(UpdateNotifier.Available))!;
        var previous = state.GetValue(null);
        var changed = typeof(UpdateNotifier).GetField("AvailabilityChanged", BindingFlags.Static | BindingFlags.NonPublic)!;
        var pendingField = typeof(UpdateNotifier).GetField("_pendingCheck", BindingFlags.Static | BindingFlags.NonPublic)!;
        var previousPending = pendingField.GetValue(null);
        ShellWindow? shell = null;
        TrayMenuWindow? tray = null;
        var packageRoot = System.IO.Directory.CreateTempSubdirectory("deskhub-update-ui-").FullName;
        try
        {
            state.SetValue(null, null);
            shell = new ShellWindow(() => new LibraryState(), () => { });
            var button = (Button)shell.FindName("UpdateButton");
            Assert.Equal("安装新版", button.Content);
            Render(shell, "update-entry-minimum", 1100, 680);
            Assert.Equal(Visibility.Collapsed, button.Visibility);
            ((RadioButton)shell.FindName("AboutNav")).IsChecked = true;
            var overview = Assert.IsType<ProductAboutPage>(((Border)shell.FindName("ContentHost")).Child);
            Render(shell, "about-update-entry-minimum", 1100, 680);
            var manual = Visuals<Button>(overview).Single(b => Equals(b.Content, "检查更新"));
            AssertInside((FrameworkElement)shell.Content, manual);

            var pending = new TaskCompletionSource<OverTranslate.Services.UpdateInfo?>();
            pendingField.SetValue(null, pending.Task);
            manual.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Equal("正在检查更新…", manual.Content);
            Assert.False(manual.IsEnabled);

            // Prerelease fixture skips release-note networking; no package is downloaded or applied.
            var asset = new VelopackAsset { PackageId = "ScreenTranslate", Version = SemanticVersion.Parse("9.9.9-test"),
                Type = VelopackAssetType.Full, FileName = "ScreenTranslate-9.9.9-test-full.nupkg" };
            var info = new OverTranslate.Services.UpdateInfo("9.9.9-test",
                new DeltaAwareUpdateManager(new GithubSource(UpdateService.GitHubRepoUrl, null, false), new UpdateOptions(),
                    new TestVelopackLocator("ScreenTranslate", "0.2.3", packageRoot, null, packageRoot, null)),
                new Velopack.UpdateInfo(asset, false, null!, []));
            state.SetValue(null, info);
            pending.SetResult(info);
            PumpUntil(() => manual.IsEnabled);
            void CheckAgain(Button target)
            {
                pending = new TaskCompletionSource<OverTranslate.Services.UpdateInfo?>();
                pendingField.SetValue(null, pending.Task);
                target.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                target.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                pending.SetResult(info);
                PumpUntil(() => !(bool)typeof(UpdateUi).GetField("_checking", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!);
            }
            ((EventHandler?)changed.GetValue(null))?.Invoke(null, EventArgs.Empty);
            Assert.Equal(Visibility.Visible, button.Visibility);
            Render(shell, "multitool-update-available", 1100, 680);
            var tools = (ScrollViewer)shell.FindName("ToolScroll");
            foreach (var entry in ((StackPanel)shell.FindName("QuickToolsPanel")).Children.OfType<Button>().Where(b => b.Visibility == Visibility.Visible))
            {
                tools.ScrollToVerticalOffset(entry.TranslatePoint(new Point(), (UIElement)tools.Content).Y);
                shell.UpdateLayout();
                AssertInside(tools, entry);
            }
            AssertInside((FrameworkElement)shell.Content, button);
            Assert.Equal("安装新版", button.Content);
            Assert.Contains("9.9.9-test", Assert.IsType<string>(button.ToolTip));
            Assert.Equal(FontWeights.SemiBold, button.FontWeight);
            CheckAgain(button);
            PumpUntil(() => Application.Current.Windows.OfType<UpdateWindow>().Any(w => w.IsVisible));
            var update = Application.Current.Windows.OfType<UpdateWindow>().Single();
            Assert.Contains("9.9.9-test", ((TextBlock)update.FindName("LatestVersionText")).Text);
            CheckAgain(button);
            Assert.Single(Application.Current.Windows.OfType<UpdateWindow>());

            tray = new TrayMenuWindow();
            var trayButton = (Button)tray.FindName("UpdateTrayButton");
            Assert.Equal("发现新版 v9.9.9-test", trayButton.Content);
            CheckAgain(trayButton);
            Assert.Single(Application.Current.Windows.OfType<UpdateWindow>());

            var about = new ProductAboutPage();
            about.Measure(new Size(760, 680));
            about.Arrange(new Rect(0, 0, 760, 680));
            about.UpdateLayout();
            var aboutButton = Visuals<Button>(about).Single(b => Equals(b.Content, "检查更新"));
            CheckAgain(aboutButton);
            Assert.Single(Application.Current.Windows.OfType<UpdateWindow>());
            update.Close();

            state.SetValue(null, null);
            foreach (var fail in new[] { false, true })
            {
                pending = new TaskCompletionSource<OverTranslate.Services.UpdateInfo?>();
                pendingField.SetValue(null, pending.Task);
                aboutButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.False(aboutButton.IsEnabled);
                Assert.Contains("正在检查", ((TextBlock)about.FindName("StatusText")).Text);
                // 让真实处理器进入等待阶段，确保测试覆盖异步完成与异常恢复。
                about.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                if (fail) pending.SetException(new InvalidOperationException("test failure"));
                else pending.SetResult(null);
                PumpUntil(() => aboutButton.IsEnabled);
                var result = ((TextBlock)about.FindName("StatusText")).Text;
                Assert.DoesNotContain("正在检查", result);
                Assert.False(string.IsNullOrWhiteSpace(result));
                if (fail) Assert.Contains("检查更新失败", result);
                Assert.Equal("检查更新", aboutButton.Content);
            }
        }
        finally
        {
            foreach (var window in Application.Current.Windows.OfType<UpdateWindow>().ToArray()) window.Close();
            tray?.Close();
            shell?.Close();
            state.SetValue(null, previous);
            pendingField.SetValue(null, previousPending);
            System.IO.Directory.Delete(packageRoot, true);
        }
    }

    private static void VerifyUpdateRefresh()
    {
        VerifyNetworkProxySettings();
        var state = typeof(UpdateNotifier).GetProperty(nameof(UpdateNotifier.Available))!;
        var pendingField = typeof(UpdateNotifier).GetField("_pendingCheck", BindingFlags.Static | BindingFlags.NonPublic)!;
        var previous = state.GetValue(null);
        var previousPending = pendingField.GetValue(null);
        var folder = System.IO.Directory.CreateTempSubdirectory("update-refresh-").FullName;
        OverTranslate.Services.UpdateInfo Offer(string version, bool downgrade = false) => new(version,
            new DeltaAwareUpdateManager(new DeskHubReleaseSource(), new UpdateOptions(),
                new TestVelopackLocator("ScreenTranslate", "0.3.5", folder, null, folder, null)),
            new Velopack.UpdateInfo(new VelopackAsset { PackageId = "ScreenTranslate", Version = SemanticVersion.Parse(version),
                Type = VelopackAssetType.Full, FileName = $"ScreenTranslate-{version}-full.nupkg" }, downgrade, null!, []));
        var older = Offer("9.9.10-test");
        var newer = Offer("9.9.11-test");
        var skipped = SettingsService.Instance.Current.SkippedUpdateVersion;
        try
        {
            state.SetValue(null, older);
            UpdateWindow.ShowOrActivate(older);
            PumpUntil(() => Application.Current.Windows.OfType<UpdateWindow>().Any());
            var original = Application.Current.Windows.OfType<UpdateWindow>().Single();
            var pending = new TaskCompletionSource<OverTranslate.Services.UpdateInfo?>();
            pendingField.SetValue(null, pending.Task);
            var button = new Button { Content = "检查更新" };
            var check = UpdateUi.CheckAndShowAsync(button, _ => { });
            Assert.False(check.IsCompleted, "Manual checks must query again even when an older update is cached.");
            Assert.False(button.IsEnabled);
            button.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            state.SetValue(null, newer); pending.SetResult(newer);
            PumpUntil(() => check.IsCompleted); check.GetAwaiter().GetResult();
            var current = Application.Current.Windows.OfType<UpdateWindow>().Single();
            Assert.NotSame(original, current);
            Assert.Equal("v9.9.11-test", ((TextBlock)current.FindName("LatestVersionText")).Text);

            var refresh = Assert.IsType<Button>(current.FindName("RefreshButton"));
            // Same-version checks keep the window, but a successful refresh can jump forward again.
            pending = new TaskCompletionSource<OverTranslate.Services.UpdateInfo?>();
            pendingField.SetValue(null, pending.Task);
            refresh.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            current.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            pending.SetResult(newer);
            PumpUntil(() => refresh.IsEnabled);
            Assert.Same(current, Application.Current.Windows.OfType<UpdateWindow>().Single());
            pending = new TaskCompletionSource<OverTranslate.Services.UpdateInfo?>();
            pendingField.SetValue(null, pending.Task);
            refresh.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            current.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            pending.SetResult(Offer("9.9.12-test"));
            PumpUntil(() => !current.IsVisible);
            current = Application.Current.Windows.OfType<UpdateWindow>().Single();
            Assert.Equal("v9.9.12-test", ((TextBlock)current.FindName("LatestVersionText")).Text);
            refresh = Assert.IsType<Button>(current.FindName("RefreshButton"));
            Assert.False(original.IsVisible);

            Assert.Equal("刷新版本", refresh.Content);
            pending = new TaskCompletionSource<OverTranslate.Services.UpdateInfo?>();
            pendingField.SetValue(null, pending.Task);
            refresh.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.False(refresh.IsEnabled);
            Assert.False(((Button)current.FindName("DownloadBtn")).IsEnabled);
            current.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            pending.SetException(new System.Net.Http.HttpRequestException("fixture error with sensitive URL withheld"));
            PumpUntil(() => refresh.IsEnabled);
            Assert.True(current.IsVisible);
            Assert.Equal("刷新版本", refresh.Content);
            Assert.True(((Button)current.FindName("DownloadBtn")).IsEnabled);
            Assert.Contains("检查更新失败", ((TextBlock)current.FindName("ErrorText")).Text);
            Assert.Equal("v9.9.12-test", ((TextBlock)current.FindName("LatestVersionText")).Text);

            var higher = current;
            UpdateWindow.ShowOrActivate(Offer("9.9.10-test", downgrade: true));
            PumpUntil(() => !higher.IsVisible);
            current = Application.Current.Windows.OfType<UpdateWindow>().Single();
            Assert.Equal("v9.9.10-test", ((TextBlock)current.FindName("LatestVersionText")).Text);
            Assert.Contains("GitHub", ((TextBlock)current.FindName("ExplanationText")).Text);
            Assert.Equal(Visibility.Visible, ((TextBlock)current.FindName("ExplanationText")).Visibility);
            SettingsService.Instance.Current.SkippedUpdateVersion = "9.9.12-test";
            Assert.False(UpdateNotifier.IsSkipped(Offer("9.9.10-test", downgrade: true)));
            Assert.True(UpdateNotifier.IsSkipped(Offer("9.9.12-test")));
            refresh = Assert.IsType<Button>(current.FindName("RefreshButton"));

            // A different offer must never replace a download which is already in progress.
            var phase = typeof(UpdateWindow).GetNestedType("Phase", BindingFlags.NonPublic)!;
            var setPhase = typeof(UpdateWindow).GetMethod("SetPhase", BindingFlags.Instance | BindingFlags.NonPublic)!;
            setPhase.Invoke(current, [Enum.Parse(phase, "Downloading")]);
            Assert.False(refresh.IsEnabled);
            UpdateWindow.ShowOrActivate(Offer("9.9.13-test"));
            Assert.Same(current, Application.Current.Windows.OfType<UpdateWindow>().Single());
            setPhase.Invoke(current, [Enum.Parse(phase, "Offering")]);
        }
        finally
        {
            foreach (var window in Application.Current.Windows.OfType<UpdateWindow>().ToArray())
            {
                var phase = typeof(UpdateWindow).GetNestedType("Phase", BindingFlags.NonPublic)!;
                typeof(UpdateWindow).GetMethod("SetPhase", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .Invoke(window, [Enum.Parse(phase, "Offering")]);
                window.Close();
            }
            state.SetValue(null, previous); pendingField.SetValue(null, previousPending);
            SettingsService.Instance.Current.SkippedUpdateVersion = skipped;
            System.IO.Directory.Delete(folder, true);
        }
    }
}
