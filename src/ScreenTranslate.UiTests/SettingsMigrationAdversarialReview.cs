using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using OverTranslate.Services;
using OverTranslate.Services.Library;
using OverTranslate.Views.Library;
using OverTranslate.Views.Settings;
using Xunit;

namespace OverTranslate.Tests;

public sealed partial class ScreenTranslateShellTests
{
    private static void VerifySettingsMigrationReview()
    {
        var failures = new List<Exception>();
        void Check(string name, Action verify)
        {
            try { verify(); }
            catch (Exception error) { failures.Add(new Exception(name, error)); }
        }
        Check("更改高级选项不能覆盖另一个页面或工具栏更新的值", VerifySettingsChangeIsolation);
        Check("从设置返回已打开的资料库时快捷键提示须刷新", VerifyLibraryShortcutRefresh);
        Check("AI 配置集中到设置并保留保存与恢复能力", VerifyAiSettingsPage);
        Check("统一同步通知须刷新资料并保留未保存输入", VerifyUnifiedSyncNotification);
        Check("迁移后的 AI 配置仍可校验并保存", () =>
        {
            var directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "settings-save-review-" + Guid.NewGuid());
            try { VerifyLibrarySaveFeedback(directory); }
            finally { if (System.IO.Directory.Exists(directory)) System.IO.Directory.Delete(directory, true); }
        });
        if (failures.Count > 0) throw new AggregateException(failures);
    }

    private static void VerifySettingsChangeIsolation()
    {
        var settings = SettingsService.Instance.Current;
        var oldFormatting = settings.OcrPreserveFormatting;
        var oldFallback = settings.AllowFallbackServices;
        var page = new SettingsPage(_ => { }, () => true);
        page.ShowQuickTools(false);
        try
        {
            // 页面保留旧值，模拟另一个配置面板已经更新了当前设置。
            settings.OcrPreserveFormatting = !oldFormatting;
            ((CheckBox)page.FindName("AllowFallbackCheckBox")).IsChecked = !oldFallback;
            Assert.Equal(!oldFormatting, settings.OcrPreserveFormatting);
            Assert.Equal(!oldFallback, settings.AllowFallbackServices);
        }
        finally
        {
            settings.OcrPreserveFormatting = oldFormatting;
            settings.AllowFallbackServices = oldFallback;
        }
    }

    private static void VerifyLibraryShortcutRefresh()
    {
        WithReviewLibrary((window, _, _) =>
        {
            var settings = SettingsService.Instance.Current;
            var original = (settings.LibraryHotkeyEnabled, settings.LibraryHotkeyDisplay);
            try
            {
                settings.LibraryHotkeyEnabled = true;
                settings.LibraryHotkeyDisplay = "Ctrl+Alt+F12";
                typeof(Window).GetMethod("OnActivated", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .Invoke(window, [EventArgs.Empty]);
                Assert.Equal("Ctrl+Alt+F12", ((TextBlock)window.FindName("ShortcutHint")).Text);
            }
            finally { (settings.LibraryHotkeyEnabled, settings.LibraryHotkeyDisplay) = original; }
        });
    }

    private static void VerifyUnifiedSyncNotification()
    {
        WithReviewLibrary((window, store, _) =>
        {
            store.SavePrompt(new() { Content = "remote fixture" });
            Task.Run(LibrarySyncNotifications.NotifyChanged).GetAwaiter().GetResult();
            PumpUntil(() => ((ListBox)window.FindName("PromptList")).Items.Count == 2);
            typeof(LibraryWindow).GetMethod("NewPrompt_Click", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(window, [window, new RoutedEventArgs()]);
            ((TextBox)window.FindName("PromptBody")).Text = "unsaved fixture";
            store.SavePrompt(new() { Content = "another remote fixture" });
            Task.Run(LibrarySyncNotifications.NotifyChanged).GetAwaiter().GetResult();
            PumpUntil(() => ((TextBlock)window.FindName("EditorStatus")).Text.Contains("当前输入保留"));
            Assert.Equal("unsaved fixture", ((TextBox)window.FindName("PromptBody")).Text);
            Assert.Equal(Visibility.Visible, ((FrameworkElement)window.FindName("EditorLayer")).Visibility);
            typeof(LibraryWindow).GetMethod("CancelEditor_Click", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(window, [window, new RoutedEventArgs()]);
            Assert.Equal(3, ((ListBox)window.FindName("PromptList")).Items.Count);
        });
    }

    private static void WithReviewLibrary(Action<LibraryWindow, LibraryStore, string> verify)
    {
        var directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "settings-review-" + Guid.NewGuid());
        var path = System.IO.Path.Combine(directory, "library.json");
        var store = new LibraryStore(path);
        store.SavePrompt(new() { Content = "local fixture" });
        var window = new LibraryWindow(store, copy: _ => { });
        try { verify(window, store, path); }
        finally { window.CloseForTests(); System.IO.Directory.Delete(directory, true); }
    }
}
