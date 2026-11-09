using System.ComponentModel;
using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using OverTranslate.Models;
using OverTranslate.Services;
using OverTranslate.Services.Library;
using Button = System.Windows.Controls.Button;
using CheckBox = System.Windows.Controls.CheckBox;
using ComboBox = System.Windows.Controls.ComboBox;
using ListBox = System.Windows.Controls.ListBox;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using MessageBox = System.Windows.MessageBox;

namespace OverTranslate.Views.Library;

public partial class LibraryWindow : Window
{
    private static LibraryWindow? _current;
    private readonly LibraryStore _store;
    private readonly LibraryHttp _http;
    private readonly Action<string> _copy;
    private readonly HashSet<string> _refreshing = [];
    private readonly HashSet<string> _summarizing = [];
    private readonly HashSet<string> _selectedTags = [];
    private bool _loading;
    private bool _ready;
    private bool _shutdown;
    private int _visiblePrompts = 30;
    private bool _appendPrompts;
    private readonly ObservableCollection<LibraryPrompt> _promptItems = [];
    private Dictionary<string, int> _promptOrder = [];
    private string _editorKind = "";
    private LibraryPrompt? _editingPrompt;
    private LibraryConnection? _editingConnection;
    private string _lastPlatform = "";
    private string _lastType = "chat";
    private string _lastEndpoint = "";
    private int _editorVersion;
    private List<string>? _editorIds;
    private CancellationTokenSource? _editorRequest;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly System.Windows.Threading.DispatcherTimer _copyFeedbackTimer = new() { Interval = TimeSpan.FromSeconds(2) };
    private bool _copyNoticeActive;
    private Button? _copySourceButton;
    private object? _copySourceContent;
    private CancellationTokenSource? _copyRequest;
    private string? _copyingPromptId;
    private int _promptCopyVersion;
    private Task _copyHistory = Task.CompletedTask;
    private int _pendingCopies;
    internal bool IsCopyPending => _pendingCopies > 0;
    private readonly System.Windows.Threading.DispatcherTimer _rankTimer = new() { Interval = TimeSpan.FromMilliseconds(600) };
    private readonly System.Windows.Threading.DispatcherTimer _saveNoticeTimer = new() { Interval = TimeSpan.FromSeconds(6) };
    private bool _savingEditor;
    private Task _editorSaveTask = Task.CompletedTask;

    public LibraryWindow() : this(new LibraryStore(LibraryStore.DefaultPath)) { }

    internal LibraryWindow(LibraryStore store, LibraryHttp? http = null, Action<string>? copy = null)
    {
        _store = store;
        _http = http ?? new();
        _copy = copy ?? (text => LibraryClipboard.SetText(text, new System.Windows.Interop.WindowInteropHelper(this).EnsureHandle()));
        InitializeComponent();
        Shell.WindowFrame.Attach(this);
        _rankTimer.Tick += (_, _) => RankCopiedPrompts();
        _copyFeedbackTimer.Tick += (_, _) => ResetCopyFeedback();
        _saveNoticeTimer.Tick += (_, _) => DismissSaveNotice();
        LibrarySyncNotifications.Changed += SyncChanged;
        Activated += (_, _) => RefreshShortcut();
        Deactivated += (_, _) => { CancelPendingCopy(); ResetCopyFeedback(); };
        IsVisibleChanged += (_, _) =>
        {
            if (!IsVisible) { CancelPendingCopy(); ResetCopyFeedback(); _rankTimer.Stop(); }
            else { _rankTimer.Start(); RankCopiedPrompts(); }
        };
        PromptList.ItemsSource = _promptItems;
        PlatformBox.ItemsSource = ModelPlatforms.Presets.Select(p => p.Name).ToList();
        TypeBox.ItemsSource = ModelPlatforms.Types;
        _ready = true;
        _loading = true;
        Tabs.SelectedIndex = Math.Clamp(_store.Read().SelectedTab, 0, 1);
        _loading = false;
        RefreshViews();
        RefreshShortcut();
    }

    public static void Open() => Open(quickAccess: false);

