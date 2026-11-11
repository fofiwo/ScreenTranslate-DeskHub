using System.IO;
using System.Windows;
using OverTranslate.Services.Lan;

namespace OverTranslate.Views.ClipboardHistory;

public partial class LanTransferWindow
{
    private SyncActivityStore _activities = null!;
    internal static event EventHandler? SyncStateChanged;
    internal static void CancelCurrentSend(string id) => Connections.FirstOrDefault(w => w._deviceId == id)?._files?.Cancel();
    internal static string CurrentSyncStatus
    {
        get
        {
            var connected = Connections.Where(w => w._node.IsConnected).ToArray();
            if (Connections.Count == 0) return "设备未连接 · 点击“设备与同步”进行配对";
            return $"{connected.Length} / {Connections.Count} 台在线 · 点击查看设备、同步开关及重连状态";
        }
    }

    internal static async Task<string?> ChooseAndSendAsync(Window owner, string[] paths)
    {
        RestoreConnections();
        var versions = Connections.ToDictionary(w => w._deviceId, w => w._node.ConnectionVersion);
        var picker = new FileSendWindow(ConnectedChoices(), paths) { Owner = owner };
        if (picker.ShowDialog() == true)
        {
            var window = Connections.FirstOrDefault(w => w._deviceId == picker.SelectedDeviceId);
            if (window is null || !versions.TryGetValue(window._deviceId, out var version)
                || version != window._node.ConnectionVersion || !window._node.IsConnected)
            {
                OverTranslate.Views.Controls.AppMessageWindow.ShowMessage(owner, "连接已变化，请重新选择接收方。", "发送未开始");
                return "连接已变化，未开始重新发送。";
            }
            return await window.SendFilesAsync(picker.Paths);
        }
        else if (picker.OpenSetup) Open();
        return null;
    }

    internal static async Task<string> SendToDeviceAsync(LanDeviceChoice peer, string[] paths)
    {
        var window = Connections.FirstOrDefault(w => w._deviceId == peer.Id);
        if (window is null || !window._node.IsConnected || !window._node.SupportsFileOffers
            || window._node.ConnectionVersion != peer.ConnectionVersion)
            return "连接已变化，请重新选择接收设备。";
        return await window.SendFilesAsync(paths);
    }

    private async Task<string?> ConfirmFilesAsync(LanHeader header, CancellationToken token)
    {
        var confirmation = await Dispatcher.InvokeAsync(() =>
        {
            if (_shutdown || token.IsCancellationRequested) return Task.FromResult<string?>(null);
            return ClipboardHistoryWindow.ConfirmIncomingFilesAsync(_node.PeerName, header, _preferences.ReceiveFolder, token);
        });
        return await confirmation;
    }

    private void SyncHistory_Click(object sender, RoutedEventArgs e) => new SyncActivityWindow(_activities) { Owner = this }.ShowDialog();
    private void NotifySyncState() => SyncStateChanged?.Invoke(this, EventArgs.Empty);
}
