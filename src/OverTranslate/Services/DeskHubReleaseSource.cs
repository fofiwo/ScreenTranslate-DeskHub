using Velopack;
using Velopack.Logging;
using Velopack.Sources;

namespace OverTranslate.Services;

public sealed class DeskHubReleaseSource(IFileDownloader? downloader = null)
    : SimpleWebSource(UpdateService.UpdateFeedUrl, downloader ?? new UpdateDownloader())
{
    public override async Task<VelopackAssetFeed> GetReleaseFeed(IVelopackLogger logger, string? appId,
        string channel, Guid? stagingId = null, VelopackAsset? latestLocalRelease = null)
    {
        // latest 是重定向，版本清单每次重新获取；安装包仍锁定检测到的版本。
        var url = new Uri(BaseUri, $"releases.{Uri.EscapeDataString(channel)}.json");
        var json = await Downloader.DownloadString($"{url}?check={Guid.NewGuid():N}",
            new Dictionary<string, string> { ["Cache-Control"] = "no-cache, no-store", ["Pragma"] = "no-cache" },
            timeout: 20d / 60).ConfigureAwait(false);
        return VelopackAssetFeed.FromJson(json);
    }

    public override Task DownloadReleaseEntry(IVelopackLogger logger, VelopackAsset releaseEntry,
        string localFile, Action<int> progress, CancellationToken cancelToken = default)
    {
        // 用户可能隔天才点安装；下载必须锁定检测到的版本，不能跟随已变化的 latest。
        var pinned = new SimpleWebSource($"{UpdateService.GitHubRepoUrl}/releases/download/v{releaseEntry.Version}/", Downloader);
        return pinned.DownloadReleaseEntry(logger, releaseEntry, localFile, progress, cancelToken);
    }
}
