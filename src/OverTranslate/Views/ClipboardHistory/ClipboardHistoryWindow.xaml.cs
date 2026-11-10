using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using OverTranslate.Services;
using OverTranslate.Services.ClipboardHistory;
using OverTranslate.Views.Shell;
using Windows.ApplicationModel.DataTransfer;
using Button = System.Windows.Controls.Button;
using TextBox = System.Windows.Controls.TextBox;
using ToolTip = System.Windows.Controls.ToolTip;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using OverTranslate.Views.Controls;
using ButtonBase = System.Windows.Controls.Primitives.ButtonBase;
using ScrollBar = System.Windows.Controls.Primitives.ScrollBar;
using Brushes = System.Windows.Media.Brushes;

namespace OverTranslate.Views.ClipboardHistory;

internal sealed class ClipboardRecordView(ClipboardRecord record, ClipboardHistoryStore store) : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    private static readonly SemaphoreSlim ImageReads = new(2);
    private static readonly string[] ImageExtensions = [".png", ".jpg", ".jpeg", ".bmp", ".gif", ".tif", ".tiff", ".webp"];
    public ClipboardRecord Record { get; } = record;
    public bool HasFiles => Record.Files.Length > 0;
    public string DeleteHint => HasFiles ? "移除历史记录，保留源文件" : "彻底删除此记录及保存的内容";
    public string FileKind { get; private set; } = record.Category;
    public bool HasFolders { get; private set; }
    public string FolderLabel { get; private set; } = "文件夹";
    public string FilePaths => string.Join("\n", Record.Files);
    public string TimeLabel => Record.TimeLabel;
    internal void RefreshTime() => PropertyChanged?.Invoke(this, new(nameof(TimeLabel)));
    public string Preview
    {
        get { if (Record.HasImage && Record.Text.Length == 0) return ""; var text = Record.DisplayText; return text[..Math.Min(600, text.Length)]; }
    }
    public string Glyph => HasFolders ? "\uE8B7" : "";
    private WeakReference<ImageSource>? _thumbnail;
    private bool _imageRead;
    public ImageSource? Thumbnail
    {
        get
        {
            if (_thumbnail is not null && _thumbnail.TryGetTarget(out var image)) return image;
            if (!_imageRead || _thumbnail is not null)
            {
                _imageRead = true;
                IsThumbnailLoading = true;
                _thumbnail = null;
                _ = LoadThumbnailAsync();
            }
            return null;
        }
    }

    internal bool IsThumbnailLoading { get; private set; }
    private async Task LoadThumbnailAsync()
    {
        // 绑定只发起加载；磁盘访问和解码不占用界面线程，并限制并行解码数。
        var loading = Task.Run(async () =>
        {
            await ImageReads.WaitAsync().ConfigureAwait(false);
            try
            {
                var folders = Record.Files.Count(Directory.Exists);
                HasFolders = folders > 0;
                FolderLabel = folders > 1 ? $"{folders} 个文件夹" : "文件夹";
                if (HasFolders && folders == Record.Files.Length) FileKind = "文件夹";
                else if (Record.Files.Length == 1)
                {
                    var path = Record.Files[0];
                    if (File.Exists(path))
                    {
                        var extension = Path.GetExtension(path).ToLowerInvariant();
                        FileKind = ClipboardRecord.IsImageFile(path) ? "图片文件"
                            : extension is ".txt" or ".md" or ".log" or ".csv" ? "文本文件" : "文件";
                    }
                }
                return LoadImage(true);
            }
            catch (Exception) { return null; }
            finally { ImageReads.Release(); }
        });
        var image = await loading;
        // 可见控件持有强引用；滚出视口的缩略图允许 GC 回收，避免浏览历史后常驻全部图片。
        _thumbnail = image is null ? null : new(image);
        IsThumbnailLoading = false;
        PropertyChanged?.Invoke(this, new(nameof(FileKind)));
        PropertyChanged?.Invoke(this, new(nameof(HasFolders)));
        PropertyChanged?.Invoke(this, new(nameof(FolderLabel)));
        PropertyChanged?.Invoke(this, new(nameof(Glyph)));
        PropertyChanged?.Invoke(this, new(nameof(Thumbnail)));
    }

    internal ImageSource? LoadImage(bool thumbnail)
    {
        if (Record.HasImage) return ClipboardContent.DecodeImage(store.ReadImage(Record.Id), thumbnail);
        var path = Record.Files.FirstOrDefault();
        if (path is null || !Path.IsPathFullyQualified(path) || path.StartsWith(@"\\")
            || !ImageExtensions.Contains(Path.GetExtension(path).ToLowerInvariant())) return null;
        // 文件记录仍是引用；按需读取且立即释放句柄，不改变原文件或复制格式。
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (stream.Length > 64 * 1024 * 1024) return null;
        var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);
        var frame = decoder.Frames[0];
        if ((long)frame.PixelWidth * frame.PixelHeight > 32 * 1024 * 1024) return null;
        stream.Position = 0;
        var image = new BitmapImage(); image.BeginInit(); image.CacheOption = BitmapCacheOption.OnLoad;
        var limit = thumbnail ? 340 : 1000;
        if (frame.PixelWidth >= frame.PixelHeight) image.DecodePixelWidth = Math.Min(limit, frame.PixelWidth);
        else image.DecodePixelHeight = Math.Min(limit, frame.PixelHeight);
        image.StreamSource = stream; image.EndInit(); image.Freeze(); return image;
    }
}

