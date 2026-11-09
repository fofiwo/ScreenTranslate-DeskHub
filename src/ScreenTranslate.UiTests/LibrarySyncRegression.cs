using System.Windows;
using System.Windows.Controls;
using System.Reflection;
using OverTranslate.Services.Library;
using OverTranslate.Views.Library;
using Xunit;

namespace OverTranslate.Tests;

public sealed partial class ScreenTranslateShellTests
{
    private static void VerifyLibrarySyncSettings()
    {
        // 注入空设置，避免读取用户配对码、启动监听或写入实际资料。
        var window = new LibrarySyncWindow(new LibrarySyncOptions())
        { WindowStartupLocation = WindowStartupLocation.Manual, Left = -10000, Top = -10000, ShowActivated = false };
        try
        {
            window.Show(); window.UpdateLayout();
            Assert.Equal("48539", ((TextBox)window.FindName("PortBox")).Text);
            Assert.False(((CheckBox)window.FindName("EnabledBox")).IsChecked);
            var code = (PasswordBox)window.FindName("CodeBox"); Assert.Empty(code.Password);
            ((System.Windows.Controls.Button)window.FindName("GenerateButton")).RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
            Assert.Equal(32, LibrarySyncTransport.ParseCode(code.Password).Length);
            Assert.Empty(((TextBox)window.FindName("PeersBox")).Text);
            Assert.NotNull(((System.Windows.Controls.Button)window.FindName("SaveButton")).Style);
            Assert.True(((System.Windows.Controls.Button)window.FindName("SaveButton")).ActualHeight >= 40);
            typeof(LibrarySyncWindow).GetMethod("SyncNow_Click", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, [window, new RoutedEventArgs()]);
            Assert.Contains("尚未开启", ((TextBlock)window.FindName("StatusText")).Text);
            Render(window, "library-sync-settings", 650, 720);
            window.Width = 560; window.Height = 540; window.UpdateLayout();
            Render(window, "library-sync-settings-minimum", 560, 540);
            Assert.True(((ScrollViewer)window.Content).ScrollableHeight > 0);
        }
        finally { window.Close(); }
        VerifyDeletedPromptEditorKeepsInput();
        VerifySyncSettingsCannotChangeDuringSave();
    }

    private static void VerifyDeletedPromptEditorKeepsInput()
    {
        var directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "sync-stale-editor-" + Guid.NewGuid());
        var store = new LibraryStore(System.IO.Path.Combine(directory, "library.json"));
        var prompt = store.SavePrompt(new() { Content = "original" });
        var window = new LibraryWindow(store, copy: _ => { })
        { WindowStartupLocation = WindowStartupLocation.Manual, Left = -10000, Top = -10000, ShowActivated = false };
        try
        {
            window.Show(); store.Update(s => s.Prompts.Clear());
            typeof(LibraryWindow).GetMethod("EditPrompt", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, [prompt]);
            ((TextBox)window.FindName("PromptBody")).Text = "unsaved local input";
            ((System.Windows.Controls.Button)window.FindName("SaveButton")).RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
            PumpUntil(() => ((System.Windows.Controls.Button)window.FindName("SaveButton")).IsEnabled);
            Assert.Empty(store.Read().Prompts);
            Assert.Equal("unsaved local input", ((TextBox)window.FindName("PromptBody")).Text);
            Assert.Contains("已被其他设备", ((TextBlock)window.FindName("EditorStatus")).Text);
            Assert.Equal(Visibility.Visible, ((FrameworkElement)window.FindName("EditorLayer")).Visibility);
        }
        finally { window.CloseForTests(); System.IO.Directory.Delete(directory, true); }
    }

    private static void VerifySyncSettingsCannotChangeDuringSave()
    {
        var pending = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var window = new LibrarySyncWindow(new LibrarySyncOptions(), _ => pending.Task)
        { WindowStartupLocation = WindowStartupLocation.Manual, Left = -10000, Top = -10000, ShowActivated = false };
        try
        {
            window.Show(); window.UpdateLayout();
            ((System.Windows.Controls.Button)window.FindName("SaveButton")).RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
            Assert.False(((StackPanel)window.FindName("SyncForm")).IsEnabled);
            Assert.False(((System.Windows.Controls.Button)window.FindName("GenerateButton")).IsEnabled);
            Assert.False(((TextBox)window.FindName("PeersBox")).IsEnabled);
            pending.SetResult();
            PumpUntil(() => ((StackPanel)window.FindName("SyncForm")).IsEnabled);
        }
        finally { pending.TrySetResult(); PumpUntil(() => ((StackPanel)window.FindName("SyncForm")).IsEnabled); window.Close(); }
    }
}
