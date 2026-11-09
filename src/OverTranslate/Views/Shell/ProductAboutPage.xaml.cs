using System.Diagnostics;
using System.Reflection;
using System.Windows;
using UserControl = System.Windows.Controls.UserControl;

namespace OverTranslate.Views.Shell;

public partial class ProductAboutPage : UserControl
{
    public ProductAboutPage()
    {
        InitializeComponent();
        VersionText.Text = $"桌面版 · v{OverTranslate.Services.UpdateService.InstalledVersion()}";
    }
    private void OpenFile(string name)
    {
        var path = System.IO.Path.Combine(AppContext.BaseDirectory, name);
        if (!System.IO.File.Exists(path)) { StatusText.Text = "此开发目录未附带说明文件，请从发布目录查看。"; return; }
        try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); }
        catch (Exception) { StatusText.Text = "无法打开文档，请在程序目录中查看。"; }
    }
    private void Licenses_Click(object sender, RoutedEventArgs e) => OpenFile("FORK-NOTICES.md");
    private void Readme_Click(object sender, RoutedEventArgs e) => OpenFile("README.md");
    private async void Update_Click(object sender, RoutedEventArgs e) =>
        await OverTranslate.Services.UpdateUi.CheckAndShowAsync((System.Windows.Controls.Button)sender, message => StatusText.Text = message);
    private void Releases_Click(object sender, RoutedEventArgs e) => OverTranslate.Services.UpdateUi.OpenReleases();
}
