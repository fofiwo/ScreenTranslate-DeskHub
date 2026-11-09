using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using OverTranslate.Models;
using OverTranslate.Services;
using Button = System.Windows.Controls.Button;
using TextBox = System.Windows.Controls.TextBox;
using ComboBox = System.Windows.Controls.ComboBox;
using Panel = System.Windows.Controls.Panel;
using DataObject = System.Windows.DataObject;
using Color = System.Windows.Media.Color;
using Point = System.Windows.Point;
using MouseEventArgs = System.Windows.Input.MouseEventArgs;
using MouseButtonEventArgs = System.Windows.Input.MouseButtonEventArgs;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;

namespace OverTranslate.Views.Capture;

public partial class ScreenCaptureWindow
{
    private Canvas? _snipCanvas;
    private SnipSurface? _snipSurface;
    private Border? _snipToolbar;
    private TextBlock? _snipStatus;
    private TextBlock? _snipSize;
    private Button? _snipUndo;
    private Button? _snipRedo;
    private TextBox? _snipText;
    private BitmapSource? _snipSource;
    private SnipTool _snipTool;
    private Color _snipColor = Colors.Red;
    private double _snipThickness = 3;
    private readonly SnipDocument _snipDocument = new();
    private readonly List<Point> _snipPoints = [];
    private readonly Dictionary<SnipTool, ToggleButton> _snipTools = [];
    private readonly CancellationTokenSource _snipCancellation = new();
    private bool _snipWriting;
    private bool _snipClosed;
    private bool _snipRecoveryYielded;
    private (SnipMark Mark, SnipMark[] Redo)? _snipDoubleClickStroke;
    private bool _snipCopyRgb;

    internal void ApplySnipRecovery(bool yielded, IntPtr recoveryWindow)
    {
        if (_snipClosed) return;
        _snipRecoveryYielded = yielded;
        Topmost = !yielded;
        if (yielded && !AlwaysOnTop.PlaceBehind(this, recoveryWindow))
        {
            _snipRecoveryYielded = false;
            Topmost = true;
        }
    }

    private void InitializeSnippingTools()
    {
        // 快捷键使用物理按键；仅文字标注框允许输入法，避免 C 被中文组合输入吞掉。
        InputMethod.SetIsInputMethodEnabled(this, false);
        PreviewGotKeyboardFocus += (_, e) => {
            if (e.NewFocus is DependencyObject target) InputMethod.SetIsInputMethodEnabled(target, target is TextBox);
        };
        _snipCanvas = new Canvas();
        ((Grid)Content).Children.Add(_snipCanvas);
        _snipSurface = new SnipSurface(_snipDocument) { ClipToBounds = true, Visibility = Visibility.Collapsed };
        _snipSurface.MouseLeftButtonDown += SnipDrawingDown;
        _snipSurface.MouseMove += SnipDrawingMove;
        _snipSurface.MouseLeftButtonUp += (_, e) => { FinishSnipStroke(); e.Handled = true; };
        _snipSurface.LostMouseCapture += (_, _) => FinishSnipStroke();
        _snipCanvas.Children.Add(_snipSurface);
        SelectionFill.Fill = System.Windows.Media.Brushes.Transparent;

        InitializeSnipChrome();
        HintHost.Visibility = Visibility.Collapsed;
        Loaded += (_, _) => { HintHost.Visibility = Visibility.Collapsed; LayoutSnipToolbar(); SetSnipTool(SnipTool.Select); };
        SizeChanged += (_, _) => LayoutSnipToolbar();
        PreviewMouseMove += SnipPreviewMove;
        PreviewMouseLeftButtonDown += SnipPickerDown;
        Closed += (_, _) => { _snipClosed = true; _snipCancellation.Cancel(); _snipCancellation.Dispose(); };
    }

