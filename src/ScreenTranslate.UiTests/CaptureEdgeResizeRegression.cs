using System.Reflection;
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using OverTranslate.Models;
using OverTranslate.Views.Capture;
using Xunit;

namespace OverTranslate.Tests;

public sealed partial class ScreenTranslateShellTests
{
    private static void VerifyCaptureEdgeResize()
    {
        foreach (var snipping in new[] { true, false })
        {
            var capture = new ScreenCaptureWindow(new System.Drawing.Bitmap(1000, 700),
                new System.Drawing.Rectangle(0, 0, 1000, 700), snippingTools: snipping);
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
            void Set(string name, object value) => typeof(ScreenCaptureWindow).GetField(name, flags)!.SetValue(capture, value);
            object? Get(string name) => typeof(ScreenCaptureWindow).GetField(name, flags)!.GetValue(capture);
            void Invoke(string name, params object[] args) => typeof(ScreenCaptureWindow).GetMethod(name, flags)!.Invoke(capture, args);
            var initial = new Rect(60, 60, 200, 120);
            void Reset()
            {
                Set("_selectionWpfRect", initial); Set("_hasSelection", true);
                ((UIElement)capture.FindName("HintHost")).Visibility = Visibility.Collapsed;
                Invoke("UpdateSelectionMetadata"); Invoke("UpdateSelectionVisuals"); capture.UpdateLayout();
            }
            void Drag(Thumb edge, double x, double y) => edge.RaiseEvent(new DragDeltaEventArgs(x, y) { RoutedEvent = Thumb.DragDeltaEvent });
            Thumb? Hit(Point point)
            {
                for (var current = capture.InputHitTest(point) as DependencyObject; current is not null;
                     current = VisualTreeHelper.GetParent(current))
                    if (current is Thumb thumb) return thumb;
                return null;
            }
            try
            {
                capture.Show(); Reset();
                Rect? adjusted = null;
                capture.SelectionAdjusted += (_, rect) => adjusted = rect;
                foreach (var (name, x, y, expected, cursor) in new[]
                {
                    ("TopEdgeHandle", 31d, -12d, new Rect(60, 48, 200, 132), Cursors.SizeNS),
                    ("BottomEdgeHandle", 31d, 12d, new Rect(60, 60, 200, 132), Cursors.SizeNS),
                    ("LeftEdgeHandle", -12d, 31d, new Rect(48, 60, 212, 120), Cursors.SizeWE),
                    ("RightEdgeHandle", 12d, 31d, new Rect(60, 60, 212, 120), Cursors.SizeWE)
                })
                {
                    Reset();
                    var edge = Assert.IsType<Thumb>(capture.FindName(name));
                    Assert.Equal(Visibility.Visible, edge.Visibility); Assert.Same(cursor, edge.Cursor);
                    foreach (var fraction in new[] { 0.25, 0.5, 0.75 })
                    {
                        var point = edge.TranslatePoint(new Point(edge.ActualWidth * fraction, edge.ActualHeight * fraction), capture);
                        Assert.True(ReferenceEquals(edge, Hit(point)),
                            $"snipping={snipping}, {name} at {fraction}: hit {capture.InputHitTest(point)?.GetType().Name}, point={point}");
                        Assert.False(capture.CopySnipOnDoubleClickAsync(point, capture.InputHitTest(point) as DependencyObject,
                            _ => Assert.Fail("双击框线不能复制截图")).GetAwaiter().GetResult());
                    }
                    Drag(edge, x, y);
                    Assert.Equal(expected, Get("_selectionWpfRect"));
                    Assert.Equal(capture.Selection, adjusted);
                    if (snipping)
                    {
                        var source = Assert.IsAssignableFrom<System.Windows.Media.Imaging.BitmapSource>(Get("_snipSource"));
                        Assert.Equal((int)capture.Selection.Width, source.PixelWidth);
                        Assert.Equal((int)capture.Selection.Height, source.PixelHeight);
                    }
                    Reset();
                    Drag(edge, x * 10000, y * 10000);
                    var bounded = (Rect)Get("_selectionWpfRect")!;
                    Assert.True(bounded.Left >= 0 && bounded.Top >= 0 && bounded.Right <= capture.ActualWidth && bounded.Bottom <= capture.ActualHeight);
                    Reset();
                    Drag(edge, name == "LeftEdgeHandle" ? 199 : name == "RightEdgeHandle" ? -199 : 0,
                        name == "TopEdgeHandle" ? 119 : name == "BottomEdgeHandle" ? -119 : 0);
                    var minimum = (Rect)Get("_selectionWpfRect")!;
                    Assert.Equal(cursor == Cursors.SizeNS ? 200 : 4, minimum.Width);
                    Assert.Equal(cursor == Cursors.SizeNS ? 4 : 120, minimum.Height);
                    Reset();
                    if (snipping)
                    {
                        Invoke("SetSnipTool", SnipTool.Pen); Assert.Equal(Visibility.Collapsed, edge.Visibility);
                        Invoke("SetSnipTool", SnipTool.Select);
                        Invoke("SetSnipWriting", true); Assert.Equal(Visibility.Collapsed, edge.Visibility);
                        Drag(edge, x, y); Assert.Equal(initial, Get("_selectionWpfRect"));
                        Invoke("SetSnipWriting", false);
                    }
                    else
                    {
                        Set("_processingStarted", true); Invoke("UpdateSelectionVisuals");
                        Assert.Equal(Visibility.Collapsed, edge.Visibility);
                        Drag(edge, x, y); Assert.Equal(initial, Get("_selectionWpfRect"));
                        Set("_processingStarted", false);
                    }
                }
                Reset();
                Assert.Same(capture.FindName("TopLeftHandle"), Hit(initial.TopLeft));
                Assert.Same(capture.FindName("SelectionBody"), Hit(new Point(160, 120)));
            }
            finally { capture.Close(); }
        }
    }
}
