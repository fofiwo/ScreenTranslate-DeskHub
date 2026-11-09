using System.Diagnostics;
using System.Windows;
using OverTranslate.Views.Shell;

namespace OverTranslate.Services;

internal static class UpdateUi
{
    internal static string ButtonLabel => UpdateNotifier.Available is { } info
        ? $"发现新版 v{info.LatestVersion} · 点击安装" : "检查更新";

    internal static async Task CheckAndShowAsync()
    {
        var info = UpdateNotifier.Available ?? await UpdateNotifier.CheckAsync();
        if (info is not null) { UpdateWindow.ShowOrActivate(info); return; }
        var message = UpdateService.LastCheckError ?? (UpdateService.IsInstalledBuild
            ? "当前已是最新版本。以后发现新版时，会在主窗口左下角提示安装。"
            : "当前为便携版或开发版。请先安装正式安装版，之后可在应用内自动检测并安装更新。");
        System.Windows.MessageBox.Show(message, "ScreenTranslate 更新", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    internal static void OpenReleases()
    {
        try { Process.Start(new ProcessStartInfo(UpdateService.ReleasesUrl) { UseShellExecute = true }); }
        catch { System.Windows.MessageBox.Show("无法打开浏览器，请访问：\n" + UpdateService.ReleasesUrl, "下载更新"); }
    }
}