    private void SetSnipTool(SnipTool tool)
    {
        if (_snipWriting || _snipClosed) return;
        CommitSnipText(); FinishSnipStroke(); _snipTool = tool;
        _snipDoubleClickStroke = null;
        foreach (var (kind, button) in _snipTools) button.IsChecked = kind == tool;
        RefreshSnipSelection();
        SetSnipStatus(tool switch {
            SnipTool.Select => "拖动移动选区，四角调整大小 · 双击 / Enter 复制 · C 复制色值",
            SnipTool.Picker => "移动鼠标实时取色 · C / 单击复制色值 · Shift 切换 HEX/RGB · 双击选区复制截图",
            SnipTool.Text => "在选区单击输入文字 · Enter 确认 · Shift+Enter 换行",
            _ => "在选区拖动标注 · Ctrl+Z 撤销 · Ctrl+Y 重做" });
        Focus();
    }

    private void RefreshSnipSelection()
    {
        if (_snipCanvas is null || _snipSurface is null) return;
        if (_hasSelection && ScreenshotImage.Source is BitmapSource desktop)
        {
            var x = Math.Clamp((int)(Selection.X - _physBounds.Left), 0, desktop.PixelWidth - 1);
            var y = Math.Clamp((int)(Selection.Y - _physBounds.Top), 0, desktop.PixelHeight - 1);
            var width = Math.Min((int)Selection.Width, desktop.PixelWidth - x);
            var height = Math.Min((int)Selection.Height, desktop.PixelHeight - y);
            _snipSource = new CroppedBitmap(desktop, new Int32Rect(x, y, Math.Max(1, width), Math.Max(1, height)));
            _snipSource.Freeze();
            _snipSurface.Source = _snipSource;
            _snipSurface.Width = _selectionWpfRect.Width; _snipSurface.Height = _selectionWpfRect.Height;
            Canvas.SetLeft(_snipSurface, _selectionWpfRect.Left); Canvas.SetTop(_snipSurface, _selectionWpfRect.Top);
            _snipSurface.Visibility = Visibility.Visible;
            _snipSize!.Text = $"{width} × {height}";
        }
        var drawing = _hasSelection && _snipTool is not SnipTool.Select and not SnipTool.Picker;
        _snipOptions!.Visibility = drawing ? Visibility.Visible : Visibility.Collapsed;
        _snipToolbar!.Visibility = _hasSelection || _snipTool == SnipTool.Picker ? Visibility.Visible : Visibility.Collapsed;
        _snipWelcome!.Visibility = !_hasSelection && _snipTool != SnipTool.Picker ? Visibility.Visible : Visibility.Collapsed;
        _snipSizeBadge!.Visibility = _hasSelection ? Visibility.Visible : Visibility.Collapsed;
        RefreshSnipDrawing(); LayoutSnipToolbar();
        if (_snipColorHud?.Visibility == Visibility.Visible) UpdateSnipColor(Mouse.GetPosition(this));
    }

    private void SetSnipWriting(bool writing)
    {
        _snipWriting = writing;
        if (!_snipClosed) RefreshSnipDrawing();
    }

    private void RefreshSnipInteraction()
    {
        var editing = !_snipWriting;
        _snipSurface!.IsHitTestVisible = editing && _hasSelection && _snipTool is not SnipTool.Select and not SnipTool.Picker;
        SelectionBody.IsHitTestVisible = editing && _snipTool == SnipTool.Select;
        SetHandlesVisibility(editing && _hasSelection && _snipTool == SnipTool.Select);
        foreach (var (tool, button) in _snipTools)
            button.IsEnabled = editing && (_hasSelection || tool is SnipTool.Select or SnipTool.Picker);
        foreach (var button in _snipWriteActions) button.IsEnabled = editing;
        _snipOptions!.IsEnabled = editing;
        _snipCopy!.IsEnabled = _snipPin!.IsEnabled = editing && _hasSelection;
        _snipUndo!.IsEnabled = editing && _snipDocument.CanUndo;
        _snipRedo!.IsEnabled = editing && _snipDocument.CanRedo;
    }

