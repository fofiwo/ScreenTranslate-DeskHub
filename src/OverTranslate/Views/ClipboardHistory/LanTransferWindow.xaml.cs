using System.ComponentModel;
using System.IO;
using System.Net;
using System.Windows;
using OverTranslate.Services;
using OverTranslate.Services.Lan;
using OverTranslate.Services.Library;
using OverTranslate.Views.Controls;

namespace OverTranslate.Views.ClipboardHistory;

public partial class LanTransferWindow : Window
{
    private static LanTransferWindow? _current;
    private readonly LanNode _node;
    private readonly string _preferencesPath;
    private readonly bool _useClipboard;
    private readonly Func<IReadOnlyList<IPAddress>> _readAddresses;
    private readonly Action<System.Windows.DataObject> _writeClipboard;
    private IPAddress? _listeningAddress;
    private LanPreferences _preferences;
    private LanClipboardSync? _clipboard;
    private LanLibrarySync? _library;
    private CancellationTokenSource? _files;
    private bool _ready;
    private bool _shutdown;
    private bool _connecting;
    private bool _disconnecting;
    private int _connectionRequest;
    internal bool IsSending => _files is not null;

    public LanTransferWindow() : this(LanPreferences.DefaultPath, true) { }

    internal LanTransferWindow(string preferencesPath, bool useClipboard = false,
        Func<IReadOnlyList<IPAddress>>? readAddresses = null, Action<System.Windows.DataObject>? writeClipboard = null,
        LanTrustedDevice? trusted = null)
    {
        _trusted = trusted;
        if (trusted is not null) _deviceId = trusted.Id;
        _preferencesPath = preferencesPath;
        _useClipboard = useClipboard;
        _readAddresses = readAddresses ?? LanNode.LocalAddresses;
        _writeClipboard = writeClipboard ?? (data => System.Windows.Clipboard.SetDataObject(data, true));
        string? loadError = null;
        try { _preferences = LanPreferences.Load(preferencesPath); }
        catch (Exception) { _preferences = new(LanPreferences.DefaultFolder); loadError = "读取接收设置失败，请重新选择并保存文件夹。"; }
        if (trusted is not null) _preferences = _preferences with { AutoSync = trusted.Clipboard, AutoSyncLibrary = trusted.Library };
        _node = new(Environment.MachineName, () => _preferences.ReceiveFolder);
        _activities = useClipboard ? SyncActivityStore.Default : new(Path.Combine(Path.GetDirectoryName(preferencesPath)!, "sync-activity.json"));
        _node.Activities = _activities;
        _node.ActivityDeviceId = _deviceId;
        _node.ConfirmFilesAsync = ConfirmFilesAsync;
        _node.ClipboardEnabled = _preferences.AutoSync;
        _node.LibraryEnabled = _preferences.AutoSyncLibrary;
        InitializeComponent();
        RememberDeviceBox.IsChecked = trusted?.AutoReconnect ?? true;
        Icon = AppIconService.CreateCompactIcon();
        ReceiveFolderBox.Text = _preferences.ReceiveFolder;
        AutoSyncBox.IsChecked = _preferences.AutoSync;
        LibrarySyncBox.IsChecked = _preferences.AutoSyncLibrary;
        RefreshLocalAddresses();
        LocalAddressBox.DropDownOpened += (_, _) => RefreshLocalAddresses();
        _node.ConfirmPairAsync = async (peer, token) => await Dispatcher.InvokeAsync(() =>
        {
            token.ThrowIfCancellationRequested();
            if (_trusted is not null) return _trusted.Name == peer.Name;
            if (!IsVisible) Show();
            Activate();
            return AppMessageWindow.ShowMessage(this, $"设备 {peer.Name}（{peer.Address}）请求配对。\n允许同步已勾选的内容（若开启资料库同步，将包括模型 API Key）。\n勾选“记住此设备”会加密保存信任信息并在重启后自动重连。\n文件批次会单独询问是否接收及保存位置。",
                "确认局域网配对", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) == MessageBoxResult.Yes;
        }, System.Windows.Threading.DispatcherPriority.Normal, token);
        _node.Changed += Node_Changed;
        _node.ClipboardNotice += (_, message) => ShowClipboardStatus(message);
        _node.Progress += Node_Progress;
        _ready = true;
        if (loadError is not null) ConnectionStatus.Text = loadError;
        UpdateControls(); UpdateClipboardAvailability();
        LibraryStatus.Text = _preferences.AutoSyncLibrary ? "已开启 · 等待连接" : "未开启";
        if (useClipboard) { Connections.Add(this); ClipboardRelay.Add(_node); }
    }

