using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using OverTranslate.Models;
using Xunit;

namespace OverTranslate.Tests;

public class SnipDocumentTests
{
    [Fact]
    public void UndoRedoAndNewDrawingHaveIndependentHistory()
    {
        var document = new SnipDocument();
        document.Add(Mark(SnipTool.Rectangle));
        document.Add(Mark(SnipTool.Arrow));
        Assert.True(document.Undo());
        Assert.Single(document.Marks);
        Assert.True(document.Redo());
        Assert.Equal(2, document.Marks.Count);
        document.Undo();
        document.Add(Mark(SnipTool.Pen));
        Assert.False(document.Redo());
        Assert.Equal(SnipTool.Pen, document.Marks.Last().Tool);
    }

    [Fact]
    public void DiscardedClickRestoresRedoOrderWithoutRemovingUnrelatedMarks()
    {
        var document = new SnipDocument();
        document.Add(Mark(SnipTool.Rectangle));
        document.Add(Mark(SnipTool.Arrow));
        document.Add(Mark(SnipTool.Text));
        document.Undo(); document.Undo();
        var redo = document.CaptureRedo();
        document.Add(Mark(SnipTool.Pen));
        var click = document.Marks[^1];
        Assert.False(document.DiscardLast(document.Marks[0], redo));
        Assert.Equal(2, document.Marks.Count);
        Assert.True(document.DiscardLast(click, redo));
        Assert.Equal(SnipTool.Rectangle, Assert.Single(document.Marks).Tool);
        Assert.True(document.Redo()); Assert.Equal(SnipTool.Arrow, document.Marks[^1].Tool);
        Assert.True(document.Redo()); Assert.Equal(SnipTool.Text, document.Marks[^1].Tool);
        Assert.False(document.Redo());
    }

    [Fact]
    public void ExportPreservesPhysicalPixelsAndDrawsOnlyCommittedMarks() => OnSta(() =>
    {
        var source = Pixels(32, 24, (x, y) => Colors.White);
        var document = new SnipDocument();
        document.Add(new SnipMark(SnipTool.Pen, Colors.Red, 4, [new(4, 12), new(28, 12)]));
        var result = document.Render(source);
        Assert.Equal(32, result.PixelWidth);
        Assert.Equal(24, result.PixelHeight);
        Assert.True(result.IsFrozen);
        Assert.Equal(Colors.Red, SnipDocument.Sample(result, 16, 12));
        Assert.Equal(Colors.White, SnipDocument.Sample(result, 0, 0));
        document.Undo();
        Assert.Equal(Colors.White, SnipDocument.Sample(document.Render(source), 16, 12));
    });

    [Fact]
    public void MosaicIsFlattenedOpaqueAndClippedToItsRectangle() => OnSta(() =>
    {
        var source = Pixels(30, 20, (x, y) => x % 2 == 0 ? Colors.Black : Colors.White);
        var document = new SnipDocument();
        document.Add(new SnipMark(SnipTool.Mosaic, Colors.Red, 4, [new(2, 2), new(22, 18)]));
        var result = document.Render(source);
        Assert.Equal(SnipDocument.Sample(result, 4, 5), SnipDocument.Sample(result, 5, 5));
        Assert.Equal((byte)255, SnipDocument.Sample(result, 4, 5).A);
        Assert.Equal(Colors.Black, SnipDocument.Sample(result, 0, 0));
        Assert.Equal(Colors.White, SnipDocument.Sample(result, 29, 19));
        document.Undo();
        Assert.Equal(Colors.Black, SnipDocument.Sample(document.Render(source), 4, 5));
    });

    [Fact]
    public void SamplingUsesSourcePixelsAndClampsEdges()
    {
        var source = Pixels(3, 2, (x, y) => Color.FromRgb((byte)(x * 40), (byte)(y * 80), 123));
        Assert.Equal("#50507B", SnipDocument.Hex(SnipDocument.Sample(source, 100, 100)));
        Assert.Equal("#00007B", SnipDocument.Hex(SnipDocument.Sample(source, -10, -10)));
    }

    internal static BitmapSource Pixels(int width, int height, Func<int, int, Color> pixel)
    {
        var bytes = new byte[width * height * 4];
        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
        {
            var c = pixel(x, y); var i = (y * width + x) * 4;
            bytes[i] = c.B; bytes[i + 1] = c.G; bytes[i + 2] = c.R; bytes[i + 3] = c.A;
        }
        var source = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, bytes, width * 4);
        source.Freeze(); return source;
    }

    private static SnipMark Mark(SnipTool tool) => new(tool, Colors.Red, 3, [new(1, 1), new(12, 12)]);
    private static void OnSta(Action action)
    {
        Exception? error = null;
        var thread = new Thread(() => { try { action(); } catch (Exception ex) { error = ex; } });
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); thread.Join();
        if (error is not null) ExceptionDispatchInfo.Capture(error).Throw();
    }
}
