using System.Reflection;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using OverTranslate.Services;
using OverTranslate.Services.Library;
using OverTranslate.Views.Realtime;
using OverTranslate.Views.Settings;
// UseWindowsForms puts System.Windows.Forms in the implicit usings, so these names collide
using Point = System.Windows.Point;
using Size = System.Windows.Size;

namespace OverTranslate.Views.Shell;

public enum ShellPage
{
    Realtime,
    Settings,
    Home,
    About,
    Screenshot
}

/// <summary>
/// The application's single operable window: a left nav rail plus a content area that swaps
/// between pages. Every entry point (tray icon, tray menu, capture toolbar, second launch)
/// funnels through <see cref="ShowOrActivate"/> so only one of these ever exists.
/// </summary>
public partial class ShellWindow : Window
{
    private static ShellWindow? _instance;
    private readonly Func<LibraryState> _readLibrary;
    private readonly Action _openLibrary;
    private bool _summaryRefreshing;
    private bool _summaryClosed;
    private int _summaryVersion;

    /// <summary>
    /// The open shell, or null when it is closed to the tray.
    /// </summary>
    /// <remarks>
    /// Unlike <see cref="ShowOrActivate"/> this never creates one. A shortcut that starts 即時翻譯
    /// needs to hand the shell over to be hidden when it happens to be open, and must not conjure a
    /// window to hide when it is not — the session is about to cover the screen either way.
    /// Reliable because <see cref="OnClosed"/> clears it.
    /// </remarks>
    public static ShellWindow? Current => _instance;

    /// <summary>
    /// The shell's dimensions from the last time it was open, for as long as the process lives.
    /// </summary>
    /// <remarks>
    /// In memory rather than in the settings file, on purpose. Closing this window is the ordinary
    /// way to get it off the screen — the app goes on running in the tray — so reopening it during
    /// the same sitting should give back the window the user had, not the one the app ships with.
    /// Surviving a restart is a different question, and the answer there is the designed default:
    /// centred, at a size picked to fit the content.
    /// </remarks>
    private static Size? _lastSize;

    /// <inheritdoc cref="_lastSize"/>
    private static WindowState _lastWindowState = WindowState.Normal;

    /// <inheritdoc cref="_lastSize"/>
    private static bool _lastRailCollapsed;


    /// <summary>
    /// The page the shell was last showing, so opening it again lands where the user left off
    /// rather than always on 文字翻譯.
    /// </summary>
    /// <inheritdoc cref="_lastSize"/>
    private static ShellPage _lastPage = ShellPage.Home;

    private static readonly Duration ContentDuration   = new(TimeSpan.FromMilliseconds(120));

    // Token for the in-flight content transition; see AnimateContentIn.
    private object? _contentTransition;

    private RealtimePage? _realtimePage;
    private SettingsPage? _settingsPage;
    private HomePage? _homePage;
    private ScreenTranslatePreferencesPage? _preferencesPage;
    private ProductAboutPage? _aboutPage;
    private ScreenshotToolsPage? _screenshotPage;
    private ScreenshotToolsPage ScreenshotPage => _screenshotPage ??= new(
        () => ScreenshotBtn_Click(this, new RoutedEventArgs()),
        () => PinImageBtn_Click(this, new RoutedEventArgs()), ShowQuickToolSettings);
    private RealtimePage RealtimePage => _realtimePage ??= new();
    private readonly Func<LibraryStore> _aiLibraryFactory;
    private ScreenTranslatePreferencesPage PreferencesPage => _preferencesPage ??= new(() => _settingsPage ??= new(), _aiLibraryFactory);

    private ShellPage? _current;
    private static ShellPage _translationMode = ShellPage.Home;
    private bool _syncingNavigation;


    /// <summary>
    /// Shows the shell on <paramref name="page"/>, creating it if needed, and returns the
    /// live instance so callers can push content into a page.
    /// </summary>
    /// <param name="page">
    /// Where to land, or null for wherever the shell was last left — which is what a plain
    /// "open the window" means. Callers with something to say pass the page that says it: the
    /// tray's 設定 item, and the capture flow handing its result to 文字翻譯.
    /// </param>
    public static ShellWindow ShowOrActivate(ShellPage? page = null) => ShowOrActivate(page, quickAccess: false);

    public static ShellWindow ShowOrActivateFromShortcut(ShellPage? page = null) => ShowOrActivate(page, quickAccess: true);