    internal void RefreshLocalAddresses()
    {
        var selected = LocalAddressBox.SelectedItem as IPAddress;
        try
        {
            var addresses = _readAddresses();
            LocalAddressBox.ItemsSource = addresses;
            LocalAddressBox.SelectedItem = addresses.FirstOrDefault(a => a.Equals(selected)) ?? addresses.FirstOrDefault();
            LocalAddressStatus.Text = "未找到局域网 IPv4 地址，请连接以太网或 Wi-Fi 后重新展开地址列表。";
            LocalAddressStatus.Visibility = addresses.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }
        catch (Exception)
        {
            LocalAddressBox.ItemsSource = Array.Empty<IPAddress>();
            LocalAddressStatus.Text = "读取本机地址失败，请检查网络后重新展开地址列表。";
            LocalAddressStatus.Visibility = Visibility.Visible;
        }
        UpdateControls();
    }

    public static void Open()
    {
        RestoreConnections();
        DeviceSyncWindow.Open();
    }

    private void EnsureClipboard()
    {
        if (!_useClipboard) return;
        if (_clipboard is null)
        {
            _clipboard = new(Dispatcher, _node);
            _clipboard.Notice += (_, message) => ShowClipboardStatus(message);
            _node.ApplyClipboardAsync = _clipboard.ApplyAsync;
        }
        _clipboard.Enabled = _preferences.AutoSync;
        EnsureLibrarySync();
    }

    private void EnsureLibrarySync()
    {
        if (!_useClipboard || _library is not null) return;
        _library = new(_node, () => SyncLibrary.Value);
        _library.Changed += LibrarySyncNotifications.NotifyChanged;
        _library.StatusChanged += message =>
        {
            if (!Dispatcher.HasShutdownStarted) Dispatcher.BeginInvoke(() =>
            { if (!_shutdown) LibraryStatus.Text = _preferences.AutoSyncLibrary ? $"{DateTime.Now:HH:mm:ss}  {message}" : "资料库同步已暂停，已有资料保留。"; });
        };
    }

    internal void EnableUnifiedSync()
    {
        // 首次引导只有在偏好真正落盘后才能记为完成，失败时保留原开关并由引导提供重试。
        var preferences = _preferences with { AutoSync = true, AutoSyncLibrary = true };
        preferences.Save(_preferencesPath);
        _preferences = preferences;
        _ready = false;
        try
        {
            AutoSyncBox.IsChecked = true;
            LibrarySyncBox.IsChecked = true;
        }
        finally { _ready = true; }
        _node.ClipboardEnabled = _node.LibraryEnabled = true;
        EnsureClipboard();
        UpdatePairingInfo(); UpdateClipboardAvailability();
        LibraryStatus.Text = "已开启资料库同步，配对后自动合并并持续同步。";
        TransferStatus.Text = "设置已保存。";
    }

    public static void OpenForSyncSetup()
    {
        OpenPairing();
        if (_current is null) return;
        _current.EnableUnifiedSync();
        if (!_current._node.IsConnected && !_current._node.IsListening && _current.LocalAddressBox.SelectedItem is IPAddress)
            _current.Listen_Click(_current, new RoutedEventArgs());
    }

    private void LibrarySync_Changed(object sender, RoutedEventArgs e)
    {
        if (!_ready) return;
        _preferences = _preferences with { AutoSyncLibrary = LibrarySyncBox.IsChecked == true };
        _node.LibraryEnabled = _preferences.AutoSyncLibrary;
        if (_preferences.AutoSyncLibrary) EnsureLibrarySync();
        LibraryStatus.Text = _preferences.AutoSyncLibrary ? "已开启资料库同步，配对后自动合并并持续同步。" : "资料库同步已暂停，已有资料保留。";
        SavePreferences();
    }