public partial class ClipboardHistoryWindow : Window
{
    private static ClipboardHistoryWindow? _current;
    private static bool _opening;
    private static int _openVersion;
    private readonly ClipboardHistoryStore _store;
    private readonly Func<ClipboardRecord, Task> _copy;
    private readonly Func<OriginalApplicationPaste.Target, Task<bool>> _paste;
    private readonly OverTranslate.Models.AppSettings _settings;
    private readonly Func<bool> _saveSettings;
    private OriginalApplicationPaste.Target _pasteTarget;
    private readonly ISystemClipboardHistory _systemHistory;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly DispatcherTimer _timeTimer = new() { Interval = TimeSpan.FromMinutes(1) };
    private string? _revealCopiedId;
    private int _previewVersion;
    private bool _filterPending;
    private bool _refreshing;
    private int _refreshVersion;
    private int _changeQueued;
    private IReadOnlyList<ClipboardRecord>? _shownSnapshot;
    private string _shownCategory = "", _shownQuery = "";
    private Dictionary<string, ClipboardRecordView> _views = new();
    internal bool IsRefreshing => _refreshing;
    private bool _closed;
    private bool _shutdown;
    private bool _busy;
    private int _copyVersion;
    private bool _importing;
    private bool _rememberPlacement;
    private readonly bool _managePosition;
    private System.Windows.Interop.HwndSource? _placementSource;
    internal bool WindowPinned { get; private set; }
    internal bool IsBusy => _busy;
    internal ClipboardHistoryStore Store => _store;
    internal ClipboardRecord? SelectedRecord => (HistoryList.SelectedItem as ClipboardRecordView)?.Record;

    internal ClipboardHistoryWindow(ClipboardHistoryStore store, Func<ClipboardRecord, Task>? copy = null,
        Func<Task<bool>>? paste = null, ISystemClipboardHistory? systemHistory = null, bool managePosition = false,
        OverTranslate.Models.AppSettings? settings = null, Func<bool>? saveSettings = null,
        OverTranslate.Services.Lan.SyncActivityStore? syncActivities = null)
    {
        _store = store;
        _copy = copy ?? (record => ClipboardContent.CopyAsync(record, store));
        _paste = paste is null ? target => OriginalApplicationPaste.PasteAsync(this, target) : _ => paste();
        _settings = settings ?? SettingsService.Instance.Current;
        _saveSettings = saveSettings ?? SettingsService.Instance.TrySave;
        _systemHistory = systemHistory ?? new SystemClipboardHistory();
        _managePosition = managePosition;
        InitializeComponent();
        InitializeSyncFeedback(syncActivities ?? OverTranslate.Services.Lan.SyncActivityStore.Default);
        RefreshBehaviorSettings();
        SourceInitialized += (_, _) =>
        {
            if (!_managePosition) return;
            _placementSource = System.Windows.Interop.HwndSource.FromHwnd(new System.Windows.Interop.WindowInteropHelper(this).Handle);
            _placementSource?.AddHook(PlacementMessage);
        };
        Icon = AppIconService.CreateCompactIcon();
        _store.Changed += Store_Changed;
        _timeTimer.Tick += (_, _) => RefreshVisibleTimes(HistoryList);
        Loaded += (_, _) => SearchBox.Focus();
        IsVisibleChanged += (_, _) =>
        {
            if (IsVisible) { _timeTimer.Start(); RefreshVisibleTimes(HistoryList); RefreshViews(); }
            else { ++_previewVersion; _timeTimer.Stop(); PreviewPanel.Visibility = Visibility.Collapsed; PreviewImage.Source = null; PreviewText.Clear(); }
        };
        Activated += (_, _) =>
        {
            _pasteTarget = OriginalApplicationPaste.CaptureTarget();
            if (ClipboardHistoryRuntime.LastError is { } error) StatusText.Text = error;
        };
        Deactivated += (_, _) => ScheduleDismiss();
        MenuButton.ContextMenu.Closed += (_, _) => ScheduleDismiss();
        HistoryList.ContextMenu.Closed += (_, _) => ScheduleDismiss();
        Closing += Window_Closing;
        Closed += (_, _) =>
        {
            _closed = true; _lifetime.Cancel(); _timeTimer.Stop(); _store.Changed -= Store_Changed;
            _placementSource?.RemoveHook(PlacementMessage); _placementSource = null;
            HistoryList.ItemsSource = null; PreviewImage.Source = null; PreviewText.Clear(); _lifetime.Dispose();
            if (ReferenceEquals(_current, this)) _current = null;
        };
        StatusText.Text = store.UnreadableCount > 0 ? $"{store.UnreadableCount} 条记录无法读取，原文件已保留。" : ClipboardHistoryRuntime.LastError ?? "";
        RefreshViews();
    }

