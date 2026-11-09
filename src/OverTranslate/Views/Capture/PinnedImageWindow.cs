using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using OverTranslate.Services;
using Button = System.Windows.Controls.Button;
using Color = System.Windows.Media.Color;
using Image = System.Windows.Controls.Image;
using ContextMenu = System.Windows.Controls.ContextMenu;
using MenuItem = System.Windows.Controls.MenuItem;
using DataObject = System.Windows.DataObject;

namespace OverTranslate.Views.Capture;

public sealed class PinnedImageWindow : Window
{
    private static readonly HashSet<PinnedImageWindow> Windows = [];
    private readonly BitmapSource _image;
    private readonly TextBlock _status;
    private readonly Image _display;
    private bool _copying;
    private bool _closed;
    private readonly CancellationTokenSource _cancellation = new();

    internal PinnedImageWindow(BitmapSource image)
    {
        _image = image.IsFrozen ? image : image.Clone();
        if (!_image.IsFrozen) _image.Freeze();
        Title = "截图贴图"; Topmost = true; ShowInTaskbar = false;
        Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/ScreenTranslate;component/Views/Capture/SnipStyles.xaml", UriKind.Relative) });
        WindowStyle = WindowStyle.None; ResizeMode = ResizeMode.CanResizeWithGrip;
        MinWidth = 160; MinHeight = 90; Background = System.Windows.Media.Brushes.White;
        var root = new DockPanel();
        var header = new DockPanel { Background = System.Windows.Media.Brushes.White, Height = 38, LastChildFill = true };
        _status = new TextBlock { Text = "贴图 · 滚轮缩放", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 4, 0), IsHitTestVisible = false };
        var close = new Button { Style = (Style)Resources["SnipAction"], Width = 36, Height = 34, ToolTip = "关闭贴图（Esc）" };
        close.Content = SnipIcons.Create("x", close, 16);
        System.Windows.Automation.AutomationProperties.SetName(close, "关闭贴图");
        close.Click += (_, _) => Close(); DockPanel.SetDock(close, Dock.Right); header.Children.Add(close);
        var copy = new Button { Style = (Style)Resources["SnipAction"], Width = 36, Height = 34, ToolTip = "复制贴图（Ctrl+C）" };
        copy.Content = SnipIcons.Create("copy", copy, 16);
        System.Windows.Automation.AutomationProperties.SetName(copy, "复制贴图");
        copy.Click += async (_, _) => await CopyAsync(); DockPanel.SetDock(copy, Dock.Right); header.Children.Add(copy);
        header.Children.Add(_status); DockPanel.SetDock(header, Dock.Top); root.Children.Add(header);
        var display = _display = new Image { Source = _image, Stretch = Stretch.Uniform, Margin = new Thickness(1) };
        root.Children.Add(display);
        var border = new Border { Child = root, BorderBrush = new SolidColorBrush(Color.FromRgb(202, 213, 226)), BorderThickness = new Thickness(1) };
        Content = border;
        header.MouseLeftButtonDown += (_, e) => { if (e.ClickCount == 2) SetZoom(1); else DragMove(); };
        display.MouseLeftButtonDown += (_, _) => DragMove();
        PreviewMouseWheel += (_, e) => { if (e.Delta != 0) SetZoom(CurrentZoom * Math.Pow(1.1, e.Delta / 120.0)); e.Handled = true; };
        KeyDown += async (_, e) => {
            if (e.Key == Key.Escape) { Close(); e.Handled = true; }
            else if (e.Key == Key.C && Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) { await CopyAsync(); e.Handled = true; }
        };
        var menu = new ContextMenu();
        var reset = new MenuItem { Header = "原始大小" }; reset.Click += (_, _) => SetZoom(1); menu.Items.Add(reset);
        var closeItem = new MenuItem { Header = "关闭贴图" }; closeItem.Click += (_, _) => Close(); menu.Items.Add(closeItem); ContextMenu = menu;
        Closed += (_, _) => { _closed = true; _cancellation.Cancel(); _cancellation.Dispose(); Windows.Remove(this); };
        display.SizeChanged += (_, _) => _status.Text = $"贴图 · {CurrentZoom:P0}";
    }

    public static PinnedImageWindow Open(BitmapSource image, int physicalX, int physicalY)
    {
        var window = new PinnedImageWindow(image);
        try
        {
            window.SourceInitialized += (_, _) => ScreenGeometry.MoveToPhysical(window, physicalX, physicalY);
            window.Show(); window.SetZoom(1);
            Windows.Add(window); return window;
        }
        catch { window.Close(); throw; }
    }

    private double CurrentZoom
    {
        get
        {
            var dpi = VisualTreeHelper.GetDpi(this);
            return _display.ActualWidth > 0 && _display.ActualHeight > 0
                ? Math.Min(_display.ActualWidth * dpi.DpiScaleX / _image.PixelWidth, _display.ActualHeight * dpi.DpiScaleY / _image.PixelHeight)
                : 1;
        }
    }

    internal void SetZoom(double zoom)
    {
        UpdateLayout();
        var requested = Math.Clamp(zoom, 0.1, 4);
        var bounds = ScreenGeometry.PhysicalBounds(this);
        var screen = System.Windows.Forms.Screen.FromPoint(new System.Drawing.Point((int)bounds.Left, (int)bounds.Top)).WorkingArea;
        var scale = VisualTreeHelper.GetDpi(this).DpiScaleX;
        var horizontalChrome = Math.Max(4, ActualWidth - _display.ActualWidth);
        var verticalChrome = Math.Max(42, ActualHeight - _display.ActualHeight);
        var fit = Math.Min(requested, Math.Min((screen.Width / scale - horizontalChrome) * scale / _image.PixelWidth,
            (screen.Height / scale - verticalChrome) * scale / _image.PixelHeight));
        Width = Math.Max(MinWidth, _image.PixelWidth * fit / scale + horizontalChrome);
        Height = Math.Max(MinHeight, _image.PixelHeight * fit / scale + verticalChrome);
        UpdateLayout();
        ScreenGeometry.MoveToPhysical(this,
            (int)Math.Clamp(bounds.Left, screen.Left, Math.Max(screen.Left, screen.Right - Width * scale)),
            (int)Math.Clamp(bounds.Top, screen.Top, Math.Max(screen.Top, screen.Bottom - Height * scale)));
    }

    internal async Task CopyAsync(Action<BitmapSource>? write = null)
    {
        if (_copying) return;
        _copying = true;
        try
        {
            await SnipClipboard.WriteAsync(() => {
                if (write is not null) write(_image);
                else { var data = new DataObject(); data.SetImage(_image); System.Windows.Clipboard.SetDataObject(data, true); }
            }, _cancellation.Token);
            if (!_closed) _status.Text = "图片已复制";
        }
        catch (OperationCanceledException) { }
        catch (Exception) { if (!_closed) _status.Text = "复制失败，请重试"; }
        finally { _copying = false; }
    }

    public static void Shutdown() { foreach (var window in Windows.ToArray()) window.Close(); }
}
