using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using OverTranslate.Services;
using OverTranslate.Services.Library;
using OverTranslate.Views.Library;
using OverTranslate.Views.Settings;
using OverTranslate.Views.Shell;
using Xunit;
using Button = System.Windows.Controls.Button;

namespace OverTranslate.Tests;

public sealed partial class ScreenTranslateShellTests
{
    private static void VerifyAiSettingsPage()
    {
        WithReviewLibrary((library, store, path) =>
        {
            AiOrganizingSettingsPage page;
            using (var locked = new System.IO.FileStream(path, System.IO.FileMode.Open,
                System.IO.FileAccess.ReadWrite, System.IO.FileShare.None))
            {
                page = new AiOrganizingSettingsPage(() => store);
                PumpUntil(() => ((Button)page.FindName("ReloadButton")).IsEnabled);
                Assert.False(((Button)page.FindName("SaveButton")).IsEnabled);
                Assert.Contains("读取", ((TextBlock)page.FindName("StatusText")).Text);
            }
            T Field<T>(string name) => (T)page.FindName(name);
            void Save()
            {
                Field<Button>("SaveButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                PumpUntil(() => Field<Button>("SaveButton").IsEnabled);
            }
            Field<Button>("ReloadButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            PumpUntil(() => Field<Button>("SaveButton").IsEnabled);
            Field<TextBox>("AiEndpoint").Text = "";
            Field<TextBox>("AiModel").Text = "fixture-model";
            Field<PasswordBox>("AiKey").Password = "fixture-only";
            Save();
            Assert.Contains("保存失败", Field<TextBlock>("StatusText").Text);
            Assert.Equal("fixture-only", Field<PasswordBox>("AiKey").Password);
            Assert.Equal("fixture-model", Field<TextBox>("AiModel").Text);

            Field<TextBox>("AiEndpoint").Text = "https://example.invalid/v1";
            Field<PasswordBox>("AiKey").Password = "fixture-only";
            Field<CheckBox>("AiAutomatic").IsChecked = false;
            store.SavePrompt(new() { Content = "concurrent fixture" });
            Save();
            Assert.Contains("已保存", Field<TextBlock>("StatusText").Text);
            Assert.False(store.Read().Ai.AutoSummarize);
            Assert.Equal("fixture-only", LocalSecretStore.Unprotect(store.Read().Ai.ApiKeyProtected));
            Assert.Equal(2, store.Read().Prompts.Count);
            Assert.Equal("", Field<PasswordBox>("AiKey").Password);

            // 留空沿用同源 Key；保存失败时保留输入。
            Field<TextBox>("AiModel").Text = "updated-fixture";
            using (var locked = new System.IO.FileStream(path, System.IO.FileMode.Open,
                System.IO.FileAccess.ReadWrite, System.IO.FileShare.None))
            {
                Save();
                Assert.Contains("输入内容已保留", Field<TextBlock>("StatusText").Text);
                Assert.Equal("updated-fixture", Field<TextBox>("AiModel").Text);
            }
            Save();
            Assert.Equal("updated-fixture", store.Read().Ai.Model);
            Assert.Equal("fixture-only", LocalSecretStore.Unprotect(store.Read().Ai.ApiKeyProtected));

            Field<CheckBox>("AiInsecure").IsChecked = true;
            Field<PasswordBox>("AiKey").Password = "pending-fixture";
            Field<TextBox>("AiEndpoint").Text = "http://other.invalid/v1";
            Assert.False(Field<CheckBox>("AiInsecure").IsChecked == true);
            Assert.Equal("", Field<PasswordBox>("AiKey").Password);
            Save();
            Assert.Contains("远程 HTTP", Field<TextBlock>("StatusText").Text);
            Field<CheckBox>("AiInsecure").IsChecked = true;
            Save();
            Assert.Empty(store.Read().Ai.ApiKeyProtected);
            Assert.True(store.Read().Ai.AllowInsecureEndpoint);

            var instance = typeof(ShellWindow).GetField("_instance", BindingFlags.Static | BindingFlags.NonPublic)!;
            var previous = instance.GetValue(null);
            var shell = new ShellWindow(() => store.Read(), () => { }, () => store)
            { Left = -10000, Top = -10000, ShowActivated = false, Width = 1100, Height = 680 };
            try
            {
                ((Button)shell.FindName("SettingsButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                var preferences = Assert.IsType<ScreenTranslatePreferencesPage>(((Border)shell.FindName("ContentHost")).Child);
                var tabs = (TabControl)preferences.FindName("Tabs");
                Assert.Equal("AI 与翻译", ((TabItem)tabs.SelectedItem).Header);
                var ai = Assert.IsType<AiOrganizingSettingsPage>(((ContentControl)preferences.FindName("AiOrganizingHost")).Content);
                PumpUntil(() => ((Button)ai.FindName("SaveButton")).IsEnabled);
                ((TextBox)ai.FindName("AiModel")).Text = "unsaved-navigation-fixture";
                shell.ShowOcrSettings();
                shell.ShowAiSettings();
                Assert.Equal("unsaved-navigation-fixture", ((TextBox)ai.FindName("AiModel")).Text);

                // 资料库跳转复用总设置，不能覆盖正在编辑的提示词。
                typeof(LibraryWindow).GetMethod("NewPrompt_Click", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .Invoke(library, [library, new RoutedEventArgs()]);
                ((TextBox)library.FindName("PromptBody")).Text = "unsaved prompt fixture";
                LibraryWindow.OpenAiSettings();
                Assert.Same(preferences, ((Border)shell.FindName("ContentHost")).Child);
                Assert.Equal("unsaved prompt fixture", ((TextBox)library.FindName("PromptBody")).Text);
                Assert.Equal(Visibility.Visible, ((FrameworkElement)library.FindName("PromptEditor")).Visibility);
                Render(shell, "settings-ai-unified", 1100, 680);
                AssertInside((FrameworkElement)shell.Content, (Button)shell.FindName("SettingsButton"));
                var scroll = (ScrollViewer)preferences.FindName("AiScroll");
                scroll.ScrollToVerticalOffset(280);
                Render(shell, "settings-ai-form", 1100, 680);
                Assert.True(ai.ActualWidth > 300);
                ApplyTheme(System.Windows.Application.Current, "Dark");
                Render(shell, "settings-ai-dark", 1100, 680);
                Assert.Equal(((System.Windows.Media.SolidColorBrush)ai.FindResource("AppTextPrimary")).Color,
                    ((System.Windows.Media.SolidColorBrush)((TextBlock)ai.FindName("KeyHint")).Foreground).Color);
                ApplyTheme(System.Windows.Application.Current, "Light");
                ShellWindow.ShowOrActivateFromShortcut();
                ((Button)shell.FindName("QuickSettingsButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Render(shell, "settings-ai-shortcut", 1100, 680);
                AssertInside((FrameworkElement)shell.Content, (Button)shell.FindName("QuickSettingsButton"));
                Assert.True(((Button)shell.FindName("QuickSettingsButton")).IsVisible);
                Assert.Equal(1, tabs.SelectedIndex);
            }
            finally
            {
                ApplyTheme(System.Windows.Application.Current, "Light");
                shell.Close();
                instance.SetValue(null, previous);
            }
        });
    }
}
