using System.Diagnostics;
using System.IO;
using System.Windows;
using OverTranslate.Services.Lan;

namespace OverTranslate.Views.ClipboardHistory;

public partial class SyncActivityWindow : Window
{
    private readonly SyncActivityStore _store;
    private bool _retrying;
    internal SyncActivityWindow(SyncActivityStore store)
    {
        _store = store;
        InitializeComponent();
        _store.Changed += Refresh;
        LanTransferWindow.SyncStateChanged += Refresh;
        Closed += (_, _) => { _store.Changed -= Refresh; LanTransferWindow.SyncStateChanged -= Refresh; };
        Refresh(this, EventArgs.Empty);
    }
    private void Refresh(object? sender, EventArgs e)
    {
        if (!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke(() => Refresh(sender, e)); return; }
        var selected = ActivityList.SelectedItem as SyncActivity;
        var items = _store.Snapshot();
        ActivityList.ItemsSource = items;
        ActivityList.SelectedItem = items.FirstOrDefault(x => x.Id == selected?.Id && x.Incoming == selected.Incoming && x.DeviceId == selected.DeviceId);
        EmptyHint.Visibility = items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        StorageWarning.Text = _store.StorageWarning;
        CurrentStatus.Text = LanTransferWindow.CurrentSyncStatus;
    }
    private void Selection_Changed(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        var item = ActivityList.SelectedItem as SyncActivity;
        OpenFolderButton.IsEnabled = item is { Incoming: true, State: SyncState.Completed, Folder.Length: > 0 };
        RetryButton.IsEnabled = !_retrying && item is { Incoming: false, Kind: SyncKind.Files, Active: false, Sources.Length: > 0 };
        CancelSendButton.IsEnabled = item is { Incoming: false, Kind: SyncKind.Files, Active: true };
    }
    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        if (ActivityList.SelectedItem is not SyncActivity item) return;
        try
        {
            var folder = LanFiles.ValidateFolder(item.Folder);
            if (!Directory.Exists(folder)) throw new DirectoryNotFoundException();
            Process.Start(new ProcessStartInfo("explorer.exe") { ArgumentList = { folder }, UseShellExecute = true });
        }
        catch (Exception) { StorageWarning.Text = "无法打开接收目录，目录可能已移动或删除。"; }
    }
    private async void Retry_Click(object sender, RoutedEventArgs e)
    {
        if (_retrying || ActivityList.SelectedItem is not SyncActivity { Incoming: false, Kind: SyncKind.Files, Active: false, Sources.Length: > 0 } item) return;
        _retrying = true; Selection_Changed(this, null!);
        RetryFeedback.Text = "正在重新发送，请选择接收设备并等待对方确认…";
        try
        {
            var result = await LanTransferWindow.ChooseAndSendAsync(this, item.Sources!);
            RetryFeedback.Text = result is null ? "已取消选择，未开始重新发送。" : $"重新发送结果：{result}";
        }
        catch (Exception) { RetryFeedback.Text = "重新发送失败，请检查文件、设备连接与磁盘后重试。"; }
        finally { _retrying = false; Selection_Changed(this, null!); }
    }
    private void Setup_Click(object sender, RoutedEventArgs e) => LanTransferWindow.Open();
    private void CancelSend_Click(object sender, RoutedEventArgs e)
    { if (ActivityList.SelectedItem is SyncActivity activity) LanTransferWindow.CancelCurrentSend(activity.DeviceId); }
}
