using System.Runtime.InteropServices;
using System.Text;
using System.Windows;

namespace OverTranslate.Services;

internal static class OriginalApplicationPaste
{
    private static readonly WinEventDelegate Callback = OnForeground;
    private static IntPtr _hook;
    private static IntPtr _focusHook;
    private static IntPtr _target;
    private static IntPtr _focus;
    private static uint _process;
    internal readonly record struct Target(IntPtr Window, IntPtr Focus, uint Process);

    public static void Start()
    {
        if (_hook != IntPtr.Zero) return;
        Remember(GetForegroundWindow());
        _hook = SetWinEventHook(3, 3, IntPtr.Zero, Callback, 0, 0, 0);
        _focusHook = SetWinEventHook(0x8005, 0x8005, IntPtr.Zero, Callback, 0, 0, 0);
    }

    private static void OnForeground(IntPtr hook, uint eventType, IntPtr window, int objectId, int childId, uint thread, uint time)
        => Remember(window);

    private static void Remember(IntPtr window)
    {
        if (window == IntPtr.Zero) return;
        window = GetAncestor(window, 2);
        if (window != GetForegroundWindow()) return;
        var thread = GetWindowThreadProcessId(window, out var process);
        if (process == 0 || process == Environment.ProcessId) return;
        var windowClass = new StringBuilder(256);
        if (GetClassName(window, windowClass, windowClass.Capacity) == 0 || !CanTargetWindowClass(windowClass.ToString())) return;
        var info = new GuiThreadInfo { Size = Marshal.SizeOf<GuiThreadInfo>() };
        var focus = GetGUIThreadInfo(thread, ref info) ? info.Focus : IntPtr.Zero;
        if (_target != window) _focus = IntPtr.Zero;
        if (focus != IntPtr.Zero && GetAncestor(focus, 2) == window) _focus = focus;
        _process = process; _target = window;
    }

    internal static Target CaptureTarget()
    {
        Remember(GetForegroundWindow());
        return new(_target, _focus, _process);
    }

    // 打开托盘菜单时任务栏可能先成为前台，不能覆盖此前真正使用的应用。
    internal static bool CanTargetWindowClass(string name) => !string.IsNullOrEmpty(name)
        && name is not ("Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd"
            or "NotifyIconOverflowWindow" or "TopLevelWindowForOverflowXamlIsland");

    public static Task<bool> PasteAsync(Window owner) => PasteAsync(owner, CaptureTarget());

    internal static async Task<bool> PasteAsync(Window owner, Target destination)
    {
        // 每次打开浮窗固定目标，异步复制期间的前台变化不能把内容发给另一个应用。
        var target = destination.Window; var process = destination.Process;
        if (target == IntPtr.Zero || !IsWindow(target)) return false;
        GetWindowThreadProcessId(target, out var currentProcess);
        if (process != currentProcess) return false;
        owner.Hide();
        if (IsIconic(target)) ShowWindow(target, 9);
        SetForegroundWindow(target);
        await Task.Delay(120);
        if (GetForegroundWindow() == target && IsWindow(target))
        {
            GetWindowThreadProcessId(target, out currentProcess);
            if (currentProcess == process && RestoreFocus(destination)
                && GetForegroundWindow() == target && await KeyboardInput.SendPasteCheckedAsync()) return true;
        }
        owner.Show(); owner.Activate();
        return false;
    }

    private static bool RestoreFocus(Target target)
    {
        if (target.Focus == IntPtr.Zero) return true;
        if (!IsWindow(target.Focus) || GetAncestor(target.Focus, 2) != target.Window) return false;
        var thread = GetWindowThreadProcessId(target.Focus, out var process);
        if (process != target.Process) return false;
        var current = GetCurrentThreadId();
        var attached = thread != current && AttachThreadInput(current, thread, true);
        try
        {
            SetFocus(target.Focus);
            var info = new GuiThreadInfo { Size = Marshal.SizeOf<GuiThreadInfo>() };
            return GetGUIThreadInfo(thread, ref info) && info.Focus == target.Focus;
        }
        finally { if (attached) AttachThreadInput(current, thread, false); }
    }

    public static void Stop()
    {
        if (_hook != IntPtr.Zero) UnhookWinEvent(_hook);
        if (_focusHook != IntPtr.Zero) UnhookWinEvent(_focusHook);
        _hook = _focusHook = _target = _focus = IntPtr.Zero; _process = 0;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct GuiThreadInfo
    {
        public int Size;
        public uint Flags;
        public IntPtr Active, Focus, Capture, MenuOwner, MoveSize, Caret;
        public int Left, Top, Right, Bottom;
    }

    private delegate void WinEventDelegate(IntPtr hook, uint eventType, IntPtr window, int objectId, int childId, uint thread, uint time);
    [DllImport("user32.dll")] private static extern IntPtr SetWinEventHook(uint min, uint max, IntPtr module, WinEventDelegate callback, uint process, uint thread, uint flags);
    [DllImport("user32.dll")] private static extern bool UnhookWinEvent(IntPtr hook);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr window, StringBuilder name, int length);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint process);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr window);
    [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr window);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr window);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr window, int command);
    [DllImport("user32.dll")] private static extern IntPtr GetAncestor(IntPtr window, uint flags);
    [DllImport("user32.dll")] private static extern bool GetGUIThreadInfo(uint thread, ref GuiThreadInfo info);
    [DllImport("user32.dll")] private static extern bool AttachThreadInput(uint first, uint second, bool attach);
    [DllImport("user32.dll")] private static extern IntPtr SetFocus(IntPtr window);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
}
