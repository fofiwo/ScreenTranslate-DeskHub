using System.IO;
using System.IO.Compression;
using OverTranslate.Services;
using Velopack;
using Velopack.Locators;
using Velopack.Sources;
using Xunit;

namespace OverTranslate.Tests;

public sealed class UpdatePackageCleanupTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("screen-translate-update-cleanup-").FullName;
    private string Packages => Path.Combine(_root, "packages");

    public void Dispose() => Directory.Delete(_root, true);

    private string Write(string name)
    {
        Directory.CreateDirectory(Packages);
        var file = Path.Combine(Packages, name);
        if (name.EndsWith(".nupkg", StringComparison.Ordinal))
        {
            var parts = name.Split('-');
            using var zip = ZipFile.Open(file, ZipArchiveMode.Create);
            using var writer = new StreamWriter(zip.CreateEntry(parts[0] + ".nuspec").Open());
            writer.Write($"<package><metadata><id>{parts[0]}</id><version>{parts[1]}</version><authors>Test</authors><description>Cleanup test</description></metadata></package>");
        }
        else File.WriteAllBytes(file, [1, 2, 3]);
        return file;
    }

    private DeltaAwareUpdateManager Manager(bool installed = true, bool portable = false, string appId = "ScreenTranslate") => new(
        new SimpleWebSource(_root), null, new CleanupLocator(_root, installed, portable, appId));

    private sealed class CleanupLocator(string root, bool installed, bool portable, string appId)
        : TestVelopackLocator(appId, "2.0.0", Path.Combine(root, "packages"), null, root, Path.Combine(root, "Update.exe"))
    {
        public override SemanticVersion? CurrentlyInstalledVersion => installed ? base.CurrentlyInstalledVersion : null;
        public override bool IsPortable => portable;
    }

    [Fact]
    public async Task SuccessfulStartupRemovesInstalledPackageAndPreservesOtherFiles()
    {
        var installed = Write("ScreenTranslate-2.0.0-full.nupkg");
        var other = Write("OtherApp-2.0.0-full.nupkg");
        var installer = Write("ScreenTranslate-win-Setup.exe");
        var metadata = Write("releases.win.json");
        await Manager().CleanupInstalledPackageAsync();
        Assert.False(File.Exists(installed));
        Assert.True(File.Exists(other)); Assert.True(File.Exists(installer)); Assert.True(File.Exists(metadata));
        await Manager().CleanupInstalledPackageAsync();
    }

    [Theory]
    [InlineData("3.0.0")]
    [InlineData("1.0.0")]
    public async Task PendingUpgradeOrDowngradePreservesBothPackages(string targetVersion)
    {
        var installed = Write("ScreenTranslate-2.0.0-full.nupkg");
        var pending = Write($"ScreenTranslate-{targetVersion}-full.nupkg");
        await Manager().CleanupInstalledPackageAsync();
        Assert.True(File.Exists(installed)); Assert.True(File.Exists(pending));
    }

    [Fact]
    public async Task InterruptedDownloadPreservesItsPartialAndBasePackage()
    {
        var installed = Write("ScreenTranslate-2.0.0-full.nupkg");
        var partial = Write("ScreenTranslate-3.0.0-full.nupkg.partial");
        await Manager().CleanupInstalledPackageAsync();
        Assert.True(File.Exists(installed)); Assert.True(File.Exists(partial));
    }

    [Fact]
    public async Task CleanupDoesNotCreateAPendingUpdate()
    {
        Write("ScreenTranslate-2.0.0-full.nupkg");
        await Manager().CleanupInstalledPackageAsync();
        Assert.Null(Manager().UpdatePendingRestart);
        Assert.Empty(Directory.GetFiles(Packages, "*.nupkg"));
    }

    [Fact]
    public async Task NonInstalledAndOtherProductsAreLeftUntouched()
    {
        var installed = Write("ScreenTranslate-2.0.0-full.nupkg");
        await Manager(installed: false).CleanupInstalledPackageAsync();
        Assert.True(File.Exists(installed));
        await Manager(portable: true).CleanupInstalledPackageAsync();
        Assert.True(File.Exists(installed));
        await Manager(appId: "OtherApp").CleanupInstalledPackageAsync();
        Assert.True(File.Exists(installed));
    }

    [Fact]
    public async Task LockedPackageIsPreservedAndCanBeCleanedOnNextStartup()
    {
        var installed = Write("ScreenTranslate-2.0.0-full.nupkg");
        using (File.Open(installed, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            await Manager().CleanupInstalledPackageAsync();
            Assert.True(File.Exists(installed));
        }
        await Manager().CleanupInstalledPackageAsync();
        Assert.False(File.Exists(installed));
    }
}
