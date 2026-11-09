using System.Text.Json;
using System.IO.Compression;
using System.Reflection;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using OverTranslate.Services;
using OverTranslate.Models;
using Velopack;
using Velopack.Locators;
using Velopack.Sources;

// Real public feed and package validation in a disposable synthetic installation; never applies an update.
var root = Directory.CreateTempSubdirectory("deskhub-feed-probe-").FullName;
try
{
    var proxy = args.FirstOrDefault(a => a.StartsWith("--proxy="))?[8..];
    if (proxy is not null)
    {
        if (!NetworkProxyService.TryParseAddress(proxy, out _)) throw new ArgumentException("Invalid proxy address");
        NetworkProxyService.Apply(new NetworkProxySettings { Enabled = true, Address = proxy, Features = NetworkFeature.Updates });
    }
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
    if (args.Contains("--transport-probe"))
    {
        using var handler = UpdateHttp.CreateHandler();
        var encoding = args.FirstOrDefault(a => a.StartsWith("--encoding="))?[11..] ?? "default";
        if (encoding == "none") handler.AutomaticDecompression = DecompressionMethods.None;
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(60) };
        client.DefaultRequestHeaders.UserAgent.Add(HttpClientFileDownloader.UserAgent);
        var url = $"{UpdateService.GitHubRepoUrl}/releases/download/v{update.TargetFullRelease.Version}/{update.TargetFullRelease.FileName}?transport={Guid.NewGuid():N}";
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        var range = args.FirstOrDefault(a => a.StartsWith("--range="))?[8..];
        if (range is not null) request.Headers.Range = RangeHeaderValue.Parse("bytes=" + range);
        using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(120));
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancel.Token);
        Console.WriteLine(JsonSerializer.Serialize(new { phase = "headers", encoding, range, status = (int)response.StatusCode,
            protocol = response.Version.ToString(), contentLength = response.Content.Headers.ContentLength,
            contentEncoding = response.Content.Headers.ContentEncoding, contentRange = response.Content.Headers.ContentRange?.ToString(),
            acceptRanges = response.Headers.AcceptRanges, etag = response.Headers.ETag?.ToString() }));
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancel.Token);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[81920];
        long received = 0;
        var watch = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            int count;
            while ((count = await stream.ReadAsync(buffer, cancel.Token)) > 0)
            { hash.AppendData(buffer, 0, count); received += count; }
        }
        catch (Exception ex)
        {
            Console.WriteLine(JsonSerializer.Serialize(new { phase = "failed", received, elapsedMs = watch.ElapsedMilliseconds,
                error = ex.GetType().FullName }));
            throw;
        }
        Console.WriteLine(JsonSerializer.Serialize(new { phase = "complete", received, elapsedMs = watch.ElapsedMilliseconds,
            sha256 = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant(), expectedSha256 = update.TargetFullRelease.SHA256 }));
        return;
    }
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
        explicitUpdateProxy = proxy is not null,
        downloadAndHashVerification = download,
        packageEntries,
        assemblyVersion,
        noPersonalDataPathsInPackage = download,
        applied = false,
        personalDataAccessed = false
    }));
}
finally { Directory.Delete(root, true); }
