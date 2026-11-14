using System.IO;
using System.Windows.Controls;
using OverTranslate.Models;
using OverTranslate.Services.ClipboardHistory;
using OverTranslate.Views.ClipboardHistory;
using Xunit;

namespace OverTranslate.Tests;

public sealed partial class ScreenTranslateShellTests
{
    private static void VerifyClipboardStorageDirectory()
    {
        var root = Path.Combine(Path.GetTempPath(), "clipboard-storage-ui-" + Guid.NewGuid().ToString("N"));
        var source = Path.Combine(root, "old", "clipboard-history");
        var targetParent = Path.Combine(root, "new");
        var target = Path.Combine(targetParent, "clipboard-history");
        var data = Path.Combine(root, "old");
        var profile = Path.Combine(root, "profile");
        var binding = ClipboardHistoryStorage.BindingPath(data, profile);
        var store = new ClipboardHistoryStore(source);
        var record = store.Add(new() { Text = "storage UI fixture" });
        string? copiedFrom = null;
        var window = new ClipboardHistoryWindow(store, _ => { copiedFrom = store.Folder; return Task.CompletedTask; },
            settings: new AppSettings { ClipboardAutoPaste = false }, saveSettings: () => true,
            changeDirectory: (current, parent) => current.Relocate(Path.Combine(parent, "clipboard-history"),
                folder => ClipboardHistoryStorage.SaveFolder(binding, folder)))
        { Left = -10000, Top = -10000, ShowActivated = false };
        System.Windows.Input.InputMethod.SetIsInputMethodEnabled((TextBox)window.FindName("SearchBox"), false);
        window.SetWindowPinned(true);
        try
        {
            window.ShowPanel(); PumpUntil(() => !window.IsRefreshing);
            Assert.NotNull(window.FindName("ChangeFolderMenuItem"));
            var open = (MenuItem)window.FindName("OpenFolderMenuItem");
            Assert.Equal(source, open.ToolTip);
            var change = window.ChangeStorageDirectoryAsync(targetParent);
            PumpUntil(() => change.IsCompleted); change.GetAwaiter().GetResult();
            PumpUntil(() => !window.IsRefreshing);
            Assert.Equal(target, store.Folder);
            Assert.Equal(target, open.ToolTip);
            Assert.Contains("已立即生效", ((TextBlock)window.FindName("StatusText")).Text);
            Assert.Equal(target, ClipboardHistoryStorage.ResolveFolder(data, profile));
            change = window.ChangeStorageDirectoryAsync(targetParent);
            PumpUntil(() => change.IsCompleted); change.GetAwaiter().GetResult();
            Assert.Contains("保存目录未改变", ((TextBlock)window.FindName("StatusText")).Text);
            var copy = window.ActivateSelectedAsync();
            PumpUntil(() => copy.IsCompleted); copy.GetAwaiter().GetResult();
            Assert.Equal(target, copiedFrom);

            var blockedParent = Path.Combine(root, "blocked");
            Directory.CreateDirectory(Path.Combine(blockedParent, "clipboard-history"));
            File.WriteAllText(Path.Combine(blockedParent, "clipboard-history", "keep.txt"), "keep fixture");
            change = window.ChangeStorageDirectoryAsync(blockedParent);
            PumpUntil(() => change.IsCompleted); change.GetAwaiter().GetResult();
            Assert.Equal(target, store.Folder);
            Assert.Contains("目录未切换", ((TextBlock)window.FindName("StatusText")).Text);
            Assert.True(File.Exists(Path.Combine(source, record.Id + ".clip")));
        }
        finally { window.Close(); if (Directory.Exists(root)) Directory.Delete(root, true); }
        VerifyClipboardStorageDoesNotBlockDismiss();
    }

    private static void VerifyClipboardStorageDoesNotBlockDismiss()
    {
        var root = Path.Combine(Path.GetTempPath(), "clipboard-storage-dismiss-" + Guid.NewGuid().ToString("N"));
        var store = new ClipboardHistoryStore(Path.Combine(root, "old", "clipboard-history"));
        store.Add(new() { Text = "slow migration fixture" });
        store.SavePlacement(new(1, 2, 560, 700, "fixture-screen"));
        using var committing = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var window = new ClipboardHistoryWindow(store, managePosition: true, settings: new AppSettings(), saveSettings: () => true,
            changeDirectory: (current, parent) => current.Relocate(Path.Combine(parent, "clipboard-history"), _ =>
            {
                committing.Set();
                if (!release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("fixture timeout");
            }));
        window.SetWindowPinned(true);
        Task? change = null;
        Task? fallback = null;
        System.Windows.Input.InputMethod.SetIsInputMethodEnabled((TextBox)window.FindName("SearchBox"), false);
        try
        {
            window.ShowPanel(); window.Left = window.Top = -10000;
            PumpUntil(() => !window.IsRefreshing);
            change = window.ChangeStorageDirectoryAsync(Path.Combine(root, "new"));
            PumpUntil(() => committing.IsSet);
            fallback = Task.Run(async () => { await Task.Delay(1500); release.Set(); });
            var elapsed = System.Diagnostics.Stopwatch.StartNew();
            window.Dismiss();
            window.ShowPanel();
            Assert.True(elapsed.ElapsedMilliseconds < 500, $"dismiss and reopen blocked for {elapsed.ElapsedMilliseconds} ms");
            release.Set();
            PumpUntil(() => change.IsCompleted); change.GetAwaiter().GetResult();
            PumpUntil(() => fallback.IsCompleted);
        }
        finally
        {
            release.Set();
            if (change is not null) PumpUntil(() => change.IsCompleted);
            if (fallback is not null) PumpUntil(() => fallback.IsCompleted);
            typeof(ClipboardHistoryWindow).GetField("_shutdown", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.SetValue(window, true);
            window.Close();
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }
}
