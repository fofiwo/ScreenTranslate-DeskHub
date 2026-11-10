using System.IO;
using System.Net;
using System.Windows;
using OverTranslate.Services;
using OverTranslate.Services.Lan;

namespace OverTranslate.Views.ClipboardHistory;

internal sealed record LanDeviceChoice(string Id, string Display, bool Supported, long ConnectionVersion = 0);
internal sealed record LanDeviceRow(string Id, string Name, string Address, string Status, string Sync, string LastResult, bool AutoReconnect,
    bool IsConnected = false, DateTimeOffset LastConnected = default)
{
    public string ReconnectLabel => AutoReconnect ? "自动重连：开启" : "自动重连：暂停";
    public string LastConnectedLabel => LastConnected == default ? "尚未记录成功连接" : $"最近连接：{LastConnected.LocalDateTime:yyyy-MM-dd HH:mm:ss}";
}

public partial class LanTransferWindow
{
    private static readonly List<LanTransferWindow> Connections = [];
    private static readonly LanClipboardRelay ClipboardRelay = new();
    private static readonly Lazy<OverTranslate.Services.Library.LibraryStore> SyncLibrary = new(
        () => new(OverTranslate.Services.Library.LibraryStore.DefaultPath), LazyThreadSafetyMode.PublicationOnly);
    private static readonly Lazy<LanTrustedDevices> TrustStore = new(() => new(Path.Combine(AppDataPaths.Root, "trusted-devices.dat")));
    private static bool _restored;
    private readonly string _deviceId = Guid.NewGuid().ToString("N");
    private LanTrustedDevice? _trusted;
    private LanReconnectLoop? _reconnect;
    private string _deviceStatus = "尚未配对";
    private IPAddress? _remoteAddress;
    private int _remotePort;
    private string _remotePairCode = "";

