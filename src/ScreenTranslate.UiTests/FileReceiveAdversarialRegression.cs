using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Threading;
using OverTranslate.Services;
using OverTranslate.Services.Lan;
using OverTranslate.Views.ClipboardHistory;
using Xunit;
using Button = System.Windows.Controls.Button;

namespace OverTranslate.Tests;

public sealed partial class ScreenTranslateShellTests
{
    private static void VerifyReceiveAdversarialCases(ClipboardHistoryWindow window, string root, SyncActivityStore journal)
    {
        using var alreadyCancelled = new CancellationTokenSource();
        alreadyCancelled.Cancel();
        var cancelled = ClipboardHistoryWindow.ConfirmIncomingFilesAsync("fixture", new("file-offer", Name: "expired.zip"), root, alreadyCancelled.Token);
        Assert.True(cancelled.IsCompletedSuccessfully); Assert.Null(cancelled.Result);
        Assert.Empty(((StackPanel)window.FindName("IncomingRequestsPanel")).Children);
        var completedCount = ((ItemsControl)window.FindName("ReceivedFilesList")).Items.Count;
        foreach (var state in new[] { SyncState.Waiting, SyncState.Running, SyncState.Failed, SyncState.Rejected })
            journal.Update(new("receive-state-" + state, SyncKind.Files, true, "fixture", state, Folder: root));
        journal.Update(new("receive-outgoing", SyncKind.Files, false, "fixture", SyncState.Completed, Folder: root));
        Assert.Equal(completedCount, ((ItemsControl)window.FindName("ReceivedFilesList")).Items.Count);
        var failures = new List<Exception>();
        foreach (var check in new Action[] { () => VerifyReceivePasteTarget(window, root), () => VerifyCancelledReceiveFolderPicker(window, root) })
        {
            try { check(); }
            catch (Exception ex) { failures.Add(ex); }
        }
        if (failures.Count > 0) throw new AggregateException(failures);
    }

