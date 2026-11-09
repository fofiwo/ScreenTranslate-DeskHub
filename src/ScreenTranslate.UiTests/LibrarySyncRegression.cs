using System.Windows;
using System.Windows.Controls;
using System.Reflection;
using OverTranslate.Services.Library;
using OverTranslate.Views.Library;
using Xunit;

namespace OverTranslate.Tests;

public sealed partial class ScreenTranslateShellTests
{
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

}
