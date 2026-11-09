using System.Runtime.InteropServices;
using System.Text;
using System.Windows;

namespace OverTranslate.Services;

internal static class OriginalApplicationPaste
{
    private static readonly WinEventDelegate Callback = OnForeground;
    private static IntPtr _hook;
    private static IntPtr _target;
    private static uint _process;

    public static void Start()
    {
        if (_hook != IntPtr.Zero) return;
        Remember(GetForegroundWindow());
        _hook = SetWinEventHook(3, 3, IntPtr.Zero, Callback, 0, 0, 0);
    }

    private static void OnForeground(IntPtr hook, uint eventType, IntPtr window, int objectId, int childId, uint thread, uint time)
        => Remember(window);

    private static void Remember(IntPtr window)
    {
        if (window == IntPtr.Zero) return;
        GetWindowThreadProcessId(window, out var process);
        if (process == 0 || process == Environment.ProcessId) return;
        var windowClass = new StringBuilder(256);
        if (GetClassName(window, windowClass, windowClass.Capacity) == 0 || !CanTargetWindowClass(windowClass.ToString())) return;
        _process = process; _target = window;
    }

    // 打开托盘菜单时任务栏可能先成为前台，不能覆盖此前真正使用的应用。
    internal static bool CanTargetWindowClass(string name) => !string.IsNullOrEmpty(name)
        && name is not ("Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd"
            or "NotifyIconOverflowWindow" or "TopLevelWindowForOverflowXamlIsland");

    public static async Task<bool> PasteAsync(Window owner)
    {
        var target = _target; var process = _process;
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
            if (currentProcess == process && await KeyboardInput.SendPasteCheckedAsync()) return true;
        }
        owner.Show(); owner.Activate();
        return false;
    }

    public static void Stop()
    {
        if (_hook != IntPtr.Zero) UnhookWinEvent(_hook);
        _hook = _target = IntPtr.Zero; _process = 0;
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
}
