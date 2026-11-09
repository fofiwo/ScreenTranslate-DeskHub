using System.ComponentModel;
using System.Runtime.InteropServices;

namespace OverTranslate.Services.Library;

internal static class LibraryClipboard
{
    // 直接提交 Unicode 文本，避免 OLE 延迟渲染及 Flush 的同步等待。
    public static void SetText(string text, IntPtr owner)
    {
        var bytes = System.Text.Encoding.Unicode.GetBytes(text + '\0');
        var memory = GlobalAlloc(0x42, (nuint)bytes.Length);
        if (memory == IntPtr.Zero) throw new OutOfMemoryException();
        try
        {
            var pointer = GlobalLock(memory);
            if (pointer == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
            try { Marshal.Copy(bytes, 0, pointer, bytes.Length); }
            finally { GlobalUnlock(memory); }
            if (!OpenClipboard(owner)) throw new COMException("Clipboard busy", unchecked((int)0x800401D0));
            try
            {
                if (!EmptyClipboard() || SetClipboardData(13, memory) == IntPtr.Zero)
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                memory = IntPtr.Zero; // 成功后由系统持有内存，不能再次释放。
            }
            finally { CloseClipboard(); }
        }
        finally { if (memory != IntPtr.Zero) GlobalFree(memory); }
    }

    public static async Task WriteAsync(string text, Action<string> write, CancellationToken token)
    {
        for (var attempt = 0; ; attempt++)
        {
            token.ThrowIfCancellationRequested();
            try { write(text); return; }
            catch (ExternalException ex) when (ex.ErrorCode == unchecked((int)0x800401D0) && attempt < 7)
            { await Task.Delay(Math.Min(100, 25 * (attempt + 1)), token); }
        }
    }

    [DllImport("user32.dll", SetLastError = true)] private static extern bool OpenClipboard(IntPtr owner);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool CloseClipboard();
    [DllImport("user32.dll", SetLastError = true)] private static extern bool EmptyClipboard();
    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr SetClipboardData(uint format, IntPtr memory);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr GlobalAlloc(uint flags, nuint bytes);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr GlobalLock(IntPtr memory);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GlobalUnlock(IntPtr memory);
    [DllImport("kernel32.dll")] private static extern IntPtr GlobalFree(IntPtr memory);
}
