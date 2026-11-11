using System.Runtime.InteropServices;

namespace OverTranslate.Services;

// 仅录入期间、且设置窗口在前台时接收组合键，避免全局热键先吃掉窗口按键。
internal sealed class ShortcutRecorder : IDisposable
{
    private readonly IntPtr _owner;
    private readonly Action<uint, uint> _recorded;
    private readonly HookProc _proc;
    private readonly HookThread _thread = new("ScreenTranslate shortcut recorder");
    private IntPtr _hook;
    private uint? _pendingKey;
    private uint _pendingModifiers;
    private volatile bool _disposed;

    internal ShortcutRecorder(IntPtr owner, Action<uint, uint> recorded)
    {
        _owner = owner;
        _recorded = recorded;
        _proc = HookCallback;
    }

    internal bool Install()
    {
        _thread.Start();
        _thread.Invoke(() => _hook = SetWindowsHookEx(13, _proc, GetModuleHandle(null), 0));
        return _hook != IntPtr.Zero;
    }

    private IntPtr HookCallback(int code, IntPtr message, IntPtr data)
    {
        if (code >= 0 && GetForegroundWindow() == _owner
            && ProcessKey(message.ToInt32(), (uint)Marshal.ReadInt32(data), GlobalHotkeyFallback.CurrentModifiers())) return (IntPtr)1;
        return CallNextHookEx(_hook, code, message, data);
    }

    internal bool ProcessKey(int message, uint key, uint modifiers)
    {
        if (_disposed || key is 0x09 or 0x10 or 0x11 or 0x12 or 0x5B or 0x5C or >= 0xA0 and <= 0xA5) return false;
        if (message is 0x0100 or 0x0104)
        {
            if (_pendingKey is null) { _pendingKey = key; _pendingModifiers = modifiers; }
            return _pendingKey == key;
        }
        if (message is not (0x0101 or 0x0105) || _pendingKey != key) return false;
        _pendingKey = null;
        // 抬键后再保存并恢复热键，防止同一次长按立刻唤起刚绑定的窗口。
        _recorded(key, _pendingModifiers);
        return true;
    }

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
    [DllImport("user32.dll")]
    private static extern IntPtr SetWindowsHookEx(int kind, HookProc callback, IntPtr module, uint thread);
    [DllImport("user32.dll")]
    private static extern bool UnhookWindowsHookEx(IntPtr hook);
    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr message, IntPtr data);
    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? name);
}