    private static void Open(bool quickAccess)
    {
        try
        {
            _current ??= new LibraryWindow();
            _current.SetQuickAccess(quickAccess);
            _current.RefreshShortcut();
            if (!_current.IsVisible) _current.Show();
            if (_current.WindowState == WindowState.Minimized) _current.WindowState = WindowState.Normal;
            _current.Activate();
        }
        catch (Exception ex) { MessageBox.Show(Error(ex), "资料库暂时无法打开", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }

    public static void Toggle()
    {
        if (_current is { IsVisible: true, WindowState: not WindowState.Minimized }) _current.ToggleVisibility();
        else Open(quickAccess: true);
    }

    internal void ToggleVisibility()
    {
        // 后台仍可见的窗口应先唤到前台，只有已在前台时才收起。
        if (IsVisible && WindowState != WindowState.Minimized && IsActive) { Hide(); }
        else
        {
            SetQuickAccess(true);
            if (!IsVisible) Show();
            if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
            Activate();
        }
    }

    private void SetQuickAccess(bool quickAccess)
    {
        ManagementHeader.Visibility = quickAccess ? Visibility.Collapsed : Visibility.Visible;
        QuickWindowControls.Visibility = quickAccess ? Visibility.Visible : Visibility.Collapsed;
        WindowStyle = quickAccess ? WindowStyle.None : WindowStyle.SingleBorderWindow;
        System.Windows.Shell.WindowChrome.SetWindowChrome(this, quickAccess ? new System.Windows.Shell.WindowChrome
        {
            CaptionHeight = 0, ResizeBorderThickness = new Thickness(6),
            GlassFrameThickness = new Thickness(0), CornerRadius = new CornerRadius(0),
            UseAeroCaptionButtons = false
        } : null);
    }

    private void QuickClose_Click(object sender, RoutedEventArgs e) => Close();

    private void QuickDrag_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
            WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
        else if (e.LeftButton == MouseButtonState.Pressed) DragMove();
    }

    public static void Shutdown()
    {
        if (_current is null) return;
        _current._shutdown = true;
        _current.Close();
        _current = null;
    }

    internal void CloseForTests() { _shutdown = true; Close(); }

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (_savingEditor && !_shutdown) { e.Cancel = true; return; }
        CancelPendingCopy();
        _rankTimer.Stop();
        ResetCopyFeedback();
        if (!_shutdown) { e.Cancel = true; Hide(); return; }
        DismissSaveNotice();
        LibrarySyncNotifications.Changed -= SyncChanged;
        // 只等待不依赖界面线程的写盘任务，避免退出中断已提交的保存。
        try { _editorSaveTask.GetAwaiter().GetResult(); }
        catch { /* 保存调用方处理错误，退出时不再次抛出。 */ }
        // 保存任务不依赖界面线程；正常退出前写完已成功复制的记录。
        try { _copyHistory.GetAwaiter().GetResult(); }
        catch { /* 复制调用方负责提示保存失败，退出时不再抛出。 */ }
        _lifetime.Cancel(); _editorRequest?.Cancel(); _http.Dispose(); _lifetime.Dispose();
    }

    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (EditorLayer.Visibility == Visibility.Visible && e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.Control)
        { SaveEditor_Click(sender, e); e.Handled = true; return; }
        if (e.Key != Key.Escape) return;
        if (EditorLayer.Visibility == Visibility.Visible) CancelEditor_Click(sender, e); else Hide();
        e.Handled = true;
    }

    private static string Error(Exception ex) => ex is InvalidOperationException or InvalidDataException
        ? ex.Message : ex is OperationCanceledException ? "操作已取消或超时，原有资料已保留。" : "操作失败，请检查目录权限、资料格式或网络。原有资料未覆盖。";

    private bool Run(Action action)
    {
        try { action(); return true; }
        catch (Exception ex) { Notice(Error(ex), true); return false; }
    }

    private void Notice(string text, bool error = false)
    {
        ResetCopyFeedback();
        Status.Text = text;
        Status.SetResourceReference(TextBlock.ForegroundProperty, error ? "AppError" : "AppTextSecondary");
        if (EditorLayer.Visibility == Visibility.Visible)
        {
            EditorStatus.Text = text;
            EditorStatus.SetResourceReference(TextBlock.ForegroundProperty, error ? "AppError" : "AppTextSecondary");
        }
    }
    private void CancelPendingCopy() => _copyRequest?.Cancel();

    private async Task<bool?> Copy(string value, string label, FrameworkElement? anchor = null)
    {
        CancelPendingCopy();
        using var request = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _copyRequest = request;
        try
        {
            if (string.IsNullOrEmpty(value)) { ShowCopyFeedback($"没有可复制的{label}。", false, anchor); return false; }
            ResetCopyFeedback();
            Status.Text = "正在复制…";
            _copyNoticeActive = true;
            try
            {
                await LibraryClipboard.WriteAsync(value, _copy, request.Token);
                if (request.IsCancellationRequested) return null;
                ShowCopyFeedback($"已复制{label}。", true, anchor);
                return true;
            }
            catch (OperationCanceledException) { return null; }
            catch (Exception) { ShowCopyFeedback($"复制{label}失败，请重试。", false, anchor); return false; }
        }
        finally { if (ReferenceEquals(_copyRequest, request)) _copyRequest = null; }
    }

    private Task<T> SaveCopyHistory<T>(Func<T> save)
    {
        var previous = _copyHistory;
        var next = Task.Run(async () => { try { await previous; } catch { /* 每次保存由对应调用方报告失败。 */ } return save(); });
        _copyHistory = next;
        return next;
    }

    private void RankCopiedPrompts()
    {
        if (_savingEditor || IsCopyPending || System.Windows.Input.Mouse.LeftButton == MouseButtonState.Pressed) return;
        _rankTimer.Stop();
        var ordered = _promptItems.OrderByDescending(p => p.CopyCount)
            .ThenBy(p => _promptOrder.GetValueOrDefault(p.Id, int.MaxValue)).ToList();
        for (var i = 0; i < ordered.Count; i++)
        {
            var index = _promptItems.IndexOf(ordered[i]);
            if (index != i) _promptItems.Move(index, i);
        }
    }

    private void ResetCopyFeedback()
    {
        _copyFeedbackTimer.Stop();
        if (CopyToast is null) return;
        CopyToast.IsOpen = false;
        if (_copySourceButton is not null)
        {
            _copySourceButton.DataContextChanged -= CopySource_ContextChanged;
            _copySourceButton.Content = _copySourceContent;
            _copySourceButton = null;
            _copySourceContent = null;
        }
        PromptCopyButton.Content = "复制全文";
        PromptCopyFeedback.Text = "单击左侧条目复制 · ↑↓ 选择 · Enter 复制";
        PromptCopyFeedback.SetResourceReference(TextBlock.ForegroundProperty, "AppTextSecondary");
        if (_copyNoticeActive)
        {
            Status.Text = Tabs.SelectedIndex == 0 ? "单击提示词复制，右侧查看全文；↑↓ 选择，Enter 复制。" : "选择连接查看详情，模型列表下拉选择即复制。";
            Status.SetResourceReference(TextBlock.ForegroundProperty, "AppTextSecondary");
            _copyNoticeActive = false;
        }
    }

    private void CopySource_ContextChanged(object sender, DependencyPropertyChangedEventArgs e) => ResetCopyFeedback();

    private void ShowCopyFeedback(string message, bool success, FrameworkElement? anchor)
    {
        _copyFeedbackTimer.Stop();
        Notice(message, !success);
        CopyToast.IsOpen = false;
        CopyToastText.Text = success ? "已复制" : "复制失败，请重试";
        CopyToastText.SetResourceReference(TextBlock.ForegroundProperty, success ? "AppAccent" : "AppError");
        CopyToastBorder.SetResourceReference(Border.BorderBrushProperty, success ? "AppAccent" : "AppError");
        CopyToast.PlacementTarget = anchor ?? (Tabs.SelectedIndex == 0 ? PromptCopyButton : ModelCatalog);
        CopyToast.Placement = System.Windows.Controls.Primitives.PlacementMode.Top;
        if (IsVisible) CopyToast.IsOpen = true;
        _copyNoticeActive = true;
        _copyFeedbackTimer.Start();
    }

    private void Tabs_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!_ready || _loading || !ReferenceEquals(e.Source, Tabs)) return;
        CancelPendingCopy();
        ResetCopyFeedback();
        Run(() => _store.Update(s => s.SelectedTab = Tabs.SelectedIndex));
    }

    private void RefreshViews()
    {
        if (!_ready) return;
        _loading = true;
        try
        {
            var state = _store.Read();
            _promptOrder = state.Prompts.Select((p, index) => (p.Id, index))
                .GroupBy(p => p.Id).ToDictionary(g => g.Key, g => g.First().index);
            var promptId = (PromptList.SelectedItem as LibraryPrompt)?.Id;
            var connectionId = (ModelList.SelectedItem as LibraryConnection)?.Id;
            var tags = state.Prompts.SelectMany(p => p.Tags).Distinct().Order(StringComparer.CurrentCulture).ToList();
            _selectedTags.IntersectWith(tags);
            TagFilters.ItemsSource = tags.Where(t => t.Contains(TagSearch.Text, StringComparison.OrdinalIgnoreCase)).ToList();
            foreach (var tag in _selectedTags) if (TagFilters.Items.Contains(tag)) TagFilters.SelectedItems.Add(tag);
            var filtered = LibraryStore.FilterPrompts(state, PromptSearch.Text, _selectedTags).ToList();
            if (!_appendPrompts) _promptItems.Clear();
            var known = _promptItems.Select(p => p.Id).ToHashSet();
            foreach (var p in filtered.Take(_visiblePrompts)) if (known.Add(p.Id)) _promptItems.Add(p);
            PromptList.SelectedItem = _promptItems.FirstOrDefault(p => p.Id == promptId) ?? _promptItems.FirstOrDefault();
            PromptCount.Text = $"{filtered.Count} / {state.Prompts.Count} 条提示词";
            PromptEmpty.Visibility = filtered.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            PromptEmpty.Text = state.Prompts.Count == 0 ? "还没有提示词，点击右上角新建。" : "没有匹配的提示词，请调整搜索或标签。";
            var query = ModelSearch.Text.Trim();
            var connections = state.Connections.Where(c => string.Join(' ', c.Platform, c.Endpoint, c.Model, c.ModelIdToCopy, c.Note)
                .Contains(query, StringComparison.OrdinalIgnoreCase)).ToList();
            ModelList.ItemsSource = connections;
            ModelList.SelectedItem = connections.FirstOrDefault(c => c.Id == connectionId) ?? connections.FirstOrDefault();
            ModelEmpty.Visibility = connections.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            ModelEmpty.Text = state.Connections.Count == 0 ? "还没有连接资料，点击右上角新增。" : "没有匹配的连接，请调整搜索。";
            ModelCount.Text = $"{connections.Count} / {state.Connections.Count} 条连接资料";
        }
        finally { _loading = false; _appendPrompts = false; }
    }

    private void PromptSearch_Changed(object sender, TextChangedEventArgs e) { _visiblePrompts = 30; RefreshViews(); }
    private void ModelSearch_Changed(object sender, TextChangedEventArgs e) => RefreshViews();
    private void TagSearch_Changed(object sender, TextChangedEventArgs e) => RefreshViews();
    private void TagFilters_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!_ready || _loading) return;
        foreach (string t in e.RemovedItems) _selectedTags.Remove(t);
        foreach (string t in e.AddedItems) _selectedTags.Add(t);
        _visiblePrompts = 30; RefreshViews();
    }
    private void ClearTags_Click(object sender, RoutedEventArgs e) { _selectedTags.Clear(); _visiblePrompts = 30; RefreshViews(); }
    private void Prompts_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (!_ready || _loading || e.VerticalChange <= 0
            || e.ExtentHeight - e.VerticalOffset - e.ViewportHeight > Math.Max(1, e.ViewportHeight / 2)) return;
        if (PromptList.Items.Count < _visiblePrompts) return;
        _visiblePrompts += 30; _appendPrompts = true; RefreshViews();
    }

    private void CopyPrompt_Click(object sender, RoutedEventArgs e)
    { if (((Button)sender).Tag is LibraryPrompt p) CopyPrompt(p, (Button)sender); }

    private async void CopyPrompt(LibraryPrompt item, FrameworkElement anchor)
    {
        if (_copyingPromptId == item.Id) return;
        PromptList.SelectedItem = item;
        var version = ++_promptCopyVersion;
        _copyingPromptId = item.Id;
        _pendingCopies++;
        try
        {
            var copied = await Copy(item.Content, $"提示词《{item.Title}》", anchor);
            if (copied is null) return;
            if (version == _promptCopyVersion) _copyingPromptId = null;
            if (anchor is Button source && !ReferenceEquals(source, PromptCopyButton))
            {
                _copySourceButton = source;
                _copySourceContent = source.Content;
                source.DataContextChanged += CopySource_ContextChanged;
                source.Content = copied.Value ? "已复制" : "重试";
            }
            PromptCopyButton.Content = copied.Value ? "已复制" : "重试复制";
            PromptCopyFeedback.Text = copied.Value ? $"已复制《{item.Title}》" : $"《{item.Title}》复制失败，请重试";
            PromptCopyFeedback.SetResourceReference(TextBlock.ForegroundProperty, copied.Value ? "AppAccent" : "AppError");
            if (copied.Value)
            {
                try
                {
                    var count = await SaveCopyHistory(() => _store.RememberCopiedPrompt(item.Id));
                    item.CopyCount = Math.Max(item.CopyCount, count);
                    // 搜索或筛选可能已替换列表对象，按 ID 更新当前快照。
                    foreach (var current in _promptItems.Where(p => p.Id == item.Id))
                        current.CopyCount = Math.Max(current.CopyCount, count);
                    if (IsVisible && !_shutdown) { _rankTimer.Stop(); _rankTimer.Start(); }
                }
                catch (Exception)
                {
                    if (IsVisible && version == _promptCopyVersion && (PromptList.SelectedItem as LibraryPrompt)?.Id == item.Id)
                        Notice("提示词已复制，但未能保存复制次数，请检查资料文件后重试。", true);
                }
            }
        }
        finally { if (version == _promptCopyVersion) _copyingPromptId = null; _pendingCopies--; }
    }

    private void PromptDetail_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (PromptHeader is null || PromptActions is null) return;
        // 长标题、摘要与标签滚动查看；紧凑布局至少保留一行标题。
        PromptHeader.MaxHeight = Math.Max(34, e.NewSize.Height - PromptActions.DesiredSize.Height - 96);
    }

    private void PromptList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!ReferenceEquals(e.Source, PromptList)) return;
        CancelPendingCopy();
        ResetCopyFeedback();
        PromptHeader?.ScrollToHome();
        PromptPreview?.ScrollToHome();
    }

    private void ModelList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    { if (ReferenceEquals(e.Source, ModelList)) { CancelPendingCopy(); ResetCopyFeedback(); ModelDetailScroll?.ScrollToHome(); } }

    private void PromptList_Click(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is not DependencyObject source) return;
        for (var node = source; node is not null && !ReferenceEquals(node, PromptList);
            node = node is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(node) : LogicalTreeHelper.GetParent(node))
        {
            // 行内按钮自行复制；滚动条和列表空白不触发。
            if (node is Button) return;
            if (node is ListBoxItem row && row.DataContext is LibraryPrompt item)
            { CopyPrompt(item, row); return; }
        }
    }

    private void PromptList_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && e.OriginalSource is ListBoxItem or ListBox
            && PromptList.SelectedItem is LibraryPrompt item)
        { CopyPrompt(item, PromptCopyButton); e.Handled = true; }
    }

    private void BeginEditor(string kind, string title)
    {
        if (_savingEditor) return;
        DismissSaveNotice();
        CancelPendingCopy(); ResetCopyFeedback();
        _editorVersion++; _editorRequest?.Cancel(); _editorIds = null;
        EditorFetchButton.IsEnabled = true;
        _editorKind = kind; EditorTitle.Text = title; EditorStatus.Text = "";
        EditorStatus.SetResourceReference(TextBlock.ForegroundProperty, "AppTextSecondary");
        EditorScroll.ScrollToTop();
        PromptEditor.Visibility = kind == "prompt" ? Visibility.Visible : Visibility.Collapsed;
        ModelEditor.Visibility = kind == "model" ? Visibility.Visible : Visibility.Collapsed;
        EditorLayer.Visibility = Visibility.Visible;
    }

    private void CloseEditor()
    {
        _editorVersion++; _editorRequest?.Cancel(); _editorIds = null;
        _editorKind = ""; _editingPrompt = null; _editingConnection = null;
        ModelKey.Clear(); EditorLayer.Visibility = Visibility.Collapsed;
    }
    private void CancelEditor_Click(object sender, RoutedEventArgs e)
    {
        if (_savingEditor) return;
        CloseEditor();
        if (Run(RefreshViews)) Notice("已取消编辑，原有资料未更改。");
    }
    private void NewPrompt_Click(object sender, RoutedEventArgs e) => BeginPromptEditor(new(), false);
    private bool _editingPromptWasSaved, _editingConnectionWasSaved;
    private void EditPrompt_Click(object sender, RoutedEventArgs e) { if (((Button)sender).Tag is LibraryPrompt p) EditPrompt(p); }
    private void EditPrompt(LibraryPrompt item) => BeginPromptEditor(item, true);
    private void BeginPromptEditor(LibraryPrompt item, bool saved)
    {
        _editingPromptWasSaved = saved;
        BeginEditor("prompt", _editingPromptWasSaved ? "编辑提示词" : "新建提示词");
        _editingPrompt = item;
        PromptTitle.Text = item.Title; PromptDescription.Text = item.Description;
        PromptBody.Text = item.Content; PromptTags.Text = string.Join("，", item.Tags); PromptBody.Focus();
    }
    private void PromptBody_Changed(object sender, TextChangedEventArgs e) { if (CharacterCount is not null) CharacterCount.Text = $"{PromptBody.Text.Length:N0} 字符"; }
    private void DeletePrompt_Click(object sender, RoutedEventArgs e)
    {
        if (((Button)sender).Tag is not LibraryPrompt p || MessageBox.Show(this, "删除这条提示词？", "确认删除", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        if (Run(() => _store.Update(s => s.Prompts.RemoveAll(x => x.Id == p.Id)))) { RefreshViews(); Notice("提示词已删除。"); }
    }

    private async void Summarize_Click(object sender, RoutedEventArgs e)
    { if (((Button)sender).Tag is LibraryPrompt p) await Summarize(p); }
    private async Task Summarize(LibraryPrompt item)
    {
        if (!_summarizing.Add(item.Id)) { Notice("这条提示词正在整理，请稍候。"); return; }
        Notice("AI 正在整理标题、描述和标签…");
        try
        {
            var version = item.UpdatedAt; var content = item.Content;
            var title = item.Title; var description = item.Description; var tags = item.Tags.ToArray();
            var summary = await _http.SummarizeAsync(_store.Read().Ai, item, _lifetime.Token);
            _store.Update(s =>
            {
                var current = s.Prompts.FirstOrDefault(p => p.Id == item.Id);
                if (current is null || current.UpdatedAt != version || current.Content != content || current.Title != title || current.Description != description || !current.Tags.SequenceEqual(tags))
                    throw new InvalidOperationException("提示词已更改，本次 AI 整理未写入。");
                current.Title = summary.Title; current.Description = summary.Description; current.Tags = summary.Tags;
                current.UpdatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            });
            RefreshViews(); Notice("AI 已更新标题、描述和标签，正文保持原样。");
        }
        catch (Exception ex) { Notice(Error(ex), true); }
        finally { _summarizing.Remove(item.Id); }
    }

    public static void OpenAiSettings()
        => OverTranslate.Views.Shell.ShellWindow.ShowOrActivate(OverTranslate.Views.Shell.ShellPage.Settings).ShowAiSettings();

    private void NewModel_Click(object sender, RoutedEventArgs e) => BeginModelEditor(new(), false);
    private void EditModel_Click(object sender, RoutedEventArgs e) { if (((Button)sender).Tag is LibraryConnection c) EditModel(c); }
    private void EditModel(LibraryConnection item) => BeginModelEditor(item, true);
    private void BeginModelEditor(LibraryConnection item, bool saved)
    {
        _editingConnectionWasSaved = saved;
        BeginEditor("model", _editingConnectionWasSaved ? "编辑连接资料" : "新增连接资料");
        _editingConnection = item;
        _loading = true;
        try
        {
            PlatformBox.Text = item.Platform; TypeBox.SelectedValue = item.ModelType;
            EndpointBox.Text = item.Endpoint; ModelBox.Text = item.Model; ModelKey.Clear();
            ModelInsecure.IsChecked = item.AllowInsecureEndpoint;
            BalanceBox.Text = item.RemainingBalance; NoteBox.Text = item.Note;
            EditorModelIds.ItemsSource = item.ModelIds; EditorModelIds.SelectedIndex = -1;
            _lastPlatform = item.Platform; _lastType = item.ModelType; _lastEndpoint = item.Endpoint;
            KeyHint.Text = item.ApiKeyProtected.Length > 0 ? "API Key（已加密保存，留空保留同源地址原值）" : "API Key（本地接口可留空）";
            PlatformHint.Text = ModelPlatforms.Hint(item.Platform, item.ModelType);
        }
        finally { _loading = false; }
        PlatformBox.Focus();
    }

    private void Platform_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_ready && !_loading && PlatformBox.SelectedItem is string platform) ChangePlatform(platform, TypeBox.SelectedValue as string ?? "chat");
    }
    private void Platform_LostFocus(object sender, KeyboardFocusChangedEventArgs e)
    { if (_ready && !_loading) ChangePlatform(PlatformBox.Text, TypeBox.SelectedValue as string ?? "chat"); }
    private void Type_Changed(object sender, SelectionChangedEventArgs e)
    { if (_ready && !_loading) ChangePlatform(PlatformBox.Text, TypeBox.SelectedValue as string ?? "chat"); }

    private void ChangePlatform(string platform, string type)
    {
        if (_editorKind != "model" || (platform == _lastPlatform && type == _lastType)) return;
        var oldPreset = ModelPlatforms.Endpoint(_lastPlatform, _lastType, EndpointBox.Text);
        var managed = EndpointBox.Text.Trim().Length == 0 || (oldPreset.Length > 0 && EndpointBox.Text.TrimEnd('/') == oldPreset.TrimEnd('/'));
        _loading = true;
        try
        {
            if (managed) EndpointBox.Text = ModelPlatforms.Endpoint(platform, type, platform == _lastPlatform ? EndpointBox.Text : "");
            if (!ModelPlatforms.SameOrigin(_lastEndpoint, EndpointBox.Text)) { ModelKey.Clear(); ModelInsecure.IsChecked = false; }
            ModelBox.Text = "";
            _lastPlatform = platform; _lastType = type; _lastEndpoint = EndpointBox.Text;
            PlatformHint.Text = ModelPlatforms.Hint(platform, type);
            InvalidateEditorModels();
        }
        finally { _loading = false; }
    }
    private void Endpoint_Changed(object sender, TextChangedEventArgs e)
    {
        if (!_ready || _loading || _editorKind != "model") return;
        if (!ModelPlatforms.SameOrigin(_lastEndpoint, EndpointBox.Text)) { ModelKey.Clear(); ModelInsecure.IsChecked = false; }
        _lastEndpoint = EndpointBox.Text;
        InvalidateEditorModels();
    }
    private void InvalidateEditorModels()
    {
        _editorVersion++; _editorRequest?.Cancel(); _editorIds = null;
        EditorModelIds.ItemsSource = null; EditorFetchButton.IsEnabled = true;
    }

    private void ModelKey_Changed(object sender, RoutedEventArgs e)
    { if (_ready && !_loading && _editorKind == "model") InvalidateEditorModels(); }

    private void ModelInsecure_Changed(object sender, RoutedEventArgs e)
    { if (_ready && !_loading && _editorKind == "model") InvalidateEditorModels(); }

    private LibraryConnection ModelDraft() => new()
    {
        Id = _editingConnection!.Id, CreatedAt = _editingConnection.CreatedAt,
        Platform = PlatformBox.Text.Trim(), ModelType = TypeBox.SelectedValue as string ?? "chat", Endpoint = EndpointBox.Text.Trim(),
        Model = ModelBox.Text.Trim(), RemainingBalance = BalanceBox.Text.Trim(), Note = NoteBox.Text,
        AllowInsecureEndpoint = ModelInsecure.IsChecked == true,
        ApiKeyProtected = ModelPlatforms.SameOrigin(_editingConnection.Endpoint, EndpointBox.Text) ? _editingConnection.ApiKeyProtected : ""
    };

    private async void EditorFetch_Click(object sender, RoutedEventArgs e)
    {
        var version = ++_editorVersion;
        _editorRequest?.Cancel(); _editorRequest?.Dispose();
        _editorRequest = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        EditorFetchButton.IsEnabled = false; EditorStatus.Text = "正在获取模型目录…";
        EditorStatus.SetResourceReference(TextBlock.ForegroundProperty, "AppTextSecondary");
        try
        {
            var draft = ModelDraft();
            if (ModelKey.Password.Length > 0) draft.ApiKeyProtected = LocalSecretStore.Protect(ModelKey.Password);
            var ids = await _http.ListModelsAsync(draft, _editorRequest.Token);
            if (version != _editorVersion || _editorKind != "model") return;
            _editorIds = ids.ToList(); EditorModelIds.ItemsSource = _editorIds; EditorModelIds.SelectedIndex = -1;
            EditorStatus.Text = $"已获取 {_editorIds.Count} 个模型；保存连接时一并保存列表。";
        }
        catch (Exception ex) { if (version == _editorVersion) { EditorStatus.Text = Error(ex); EditorStatus.SetResourceReference(TextBlock.ForegroundProperty, "AppError"); } }
        finally { if (version == _editorVersion) EditorFetchButton.IsEnabled = true; }
    }

    private async void EditorModelId_Selected(object sender, SelectionChangedEventArgs e)
    {
        if (!_loading && EditorModelIds.SelectedItem is string id) { ModelBox.Text = id; await Copy(id, $"模型 ID《{id}》", EditorModelIds); }
    }
    private void ModelId_Selected(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || sender is not ComboBox box || box.SelectedItem is not string id || box.Tag is not LibraryConnection connection) return;
        CopyConnectionModel(connection, id, box);
        // 清除选择使再次点击同一ID也能触发复制；不写入连接的固定模型字段。
        box.SelectedIndex = -1;
    }
    private async void RefreshModels_Click(object sender, RoutedEventArgs e)
    {
        var button = (Button)sender;
        if (button.Tag is not LibraryConnection connection || !_refreshing.Add(connection.Id)) return;
        var label = button.Content;
        button.Content = "正在获取…";
        button.IsEnabled = false; Notice("正在获取完整模型目录，成功后覆盖旧列表…");
        try
        {
            await _store.RefreshModelsAsync(connection.Id, _http.ListModelsAsync, _lifetime.Token);
            RefreshViews(); Notice("模型列表已更新并保存。");
        }
        catch (Exception ex) { Notice(Error(ex), true); }
        finally { _refreshing.Remove(connection.Id); button.IsEnabled = true; button.Content = label; }
    }
    private async void CopyEndpoint_Click(object sender, RoutedEventArgs e) { if (((Button)sender).Tag is LibraryConnection c) await Copy(c.Endpoint, $"《{c.Platform}》的地址", (Button)sender); }
    private async void CopyKey_Click(object sender, RoutedEventArgs e)
    {
        if (((Button)sender).Tag is not LibraryConnection c) return;
        try { await Copy(LibraryHttp.UnprotectKey(c.ApiKeyProtected), $"《{c.Platform}》的 API Key", (Button)sender); }
        catch (Exception ex) { Notice(Error(ex), true); }
    }
    private void CopyModel_Click(object sender, RoutedEventArgs e)
    { if (((Button)sender).Tag is LibraryConnection c) CopyConnectionModel(c, c.ModelIdToCopy, (Button)sender); }
    private async void CopyConnectionModel(LibraryConnection connection, string id, FrameworkElement anchor)
    {
        _pendingCopies++;
        try
        {
            if (await Copy(id, $"模型 ID《{id}》", anchor) != true) return;
            connection.LastCopiedModelId = id;
            try { await SaveCopyHistory(() => { _store.RememberCopiedModel(connection.Id, connection.Revision, id); return true; }); }
            catch (Exception) { Notice("模型 ID 已复制，但未能保存复制记录，请检查资料文件或重新打开连接后重试。", true); }
        }
        finally { _pendingCopies--; }
    }
    private void CloneModel_Click(object sender, RoutedEventArgs e)
    { if (((Button)sender).Tag is LibraryConnection c && Run(() => _store.DuplicateConnection(c.Id))) { RefreshViews(); Notice("已克隆一条连接资料。"); } }
    private void DeleteModel_Click(object sender, RoutedEventArgs e)
    {
        if (((Button)sender).Tag is not LibraryConnection c || MessageBox.Show(this, "删除这条连接资料及其已保存列表？", "确认删除", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        if (Run(() => _store.Update(s => s.Connections.RemoveAll(x => x.Id == c.Id)))) { RefreshViews(); Notice("连接资料已删除。"); }
    }

    private void SetEditorSaving(bool saving)
    {
        _savingEditor = saving;
        SaveButton.IsEnabled = CancelEditorButton.IsEnabled = EditorScroll.IsEnabled = !saving;
        SaveButton.Content = saving ? "保存中…" : "保存";
        if (!saving) EditorFetchButton.IsEnabled = true;
        EditorSaveProgress.Visibility = saving ? Visibility.Visible : Visibility.Collapsed;
        if (saving)
        {
            _editorVersion++; _editorRequest?.Cancel();
            EditorStatus.Text = "正在保存，请稍候…";
            EditorStatus.SetResourceReference(TextBlock.ForegroundProperty, "AppTextSecondary");
        }
    }

    private void DismissSaveNotice()
    { _saveNoticeTimer.Stop(); SaveNotice.Visibility = Visibility.Collapsed; }

    private void DismissSaveNotice_Click(object sender, RoutedEventArgs e) => DismissSaveNotice();

    private void ShowSaveSuccess(string text)
    {
        Notice(text);
        SaveNoticeText.Text = text;
        SaveNotice.Visibility = Visibility.Visible;
        _saveNoticeTimer.Stop(); _saveNoticeTimer.Start();
    }

    private static string FeedbackLabel(string value) => value.Length <= 48 ? value : value[..48] + "…";

    private void SyncChanged()
    {
        if (_shutdown || Dispatcher.HasShutdownStarted) return;
        Dispatcher.BeginInvoke(() =>
        {
            if (_shutdown) return;
            if (EditorLayer.Visibility == Visibility.Visible)
                Notice("已收到其他设备的资料变更；当前输入保留，保存时会检查是否与远端修改冲突。");
            else if (Run(RefreshViews)) Notice("已同步其他设备的资料。冲突版本会标记为同步冲突副本。");
        });
    }

    private async void SaveEditor_Click(object sender, RoutedEventArgs e)
    {
        if (_savingEditor || EditorLayer.Visibility != Visibility.Visible) return;
        var persisted = false;
        LibraryPrompt? summarize = null;
        SetEditorSaving(true);
        try
        {
            if (_editorKind == "prompt")
            {
                if (string.IsNullOrWhiteSpace(PromptBody.Text))
                {
                    SetEditorSaving(false); PromptBody.Focus();
                    throw new InvalidOperationException("请填写提示词正文后重试。");
                }
                var item = _editingPrompt!;
                var expected = _editingPromptWasSaved ? item : null;
                var draft = new LibraryPrompt { Id = item.Id, CreatedAt = item.CreatedAt, Title = PromptTitle.Text,
                    Description = PromptDescription.Text, Content = PromptBody.Text,
                    Tags = PromptTags.Text.Split([',', '，'], StringSplitOptions.RemoveEmptyEntries).ToList() };
                var save = Task.Run(() =>
                {
                    var state = _store.Read();
                    var isNew = state.Prompts.All(p => p.Id != item.Id);
                    return (Saved: _store.SavePrompt(draft, expected), AutoSummarize: isNew && state.Ai.AutoSummarize && state.Ai.Model.Length > 0);
                });
                _editorSaveTask = save;
                var result = await save;
                persisted = true;
                if (_shutdown) return;
                CloseEditor(); _visiblePrompts = 30; RefreshViews();
                // 筛选或分页隐藏了刚保存的条目时，恢复完整列表并定位到它。
                if (_promptItems.All(p => p.Id != result.Saved.Id))
                {
                    _selectedTags.Clear(); TagFilters.UnselectAll(); PromptSearch.Clear();
                    _visiblePrompts = Math.Max(30, LibraryStore.FilterPrompts(_store.Read(), "", []).ToList().FindIndex(p => p.Id == result.Saved.Id) + 1);
                    RefreshViews();
                }
                PromptList.SelectedItem = _promptItems.FirstOrDefault(p => p.Id == result.Saved.Id);
                if (PromptList.SelectedItem is not null) PromptList.ScrollIntoView(PromptList.SelectedItem);
                ShowSaveSuccess($"提示词已保存：《{FeedbackLabel(result.Saved.Title)}》。" + (result.AutoSummarize ? "将继续进行 AI 整理。" : ""));
                if (result.AutoSummarize) summarize = result.Saved;
            }
            else if (_editorKind == "model")
            {
                var draft = ModelDraft(); var key = ModelKey.Password; var ids = _editorIds?.ToList();
                var expectedRevision = _editingConnectionWasSaved ? _editingConnection!.Revision : null;
                var save = Task.Run(() => _store.SaveConnection(draft, key, ids, expectedRevision));
                _editorSaveTask = save;
                var saved = await save;
                persisted = true;
                if (_shutdown) return;
                CloseEditor(); RefreshViews();
                if (!ModelList.Items.OfType<LibraryConnection>().Any(c => c.Id == saved.Id))
                { ModelSearch.Clear(); RefreshViews(); }
                ModelList.SelectedItem = ModelList.Items.OfType<LibraryConnection>().FirstOrDefault(c => c.Id == saved.Id);
                if (ModelList.SelectedItem is not null) ModelList.ScrollIntoView(ModelList.SelectedItem);
                var modelLabel = saved.Model.Length > 0 ? $" · {FeedbackLabel(saved.Model)}" : "";
                ShowSaveSuccess($"连接资料已保存：{FeedbackLabel(saved.Platform)}{modelLabel}。");
            }
        }
        catch (Exception ex)
        {
            if (!_shutdown)
                Notice(persisted ? "资料已保存，但列表刷新失败，请重新打开资料库查看。"
                    : "保存失败：" + Error(ex) + " 输入内容已保留，可修改后重新保存。", true);
        }
        finally { SetEditorSaving(false); }
        if (summarize is not null && !_shutdown) await Summarize(summarize);
    }

    private void AiSettings_Click(object sender, RoutedEventArgs e) => OpenAiSettings();

    private void Shortcut_Click(object sender, RoutedEventArgs e)
        => OverTranslate.Views.Shell.ShellWindow.ShowOrActivate(OverTranslate.Views.Shell.ShellPage.Settings).ShowQuickToolSettings();

    private void RefreshShortcut()
    {
        if (!_ready) return;
        var s = SettingsService.Instance.Current;
        ShortcutHint.Text = s.LibraryHotkeyEnabled ? s.LibraryHotkeyDisplay : "快捷键未启用";
    }

}
