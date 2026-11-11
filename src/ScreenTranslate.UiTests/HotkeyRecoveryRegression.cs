using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using OverTranslate.Services;
using OverTranslate.Services.ClipboardHistory;
using OverTranslate.Services.Lan;
using OverTranslate.Views.ClipboardHistory;
using Xunit;

namespace OverTranslate.Tests;

public sealed partial class ScreenTranslateShellTests
{
    private static void VerifyHotkeyRecovery()
    {
        var failures = new List<Exception>();
        foreach (var check in new Action[] { VerifyOccupiedHotkey, VerifyHotkeyRebind, VerifyOccupiedShortcutRecording, VerifyShortcutRecording, VerifyClipboardBackgroundWake })
        {
            try { check(); }
            catch (Exception ex) { failures.Add(ex); }
        }
        Assert.True(failures.Count == 0, string.Join("\n", failures));
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr window, int id, uint modifiers, uint key);

    [DllImport("user32.dll")]
    private static extern bool UnregisterHotKey(IntPtr window, int id);

    private static void VerifyOccupiedHotkey()
    {
        using var source = new HwndSource(new HwndSourceParameters("Hotkey recovery fixture") { WindowStyle = 0 });
        const uint modifiers = 7, key = 0x87; // 测试专用 Ctrl+Alt+Shift+F24。
        Assert.True(RegisterHotKey(source.Handle, 23000, modifiers, key));
        try
        {
            using var hotkey = new GlobalHotkey(GlobalHotkey.LibraryWindowId);
            hotkey.Register(source.Handle, modifiers, key);
            Assert.True(hotkey.IsRegistered, "An occupied startup shortcut must still have a working trigger.");
            Assert.True(hotkey.UsesKeyboardHook);
            var fallback = (GlobalHotkeyFallback)typeof(GlobalHotkey).GetField("_fallback",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(hotkey)!;
            var presses = 0;
            var uiThread = Environment.CurrentManagedThreadId;
            hotkey.HotkeyPressed += (_, _) => { Assert.Equal(uiThread, Environment.CurrentManagedThreadId); presses++; };
            Task.Run(() => fallback.ProcessKey(0x0104, key, modifiers)).GetAwaiter().GetResult();
            PumpUntil(() => presses == 1);
            fallback.ProcessKey(0x0105, key, modifiers);
            fallback.ProcessKey(0x0104, key, modifiers);
            hotkey.Unregister();
            System.Windows.Threading.Dispatcher.CurrentDispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            Assert.Equal(1, presses); // 录入或改键后，排队中的旧通知不能再打开窗口。
            Assert.False(fallback.IsInstalled);
            Assert.False(hotkey.IsRegistered);
            Assert.False(RegisterHotKey(source.Handle, 23001, modifiers, key));
            Assert.Equal(1409, Marshal.GetLastWin32Error()); // 不注销原占用方的注册。
            hotkey.Register(source.Handle, modifiers, key);
            Assert.True(hotkey.UsesKeyboardHook);
        }
        finally { UnregisterHotKey(source.Handle, 23000); }
    }

    private static void VerifyHotkeyRebind()
    {
        using var first = new HwndSource(new HwndSourceParameters("Hotkey old owner") { WindowStyle = 0 });
        using var second = new HwndSource(new HwndSourceParameters("Hotkey new owner") { WindowStyle = 0 });
        using var hotkey = new GlobalHotkey(GlobalHotkey.LibraryWindowId);
        hotkey.Register(first.Handle, 7, 0x86);
        Assert.True(hotkey.IsRegistered); Assert.False(hotkey.UsesKeyboardHook);
        hotkey.Register(second.Handle, 7, 0x85);
        Assert.True(hotkey.IsRegistered); Assert.False(hotkey.UsesKeyboardHook);
        var released = RegisterHotKey(second.Handle, 23002, 7, 0x86);
        try { Assert.True(released, "Rebinding must release the registration on the old HWND."); }
        finally { if (released) UnregisterHotKey(second.Handle, 23002); }
        var presses = 0;
        hotkey.HotkeyPressed += (_, _) => presses++;
        SendMessage(first.Handle, 0x0312, (IntPtr)GlobalHotkey.LibraryWindowId, IntPtr.Zero);
        Assert.Equal(0, presses);
        SendMessage(second.Handle, 0x0312, (IntPtr)GlobalHotkey.LibraryWindowId, IntPtr.Zero);
        Assert.Equal(1, presses);
    }

    private static void VerifyOccupiedShortcutRecording()
    {
        var settings = SettingsService.Instance.Current;
        var previous = (settings.LibraryHotkeyModifiers, settings.LibraryHotkeyVirtualKey, settings.LibraryHotkeyDisplay);
        var saved = 0;
        var states = new List<bool>();
        var page = new OverTranslate.Views.Settings.SettingsPage(states.Add, () => { saved++; return true; });
        using var source = new HwndSource(new HwndSourceParameters("Occupied shortcut recording")
            { Width = 800, Height = 600, WindowStyle = 0 }) { RootVisual = page };
        var rows = (System.Windows.Controls.StackPanel)page.FindName("HotkeyRows");
        var box = rows.Children.Cast<System.Windows.Controls.Grid>().SelectMany(row => row.Children.OfType<System.Windows.Controls.TextBox>())
            .Single(text => Equals(text.Tag, HotkeyAction.LibraryWindow));
        Assert.True(RegisterHotKey(source.Handle, 23003, 7, 0x87));
        try
        {
            box.RaiseEvent(new System.Windows.Input.MouseButtonEventArgs(System.Windows.Input.Mouse.PrimaryDevice, 0,
                System.Windows.Input.MouseButton.Left) { RoutedEvent = UIElement.PreviewMouseLeftButtonDownEvent });
            var recorder = (ShortcutRecorder?)typeof(OverTranslate.Views.Settings.SettingsPage).GetField("_recorder",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(page);
            Assert.NotNull(recorder);
            Assert.True(recorder.ProcessKey(0x0104, 0x87, 7));
            Assert.Equal(0, saved);
            Assert.True(recorder.ProcessKey(0x0105, 0x87, 0));
            PumpUntil(() => saved == 1);
            Assert.Equal(7u, settings.LibraryHotkeyModifiers);
            Assert.Equal(0x87u, settings.LibraryHotkeyVirtualKey);
            Assert.Equal("Ctrl+Alt+Shift+F24", settings.LibraryHotkeyDisplay);
            Assert.Equal(new[] { true, false }, states);
            Assert.False(recorder.ProcessKey(0x0104, 0x87, 7));
        }
        finally
        {
            page.RaiseEvent(new RoutedEventArgs(FrameworkElement.UnloadedEvent));
            UnregisterHotKey(source.Handle, 23003);
            (settings.LibraryHotkeyModifiers, settings.LibraryHotkeyVirtualKey, settings.LibraryHotkeyDisplay) = previous;
        }
    }

    private static void VerifyClipboardBackgroundWake()
    {
        var folder = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "hotkey-wake-" + Guid.NewGuid().ToString("N"));
        var window = new ClipboardHistoryWindow(new ClipboardHistoryStore(folder), _ => Task.CompletedTask,
            settings: new(), saveSettings: () => true, syncActivities: new SyncActivityStore(System.IO.Path.Combine(folder, "activity.json")))
        { Left = -10000, Top = -10000 };
        var other = new Window { Width = 160, Height = 80, Left = -10000, Top = -10000, ShowInTaskbar = false };
        try
        {
            window.SetWindowPinned(true);
            window.ShowPanel(); PumpUntil(() => window.IsActive);
            other.Show(); other.Activate(); PumpUntil(() => !window.IsActive);
            Assert.True(window.IsVisible);
            window.ToggleVisibility();
            Assert.True(window.IsVisible, "A background clipboard window must be raised, not hidden, by its shortcut.");
            PumpUntil(() => window.IsActive);
            window.ToggleVisibility(); Assert.False(window.IsVisible);
            window.ToggleVisibility(); PumpUntil(() => window.IsActive);
            window.WindowState = WindowState.Minimized;
            window.ToggleVisibility();
            Assert.True(window.IsVisible);
            Assert.Equal(WindowState.Normal, window.WindowState);
        }
        finally
        {
            other.Close(); window.Close();
            if (System.IO.Directory.Exists(folder)) System.IO.Directory.Delete(folder, true);
        }
    }
}
