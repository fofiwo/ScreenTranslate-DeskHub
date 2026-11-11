using System.IO;
using System.Net;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using OverTranslate.Services;
using OverTranslate.Services.Lan;
using OverTranslate.Views.ClipboardHistory;
using Xunit;
using Button = System.Windows.Controls.Button;

namespace OverTranslate.Tests;

public sealed partial class ScreenTranslateShellTests
{
    private static void VerifyTransferCompletion(ClipboardHistoryWindow window, string root, SyncActivityStore journal)
    {
        ((Button)window.FindName("BackToSendButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        var remove = Path.Combine(root, "remove-one.txt"); var retain = Path.Combine(root, "retain-one.txt");
        File.WriteAllText(remove, "remove fixture"); File.WriteAllText(retain, "retain fixture");
        typeof(ClipboardHistoryWindow).GetMethod("AddSendSources", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, [new[] { remove, retain }]);
        window.UpdateLayout();
        Visuals<Button>(window).Single(b => Equals(b.Content, "移除") && Equals(b.DataContext, remove)).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        var paths = ((ListBox)window.FindName("SendSourceList")).Items.Cast<string>().ToArray();
        Assert.DoesNotContain(remove, paths); Assert.Contains(retain, paths); Assert.True(File.Exists(remove));
        var now = DateTimeOffset.UtcNow;
        var card = new FileReceiveCard("fixture", new("file-offer", Name: "countdown.zip"), root, now.AddMinutes(5));
        card.SelectFolder(root); card.UpdateRemaining(now.AddMinutes(4));
        Assert.Contains("01:00", ((TextBlock)card.FindName("RemainingText")).Text);
        card.UpdateRemaining(now.AddMinutes(5));
        Assert.Null(card.Decision.Result);
        ((Button)card.FindName("AcceptButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.Null(card.Decision.Result);

        for (var i = 0; i < 125; i++) journal.Update(new("receipt-page-" + i, SyncKind.Files, true, "fixture", SyncState.Completed, Folder: root));
        var count = journal.ReceivedSnapshot().Count;
        for (var i = 0; i < 110; i++) journal.Update(new("eviction-" + i, SyncKind.Clipboard, false, "fixture", SyncState.Completed));
        Assert.Equal(count, journal.ReceivedSnapshot().Count);
        Assert.Equal(count, new SyncActivityStore(Path.Combine(root, "receive-activity.json")).ReceivedSnapshot().Count);
        ((Button)window.FindName("ViewReceivedFilesButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        var seen = new HashSet<string>();
        var list = (ItemsControl)window.FindName("ReceivedFilesList");
        var next = (Button)window.FindName("NextReceivedPageButton");
        for (var page = 0; ; page++)
        {
            Assert.InRange(list.Items.Count, 1, 50);
            foreach (SyncActivity item in list.Items) Assert.True(seen.Add(item.Id));
            if (!next.IsEnabled) break;
            Assert.True(page < 10); next.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        }
        Assert.Equal(count, seen.Count); Assert.Contains("receipt-page-0", seen);
        ((Button)window.FindName("PreviousReceivedPageButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.True(next.IsEnabled);
        VerifyRetryFeedback(window, root, remove, retain);
    }

    private static void VerifyRetryFeedback(ClipboardHistoryWindow owner, string root, string remove, string source)
    {
        var folder = Path.Combine(root, "retry-received");
        var journal = new SyncActivityStore(Path.Combine(root, "retry-activity.json"));
        journal.Update(new("failed-batch", SyncKind.Files, false, "fixture", SyncState.Failed, Sources: [source]));
        var history = new SyncActivityWindow(journal) { Owner = owner };
        var transfer = new LanTransferWindow(Path.Combine(root, "retry-preferences.json"));
        var node = (LanNode)Field(transfer, "_node")!;
        var host = new LanNode("retry-fixture", () => folder);
        var connections = (List<LanTransferWindow>)typeof(LanTransferWindow).GetField("Connections", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
        void Complete(Task task) { PumpUntil(() => task.IsCompleted); task.GetAwaiter().GetResult(); }
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(10) };
        try
        {
            host.ConfirmPairAsync = (_, _) => Task.FromResult(true);
            Complete(host.ListenAsync(IPAddress.Loopback, 0)); connections.Add(transfer);
            Complete(node.ConnectAsync(IPAddress.Loopback, host.Port, host.PairCode));
            history.Show(); KeyboardForeground.Activate(history);
            ((ListBox)history.FindName("ActivityList")).SelectedIndex = 0;
            var retry = (Button)history.FindName("RetryButton");
            var feedback = (TextBlock)history.FindName("RetryFeedback");
            var accept = false; var sawPicker = false;
            timer.Tick += (_, _) =>
            {
                var picker = System.Windows.Application.Current.Windows.OfType<FileSendWindow>().SingleOrDefault(w => ReferenceEquals(w.Owner, history));
                if (picker is null) return;
                sawPicker = true; timer.Stop();
                Assert.False(retry.IsEnabled);
                if (!accept) { picker.Close(); return; }
                var box = (ComboBox)picker.FindName("RecipientBox");
                box.SelectedItem = box.Items.Cast<LanDeviceChoice>().Single(x => x.Id == (string)Field(transfer, "_deviceId")!);
                ((Button)picker.FindName("SendButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            };
            foreach (var outcome in new[] { "cancel", "rejected", "completed" })
            {
                accept = outcome != "cancel"; sawPicker = false;
                host.ConfirmFilesAsync = (_, _) => Task.FromResult<string?>(outcome == "completed" ? folder : null);
                timer.Start(); retry.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                PumpUntil(() => retry.IsEnabled);
                Assert.True(sawPicker);
                Assert.Contains(outcome == "cancel" ? "未开始" : outcome == "rejected" ? "未接收" : "已全部送达", feedback.Text);
            }
            Assert.Equal("retain fixture", File.ReadAllText(Assert.Single(Directory.GetFiles(folder, "retain-one.txt", SearchOption.AllDirectories))));
            var removePicker = new FileSendWindow("fixture", true, [remove, source]);
            try
            {
                removePicker.Show(); removePicker.UpdateLayout();
                Visuals<Button>(removePicker).Single(b => Equals(b.Content, "移除") && Equals(b.DataContext, remove)).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.Equal(source, Assert.Single(removePicker.Paths)); Assert.True(File.Exists(remove));
            }
            finally { removePicker.Close(); }
        }
        finally { timer.Stop(); history.Close(); Complete(transfer.ShutdownAsync()); Complete(host.DisposeAsync().AsTask()); }
    }
}
