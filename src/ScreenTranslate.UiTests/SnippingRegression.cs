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
        VerifyCaptureEdgeResize();
        VerifySnipCopyResponsiveness();
        VerifySnipCopyAdversarial();
        VerifySnipImeAndReadableHud();
        VerifySnipReviewCases();
        VerifySnipAdversarialInput();
        VerifySnipQuickInteractions();
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
            capture.UpdateSnipColor(new Point(20 / scaleX, 20 / scaleY));
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
            Assert.True(capture.IsVisible, "复制失败应恢复截图界面。");
            Assert.True(capture.HasSelection); Assert.Equal(5, document.Marks.Count);
            Assert.Equal(SnipTool.Text, Get("_snipTool"));
            Assert.True(((Button)Get("_snipCopy")!).IsEnabled);
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
            Render(first, "snip-pinned", 600, 300);
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
            PumpUntil(() => Volatile.Read(ref cancelledAttempts) > 0);
            capture.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(capture), 0, Key.Escape) { RoutedEvent = Keyboard.KeyDownEvent });
            // STA 后台线程可能在 Esc 前已重试；取消后不得再增加写入次数。
            var attemptsAtCancellation = Volatile.Read(ref cancelledAttempts);
            PumpUntil(() => cancelled.IsCompleted); cancelled.GetAwaiter().GetResult();
            Assert.Equal(attemptsAtCancellation, Volatile.Read(ref cancelledAttempts));
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

    private static void VerifySnipQuickInteractions()
    {
        using var pixels = new System.Drawing.Bitmap(1000, 700);
        using (var graphics = System.Drawing.Graphics.FromImage(pixels))
        {
            graphics.Clear(System.Drawing.Color.FromArgb(12, 34, 56));
            graphics.FillRectangle(System.Drawing.Brushes.White, 100, 100, 600, 300);
            graphics.FillRectangle(System.Drawing.Brushes.Blue, 300, 100, 100, 100);
        }
        var capture = new ScreenCaptureWindow((System.Drawing.Bitmap)pixels.Clone(), new System.Drawing.Rectangle(0, 0, 1000, 700), snippingTools: true);
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        object? Get(string name) => typeof(ScreenCaptureWindow).GetField(name, flags)!.GetValue(capture);
        void Set(string name, object value) => typeof(ScreenCaptureWindow).GetField(name, flags)!.SetValue(capture, value);
        void Invoke(string name, params object[] args) => typeof(ScreenCaptureWindow).GetMethod(name, flags)!.Invoke(capture, args);
        void Complete(System.Threading.Tasks.Task task) { PumpUntil(() => task.IsCompleted); task.GetAwaiter().GetResult(); }
        try
        {
            capture.Show(); capture.UpdateLayout();
            var sx = (double)Get("_dpiX")!; var sy = (double)Get("_dpiY")!;
            var inside = new Point(150 / sx, 150 / sy);
            var outside = new Point(20 / sx, 20 / sy);
            Set("_selectionWpfRect", new Rect(100 / sx, 100 / sy, 600 / sx, 300 / sy)); Set("_hasSelection", true);
            Invoke("UpdateSelectionMetadata"); Invoke("UpdateSelectionVisuals");
            var document = (SnipDocument)Get("_snipDocument")!;
            document.Add(new SnipMark(SnipTool.Rectangle, Colors.Red, 3, [new(20, 20), new(200, 100)]));
            foreach (var tool in new[] { SnipTool.Select, SnipTool.Rectangle, SnipTool.Picker })
            {
                Invoke("SetSnipTool", tool);
                Assert.Equal(Colors.White, capture.UpdateSnipColor(inside));
                Assert.Equal(Colors.White, ((SolidColorBrush)((TextBlock)Get("_snipColorLabel")!).Foreground).Color);
                Assert.Equal(Colors.Blue, capture.UpdateSnipColor(new Point(350 / sx, 150 / sy)));
                Assert.Equal(Colors.Blue, ((SolidColorBrush)((TextBlock)Get("_snipColorLabel")!).Foreground).Color);
                Assert.Contains("#0000FF", ((TextBlock)Get("_snipColorLabel")!).Text);
                Assert.Equal(Color.FromRgb(12, 34, 56), capture.UpdateSnipColor(outside));
                Assert.Equal(Color.FromRgb(12, 34, 56), ((SolidColorBrush)((TextBlock)Get("_snipColorLabel")!).Foreground).Color);
                Assert.Contains("#0C2238", ((TextBlock)Get("_snipColorLabel")!).Text);
                Assert.Contains("RGB(12, 34, 56)", ((TextBlock)Get("_snipColorLabel")!).Text);
                Assert.Equal(new[] { "#0C2238", "RGB(12, 34, 56)", "C 复制HEX · Shift切换" }, ((TextBlock)Get("_snipColorLabel")!).Text.Split('\n'));
                Assert.Equal(Colors.White, capture.UpdateSnipColor(new Point(120 / sx, 140 / sy))); // 标注下仍取原图。
            }
            capture.UpdateSnipColor(new Point(capture.ActualWidth - 1, capture.ActualHeight - 1));
            capture.UpdateLayout();
            var hud = (FrameworkElement)Get("_snipColorHud")!;
            Assert.False(hud.IsHitTestVisible);
            Assert.Null(((Border)hud).Background);
            Assert.InRange(Canvas.GetLeft(hud), 100 / sx, 700 / sx - hud.ActualWidth);
            Assert.InRange(Canvas.GetTop(hud), 100 / sy, 400 / sy - hud.ActualHeight);
            AssertInside((FrameworkElement)capture.Content, hud);
            Render(capture, "snip-live-color", (int)(1000 / sx), (int)(700 / sy), sx);
            string? hex = null;
            Complete(capture.CopySnipColorAsync(outside, value => hex = value));
            Assert.Equal("#0C2238", hex);
            capture.Focus();
            void ShiftFormat() {
                var shift = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(capture), Environment.TickCount, Key.LeftShift) { RoutedEvent = Keyboard.PreviewKeyDownEvent };
                Invoke("HandleSnipKey", shift); Assert.True(shift.Handled);
            }
            ShiftFormat();
            Complete(capture.CopySnipColorAsync(outside, value => hex = value));
            Assert.Equal("RGB(12, 34, 56)", hex);
            capture.UpdateSnipColor(outside);
            Assert.StartsWith("RGB(12, 34, 56)", ((TextBlock)Get("_snipColorLabel")!).Text);
            ShiftFormat();
            Complete(capture.CopySnipColorAsync(outside, value => hex = value));
            Assert.Equal("#0C2238", hex);
            Assert.False((bool)Get("_snipClosed")!);
            Assert.True(capture.HasSelection);
            Complete(capture.CopySnipColorAsync(inside, _ => throw new InvalidOperationException("fixture")));
            Assert.Contains("颜色复制失败", ((TextBlock)Get("_snipStatus")!).Text);
            capture.UpdateSnipColor(outside);
            Assert.Contains("颜色复制失败", ((TextBlock)Get("_snipStatus")!).Text); // 移动不覆盖失败反馈。
            var outsideColorWritten = false;
            Complete(capture.CopySnipColorAsync(new Point(-1, -1), _ => outsideColorWritten = true));
            Assert.False(outsideColorWritten);

            foreach (var source in new DependencyObject[] { (DependencyObject)Get("_snipToolbar")!, new TextBox(), (DependencyObject)capture.FindName("TopLeftHandle") })
            {
                var rejected = capture.CopySnipOnDoubleClickAsync(inside, source, _ => Assert.Fail("控件不能触发双击截图"));
                Complete(rejected); Assert.False(rejected.Result);
            }
            var outsideCopy = capture.CopySnipOnDoubleClickAsync(outside, null, _ => Assert.Fail("不能双击选区外复制"));
            Complete(outsideCopy); Assert.False(outsideCopy.Result);
            // 画笔双击的首击点去除；已有矩形和其他独立画笔点保留。
            Invoke("SetSnipTool", SnipTool.Pen);
            ((List<Point>)Get("_snipPoints")!).Add(new Point(40, 40)); Invoke("FinishSnipStroke");
            Assert.Equal(2, document.Marks.Count);
            Complete(capture.CopySnipOnDoubleClickAsync(inside, null, _ => throw new InvalidOperationException("fixture")));
            Assert.Single(document.Marks);
            Assert.False((bool)Get("_snipClosed")!);
            ((List<Point>)Get("_snipPoints")!).Add(new Point(50, 50)); Invoke("FinishSnipStroke");
            Invoke("SetSnipTool", SnipTool.Select);
            BitmapSource? copied = null;
            var success = capture.CopySnipOnDoubleClickAsync(inside, null, image => copied = image);
            Complete(success); Assert.True(success.Result);
            Assert.True((bool)Get("_snipClosed")!);
            Assert.Equal(2, document.Marks.Count);
            Assert.NotNull(copied); Assert.Equal(600, copied.PixelWidth); Assert.Equal(300, copied.PixelHeight);
            Assert.Equal(Colors.Red, SnipDocument.Sample(copied, 20, 40));
            Assert.Equal(Color.FromRgb(12, 34, 56), SnipDocument.Sample(copied, 50, 50));
        }
        finally { capture.Close(); }

        foreach (var size in new[] { (1, 1), (3, 2), (600, 300), (5000, 3000) })
        {
            var pin = PinnedImageWindow.Open(SnipFixture(size.Item1, size.Item2), 100, 100);
            try
            {
                pin.UpdateLayout();
                Assert.Equal(WindowStyle.None, pin.WindowStyle); Assert.Equal(ResizeMode.NoResize, pin.ResizeMode);
                var display = Assert.IsType<Image>(pin.Content);
                Assert.Empty(Visuals<Button>(pin));
                var dpi = VisualTreeHelper.GetDpi(pin);
                var physical = OverTranslate.Services.ScreenGeometry.PhysicalBounds(pin);
                Assert.InRange(physical.Width, size.Item1 - 1.0, size.Item1 + 1.0);
                Assert.InRange(physical.Height, size.Item2 - 1.0, size.Item2 + 1.0);
                Assert.InRange(display.ActualWidth * dpi.DpiScaleX, size.Item1 - 1.0, size.Item1 + 1.0);
                Assert.InRange(display.ActualHeight * dpi.DpiScaleY, size.Item2 - 1.0, size.Item2 + 1.0);
                Assert.Equal(3, pin.ContextMenu.Items.Count);
                pin.SetZoom(0.5); pin.UpdateLayout();
                Assert.InRange(display.ActualWidth * dpi.DpiScaleX, Math.Max(1, Math.Round(size.Item1 * 0.5)) - 1, Math.Max(1, Math.Round(size.Item1 * 0.5)) + 1);
                ((MenuItem)pin.ContextMenu.Items[1]).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent)); pin.UpdateLayout();
                Assert.InRange(display.ActualWidth * dpi.DpiScaleX, size.Item1 - 1.0, size.Item1 + 1.0);
            }
            finally { pin.Close(); }
        }
    }

    private static void VerifySnipReviewCases()
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var failures = new List<Exception>();
        foreach (var scenario in new[] { "stationary-move-double-click", "discarded-click-redo", "preserve-existing-redo", "copy-color-mid-stroke", "offscreen-color-hud" })
        {
            using var pixels = new System.Drawing.Bitmap(1000, 700);
            using (var graphics = System.Drawing.Graphics.FromImage(pixels)) graphics.Clear(System.Drawing.Color.White);
            var capture = new ScreenCaptureWindow((System.Drawing.Bitmap)pixels.Clone(), new System.Drawing.Rectangle(0, 0, 1000, 700), snippingTools: true);
            object? Get(string name) => typeof(ScreenCaptureWindow).GetField(name, flags)!.GetValue(capture);
            void Set(string name, object value) => typeof(ScreenCaptureWindow).GetField(name, flags)!.SetValue(capture, value);
            void Invoke(string name, params object[] args) => typeof(ScreenCaptureWindow).GetMethod(name, flags)!.Invoke(capture, args);
            try
            {
                capture.Show(); capture.UpdateLayout();
                var sx = (double)Get("_dpiX")!; var sy = (double)Get("_dpiY")!;
                Set("_selectionWpfRect", new Rect(100 / sx, 100 / sy, 600 / sx, 300 / sy)); Set("_hasSelection", true);
                Invoke("UpdateSelectionMetadata"); Invoke("UpdateSelectionVisuals"); Invoke("SetSnipTool", SnipTool.Pen);
                var document = (SnipDocument)Get("_snipDocument")!;
                if (scenario == "preserve-existing-redo")
                {
                    document.Add(new SnipMark(SnipTool.Arrow, Colors.Blue, 3, [new(20, 20), new(200, 100)]));
                    document.Undo();
                }
                if (scenario == "offscreen-color-hud")
                {
                    Set("_selectionWpfRect", new Rect(capture.ActualWidth + 100, 100, 50, 50));
                    capture.UpdateSnipColor(new Point(150 / sx, 150 / sy));
                    Assert.Equal(Visibility.Collapsed, ((FrameworkElement)Get("_snipColorHud")!).Visibility);
                    Set("_selectionWpfRect", new Rect(100, 100, 60, 25));
                    capture.UpdateSnipColor(new Point(150 / sx, 150 / sy)); capture.UpdateLayout();
                    var hud = (FrameworkElement)Get("_snipColorHud")!;
                    Assert.Equal(Visibility.Visible, hud.Visibility);
                    Assert.InRange(Canvas.GetLeft(hud), 100, 160 - hud.ActualWidth);
                    Assert.InRange(Canvas.GetTop(hud), 100, 125 - hud.ActualHeight);
                    Assert.DoesNotContain("RGB", ((TextBlock)Get("_snipColorLabel")!).Text);
                    continue;
                }
                var points = (List<Point>)Get("_snipPoints")!;
                points.Add(new Point(40, 40));
                if (scenario == "stationary-move-double-click") points.Add(new Point(40, 40));
                if (scenario == "copy-color-mid-stroke")
                {
                    points.Add(new Point(100, 100));
                    var colorCopy = capture.CopySnipColorAsync(new Point(150 / sx, 150 / sy), _ => { });
                    PumpUntil(() => colorCopy.IsCompleted); colorCopy.GetAwaiter().GetResult();
                    Invoke("FinishSnipStroke");
                    Assert.Equal(Colors.Red, Assert.Single(document.Marks).Color);
                }
                else
                {
                    Invoke("FinishSnipStroke");
                    var copy = capture.CopySnipOnDoubleClickAsync(new Point(150 / sx, 150 / sy), null, _ => throw new InvalidOperationException("fixture failure"));
                    PumpUntil(() => copy.IsCompleted); copy.GetAwaiter().GetResult();
                    Assert.Empty(document.Marks);
                    if (scenario == "preserve-existing-redo")
                    {
                        Assert.True(document.Redo());
                        Assert.Equal(SnipTool.Arrow, Assert.Single(document.Marks).Tool);
                    }
                    else Assert.False(document.CanRedo, "双击首击点不能进入重做记录。");
                }
            }
            catch (Exception error) { failures.Add(new InvalidOperationException(scenario, error)); }
            finally { capture.Close(); }
        }
        var pin = PinnedImageWindow.Open(SnipFixture(600, 300), 100, 100);
        var memory = System.Runtime.InteropServices.Marshal.AllocHGlobal(16);
        try
        {
            // 合成单个测试窗口的 DPI 消息，不改系统显示设置或用户窗口。
            System.Runtime.InteropServices.Marshal.Copy(new[] { 100, 100, 1300, 700 }, 0, memory, 4);
            SendMessage(new System.Windows.Interop.WindowInteropHelper(pin).Handle, 0x02E0, new IntPtr(192 | (192 << 16)), memory);
            pin.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            pin.UpdateLayout();
            var actual = OverTranslate.Services.ScreenGeometry.PhysicalBounds(pin);
            Assert.InRange(actual.Width, 599, 601);
            Assert.InRange(actual.Height, 299, 301);
            pin.SetZoom(0.5);
            System.Runtime.InteropServices.Marshal.Copy(new[] { 100, 100, 250, 175 }, 0, memory, 4);
            SendMessage(new System.Windows.Interop.WindowInteropHelper(pin).Handle, 0x02E0, new IntPtr(96 | (96 << 16)), memory);
            pin.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            pin.UpdateLayout();
            actual = OverTranslate.Services.ScreenGeometry.PhysicalBounds(pin);
            Assert.InRange(actual.Width, 299, 301);
            Assert.InRange(actual.Height, 149, 151);
        }
        catch (Exception error) { failures.Add(new InvalidOperationException("pin-preserves-pixels-on-dpi-change", error)); }
        finally { System.Runtime.InteropServices.Marshal.FreeHGlobal(memory); pin.Close(); }
        if (failures.Count > 0) throw new AggregateException(failures);
    }

    private static void VerifySnipImeAndReadableHud()
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        using var pixels = new System.Drawing.Bitmap(1000, 700);
        var capture = new ScreenCaptureWindow((System.Drawing.Bitmap)pixels.Clone(), new System.Drawing.Rectangle(0, 0, 1000, 700), snippingTools: true);
        object? Get(string name) => typeof(ScreenCaptureWindow).GetField(name, flags)!.GetValue(capture);
        void Set(string name, object value) => typeof(ScreenCaptureWindow).GetField(name, flags)!.SetValue(capture, value);
        void Invoke(string name, params object[] args) => typeof(ScreenCaptureWindow).GetMethod(name, flags)!.Invoke(capture, args);
        KeyEventArgs ImeKey() {
            var key = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(capture), Environment.TickCount, Key.C) { RoutedEvent = Keyboard.PreviewKeyDownEvent };
            typeof(KeyEventArgs).GetMethod("MarkImeProcessed", flags)!.Invoke(key, null);
            return key;
        }
        try
        {
            capture.Show(); capture.Activate(); capture.Focus(); capture.UpdateLayout();
            Assert.False(InputMethod.GetIsInputMethodEnabled(capture), "截图快捷键层不能启动输入法组合输入。");
            Set("_selectionWpfRect", new Rect(100, 100, 600, 300)); Set("_hasSelection", true);
            Invoke("UpdateSelectionMetadata"); Invoke("UpdateSelectionVisuals");
            var button = Visuals<ToggleButton>(capture).Single(b => Equals(b.Tag, SnipTool.Pen));
            button.Focus();
            Assert.False(InputMethod.GetIsInputMethodEnabled(button), "工具栏取得焦点后同样不能启动输入法。");
            // 模拟 WPF 已经把物理 C 标为 ImeProcessed；忙碌路径只验证路由，不访问系统剪贴板。
            capture.Focus(); Set("_snipWriting", true);
            var busyKey = ImeKey(); Invoke("HandleSnipKey", busyKey);
            Assert.True(busyKey.Handled, "输入法处理后的 C 必须仍被截图快捷键路由识别。");
            Set("_snipWriting", false);
            Invoke("SetSnipTool", SnipTool.Text); Invoke("BeginSnipText", new Point(30, 30));
            var editor = (TextBox)Get("_snipText")!;
            Assert.True(InputMethod.GetIsInputMethodEnabled(editor), "文字标注必须继续允许中文输入。");
            editor.Text = "中文标注 c"; editor.Focus();
            var textKey = ImeKey(); Invoke("HandleSnipKey", textKey);
            Assert.False(textKey.Handled); Assert.Same(editor, Keyboard.FocusedElement);
            var textShift = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(capture), Environment.TickCount, Key.LeftShift) { RoutedEvent = Keyboard.PreviewKeyDownEvent };
            Invoke("HandleSnipKey", textShift); Assert.False(textShift.Handled);
            Assert.False((bool)Get("_snipCopyRgb")!);
            Invoke("CommitSnipText"); capture.Focus();
            Assert.Equal("中文标注 c", Assert.Single(((SnipDocument)Get("_snipDocument")!).Marks).Text);
            Assert.False(InputMethod.GetIsInputMethodEnabled(capture));
            capture.UpdateSnipColor(new Point(150, 150)); capture.UpdateLayout();
            var label = (TextBlock)Get("_snipColorLabel")!;
            var swatch = (Border)Get("_snipColorPreview")!;
            Assert.True(label.FontSize >= 16); Assert.True(swatch.Width >= 18 && swatch.Height >= 18);
            Assert.Contains("RGB", label.Text);
            Set("_selectionWpfRect", new Rect(100, 100, 160, 70));
            capture.UpdateSnipColor(new Point(150, 150)); capture.UpdateLayout();
            Assert.DoesNotContain("RGB", label.Text);
            Assert.Equal(new[] { "#000000", "C 复制" }, label.Text.Split('\n'));
            var hud = (FrameworkElement)Get("_snipColorHud")!;
            Assert.InRange(Canvas.GetLeft(hud) + hud.ActualWidth, 100, 260);
            Assert.InRange(Canvas.GetTop(hud) + hud.ActualHeight, 100, 170);
            Assert.Null(((Border)hud).Background); Assert.False(hud.IsHitTestVisible);
            Render(capture, "snip-readable-color", 1000, 700);
        }
        finally { capture.Close(); }
    }

    private static BitmapSource SnipFixture(int width, int height)
    {
        var image = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, new byte[width * height * 4], width * 4);
        image.Freeze(); return image;
    }
}