    private static void VerifyReceivePasteTarget(ClipboardHistoryWindow window, string root)
    {
        var fields = new[] { "_target", "_focus", "_process" }.Select(n => typeof(OriginalApplicationPaste).GetField(n, BindingFlags.Static | BindingFlags.NonPublic)!).ToArray();
        var saved = fields.Select(f => f.GetValue(null)).ToArray();
        var pasteTarget = typeof(ClipboardHistoryWindow).GetField("_pasteTarget", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var savedPasteTarget = pasteTarget.GetValue(window);
        var cover = new Window { Width = 300, Height = 180, Title = "隔离粘贴目标夹具" };
        Task<string?>? request = null;
        try
        {
            cover.Show(); KeyboardForeground.Activate(cover);
            var expected = new OriginalApplicationPaste.Target(new WindowInteropHelper(cover).Handle, IntPtr.Zero, (uint)Environment.ProcessId);
            // 仅设置本测试进程的跟踪状态；不写剪贴板，也不向任何窗口发送粘贴。
            foreach (var state in new[] { "hidden", "minimized", "visible" })
            {
                fields[0].SetValue(null, expected.Window); fields[1].SetValue(null, expected.Focus); fields[2].SetValue(null, expected.Process);
                var previousTarget = new OriginalApplicationPaste.Target(IntPtr.Zero, IntPtr.Zero, 0);
                pasteTarget.SetValue(window, previousTarget);
                if (state == "hidden") window.Hide();
                else { window.Show(); window.WindowState = state == "minimized" ? WindowState.Minimized : WindowState.Normal; }
                request = ClipboardHistoryWindow.ConfirmIncomingFilesAsync("fixture", new("file-offer", Name: "paste-target.zip"), root, CancellationToken.None);
                Assert.True(window.IsVisible);
                Assert.Equal(state == "visible" ? previousTarget : expected, (OriginalApplicationPaste.Target)pasteTarget.GetValue(window)!);
                Assert.Single(((StackPanel)window.FindName("IncomingRequestsPanel")).Children.OfType<FileReceiveCard>()).Reject();
                PumpUntil(() => request.IsCompleted);
            }
        }
        finally
        {
            foreach (var card in ((StackPanel)window.FindName("IncomingRequestsPanel")).Children.OfType<FileReceiveCard>()) card.Reject();
            if (request is not null) PumpUntil(() => request.IsCompleted);
            for (var i = 0; i < fields.Length; i++) fields[i].SetValue(null, saved[i]);
            pasteTarget.SetValue(window, savedPasteTarget);
            cover.Close();
        }
    }

    private static void VerifyCancelledReceiveFolderPicker(ClipboardHistoryWindow window, string root)
    {
        window.Width = 560; window.Height = 700;
        using var cancellation = new CancellationTokenSource();
        var request = ClipboardHistoryWindow.ConfirmIncomingFilesAsync("fixture", new("file-offer", Name: "cancel-picker.zip"), root, cancellation.Token);
        var panel = (StackPanel)window.FindName("IncomingRequestsPanel");
        var card = Assert.IsType<FileReceiveCard>(Assert.Single(panel.Children.Cast<object>()));
        var owner = new WindowInteropHelper(window).Handle;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
        var deadline = DateTime.UtcNow.AddSeconds(4);
        var cancelledAt = DateTime.MaxValue;
        var sawPicker = false; var forcedCleanup = false; var phase = 0;
        IntPtr picker = IntPtr.Zero;
        timer.Tick += (_, _) =>
        {
            if (phase == 0)
            {
                picker = ReceiveGetLastPopup(owner);
                if (picker != IntPtr.Zero && picker != owner && NativePasteIsWindowVisible(picker))
                { sawPicker = true; cancelledAt = DateTime.UtcNow; cancellation.Cancel(); phase = 1; }
            }
            else if (request.IsCompleted)
            {
                var closedOnCancel = !NativePasteIsWindowVisible(picker) && window.IsEnabled;
                if (closedOnCancel || DateTime.UtcNow > cancelledAt.AddMilliseconds(500))
                {
                    if (!closedOnCancel) { forcedCleanup = true; ReceivePostMessage(picker, 0x10, IntPtr.Zero, IntPtr.Zero); }
                    timer.Stop();
                }
            }
            if (DateTime.UtcNow > deadline)
            {
                cancellation.Cancel();
                if (picker != IntPtr.Zero && picker != owner) { forcedCleanup = true; ReceivePostMessage(picker, 0x10, IntPtr.Zero, IntPtr.Zero); }
                timer.Stop();
            }
        };
        try
        {
            timer.Start();
            ((Button)card.FindName("ChooseFolderButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            PumpUntil(() => request.IsCompleted);
            Assert.True(sawPicker, "The native receive-folder picker must actually open.");
            Assert.True(!forcedCleanup && !NativePasteIsWindowVisible(picker) && window.IsEnabled,
                "Cancelled request left its folder picker open and the clipboard window disabled.");
            Assert.Null(request.GetAwaiter().GetResult());
            Assert.Empty(panel.Children);
            card.SelectFolder(root);
            ((Button)card.FindName("AcceptButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            ((Button)card.FindName("ChooseFolderButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Null(request.Result);
            Assert.Equal(owner, ReceiveGetLastPopup(owner));
        }
        finally
        {
            timer.Stop(); cancellation.Cancel();
            if (picker != IntPtr.Zero && picker != owner && NativePasteIsWindowVisible(picker)) ReceivePostMessage(picker, 0x10, IntPtr.Zero, IntPtr.Zero);
        }
    }

    private static void VerifyReceivedFolderOpens(ClipboardHistoryWindow window, string folder)
    {
        IntPtr explorer = IntPtr.Zero;
        dynamic shell = Activator.CreateInstance(Type.GetTypeFromProgID("Shell.Application")!)!;
        try
        {
            Visuals<Button>(window).Single(b => Equals(b.Content, "打开保存文件夹")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            PumpUntil(() =>
            {
                object windows = shell.Windows();
                try
                {
                    foreach (dynamic item in (System.Collections.IEnumerable)windows)
                    {
                        try
                        {
                            if (!string.Equals((string)item.LocationName, Path.GetFileName(folder), StringComparison.OrdinalIgnoreCase)) continue;
                            if (Uri.TryCreate((string)item.LocationURL, UriKind.Absolute, out var uri) && uri.IsFile
                                && string.Equals(uri.LocalPath.TrimEnd('\\'), Path.GetFullPath(folder).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
                            { explorer = new IntPtr((long)item.HWND); return true; }
                        }
                        finally { Marshal.FinalReleaseComObject(item); }
                    }
                    return false;
                }
                finally { Marshal.FinalReleaseComObject(windows); }
            });
            Assert.Equal("", ((TextBlock)window.FindName("ReceiveErrorText")).Text);
        }
        finally
        {
            // 只关闭实际路径精确匹配本次临时收件目录的资源管理器窗口。
            if (explorer != IntPtr.Zero) ReceivePostMessage(explorer, 0x10, IntPtr.Zero, IntPtr.Zero);
            Marshal.FinalReleaseComObject(shell);
            window.ShowPanel();
        }
    }

    [DllImport("user32.dll", EntryPoint = "GetLastActivePopup")]
    private static extern IntPtr ReceiveGetLastPopup(IntPtr owner);
    [DllImport("user32.dll", EntryPoint = "PostMessageW")]
    private static extern bool ReceivePostMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
}
