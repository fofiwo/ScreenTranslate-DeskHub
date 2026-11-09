using System.Security.Cryptography;
using System.Windows;
using OverTranslate.Services;
using OverTranslate.Services.Library;
using MessageBox = System.Windows.MessageBox;

namespace OverTranslate.Views.Library;

public partial class LibrarySyncWindow : Window
{
    private LibrarySyncOptions _options = new();
    private bool _saving;
    private readonly Func<LibrarySyncOptions, Task> _apply;

    public LibrarySyncWindow() : this(null) { }

    internal LibrarySyncWindow(LibrarySyncOptions? options, Func<LibrarySyncOptions, Task>? apply = null)
    {
        _apply = apply ?? (value => LibrarySyncRuntime.ConfigureAsync(value));
        InitializeComponent();
        try
        {
            _options = options ?? LibrarySyncOptions.Load(LibrarySyncOptions.DefaultPath);
            EnabledBox.IsChecked = _options.Enabled;
            PortBox.Text = _options.Port.ToString(); PeersBox.Text = string.Join(Environment.NewLine, _options.Peers);
            LocalIpText.Text = LibrarySyncRuntime.LocalAddresses();
            if (LocalIpText.Text.Length == 0) LocalIpText.Text = "尚未找到局域网 IPv4，请检查网络连接。";
            StatusText.Text = LibrarySyncRuntime.Status;
        }
        catch (Exception) { StatusText.Text = "无法读取同步设置，请检查文件权限或恢复设置文件。"; SaveButton.IsEnabled = false; }
        LibrarySyncRuntime.StatusChanged += UpdateStatus;
        Closed += (_, _) => { LibrarySyncRuntime.StatusChanged -= UpdateStatus; CodeBox.Clear(); };
        Closing += (_, e) => e.Cancel = _saving;
    }

    private void UpdateStatus()
    {
        if (!Dispatcher.HasShutdownStarted) Dispatcher.BeginInvoke(() => StatusText.Text = LibrarySyncRuntime.Status);
    }

    private void Generate_Click(object sender, RoutedEventArgs e)
    {
        if (_options.CodeProtected.Length > 0 && MessageBox.Show(this,
            "更换配对码后，所有继续同步的电脑都需要更新代码。继续生成？", "更换配对码", MessageBoxButton.YesNo,
            MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        CodeBox.Password = LibrarySyncTransport.GenerateCode();
        StatusText.Text = "已生成，尚未应用。请复制到自己的其他电脑，然后保存。";
    }

    private void CopyCode_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var code = CodeBox.Password.Length > 0 ? CodeBox.Password : LocalSecretStore.Unprotect(_options.CodeProtected);
            CryptographicOperations.ZeroMemory(LibrarySyncTransport.ParseCode(code));
            LibraryClipboard.SetText(code, new System.Windows.Interop.WindowInteropHelper(this).EnsureHandle());
            StatusText.Text = "配对码已复制。请仅粘贴到自己的同步设备。";
        }
        catch (Exception) { StatusText.Text = "无法复制配对码，请先生成代码或检查本机凭据权限。"; }
    }

    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        if (_saving) return;
        try
        {
            if (!int.TryParse(PortBox.Text, out var port)) throw new InvalidOperationException("请输入有效端口。");
            var code = CodeBox.Password.Trim();
            if (code.Length > 0) CryptographicOperations.ZeroMemory(LibrarySyncTransport.ParseCode(code));
            var options = new LibrarySyncOptions
            {
                Enabled = EnabledBox.IsChecked == true, Port = port,
                Peers = PeersBox.Text.Split(['\r', '\n', ',', '，'], StringSplitOptions.RemoveEmptyEntries)
                    .Select(p => p.Trim()).Where(p => p.Length > 0).Distinct().ToList(),
                CodeProtected = code.Length > 0 ? LocalSecretStore.Protect(code) : _options.CodeProtected
            };
            options.Validate(); _saving = true; SyncForm.IsEnabled = false;
            await _apply(options);
            _options = options; CodeBox.Clear(); StatusText.Text = LibrarySyncRuntime.Status;
        }
        catch (InvalidOperationException ex) { StatusText.Text = ex.Message; }
        catch (Exception) { StatusText.Text = "保存或启动失败，请检查端口占用、网络和文件权限；资料未清空，可重试。"; }
        finally { _saving = false; SyncForm.IsEnabled = true; }
    }

    private void SyncNow_Click(object sender, RoutedEventArgs e)
    { StatusText.Text = LibrarySyncRuntime.SyncNow() ? "已请求同步。未保存的设置需先保存并应用。" : "同步尚未开启，请先启用并保存设置。"; }
}