    private async void Listen_Click(object sender, RoutedEventArgs e)
    {
        if (_connecting || _disconnecting || _shutdown || IsSending) return;
        if (LocalAddressBox.SelectedItem is not IPAddress address) { ConnectionStatus.Text = "未找到局域网 IPv4 地址，请检查网络连接。"; return; }
        var request = ++_connectionRequest;
        _connecting = true; UpdateControls();
        try
        {
            EnsureClipboard();
            _listeningAddress = null;
            await _node.ListenAsync(address, 0);
            if (request != _connectionRequest || _shutdown) return;
            _listeningAddress = address;
            PairingCopyStatus.Text = "已生成，复制给另一台电脑";
        }
        catch (Exception) { if (request == _connectionRequest && !_shutdown) ConnectionStatus.Text = "无法等待配对，请检查网络和防火墙后重试。"; }
        finally { if (request == _connectionRequest) _connecting = false; UpdateControls(); }
    }

    private async void Connect_Click(object sender, RoutedEventArgs e)
    {
        if (_connecting || _disconnecting || _shutdown || IsSending) return;
        var parts = RemoteAddressBox.Text.Trim().Split(':');
        if (parts.Length != 2 || !IPAddress.TryParse(parts[0], out var address) || !LanNode.IsLocalAddress(address)
            || !int.TryParse(parts[1], out var port) || port is < 1 or > 65535)
        { ConnectionStatus.Text = "请输入对方显示的局域网 IPv4:端口。"; return; }
        var request = ++_connectionRequest;
        _connecting = true; UpdateControls();
        try
        {
            EnsureClipboard(); ConnectionStatus.Text = "正在连接，等待对方确认配对…";
            _remoteAddress = address; _remotePort = port; _remotePairCode = PairCodeBox.Text;
            _listeningAddress = null;
            await _node.ConnectAsync(address, port, PairCodeBox.Text);
            if (request != _connectionRequest || _shutdown) return;
            PairingInfo.Text = "已作为连接方配对。";
            PairCodeBox.Clear();
        }
        catch (Exception) { if (request == _connectionRequest && !_shutdown) ConnectionStatus.Text = "连接失败或对方未接受。请核对地址、配对码、防火墙，并确认两台均已更新到相同新版。"; }
        finally { if (request == _connectionRequest) _connecting = false; UpdateControls(); }
    }

    private async void Disconnect_Click(object sender, RoutedEventArgs e)
    {
        if (_disconnecting || _shutdown) return;
        ++_connectionRequest; _connecting = false; _disconnecting = true; UpdateControls();
        await PauseReconnectAsync();
        _files?.Cancel();
        try
        {
            await _node.StopAsync();
            PairingInfo.Text = "开始等待后显示地址和配对码。";
            ConnectionStatus.Text = "已断开，自动同步和未完成传输已停止。";
        }
        catch (Exception) { ConnectionStatus.Text = "断开连接未完成，请退出程序后重试。"; }
        finally { _clipboard?.Dispose(); _clipboard = null; _disconnecting = false; UpdateControls(); }
    }

    private void Node_Changed(object? sender, string message)
    {
        if (!Dispatcher.HasShutdownStarted) Dispatcher.BeginInvoke(() =>
        {
            if (_shutdown) return;
            _deviceStatus = message;
            if (_node.IsConnected)
            {
                try { SaveTrust(); }
                catch (Exception) { _deviceStatus = "已连接，但信任保存失败；重启后需要重新配对。"; }
                _activities.Update(new("connection-" + _deviceId, SyncKind.Connection, false, _node.PeerName, SyncState.Completed, DeviceId: _deviceId));
            }
            else if (_trusted is { } profile)
            {
                _deviceStatus = !profile.AutoReconnect ? "已断开 · 自动重连已暂停"
                    : _node.IsListening ? "设备离线 · 等待已信任设备自动连接" : "设备离线 · 准备自动重连";
                _activities.Update(new("connection-" + _deviceId, SyncKind.Connection, false, profile.Name,
                    profile.AutoReconnect ? SyncState.Interrupted : SyncState.Cancelled, Name: _deviceStatus, DeviceId: _deviceId));
                if (!profile.AutoReconnect && _node.IsListening) _ = _node.StopAsync();
            }
            ConnectionStatus.Text = _deviceStatus; UpdateClipboardAvailability(); UpdateControls();
        });
    }

    internal void ShowClipboardStatus(string message)
    {
        if (Dispatcher.HasShutdownStarted) return;
        Dispatcher.BeginInvoke(() =>
        {
            if (!_shutdown) ClipboardStatus.Text = $"{DateTime.Now:HH:mm:ss}  {message}";
        });
    }

