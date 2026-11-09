using System.Drawing;
using System.Runtime.InteropServices;
using OverTranslate.Models;
using OverTranslate.Services.Realtime;
using OverTranslate.Views.Realtime;

namespace OverTranslate.Services;

internal static class WindowRegionGeometry
{
    [StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct NativePoint { public int X, Y; }
    [DllImport("user32.dll")] private static extern bool GetClientRect(IntPtr hwnd, out NativeRect rect);
    [DllImport("user32.dll")] private static extern bool ClientToScreen(IntPtr hwnd, ref NativePoint point);

    internal static Rectangle ClientBounds(IntPtr hwnd)
    {
        if (!GetClientRect(hwnd, out var rect)) return Rectangle.Empty;
        var origin = new NativePoint();
        if (!ClientToScreen(hwnd, ref origin)) return Rectangle.Empty;
        return new Rectangle(origin.X, origin.Y, rect.Right, rect.Bottom);
    }

    internal static List<ProfileRegion> Normalize(Rectangle client, IReadOnlyList<RealtimeBlockPlacement> blocks) =>
        client.Width <= 0 || client.Height <= 0 ? [] : blocks.Select(block =>
        {
            var rect = Rectangle.Intersect(client, block.Bounds);
            return new ProfileRegion
            {
                X = (double)(rect.X - client.X) / client.Width, Y = (double)(rect.Y - client.Y) / client.Height,
                Width = (double)rect.Width / client.Width, Height = (double)rect.Height / client.Height,
                Mode = block.Mode, Orientation = block.Orientation,
            };
        }).Where(region => region.Width > 0 && region.Height > 0).Take(RealtimeSettings.MaxBlockCount).ToList();

    internal static List<RealtimeBlockPlacement> Restore(Rectangle client, IEnumerable<ProfileRegion> regions)
    {
        var result = new List<RealtimeBlockPlacement>();
        if (client.Width <= 0 || client.Height <= 0) return result;
        foreach (var region in regions.Take(RealtimeSettings.MaxBlockCount))
        {
            if (!double.IsFinite(region.X) || !double.IsFinite(region.Y) || !double.IsFinite(region.Width) || !double.IsFinite(region.Height)
                || region.X < 0 || region.Y < 0 || region.Width <= 0 || region.Height <= 0 || region.X + region.Width > 1.001 || region.Y + region.Height > 1.001) continue;
            var bounds = Rectangle.Intersect(client, new Rectangle(client.X + (int)Math.Round(region.X * client.Width),
                client.Y + (int)Math.Round(region.Y * client.Height), (int)Math.Round(region.Width * client.Width), (int)Math.Round(region.Height * client.Height)));
            if (bounds.Width >= 4 && bounds.Height >= 4) result.Add(new RealtimeBlockPlacement(bounds, region.Mode, region.Orientation));
        }
        return result;
    }

}
