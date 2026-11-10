using System.Windows;
using OverTranslate.Services.Lan;

namespace OverTranslate.Views.ClipboardHistory;

public partial class FileReceiveWindow : Window
{
    internal string? Folder { get; private set; }
    internal FileReceiveWindow(string peer, LanHeader offer)
    {
        InitializeComponent();
        OfferText.Text = $"来自 {peer}\n{offer.Name}\n大小：{offer.Length:N0} 字节\n请求将在 5 分钟后过期。";
    }
    private void Folder_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog { Title = "选择本批文件的保存目录" };
        if (dialog.ShowDialog(this) == true && IsVisible)
        { Folder = dialog.FolderName; FolderText.Text = Folder; AcceptButton.IsEnabled = true; }
    }
    private void Accept_Click(object sender, RoutedEventArgs e) { if (Folder is not null) DialogResult = true; }
}
