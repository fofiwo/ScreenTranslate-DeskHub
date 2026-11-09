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
            Assert.Equal("检查更新", button.Content);
            Render(shell, "update-entry-minimum", 1100, 680);
            AssertInside((FrameworkElement)shell.Content, button);

            var pending = new TaskCompletionSource<OverTranslate.Services.UpdateInfo?>();
            pendingField.SetValue(null, pending.Task);
            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Equal("正在检查更新…", button.Content);
            Assert.False(button.IsEnabled);

            // Prerelease fixture skips release-note networking; no package is downloaded or applied.
            var asset = new VelopackAsset { PackageId = "ScreenTranslate", Version = SemanticVersion.Parse("9.9.9-test"),
                Type = VelopackAssetType.Full, FileName = "ScreenTranslate-9.9.9-test-full.nupkg" };
            var info = new OverTranslate.Services.UpdateInfo("9.9.9-test",
                new DeltaAwareUpdateManager(new GithubSource(UpdateService.GitHubRepoUrl, null, false), new UpdateOptions(),
                    new TestVelopackLocator("ScreenTranslate", "0.2.3", packageRoot, null, packageRoot, null)),
                new Velopack.UpdateInfo(asset, false, null!, []));
            state.SetValue(null, info);
            pending.SetResult(info);
            PumpUntil(() => button.IsEnabled);
            ((EventHandler?)changed.GetValue(null))?.Invoke(null, EventArgs.Empty);
            Assert.Equal("发现新版 v9.9.9-test", button.Content);
            Assert.Equal(FontWeights.SemiBold, button.FontWeight);
            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            PumpUntil(() => Application.Current.Windows.OfType<UpdateWindow>().Any(w => w.IsVisible));
            var update = Application.Current.Windows.OfType<UpdateWindow>().Single();
            Assert.Contains("9.9.9-test", ((TextBlock)update.FindName("LatestVersionText")).Text);
            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Single(Application.Current.Windows.OfType<UpdateWindow>());

            tray = new TrayMenuWindow();
            var trayButton = (Button)tray.FindName("UpdateTrayButton");
            Assert.Equal(button.Content, trayButton.Content);
            trayButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Single(Application.Current.Windows.OfType<UpdateWindow>());

            var about = new ProductAboutPage();
            about.Measure(new Size(760, 680));
            about.Arrange(new Rect(0, 0, 760, 680));
            about.UpdateLayout();
            var aboutButton = Visuals<Button>(about).Single(b => Equals(b.Content, "检查更新"));
            aboutButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
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
}
