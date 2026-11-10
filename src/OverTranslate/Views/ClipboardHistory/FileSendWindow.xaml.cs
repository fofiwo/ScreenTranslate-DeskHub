using System.IO;
using System.Windows;

namespace OverTranslate.Views.ClipboardHistory;

public partial class FileSendWindow : Window
{
    private readonly List<string> _paths = [];
    internal string? SelectedDeviceId => (RecipientBox.SelectedItem as LanDeviceChoice)?.Id;
    internal string[] Paths => _paths.ToArray();
    internal bool OpenSetup { get; private set; }
    internal FileSendWindow(string peer, bool supported, string[] paths)
        : this(peer.Length == 0 ? [] : new[] { new LanDeviceChoice("fixture", peer, supported) }, paths) { }
    internal FileSendWindow(IReadOnlyList<LanDeviceChoice> peers, string[] paths)
    {
        InitializeComponent();
        RecipientBox.ItemsSource = peers;
        RecipientBox.SelectedIndex = peers.Count == 0 ? -1 : 0;
        Add(paths);
    }
    private void Add(string[] paths)
    {
        foreach (var path in paths)
            if (!_paths.Contains(path, StringComparer.OrdinalIgnoreCase)) _paths.Add(path);
        SourceList.ItemsSource = _paths.ToArray();
        SelectionHint.Text = _paths.Count == 0 ? "请选择文件，或关闭后把文件 / 文件夹拖到剪贴板窗口。"
            : $"共 {_paths.Count} 项 · 每批最多 100 项、10000 个文件与文件夹、64 GB。";
        UpdateRecipient();
    }
    private void Recipient_Changed(object sender, System.Windows.Controls.SelectionChangedEventArgs e) { if (SendButton is not null) UpdateRecipient(); }
    private void UpdateRecipient()
    {
        var peer = RecipientBox.SelectedItem as LanDeviceChoice;
        ConnectionHint.Text = peer is null ? "尚无已连接设备，请先到“设备与同步”配对。"
            : !peer.Supported ? "对方版本不支持接收确认，请更新对方后重新配对。" : "仅发送到所选设备，其他已连接设备不接收本批文件。";
        SendButton.IsEnabled = peer?.Supported == true && _paths.Count is > 0 and <= 100;
    }
    private void AddFiles_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog { Multiselect = true, CheckFileExists = true, Title = "选择要发送的文件" };
        if (dialog.ShowDialog(this) == true) Add(dialog.FileNames);
    }
    private void AddFolder_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog { Title = "选择要发送的文件夹", Multiselect = true };
        if (dialog.ShowDialog(this) == true) Add(dialog.FolderNames);
    }
    private void Clear_Click(object sender, RoutedEventArgs e) { _paths.Clear(); Add([]); }
    private void Setup_Click(object sender, RoutedEventArgs e) { OpenSetup = true; DialogResult = false; }
    private void Send_Click(object sender, RoutedEventArgs e)
    {
        if (_paths.Any(path => !File.Exists(path) && !Directory.Exists(path)))
        { SelectionHint.Text = "部分路径已不存在，请重新选择。"; return; }
        DialogResult = true;
    }
}
