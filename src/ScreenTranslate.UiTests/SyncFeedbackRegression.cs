using System.Windows;
using System.Windows.Controls;
using OverTranslate.Services.ClipboardHistory;
using OverTranslate.Services.Lan;
using OverTranslate.Views.ClipboardHistory;
using Xunit;
using Button = System.Windows.Controls.Button;

namespace OverTranslate.Tests;

public sealed partial class ScreenTranslateShellTests
{
    private static void VerifySyncFeedbackAndFilePicker()
    {
        var root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "sync-feedback-ui-" + Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(root);
        var path = System.IO.Path.Combine(root, "activity.json");
        var journal = new SyncActivityStore(path);
        journal.Update(new("clipboard", SyncKind.Clipboard, false, "另一台电脑", SyncState.Completed));
        journal.Update(new("library", SyncKind.Library, true, "另一台电脑", SyncState.Rejected, Issue: SyncIssue.Paused));
        journal.Update(new("files", SyncKind.Files, false, "另一台电脑", SyncState.Waiting, "项目文件", Total: 1024));
        var window = new ClipboardHistoryWindow(new ClipboardHistoryStore(System.IO.Path.Combine(root, "clipboard")), _ => Task.CompletedTask,
            settings: new(), saveSettings: () => true, syncActivities: journal)
        { Left = -10000, Top = -10000, ShowActivated = false };
        try
        {
            window.Show(); window.UpdateLayout();
            var summary = (TextBlock)window.FindName("SyncSummaryText");
            Assert.Contains("剪贴板：已完成", summary.Text);
            Assert.Contains("资料库：未被接收", summary.Text);
            Assert.Contains("文件：等待对方接收", summary.Text);
            Assert.True(((Button)window.FindName("SendToDeviceButton")).IsEnabled);
            Assert.True(window.AllowDrop);
            Assert.Equal(Visibility.Visible, ((ProgressBar)window.FindName("SyncProgress")).Visibility);
            Render(window, "clipboard-sync-status-light", 560, 700);
            ApplyTheme(System.Windows.Application.Current, "Dark");
            window.Width = 460; window.Height = 400; window.UpdateLayout();
            Render(window, "clipboard-sync-status-dark-minimum", 460, 400);
            Assert.True(((Button)window.FindName("SendToDeviceButton")).ActualWidth > 60);
            Assert.True(((TextBlock)window.FindName("SyncConnectionText")).ActualWidth < 350);
            ApplyTheme(System.Windows.Application.Current, "Light");
            journal.Update(new("files", SyncKind.Files, false, "另一台电脑", SyncState.Rejected));
            Assert.Contains("文件：未被接收", summary.Text);
            Assert.Equal(Visibility.Collapsed, ((ProgressBar)window.FindName("SyncProgress")).Visibility);
            var restored = new SyncActivityStore(path);
            var history = new SyncActivityWindow(restored);
            history.Show(); history.UpdateLayout();
            try
            {
                Assert.Equal(3, ((ListBox)history.FindName("ActivityList")).Items.Count);
                Assert.Contains(Visuals<TextBlock>(history), text => text.IsVisible && text.Text == "发送至");
                Assert.Contains(Visuals<TextBlock>(history), text => text.IsVisible && text.Text == "接收自");
                Render(history, "sync-history-light", 680, 560);
            }
            finally { history.Close(); }
            foreach (var (peer, supported, canSend) in new[] { ("", false, false), ("旧设备", false, false), ("另一台电脑", true, true) })
            {
                var picker = new FileSendWindow(peer, supported, [System.IO.Path.Combine(root, "project")]);
                picker.Show(); picker.UpdateLayout();
                try
                {
                    Assert.Equal(canSend, ((Button)picker.FindName("SendButton")).IsEnabled);
                    if (peer.Length > 0)
                    {
                        var recipients = (ComboBox)picker.FindName("RecipientBox");
                        Assert.Equal(peer, ((LanDeviceChoice)recipients.SelectedItem).Display);
                        Assert.Contains(Visuals<TextBlock>(recipients), x => x.Text == peer);
                    }
                    var pickerRoot = (FrameworkElement)picker.Content;
                    Render(picker, "file-send-" + (peer.Length == 0 ? "offline" : supported ? "ready" : "old-peer"),
                        (int)Math.Ceiling(pickerRoot.ActualWidth), (int)Math.Ceiling(pickerRoot.ActualHeight));
                }
                finally { picker.Close(); }
            }
            var receiver = new FileReceiveWindow("另一台电脑", new("file-offer", Name: "项目文件.zip", Length: 1024));
            receiver.Show(); receiver.UpdateLayout();
            try
            {
                Assert.False(((Button)receiver.FindName("AcceptButton")).IsEnabled);
                Assert.Contains("另一台电脑", ((TextBlock)receiver.FindName("OfferText")).Text);
                var receiverRoot = (FrameworkElement)receiver.Content;
                Render(receiver, "file-receive-confirm", (int)Math.Ceiling(receiverRoot.ActualWidth), (int)Math.Ceiling(receiverRoot.ActualHeight));
            }
            finally { receiver.Close(); }
            var deviceWindow = new DeviceSyncWindow(() => new[]
            {
                new LanDeviceRow("a", "DELL-MY", "192.168.1.20:51000", "已连接", "剪贴板开启 · 资料库暂停", "文件：已完成", true, true, DateTimeOffset.Now),
                new LanDeviceRow("b", "FLIPPED", "192.168.1.30:52000", "暂时无法连接 · 30 秒后重试", "剪贴板开启 · 资料库开启", "资料库：已中断", true)
            }, journal);
            deviceWindow.Show(); deviceWindow.UpdateLayout();
            try
            {
                var devices = (ListBox)deviceWindow.FindName("DeviceList");
                Assert.Equal(2, devices.Items.Count);
                devices.SelectedIndex = 1;
                Assert.True(((Button)deviceWindow.FindName("RetryButton")).IsEnabled);
                Render(deviceWindow, "devices-multiple-status", 760, 600);
                deviceWindow.Width = 520; deviceWindow.Height = 420; deviceWindow.UpdateLayout();
                Render(deviceWindow, "devices-minimum", 520, 420);
            }
            finally { deviceWindow.Close(); }
            var multiplePicker = new FileSendWindow(new[] { new LanDeviceChoice("a", "电脑 A", true), new LanDeviceChoice("b", "电脑 B", true) }, [root]);
            multiplePicker.Show(); multiplePicker.UpdateLayout();
            try
            {
                ((ComboBox)multiplePicker.FindName("RecipientBox")).SelectedIndex = 1;
                Assert.Equal("b", multiplePicker.SelectedDeviceId);
                Assert.True(((Button)multiplePicker.FindName("SendButton")).IsEnabled);
            }
            finally { multiplePicker.Close(); }
        }
        finally
        {
            window.Close();
            System.IO.Directory.Delete(root, true);
        }
    }
}
