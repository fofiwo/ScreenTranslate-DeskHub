using System.Windows;
using OverTranslate.Services.Lan;

namespace OverTranslate.Views.ClipboardHistory;

public partial class ClipboardHistoryWindow
{
    private SyncActivityStore _syncActivities = null!;
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
        SyncConnectionText.Text = LanTransferWindow.CurrentSyncStatus;
        var items = _syncActivities.Snapshot();
        var parts = new List<string>();
        foreach (var kind in new[] { SyncKind.Clipboard, SyncKind.Library, SyncKind.Files })
        {
            var item = items.FirstOrDefault(x => x.Kind == kind && x.Active) ?? items.FirstOrDefault(x => x.Kind == kind);
            if (item is null) continue;
            parts.Add($"{item.KindLabel}：{item.StateLabel}" + (item.Active && item.Total > 0 ? $" {100.0 * item.Completed / item.Total:N0}%" : "")
                + $"（{item.Updated.LocalDateTime:HH:mm}）");
        }
        SyncSummaryText.Text = _syncActivities.StorageWarning.Length > 0 ? _syncActivities.StorageWarning
            : parts.Count == 0 ? "拖入文件 / 文件夹发送 · 点击上方连接状态查看同步记录" : string.Join("\n", parts);
        var active = items.FirstOrDefault(x => x.Active && x.Kind != SyncKind.Connection);
        SyncProgress.Visibility = active is null ? Visibility.Collapsed : Visibility.Visible;
        SyncProgress.IsIndeterminate = active is not null && (active.Total == 0 || active.State is SyncState.Preparing or SyncState.Waiting or SyncState.Confirming);
        SyncProgress.Value = active is { Total: > 0 } ? 100.0 * active.Completed / active.Total : 0;
    }
    private void Sync_DragOver(object sender, System.Windows.DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(System.Windows.DataFormats.FileDrop)) return;
        e.Handled = true;
        e.Effects = _busy ? System.Windows.DragDropEffects.None : System.Windows.DragDropEffects.Copy;
    }
    private async void Sync_Drop(object sender, System.Windows.DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(System.Windows.DataFormats.FileDrop)) return;
        e.Handled = true;
        if (e.Data.GetData(System.Windows.DataFormats.FileDrop) is string[] paths) await SendPathsAsync(paths);
    }
    private async void SendToDevice_Click(object sender, RoutedEventArgs e) => await SendPathsAsync([]);
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
        if (_busy) return;
        _busy = true;
        try { new SyncActivityWindow(_syncActivities) { Owner = this }.ShowDialog(); }
        finally { _busy = false; ScheduleDismiss(); }
    }
}
