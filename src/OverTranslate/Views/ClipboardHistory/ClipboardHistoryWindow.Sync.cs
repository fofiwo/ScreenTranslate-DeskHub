using System.IO;
using System.Windows;
using OverTranslate.Services.Lan;

namespace OverTranslate.Views.ClipboardHistory;

public partial class ClipboardHistoryWindow
{
    private SyncActivityStore _syncActivities = null!;
    private readonly List<string> _sendPaths = [];
    private bool _inlineSending;
    private string? _sendRecipientId;
    private string? _sendingDeviceId;
    private void InitializeSyncFeedback(SyncActivityStore store)
    {
        _syncActivities = store;
        store.Changed += SyncFeedback_Changed;
        LanTransferWindow.SyncStateChanged += SyncFeedback_Changed;
        Closed += (_, _) => { store.Changed -= SyncFeedback_Changed; LanTransferWindow.SyncStateChanged -= SyncFeedback_Changed; };
        SyncFeedback_Changed(this, EventArgs.Empty);
    }
    private void SyncFeedback_Changed(object? sender, EventArgs e)
    {
        if (_closed || Dispatcher.HasShutdownStarted) return;
        if (!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke(() => SyncFeedback_Changed(sender, e)); return; }
        SyncConnectionText.Text = LanTransferWindow.CurrentSyncStatus.Split('·', 2)[0].Trim();
        RefreshSendRecipients();
        var items = _syncActivities.Snapshot();
        RefreshReceivedFiles();
        var parts = new List<string>();
        foreach (var kind in new[] { SyncKind.Clipboard, SyncKind.Library, SyncKind.Files })
        {
            var item = items.FirstOrDefault(x => x.Kind == kind && x.Active) ?? items.FirstOrDefault(x => x.Kind == kind);
            if (item is null) continue;
            parts.Add($"{item.KindLabel}：{item.StateLabel}" + (item.Active && item.Total > 0 ? $" {100.0 * item.Completed / item.Total:N0}%" : "")
                + $"（{item.Updated.LocalDateTime:HH:mm}）");
        }
        SyncSummaryText.Text = _syncActivities.StorageWarning.Length > 0 ? _syncActivities.StorageWarning
            : parts.Count == 0 ? "点击设备状态，管理连接与同步开关" : string.Join(" · ", parts);
        var active = items.FirstOrDefault(x => x.Active && x.Kind != SyncKind.Connection);
        SyncProgress.Visibility = active is null ? Visibility.Collapsed : Visibility.Visible;
        SyncProgress.IsIndeterminate = active is not null && (active.Total == 0 || active.State is SyncState.Preparing or SyncState.Waiting or SyncState.Confirming);
        SyncProgress.Value = active is { Total: > 0 } ? 100.0 * active.Completed / active.Total : 0;
    }
    private void Sync_DragOver(object sender, System.Windows.DragEventArgs e)
    {
        SendDropZone.Tag = null;
        if (!e.Data.GetDataPresent(System.Windows.DataFormats.FileDrop)) return;
        e.Handled = true;
        e.Effects = _busy || _inlineSending ? System.Windows.DragDropEffects.None : System.Windows.DragDropEffects.Copy;
        if (e.Effects == System.Windows.DragDropEffects.Copy) SendDropZone.Tag = "dragging";
    }
    private void Sync_DragLeave(object sender, System.Windows.DragEventArgs e) => SendDropZone.Tag = null;
    private void Sync_Drop(object sender, System.Windows.DragEventArgs e)
    {
        SendDropZone.Tag = null;
        if (!e.Data.GetDataPresent(System.Windows.DataFormats.FileDrop)) return;
        e.Handled = true;
        if (_busy || _inlineSending) return;
        if (e.Data.GetData(System.Windows.DataFormats.FileDrop) is string[] paths)
        { ShowSendPage(); AddSendSources(paths); }
    }
    private void RefreshSendRecipients()
    {
        if (_inlineSending) return;
        var selected = _sendRecipientId;
        var peers = LanTransferWindow.ConnectedChoices();
        if (SendRecipientBox.ItemsSource is not IReadOnlyList<LanDeviceChoice> old || !old.SequenceEqual(peers))
        {
            SendRecipientBox.ItemsSource = peers;
            SendRecipientBox.SelectedItem = selected is null ? peers.FirstOrDefault() : peers.FirstOrDefault(p => p.Id == selected);
        }
        UpdateSendControls();
    }
    private void SendRecipient_Changed(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (SendRecipientBox.SelectedItem is LanDeviceChoice peer) _sendRecipientId = peer.Id;
        if (SendFilesButton is not null) UpdateSendControls();
    }
    private void UpdateSendControls()
    {
        var peer = SendRecipientBox.SelectedItem as LanDeviceChoice;
        SendConnectionHint.Text = peer is null ? _sendRecipientId is not null
                ? "原接收设备已断开，请重新选择接收设备。" : "暂无在线设备 · 点击上方连接状态进行配对"
            : !peer.Supported ? "对方版本不支持接收确认，请更新后重新连接。" : "仅发送到所选设备";
        SendFilesButton.IsEnabled = !_inlineSending && peer?.Supported == true && _sendPaths.Count is > 0 and <= 100;
        SendFilesButton.Content = _inlineSending ? "正在发送…" : _sendPaths.Count > 0 ? $"发送 {_sendPaths.Count} 项" : "发送文件";
        SendFilesButton.ToolTip = _inlineSending ? "等待对方确认或传输完成，可点击取消传输" : peer?.Supported != true
            ? SendConnectionHint.Text : _sendPaths.Count == 0 ? "请先添加文件或文件夹" : "发送到所选设备，对方确认后开始传输";
        SendRecipientBox.IsEnabled = AddSendFilesButton.IsEnabled = AddSendFoldersButton.IsEnabled = SendDropButton.IsEnabled = SendSourceList.IsEnabled = !_inlineSending;
        ClearSendSourcesButton.IsEnabled = !_inlineSending && _sendPaths.Count > 0;
        SendDropHint.Visibility = _sendPaths.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        SendRecipientEmpty.Visibility = peer is null ? Visibility.Visible : Visibility.Collapsed;
        SendRecipientEmpty.Text = _sendRecipientId is null ? "暂无在线设备" : "请重新选择接收设备";
        CancelInlineSendButton.Visibility = _inlineSending ? Visibility.Visible : Visibility.Collapsed;
    }
    private void AddSendSources(string[] paths)
    {
        var combined = _sendPaths.Concat(paths).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (combined.Length > 100) { SendSelectionHint.Text = "每批最多 100 项，请分批添加。"; return; }
        if (paths.Any(p => !File.Exists(p) && !Directory.Exists(p)))
        { SendSelectionHint.Text = "部分路径无法访问或已不存在，请重新添加。"; return; }
        _sendPaths.Clear(); _sendPaths.AddRange(combined);
        SendSourceList.ItemsSource = _sendPaths.ToArray();
        SendSelectionHint.Text = _sendPaths.Count == 0 ? "尚未添加文件"
            : $"已添加 {_sendPaths.Count} 项 · 每批最多 10000 个文件与文件夹、64 GB";
        RefreshSendRecipients();
    }
    private void AddSendFiles_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || _inlineSending) return;
        _busy = true;
        try
        {
            var dialog = new Microsoft.Win32.OpenFileDialog { Multiselect = true, CheckFileExists = true, Title = "选择要发送的文件" };
            if (dialog.ShowDialog(this) == true) AddSendSources(dialog.FileNames);
        }
        finally { _busy = false; ScheduleDismiss(); }
    }
    private void AddSendFolders_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || _inlineSending) return;
        _busy = true;
        try
        {
            var dialog = new Microsoft.Win32.OpenFolderDialog { Multiselect = true, Title = "选择要发送的文件夹" };
            if (dialog.ShowDialog(this) == true) AddSendSources(dialog.FolderNames);
        }
        finally { _busy = false; ScheduleDismiss(); }
    }
    private void ClearSendSources_Click(object sender, RoutedEventArgs e)
    { if (!_inlineSending) { _sendPaths.Clear(); AddSendSources([]); } }
    private void RemoveSendSource_Click(object sender, RoutedEventArgs e)
    {
        if (_inlineSending || sender is not FrameworkElement { DataContext: string path }) return;
        _sendPaths.RemoveAll(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase));
        AddSendSources([]);
    }
    private async void SendToDevice_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || _inlineSending || _sendPaths.Count is 0 or > 100
            || SendRecipientBox.SelectedItem is not LanDeviceChoice { Supported: true } peer) return;
        if (_sendPaths.Any(p => !File.Exists(p) && !Directory.Exists(p)))
        { SendSelectionHint.Text = "部分路径已不存在，请清空后重新添加。"; return; }
        _busy = _inlineSending = true; _sendingDeviceId = peer.Id; UpdateSendControls();
        SendSelectionHint.Text = "正在准备，等待对方确认接收…";
        try { SendSelectionHint.Text = await LanTransferWindow.SendToDeviceAsync(peer, _sendPaths.ToArray()); }
        finally { _busy = _inlineSending = false; _sendingDeviceId = null; RefreshSendRecipients(); ScheduleDismiss(); }
    }
    private void CancelInlineSend_Click(object sender, RoutedEventArgs e)
    { if (_sendingDeviceId is { } id) LanTransferWindow.CancelCurrentSend(id); }
    private async void SendRecord_Click(object sender, RoutedEventArgs e) => await SendPathsAsync(SelectedRecord?.Files ?? []);
    private async Task SendPathsAsync(string[] paths)
    {
        if (_busy) return;
        _busy = true;
        try
        {
            var sending = LanTransferWindow.ChooseAndSendAsync(this, paths);
            _busy = false;
            await sending;
        }
        finally { _busy = false; ScheduleDismiss(); }
    }
    private void SyncSettings_Click(object sender, RoutedEventArgs e) => LanTransferWindow.Open();
    private void SyncHistory_Click(object sender, RoutedEventArgs e)
    {
        if (_busy && !_inlineSending) return;
        _busy = true;
        try { new SyncActivityWindow(_syncActivities) { Owner = this }.ShowDialog(); }
        finally { _busy = _inlineSending; ScheduleDismiss(); }
    }
}
