using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Color = System.Windows.Media.Color;
using Pen = System.Windows.Media.Pen;
using Point = System.Windows.Point;

namespace OverTranslate.Models;

public enum SnipTool { Select, Rectangle, Arrow, Pen, Text, Mosaic, Picker }

public sealed record SnipMark(SnipTool Tool, Color Color, double Thickness, IReadOnlyList<Point> Points, string? Text = null);

// 坐标和线宽均为截图物理像素；预览与导出使用同一绘制路径。
public sealed class SnipDocument
{
    private readonly List<SnipMark> _marks = [];
    private readonly Stack<SnipMark> _redo = [];
    public IReadOnlyList<SnipMark> Marks => _marks;
    public bool CanUndo => _marks.Count > 0;
    public bool CanRedo => _redo.Count > 0;

    public void Add(SnipMark mark)
    {
        if (mark.Points.Count == 0) return;
        _marks.Add(mark with { Points = mark.Points.ToArray() });
        _redo.Clear();
    }

    public bool Undo()
    {
        if (_marks.Count == 0) return false;
        _redo.Push(_marks[^1]); _marks.RemoveAt(_marks.Count - 1); return true;
    }

    public bool Redo()
    {
        if (!_redo.TryPop(out var mark)) return false;
        _marks.Add(mark); return true;
    }

    public BitmapSource Render(BitmapSource source)
    {
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawImage(source, new Rect(0, 0, source.PixelWidth, source.PixelHeight));
            Draw(dc, source);
        }
        var result = new RenderTargetBitmap(source.PixelWidth, source.PixelHeight, 96, 96, PixelFormats.Pbgra32);
        result.Render(visual); result.Freeze(); return result;
    }

    public void Draw(DrawingContext dc, BitmapSource source, SnipMark? preview = null)
    {
        dc.PushClip(new RectangleGeometry(new Rect(0, 0, source.PixelWidth, source.PixelHeight)));
        foreach (var mark in _marks) DrawMark(dc, source, mark);
        if (preview is not null) DrawMark(dc, source, preview);
        dc.Pop();
    }

    private static void DrawMark(DrawingContext dc, BitmapSource source, SnipMark mark)
    {
        if (mark.Points.Count == 0) return;
        var start = mark.Points[0]; var end = mark.Points[^1];
        var brush = new SolidColorBrush(mark.Color);
        var pen = new Pen(brush, mark.Thickness) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round };
        switch (mark.Tool)
        {
            case SnipTool.Rectangle:
            case SnipTool.Arrow:
                AnnotationStroke.DrawShape(dc, pen, mark.Tool == SnipTool.Arrow ? AnnotationTool.Arrow : AnnotationTool.Rectangle, start, end);
                break;
            case SnipTool.Pen:
                if (mark.Points.Count == 1) dc.DrawEllipse(brush, null, start, mark.Thickness / 2, mark.Thickness / 2);
                else
                {
                    var path = new StreamGeometry();
                    using (var context = path.Open())
                    {
                        context.BeginFigure(start, false, false);
                        context.PolyLineTo(mark.Points.Skip(1).ToArray(), true, false);
                    }
                    dc.DrawGeometry(null, pen, path);
                }
                break;
            case SnipTool.Text when !string.IsNullOrWhiteSpace(mark.Text):
                var text = new FormattedText(mark.Text, CultureInfo.CurrentUICulture, System.Windows.FlowDirection.LeftToRight,
                    new Typeface("Microsoft YaHei UI"), Math.Max(16, mark.Thickness * 6), brush, 1);
                dc.DrawText(text, start);
                break;
            case SnipTool.Mosaic:
                var rect = Rect.Intersect(new Rect(start, end), new Rect(0, 0, source.PixelWidth, source.PixelHeight));
                if (rect.IsEmpty || rect.Width < 1 || rect.Height < 1) break;
                var x = (int)Math.Floor(rect.Left); var y = (int)Math.Floor(rect.Top);
                var width = Math.Min(source.PixelWidth - x, (int)Math.Ceiling(rect.Right) - x);
                var height = Math.Min(source.PixelHeight - y, (int)Math.Ceiling(rect.Bottom) - y);
                var crop = new CroppedBitmap(source, new Int32Rect(x, y, width, height));
                var pixels = Bgra(crop); var bytes = new byte[width * height * 4];
                pixels.CopyPixels(bytes, width * 4, 0);
                var block = Math.Max(8, (int)(mark.Thickness * 4));
                for (var by = 0; by < height; by += block)
                for (var bx = 0; bx < width; bx += block)
                {
                    var bw = Math.Min(block, width - bx); var bh = Math.Min(block, height - by);
                    long r = 0, g = 0, b = 0;
                    for (var py = by; py < by + bh; py++)
                    for (var px = bx; px < bx + bw; px++)
                    { var i = (py * width + px) * 4; b += bytes[i]; g += bytes[i + 1]; r += bytes[i + 2]; }
                    var count = bw * bh;
                    dc.DrawRectangle(new SolidColorBrush(Color.FromRgb((byte)(r / count), (byte)(g / count), (byte)(b / count))),
                        null, new Rect(x + bx, y + by, bw, bh));
                }
                break;
        }
    }

    public static Color Sample(BitmapSource source, int x, int y)
    {
        var pixel = new byte[4];
        Bgra(source).CopyPixels(new Int32Rect(Math.Clamp(x, 0, source.PixelWidth - 1),
            Math.Clamp(y, 0, source.PixelHeight - 1), 1, 1), pixel, 4, 0);
        return Color.FromArgb(pixel[3], pixel[2], pixel[1], pixel[0]);
    }

    public static string Hex(Color color) => $"#{color.R:X2}{color.G:X2}{color.B:X2}";

    private static BitmapSource Bgra(BitmapSource source) => source.Format == PixelFormats.Bgra32
        ? source : new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
}
