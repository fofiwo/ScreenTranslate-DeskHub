using System.Diagnostics;
using System.Windows;
using OverTranslate.Views.Shell;

namespace OverTranslate.Services;

internal static class UpdateUi
{
    private static bool _checking;
    internal static string ButtonLabel => UpdateNotifier.Available is { } info
        ? $"发现新版 v{info.LatestVersion}" : "检查更新";

    internal static async Task CheckAndShowAsync(System.Windows.Controls.Button? button = null, Action<string>? reportStatus = null)
    {
        if (_checking) { reportStatus?.Invoke("正在检查更新，请稍候…"); return; }
        _checking = true;
        var owner = button is null ? null : Window.GetWindow(button);
        var wasEnabled = button?.IsEnabled ?? true;
        try
        {
            if (button is not null) { button.Content = "正在检查更新…"; button.IsEnabled = false; }
            reportStatus?.Invoke("正在检查更新，请稍候…");
            // 先让界面绘制反馈，再开始可能同步完成或阻塞的更新器初始化。
            await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.Background);
            var info = await UpdateNotifier.CheckAsync().WaitAsync(TimeSpan.FromSeconds(30));
            if (info is not null)
            {
                reportStatus?.Invoke($"发现新版 v{info.LatestVersion}");
                UpdateWindow.ShowOrActivate(info);
                return;
            }
            ShowResult(UpdateService.LastCheckError ?? (UpdateService.IsInstalledBuild
                ? "当前已是最新版本。以后发现新版时，会在主窗口左下角提示安装。"
                : "当前为便携版或开发版。请先安装正式安装版，之后可在应用内自动检测并安装更新。"));
        }
        catch (TimeoutException)
        {
            ShowResult("检查更新超时，请检查网络后重试，或到发布页下载安装包。");
        }
        catch (Exception)
        {
            ShowResult("检查更新失败，请稍后重试，或到发布页下载安装包。");
        }
        finally
        {
            _checking = false;
            if (button is not null) { button.Content = ButtonLabel; button.IsEnabled = wasEnabled; }
        }

        void ShowResult(string message)
        {
            reportStatus?.Invoke(message);
            if (reportStatus is not null) return;
            if (owner?.IsVisible == true)
                OverTranslate.Views.Controls.AppMessageWindow.ShowMessage(owner, message, "ScreenTranslate 更新", MessageBoxButton.OK, MessageBoxImage.Information);
            else
                OverTranslate.Views.Controls.AppMessageWindow.ShowMessage(message, "ScreenTranslate 更新", MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }

    internal static void OpenReleases()
    {
        try { Process.Start(new ProcessStartInfo(UpdateService.ReleasesUrl) { UseShellExecute = true }); }
        catch { OverTranslate.Views.Controls.AppMessageWindow.ShowMessage("无法打开浏览器，请访问：\n" + UpdateService.ReleasesUrl, "下载更新"); }
    }
}
