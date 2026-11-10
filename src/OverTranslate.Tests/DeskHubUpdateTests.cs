using OverTranslate.Services;
using Xunit;
using Velopack.Sources;
using Velopack;
using Velopack.Logging;
using Velopack.Locators;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;

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

    [Theory]
    [InlineData("0.3.16", false)]
    [InlineData("0.3.18", true)]
    [InlineData("0.3.17", null)]
    public async Task GitHubReleaseIsOfferedWheneverTheInstalledVersionDiffers(string installedVersion, bool? downgrade)
    {
        var folder = Directory.CreateTempSubdirectory("deskhub-github-version-").FullName;
        byte[] package = [1, 2, 3, 4];
        try
        {
            var downloader = new RecordingDownloader
            {
                ReleaseVersion = "0.3.17",
                Package = package,
                WritePackage = path => File.WriteAllBytes(path, package)
            };
            var locator = new TestVelopackLocator("ScreenTranslate", installedVersion, folder, null, folder, null);
            var manager = UpdateService.CreateManager(new DeskHubReleaseSource(downloader), locator);
            var offer = await manager.CheckForUpdatesAsync();
            if (downgrade is null) { Assert.Null(offer); return; }
            Assert.NotNull(offer);
            Assert.Equal("0.3.17", offer.TargetFullRelease.Version.ToString());
            Assert.Equal(downgrade.Value, offer.IsDowngrade);
            Assert.Empty(offer.DeltasToTarget);
            await manager.DownloadUpdatesAsync(offer);
            Assert.Equal(package, File.ReadAllBytes(Path.Combine(folder, "ScreenTranslate-0.3.17-full.nupkg")));
            Assert.Equal(UpdateService.GitHubRepoUrl + "/releases/download/v0.3.17/ScreenTranslate-0.3.17-full.nupkg", downloader.Requested);
        }
        finally { Directory.Delete(folder, recursive: true); }
    }

    [Fact]
    public async Task GitHubDowngradeStillRejectsACorruptedPackage()
    {
        var folder = Directory.CreateTempSubdirectory("deskhub-github-corrupt-").FullName;
        try
        {
            var downloader = new RecordingDownloader
            {
                ReleaseVersion = "0.3.17",
                WritePackage = path => File.WriteAllBytes(path, [4, 3, 2, 1])
            };
            var manager = UpdateService.CreateManager(new DeskHubReleaseSource(downloader),
                new TestVelopackLocator("ScreenTranslate", "0.3.18", folder, null, folder, null));
            var offer = await manager.CheckForUpdatesAsync();
            Assert.NotNull(offer);
            await Assert.ThrowsAsync<Velopack.Exceptions.ChecksumFailedException>(() => manager.DownloadUpdatesAsync(offer));
            Assert.False(File.Exists(Path.Combine(folder, "ScreenTranslate-0.3.17-full.nupkg")));
        }
        finally { Directory.Delete(folder, recursive: true); }
    }

    private sealed class RecordingDownloader : IFileDownloader
    {
        public string? Requested { get; private set; }
        public string ReleaseVersion { get; init; } = "0.3.11";
        public byte[] Package { get; init; } = [1, 2, 3, 4];
        public Action<string>? WritePackage { get; init; }
        public Task DownloadFile(string url, string targetFile, Action<int> progress,
            IDictionary<string, string>? headers, double timeout, CancellationToken cancelToken)
        { Requested = url; WritePackage?.Invoke(targetFile); return Task.CompletedTask; }
        public Task<byte[]> DownloadBytes(string url, IDictionary<string, string>? headers, double timeout) => throw new NotSupportedException();
        public Task<string> DownloadString(string url, IDictionary<string, string>? headers, double timeout)
        {
            Requested = url;
            return Task.FromResult(JsonSerializer.Serialize(new { Assets = new[] { new {
                PackageId = "ScreenTranslate", Version = ReleaseVersion, Type = "Full",
                FileName = $"ScreenTranslate-{ReleaseVersion}-full.nupkg", Size = Package.Length,
                SHA1 = Convert.ToHexString(SHA1.HashData(Package)), SHA256 = Convert.ToHexString(SHA256.HashData(Package))
            } } }));
        }
    }
}
