using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using OverTranslate.Models;
using OverTranslate.Views.Capture;
using Xunit;

namespace OverTranslate.Tests;

public sealed partial class ScreenTranslateShellTests
{
    private static void VerifySnippingTools()
    {
        VerifySnipAdversarialInput();
        using var pixels = new System.Drawing.Bitmap(1000, 700);
        using (var graphics = System.Drawing.Graphics.FromImage(pixels))
        {
            graphics.Clear(System.Drawing.Color.FromArgb(238, 245, 252));
            graphics.FillRectangle(System.Drawing.Brushes.White, 100, 100, 600, 300);
            graphics.DrawString("Screenshot annotation fixture", new System.Drawing.Font("Segoe UI", 22), System.Drawing.Brushes.DarkSlateBlue, 130, 150);
        }
        var capture = new ScreenCaptureWindow((System.Drawing.Bitmap)pixels.Clone(), new System.Drawing.Rectangle(0, 0, 1000, 700), snippingTools: true);
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        void Set(string name, object value) => typeof(ScreenCaptureWindow).GetField(name, flags)!.SetValue(capture, value);
        object? Get(string name) => typeof(ScreenCaptureWindow).GetField(name, flags)!.GetValue(capture);
        void Invoke(string name, params object[] args) => typeof(ScreenCaptureWindow).GetMethod(name, flags)!.Invoke(capture, args);
        BitmapSource? copied = null;
        double scaleX = 1, scaleY = 1;
        try
        {
            capture.Show();
            Assert.NotNull(((Image)capture.FindName("ScreenshotImage")).Source);
            scaleX = (double)Get("_dpiX")!; scaleY = (double)Get("_dpiY")!;
            Render(capture, "snip-ready", (int)(1000 / scaleX), (int)(700 / scaleY), scaleX);
            Assert.Equal(Visibility.Visible, ((FrameworkElement)Get("_snipWelcome")!).Visibility);
            Assert.Equal(Visibility.Collapsed, ((FrameworkElement)Get("_snipToolbar")!).Visibility);
            Set("_selectionWpfRect", new Rect(100 / scaleX, 100 / scaleY, 600 / scaleX, 300 / scaleY)); Set("_hasSelection", true);
            Render(capture, "snip-before-annotations", (int)(1000 / scaleX), (int)(700 / scaleY), scaleX);
            Invoke("UpdateSelectionMetadata"); Invoke("UpdateSelectionVisuals");
            var document = (SnipDocument)Get("_snipDocument")!;
            document.Add(new SnipMark(SnipTool.Rectangle, Colors.Red, 3, [new(20, 20), new(430, 105)]));
            document.Add(new SnipMark(SnipTool.Arrow, Colors.DodgerBlue, 5, [new(120, 220), new(350, 130)]));
            document.Add(new SnipMark(SnipTool.Text, Colors.Red, 3, [new(430, 150)], "文字标注"));
            document.Add(new SnipMark(SnipTool.Mosaic, Colors.Red, 3, [new(60, 55), new(170, 85)]));
            Invoke("RefreshSnipDrawing");
            Render(capture, "snip-annotated", (int)(1000 / scaleX), (int)(700 / scaleY), scaleX);
            var toolbar = (FrameworkElement)Get("_snipToolbar")!;
            AssertInside((FrameworkElement)capture.Content, toolbar);
            var toolButtons = Visuals<ToggleButton>(toolbar).Where(b => b.Tag is SnipTool).ToArray();
            Assert.Equal(7, toolButtons.Length);
            foreach (var caption in new[] { "矩形", "箭头", "画笔", "文字", "马赛克", "取色" })
                Assert.Contains(toolButtons, b => System.Windows.Automation.AutomationProperties.GetName(b) == caption);
            Assert.DoesNotContain(Visuals<Button>(toolbar), b => Equals(b.Content, "保存"));

            var drawing = Get("_snipSurface")!;
            toolButtons.Single(b => Equals(b.Tag, SnipTool.Rectangle)).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            Assert.Equal(Visibility.Visible, ((FrameworkElement)Get("_snipOptions")!).Visibility);
            Render(capture, "snip-tools-refined", (int)(1000 / scaleX), (int)(700 / scaleY), scaleX);
            Assert.True(((UIElement)drawing).IsHitTestVisible);
            Assert.False(((UIElement)capture.FindName("SelectionBody")).IsHitTestVisible);
            toolButtons.Single(b => Equals(b.Tag, SnipTool.Select)).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            Assert.Equal(Visibility.Collapsed, ((FrameworkElement)Get("_snipOptions")!).Visibility);
            Assert.False(((UIElement)drawing).IsHitTestVisible);
            Assert.True(((UIElement)capture.FindName("SelectionBody")).IsHitTestVisible);
            toolButtons.Single(b => Equals(b.Tag, SnipTool.Picker)).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            Assert.Equal(Visibility.Visible, ((FrameworkElement)Get("_snipColorPreview")!).Visibility);
            Assert.Equal(Visibility.Collapsed, ((FrameworkElement)Get("_snipOptions")!).Visibility);
            Invoke("SetSnipStatus", "#EEF5FC    RGB(238, 245, 252) · 单击复制 HEX");
            Render(capture, "snip-picker", (int)(1000 / scaleX), (int)(700 / scaleY), scaleX);
            Render(capture, "snip-narrow-before-layout", 600, 420);
            Invoke("LayoutSnipToolbar");
            Render(capture, "snip-narrow", 600, 420);
            AssertInside((FrameworkElement)capture.Content, toolbar);
            Render(capture, "snip-restored", (int)(1000 / scaleX), (int)(700 / scaleY), scaleX);
            Invoke("LayoutSnipToolbar");
            toolButtons.Single(b => Equals(b.Tag, SnipTool.Text)).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            Invoke("BeginSnipText", new Point(20, 240));
            ((TextBox)Get("_snipText")!).Text = "编辑后确认";
            Invoke("CommitSnipText");
            Assert.Equal("编辑后确认", document.Marks.Last().Text);

            var failed = capture.CopySnipAsync(_ => throw new InvalidOperationException("fixture failure"));
            PumpUntil(() => failed.IsCompleted); failed.GetAwaiter().GetResult();
            Assert.Contains("复制失败", ((TextBlock)Get("_snipStatus")!).Text);
            Assert.True(capture.HasSelection); Assert.Equal(5, document.Marks.Count);
            Assert.False((bool)Get("_snipClosed")!);

            var success = capture.CopySnipAsync(image => copied = image);
            PumpUntil(() => success.IsCompleted); success.GetAwaiter().GetResult();
            Assert.True((bool)Get("_snipClosed")!);
            Assert.NotNull(copied); Assert.Equal(600, copied.PixelWidth); Assert.Equal(300, copied.PixelHeight);
            Assert.True(copied.IsFrozen);
        }
        finally { capture.Close(); }

        var first = new PinnedImageWindow(copied!);
        var second = new PinnedImageWindow(copied!);
        try
        {
            Assert.True(first.Topmost); Assert.True(second.Topmost);
            Render(first, "snip-pinned", 604, 338);
            first.Close();
            BitmapSource? pinned = null;
            var copy = second.CopyAsync(image => pinned = image);
            PumpUntil(() => copy.IsCompleted); copy.GetAwaiter().GetResult();
            Assert.Same(copied, pinned);
            Assert.Equal(600, pinned!.PixelWidth);
            // 原框选窗口已经关闭并释放底图，两张贴图仍独立持有导出图片。
            Assert.Equal(Colors.Red, SnipDocument.Sample(pinned, 20, 40));
        }
        finally { second.Close(); }
    }