    public static async void Open()
    {
        var version = ++_openVersion; _opening = true;
        try
        {
            var store = await ClipboardHistoryRuntime.StoreAsync;
            if (version != _openVersion) return;
            _current ??= new ClipboardHistoryWindow(store, managePosition: true);
            _current.ShowPanel();
        }
        catch (Exception) { AppMessageWindow.ShowMessage("剪贴板历史暂时无法打开，请检查数据目录权限与磁盘空间。", "剪贴板", MessageBoxButton.OK, MessageBoxImage.Warning); }
        finally { if (version == _openVersion) _opening = false; }
    }

    public static void Toggle()
    {
        if (_current is { IsVisible: true }) _current.ToggleVisibility();
        else if (_opening) { _opening = false; ++_openVersion; }
        else Open();
    }

    internal void ShowPanel()
    {
        _pasteTarget = OriginalApplicationPaste.CaptureTarget();
        RefreshBehaviorSettings();
        SearchBox.Clear(); TypeFilter.SelectedIndex = 0;
        if (!IsVisible) Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        if (_managePosition) PositionWindow();
        Activate(); SearchBox.Focus();
    }

    internal void ToggleVisibility()
    {
        if (IsVisible) Dismiss(); else ShowPanel();
    }

    internal void Dismiss()
    {
        if (_closed) return;
        if (_managePosition) SavePlacement();
        Hide();
    }

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (!_shutdown && _managePosition) { e.Cancel = true; Dismiss(); }
    }

    public static void Shutdown()
    {
        ++_openVersion; _opening = false;
        if (_current is not { } window) return;
        window._shutdown = true;
        window.SavePlacement(); window.Close();
    }

    private void Store_Changed(object? sender, EventArgs e)
    {
        if (_closed || Dispatcher.HasShutdownStarted) return;
        if (Interlocked.Exchange(ref _changeQueued, 1) != 0) return;
        Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
        {
            Interlocked.Exchange(ref _changeQueued, 0);
            if (!_closed && IsVisible) RefreshViews();
        });
    }

    private void ScheduleDismiss() => Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
    {
        if (!_closed && IsVisible && !IsActive && !WindowPinned && !_busy
            && !MenuButton.ContextMenu.IsOpen && !HistoryList.ContextMenu.IsOpen) Dismiss();
    });

    private static void RefreshVisibleTimes(DependencyObject parent)
    {
        // 只通知已实现的卡片，避免每次唤起或计时都遍历全部历史。
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is ListBoxItem { DataContext: ClipboardRecordView view }) view.RefreshTime();
            else RefreshVisibleTimes(child);
        }
    }

    internal void RefreshViews()
    {
        if (_closed || HistoryList is null || TypeFilter is null || SearchBox is null || EmptyState is null) return;
        SearchPlaceholder.Visibility = SearchBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        ++_refreshVersion;
        if (!_refreshing) _ = RefreshLoopAsync();
    }

    private async Task RefreshLoopAsync()
    {
        _refreshing = true;
        try
        {
            while (!_closed)
            {
                var version = _refreshVersion;
                // 合并一轮唤起事件、连续复制通知及快速输入；旧查询不覆盖新条件。
                await Task.Delay(100, _lifetime.Token);
                if (version != _refreshVersion) continue;
                var category = (TypeFilter.SelectedItem as ListBoxItem)?.Content as string ?? "全部";
                var query = SearchBox.Text.Trim();
                var previous = _views;
                var shown = _shownSnapshot;
                var sameFilter = category == _shownCategory && query == _shownQuery;
                var result = await Task.Run(() =>
                {
                    var all = _store.Read();
                    if (ReferenceEquals(all, shown) && sameFilter) return (all, items: (List<ClipboardRecordView>?)null);
                    var items = new List<ClipboardRecordView>();
                    foreach (var record in all)
                    {
                        if (_lifetime.IsCancellationRequested || version != Volatile.Read(ref _refreshVersion)) break;
                        if (record.Matches(category, query))
                            items.Add(previous.TryGetValue(record.Id, out var view) && ReferenceEquals(view.Record, record)
                                ? view : new ClipboardRecordView(record, _store));
                    }
                    return (all, items);
                });
                if (_closed) return;
                if (version != _refreshVersion) continue;
                if (result.items is { } items)
                {
                    var id = SelectedRecord?.Id;
                    _views = items.ToDictionary(v => v.Record.Id);
                    HistoryList.ItemsSource = items;
                    HistoryList.SelectedItem = items.FirstOrDefault(r => r.Record.Id == id) ?? items.FirstOrDefault();
                    EmptyState.Visibility = items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
                    EmptyTitle.Text = result.all.Count == 0 ? "还没有剪贴板记录" : "没有匹配的记录";
                    EmptyHint.Text = result.all.Count == 0 ? "复制内容后会自动记录，也可在设置菜单导入系统历史" : "试试其他关键词或切换分类";
                    CountText.Text = items.Count == result.all.Count ? $"{result.all.Count} 条记录" : $"{items.Count} / {result.all.Count} 条";
                    _shownSnapshot = result.all; _shownCategory = category; _shownQuery = query;
                }
                _filterPending = false; HistoryList.IsEnabled = true;
                RevealCopiedRecord();
                return;
            }
        }
        catch (OperationCanceledException) when (_closed) { }
        catch (Exception) { if (!_closed) StatusText.Text = "历史加载失败，请重试。"; }
        finally { _refreshing = false; }
    }

    private void RevealCopiedRecord()
    {
        if (!IsVisible || _revealCopiedId is not { } id || !_views.TryGetValue(id, out var view)) return;
        HistoryList.SelectedItem = view;
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
            if (_closed || !IsVisible || _filterPending || _revealCopiedId != id || !HistoryList.Items.Contains(view)) return;
            HistoryList.ScrollIntoView(view);
            HistoryList.UpdateLayout();
            // 当前列表使用按项滚动；让刚复制项成为视口首项，同时保留置顶分组。
            FindScrollViewer(HistoryList)?.ScrollToVerticalOffset(HistoryList.Items.IndexOf(view));
            _revealCopiedId = null;
        });
    }

    private static ScrollViewer? FindScrollViewer(DependencyObject root)
    {
        if (root is ScrollViewer viewer) return viewer;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            if (FindScrollViewer(VisualTreeHelper.GetChild(root, i)) is { } found) return found;
        return null;
    }

    private void Filter_Changed(object sender, RoutedEventArgs e)
    {
        if (SyncPanel is not null && HistoryPanel is not null && HistoryFooter is not null)
        {
            var syncing = SyncTab.IsSelected;
            SyncPanel.Visibility = syncing ? Visibility.Visible : Visibility.Collapsed;
            SearchPanel.Visibility = HistoryPanel.Visibility = HistoryFooter.Visibility = syncing ? Visibility.Collapsed : Visibility.Visible;
            if (syncing) { ClosePreview(); RefreshSendRecipients(); return; }
        }
        if (HistoryList is not null) { _filterPending = true; HistoryList.IsEnabled = false; }
        RefreshViews();
    }
    private void ClearSearch_Click(object sender, RoutedEventArgs e) { SearchBox.Clear(); SearchBox.Focus(); }
    private void Hide_Click(object sender, RoutedEventArgs e) => Dismiss();

    internal async Task CopySelectedAsync(bool paste = false)
    {
        if (_busy || _filterPending || SelectedRecord is not { } record) return;
        var pasteTarget = _pasteTarget;
        var version = ++_copyVersion;
        DateTimeOffset copiedAt;
        _busy = true;
        try
        {
            await _copy(record);
            if (_closed) return;
            copiedAt = DateTimeOffset.UtcNow;
            if (paste)
            {
                if (await _paste(pasteTarget)) { StatusText.Text = "已向原应用发送粘贴快捷键。"; Dismiss(); }
                else { Show(); Activate(); StatusText.Text = "内容已复制，但无法自动粘贴。请切换到原应用按 Ctrl + V。"; }
            }
            else { StatusText.Text = "已复制。"; Dismiss(); }
        }
        catch (IOException) { if (!_closed) StatusText.Text = record.Files.Length > 0 ? "原文件已移动、删除或无法访问，未完成复制。" : "图片附件无法读取，未完成复制。"; return; }
        catch (Exception) { if (!_closed) StatusText.Text = "复制失败，请稍后重试；窗口已保留。"; return; }
        finally { _busy = false; }
        // 剪贴板操作已结束；历史写盘不应阻止用户再次唤起并复制。
        try
        {
            await Task.Run(() => _store.MarkCopied(record.Id, copiedAt));
            if (!_closed && version == _copyVersion) { _revealCopiedId = record.Id; if (IsVisible) RefreshViews(); }
        }
        catch (Exception) { if (!_closed && version == _copyVersion) StatusText.Text += " 复制时间未能保存，请检查目录权限或磁盘空间。"; }
    }

    internal Task PasteSelectedAsync() => CopySelectedAsync(true);
    internal Task ActivateSelectedAsync() => CopySelectedAsync(_settings.ClipboardAutoPaste);
    private async void CopyMenu_Click(object sender, RoutedEventArgs e) => await CopySelectedAsync();
    private async void Paste_Click(object sender, RoutedEventArgs e) => await PasteSelectedAsync();

    private async void History_Click(object sender, MouseButtonEventArgs e)
    {
        if (_busy || e.ChangedButton != MouseButton.Left || e.OriginalSource is not DependencyObject source || InsideAction(source)) return;
        if (ItemsControl.ContainerFromElement(HistoryList, source) is not ListBoxItem item || item.DataContext is not ClipboardRecordView) return;
        HistoryList.SelectedItem = item.DataContext; e.Handled = true; await ActivateSelectedAsync();
    }

    private static bool InsideAction(DependencyObject? element)
    {
        while (element is not null)
        {
            if (element is ButtonBase or ScrollBar or Thumb) return true;
            element = element is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(element)
                : element is FrameworkContentElement content ? content.Parent : null;
        }
        return false;
    }

    private void RecordMenu_Opened(object sender, RoutedEventArgs e)
    {
        var record = SelectedRecord;
        foreach (var menu in HistoryList.ContextMenu.Items.OfType<MenuItem>()) menu.IsEnabled = record is not null && !_busy;
        PinMenuItem.Header = record?.PinLabel ?? "置顶记录";
        SendRecordMenuItem.IsEnabled = record?.Files.Length > 0 && !_busy;
    }

    private async void CardAction_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (_busy || sender is not Button { DataContext: ClipboardRecordView view } button) return;
        HistoryList.SelectedItem = view;
        switch (button.Tag as string)
        {
            case "pin": await TogglePinAsync(); break;
            case "delete": await DeleteSelectedAsync(); break;
            case "preview": ShowPreview(); break;
            case "send": await SendPathsAsync(view.Record.Files); break;
        }
    }

    internal async Task TogglePinAsync()
    {
        if (_busy || _filterPending || SelectedRecord is not { } record) return;
        try { await Task.Run(() => _store.SetPinned(record.Id, !record.IsPinned)); RefreshViews(); }
        catch (Exception) { StatusText.Text = "置顶状态保存失败，请检查数据目录权限或磁盘空间。"; }
    }
    internal async Task DeleteSelectedAsync()
    {
        if (_busy || _importing || _filterPending || SelectedRecord is not { } record) return;
        _busy = true;
        ClosePreview();
        try
        {
            await ClipboardHistoryRuntime.DrainPendingAsync(_store);
            await Task.Run(() => _store.Delete(record.Id));
            if (!_closed) StatusText.Text = record.Files.Length > 0 ? "已移除历史记录，源文件已保留。" : "已彻底删除记录及保存的内容。";
        }
        catch (Exception) { if (!_closed) StatusText.Text = "删除未完成，请解除文件占用、只读属性或检查数据目录权限后重试。"; }
        finally { _busy = false; if (!_closed) RefreshViews(); }
    }
    private async void PinMenu_Click(object sender, RoutedEventArgs e) => await TogglePinAsync();
    private async void ClearUnpinned_Click(object sender, RoutedEventArgs e) => await ClearUnpinnedAsync();

    internal async Task ClearUnpinnedAsync()
    {
        if (_busy || _importing) return;
        _busy = true; ClearUnpinnedButton.IsEnabled = false;
        ClosePreview();
        try
        {
            await ClipboardHistoryRuntime.DrainPendingAsync(_store);
            var result = await Task.Run(() => _store.DeleteUnpinned());
            if (_closed) return;
            RefreshViews();
            StatusText.Text = $"已删除 {result.Deleted} 条未置顶记录，置顶内容已保留。"
                + (result.Failed > 0 ? $"{result.Failed} 项内容未清理，请检查占用、只读属性、权限或文件归属后重试。" : "可确认归属的未引用附件与恢复副本已清理。")
                + (_store.UnreadableCount > 0 ? "无法读取的历史文件已保留。" : "");
        }
        catch (Exception) { if (!_closed) { RefreshViews(); StatusText.Text = "清空未完成，请检查文件占用或权限后重试。"; } }
        finally { _busy = false; if (!_closed) ClearUnpinnedButton.IsEnabled = true; }
    }
    private async void DeleteMenu_Click(object sender, RoutedEventArgs e) => await DeleteSelectedAsync();
    private void PreviewMenu_Click(object sender, RoutedEventArgs e) => ShowPreview();

    internal async void ShowPreview()
    {
        if (_filterPending || SelectedRecord is not { } record) return;
        var version = ++_previewVersion;
        PreviewText.Text = record.Files.Length > 0 ? string.Join("\n", record.Files) : record.DisplayText;
        if (record.Text.Length == 0 && record.Rtf.Length > 0)
        {
            try
            {
                var document = new FlowDocument(); var range = new TextRange(document.ContentStart, document.ContentEnd);
                using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(record.Rtf));
                range.Load(stream, System.Windows.DataFormats.Rtf); PreviewText.Text = range.Text;
            }
            catch (Exception) { PreviewText.Text = record.Rtf; StatusText.Text = "富文本无法排版，预览显示原始内容；复制仍保留原格式。"; }
        }
        PreviewImage.Source = null;
        DetailHint.Text = record.Category + " · " + record.TimeLabel;
        DetailSource.Text = record.SourceLabel;
        DetailSource.Visibility = record.SourceLabel.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        PreviewPanel.Visibility = Visibility.Visible; PreviewText.Focus();
        if (HistoryList.SelectedItem is ClipboardRecordView view)
        {
            try
            {
                var image = await Task.Run(() => view.LoadImage(false));
                if (!_closed && version == _previewVersion) PreviewImage.Source = image;
            }
            catch (Exception) { if (!_closed && version == _previewVersion) StatusText.Text = "图片附件无法预览，原记录已保留。"; }
        }
    }

    private async void ImagePreview_Opened(object sender, RoutedEventArgs e)
    {
        if (sender is not ToolTip tip || tip.PlacementTarget is not FrameworkElement { DataContext: ClipboardRecordView view }
            || tip.Content is not System.Windows.Controls.Image image) return;
        var request = new object();
        tip.Tag = request;
        ImageSource? source;
        try { source = await Task.Run(() => view.LoadImage(false)); }
        catch (Exception) { source = view.Thumbnail; }
        if (!_closed && tip.IsOpen && ReferenceEquals(tip.Tag, request)
            && tip.PlacementTarget is FrameworkElement target && ReferenceEquals(target.DataContext, view)) image.Source = source;
    }

    private void ImagePreview_Closed(object sender, RoutedEventArgs e)
    {
        if (sender is ToolTip { Content: System.Windows.Controls.Image image } tip) { tip.Tag = null; image.Source = null; }
    }
    private void ClosePreview_Click(object sender, RoutedEventArgs e) => ClosePreview();
    private void ClosePreview() { ++_previewVersion; PreviewPanel.Visibility = Visibility.Collapsed; PreviewImage.Source = null; PreviewText.Clear(); SearchBox.Focus(); }

    internal void SetWindowPinned(bool pinned)
    {
        WindowPinned = pinned; Topmost = pinned;
        WindowPinButton.Content = pinned ? "\uE77A" : "\uE718";
        if (pinned) WindowPinButton.SetResourceReference(BackgroundProperty, "AppAccentSubtle");
        else WindowPinButton.Background = Brushes.Transparent;
        WindowPinButton.SetResourceReference(ForegroundProperty, pinned ? "AppAccent" : "AppTextSecondary");
        WindowPinButton.ToolTip = pinned ? "取消固定窗口：点击外部自动收起" : "固定窗口：失焦后保持显示";
        System.Windows.Automation.AutomationProperties.SetName(WindowPinButton, pinned ? "取消固定窗口" : "固定窗口");
    }
    private void WindowPin_Click(object sender, RoutedEventArgs e) => SetWindowPinned(!WindowPinned);
    private void Header_Drag(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject source && InsideAction(source)) return;
        if (e.LeftButton == MouseButtonState.Pressed)
        { DragMove(); if (_managePosition) { _rememberPlacement = true; SavePlacement(); } }
    }

    private void Menu_Click(object sender, RoutedEventArgs e)
    { RefreshBehaviorSettings(); MenuButton.ContextMenu.PlacementTarget = MenuButton; MenuButton.ContextMenu.IsOpen = true; }

    private void RefreshBehaviorSettings()
    {
        AutoPasteMenuItem.IsChecked = _settings.ClipboardAutoPaste;
        RecordHistoryMenuItem.IsChecked = _settings.ClipboardHistoryEnabled;
        ActionHint.Text = _settings.ClipboardAutoPaste ? "点击 / Enter 粘贴 · Esc 收起" : "点击 / Enter 复制 · Esc 收起";
        RecordingHint.Text = _settings.ClipboardHistoryEnabled ? "剪贴板历史" : "历史记录已暂停";
    }

    private void BehaviorSetting_Click(object sender, RoutedEventArgs e)
    {
        var previousPaste = _settings.ClipboardAutoPaste;
        var previousHistory = _settings.ClipboardHistoryEnabled;
        _settings.ClipboardAutoPaste = AutoPasteMenuItem.IsChecked;
        _settings.ClipboardHistoryEnabled = RecordHistoryMenuItem.IsChecked;
        if (!_saveSettings())
        {
            _settings.ClipboardAutoPaste = previousPaste;
            _settings.ClipboardHistoryEnabled = previousHistory;
            StatusText.Text = "设置保存失败，请检查目录权限或磁盘空间后重试。";
        }
        else StatusText.Text = ReferenceEquals(sender, AutoPasteMenuItem)
            ? (_settings.ClipboardAutoPaste ? "自动粘贴已开启：点击条目后回到原输入框。" : "自动粘贴已关闭：点击条目只复制内容。")
            : (_settings.ClipboardHistoryEnabled ? "历史记录已开启，仅记录此后复制的内容。" : "历史记录已暂停，已有内容仍可使用。" );
        RefreshBehaviorSettings();
    }
    private void ShortcutSettings_Click(object sender, RoutedEventArgs e) { Dismiss(); ShellWindow.ShowOrActivate(ShellPage.Settings).ShowQuickToolSettings(); }
    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        try { Directory.CreateDirectory(_store.Folder); Process.Start(new ProcessStartInfo(_store.Folder) { UseShellExecute = true }); }
        catch (Exception) { StatusText.Text = "无法打开历史数据目录。"; }
    }
    private void ResetPlacement_Click(object sender, RoutedEventArgs e)
    {
        try { _store.ClearPlacement(); }
        catch (Exception) { StatusText.Text = "位置设置未能清除，本次先使用默认位置。"; }
        Width = 560; Height = 700; _rememberPlacement = false; PositionWindow(useSaved: false);
    }

    private IntPtr PlacementMessage(IntPtr hwnd, int message, IntPtr wparam, IntPtr lparam, ref bool handled)
    {
        // 只在用户完成原生拖动／缩放后记忆；程序默认定位不会把鼠标屏幕永久固定成第一块屏幕。
        if (message == 0x0232)
        {
            _rememberPlacement = true;
            Dispatcher.BeginInvoke(() => { if (!_closed) SavePlacement(); });
        }
        return IntPtr.Zero;
    }

    private void PositionWindow(bool useSaved = true)
    {
        ClipboardWindowPlacement? saved = null;
        if (useSaved)
        {
            try { saved = _store.ReadPlacement(); }
            catch (Exception) { StatusText.Text = "窗口位置设置无法读取，已使用右下角默认位置。"; }
        }
        var cursor = System.Windows.Forms.Cursor.Position;
        var screen = saved is null ? null : System.Windows.Forms.Screen.AllScreens.FirstOrDefault(s => s.DeviceName == saved.Monitor);
        screen ??= System.Windows.Forms.Screen.FromPoint(cursor);
        var area = screen.WorkingArea;
        var scale = ScreenGeometry.ScaleAt(area.Left + area.Width / 2, area.Top + area.Height / 2);
        var width = saved?.Width ?? Width; var height = saved?.Height ?? Height;
        var sameMonitor = saved is not null && saved.Monitor == screen.DeviceName;
        var bounds = ClipboardPopupPlacement.Place(area, scale, width, height, sameMonitor ? saved : null);
        MinWidth = Math.Min(460, area.Width / scale); MinHeight = Math.Min(400, area.Height / scale);
        Width = bounds.Width / scale; Height = bounds.Height / scale;
        ScreenGeometry.MoveToPhysical(this, bounds.Left, bounds.Top);
        _rememberPlacement = saved is not null;
    }
    private void SavePlacement()
    {
        if (!_rememberPlacement || _closed || WindowState != WindowState.Normal) return;
        var bounds = ScreenGeometry.PhysicalBounds(this); if (bounds.IsEmpty) return;
        var screen = System.Windows.Forms.Screen.FromRectangle(bounds);
        try { _store.SavePlacement(new(bounds.Left, bounds.Top, Width, Height, screen.DeviceName)); }
        catch (Exception) { StatusText.Text = "窗口位置保存失败，下次将使用已有位置或默认位置。"; }
    }

    private async void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key is Key.Enter or Key.Up or Key.Down && e.OriginalSource is DependencyObject source && InsideAction(source)) return;
        if (e.Key == Key.Escape)
        { e.Handled = true; if (PreviewPanel.Visibility == Visibility.Visible) ClosePreview(); else Dismiss(); }
        else if (SyncTab.IsSelected) return;
        else if (PreviewPanel.Visibility != Visibility.Visible && e.Key is Key.Up or Key.Down && !TypeFilter.IsKeyboardFocusWithin)
        {
            e.Handled = true;
            var count = HistoryList.Items.Count;
            if (count == 0) return;
            HistoryList.SelectedIndex = Math.Clamp(HistoryList.SelectedIndex + (e.Key == Key.Down ? 1 : -1), 0, count - 1);
            HistoryList.ScrollIntoView(HistoryList.SelectedItem);
        }
        else if (e.Key == Key.Enter && PreviewPanel.Visibility != Visibility.Visible && (SearchBox.IsKeyboardFocusWithin || HistoryList.IsKeyboardFocusWithin))
        { e.Handled = true; await ActivateSelectedAsync(); }
    }

    private async void Import_Click(object sender, RoutedEventArgs e) => await ImportSystemHistoryAsync();
    internal async Task ImportSystemHistoryAsync()
    {
        if (_busy || _importing || _closed) return;
        _importing = true; ImportMenuItem.IsEnabled = false;
        var count = 0; var failed = 0;
        try
        {
            StatusText.Text = "正在导入系统历史…";
            var snapshot = await _systemHistory.ReadAsync(_lifetime.Token);
            if (snapshot.Status != ClipboardHistoryItemsResultStatus.Success)
            {
                StatusText.Text = snapshot.Status == ClipboardHistoryItemsResultStatus.ClipboardHistoryDisabled
                    ? "Windows 历史未开启；独立历史仍会记录新复制内容。" : "无法读取系统历史，请检查 Windows 设置或系统权限。";
                return;
            }
            foreach (var item in snapshot.Items)
            {
                _lifetime.Token.ThrowIfCancellationRequested();
                try
                {
                    var record = new ClipboardRecord { Text = item.Text, Source = "系统历史", CopiedAt = item.Timestamp };
                    byte[]? png = null;
                    if (item.NativeItem is { } native)
                    {
                        if (native.Content.Contains(StandardDataFormats.Html)) record = record with { Html = await native.Content.GetHtmlFormatAsync().AsTask(_lifetime.Token) };
                        if (native.Content.Contains(StandardDataFormats.Rtf)) record = record with { Rtf = await native.Content.GetRtfAsync().AsTask(_lifetime.Token) };
                        if (item.HasImage)
                        {
                            var reference = await native.Content.GetBitmapAsync().AsTask(_lifetime.Token);
                            using var access = await reference.OpenReadAsync().AsTask(_lifetime.Token);
                            using var stream = access.AsStreamForRead();
                            var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.None);
                            var frame = decoder.Frames[0];
                            if ((long)frame.PixelWidth * frame.PixelHeight > 32 * 1024 * 1024) throw new InvalidDataException();
                            using var output = new MemoryStream();
                            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(frame)); encoder.Save(output); png = output.ToArray();
                        }
                    }
                    else if (item.Image is BitmapSource image)
                    { using var output = new MemoryStream(); var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image)); encoder.Save(output); png = output.ToArray(); }
                    if (record.Text.Length == 0 && record.Rtf.Length > 0) record = record with { Text = ClipboardContent.RtfText(record.Rtf) };
                    if (record.Text.Length == 0 && record.Html.Length == 0 && record.Rtf.Length == 0 && png is null) { failed++; continue; }
                    await Task.Run(() => _store.Add(record, png)); count++;
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception) { failed++; }
            }
            if (!_closed) { RefreshViews(); StatusText.Text = $"已导入 {count} 条，跳过 {failed} 条；相同内容合并，Windows 原记录未改动。"; }
        }
        catch (OperationCanceledException) { }
        catch (Exception) { if (!_closed) StatusText.Text = "系统历史导入未完成，已保存的记录保留，可稍后重试。"; }
        finally { _importing = false; if (!_closed) ImportMenuItem.IsEnabled = true; }
    }
}
