using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using NLog;

namespace OverTranslate.Services;

public class GlobalHotkey : IDisposable
{
    private static readonly Logger Log = LogManager.GetCurrentClassLogger();
    private const int WM_HOTKEY = 0x0312;

    /// <summary>
    /// Identifies this registration to Windows, which keys them per window handle.
    /// </summary>
    /// <remarks>
    /// Every instance needs its own: the application registers more than one combination against
    /// the same hidden window, and a shared id would have the second registration refused and the
    /// first one's messages answered by both hooks. The number itself means nothing beyond being
    /// distinct — see the constants below for the ones in use.
    /// </remarks>
    private readonly int _id;

    /// <summary>The screenshot capture shortcut, the application's original and main one.</summary>
    public const int CaptureId = 9001;
    public const int OcrExtractId = 9007;
    public const int LibraryWindowId = 9008;
    public const int ClipboardHistoryId = 9009;
    public const int ScreenshotId = 9010;
    public const int PinImageId = 9011;

    /// <summary>The shortcut that opens the main window.</summary>
    public const int TranslationWindowId = 9002;

    /// <summary>The 即時翻譯 shortcut: starts framing, then translating, then pauses and resumes (#324).</summary>
    public const int RealtimePauseId = 9004;

    public GlobalHotkey(int id) => _id = id;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll")]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    public event EventHandler? HotkeyPressed;

    private IntPtr _hwnd;
    private HwndSource? _source;
    private bool _registered;
    private GlobalHotkeyFallback? _fallback;
    private int _generation;
    public bool UsesKeyboardHook => _fallback?.IsInstalled == true;
    public bool IsRegistered => _registered || UsesKeyboardHook;

    public void Register(IntPtr hwnd, uint modifiers, uint virtualKey)
    {
        Unregister();
        _hwnd = hwnd;

        _source = HwndSource.FromHwnd(hwnd);
        _source?.AddHook(WndProc);

        // MOD_NOREPEAT added here rather than stored: it is how the shortcut is registered, not part
        // of what the user chose, and settings.json should go on saying only what they picked.
        // Without it Windows repeats WM_HOTKEY at the keyboard's repeat rate while the key is held,
        // and every one of these shortcuts is a toggle or a session start — held for half a second,
        // a single key would switch the one-shot view back and forth a dozen times.
        _registered = RegisterHotKey(hwnd, _id, modifiers | MOD_NOREPEAT, virtualKey);
        if (_registered) return;

        var error = Marshal.GetLastWin32Error();
        if (error == 1409 && _source is { } source)
        {
            var generation = _generation;
            _fallback = new GlobalHotkeyFallback(modifiers, virtualKey, () =>
            {
                // 钩子线程只投递通知；录入、改键或退出后丢弃旧通知。
                if (source.Dispatcher.HasShutdownStarted) return;
                source.Dispatcher.BeginInvoke(() =>
                {
                    if (generation == _generation && UsesKeyboardHook)
                        HotkeyPressed?.Invoke(this, EventArgs.Empty);
                });
            });
            if (_fallback.Install())
            {
                Log.Info("Hotkey {Id} uses keyboard fallback after registration conflict", _id);
                return;
            }
            error = _fallback.InstallError;
            _fallback.Dispose();
            _fallback = null;
        }
        Log.Warn("Hotkey {Id} could not be enabled (win32 error {Error})", _id, error);
    }

    public void Unregister()
    {
        _generation++;
        _fallback?.Dispose();
        _fallback = null;
        if (_registered)
        {
            UnregisterHotKey(_hwnd, _id);
            _registered = false;
        }
        _source?.RemoveHook(WndProc);
        _source = null;
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_HOTKEY && wParam.ToInt32() == _id)
        {
            HotkeyPressed?.Invoke(this, EventArgs.Empty);
            handled = true;
        }
        return IntPtr.Zero;
    }

    public void Dispose() => Unregister();

    // Modifier constants for display/recording
    public const uint MOD_ALT = 0x0001;
    public const uint MOD_CONTROL = 0x0002;
    public const uint MOD_SHIFT = 0x0004;

    // Registration only, never recorded or displayed: see Register.
    private const uint MOD_NOREPEAT = 0x4000;

    public static string ModifiersToString(uint modifiers)
    {
        var parts = new List<string>();
        if ((modifiers & MOD_CONTROL) != 0) parts.Add("Ctrl");
        if ((modifiers & MOD_ALT) != 0) parts.Add("Alt");
        if ((modifiers & MOD_SHIFT) != 0) parts.Add("Shift");
        return string.Join("+", parts);
    }
}
