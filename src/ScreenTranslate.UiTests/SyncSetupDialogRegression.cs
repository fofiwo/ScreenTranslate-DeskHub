using System.Windows;
using System.Windows.Controls;
using OverTranslate.Services.Lan;
using OverTranslate.Views.ClipboardHistory;
using Xunit;
using Button = System.Windows.Controls.Button;

namespace OverTranslate.Tests;

public sealed partial class ScreenTranslateShellTests
{
    private static void VerifySyncSetupDialog()
    {
        foreach (var (buttonName, expected) in new[]
        {
            ("StartPairingButton", MessageBoxResult.Yes), ("DeclineButton", MessageBoxResult.No),
            ("LaterButton", MessageBoxResult.Cancel), ("CloseButton", MessageBoxResult.Cancel)
        })
        {
            bool? chosen = null;
            var dialog = new SyncSetupWindow(value => chosen = value)
            { Left = -10000, Top = -10000, WindowStartupLocation = WindowStartupLocation.Manual, ShowActivated = false };
            dialog.Show(); dialog.UpdateLayout();
            ((Button)dialog.FindName(buttonName)).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Equal(expected, dialog.Choice);
            Assert.Equal(expected == MessageBoxResult.Cancel ? (bool?)null : expected == MessageBoxResult.Yes, chosen);
            Assert.False(dialog.IsVisible);
        }

        var fail = false;
        var window = new SyncSetupWindow(_ => { if (fail) throw new System.IO.IOException("fixture save failure"); })
        { Left = -10000, Top = -10000, WindowStartupLocation = WindowStartupLocation.Manual, ShowActivated = false };
        try
        {
            window.Show(); window.UpdateLayout();
            var primary = (Button)window.FindName("StartPairingButton");
            Assert.False(primary.IsDefault);
            Assert.True(((Button)window.FindName("LaterButton")).IsCancel);
            Render(window, "sync-setup-light", 560, 540);
            window.Width = 480; window.Height = 520; window.UpdateLayout();
            foreach (var name in new[] { "StartPairingButton", "DeclineButton", "LaterButton", "CloseButton" })
                AssertInside((FrameworkElement)window.Content, (Button)window.FindName(name));
            Render(window, "sync-setup-minimum", 480, 520);
            ApplyTheme(System.Windows.Application.Current, "Dark");
            Render(window, "sync-setup-dark", 480, 520);
            fail = true; primary.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.True(window.IsVisible); Assert.True(primary.IsEnabled);
            Assert.Equal(MessageBoxResult.Cancel, window.Choice);
            Assert.Equal(Visibility.Visible, ((TextBlock)window.FindName("ErrorText")).Visibility);
            Render(window, "sync-setup-error-dark", 480, 520);
            ApplyTheme(System.Windows.Application.Current, "Light");
            fail = false; primary.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Equal(MessageBoxResult.Yes, window.Choice); Assert.False(window.IsVisible);
        }
        finally { window.Close(); }

        VerifySyncSetupPreferenceFailure();
    }

    private static void VerifySyncSetupPreferenceFailure()
    {
        var directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "sync-setup-save-" + Guid.NewGuid());
        System.IO.Directory.CreateDirectory(directory);
        var preferencesPath = System.IO.Path.Combine(directory, "lan-preferences.json");
        var choicePath = System.IO.Path.Combine(directory, "sync-onboarding.json");
        new LanPreferences(directory, AutoSync: false, AutoSyncLibrary: false).Save(preferencesPath);
        var transfer = new LanTransferWindow(preferencesPath, readAddresses: () => [])
        { Left = -10000, Top = -10000, WindowStartupLocation = WindowStartupLocation.Manual, ShowActivated = false };
        var dialog = new SyncSetupWindow(enabled =>
        {
            if (enabled) transfer.EnableUnifiedSync();
            SyncSetupPrompt.SaveChoice(choicePath, enabled);
        })
        { Left = -10000, Top = -10000, WindowStartupLocation = WindowStartupLocation.Manual, ShowActivated = false };
        try
        {
            dialog.Show(); dialog.UpdateLayout();
            var start = (Button)dialog.FindName("StartPairingButton");
            using (var locked = new System.IO.FileStream(preferencesPath, System.IO.FileMode.Open,
                System.IO.FileAccess.Read, System.IO.FileShare.None))
            {
                start.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.True(dialog.IsVisible, "A real preference-save failure must keep setup open for retry.");
                Assert.Equal(MessageBoxResult.Cancel, dialog.Choice);
                Assert.Equal(Visibility.Visible, ((TextBlock)dialog.FindName("ErrorText")).Visibility);
                Assert.False(System.IO.File.Exists(choicePath));
                Assert.False(((CheckBox)transfer.FindName("AutoSyncBox")).IsChecked);
                Assert.False(((CheckBox)transfer.FindName("LibrarySyncBox")).IsChecked);
            }
            start.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Equal(MessageBoxResult.Yes, dialog.Choice);
            Assert.False(dialog.IsVisible);
            Assert.False(SyncSetupPrompt.NeedsPrompt(choicePath));
            var saved = LanPreferences.Load(preferencesPath);
            Assert.True(saved.AutoSync);
            Assert.True(saved.AutoSyncLibrary);
            Assert.True(((CheckBox)transfer.FindName("AutoSyncBox")).IsChecked);
            Assert.True(((CheckBox)transfer.FindName("LibrarySyncBox")).IsChecked);
            Assert.Empty(System.IO.Directory.GetFiles(directory, "*.tmp"));
        }
        finally
        {
            dialog.Close();
            var shutdown = transfer.ShutdownAsync();
            PumpUntil(() => shutdown.IsCompleted); shutdown.GetAwaiter().GetResult();
            System.IO.Directory.Delete(directory, true);
        }
    }
}
