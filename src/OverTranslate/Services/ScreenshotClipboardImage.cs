using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace OverTranslate.Services;

internal static class ScreenshotClipboardImage
{
    private const uint BitmapFormat = 2; // CF_BITMAP

    internal static BitmapSource? ReadImage()
    {
        if (!OpenClipboard(IntPtr.Zero)) throw new COMException("Clipboard busy", unchecked((int)0x800401D0));
        try
        {
            var bitmap = GetClipboardData(BitmapFormat);
            if (bitmap == IntPtr.Zero) return null;
            var image = System.Windows.Interop.Imaging.CreateBitmapSourceFromHBitmap(bitmap, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            image.Freeze();
            return image;
        }
        finally { CloseClipboard(); }
    }

    internal static void SetImage(BitmapSource image, IntPtr owner, Action? writeAdditionalFormats = null)
    {
        if (owner == IntPtr.Zero || !IsWindow(owner)) throw new InvalidOperationException("截图窗口已关闭。");
        var stride = checked(image.PixelWidth * 4);
        var size = checked(stride * image.PixelHeight);
        var header = new BitmapInfoHeader { Size = 40, Width = image.PixelWidth, Height = -image.PixelHeight, Planes = 1, BitCount = 32 };
        var pixels = new byte[size];
        var rgb = image.Format == PixelFormats.Bgr32 ? image : new FormatConvertedBitmap(image, PixelFormats.Bgr32, null, 0);
        rgb.CopyPixels(pixels, stride, 0);
        var dc = GetDC(IntPtr.Zero);
        if (dc == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
        IntPtr bitmap;
        // CF_BITMAP 使用独立 DDB；不能发布依赖本进程内存的 DIB section，否则跨进程/退出后读取会失效。
        try { bitmap = CreateDIBitmap(dc, ref header, 4, pixels, ref header, 0); }
        finally { ReleaseDC(IntPtr.Zero, dc); }
        if (bitmap == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
        try
        {
            // 在占用剪贴板前准备不透明 RGB 位图，避免 OLE 延迟渲染、Flush 及其第二次通知。
            if (!OpenClipboard(owner)) throw new COMException("Clipboard busy", unchecked((int)0x800401D0));
            try
            {
                if (!EmptyClipboard()) throw new Win32Exception(Marshal.GetLastWin32Error());
                writeAdditionalFormats?.Invoke();
                if (SetClipboardData(BitmapFormat, bitmap) == IntPtr.Zero)
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                bitmap = IntPtr.Zero; // CF_BITMAP 成功交给系统后，窗口/工作线程退出也不释放它。
            }
            finally { CloseClipboard(); }
        }
        finally { if (bitmap != IntPtr.Zero) DeleteObject(bitmap); }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfoHeader
    {
        public uint Size;
        public int Width, Height;
        public ushort Planes, BitCount;
        public uint Compression, SizeImage;
        public int XPelsPerMeter, YPelsPerMeter;
        public uint ClrUsed, ClrImportant;
    }

    [DllImport("gdi32.dll", SetLastError = true)] private static extern IntPtr CreateDIBitmap(IntPtr dc, ref BitmapInfoHeader header, uint init, byte[] bits, ref BitmapInfoHeader info, uint usage);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr bitmap);
    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr GetDC(IntPtr window);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr window, IntPtr dc);
    [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr window);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool OpenClipboard(IntPtr owner);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool CloseClipboard();
    [DllImport("user32.dll", SetLastError = true)] private static extern bool EmptyClipboard();
    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr SetClipboardData(uint format, IntPtr bitmap);
    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr GetClipboardData(uint format);
}
