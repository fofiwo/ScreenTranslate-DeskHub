using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using OverTranslate.Services.Library;
using OverTranslate.Views.Capture;

namespace OverTranslate.Services.Lan;

internal static class LanClipboardWriter
{
    private const int Busy = unchecked((int)0x800401D0);
    private const int Changed = unchecked((int)0x800401D3);
    private static readonly SemaphoreSlim Writer = new(1, 1);

    internal static async Task ApplyAsync(LanClipboardPayload payload, BitmapSource? image,
        CancellationToken token, Func<bool> canApply)
    {
        var text = payload.Kind == "text" ? new UTF8Encoding(false, true).GetString(payload.Bytes) : null;
        if (text?.Contains('\0') == true || payload.Kind is not ("text" or "image"))
            throw new InvalidDataException("不支持的剪贴板内容。");
        var content = text is null ? payload.Bytes : Encoding.Unicode.GetBytes(text + '\0');
        var origin = Encoding.UTF8.GetBytes(payload.Origin);
        await Writer.WaitAsync(token).ConfigureAwait(false);
        try
        {
            await SnipClipboard.RunStaAsync(async () =>
            {
                using var owner = new HwndSource(new HwndSourceParameters("LAN clipboard writer")
                    { ParentWindow = new IntPtr(-3), WindowStyle = 0 });
                var markerFormat = RegisterClipboardFormat(LanClipboardSync.OriginFormat);
                var contentFormat = text is null ? RegisterClipboardFormat("PNG") : 13u;
                if (markerFormat == 0 || contentFormat == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
                void CheckCurrent()
                {
                    token.ThrowIfCancellationRequested();
                    if (!canApply()) throw new OperationCanceledException("自动同步已暂停或关闭。", token);
                }
                void Metadata()
                {
                    SetBytes(markerFormat, origin);
                    if (text is null) SetBytes(contentFormat, content);
                }
                await WriteVerifiedAsync(() =>
                {
                    CheckCurrent();
                    // 来源与正文在同一次 Open/Close 中提交，监听者不会先收到没有防回环标记的正文。
                    if (text is null) ScreenshotClipboardImage.SetImage(image!, owner.Handle, Metadata);
                    else LibraryClipboard.SetText(text, owner.Handle, Metadata);
                }, () =>
                {
                    CheckCurrent();
                    var sequence = GetClipboardSequenceNumber();
                    if (!OpenClipboard(IntPtr.Zero)) throw new COMException("Clipboard busy", Busy);
                    try
                    {
                        if (!Matches(markerFormat, origin) || !Matches(contentFormat, content))
                            throw new COMException("剪贴板内容已被替换或无法读回。", Changed);
                    }
                    finally { CloseClipboard(); }
                    if (image is not null && !SnipClipboard.SameImage(image, ScreenshotClipboardImage.ReadImage())
                        || sequence != GetClipboardSequenceNumber())
                        throw new COMException("剪贴板图片已被替换或无法读回。", Changed);
                }, token);
            }).ConfigureAwait(false);
        }
        finally { Writer.Release(); }
    }

    internal static async Task WriteVerifiedAsync(Action write, Action verify, CancellationToken token)
    {
        async Task RetryBusy(Action action)
        {
            for (var attempt = 0; ; attempt++)
            {
                token.ThrowIfCancellationRequested();
                try { action(); return; }
                catch (ExternalException ex) when (ex.ErrorCode == Busy && attempt < 5)
                { await Task.Delay(60, token); }
            }
        }
        await RetryBusy(write);
        // 读回被占用只重读；不能重发旧内容覆盖用户的新复制或制造监听风暴。
        await RetryBusy(verify);
    }

    private static void SetBytes(uint format, byte[] bytes)
    {
        var memory = GlobalAlloc(0x42, (nuint)Math.Max(1, bytes.Length));
        if (memory == IntPtr.Zero) throw new OutOfMemoryException();
        try
        {
            var pointer = GlobalLock(memory);
            if (pointer == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
            try { Marshal.Copy(bytes, 0, pointer, bytes.Length); }
            finally { GlobalUnlock(memory); }
            if (SetClipboardData(format, memory) == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
            memory = IntPtr.Zero;
        }
        finally { if (memory != IntPtr.Zero) GlobalFree(memory); }
    }

    private static bool Matches(uint format, byte[] expected)
    {
        var memory = GetClipboardData(format);
        if (memory == IntPtr.Zero || GlobalSize(memory) < (nuint)expected.Length) return false;
        var pointer = GlobalLock(memory);
        if (pointer == IntPtr.Zero) return false;
        try
        {
            var actual = new byte[expected.Length];
            Marshal.Copy(pointer, actual, 0, actual.Length);
            return actual.AsSpan().SequenceEqual(expected);
        }
        finally { GlobalUnlock(memory); }
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern uint RegisterClipboardFormat(string name);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool OpenClipboard(IntPtr owner);
    [DllImport("user32.dll")] private static extern bool CloseClipboard();
    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr SetClipboardData(uint format, IntPtr memory);
    [DllImport("user32.dll")] private static extern IntPtr GetClipboardData(uint format);
    [DllImport("user32.dll")] private static extern uint GetClipboardSequenceNumber();
    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr GlobalAlloc(uint flags, nuint bytes);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr GlobalLock(IntPtr memory);
    [DllImport("kernel32.dll")] private static extern bool GlobalUnlock(IntPtr memory);
    [DllImport("kernel32.dll")] private static extern IntPtr GlobalFree(IntPtr memory);
    [DllImport("kernel32.dll")] private static extern nuint GlobalSize(IntPtr memory);
}
