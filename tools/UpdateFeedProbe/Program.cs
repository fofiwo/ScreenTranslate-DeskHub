using System.Text.Json;
using OverTranslate.Services;
using Velopack;
using Velopack.Locators;
using Velopack.Sources;

// Real public feed and package validation in a disposable synthetic installation; never applies an update.
var root = Directory.CreateTempSubdirectory("deskhub-feed-probe-").FullName;
try
{
    var from = args.FirstOrDefault(a => a.StartsWith("--from="))?[7..] ?? "0.2.3";
    var locator = new TestVelopackLocator("ScreenTranslate", from, root, null, root, null);
    var manager = new DeltaAwareUpdateManager(new GithubSource(UpdateService.GitHubRepoUrl, null, false),
        new UpdateOptions { ExplicitChannel = "win" }, locator);
    var update = await manager.CheckForUpdatesAsync();
    if (update is null) throw new InvalidOperationException("No newer public package was detected");
    if (update.TargetFullRelease.PackageId != "ScreenTranslate") throw new InvalidOperationException("Unexpected product");
    var download = args.Contains("--download");
    if (download) await manager.DownloadUpdatesAsync(update);
    Console.WriteLine(JsonSerializer.Serialize(new
    {
        repository = UpdateService.GitHubRepoUrl,
        from,
        latest = update.TargetFullRelease.Version.ToString(),
        package = update.TargetFullRelease.FileName,
        packageId = update.TargetFullRelease.PackageId,
        downloadAndHashVerification = download,
        applied = false,
        personalDataAccessed = false
    }));
}
finally { Directory.Delete(root, true); }
