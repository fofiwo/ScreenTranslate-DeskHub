using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using OverTranslate.Services;
using Image = System.Windows.Controls.Image;
using ContextMenu = System.Windows.Controls.ContextMenu;
using MenuItem = System.Windows.Controls.MenuItem;
using DataObject = System.Windows.DataObject;

namespace OverTranslate.Views.Capture;

public sealed class PinnedImageWindow : Window
{
    private static readonly HashSet<PinnedImageWindow> Windows = [];
    private readonly BitmapSource _image;
    private readonly Image _display;
    private readonly System.Windows.Controls.ToolTip _feedback;
    private readonly System.Windows.Threading.DispatcherTimer _feedbackTimer;
    private bool _copying;
    private bool _closed;
    private readonly CancellationTokenSource _cancellation = new();
    private HwndSource? _windowSource;
    private double _zoom = 1;

    internal PinnedImageWindow(BitmapSource image)
    {
        _image = image.IsFrozen ? image : image.Clone();
        if (!_image.IsFrozen) _image.Freeze();
        Title = "截图贴图"; Topmost = true; ShowInTaskbar = false;
        Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/ScreenTranslate;component/Views/Capture/SnipStyles.xaml", UriKind.Relative) });
        WindowStyle = WindowStyle.None; ResizeMode = ResizeMode.NoResize; AllowsTransparency = true;
        MinWidth = 0; MinHeight = 0; Background = System.Windows.Media.Brushes.Transparent;
        var display = _display = new Image { Source = _image, Stretch = Stretch.Fill, SnapsToDevicePixels = true };
        RenderOptions.SetBitmapScalingMode(display, BitmapScalingMode.NearestNeighbor);
        Content = display;
        SourceInitialized += (_, _) => {
            _windowSource = HwndSource.FromHwnd(new WindowInteropHelper(this).Handle);
            _windowSource.AddHook(AllowOriginalImageSize);
        };
        display.MouseLeftButtonDown += (_, e) => { if (e.ClickCount == 2) SetZoom(1); else DragMove(); };
        _feedback = new System.Windows.Controls.ToolTip { PlacementTarget = display, Placement = PlacementMode.MousePoint, StaysOpen = true };
        _feedbackTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _feedbackTimer.Tick += (_, _) => { _feedbackTimer.Stop(); _feedback.IsOpen = false; };
        PreviewMouseWheel += (_, e) => { if (e.Delta != 0) SetZoom(CurrentZoom * Math.Pow(1.1, e.Delta / 120.0)); e.Handled = true; };
        KeyDown += async (_, e) => {
            if (e.Key == Key.Escape) { Close(); e.Handled = true; }
            else if (e.Key == Key.C && Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) { await CopyAsync(); e.Handled = true; }
        };
        var menu = new ContextMenu();
        var copy = new MenuItem { Header = "复制图片", InputGestureText = "Ctrl+C" }; copy.Click += async (_, _) => await CopyAsync(); menu.Items.Add(copy);
        var reset = new MenuItem { Header = "原始尺寸（100%）", InputGestureText = "双击" }; reset.Click += (_, _) => SetZoom(1); menu.Items.Add(reset);
        var closeItem = new MenuItem { Header = "关闭贴图", InputGestureText = "Esc" }; closeItem.Click += (_, _) => Close(); menu.Items.Add(closeItem); ContextMenu = menu;
        Closed += (_, _) => { _closed = true; _windowSource?.RemoveHook(AllowOriginalImageSize); _feedbackTimer.Stop(); _feedback.IsOpen = false; _cancellation.Cancel(); _cancellation.Dispose(); Windows.Remove(this); };
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

    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);
        // WPF 在通知 DPI 变化后还会应用系统建议的放大尺寸；待该过程结束再恢复物理像素大小。
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, new Action(() => {
            if (!_closed) ResizeImage(VisualTreeHelper.GetDpi(this));
        }));
    }

    private void ResizeImage(DpiScale dpi)
    {
        Width = Math.Max(1, Math.Round(_image.PixelWidth * _zoom)) / dpi.DpiScaleX;
        Height = Math.Max(1, Math.Round(_image.PixelHeight * _zoom)) / dpi.DpiScaleY;
    }

    private IntPtr AllowOriginalImageSize(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message == 0x0024) // WM_GETMINMAXINFO
        {
            // WPF 会缓存系统的屏幕尺寸上限；先放宽该值，再让 WPF 处理，避免大图被缩小。
            var info = Marshal.PtrToStructure<MinMaxInfo>(lParam);
            info.MaxTrackSize.X = Math.Max(info.MaxTrackSize.X, _image.PixelWidth * 4);
            info.MaxTrackSize.Y = Math.Max(info.MaxTrackSize.Y, _image.PixelHeight * 4);
            Marshal.StructureToPtr(info, lParam, false);
        }
        return IntPtr.Zero;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint { public int X; public int Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MinMaxInfo
    {
        public NativePoint Reserved, MaxSize, MaxPosition, MinTrackSize, MaxTrackSize;
    }

    internal void SetZoom(double zoom)
    {
        UpdateLayout();
        _zoom = Math.Clamp(zoom, 0.1, 4);
        var bounds = ScreenGeometry.PhysicalBounds(this);
        var screen = System.Windows.Forms.Screen.FromPoint(new System.Drawing.Point((int)bounds.Left, (int)bounds.Top)).WorkingArea;
        var scale = VisualTreeHelper.GetDpi(this).DpiScaleX;
        ResizeImage(VisualTreeHelper.GetDpi(this));
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
            if (!_closed) ShowFeedback("图片已复制");
        }
        catch (OperationCanceledException) { }
        catch (Exception) { if (!_closed) ShowFeedback("复制失败，请重试"); }
        finally { _copying = false; }
    }

    private void ShowFeedback(string text)
    {
        _feedback.Content = text; _feedback.IsOpen = true;
        _feedbackTimer.Stop(); _feedbackTimer.Start();
    }

    public static void Shutdown() { foreach (var window in Windows.ToArray()) window.Close(); }
}
