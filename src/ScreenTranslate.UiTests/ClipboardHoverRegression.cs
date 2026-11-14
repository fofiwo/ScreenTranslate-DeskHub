using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using OverTranslate.Models;
using OverTranslate.Services;
using OverTranslate.Services.ClipboardHistory;
using OverTranslate.Views.ClipboardHistory;
using Xunit;

namespace OverTranslate.Tests;

public sealed partial class ScreenTranslateShellTests
{
    private static void VerifyClipboardPointerActivation()
    {
        var folder = Path.Combine(Path.GetTempPath(), "clipboard-hover-" + Guid.NewGuid().ToString("N"));
        var store = new ClipboardHistoryStore(folder);
        for (var i = 0; i < 45; i++) store.Add(new() { Text = "滚轮激活测试 " + i });
        var copies = 0;
        var window = new ClipboardHistoryWindow(store, _ => { copies++; return Task.CompletedTask; },
            settings: new AppSettings(), saveSettings: () => true,
            syncActivities: new OverTranslate.Services.Lan.SyncActivityStore(Path.Combine(folder, "activity.json")))
        { Left = 80, Top = 80, ShowActivated = false };
        InputMethod.SetIsInputMethodEnabled((TextBox)window.FindName("SearchBox"), false);
        var other = new Window { Width = 160, Height = 80, Left = 700, Top = 80,
            WindowStartupLocation = WindowStartupLocation.Manual, ShowInTaskbar = false };
        Assert.True(HoverGetCursorPos(out var cursor));
        try
        {
            Assert.True(HoverSetCursorPos(10, 10));
            // 测试中保持窗口可见，以独立建立“已显示但未激活”的前提。
            window.SetWindowPinned(true);
            window.ShowPanel();
            var handle = new System.Windows.Interop.WindowInteropHelper(window).Handle;
            // 测试宿主以隐藏窗口启动，显式显示本测试创建的窗口。
            NativePasteShowWindow(handle, 5);
            Assert.True(NativePasteIsWindowVisible(handle));
            PumpUntil(() => !window.IsRefreshing);
            Assert.NotNull(window.SelectedRecord);
            window.UpdateLayout();
            other.Show(); other.Activate();
            var otherHandle = new System.Windows.Interop.WindowInteropHelper(other).Handle;
            NativePasteShowWindow(otherHandle, 5);
            KeyboardForeground.Activate(other);
            Assert.Equal(otherHandle, NativePasteGetForeground());
            PumpUntil(() => other.IsActive && !window.IsActive);
            var list = (ListBox)window.FindName("HistoryList");
            var scroll = Visuals<ScrollViewer>(list).First();
            var selection = window.SelectedRecord!.Id;
            var targetField = typeof(ClipboardHistoryWindow).GetField("_pasteTarget", BindingFlags.Instance | BindingFlags.NonPublic)!;
            var target = new OriginalApplicationPaste.Target(new IntPtr(1234), new IntPtr(5678), 123);
            targetField.SetValue(window, target);
            var inside = list.PointToScreen(new Point(list.ActualWidth / 2, 100));
            Assert.True(HoverSetCursorPos((int)inside.X, (int)inside.Y));
            PumpUntil(() => window.IsActive && NativePasteGetForeground() == handle);
            Assert.Equal(target, (OriginalApplicationPaste.Target)targetField.GetValue(window)!);

            // 确认真实前台后投递原生滚轮消息，不发送鼠标点击或向用户应用注入输入。
            var wheelEvents = 0;
            string? wheelSource = null;
            window.PreviewMouseWheel += (_, e) => { wheelEvents++; wheelSource = e.OriginalSource?.GetType().Name; };
            Assert.Equal(handle, NativePasteGetForeground());
            var coordinates = ((int)inside.X & 0xffff) | (((int)inside.Y & 0xffff) << 16);
            Assert.True(HoverPostMessage(handle, 0x020A, new IntPtr(-120 << 16), new IntPtr(coordinates)));
            try { PumpUntil(() => scroll.VerticalOffset > 0); }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"Wheel events={wheelEvents}, source={wheelSource}, focused={Keyboard.FocusedElement?.GetType().Name}, active={window.IsActive}, mouseOver={list.IsMouseOver}, scrollable={scroll.ScrollableHeight}", ex);
            }
            Assert.Equal(selection, window.SelectedRecord!.Id);
            Assert.Equal(0, copies);

            Keyboard.Focus(list);
            var focus = Keyboard.FocusedElement;
            var offset = scroll.VerticalOffset;
            Assert.True(HoverSetCursorPos(10, 10));
            PumpUntil(() => !window.IsMouseOver);
            Assert.True(HoverSetCursorPos((int)inside.X, (int)inside.Y));
            PumpUntil(() => window.IsMouseOver);
            window.Dispatcher.Invoke(() => { }, DispatcherPriority.Background);
            Assert.Same(focus, Keyboard.FocusedElement);
            Assert.Equal(offset, scroll.VerticalOffset);
            Assert.Equal(0, copies);

            window.Dismiss();
            window.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, 0) { RoutedEvent = Mouse.MouseEnterEvent });
            window.Dispatcher.Invoke(() => { }, DispatcherPriority.Background);
            Assert.False(window.IsVisible);
        }
        finally
        {
            window.Close(); other.Close();
            HoverSetCursorPos(cursor.X, cursor.Y);
            Directory.Delete(folder, true);
        }
    }

    [DllImport("user32.dll", EntryPoint = "SetCursorPos")]
    private static extern bool HoverSetCursorPos(int x, int y);
    [DllImport("user32.dll", EntryPoint = "GetCursorPos")]
    private static extern bool HoverGetCursorPos(out System.Drawing.Point point);
    [DllImport("user32.dll", EntryPoint = "PostMessageW")]
    private static extern bool HoverPostMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
}
