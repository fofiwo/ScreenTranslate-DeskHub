using System.Runtime.InteropServices;

namespace OverTranslate.Services;

// 仅在系统拒绝注册的组合上接管输入，不记录其他按键。
internal sealed class GlobalHotkeyFallback : IDisposable
{
    private readonly uint _modifiers, _key;
    private readonly Action _pressed;
    private readonly HookProc _proc;
    private readonly HookThread _thread = new("ScreenTranslate shortcut fallback");
    private IntPtr _hook;
    private bool _keyDown, _swallowKey;
    private volatile bool _disposed;
    internal bool IsInstalled => _hook != IntPtr.Zero;
    internal int InstallError { get; private set; }

    internal GlobalHotkeyFallback(uint modifiers, uint key, Action pressed)
    {
        _modifiers = modifiers;
        _key = key;
        _pressed = pressed;
        _proc = HookCallback;
    }

    internal bool Install()
    {
        _thread.Start();
        _thread.Invoke(() =>
        {
            _hook = SetWindowsHookEx(13, _proc, GetModuleHandle(null), 0);
            InstallError = _hook == IntPtr.Zero ? Marshal.GetLastWin32Error() : 0;
        });
        return IsInstalled;
    }

    private IntPtr HookCallback(int code, IntPtr message, IntPtr data)
    {
        if (code >= 0 && (uint)Marshal.ReadInt32(data) == _key
            && ProcessKey(message.ToInt32(), _key, CurrentModifiers())) return (IntPtr)1;
        return CallNextHookEx(_hook, code, message, data);
    }

    internal bool ProcessKey(int message, uint key, uint modifiers)
    {
        if (_disposed || key != _key) return false;
        if (message is 0x0101 or 0x0105)
        {
            var swallowed = _swallowKey;
            _keyDown = _swallowKey = false;
            return swallowed;
        }
        if (message is not (0x0100 or 0x0104)) return false;
        if (_keyDown) return _swallowKey;
        _keyDown = true;
        _swallowKey = modifiers == _modifiers;
        if (_swallowKey) _pressed();
        return _swallowKey;
    }

    internal static uint CurrentModifiers()
    {
        uint modifiers = 0;
        if (Down(0x12)) modifiers |= 1;
        if (Down(0x11)) modifiers |= 2;
        if (Down(0x10)) modifiers |= 4;
        if (Down(0x5B) || Down(0x5C)) modifiers |= 8;
        return modifiers;
    }

    private static bool Down(int key) => (GetAsyncKeyState(key) & 0x8000) != 0;

    public void Dispose()
    {
        _disposed = true;
        _thread.Stop(() =>
        {
            if (_hook != IntPtr.Zero) UnhookWindowsHookEx(_hook);
            _hook = IntPtr.Zero;
        });
    }

    private delegate IntPtr HookProc(int code, IntPtr message, IntPtr data);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int kind, HookProc callback, IntPtr module, uint thread);
    [DllImport("user32.dll")]
    private static extern bool UnhookWindowsHookEx(IntPtr hook);
    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr message, IntPtr data);
    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int key);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? name);
}
