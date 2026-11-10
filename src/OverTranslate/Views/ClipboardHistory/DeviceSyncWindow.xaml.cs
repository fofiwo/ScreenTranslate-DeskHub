using System.Windows;
using OverTranslate.Services.Lan;

namespace OverTranslate.Views.ClipboardHistory;

public partial class DeviceSyncWindow : Window
{
    private static DeviceSyncWindow? _current;
    private readonly Func<IReadOnlyList<LanDeviceRow>> _readRows;
    private readonly SyncActivityStore _activities;
    private bool _closed;
    internal DeviceSyncWindow(Func<IReadOnlyList<LanDeviceRow>>? readRows = null, SyncActivityStore? activities = null)
    {
        _readRows = readRows ?? LanTransferWindow.DeviceRows;
        _activities = activities ?? SyncActivityStore.Default;
        InitializeComponent();
        LanTransferWindow.SyncStateChanged += Refresh;
        _activities.Changed += Refresh;
        Closed += (_, _) =>
        {
            _closed = true; LanTransferWindow.SyncStateChanged -= Refresh; _activities.Changed -= Refresh;
            if (ReferenceEquals(_current, this)) _current = null;
        };
        Refresh(this, EventArgs.Empty);
    }
    internal static void Open()
    {
        _current ??= new();
        if (!_current.IsVisible) _current.Show();
        _current.WindowState = WindowState.Normal; _current.Activate();
    }
    private void Refresh(object? sender, EventArgs e)
    {
        if (_closed || Dispatcher.HasShutdownStarted) return;
        if (!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke(() => Refresh(sender, e)); return; }
        var selected = (DeviceList.SelectedItem as LanDeviceRow)?.Id;
        var rows = _readRows();
        DeviceList.ItemsSource = rows;
        DeviceList.SelectedItem = rows.FirstOrDefault(r => r.Id == selected);
        EmptyHint.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        OnlineStatus.Text = $"{rows.Count(r => r.IsConnected)} / {rows.Count} 台在线";
        WarningText.Text = LanTransferWindow.TrustError;
    }
    private string? Selected => (DeviceList.SelectedItem as LanDeviceRow)?.Id;
    private void Selection_Changed(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        SettingsButton.IsEnabled = RetryButton.IsEnabled = DisconnectButton.IsEnabled = ForgetButton.IsEnabled = Selected is not null;
    }
    private void Add_Click(object sender, RoutedEventArgs e) => LanTransferWindow.OpenPairing();
    private void Settings_Click(object sender, RoutedEventArgs e) { if (Selected is { } id) LanTransferWindow.OpenPairing(id); }
    private async void Retry_Click(object sender, RoutedEventArgs e) { if (Selected is { } id) await LanTransferWindow.RetryDeviceAsync(id); }
    private async void Disconnect_Click(object sender, RoutedEventArgs e) { if (Selected is { } id) await LanTransferWindow.DisconnectDeviceAsync(id); }
    private async void Forget_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is { } id && OverTranslate.Views.Controls.AppMessageWindow.ShowMessage(this, "忘记后将断开连接，并清除本机保存的设备信任；再次连接需要双方重新配对。", "忘记设备",
            MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) == MessageBoxResult.Yes)
            await LanTransferWindow.ForgetDeviceAsync(id);
    }
    private void History_Click(object sender, RoutedEventArgs e) => new SyncActivityWindow(_activities) { Owner = this }.ShowDialog();
}
