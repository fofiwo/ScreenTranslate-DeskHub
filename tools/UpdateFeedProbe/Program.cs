using System.Text.Json;
using System.IO.Compression;
using System.Reflection;
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
    var manager = new DeltaAwareUpdateManager(new DeskHubReleaseSource(),
        new UpdateOptions { ExplicitChannel = "win" }, locator);
    var update = await manager.CheckForUpdatesAsync();
    if (update is null)
    {
        if (!args.Contains("--expect-current")) throw new InvalidOperationException("No newer public package was detected");
        Console.WriteLine(JsonSerializer.Serialize(new { repository = UpdateService.GitHubRepoUrl, from,
            noNewerVersion = true, applied = false, personalDataAccessed = false }));
        return;
    }
    if (args.Contains("--expect-current")) throw new InvalidOperationException("Unexpected newer public package");
    if (update.TargetFullRelease.PackageId != "ScreenTranslate") throw new InvalidOperationException("Unexpected product");
    var download = args.Contains("--download");
    if (download) await manager.DownloadUpdatesAsync(update);
    int? packageEntries = null;
    string? assemblyVersion = null;
    if (download)
    {
        var packagePath = Directory.GetFiles(root, update.TargetFullRelease.FileName, SearchOption.AllDirectories).Single();
        using var archive = ZipFile.OpenRead(packagePath);
        packageEntries = archive.Entries.Count;
        var forbiddenNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { "data", "logs", "clipboard-history", "library.json", "sync.json", "lan-preferences.json", "sync-onboarding.json", "settings.json" };
        foreach (var entry in archive.Entries)
        {
            var parts = entry.FullName.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries);
            if (parts.Any(part => forbiddenNames.Contains(part) || part.StartsWith(".env", StringComparison.OrdinalIgnoreCase)
                || part.StartsWith("appsettings", StringComparison.OrdinalIgnoreCase) && part.EndsWith(".json", StringComparison.OrdinalIgnoreCase)))
                throw new InvalidDataException("Personal-data path found in package");
        }
        var assembly = archive.Entries.Single(e => e.Name == "ScreenTranslate.dll");
        var assemblyPath = Path.Combine(root, "verified-ScreenTranslate.dll");
        assembly.ExtractToFile(assemblyPath);
        assemblyVersion = AssemblyName.GetAssemblyName(assemblyPath).Version?.ToString(3);
        if (assemblyVersion != update.TargetFullRelease.Version.ToString())
            throw new InvalidDataException("Packaged assembly version does not match update version");
    }
    Console.WriteLine(JsonSerializer.Serialize(new
    {
        repository = UpdateService.GitHubRepoUrl,
        from,
        latest = update.TargetFullRelease.Version.ToString(),
        package = update.TargetFullRelease.FileName,
        packageId = update.TargetFullRelease.PackageId,
        downloadAndHashVerification = download,
        packageEntries,
        assemblyVersion,
        noPersonalDataPathsInPackage = download,
        applied = false,
        personalDataAccessed = false
    }));
}
finally { Directory.Delete(root, true); }
