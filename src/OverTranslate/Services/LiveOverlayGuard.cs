using System.Runtime.InteropServices;
using System.Windows.Threading;

namespace OverTranslate.Services;

// 仅在单次实时覆盖期间监听：界面已滚动、移动或切换时撤掉旧译文。
internal sealed class LiveOverlayGuard : IDisposable
{
    [StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct Point { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct MouseInfo { public Point Position; public uint Data, Flags, Time; public UIntPtr Extra; }
    private delegate IntPtr MouseProc(int code, IntPtr message, IntPtr data);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hwnd, out NativeRect rect);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);
    [DllImport("user32.dll")] private static extern IntPtr WindowFromPoint(Point point);
    [DllImport("user32.dll")] private static extern IntPtr SetWindowsHookEx(int id, MouseProc callback, IntPtr module, uint threadId);
    [DllImport("user32.dll")] private static extern bool UnhookWindowsHookEx(IntPtr handle);
    [DllImport("user32.dll")] private static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr message, IntPtr data);
    private readonly DispatcherTimer _timer;
    private readonly MouseProc _callback;
    private readonly IntPtr _source;
    private readonly NativeRect _bounds;
    private IntPtr _hook;
    private bool _disposed;
    private bool _closing;
    private readonly Action _close;
    private readonly Dispatcher _dispatcher;
    internal static IntPtr Foreground => GetForegroundWindow();

    internal LiveOverlayGuard(IntPtr source, Action close)
    {
        _source = source; _close = close; _dispatcher = Dispatcher.CurrentDispatcher;
        GetWindowRect(source, out _bounds);
        _callback = OnMouse;
        _hook = SetWindowsHookEx(14, _callback, IntPtr.Zero, 0);
        _timer = new DispatcherTimer(TimeSpan.FromMilliseconds(200), DispatcherPriority.Background, (_, _) => Check(), _dispatcher);
        _timer.Start();
    }

    private bool IsOurs(IntPtr hwnd) { GetWindowThreadProcessId(hwnd, out var pid); return pid == Environment.ProcessId; }
    private void Check()
    {
        if (_source == IntPtr.Zero) return;
        var foreground = GetForegroundWindow();
        if ((foreground != IntPtr.Zero && foreground != _source && !IsOurs(foreground))
            || !GetWindowRect(_source, out var current) || !current.Equals(_bounds)) RequestClose();
    }

    private IntPtr OnMouse(int code, IntPtr message, IntPtr data)
    {
        if (code >= 0 && ((int)message is 0x020A or 0x020E or 0x0201))
        {
            var info = Marshal.PtrToStructure<MouseInfo>(data);
            if ((int)message is 0x020A or 0x020E || !IsOurs(WindowFromPoint(info.Position))) RequestClose();
        }
        return CallNextHookEx(_hook, code, message, data);
    }

    private void RequestClose()
    {
        if (_disposed || _closing) return;
        _closing = true;
        _dispatcher.BeginInvoke(() => { if (!_disposed) _close(); });
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true; _timer.Stop();
        if (_hook != IntPtr.Zero) UnhookWindowsHookEx(_hook);
        _hook = IntPtr.Zero;
    }
}