    private void UpdateClipboardAvailability() => ClipboardStatus.Text = !_preferences.AutoSync
        ? "已暂停 · 本机复制不受影响"
        : _node.IsConnected ? "已开启 · 等待新内容"
        : "未连接";

    private void Node_Progress(object? sender, LanProgress progress)
    {
        if (!Dispatcher.HasShutdownStarted) Dispatcher.BeginInvoke(() =>
        {
            if (_shutdown) return;
            TransferStatus.Text = $"{progress.Phase}：{progress.Name}（{progress.Completed:N0} / {progress.Total:N0} 字节）";
            TransferProgress.Value = progress.Total == 0 ? 100 : 100.0 * progress.Completed / progress.Total;
            if (progress.Phase is "已接收" or "已送达")
            {
                TransferLog.Items.Insert(0, $"{DateTime.Now:t}  {progress.Phase}：{progress.Name}");
                while (TransferLog.Items.Count > 50) TransferLog.Items.RemoveAt(TransferLog.Items.Count - 1);
            }
        });
    }

    private void AutoSync_Changed(object sender, RoutedEventArgs e)
    {
        if (!_ready) return;
        _preferences = _preferences with { AutoSync = AutoSyncBox.IsChecked == true };
        _node.ClipboardEnabled = _preferences.AutoSync;
        if (_clipboard is not null) _clipboard.Enabled = _preferences.AutoSync;
        UpdateClipboardAvailability();
        SavePreferences();
    }