    private static void VerifySnipAdversarialInput()
    {
        using var pixels = new System.Drawing.Bitmap(1000, 700);
        var capture = new ScreenCaptureWindow((System.Drawing.Bitmap)pixels.Clone(), new System.Drawing.Rectangle(0, 0, 1000, 700), snippingTools: true);
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        object? Get(string name) => typeof(ScreenCaptureWindow).GetField(name, flags)!.GetValue(capture);
        void Set(string name, object value) => typeof(ScreenCaptureWindow).GetField(name, flags)!.SetValue(capture, value);
        void Invoke(string name, params object[] args) => typeof(ScreenCaptureWindow).GetMethod(name, flags)!.Invoke(capture, args);
        try
        {
            capture.Show(); capture.UpdateLayout();
            var recovery = new Window { Width = 200, Height = 100, ShowInTaskbar = false };
            try
            {
                recovery.Show();
                capture.ApplySnipRecovery(true, new System.Windows.Interop.WindowInteropHelper(recovery).Handle);
                Assert.False(capture.Topmost);
                capture.ApplySnipRecovery(false, IntPtr.Zero);
                Assert.True(capture.Topmost);
            }
            finally { recovery.Close(); }
            Invoke("SetSnipTool", SnipTool.Picker);
            var tool = Visuals<ToggleButton>((FrameworkElement)Get("_snipToolbar")!).Single(b => Equals(b.Tag, SnipTool.Picker));
            tool.Focus();
            var enter = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(capture), 0, Key.Enter) { RoutedEvent = Keyboard.PreviewKeyDownEvent };
            tool.RaiseEvent(enter);
            Assert.True(enter.Handled, "工具获得焦点后 Enter 仍须执行截图命令，而不是被按钮吃掉。");
            Assert.Contains("请先", ((TextBlock)Get("_snipStatus")!).Text);

            var sx = (double)Get("_dpiX")!; var sy = (double)Get("_dpiY")!;
            Set("_selectionWpfRect", new Rect(100 / sx, 100 / sy, 600 / sx, 300 / sy)); Set("_hasSelection", true);
            Invoke("UpdateSelectionMetadata"); Invoke("UpdateSelectionVisuals"); Invoke("SetSnipTool", SnipTool.Select);
            var before = capture.Selection;
            var attempts = 0;
            var copy = capture.CopySnipAsync(_ => { attempts++; throw new System.Runtime.InteropServices.ExternalException("clipboard busy fixture", unchecked((int)0x800401D0)); });
            Assert.False(copy.IsCompleted);
            Assert.False(((UIElement)capture.FindName("SelectionBody")).IsHitTestVisible, "剪贴板重试时不能继续移动即将复制的选区。");
            Assert.All(Visuals<ToggleButton>((FrameworkElement)Get("_snipToolbar")!), b => Assert.False(b.IsEnabled));
            Assert.True(Visuals<Button>((FrameworkElement)Get("_snipToolbar")!).Single(b => Equals(b.ToolTip, "取消（Esc）")).IsEnabled);
            Invoke("SelectionBody_DragDelta", capture, new DragDeltaEventArgs(20, 20));
            Assert.Equal(before, capture.Selection);
            Invoke("SetSnipTool", SnipTool.Pen);
            Assert.Equal(SnipTool.Select, Get("_snipTool"));
            PumpUntil(() => copy.IsCompleted); copy.GetAwaiter().GetResult();
            Assert.Equal(5, attempts);
            Assert.True(((UIElement)capture.FindName("SelectionBody")).IsHitTestVisible);
            Assert.Contains("复制失败", ((TextBlock)Get("_snipStatus")!).Text);

            var cancelledAttempts = 0;
            var cancelled = capture.CopySnipAsync(_ => { cancelledAttempts++; throw new System.Runtime.InteropServices.ExternalException("clipboard busy fixture", unchecked((int)0x800401D0)); });
            capture.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(capture), 0, Key.Escape) { RoutedEvent = Keyboard.KeyDownEvent });
            PumpUntil(() => cancelled.IsCompleted); cancelled.GetAwaiter().GetResult();
            Assert.Equal(1, cancelledAttempts);
            capture.ApplySnipRecovery(true, IntPtr.Zero); // 延迟的恢复通知不能触碰已关闭窗口。
        }
        finally { capture.Close(); }

        var image = SnipFixture(600, 300);
        var pin = PinnedImageWindow.Open(image, 100, 100);
        try
        {
            pin.Width = 340; pin.Height = 220; pin.UpdateLayout();
            var display = Visuals<Image>(pin).Single();
            var before = Math.Min(display.ActualWidth * VisualTreeHelper.GetDpi(pin).DpiScaleX / image.PixelWidth,
                display.ActualHeight * VisualTreeHelper.GetDpi(pin).DpiScaleY / image.PixelHeight);
            pin.RaiseEvent(new MouseWheelEventArgs(Mouse.PrimaryDevice, 0, 120) { RoutedEvent = Mouse.PreviewMouseWheelEvent });
            pin.UpdateLayout();
            var after = Math.Min(display.ActualWidth * VisualTreeHelper.GetDpi(pin).DpiScaleX / image.PixelWidth,
                display.ActualHeight * VisualTreeHelper.GetDpi(pin).DpiScaleY / image.PixelHeight);
            Assert.InRange(after / before, 1.08, 1.12);
        }
        finally { pin.Close(); }
    }

    private static BitmapSource SnipFixture(int width, int height)
    {
        var image = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, new byte[width * height * 4], width * 4);
        image.Freeze(); return image;
    }
}
