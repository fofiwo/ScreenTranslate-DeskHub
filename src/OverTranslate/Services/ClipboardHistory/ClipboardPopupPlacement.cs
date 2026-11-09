using System.Drawing;

namespace OverTranslate.Services.ClipboardHistory;

internal static class ClipboardPopupPlacement
{
    public static Rectangle Place(Rectangle workArea, double scale, double width, double height, ClipboardWindowPlacement? saved)
    {
        if (workArea.Width <= 0 || workArea.Height <= 0 || !double.IsFinite(scale) || scale <= 0) throw new ArgumentOutOfRangeException(nameof(workArea));
        if (!double.IsFinite(width) || !double.IsFinite(height)) { width = 560; height = 700; saved = null; }
        var pixelWidth = Math.Clamp((int)Math.Ceiling(Math.Clamp(width, 0, workArea.Width / scale) * scale), Math.Min(workArea.Width, (int)Math.Ceiling(460 * scale)), workArea.Width);
        var pixelHeight = Math.Clamp((int)Math.Ceiling(Math.Clamp(height, 0, workArea.Height / scale) * scale), Math.Min(workArea.Height, (int)Math.Ceiling(400 * scale)), workArea.Height);
        var margin = (int)Math.Round(12 * scale);
        var x = saved?.X ?? workArea.Right - pixelWidth - margin;
        var y = saved?.Y ?? workArea.Bottom - pixelHeight - margin;
        return new(Math.Clamp(x, workArea.Left, workArea.Right - pixelWidth), Math.Clamp(y, workArea.Top, workArea.Bottom - pixelHeight), pixelWidth, pixelHeight);
    }
}