    private void LayoutSnipToolbar()
    {
        if (_snipToolbar is null || _snipCanvas is null || _snipCanvas.ActualWidth < 1) return;
        var physical = _hasSelection ? new System.Drawing.Point((int)(Selection.X + Selection.Width / 2), (int)(Selection.Y + Selection.Height / 2))
            : System.Windows.Forms.Cursor.Position;
        var screen = System.Windows.Forms.Screen.FromPoint(physical).Bounds;
        var left = Math.Max(0, (screen.Left - _physBounds.Left) / _dpiX); var top = Math.Max(0, (screen.Top - _physBounds.Top) / _dpiY);
        var width = Math.Min(screen.Width / _dpiX, _snipCanvas.ActualWidth - left);
        var height = Math.Min(screen.Height / _dpiY, _snipCanvas.ActualHeight - top);
        _snipToolbar.MaxWidth = Math.Max(80, width - 24);
        _snipToolbar.Measure(new System.Windows.Size(_snipToolbar.MaxWidth, double.PositiveInfinity));
        var barWidth = _snipToolbar.DesiredSize.Width;
        var barHeight = _snipToolbar.DesiredSize.Height;
        var x = _hasSelection ? _selectionWpfRect.Right - barWidth : left + (width - barWidth) / 2;
        var y = _hasSelection ? _selectionWpfRect.Bottom + 8 : top + 16;
        if (y + barHeight > top + height - 8) y = _selectionWpfRect.Top - barHeight - 8;
        Canvas.SetLeft(_snipToolbar, Math.Clamp(x, left + 8, Math.Max(left + 8, left + width - barWidth - 8)));
        Canvas.SetTop(_snipToolbar, Math.Clamp(y, top + 8, Math.Max(top + 8, top + height - barHeight - 8)));
        _snipWelcome!.Measure(new System.Windows.Size(Math.Max(80, width - 24), double.PositiveInfinity));
        Canvas.SetLeft(_snipWelcome, left + (width - _snipWelcome.DesiredSize.Width) / 2);
        Canvas.SetTop(_snipWelcome, top + 28);
        Canvas.SetLeft(_snipSizeBadge!, _selectionWpfRect.Left);
        Canvas.SetTop(_snipSizeBadge!, _selectionWpfRect.Top >= top + 32 ? _selectionWpfRect.Top - 30 : _selectionWpfRect.Top + 8);
    }

    private Point SnipPoint(MouseEventArgs e)
    {
        var p = e.GetPosition(_snipSurface!);
        return new Point(Math.Clamp(p.X / _snipSurface!.Width * _snipSource!.PixelWidth, 0, _snipSource.PixelWidth),
            Math.Clamp(p.Y / _snipSurface.Height * _snipSource.PixelHeight, 0, _snipSource.PixelHeight));
    }

    private void SnipDrawingDown(object sender, MouseButtonEventArgs e)
    {
        if (_snipWriting || _snipSource is null) return;
        CommitSnipText();
        var point = SnipPoint(e);
        if (_snipTool == SnipTool.Text) BeginSnipText(point);
        else { _snipPoints.Clear(); _snipPoints.Add(point); _snipSurface!.CaptureMouse(); RefreshSnipDrawing(); }
        e.Handled = true;
    }

    private void SnipDrawingMove(object sender, MouseEventArgs e)
    {
        if (_snipPoints.Count == 0 || e.LeftButton != MouseButtonState.Pressed) return;
        var point = SnipPoint(e);
        if (_snipTool != SnipTool.Pen && _snipPoints.Count > 1) _snipPoints[^1] = point;
        else _snipPoints.Add(point);
        RefreshSnipDrawing(); e.Handled = true;
    }

    private SnipMark? PendingSnipMark() => _snipPoints.Count == 0 ? null : new(_snipTool, _snipColor, _snipThickness, _snipPoints.ToArray());

    private void FinishSnipStroke()
    {
        if (PendingSnipMark() is not { } mark) return;
        _snipPoints.Clear();
        _snipDoubleClickStroke = null;
        if (mark.Tool == SnipTool.Pen || (mark.Points.Count > 1 && (mark.Points[^1] - mark.Points[0]).Length >= 1))
        {
            var clickSize = System.Windows.Forms.SystemInformation.DoubleClickSize;
            var click = mark.Tool == SnipTool.Pen && mark.Points.All(p => Math.Abs(p.X - mark.Points[0].X) <= clickSize.Width / 2.0
                && Math.Abs(p.Y - mark.Points[0].Y) <= clickSize.Height / 2.0);
            var redo = click ? _snipDocument.CaptureRedo() : [];
            _snipDocument.Add(mark);
            if (click) _snipDoubleClickStroke = (_snipDocument.Marks[^1], redo);
        }
        _snipSurface?.ReleaseMouseCapture(); RefreshSnipDrawing();
    }

