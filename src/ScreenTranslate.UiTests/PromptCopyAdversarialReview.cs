using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using OverTranslate.Services.Library;
using OverTranslate.Views.Library;
using Xunit;

namespace OverTranslate.Tests;

public sealed partial class ScreenTranslateShellTests
{
    private static void VerifyPromptCopyAdversarial(string directory)
    {
        VerifyCopyCountAfterViewRefresh(directory);
        VerifyRankingAfterHide(directory);
        VerifyCountSaveFailure(directory);
        VerifyCopyHistoryDrainedOnExit(directory);
    }

    private static void VerifySyncedCountRefresh(string directory)
    {
        var sender = new LibraryStore(System.IO.Path.Combine(directory, "count-sync-a", "library.json"));
        var receiver = new LibraryStore(System.IO.Path.Combine(directory, "count-sync-b", "library.json"));
        var prompt = sender.SavePrompt(new() { Title = "synced", Content = "synced count fixture" });
        sender.SavePrompt(new() { Title = "uncopied", Content = "uncopied fixture" });
        receiver.MergeSync(sender.ExportSync());
        var window = new LibraryWindow(receiver, copy: _ => { })
        { Left = -10000, Top = -10000, ShowActivated = false, WindowStartupLocation = WindowStartupLocation.Manual };
        try
        {
            window.Show(); window.UpdateLayout();
            sender.RememberCopiedPrompt(prompt.Id); sender.RememberCopiedPrompt(prompt.Id);
            receiver.MergeSync(sender.ExportSync());
            LibrarySyncNotifications.NotifyChanged();
            var list = (ListBox)window.FindName("PromptList");
            PumpUntil(() => list.Items.Cast<LibraryPrompt>().Any(p => p.Id == prompt.Id && p.CopyCount == 2));
            Assert.Equal(prompt.Id, ((LibraryPrompt)list.Items[0]).Id);
            window.UpdateLayout();
            Assert.Contains(Visuals<TextBlock>(list), text => text.Text.Contains("2 次"));
        }
        finally { window.CloseForTests(); }
    }

    private static void VerifyCopyCountAfterViewRefresh(string directory)
    {
        var store = new LibraryStore(System.IO.Path.Combine(directory, "count-view-refresh", "library.json"));
        var first = new LibraryPrompt { Title = "first", Content = "first body" };
        var second = new LibraryPrompt { Title = "second", Content = "second body" };
        store.Update(s => s.Prompts = [first, second]);
        var copied = new List<string>();
        var window = new LibraryWindow(store, copy: copied.Add)
        { Left = -10000, Top = -10000, ShowActivated = false, WindowStartupLocation = WindowStartupLocation.Manual };
        var hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            window.Show(); window.UpdateLayout();
            var list = (ListBox)window.FindName("PromptList");
            var search = (TextBox)window.FindName("PromptSearch");
            list.SelectedIndex = 1;
            window.UpdateLayout();
            typeof(LibraryWindow).GetField("_copyHistory", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(window, hold.Task);
            ((System.Windows.Controls.Button)window.FindName("PromptCopyButton")).RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
            Assert.Equal("second body", copied.Single());
            search.Text = "first";
            search.Text = "";
            hold.SetResult();
            PumpUntil(() => !window.IsCopyPending);
            Assert.Equal(1, store.Read().Prompts.Single(p => p.Id == second.Id).CopyCount);
            Assert.Equal(1, list.Items.Cast<LibraryPrompt>().Single(p => p.Id == second.Id).CopyCount);
            PumpUntil(() => ((LibraryPrompt)list.Items[0]).Id == second.Id);
        }
        finally { hold.TrySetResult(); window.CloseForTests(); }
    }

    private static void VerifyRankingAfterHide(string directory)
    {
        var store = new LibraryStore(System.IO.Path.Combine(directory, "count-hide", "library.json"));
        var second = new LibraryPrompt { Title = "second", Content = "second body" };
        store.Update(s => s.Prompts = [new() { Title = "first", Content = "first body" }, second]);
        var window = new LibraryWindow(store, copy: _ => { })
        { Left = -10000, Top = -10000, ShowActivated = false, WindowStartupLocation = WindowStartupLocation.Manual };
        try
        {
            window.Show(); window.UpdateLayout();
            var list = (ListBox)window.FindName("PromptList");
            list.SelectedIndex = 1;
            window.UpdateLayout();
            ((System.Windows.Controls.Button)window.FindName("PromptCopyButton")).RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
            PumpUntil(() => !window.IsCopyPending);
            window.Hide();
            window.Show(); window.UpdateLayout();
            Assert.Equal(second.Id, ((LibraryPrompt)list.Items[0]).Id);
        }
        finally { window.CloseForTests(); }
    }

    private static void VerifyCopyHistoryDrainedOnExit(string directory)
    {
        var store = new LibraryStore(System.IO.Path.Combine(directory, "count-exit", "library.json"));
        store.SavePrompt(new() { Content = "exit fixture" });
        var window = new LibraryWindow(store, copy: _ => { })
        { Left = -10000, Top = -10000, ShowActivated = false, WindowStartupLocation = WindowStartupLocation.Manual };
        var hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task? history = null;
        try
        {
            window.Show(); window.UpdateLayout();
            var field = typeof(LibraryWindow).GetField("_copyHistory", BindingFlags.Instance | BindingFlags.NonPublic)!;
            field.SetValue(window, hold.Task);
            ((System.Windows.Controls.Button)window.FindName("PromptCopyButton")).RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
            history = (Task)field.GetValue(window)!;
            var release = Task.Run(async () => { await Task.Delay(200); hold.TrySetResult(); });
            window.CloseForTests();
            Assert.True(history.IsCompleted, "Normal exit must finish history already queued after a successful clipboard write.");
            Assert.Equal(1, store.Read().Prompts.Single().CopyCount);
            release.GetAwaiter().GetResult();
        }
        finally
        {
            hold.TrySetResult();
            history?.GetAwaiter().GetResult();
            window.CloseForTests();
        }
    }

    private static void VerifyCountSaveFailure(string directory)
    {
        var path = System.IO.Path.Combine(directory, "count-save-failure", "library.json");
        var store = new LibraryStore(path);
        store.SavePrompt(new() { Content = "copy should still succeed" });
        var copied = new List<string>();
        var window = new LibraryWindow(store, copy: copied.Add)
        { Left = -10000, Top = -10000, ShowActivated = false, WindowStartupLocation = WindowStartupLocation.Manual };
        var hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            window.Show(); window.UpdateLayout();
            var button = (System.Windows.Controls.Button)window.FindName("PromptCopyButton");
            typeof(LibraryWindow).GetField("_copyHistory", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(window, hold.Task);
            button.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
            var search = (TextBox)window.FindName("PromptSearch");
            search.Text = "copy";
            search.Text = "";
            using (var locked = new System.IO.FileStream(path, System.IO.FileMode.Open, System.IO.FileAccess.Read, System.IO.FileShare.None))
            {
                hold.SetResult();
                PumpUntil(() => !window.IsCopyPending);
                Assert.Single(copied);
                Assert.Contains("已复制", ((TextBlock)window.FindName("Status")).Text);
                Assert.Contains("未能保存复制次数", ((TextBlock)window.FindName("Status")).Text);
            }
            Assert.Equal(0, store.Read().Prompts.Single().CopyCount);
            button.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
            PumpUntil(() => !window.IsCopyPending);
            Assert.Equal(2, copied.Count);
            Assert.Equal(1, store.Read().Prompts.Single().CopyCount);
        }
        finally { hold.TrySetResult(); window.CloseForTests(); }
    }
}
