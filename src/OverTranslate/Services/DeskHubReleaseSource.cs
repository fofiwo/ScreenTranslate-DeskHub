using Velopack;
using Velopack.Logging;
using Velopack.Sources;

namespace OverTranslate.Services;

public sealed class DeskHubReleaseSource(IFileDownloader? downloader = null)
    : SimpleWebSource(UpdateService.UpdateFeedUrl, downloader)
{
    public override Task DownloadReleaseEntry(IVelopackLogger logger, VelopackAsset releaseEntry,
        string localFile, Action<int> progress, CancellationToken cancelToken = default)
    {
        // 用户可能隔天才点安装；下载必须锁定检测到的版本，不能跟随已变化的 latest。
        var pinned = new SimpleWebSource($"{UpdateService.GitHubRepoUrl}/releases/download/v{releaseEntry.Version}/", Downloader);
        return pinned.DownloadReleaseEntry(logger, releaseEntry, localFile, progress, cancelToken);
    }
}