    public static void RestoreConnections()
    {
        if (_restored) return;
        _restored = true;
        foreach (var profile in TrustStore.Value.Load())
        {
            var window = new LanTransferWindow(LanPreferences.DefaultPath, true, trusted: profile);
            window._deviceStatus = profile.AutoReconnect ? "等待自动重连" : "已暂停自动重连";
            if (profile.AutoReconnect) window.StartReconnect();
        }
        SyncStateChanged?.Invoke(null, EventArgs.Empty);
    }
    internal static string TrustError => TrustStore.IsValueCreated ? TrustStore.Value.Error : "";
    internal static IReadOnlyList<LanDeviceRow> DeviceRows() => Connections.Select(w =>
    {
        var peer = w._node.IsConnected ? w._node.PeerName : w._trusted?.Name ?? "新设备 · 待配对";
        var result = w._activities.Snapshot().FirstOrDefault(x => x.Peer == peer && x.Kind != SyncKind.Connection);
        var endpoint = w._trusted is { } p ? $"{p.Address}:{p.Port} · {(p.Listening ? "本机等待连接 · 对方 " + p.LastPeerAddress : "连接对方")}" : "尚未保存地址";
        return new LanDeviceRow(w._deviceId, peer, endpoint, w._deviceStatus,
            $"剪贴板{(w._node.ClipboardEnabled ? "开启" : "暂停")} · 资料库{(w._node.LibraryEnabled ? "开启" : "暂停")}",
            result is null ? "还没有同步结果" : $"{result.KindLabel}：{result.StateLabel} · {result.Updated.LocalDateTime:MM-dd HH:mm}",
            w._trusted?.AutoReconnect == true, w._node.IsConnected, w._trusted?.LastConnected ?? default);
    }).ToArray();
    internal static IReadOnlyList<LanDeviceChoice> ConnectedChoices() => Connections.Where(w => w._node.IsConnected)
        .Select(w => new LanDeviceChoice(w._deviceId, $"{w._node.PeerName} · {w._node.PeerAddress}", w._node.SupportsFileOffers, w._node.ConnectionVersion)).ToArray();
    internal static void OpenPairing(string? id = null)
    {
        RestoreConnections();
        var window = id is null ? Connections.FirstOrDefault(w => w._trusted is null && !w._node.IsConnected && !w._node.IsListening)
            : Connections.FirstOrDefault(w => w._deviceId == id);
        if (window is null)
        {
            if (Connections.Count >= 16) { OverTranslate.Views.Controls.AppMessageWindow.ShowMessage("最多保存 16 台设备，请先忘记不用的设备。"); return; }
            window = new();
        }
        _current = window;
        if (!window.IsVisible) window.Show();
        window.WindowState = WindowState.Normal;
        window.Activate(); window.NotifySyncState();
    }
    private void SaveTrust()
    {
        if (!_useClipboard || !_node.IsConnected) return;
        if (!_node.SupportsReconnect)
        { _deviceStatus = "已连接 · 对方版本需更新后才能记住设备并自动重连"; return; }
        if (_trusted is null && RememberDeviceBox.IsChecked != true) return;
        var listener = _node.IsListening;
        var identity = listener ? _trusted?.Identity ?? _node.ExportListenIdentity() : null;
        _trusted = new(_deviceId, _node.PeerName, listener,
            (listener ? _listeningAddress : _remoteAddress)?.ToString() ?? _trusted?.Address ?? "",
            listener ? _node.Port : _remotePort,
            listener ? _node.PairCode : _remotePairCode, identity, _node.ClipboardEnabled, _node.LibraryEnabled,
            RememberDeviceBox.IsChecked == true, DateTimeOffset.Now, _node.PeerAddress);
        PersistTrust();
        if (_trusted.AutoReconnect) StartReconnect();
    }
    private void PersistTrust()
    {
        try { TrustStore.Value.Save(Connections.Select(w => w._trusted).OfType<LanTrustedDevice>()); }
        catch (Exception) { _deviceStatus = TrustStore.Value.Error; ConnectionStatus.Text = _deviceStatus; }
    }
    private void StartReconnect()
    {
        if (!_useClipboard || _trusted is not { AutoReconnect: true } || _reconnect is not null || _shutdown) return;
        _reconnect = new(() => _node.IsConnected || _node.IsListening,
            async token =>
            {
                if (_shutdown || _trusted is not { } profile) return;
                EnsureClipboard();
                if (profile.Listening)
                {
                    var addresses = _readAddresses();
                    var address = addresses.FirstOrDefault(a => a.ToString() == profile.Address) ?? addresses.FirstOrDefault()
                        ?? throw new IOException("没有可用局域网地址。");
                    _listeningAddress = address;
                    await _node.ListenAsync(address, profile.Port, profile.Identity);
                    _deviceStatus = "等待已信任设备连接";
                    NotifySyncState();
                }
                else
                {
                    _remoteAddress = IPAddress.Parse(profile.Address); _remotePort = profile.Port; _remotePairCode = profile.PairCode;
                    try { await _node.ConnectAsync(_remoteAddress, profile.Port, profile.PairCode, token); }
                    catch (Exception) when (!token.IsCancellationRequested)
                    {
                        // 仍以已保存的证书指纹认证；主机名仅用于寻找变化后的局域网地址。
                        var addresses = await Dns.GetHostAddressesAsync(profile.Name, token);
                        var candidates = addresses.Where(a => LanNode.IsLocalAddress(a) && !a.Equals(_remoteAddress)).Take(3).ToArray();
                        if (candidates.Length == 0) throw;
                        Exception? failure = null;
                        foreach (var candidate in candidates)
                        {
                            _remoteAddress = candidate;
                            try { await _node.ConnectAsync(candidate, profile.Port, profile.PairCode, token); failure = null; break; }
                            catch (Exception ex) when (!token.IsCancellationRequested) { failure = ex; }
                        }
                        if (failure is not null) throw failure;
                    }
                }
            }, message =>
            {
                _deviceStatus = message; ConnectionStatus.Text = message;
                _activities.Update(new("connection-" + _deviceId, SyncKind.Connection, false, _trusted?.Name ?? "设备",
                    message.StartsWith("正在") ? SyncState.Waiting : SyncState.Failed, Name: message, DeviceId: _deviceId));
                NotifySyncState();
            });
        _reconnect.Start();
    }
    private async Task PauseReconnectAsync()
    {
        if (_trusted is { } profile)
        {
            _trusted = profile with { AutoReconnect = false };
            var ready = _ready; _ready = false;
            RememberDeviceBox.IsChecked = false; _ready = ready; PersistTrust();
        }
        var reconnect = _reconnect; _reconnect = null;
        if (reconnect is not null) await reconnect.DisposeAsync();
    }
    internal static async Task RetryDeviceAsync(string id)
    {
        if (Connections.FirstOrDefault(w => w._deviceId == id) is not { } window) return;
        if (window._trusted is null) { OpenPairing(id); return; }
        await window.PauseReconnectAsync();
        window._trusted = window._trusted with { AutoReconnect = true };
        window.RememberDeviceBox.IsChecked = true; window.PersistTrust(); window.StartReconnect(); window.NotifySyncState();
    }
    internal static async Task DisconnectDeviceAsync(string id)
    {
        if (Connections.FirstOrDefault(w => w._deviceId == id) is { } window)
            await window.DisconnectDeviceCoreAsync();
    }
    private async Task DisconnectDeviceCoreAsync()
    {
        await PauseReconnectAsync();
        _files?.Cancel(); await _node.StopAsync();
        _deviceStatus = "已断开 · 自动重连已暂停";
        _activities.Update(new("connection-" + _deviceId, SyncKind.Connection, false, _trusted?.Name ?? "设备", SyncState.Cancelled, DeviceId: _deviceId));
        NotifySyncState();
    }
    internal static async Task ForgetDeviceAsync(string id)
    {
        if (Connections.FirstOrDefault(w => w._deviceId == id) is not { } window) return;
        var profile = window._trusted;
        window._trusted = null;
        try { TrustStore.Value.Save(Connections.Where(w => w != window).Select(w => w._trusted).OfType<LanTrustedDevice>()); }
        catch (Exception) { window._trusted = profile; window._deviceStatus = TrustStore.Value.Error; window.NotifySyncState(); return; }
        await window.ShutdownAsync();
    }
    private void RememberDevice_Changed(object sender, RoutedEventArgs e)
    {
        if (!_ready || _trusted is null) return;
        _trusted = _trusted with { AutoReconnect = RememberDeviceBox.IsChecked == true };
        PersistTrust();
        if (_trusted.AutoReconnect) StartReconnect();
        else if (_reconnect is { } reconnect) { _reconnect = null; _ = reconnect.DisposeAsync(); }
        NotifySyncState();
    }
    private void RememberCurrentPreferences()
    {
        if (_trusted is null) return;
        _trusted = _trusted with { Clipboard = _node.ClipboardEnabled, Library = _node.LibraryEnabled };
        PersistTrust();
    }
}
