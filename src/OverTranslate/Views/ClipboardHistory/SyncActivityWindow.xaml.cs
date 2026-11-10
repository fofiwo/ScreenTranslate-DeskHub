using System.Diagnostics;
using System.IO;
using System.Windows;
using OverTranslate.Services.Lan;

namespace OverTranslate.Views.ClipboardHistory;

public partial class SyncActivityWindow : Window
{
    private readonly SyncActivityStore _store;
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
        RetryButton.IsEnabled = item is { Incoming: false, Kind: SyncKind.Files, Active: false, Sources.Length: > 0 };
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
        if (ActivityList.SelectedItem is SyncActivity { Sources: { } paths })
            await LanTransferWindow.ChooseAndSendAsync(this, paths);
    }
    private void Setup_Click(object sender, RoutedEventArgs e) => LanTransferWindow.Open();
    private void CancelSend_Click(object sender, RoutedEventArgs e)
    { if (ActivityList.SelectedItem is SyncActivity activity) LanTransferWindow.CancelCurrentSend(activity.DeviceId); }
}
