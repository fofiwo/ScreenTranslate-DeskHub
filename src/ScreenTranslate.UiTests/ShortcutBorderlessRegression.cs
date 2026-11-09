using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Shell;
using OverTranslate.Services.Library;
using OverTranslate.Views.Shell;
using Xunit;

namespace OverTranslate.Tests;

public sealed partial class ScreenTranslateShellTests
{
    private static void VerifyBorderlessShortcutShell()
    {
        var sessionState = new[] { "_instance", "_lastSize", "_lastWindowState", "_lastRailCollapsed", "_lastPage", "_translationMode" }
            .Select(name => typeof(ShellWindow).GetField(name,
                System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!)
            .Select(field => (Field: field, Value: field.GetValue(null))).ToArray();
        var window = new ShellWindow(() => new LibraryState(), () => { })
        {
            Left = -10000, Top = -10000, ShowActivated = false,
            WindowStartupLocation = WindowStartupLocation.Manual,
            Width = 1100, Height = 680
        };
        try
        {
            ShellWindow.ShowOrActivateFromShortcut(ShellPage.Home);
            var header = (Grid)window.FindName("WindowTitleBar");
            var controls = (FrameworkElement)window.FindName("QuickWindowControls");
            Assert.Equal(Visibility.Collapsed, header.Visibility);
            Assert.Equal(0, ((Grid)window.FindName("WindowRoot")).RowDefinitions[0].Height.Value);
            Assert.Equal(0, WindowChrome.GetWindowChrome(window).CaptionHeight);
            Assert.True(controls.IsVisible);
            AssertNoNativeCaption(window);
            Assert.Equal(ResizeMode.CanResize, window.ResizeMode);
            var toggle = (System.Windows.Controls.Button)window.FindName("QuickSidebarToggle");
            var label = toggle.ToolTip;
            toggle.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
            Assert.NotEqual(label, toggle.ToolTip);
            toggle.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
            Assert.Equal(label, toggle.ToolTip);
            foreach (var scale in new[] { 1.0, 1.25, 1.5, 2.0 })
            {
                Render(window, $"shell-shortcut-{scale * 100:0}", 1100, 680, scale);
                Assert.True(((FrameworkElement)window.Content).ActualWidth <= 1101);
                AssertInside((FrameworkElement)window.Content, controls);
            }
            // 合成 DPI 仅用于布局；恢复后再操作真实 HWND 的最小化/还原。
            Render(window, "shell-shortcut-light", 1100, 680);
            window.WindowState = WindowState.Minimized;
            ShellWindow.ShowOrActivateFromShortcut(ShellPage.Realtime);
            Assert.Equal(WindowState.Normal, window.WindowState);
            Assert.True(((RadioButton)window.FindName("RealtimeNav")).IsChecked);
            ShellWindow.ShowOrActivate(ShellPage.Settings);
            Assert.Equal(Visibility.Visible, header.Visibility);
            Assert.Equal(40, WindowChrome.GetWindowChrome(window).CaptionHeight);
            Assert.Equal(Visibility.Collapsed, controls.Visibility);
            window.Hide();
            var main = new OverTranslate.Views.MainWindow();
            try
            {
                typeof(OverTranslate.Views.MainWindow).GetMethod("OnTranslationWindowHotkeyPressed",
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                    .Invoke(main, [null, EventArgs.Empty]);
            }
            finally
            {
                // 此实例只验证快捷键路由，关闭时不能触发生产入口的整应用退出流程。
                typeof(OverTranslate.Views.MainWindow).GetField("_exitingApplication",
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.SetValue(main, true);
                main.Close();
            }
            Assert.Equal(Visibility.Collapsed, header.Visibility);
            Assert.True(window.IsVisible);
            ApplyTheme(System.Windows.Application.Current, "Dark");
            typeof(ShellWindow).GetMethod("ReleaseContentAnimations",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.Invoke(window, null);
            Render(window, "shell-shortcut-settings-dark", 1100, 680);
            Assert.True(((FrameworkElement)window.Content).ActualWidth <= 1101);
            AssertInside((FrameworkElement)window.Content, controls);
            ApplyTheme(System.Windows.Application.Current, "Light");
            ((System.Windows.Controls.Button)window.FindName("QuickShellClose"))
                .RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
            Assert.False(window.IsVisible);
        }
        finally
        {
            window.Close();
            foreach (var state in sessionState) state.Field.SetValue(null, state.Value);
            ApplyTheme(System.Windows.Application.Current, "Light");
        }
    }

    private static void AssertNoNativeCaption(Window window)
    {
        var dpi = System.Windows.Media.VisualTreeHelper.GetDpi(window);
        // WindowFrame 为 DWM 动画保留 WS_CAPTION 标志，因此检查实际非客户区尺寸。
        Assert.InRange(NativeTopInset(window), 0, (int)Math.Ceiling(8 * dpi.DpiScaleY));
    }

    private static int NativeTopInset(Window window)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        Assert.NotEqual(IntPtr.Zero, hwnd);
        Assert.True(GetWindowRect(hwnd, out var bounds));
        var clientOrigin = new NativePoint();
        Assert.True(ClientToScreen(hwnd, ref clientOrigin));
        return clientOrigin.Y - bounds.Top;
    }

    private static void VerifyNativeFrameIsNotOverridden(Window window)
    {
        var memory = Marshal.AllocHGlobal(40);
        try
        {
            var sentinel = Enumerable.Range(1, 10).ToArray();
            Marshal.Copy(sentinel, 0, memory, sentinel.Length);
            object?[] arguments = [window, new WindowInteropHelper(window).Handle, 0x0024, memory, false];
            typeof(WindowFrame).GetMethod("Hook", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!
                .Invoke(null, arguments);
            Assert.False((bool)arguments[4]!);
            var actual = new int[10];
            Marshal.Copy(memory, actual, 0, actual.Length);
            Assert.Equal(sentinel, actual);
        }
        finally { Marshal.FreeHGlobal(memory); }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint { public int X, Y; }
    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hwnd, out NativeRect rect);
    [DllImport("user32.dll")]
    private static extern bool ClientToScreen(IntPtr hwnd, ref NativePoint point);
}
