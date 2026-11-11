using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using OverTranslate.Services;
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
        var clipboard = new ClipboardHistoryStore(System.IO.Path.Combine(root, "clipboard"));
        clipboard.Add(new() { Text = "同步页面不应复制这条历史", Source = "fixture.exe" });
        var copies = 0;
        var window = new ClipboardHistoryWindow(clipboard, _ => { copies++; return Task.CompletedTask; },
            paste: () => Task.FromResult(false),
            settings: new(), saveSettings: () => true, syncActivities: journal)
        { Left = -10000, Top = -10000, ShowActivated = false };
        try
        {
            window.Show(); window.UpdateLayout();
            PumpUntil(() => !window.IsRefreshing);
            var tabs = (ListBox)window.FindName("TypeFilter");
            var send = (Button)window.FindName("SendFilesButton");
            Assert.Null(window.FindName("SendToDeviceButton"));
            var records = (ListBox)window.FindName("HistoryList");
            var search = (TextBox)window.FindName("SearchBox");
            Assert.Equal("全部", ((ListBoxItem)tabs.SelectedItem).Content);
            Assert.False(send.IsVisible);
            Render(window, "clipboard-sync-hidden-default", 560, 700);
            var syncTab = tabs.Items.Cast<ListBoxItem>().Single(item => Equals(item.Content, "设备与同步"));
            search.Text = "同步页面";
            tabs.SelectedItem = syncTab; window.UpdateLayout();
            Assert.True(send.IsVisible);
            Assert.False(records.IsVisible);
            Assert.False(search.IsVisible);
            Assert.False(((TextBlock)window.FindName("ActionHint")).IsVisible);
            Assert.NotNull(window.SelectedRecord);
            foreach (var key in new[] { Key.Enter, Key.Up, Key.Down })
                window.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(window), 0, key)
                    { RoutedEvent = Keyboard.PreviewKeyDownEvent });
            Assert.Equal(0, copies);
            Assert.True(window.IsVisible);
            foreach (var tab in tabs.Items.Cast<ListBoxItem>().Where(item => item != syncTab))
            {
                tabs.SelectedItem = tab; window.UpdateLayout();
                Assert.False(send.IsVisible);
                Assert.True(records.IsVisible);
                Assert.True(search.IsVisible);
            }
            Assert.Equal("同步页面", search.Text);
            tabs.SelectedItem = syncTab;
            window.ShowPanel(); window.UpdateLayout();
            Assert.Equal(0, tabs.SelectedIndex);
            Assert.False(send.IsVisible);
            tabs.SelectedItem = syncTab; window.UpdateLayout();
            var summary = (TextBlock)window.FindName("SyncSummaryText");
            Assert.Contains("剪贴板：已完成", summary.Text);
            Assert.Contains("资料库：未被接收", summary.Text);
            Assert.Contains("文件：等待对方接收", summary.Text);
            Assert.False(((Button)window.FindName("SendFilesButton")).IsEnabled);
            var sources = Assert.IsType<ListBox>(window.FindName("SendSourceList"));
            var recipient = Assert.IsType<ComboBox>(window.FindName("SendRecipientBox"));
            Assert.True(sources.IsVisible); Assert.True(recipient.IsVisible);
            var droppedFolder = System.IO.Path.Combine(root, "dropped-folder");
            System.IO.Directory.CreateDirectory(droppedFolder);
            var payload = new System.Windows.DataObject(System.Windows.DataFormats.FileDrop, new[] { droppedFolder });
            System.Windows.DragEventArgs RaiseDrop(RoutedEvent routedEvent)
            {
                var args = (System.Windows.DragEventArgs)Activator.CreateInstance(typeof(System.Windows.DragEventArgs),
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic, null,
                    [payload, System.Windows.DragDropKeyStates.LeftMouseButton, System.Windows.DragDropEffects.Copy,
                        sources, new Point(10, 10)], null)!;
                args.RoutedEvent = routedEvent; sources.RaiseEvent(args); return args;
            }
            Assert.Equal(System.Windows.DragDropEffects.Copy, RaiseDrop(System.Windows.DragDrop.PreviewDragOverEvent).Effects);
            Assert.True(RaiseDrop(System.Windows.DragDrop.PreviewDropEvent).Handled);
            Assert.Equal(droppedFolder, Assert.Single(sources.Items.Cast<string>()));
            RaiseDrop(System.Windows.DragDrop.PreviewDropEvent);
            Assert.Single(sources.Items);
            payload.SetData(System.Windows.DataFormats.FileDrop, new[] { System.IO.Path.Combine(root, "missing") });
            RaiseDrop(System.Windows.DragDrop.PreviewDropEvent);
            Assert.Single(sources.Items);
            Assert.Contains("无法访问", ((TextBlock)window.FindName("SendSelectionHint")).Text);
            payload.SetData(System.Windows.DataFormats.FileDrop, Enumerable.Range(0, 101)
                .Select(i => System.IO.Path.Combine(root, "too-many-" + i)).ToArray());
            RaiseDrop(System.Windows.DragDrop.PreviewDropEvent);
            Assert.Single(sources.Items);
            Assert.Contains("最多 100 项", ((TextBlock)window.FindName("SendSelectionHint")).Text);
            var standalone = System.IO.Path.Combine(root, "standalone.txt");
            System.IO.File.WriteAllText(standalone, "standalone fixture");
            payload.SetData(System.Windows.DataFormats.FileDrop, new[] { standalone });
            RaiseDrop(System.Windows.DragDrop.PreviewDropEvent);
            Assert.Equal(2, sources.Items.Count);
            Assert.False(send.IsEnabled);
            var emptyFolder = System.IO.Path.Combine(droppedFolder, "empty");
            System.IO.Directory.CreateDirectory(emptyFolder);
            System.IO.File.WriteAllText(System.IO.Path.Combine(droppedFolder, "fixture.txt"), "inline folder fixture");
            VerifyInlineFolderDelivery(window, root, send);
            ((Button)window.FindName("ClearSendSourcesButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Empty(sources.Items);
            Assert.True(window.AllowDrop);
            Assert.Equal(Visibility.Visible, ((ProgressBar)window.FindName("SyncProgress")).Visibility);
            Render(window, "clipboard-sync-status-light", 560, 700);
            ApplyTheme(System.Windows.Application.Current, "Dark");
            window.Width = 460; window.Height = 400; window.UpdateLayout();
            Render(window, "clipboard-sync-status-dark-minimum", 460, 400);
            var scroll = (ScrollViewer)window.FindName("SendPageScroll");
            scroll.ScrollToEnd(); window.UpdateLayout();
            Render(window, "clipboard-inline-drop-dark-minimum", 460, 400);
            AssertInside(window, (Button)window.FindName("SendFilesButton"));
            foreach (var tab in tabs.Items.Cast<ListBoxItem>()) AssertInside(tabs, tab);
            Assert.True(((Button)window.FindName("SendFilesButton")).ActualWidth > 60);
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
            window.ShowPanel();
            PumpUntil(() => !window.IsRefreshing);
            Assert.NotNull(window.SelectedRecord);
            var previousCopiedAt = clipboard.Read().Single().CopiedAt;
            window.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(window), 0, Key.Enter)
                { RoutedEvent = Keyboard.PreviewKeyDownEvent });
            PumpUntil(() => !window.IsBusy);
            Assert.Equal(1, copies);
            PumpUntil(() => clipboard.Read().Single().CopiedAt > previousCopiedAt);
            VerifyIncomingFileAttention(root);
        }
        finally
        {
            window.Close();
            System.IO.Directory.Delete(root, true);
        }
    }

    private static void VerifyInlineFolderDelivery(ClipboardHistoryWindow window, string root, Button send)
    {
        var receive = System.IO.Path.Combine(root, "received");
        System.IO.Directory.CreateDirectory(receive);
        var host = new LanNode("fixture-receiver", () => receive);
        var client = new LanTransferWindow(System.IO.Path.Combine(root, "send-prefs.json"));
        var connections = (List<LanTransferWindow>)typeof(LanTransferWindow).GetField("Connections",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!.GetValue(null)!;
        var node = (LanNode)Field(client, "_node")!;
        void Complete(Task task) { PumpUntil(() => task.IsCompleted); task.GetAwaiter().GetResult(); }
        try
        {
            host.ConfirmPairAsync = (_, _) => Task.FromResult(true);
            host.ConfirmFilesAsync = (_, _) => Task.FromResult<string?>(receive);
            Complete(host.ListenAsync(System.Net.IPAddress.Loopback, 0));
            connections.Add(client);
            Complete(node.ConnectAsync(System.Net.IPAddress.Loopback, host.Port, host.PairCode));
            PumpUntil(() => send.IsEnabled);
            Render(window, "clipboard-inline-folder-ready", 560, 700);
            send.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.True(window.IsBusy); Assert.False(send.IsEnabled);
            PumpUntil(() => !window.IsBusy);
            Assert.Contains("已全部送达", ((TextBlock)window.FindName("SendSelectionHint")).Text);
            var batch = Assert.Single(System.IO.Directory.GetDirectories(receive));
            Assert.Equal("inline folder fixture", System.IO.File.ReadAllText(System.IO.Path.Combine(batch, "dropped-folder", "fixture.txt")));
            Assert.True(System.IO.Directory.Exists(System.IO.Path.Combine(batch, "dropped-folder", "empty")));
            Assert.Equal("standalone fixture", System.IO.File.ReadAllText(System.IO.Path.Combine(batch, "standalone.txt")));
            host.ConfirmFilesAsync = (_, _) => Task.FromResult<string?>(null);
            send.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            PumpUntil(() => !window.IsBusy);
            Assert.Contains("未接收", ((TextBlock)window.FindName("SendSelectionHint")).Text);
            Assert.Single(System.IO.Directory.GetDirectories(receive));
            var stale = Assert.Single(LanTransferWindow.ConnectedChoices());
            VerifyDisconnectedRecipientIsNotReplaced(window, root, send, node, stale.Id, Complete);
            Complete(node.StopAsync());
            var result = LanTransferWindow.SendToDeviceAsync(stale, [System.IO.Path.Combine(root, "dropped-folder")]);
            Complete(result); Assert.Contains("连接已变化", result.Result);
            host.ConfirmFilesAsync = async (_, token) =>
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, token); return receive;
            };
            Complete(node.ConnectAsync(System.Net.IPAddress.Loopback, host.Port, host.PairCode));
            PumpUntil(() => send.IsEnabled);
            // 前一次发送结束允许失焦收起；模拟用户重新打开页面后再发起下一次发送。
            window.Show(); KeyboardForeground.Activate(window);
            send.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            PumpUntil(() => window.IsBusy);
            var openedHistory = false;
            window.Dispatcher.BeginInvoke(() =>
            {
                var history = System.Windows.Application.Current.Windows.OfType<SyncActivityWindow>().SingleOrDefault();
                if (history is not null) { openedHistory = true; history.Close(); }
            });
            ((Button)window.FindName("SyncHistoryButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.True(openedHistory); Assert.True(window.IsBusy);
            var cancel = Assert.IsType<Button>(window.FindName("CancelInlineSendButton"));
            Assert.True(cancel.IsVisible); Assert.True(cancel.IsEnabled);
            Assert.False(((Button)window.FindName("ClearSendSourcesButton")).IsEnabled);
            Assert.False(((ComboBox)window.FindName("SendRecipientBox")).IsEnabled);
            window.Width = 460; window.Height = 400; window.UpdateLayout();
            Render(window, "clipboard-inline-cancel-minimum", 460, 400);
            AssertInside(window, cancel);
            window.Width = 560; window.Height = 700; window.UpdateLayout();
            cancel.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            PumpUntil(() => !window.IsBusy);
            Assert.Contains("取消", ((TextBlock)window.FindName("SendSelectionHint")).Text);
            Assert.False(cancel.IsVisible);
            Assert.Single(System.IO.Directory.GetDirectories(receive));
        }
        finally { Complete(client.ShutdownAsync()); Complete(host.DisposeAsync().AsTask()); }
    }

    private static void VerifyDisconnectedRecipientIsNotReplaced(ClipboardHistoryWindow window, string root,
        Button send, LanNode original, string originalId, Action<Task> complete)
    {
        var backupFolder = System.IO.Path.Combine(root, "backup-received");
        System.IO.Directory.CreateDirectory(backupFolder);
        var host = new LanNode("backup-fixture", () => backupFolder);
        var client = new LanTransferWindow(System.IO.Path.Combine(root, "backup-prefs.json"));
        var connections = (List<LanTransferWindow>)typeof(LanTransferWindow).GetField("Connections",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!.GetValue(null)!;
        var node = (LanNode)Field(client, "_node")!;
        try
        {
            host.ConfirmPairAsync = (_, _) => Task.FromResult(true);
            host.ConfirmFilesAsync = (_, _) => Task.FromResult<string?>(backupFolder);
            complete(host.ListenAsync(System.Net.IPAddress.Loopback, 0)); connections.Add(client);
            complete(node.ConnectAsync(System.Net.IPAddress.Loopback, host.Port, host.PairCode));
            var recipient = (ComboBox)window.FindName("SendRecipientBox");
            PumpUntil(() => recipient.Items.Count == 2);
            recipient.SelectedItem = recipient.Items.Cast<LanDeviceChoice>().Single(p => p.Id == originalId);
            complete(original.StopAsync());
            PumpUntil(() => recipient.Items.Count == 1);
            Assert.Null(recipient.SelectedItem);
            Assert.False(send.IsEnabled);
            ((ListBox)window.FindName("TypeFilter")).SelectedIndex = 0;
            ((ListBoxItem)window.FindName("SyncTab")).IsSelected = true;
            Assert.Null(recipient.SelectedItem);
            Assert.False(send.IsEnabled);
            send.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Empty(System.IO.Directory.GetFileSystemEntries(backupFolder));
        }
        finally { complete(client.ShutdownAsync()); complete(host.DisposeAsync().AsTask()); }
    }
}
