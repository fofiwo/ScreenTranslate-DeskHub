using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using OverTranslate.Services.Lan;

namespace OverTranslate.Views.ClipboardHistory;

public partial class FileReceiveCard : System.Windows.Controls.UserControl
{
    private readonly string _initialDirectory;
    private readonly TaskCompletionSource<string?> _decision = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private string? _folder;
    private IntPtr _pickerOwner;
    private readonly DateTimeOffset _expiresAt;
    private readonly DispatcherTimer _countdown = new() { Interval = TimeSpan.FromSeconds(1) };
    internal Task<string?> Decision => _decision.Task;

    internal FileReceiveCard(string peer, LanHeader offer, string initialDirectory, DateTimeOffset? expiresAt = null)
    {
        _initialDirectory = initialDirectory;
        InitializeComponent();
        _expiresAt = expiresAt ?? DateTimeOffset.UtcNow.AddMinutes(5);
        _countdown.Tick += (_, _) => UpdateRemaining(DateTimeOffset.UtcNow);
        Loaded += (_, _) => { UpdateRemaining(DateTimeOffset.UtcNow); if (!Decision.IsCompleted) _countdown.Start(); };
        Unloaded += (_, _) => _countdown.Stop();
        UpdateRemaining(DateTimeOffset.UtcNow);
        var size = offer.Length >= 1024L * 1024 * 1024 ? $"{offer.Length / (1024d * 1024 * 1024):0.##} GB"
            : offer.Length >= 1024 * 1024 ? $"{offer.Length / (1024d * 1024):0.##} MB"
            : offer.Length >= 1024 ? $"{offer.Length / 1024d:0.##} KB" : $"{offer.Length:N0} 字节";
        OfferText.Text = $"来自 {peer}\n{offer.Name}\n传输大小：{size}";
    }
    internal void UpdateRemaining(DateTimeOffset now)
    {
        var seconds = Math.Max(0, (int)Math.Ceiling((_expiresAt - now).TotalSeconds));
        RemainingText.Text = seconds == 0 ? "请求已到期" : $"剩余等待时间 {seconds / 60:00}:{seconds % 60:00}";
        if (seconds == 0) Reject();
    }

    private void Folder_Click(object sender, RoutedEventArgs e)
    {
        if (Decision.IsCompleted) return;
        var picker = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "选择本批文件的保存目录",
            InitialDirectory = Directory.Exists(_folder ?? _initialDirectory) ? _folder ?? _initialDirectory : ""
        };
        try
        {
            var owner = Window.GetWindow(this);
            _pickerOwner = new WindowInteropHelper(owner).Handle;
            if (picker.ShowDialog(owner) == true && !Decision.IsCompleted) SelectFolder(picker.FolderName);
        }
        catch (Exception) { if (!Decision.IsCompleted) ErrorText.Text = "保存目录不可用，请重新选择本地文件夹。"; }
        finally { _pickerOwner = IntPtr.Zero; }
    }

    internal void SelectFolder(string folder)
    {
        if (Decision.IsCompleted) return;
        folder = LanFiles.ValidateFolder(folder);
        if (!Directory.Exists(folder)) throw new DirectoryNotFoundException();
        _folder = folder; FolderText.Text = folder; AcceptButton.IsEnabled = true; ErrorText.Text = "";
    }
    internal void Reject()
    {
        _decision.TrySetResult(null);
        _countdown.Stop();
        if (_pickerOwner == IntPtr.Zero) return;
        var picker = GetLastActivePopup(_pickerOwner);
        // 仅关闭本请求正在打开、且直接归属本窗口的本进程目录选择器。
        if (picker == IntPtr.Zero || picker == _pickerOwner || GetWindow(picker, 4) != _pickerOwner) return;
        GetWindowThreadProcessId(picker, out var process);
        if (process == Environment.ProcessId) PostMessage(picker, 0x10, IntPtr.Zero, IntPtr.Zero);
    }
    private void Reject_Click(object sender, RoutedEventArgs e) => Reject();
    private void Accept_Click(object sender, RoutedEventArgs e)
    {
        UpdateRemaining(DateTimeOffset.UtcNow);
        if (_folder is not null) _decision.TrySetResult(_folder);
        if (Decision.IsCompleted) _countdown.Stop();
    }

    [DllImport("user32.dll")] private static extern IntPtr GetLastActivePopup(IntPtr owner);
    [DllImport("user32.dll")] private static extern IntPtr GetWindow(IntPtr window, uint command);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint process);
    [DllImport("user32.dll", EntryPoint = "PostMessageW")] private static extern bool PostMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
}