    private static ShellWindow ShowOrActivate(ShellPage? page, bool quickAccess)
    {
        // Read before the window is built: constructing one mounts a page, which writes _lastPage,
        // so asking for it after would only ever get back the page the constructor chose.
        var target = page ?? _lastPage;

        if (_instance == null)
        {
            _instance = new ShellWindow();
        }
        _instance.SetQuickAccess(quickAccess);
        if (_instance.WindowState == WindowState.Minimized)
        {
            _instance.WindowState = WindowState.Normal;
        }
        // OCR 会隐藏而不销毁主窗口；激活前必须先恢复可见性。
        if (!_instance.IsVisible)
            _instance.Show();

        _instance.Navigate(target);
        // 快捷键唤醒直接呈现内容，避免首次打开仍需等待页面过渡。
        if (quickAccess) _instance.ReleaseContentAnimations();
        _instance.RefreshHotkeyHints();
        _instance.RefreshQuickToolAvailability();

        var shell = _instance;
        shell.Dispatcher.BeginInvoke(shell.Activate, DispatcherPriority.ApplicationIdle);
        return shell;
    }

    private void SetQuickAccess(bool quickAccess)
    {
        WindowTitleBar.Visibility = quickAccess ? Visibility.Collapsed : Visibility.Visible;
        WindowRoot.RowDefinitions[0].Height = new GridLength(quickAccess ? 0 : 40);
        System.Windows.Shell.WindowChrome.GetWindowChrome(this).CaptionHeight = quickAccess ? 0 : 40;
        QuickWindowControls.Visibility = quickAccess ? Visibility.Visible : Visibility.Collapsed;
        QuickControlsColumn.Width = new GridLength(quickAccess ? 40 : 0);
    }

