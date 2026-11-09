using OverTranslate.Services;
using Xunit;
using Velopack.Sources;
using Velopack;
using Velopack.Logging;

namespace OverTranslate.Tests;

public sealed class DeskHubUpdateTests
{
    [Fact]
    public void ReleaseFeedBelongsToOurRepository()
    {
        Assert.Equal("https://github.com/fofiwo/ScreenTranslate-DeskHub", UpdateService.GitHubRepoUrl);
        Assert.Equal(UpdateService.GitHubRepoUrl + "/releases/latest", UpdateService.ReleasesUrl);
    }

    [Theory]
    [InlineData("OverTranslate")]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("ScreenTranslateEvil")]
    public void OtherProductsCannotBeInstalled(string? packageId) =>
        Assert.Throws<InvalidOperationException>(() => UpdateService.ValidatePackageId(packageId));

    [Fact]
    public void OurPackageIsAccepted() => UpdateService.ValidatePackageId("ScreenTranslate");

    [Fact]
    public void StableUpdatesReadReleaseAssetsWithoutGitHubApiQuota()
    {
        var source = UpdateService.CreateUpdateSource(new UpdateFeedSource(UpdateService.GitHubRepoUrl, null, "win", false, false));
        var web = Assert.IsType<DeskHubReleaseSource>(source);
        Assert.Equal("https://github.com/fofiwo/ScreenTranslate-DeskHub/releases/latest/download/", web.BaseUri.AbsoluteUri);
    }

    [Fact]
    public async Task DownloadStaysOnTheOfferedVersionAfterLatestChanges()
    {
        var downloader = new RecordingDownloader();
        var source = new DeskHubReleaseSource(downloader);
        var asset = new VelopackAsset { PackageId = "ScreenTranslate", Version = SemanticVersion.Parse("0.3.4"),
            FileName = "ScreenTranslate-0.3.4-full.nupkg", Type = VelopackAssetType.Full };
        await source.DownloadReleaseEntry(new SilentLogger(), asset, "unused", _ => { });
        Assert.Equal("https://github.com/fofiwo/ScreenTranslate-DeskHub/releases/download/v0.3.4/ScreenTranslate-0.3.4-full.nupkg", downloader.Requested);
    }

    private sealed class SilentLogger : IVelopackLogger
    {
        public void Log(VelopackLogLevel level, string? message, Exception? exception) { }
    }

    [Fact]
    public async Task ConsecutiveChecksBypassCachedLatestRedirects()
    {
        var downloader = new RecordingDownloader();
        var source = new DeskHubReleaseSource(downloader);
        var first = await source.GetReleaseFeed(new SilentLogger(), "ScreenTranslate", "win");
        var firstUrl = downloader.Requested;
        var second = await source.GetReleaseFeed(new SilentLogger(), "ScreenTranslate", "win");
        Assert.Equal("0.3.11", first.Assets.Single().Version.ToString());
        Assert.Equal("0.3.11", second.Assets.Single().Version.ToString());
        Assert.NotEqual(firstUrl, downloader.Requested);
        Assert.Equal("/fofiwo/ScreenTranslate-DeskHub/releases/latest/download/releases.win.json", new Uri(downloader.Requested!).AbsolutePath);
    }

    private sealed class RecordingDownloader : IFileDownloader
    {
        public string? Requested { get; private set; }
        public Task DownloadFile(string url, string targetFile, Action<int> progress,
            IDictionary<string, string>? headers, double timeout, CancellationToken cancelToken)
        { Requested = url; return Task.CompletedTask; }
        public Task<byte[]> DownloadBytes(string url, IDictionary<string, string>? headers, double timeout) => throw new NotSupportedException();
        public Task<string> DownloadString(string url, IDictionary<string, string>? headers, double timeout)
        {
            Requested = url;
            return Task.FromResult("""{"Assets":[{"PackageId":"ScreenTranslate","Version":"0.3.11","Type":"Full","FileName":"ScreenTranslate-0.3.11-full.nupkg"}]}""");
        }
    }
}