    private void ChooseFolder_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog { Title = "选择文件接收文件夹", Multiselect = false };
        if (dialog.ShowDialog(this) == true) { ReceiveFolderBox.Text = dialog.FolderName; SaveFolder(); }
    }

    private void PairingInfo_PreviewExecuted(object sender, System.Windows.Input.ExecutedRoutedEventArgs e)
    {
        if (e.Command != System.Windows.Input.ApplicationCommands.Copy) return;
        e.Handled = true;
        if (PairingInfo.SelectionLength == 0) return;
        CopyPairingText(PairingInfo.SelectedText);
    }

    private void CopyPairing_Click(object sender, RoutedEventArgs e)
    {
        UpdatePairingInfo();
        if (CopyPairingButton.IsEnabled) CopyPairingText(PairingInfo.Text);
    }

    private void CopyPairingText(string text)
    {
        try
        {
            var data = new System.Windows.DataObject();
            data.SetText(text);
            data.SetData("ExcludeClipboardContentFromMonitorProcessing", new MemoryStream(BitConverter.GetBytes(1)));
            data.SetData("CanIncludeInClipboardHistory", new MemoryStream(BitConverter.GetBytes(0)));
            data.SetData("CanUploadToCloudClipboard", new MemoryStream(BitConverter.GetBytes(0)));
            _writeClipboard(data);
            PairingCopyStatus.Text = "已复制，可发给另一台电脑";
        }
        catch (Exception) { PairingCopyStatus.Text = "复制失败，请重试"; }
    }

    private void UpdatePairingInfo()
    {
        var available = !_shutdown && !_connecting && !_disconnecting && _node.IsListening
            && !_node.IsConnected && _listeningAddress is not null && _trusted is null;
        CopyPairingButton.IsEnabled = available;
        if (available)
            PairingInfo.Text = BuildPairingInfo(Environment.MachineName, $"{_listeningAddress}:{_node.Port}", _node.PairCode, _preferences);
        else PairingInfo.Text = _node.IsConnected ? "已连接，无需再次配对" : _trusted is not null ? "此设备已记住，可在设备列表重试连接；重新配对请先忘记设备。"
            : _connecting ? "正在连接…" : "生成后显示地址和配对码";
    }

    internal static string BuildPairingInfo(string device, string address, string code, LanPreferences preferences) =>
        $"设备：{device} · v{typeof(LanTransferWindow).Assembly.GetName().Version?.ToString(3)}\n"
        + $"对方地址：{address}\n完整配对码：{code}\n"
        + $"剪贴板：{(preferences.AutoSync ? "开启" : "暂停")}\n"
        + $"提示词与模型库：{(preferences.AutoSyncLibrary ? "开启（含 API Key）" : "暂停")}";
    private void SaveFolder_Click(object sender, RoutedEventArgs e) => SaveFolder();
    private void SaveFolder()
    {
        try
        {
            var folder = LanFiles.ValidateFolder(ReceiveFolderBox.Text.Trim());
            Directory.CreateDirectory(folder);
            _preferences = _preferences with { ReceiveFolder = folder };
            SavePreferences();
        }
        catch (Exception) { TransferStatus.Text = "接收文件夹不可用，请检查路径与写入权限。"; }
    }
    private void SavePreferences()
    {
        UpdatePairingInfo();
        NotifySyncState();
        RememberCurrentPreferences();
        try { _preferences.Save(_preferencesPath); TransferStatus.Text = "设置已保存。"; }
        catch (Exception) { TransferStatus.Text = "设置仅在当前运行中生效，保存失败，请检查目录权限后重试。"; }
    }

    private async void ChooseFiles_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog { Title = "选择要发送的文件", Multiselect = true, CheckFileExists = true };
        if (dialog.ShowDialog(this) == true) await SendFilesAsync(dialog.FileNames);
    }

    private void Files_DragOver(object sender, System.Windows.DragEventArgs e)
    {
        e.Effects = _node.IsConnected && !IsSending && e.Data.GetDataPresent(System.Windows.DataFormats.FileDrop)
            ? System.Windows.DragDropEffects.Copy : System.Windows.DragDropEffects.None;
        e.Handled = true;
    }
    private async void Files_Drop(object sender, System.Windows.DragEventArgs e)
    {
        e.Handled = true;
        if (e.Data.GetData(System.Windows.DataFormats.FileDrop) is string[] paths) await SendFilesAsync(paths);
    }
    internal async Task SendFilesAsync(string[] paths)
    {
        if (IsSending) { TransferStatus.Text = "上一批文件仍在发送，请等待或取消。"; return; }
        if (!_node.IsConnected) { TransferStatus.Text = "请先配对另一台电脑。"; return; }
        if (paths.Length == 0 || paths.Length > 100 || paths.Any(p => !File.Exists(p) && !Directory.Exists(p)))
        { TransferStatus.Text = "请拖入 1–100 个文件或文件夹。"; return; }
        using var request = new CancellationTokenSource();
        var connection = _node.ConnectionVersion;
        _files = request; UpdateControls();
        try
        {
            if (_node.ConnectionVersion != connection) throw new IOException("配对设备已更换。");
            TransferStatus.Text = "正在准备文件与文件夹，随后等待对方选择保存目录…";
            TransferStatus.Text = await _node.SendBundleAsync(paths, request.Token)
                ? $"本批 {paths.Length} 项已全部送达并保存。" : "对方未接收本批文件，请联系对方后重试。";
        }
        catch (OperationCanceledException) { TransferStatus.Text = "传输已取消，已完成文件保留。若连接已断开，请重新配对后继续。"; }
        catch (Exception) { TransferStatus.Text = "文件未全部送达，请检查连接、接收目录权限、磁盘空间、文件名或文件大小后重试。"; }
        finally { _files = null; UpdateControls(); }
    }
    private void CancelFiles_Click(object sender, RoutedEventArgs e) => _files?.Cancel();
    private void UpdateControls()
    {
        UpdatePairingInfo();
        NotifySyncState();
        var canPair = !_shutdown && !_connecting && !_disconnecting && !IsSending && !_node.IsConnected;
        ListenButton.IsEnabled = canPair && _trusted is null && LocalAddressBox.SelectedItem is IPAddress;
        ConnectButton.IsEnabled = canPair && _trusted is null;
        ChooseFilesButton.IsEnabled = _node.IsConnected && !IsSending;
        CancelFilesButton.IsEnabled = IsSending;
    }
    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (!_shutdown) { e.Cancel = true; Hide(); }
    }
    internal async Task ShutdownAsync()
    {
        _shutdown = true; ++_connectionRequest; _files?.Cancel();
        var reconnect = _reconnect; _reconnect = null;
        if (reconnect is not null) await reconnect.DisposeAsync();
        _clipboard?.Dispose(); _clipboard = null;
        _node.Changed -= Node_Changed; _node.Progress -= Node_Progress;
        Close(); if (ReferenceEquals(_current, this)) _current = null;
        Connections.Remove(this);
        ClipboardRelay.Remove(_node);
        NotifySyncState();
        await _node.DisposeAsync();
        if (_library is not null) { await _library.DisposeAsync(); _library = null; }
    }
    internal static Task ShutdownCurrentAsync() => Task.WhenAll(Connections.ToArray().Select(w => w.ShutdownAsync()));
}