    private void BeginSnipText(Point point)
    {
        if (_snipCanvas is null || _snipSource is null) return;
        var box = new TextBox { AcceptsReturn = true, FontSize = Math.Max(16, _snipThickness * 6) / _dpiY,
            Foreground = new SolidColorBrush(_snipColor), Background = System.Windows.Media.Brushes.White,
            Width = Math.Max(30, Math.Min(280, _selectionWpfRect.Width - point.X / _dpiX)), MinHeight = 30,
            MaxLength = 4000, Tag = new SnipMark(SnipTool.Text, _snipColor, _snipThickness, [point]) };
        System.Windows.Automation.AutomationProperties.SetName(box, "截图标注文字");
        InputMethod.SetIsInputMethodEnabled(box, true);
        Canvas.SetLeft(box, _selectionWpfRect.Left + point.X / _snipSource.PixelWidth * _selectionWpfRect.Width);
        Canvas.SetTop(box, _selectionWpfRect.Top + point.Y / _snipSource.PixelHeight * _selectionWpfRect.Height);
        box.PreviewKeyDown += (_, e) => { if (e.Key == Key.Enter && !Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)) { CommitSnipText(); Focus(); e.Handled = true; } };
        box.LostKeyboardFocus += (_, _) => CommitSnipText();
        _snipText = box; _snipCanvas.Children.Add(box); box.Focus();
    }

    private void CommitSnipText()
    {
        if (_snipText is not { } box) return;
        _snipText = null;
        if (!string.IsNullOrWhiteSpace(box.Text)) _snipDocument.Add(((SnipMark)box.Tag) with { Text = box.Text });
        _snipCanvas?.Children.Remove(box); RefreshSnipDrawing();
    }

    private void RefreshSnipDrawing()
    {
        if (_snipSurface is null) return;
        _snipSurface.Preview = PendingSnipMark(); _snipSurface.InvalidateVisual();
        RefreshSnipInteraction();
    }

    private bool IsSnipToolbarTarget(DependencyObject? source)
    {
        while (source is not null)
        {
            if (ReferenceEquals(source, _snipToolbar) || ReferenceEquals(source, _snipWelcome)) return true;
            source = source is Visual ? VisualTreeHelper.GetParent(source) : LogicalTreeHelper.GetParent(source);
        }
        return false;
    }

    private Color SnipColorAt(Point point)
    {
        var x = Math.Clamp((int)(point.X * _dpiX), 0, _screenshot.Width - 1);
        var y = Math.Clamp((int)(point.Y * _dpiY), 0, _screenshot.Height - 1);
        var color = _screenshot.GetPixel(x, y);
        return Color.FromRgb(color.R, color.G, color.B);
    }

    private void SnipPreviewMove(object sender, MouseEventArgs e)
    {
        if (_snipClosed) return;
        UpdateSnipColor(e.GetPosition(this));
    }

    internal Color UpdateSnipColor(Point point)
    {
        var color = SnipColorAt(point);
        _snipColorPreview!.Background = new SolidColorBrush(color);
        _snipColorLabel!.Foreground = new SolidColorBrush(color);
        var region = new Rect(0, 0, Math.Max(1, _snipCanvas!.ActualWidth), Math.Max(1, _snipCanvas.ActualHeight));
        if (_hasSelection) region.Intersect(_selectionWpfRect);
        if (region.IsEmpty || region.Width <= 0 || region.Height <= 0)
        {
            _snipColorHud!.Visibility = Visibility.Collapsed;
            return color;
        }
        var inset = Math.Min(8, Math.Min(region.Width, region.Height) / 4);
        var hex = SnipDocument.Hex(color);
        var rgb = $"RGB({color.R}, {color.G}, {color.B})";
        var primary = _snipCopyRgb ? rgb : hex;
        var secondary = _snipCopyRgb ? hex : rgb;
        _snipColorLabel!.Text = $"{primary}\n{secondary}\nC 复制{(_snipCopyRgb ? "RGB" : "HEX")} · Shift切换";
        _snipColorLabel.Measure(new System.Windows.Size(double.PositiveInfinity, double.PositiveInfinity));
        if (_snipColorLabel.DesiredSize.Width + _snipColorPreview.Width + _snipColorPreview.Margin.Left + _snipColorPreview.Margin.Right > region.Width - inset * 2)
            _snipColorLabel.Text = $"{primary}\nC 复制";
        _snipColorHud!.MaxWidth = Math.Max(1, region.Width - inset * 2);
        _snipColorHud.MaxHeight = Math.Max(1, region.Height - inset * 2);
        _snipColorHud!.Visibility = Visibility.Visible;
        _snipColorHud.Measure(new System.Windows.Size(double.PositiveInfinity, double.PositiveInfinity));
        var size = _snipColorHud.DesiredSize;
        var x = _hasSelection ? region.Left + inset : point.X + 20;
        var y = _hasSelection ? region.Bottom - inset - size.Height : point.Y + 24;
        x = Math.Clamp(x, region.Left, Math.Max(region.Left, region.Right - size.Width));
        y = Math.Clamp(y, region.Top, Math.Max(region.Top, region.Bottom - size.Height));
        Canvas.SetLeft(_snipColorHud, x);
        Canvas.SetTop(_snipColorHud, y);
        return color;
    }

    internal async Task CopySnipColorAsync(Point? point = null, Action<string>? write = null)
    {
        if (_snipWriting || _snipClosed) return;
        var position = point ?? Mouse.GetPosition(this);
        if (position.X < 0 || position.Y < 0 || position.X >= ActualWidth || position.Y >= ActualHeight) return;
        FinishSnipStroke();
        var color = UpdateSnipColor(position); SetSnipWriting(true);
        try
        {
            var value = _snipCopyRgb ? $"RGB({color.R}, {color.G}, {color.B})" : SnipDocument.Hex(color);
            await SnipClipboard.WriteAsync(() => {
                if (write is not null) write(value); else System.Windows.Clipboard.SetDataObject(value, true);
            }, _snipCancellation.Token);
            if (!_snipClosed) { ApplySnipColor(color); SetSnipStatus($"已复制 {value} · Shift 切换 HEX/RGB"); }
        }
        catch (OperationCanceledException) { }
        catch (Exception) { if (!_snipClosed) SetSnipStatus("颜色复制失败，请重试；可按 Esc 取消。"); }
        finally { SetSnipWriting(false); }
    }

    private bool CanCopySnipOnDoubleClick(Point point, DependencyObject? source)
    {
        if (_snipClosed || _snipWriting || !_hasSelection || !_selectionWpfRect.Contains(point) || IsSnipToolbarTarget(source)) return false;
        for (var current = source; current is not null; current = current is Visual ? VisualTreeHelper.GetParent(current) : LogicalTreeHelper.GetParent(current))
            if (current is TextBox || ReferenceEquals(current, TopLeftHandle) || ReferenceEquals(current, TopRightHandle)
                || ReferenceEquals(current, BottomLeftHandle) || ReferenceEquals(current, BottomRightHandle)) return false;
        return true;
    }

    internal async Task<bool> CopySnipOnDoubleClickAsync(Point point, DependencyObject? source, Action<BitmapSource>? write = null)
    {
        if (!CanCopySnipOnDoubleClick(point, source)) return false;
        // 双击的第一下可能被画笔记为一个点；完成截图时不把这个点带入结果。
        if (_snipDoubleClickStroke is { } stroke) _snipDocument.DiscardLast(stroke.Mark, stroke.Redo);
        _snipDoubleClickStroke = null;
        await CopySnipAsync(write);
        return true;
    }

    private async void SnipPickerDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2 && CanCopySnipOnDoubleClick(e.GetPosition(this), e.OriginalSource as DependencyObject))
        {
            e.Handled = true;
            await CopySnipOnDoubleClickAsync(e.GetPosition(this), e.OriginalSource as DependencyObject);
            return;
        }
        // 只关联本次双击的第一下，不能删除更早的独立画笔点。
        _snipDoubleClickStroke = null;
        if (_snipTool != SnipTool.Picker && !_hasSelection && !IsSnipToolbarTarget(e.OriginalSource as DependencyObject))
            _snipWelcome!.Visibility = Visibility.Collapsed;
        if (_snipTool != SnipTool.Picker || IsSnipToolbarTarget(e.OriginalSource as DependencyObject)) return;
        e.Handled = true;
        if (_snipWriting) return;
        await CopySnipColorAsync(e.GetPosition(this));
    }

    internal async Task CopySnipAsync(Action<BitmapSource>? write = null)
    {
        if (_snipWriting || _snipClosed) return;
        if (_snipSource is null) { SetSnipStatus("请先拖动框选截图区域。"); return; }
        FinishSnipStroke(); CommitSnipText(); SetSnipWriting(true);
        try
        {
            var image = _snipDocument.Render(_snipSource);
            await SnipClipboard.WriteAsync(() => {
                if (write is not null) write(image);
                else { var data = new DataObject(); data.SetImage(image); System.Windows.Clipboard.SetDataObject(data, true); }
            }, _snipCancellation.Token);
            if (!_snipClosed) Close();
        }
        catch (OperationCanceledException) { }
        catch (Exception) { if (!_snipClosed) SetSnipStatus("截图复制失败，选区和标注已保留，请重试。"); }
        finally { SetSnipWriting(false); }
    }

    private void PinSnip()
    {
        if (_snipSource is null || _snipWriting) { SetSnipStatus("请先拖动框选截图区域。"); return; }
        FinishSnipStroke(); CommitSnipText();
        try
        {
            PinnedImageWindow.Open(_snipDocument.Render(_snipSource), (int)Selection.Left, (int)Selection.Top);
            Close();
        }
        catch (Exception) { SetSnipStatus("贴图未能打开，选区和标注已保留，请重试。"); }
    }

    private void SetSnipStatus(string text) { if (_snipStatus is not null) _snipStatus.Text = text; }

    private bool HandleSnipKey(KeyEventArgs e)
    {
        if (_snipCanvas is null || e.Handled || Keyboard.FocusedElement is TextBox) return false;
        var key = e.Key == Key.ImeProcessed ? e.ImeProcessedKey : e.Key;
        if (_snipWriting)
        {
            if (key is Key.Enter or Key.C or Key.P or Key.Z or Key.Y) { e.Handled = true; return true; }
            return false;
        }
        var control = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
        if (key is Key.LeftShift or Key.RightShift && (Keyboard.Modifiers & ~ModifierKeys.Shift) == ModifierKeys.None)
        {
            if (!e.IsRepeat)
            {
                _snipCopyRgb = !_snipCopyRgb;
                UpdateSnipColor(Mouse.GetPosition(this));
                SetSnipStatus($"当前复制格式：{(_snipCopyRgb ? "RGB" : "HEX (#RRGGBB)")} · C 复制 · Shift 切换");
            }
        }
        else if (control && key == Key.Z) { FinishSnipStroke(); _snipDocument.Undo(); RefreshSnipDrawing(); }
        else if (control && key == Key.Y) { _snipDocument.Redo(); RefreshSnipDrawing(); }
        else if (control && key == Key.P) PinSnip();
        else if ((control && key == Key.C) || key == Key.Enter) _ = CopySnipAsync();
        else if (Keyboard.Modifiers == ModifierKeys.None && key == Key.C) _ = CopySnipColorAsync();
        else return false;
        e.Handled = true; return true;
    }

    private sealed class SnipSurface(SnipDocument document) : FrameworkElement
    {
        public BitmapSource? Source { get; set; }
        public SnipMark? Preview { get; set; }
        protected override void OnRender(DrawingContext dc)
        {
            dc.DrawRectangle(System.Windows.Media.Brushes.Transparent, null, new Rect(RenderSize));
            if (Source is null) return;
            dc.PushTransform(new ScaleTransform(ActualWidth / Source.PixelWidth, ActualHeight / Source.PixelHeight));
            document.Draw(dc, Source, Preview); dc.Pop();
        }
    }
}

internal static class SnipClipboard
{
    internal static async Task WriteAsync(Action write, CancellationToken cancellationToken)
    {
        for (var attempt = 0; ; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try { write(); return; }
            catch (ExternalException ex) when (ex.ErrorCode == unchecked((int)0x800401D0) && attempt < 4)
            { await Task.Delay(40 * (attempt + 1), cancellationToken); }
        }
    }
}