    private void QuickDrag_MouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2) MaximizeBtn_Click(sender, e);
        else if (e.LeftButton == System.Windows.Input.MouseButtonState.Pressed) DragMove();
    }

    public ShellWindow() : this(() => new LibraryStore(LibraryStore.DefaultPath).Read(), Library.LibraryWindow.Open) { }

    internal ShellWindow(Func<LibraryState> readLibrary, Action openLibrary, Func<LibraryStore>? aiLibraryFactory = null)
    {
        _readLibrary = readLibrary;
        _openLibrary = openLibrary;
        _aiLibraryFactory = aiLibraryFactory ?? (() => new LibraryStore(LibraryStore.DefaultPath));
        InitializeComponent();
        RefreshUpdateButton();
        UpdateNotifier.AvailabilityChanged += OnUpdateAvailabilityChanged;
        Closed += (_, _) => UpdateNotifier.AvailabilityChanged -= OnUpdateAvailabilityChanged;

        var compactIcon = AppIconService.CreateCompactIcon();
        Icon = compactIcon;
        VersionText.Text = $"v{UpdateService.InstalledVersion()}";

        _instance = this;
        RefreshHotkeyHints();
        RefreshLibrarySummary();
        // 回到主窗口时重读磁盘，让资料增删及外部迁入立即反映在统计中，不轮询文件。
        Activated += (_, _) => { RefreshHotkeyHints(); RefreshLibrarySummary(); };

        // Subscribed once here rather than per open, so the handler is not stacked up by a user who
        // opens the service panel more than once.

        // Subscribed rather than refreshed on show: a session ending brings this window back with
        // Show(), not through ShowOrActivate, so nothing else would clear the disabled state and
        // the button would stay greyed out for as long as the shell stayed open.
        Realtime.RealtimeSessionController.Instance.StateChanged += OnRealtimeStateChanged;
        RefreshQuickToolAvailability();

        // Compose shortcut hints again when the interface language changes.
        LocalizationService.LanguageChanged += OnLanguageChanged;
        Closed += (_, _) => LocalizationService.LanguageChanged -= OnLanguageChanged;

        // The system title bar is gone, so what it used to do for itself is done here: maximising
        // to the work area rather than over it, rounding the window and colouring its outer edge,
        // and saying which of maximise or restore this window is currently offering.
        WindowFrame.Attach(this);
        ThemeService.Changed += OnThemeChanged;
        Closed += (_, _) => ThemeService.Changed -= OnThemeChanged;
        StateChanged += (_, _) => RefreshMaximizeButton();
        RefreshMaximizeButton();

        RestoreSessionLayout();

        Navigate(_translationMode);
    }

    /// <inheritdoc cref="_lastSize"/>
    private void RestoreSessionLayout()
    {
        if (_lastSize is { } size)
        {
            Width  = size.Width;
            Height = size.Height;
        }

        if (_lastWindowState == WindowState.Maximized) WindowState = WindowState.Maximized;

        _railCollapsed = _lastRailCollapsed;
        SetRailWidth(_railCollapsed ? 0 : RailWidth);
        RefreshSidebarToggle();


    }

    // The window's outer edge is the compositor's, not this window's, so it is the one colour in
    // the application that has to be handed over again rather than re-resolved.
    private void OnThemeChanged(object? sender, EventArgs e) => WindowFrame.ApplyAppearance(this);

    private void OnLanguageChanged(object? sender, EventArgs e)
    {
        RefreshQuickToolAvailability();
        RefreshSidebarToggle();

    }

    /// <summary>
    /// Re-reads the shortcuts into the 快速工具 rows' trailing hints. Called on construction and on
    /// every ShowOrActivate, and directly by <see cref="Settings.SettingsPage"/> the moment a new
    /// shortcut is recorded — the rail is visible the whole time the user is on 設定, so waiting
    /// for the next navigation would leave the wrong shortcut on screen next to the button.
    /// </summary>
    /// <summary>Puts the 即時翻譯 shortcut's refusal on the realtime page — see MainWindow.</summary>
    public void ShowRealtimeShortcutRefusal(string key) => RealtimePage.ShowShortcutRefusal(key);

    public void RefreshHotkeyHints()
    {
        var settings = SettingsService.Instance.Current;

        // Blank rather than a "未設定" placeholder: the label already says what the button does,
        // and a slot that only ever holds a shortcut should be empty when there is none.
        _homePage?.Reload();
        var bindings = HotkeyBindings.Resolve(settings);
        foreach (var button in QuickToolsPanel.Children.OfType<System.Windows.Controls.Button>())
            button.Visibility = bindings.Any(b => b.Action.ToString() == (string)button.Tag && b.Enabled)
                ? Visibility.Visible : Visibility.Collapsed;
        string Hint(HotkeyAction action, string display) => bindings.Any(b => b.Action == action && b.IsActive)
            && !(System.Windows.Application.Current?.MainWindow is MainWindow owner && owner.HotkeyRegistrationFailures.Contains(action)) ? display : "";
        OcrHotkeyText.Text = Hint(HotkeyAction.OcrExtract, settings.OcrHotkeyDisplay);
        LibraryHotkeyText.Text = Hint(HotkeyAction.LibraryWindow, settings.LibraryHotkeyDisplay);
        ClipboardHotkeyText.Text = Hint(HotkeyAction.ClipboardHistory, settings.ClipboardHotkeyDisplay);
        ScreenshotHotkeyText.Text = Hint(HotkeyAction.Screenshot, settings.ScreenshotHotkeyDisplay);
        PinImageHotkeyText.Text = Hint(HotkeyAction.PinImage, settings.PinImageHotkeyDisplay);
        CaptureHotkeyText.Text = Hint(HotkeyAction.Capture, settings.HotkeyDisplay);
        RealtimeHotkeyText.Text = Hint(HotkeyAction.RealtimePause, settings.RealtimePauseHotkeyDisplay);
        WindowHotkeyText.Text = Hint(HotkeyAction.TranslationWindow, settings.TranslationWindowHotkeyDisplay);
        QuickLibraryHotkeyText.Text = LibraryHotkeyText.Text;
        QuickClipboardHotkeyText.Text = ClipboardHotkeyText.Text;
        RefreshQuickToolAvailability();
    }

    internal async void RefreshLibrarySummary()
    {
        ++_summaryVersion;
        if (_summaryRefreshing || _summaryClosed) return;
        _summaryRefreshing = true;
        try
        {
            while (!_summaryClosed)
            {
                var version = _summaryVersion;
                string text;
                try
                {
                    text = await Task.Run(() =>
                    {
                        var state = _readLibrary();
                        return $"提示词 {state.Prompts.Count} · 连接 {state.Connections.Count}";
                    });
                }
                catch (Exception ex) when (ex is System.IO.IOException or System.IO.InvalidDataException or UnauthorizedAccessException or System.Text.Json.JsonException or InvalidOperationException)
                {
                    text = "资料暂不可用，请打开检查";
                }
                if (_summaryClosed) return;
                if (version != _summaryVersion) continue;
                LibrarySummaryText.Text = text;
                return;
            }
        }
        finally { _summaryRefreshing = false; }
    }

    private void LibraryBtn_Click(object sender, RoutedEventArgs e) => _openLibrary();

    private void Devices_Click(object sender, RoutedEventArgs e) => ClipboardHistory.LanTransferWindow.Open();

    private void ClipboardHistoryBtn_Click(object sender, RoutedEventArgs e) => ClipboardHistory.ClipboardHistoryWindow.Open();

    private void CaptureTranslateBtn_Click(object sender, RoutedEventArgs e)
    {
        if (System.Windows.Application.Current?.MainWindow is MainWindow main) main.StartCaptureFromShell(this);
    }

    private void RealtimeQuickBtn_Click(object sender, RoutedEventArgs e)
    {
        if (System.Windows.Application.Current?.MainWindow is MainWindow main) main.OnRealtimeShortcut(this);
    }

    private void MainWindowBtn_Click(object sender, RoutedEventArgs e) => ShowOrActivate(_translationMode);

    private void ScreenshotBtn_Click(object sender, RoutedEventArgs e)
    {
        if (System.Windows.Application.Current?.MainWindow is MainWindow main) main.StartScreenshotFromShell(this);
    }

    private void PinImageBtn_Click(object sender, RoutedEventArgs e)
    {
        if (System.Windows.Application.Current?.MainWindow is MainWindow main) main.PinClipboardImage();
    }

    private void OnRealtimeStateChanged(object? sender, EventArgs e) =>
        Dispatcher.BeginInvoke(RefreshQuickToolAvailability);

    public void RefreshQuickToolAvailability()
    {
        var controller = Realtime.RealtimeSessionController.Instance;
        var running = controller.IsActive;
        var framing = running && !controller.IsTranslating;

        _homePage?.RefreshAvailability();
        CaptureTranslateBtn.IsEnabled = !framing;
        ScreenshotBtn.IsEnabled = !framing;
        var blocked = LocalizationService.Get("S.Shell.CaptureBlockedByRealtimeFraming");
        CaptureTranslateBtn.ToolTip = framing ? blocked : WithShortcut("框选屏幕区域并翻译", CaptureHotkeyText.Text);
        RealtimeQuickBtn.ToolTip = WithShortcut("持续翻译 / 暂停 / 恢复", RealtimeHotkeyText.Text);
        MainWindowBtn.ToolTip = WithShortcut("打开主窗口的翻译页面", WindowHotkeyText.Text);
        QuickLibraryBtn.ToolTip = WithShortcut("打开提示词与模型资料独立窗口", QuickLibraryHotkeyText.Text);
        QuickClipboardBtn.ToolTip = WithShortcut("打开剪贴板历史浮窗", QuickClipboardHotkeyText.Text);
        ScreenshotBtn.ToolTip = framing ? blocked : WithShortcut("框选、标注、取色与贴图", ScreenshotHotkeyText.Text);
        OcrExtractBtn.IsEnabled = !framing;
        OcrExtractBtn.ToolTip = framing ? blocked : WithShortcut("框选后提取文字并复制", OcrHotkeyText.Text);
        PinImageBtn.ToolTip = WithShortcut("将剪贴板图片贴到桌面", PinImageHotkeyText.Text);
        _screenshotPage?.Reload(ScreenshotHotkeyText.Text, PinImageHotkeyText.Text, framing);

        static string WithShortcut(string description, string shortcut) =>
            string.IsNullOrWhiteSpace(shortcut) ? description : $"{description}\n{shortcut}";
    }

    private void MinimizeBtn_Click(object sender, RoutedEventArgs e) =>
        WindowState = WindowState.Minimized;

    private void MaximizeBtn_Click(object sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized
            ? WindowState.Normal
            : WindowState.Maximized;

    private void CloseBtn_Click(object sender, RoutedEventArgs e) => Close();

    /// <summary>
    /// Puts the right one of maximise and restore on the middle caption button.
    /// </summary>
    /// <remarks>
    /// Driven from StateChanged rather than only from the button's own click: double-clicking the
    /// title bar and Aero Snap both maximise this window without going anywhere near it.
    /// </remarks>
    private void RefreshMaximizeButton() =>
        MaximizeBtn.Content = WindowState == WindowState.Maximized ? "" : "";

    /// <summary>The rail's width whenever it is showing.</summary>
    /// <remarks>
    /// One width rather than a range. The rail holds a fixed set of rows whose widest line is a
    /// 快速工具 label and its shortcut, so there is one width that fits them — every other width is
    /// either wasting the page's or trimming the rail's own labels. What a user dragging the rail
    /// narrow actually wants is the width back, and the toggle in the title bar gives them all of
    /// it in one press.
    /// </remarks>
    private const double RailWidth = 216;

    private static readonly Duration RailDuration = new(TimeSpan.FromMilliseconds(320));

    /// <summary>
    /// The rail column's width, as something that can be animated.
    /// </summary>
    /// <remarks>
    /// GridLength is not a double and WPF ships no animation for it, so a column driven directly
    /// can only jump. Animating a plain double here and writing the column from it is what lets the
    /// rail settle into place instead.
    /// </remarks>
    private static readonly DependencyProperty RailWidthProperty = DependencyProperty.Register(
        "RailWidth", typeof(double), typeof(ShellWindow),
        new PropertyMetadata(0.0, (d, e) => ((ShellWindow)d).ApplyRailWidth((double)e.NewValue)));

    /// <summary>
    /// The rail's width as it has just been written, animation included.
    /// </summary>
    /// <remarks>
    /// Not ActualWidth: the column's own Width is what the animation writes, and it is read here
    /// before the layout pass that would make ActualWidth agree — so a toggle pressed mid-slide
    /// would otherwise carry on from the width the rail had a frame ago and jump.
    /// </remarks>
    private double CurrentRailWidth => SidebarColumn.Width.IsAbsolute
        ? SidebarColumn.Width.Value
        : SidebarColumn.ActualWidth;

    private void ApplyRailWidth(double width)
    {
        SidebarColumn.Width = new GridLength(Math.Max(0, width));

        // Only once there is nothing left to draw. The rail is clipped to its column, so the
        // collapse reads as the card being wiped off the edge rather than as it shrinking — and a
        // panel left visible at zero width would still take a hit test in the page's first pixel.
        RailPanel.Visibility = width < 1
            ? Visibility.Collapsed
            : Visibility.Visible;
    }

    /// <summary>Puts the rail at <paramref name="width"/> at once, for a layout being restored.</summary>
    private void SetRailWidth(double width)
    {
        BeginAnimation(RailWidthProperty, null);
        ApplyRailWidth(width);
    }

    private void AnimateRailTo(double width)
    {
        var from = CurrentRailWidth;
        if (Math.Abs(from - width) < 0.5)
        {
            SetRailWidth(width);
            return;
        }

        // From wherever the rail is on screen right now, not from where the animation in flight was
        // headed: pressing the button again mid-slide reverses it instead of finishing first.
        // Critically damped — eased out with no overshoot. The rail is a panel being put somewhere,
        // not something thrown, and a bounce here would read as it having missed.
        BeginAnimation(RailWidthProperty, new DoubleAnimation
        {
            From = from,
            To = width,
            Duration = RailDuration,
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        });
    }

    /// <summary>Whether the rail is currently put away.</summary>
    /// <remarks>
    /// Held here rather than read back off the column, so a press that lands while the slide is
    /// still running toggles what the rail is going to be rather than whatever width it happens to
    /// be passing through at that instant.
    /// </remarks>
    private bool _railCollapsed;

    private void SidebarToggleBtn_Click(object sender, RoutedEventArgs e)
    {
        _railCollapsed = !_railCollapsed;
        AnimateRailTo(_railCollapsed ? 0 : RailWidth);
        // Now, rather than when the slide lands: the button already offers the opposite of what it
        // just did, and a tooltip that only caught up 320ms later would be wrong for the whole of
        // the one moment the pointer is still sitting on it.
        RefreshSidebarToggle();
    }

    /// <summary>
    /// Says what pressing the toggle would do next, in the language the window is currently in.
    /// </summary>
    /// <remarks>
    /// The glyph does not change with the state — it names the rail rather than a direction, the
    /// way Windows' own pane toggle does — so this text is the only place that direction is stated.
    /// It is the accessible name as well as the tooltip: a glyph-only button has nothing else for a
    /// screen reader to read out.
    /// </remarks>
    private void RefreshSidebarToggle()
    {
        var label = LocalizationService.Get(_railCollapsed
            ? "S.Shell.ShowSidebar"
            : "S.Shell.HideSidebar");

        SidebarToggleBtn.ToolTip = label;
        AutomationProperties.SetName(SidebarToggleBtn, label);
        QuickSidebarToggle.ToolTip = label;
        AutomationProperties.SetName(QuickSidebarToggle, label);
    }


    private void OcrExtractBtn_Click(object sender, RoutedEventArgs e)
    {
        if (System.Windows.Application.Current.MainWindow is MainWindow main)
            main.StartOcrFromShell();
    }

    public void Navigate(ShellPage page)
    {
        if (_current != page) ShowPage(page);
    }

    private void TranslationNav_Checked(object sender, RoutedEventArgs e)
    {
        if (!_syncingNavigation) Navigate(_translationMode);
    }

    private void Nav_Checked(object sender, RoutedEventArgs e)
    {
        if (_syncingNavigation) return;
        var page = ReferenceEquals(sender, SettingsNav) ? ShellPage.Settings :
            ReferenceEquals(sender, AboutNav) ? ShellPage.About :
            ReferenceEquals(sender, ScreenshotNav) ? ShellPage.Screenshot :
            ReferenceEquals(sender, RealtimeNav) ? ShellPage.Realtime : ShellPage.Home;
        Navigate(page);
    }

    private void ShowPage(ShellPage page)
    {
        _current = page;
        _lastPage = page;
        var translating = page is ShellPage.Home or ShellPage.Realtime;
        if (translating) _translationMode = page;
        // 侧栏与页内模式属于两个选择组；同步状态时不再次触发导航或重载表单。
        _syncingNavigation = true;
        try
        {
            TranslationNav.IsChecked = translating;
            SettingsNav.IsChecked = page == ShellPage.Settings;
            AboutNav.IsChecked = page == ShellPage.About;
            ScreenshotNav.IsChecked = page == ShellPage.Screenshot;
            HomeNav.IsChecked = _translationMode == ShellPage.Home;
            RealtimeNav.IsChecked = _translationMode == ShellPage.Realtime;
            TranslationHeader.Visibility = translating ? Visibility.Visible : Visibility.Collapsed;
        }
        finally { _syncingNavigation = false; }

        UIElement content;
        switch (page)
        {
            case ShellPage.Realtime:
                RealtimePage.Reload(); content = RealtimePage; break;
            case ShellPage.Settings:
                PreferencesPage.Reload(); content = PreferencesPage; break;
            case ShellPage.Screenshot:
                ScreenshotPage.Reload(ScreenshotHotkeyText.Text, PinImageHotkeyText.Text,
                    RealtimeSessionController.Instance.IsActive && !RealtimeSessionController.Instance.IsTranslating);
                content = ScreenshotPage; break;
            case ShellPage.About:
                content = _aboutPage ??= new(); break;
            default:
                _homePage ??= new(); _homePage.Reload(); content = _homePage; break;
        }
        ContentHost.Child = content;
        AnimateContentIn();
    }

    private void AnimateContentIn()
    {
        // Identifies this particular transition, so a fast page switch cannot have the previous
        // animation's completion handler tear down the animation that replaced it.
        var transition = new object();
        _contentTransition = transition;

        // WPF switches text off pixel snapping as soon as it detects the text is being animated or
        // scrolled, then ramps snapping back on over roughly a second once the motion stops. That
        // ramp is why the page used to sit visibly settled and stay soft for a beat before turning
        // sharp, and there is no API to shorten or disable it. Rendering the page into a bitmap
        // cache for the duration of the slide sidesteps the whole mechanism: the glyphs are
        // rasterised once as static, snapped text, and the render thread then only moves the
        // finished bitmap, so nothing ever looks like animating text to the detector.
        // SnapsToDevicePixels keeps that bitmap on whole pixels as it moves, so no frame of the
        // slide is resampled either. The cache is dropped again in ReleaseContentAnimations.
        ContentHost.CacheMode = new BitmapCache { SnapsToDevicePixels = true };

        // Both animations run for the same duration, so one Completed handler covers the pair.
        var fade = new DoubleAnimation { From = 0, To = 1, Duration = ContentDuration };
        fade.Completed += (_, _) =>
        {
            if (!ReferenceEquals(_contentTransition, transition)) return;
            ReleaseContentAnimations();
        };

        ContentHost.BeginAnimation(OpacityProperty, fade);
        ContentTransform.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation
        {
            From = 8, To = 0,
            Duration = ContentDuration,
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        });
    }

    // DoubleAnimation defaults to FillBehavior.HoldEnd, which keeps the animated properties under
    // the animation clock's control even after the animation has visually finished, holding the
    // content in an intermediate composition layer indefinitely. Handing the properties back to the
    // elements drops that layer the moment the transition ends, so the settled page renders exactly
    // as it would have if it had never animated.
    private void ReleaseContentAnimations()
    {
        ContentHost.BeginAnimation(OpacityProperty, null);
        ContentHost.Opacity = 1;

        ContentTransform.BeginAnimation(TranslateTransform.YProperty, null);
        ContentTransform.Y = 0;

        // Back to rendering the live visual tree — the cache existed only for the slide, and the
        // settled page has to be real text again for selection, scrolling and DPI changes.
        ContentHost.CacheMode = null;
    }

    private void OnUpdateAvailabilityChanged(object? sender, EventArgs e) => RefreshUpdateButton();
    private void RefreshUpdateButton()
    {
        UpdateButton.Visibility = UpdateNotifier.Available is null ? Visibility.Collapsed : Visibility.Visible;
        UpdateButton.Content = UpdateNotifier.Available?.VelopackInfo.IsDowngrade == true ? "同步正式版" : "安装新版";
        UpdateButton.ToolTip = UpdateNotifier.Available is not null
            ? $"{UpdateUi.ButtonLabel}，下载并安装，个人数据保留" : "在关于页面检查更新";
        UpdateButton.FontWeight = UpdateNotifier.Available is null ? FontWeights.Normal : FontWeights.SemiBold;
    }
    private async void UpdateButton_Click(object sender, RoutedEventArgs e)
    {
        UpdateButton.IsEnabled = false;
        try { await UpdateUi.CheckAndShowAsync(); }
        finally { UpdateButton.IsEnabled = true; RefreshUpdateButton(); }
    }

    public void ShowQuickToolSettings()
    {
        Navigate(ShellPage.Settings);
        PreferencesPage.ShowQuickTools();
    }

    private void SettingsButton_Click(object sender, RoutedEventArgs e) => ShowAiSettings();

    public void ShowAiSettings()
    {
        Navigate(ShellPage.Settings);
        PreferencesPage.ShowAiSettings();
    }

    public void ShowOcrSettings()
    {
        Navigate(ShellPage.Settings);
        PreferencesPage.ShowOcr();
    }

    /// <summary>
    /// Opens the panel holding what one translation service has to be told, over the whole window.
    /// </summary>
    /// <remarks>
    /// Hosted here rather than inside the settings page so the scrim covers the nav rail too. The
    /// page is told when it closes because what was typed in there is what its service tiles report.
    /// </remarks>
    public void OpenServiceSettings(Models.TranslationProvider provider)
        => ServiceSettings.Open(provider);

    /// <summary>
    /// Opens the panel that collects what the user wants to say about the problem and sends the
    /// bundle with it.
    /// </summary>
    /// <remarks>
    /// Hosted here for the same reason the service panel is: the scrim has to cover the nav rail,
    /// and an upload in flight is exactly the moment the user must not be able to navigate away
    /// from the thing that will tell them how it went.
    /// </remarks>
    public void OpenDiagnosticNote(string bundlePath)
        => DiagnosticNote.Open(bundlePath);

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        // RestoreBounds rather than the live size: a maximised window's actual size is the screen's,
        // and handing that back as a normal-state size would reopen a window that fills the display
        // without being maximised — and with no way to shrink it back short of dragging.
        var bounds = WindowState == WindowState.Normal
            ? new Rect(Left, Top, Width, Height)
            : RestoreBounds;

        _lastSize = new Size(bounds.Width, bounds.Height);
        // Minimised is a state to reopen out of, not into.
        _lastWindowState = WindowState == WindowState.Minimized ? WindowState.Normal : WindowState;
        _lastRailCollapsed = _railCollapsed;


        base.OnClosing(e);
    }

    protected override void OnClosed(EventArgs e)
    {
        _summaryClosed = true;
        // The window is destroyed on close (not hidden), so the pages' timers, TTS playback
        // and HTTP handles have to go with it.

        // Only detaches the page from the session controller: a realtime session already running is
        // on the screen, not in this window, and closing the shell is not a request to end it.
        _realtimePage?.Teardown();
        _homePage?.Teardown();
        _preferencesPage?.Teardown();
        Realtime.RealtimeSessionController.Instance.StateChanged -= OnRealtimeStateChanged;
        _instance = null;
        base.OnClosed(e);
    }
}
