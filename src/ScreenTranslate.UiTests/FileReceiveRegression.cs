using System.IO;
using System.Net;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using OverTranslate.Services;
using OverTranslate.Services.ClipboardHistory;
using OverTranslate.Services.Lan;
using OverTranslate.Views.ClipboardHistory;
using Xunit;
using Button = System.Windows.Controls.Button;

namespace OverTranslate.Tests;

public sealed partial class ScreenTranslateShellTests
{
    private static void VerifyIncomingFileAttention(string root)
    {
        var journal = new SyncActivityStore(Path.Combine(root, "receive-activity.json"));
        var window = new ClipboardHistoryWindow(new ClipboardHistoryStore(Path.Combine(root, "receive-clipboard")),
            settings: new(), saveSettings: () => true, syncActivities: journal);
        // 本用例不验证输入法，避免本机 IME 的原生重转换回调干扰收件焦点测试。
        System.Windows.Input.InputMethod.SetIsInputMethodEnabled(window, false);
        var transfer = new LanTransferWindow(Path.Combine(root, "attention-prefs.json"));
        var node = (LanNode)Field(transfer, "_node")!;
        node.Activities = journal; node.ConfirmPairAsync = (_, _) => Task.FromResult(true);
        var current = typeof(ClipboardHistoryWindow).GetField("_current", BindingFlags.NonPublic | BindingFlags.Static)!;
        var previous = current.GetValue(null); current.SetValue(null, window);
        var connections = (List<LanTransferWindow>)typeof(LanTransferWindow).GetField("Connections", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
        connections.Add(transfer);
        var receive = Path.Combine(root, "inline-received"); Directory.CreateDirectory(receive);
        var source = Path.Combine(root, "receive-fixture.txt"); File.WriteAllText(source, "inline receive fixture");
        var sender = new LanNode("fixture sender", () => root);
        var cover = new Window { Width = 700, Height = 600, Title = "收件遮挡测试" };
        var pending = (StackPanel)window.FindName("IncomingRequestsPanel");
        void Complete(Task task) { PumpUntil(() => task.IsCompleted); task.GetAwaiter().GetResult(); }
        void Click(object owner, string name)
        {
            KeyboardForeground.Activate(Window.GetWindow((FrameworkElement)owner));
            ((Button)((FrameworkElement)owner).FindName(name)).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        }
        try
        {
            foreach (var (bytes, size) in new[] { (0L, "0 字节"), (1024L, "1 KB"), (1024L * 1024, "1 MB"), (64L * 1024 * 1024 * 1024, "64 GB") })
            {
                var preview = new FileReceiveCard("fixture", new("file-offer", Name: "fixture.zip", Length: bytes), receive);
                Assert.Contains($"传输大小：{size}", ((TextBlock)preview.FindName("OfferText")).Text);
            }
            Complete(node.ListenAsync(IPAddress.Loopback, 0));
            Complete(sender.ConnectAsync(IPAddress.Loopback, node.Port, node.PairCode));
            window.ShowPanel();
            typeof(ClipboardHistoryWindow).GetMethod("AddSendSources", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(window, [new[] { source }]);
            var selected = Assert.IsType<LanDeviceChoice>(((ComboBox)window.FindName("SendRecipientBox")).SelectedItem);
            foreach (var state in new[] { "hidden", "visible", "minimized" })
            {
                if (state == "hidden") window.Hide();
                else window.ShowPanel();
                if (state == "minimized") window.WindowState = WindowState.Minimized;
                using var cancellation = new CancellationTokenSource();
                var sending = sender.SendBundleAsync([source], cancellation.Token);
                PumpUntil(() => pending.Children.Count == 1);
                window.UpdateLayout();
                Assert.True(window.IsVisible); Assert.Equal(WindowState.Normal, window.WindowState);
                Assert.True(((ListBoxItem)window.FindName("SyncTab")).IsSelected);
                Assert.True(((FrameworkElement)window.FindName("ReceivePageContent")).IsVisible);
                Assert.False(((FrameworkElement)window.FindName("SendActionsPanel")).IsVisible);
                Assert.True(window.Topmost); Assert.True(window.ShowInTaskbar);
                Assert.True((WindowZOrderDiagnostics.ExStyle(new WindowInteropHelper(window).Handle) & 8) != 0);
                Assert.Contains("1", ((Button)window.FindName("ViewReceivedFilesButton")).Content.ToString());
                Assert.Equal("设备与同步", ((ListBoxItem)window.FindName("SyncTab")).Content);
                Assert.DoesNotContain(System.Windows.Application.Current.Windows.Cast<Window>(), w => w.GetType().Name == "FileReceiveWindow");
                var card = Assert.IsType<FileReceiveCard>(pending.Children[0]);
                Assert.False(((Button)card.FindName("AcceptButton")).IsEnabled);
                Assert.Throws<InvalidDataException>(() => card.SelectFolder(@"\\server\share"));
                cover.Show(); cover.Activate();
                PumpUntil(() => !window.IsActive);
                Assert.True(window.IsVisible); window.SetWindowPinned(false); Assert.True(window.Topmost);
                Click(window, "BackToSendButton");
                Assert.True(((FrameworkElement)window.FindName("SendPageContent")).IsVisible);
                Assert.Equal(source, Assert.Single(((ListBox)window.FindName("SendSourceList")).Items.Cast<string>()));
                Assert.Equal(selected.Id, ((LanDeviceChoice)((ComboBox)window.FindName("SendRecipientBox")).SelectedItem).Id);
                Click(window, "ViewReceivedFilesButton");
                Assert.True(((FrameworkElement)window.FindName("ReceivePageContent")).IsVisible);
                var drop = (System.Windows.DragEventArgs)Activator.CreateInstance(typeof(System.Windows.DragEventArgs),
                    BindingFlags.Instance | BindingFlags.NonPublic, null,
                    [new System.Windows.DataObject(System.Windows.DataFormats.FileDrop, new[] { source }),
                        System.Windows.DragDropKeyStates.LeftMouseButton, System.Windows.DragDropEffects.Copy, window, new Point(10, 10)], null)!;
                drop.RoutedEvent = System.Windows.DragDrop.PreviewDropEvent; window.RaiseEvent(drop);
                Assert.True(((FrameworkElement)window.FindName("SendPageContent")).IsVisible);
                Assert.Single(((ListBox)window.FindName("SendSourceList")).Items);
                Click(window, "ViewReceivedFilesButton");
                if (state == "hidden")
                {
                    Render(window, "file-receive-inline-light", 560, 700);
                    Click(card, "RejectButton"); Complete(sending); Assert.False(sending.Result);
                }
                else if (state == "visible")
                {
                    card.SelectFolder(receive); Click(card, "AcceptButton"); Complete(sending); Assert.True(sending.Result);
                    PumpUntil(() => ((ItemsControl)window.FindName("ReceivedFilesList")).Items.Count == 1);
                    var saved = Assert.IsType<SyncActivity>(((ItemsControl)window.FindName("ReceivedFilesList")).Items[0]);
                    Assert.Equal("inline receive fixture", File.ReadAllText(Path.Combine(saved.Folder, "receive-fixture.txt")));
                    // 请求完成后恢复失焦收起；固定历史窗口，避免资源管理器抢焦点影响目录验证。
                    window.SetWindowPinned(true); window.ShowPanel(); Click(window, "ViewReceivedFilesButton");
                    PumpUntil(() => window.IsVisible && Visuals<Button>(window).Any(b => Equals(b.Content, "打开保存文件夹") && b.IsVisible));
                    VerifyReceivedFolderOpens(window, saved.Folder);
                    // 删除测试生成的目录，验证打开失败时只在接收页提示。
                    Directory.Delete(saved.Folder, true);
                    Visuals<Button>(window).Single(b => Equals(b.Content, "打开保存文件夹")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    Assert.Contains("无法打开保存文件夹", ((TextBlock)window.FindName("ReceiveErrorText")).Text);
                    Assert.DoesNotContain("无法打开保存文件夹", ((TextBlock)window.FindName("StatusText")).Text);
                    window.SetWindowPinned(false);
                }
                else
                {
                    window.Width = 460; window.Height = 400;
                    ApplyTheme(System.Windows.Application.Current, "Dark");
                    window.UpdateLayout(); ((Button)card.FindName("RejectButton")).BringIntoView(); window.UpdateLayout();
                    AssertInside(window, (Button)card.FindName("RejectButton"));
                    Render(window, "file-receive-inline-dark-minimum", 460, 400);
                    cancellation.Cancel(); PumpUntil(() => sending.IsCompleted);
                    Assert.ThrowsAny<OperationCanceledException>(() => sending.GetAwaiter().GetResult());
                }
                PumpUntil(() => pending.Children.Count == 0);
                Assert.False(window.Topmost); Assert.False(window.ShowInTaskbar);
            }
            window.SetWindowPinned(true);
            using var cancelled = new CancellationTokenSource();
            var first = ClipboardHistoryWindow.ConfirmIncomingFilesAsync("电脑 A", new("file-offer", Name: new string('长', 600), Length: 1024), receive, cancelled.Token);
            var second = ClipboardHistoryWindow.ConfirmIncomingFilesAsync("电脑 B", new("file-offer", Name: "second.zip", Length: 0), receive, CancellationToken.None);
            Assert.Equal(2, pending.Children.Count);
            window.UpdateLayout();
            var longCard = (FileReceiveCard)pending.Children[0];
            var folderText = (TextBlock)longCard.FindName("FolderText");
            folderText.Text = @"C:\fixture\" + string.Concat(Enumerable.Repeat(@"很长的目录\", 60));
            window.UpdateLayout();
            foreach (var text in new[] { (TextBlock)longCard.FindName("OfferText"), folderText })
            {
                var contentScroll = Visuals<ScrollViewer>(longCard).Single(s => Visuals<TextBlock>(s).Contains(text));
                Assert.True(contentScroll.ScrollableHeight > 0);
                var viewport = Visuals<ScrollContentPresenter>(contentScroll).First();
                contentScroll.ScrollToBottom(); window.UpdateLayout();
                Assert.InRange(text.TranslatePoint(new Point(0, text.ActualHeight), viewport).Y, 0, viewport.ActualHeight + 1);
                contentScroll.ScrollToTop(); window.UpdateLayout();
                Assert.InRange(text.TranslatePoint(new Point(), viewport).Y, -1, viewport.ActualHeight);
            }
            ((FileReceiveCard)pending.Children[1]).Reject(); Complete(second); Assert.Null(second.Result);
            Assert.Single(pending.Children); Assert.True(window.Topmost);
            cancelled.Cancel(); Complete(first); Assert.Null(first.Result);
            Assert.Empty(pending.Children); Assert.True(window.Topmost); Assert.True(window.WindowPinned);
            Complete(sender.ConnectAsync(IPAddress.Loopback, node.Port, node.PairCode));
            PumpUntil(() => ((Button)window.FindName("SendFilesButton")).IsEnabled);
            var outgoingDecision = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
            sender.ConfirmFilesAsync = (_, token) => outgoingDecision.Task.WaitAsync(token);
            Click(window, "BackToSendButton"); Click(window, "SendFilesButton");
            PumpUntil(() => window.IsBusy);
            var simultaneous = sender.SendBundleAsync([source]);
            PumpUntil(() => pending.Children.Count == 1);
            var simultaneousCard = (FileReceiveCard)pending.Children[0];
            Assert.True(((FrameworkElement)window.FindName("ReceivePageContent")).IsVisible);
            Assert.True(window.IsBusy);
            Assert.False(((ListBox)window.FindName("SendSourceList")).IsEnabled);
            Click(window, "BackToSendButton");
            Assert.True(((Button)window.FindName("CancelInlineSendButton")).IsEnabled);
            Assert.Single(pending.Children);
            Assert.Equal(source, Assert.Single(((ListBox)window.FindName("SendSourceList")).Items.Cast<string>()));
            Click(window, "ViewReceivedFilesButton"); simultaneousCard.SelectFolder(receive); Click(simultaneousCard, "AcceptButton");
            outgoingDecision.SetResult(root);
            Complete(simultaneous); Assert.True(simultaneous.Result); Assert.Empty(pending.Children);
            PumpUntil(() => !window.IsBusy);
            Assert.Contains("已全部送达并保存", ((TextBlock)window.FindName("SendSelectionHint")).Text);
            VerifyReceiveAdversarialCases(window, root, journal);
            VerifyTransferCompletion(window, root, journal);
            var closing = ClipboardHistoryWindow.ConfirmIncomingFilesAsync("电脑 C", new("file-offer", Name: "closing.zip"), receive, CancellationToken.None);
            Assert.False(((Button)window.FindName("PreviousReceivedPageButton")).IsEnabled);
            window.Close(); Complete(closing); Assert.Null(closing.Result);
        }
        finally
        {
            cover.Close(); window.Close(); current.SetValue(null, previous);
            Complete(transfer.ShutdownAsync()); Complete(sender.DisposeAsync().AsTask());
            ApplyTheme(System.Windows.Application.Current, "Light");
        }
    }
}
