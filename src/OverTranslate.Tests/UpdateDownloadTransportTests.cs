using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Security.Cryptography;
using OverTranslate.Services;
using Velopack;
using Velopack.Locators;
using Velopack.Sources;
using Xunit;

namespace OverTranslate.Tests;

[Collection("Network proxy")]
public sealed class UpdateDownloadTransportTests
{
    private const string PackageUrl = "http://release.fixture.invalid/releases/download/v0.3.12/ScreenTranslate-0.3.12-full.nupkg?existing=keep";
    private static readonly byte[] Payload = Encoding.UTF8.GetBytes(new string('x', 131072) + "complete package");

    [Fact]
    public async Task TruncatedPackageRetriesThroughSameProxyAndReplacesPartialBytes()
    {
        using var proxy = new DownloadProxy((attempt, stream, token) => Respond(stream, 200,
            attempt == 1 ? Payload[..10000] : Payload, Payload.Length, token));
        await WithDownload(proxy, async (downloader, file) =>
        {
            await downloader.DownloadFile(PackageUrl, file, _ => { }, new Dictionary<string, string> { ["X-Fixture"] = "preserved" }, 0.1, default);
            Assert.Equal(Payload, await File.ReadAllBytesAsync(file));
            Assert.Equal(2, proxy.Requests.Count);
            var requests = proxy.Requests.ToArray();
            Assert.NotEqual(requests[0], requests[1]);
            Assert.All(proxy.Headers, headers => Assert.Contains("X-Fixture: preserved", headers));
            var cache = proxy.Headers.Last().Split('\n').Single(header => header.StartsWith("Cache-Control:", StringComparison.OrdinalIgnoreCase));
            Assert.Contains("no-cache", cache); Assert.Contains("no-store", cache);
            Assert.All(requests, request =>
            {
                Assert.Contains("/releases/download/v0.3.12/ScreenTranslate-0.3.12-full.nupkg?existing=keep", request);
                Assert.DoesNotContain("/latest/", request);
            });
        });
    }

    [Fact]
    public async Task RecoveredTransportStillRejectsPackageWithWrongHash()
    {
        using var proxy = new DownloadProxy((attempt, stream, token) => Respond(stream, 200,
            attempt == 1 ? Payload[..10000] : Payload, Payload.Length, token));
        await WithDownload(proxy, async (downloader, file) =>
        {
            var root = Path.GetDirectoryName(file)!;
            var expected = Payload.ToArray(); expected[0] ^= 1;
            var asset = new VelopackAsset { PackageId = "ScreenTranslate", Version = SemanticVersion.Parse("0.3.12"),
                FileName = "ScreenTranslate-0.3.12-full.nupkg", Type = VelopackAssetType.Full, Size = Payload.Length,
                SHA1 = Convert.ToHexString(SHA1.HashData(expected)), SHA256 = Convert.ToHexString(SHA256.HashData(expected)) };
            var manager = new DeltaAwareUpdateManager(new SimpleWebSource("http://release.fixture.invalid/", downloader),
                null, new TestVelopackLocator("ScreenTranslate", "0.3.5", root, null, root, null));
            await Assert.ThrowsAsync<Velopack.Exceptions.ChecksumFailedException>(() => manager.DownloadUpdatesAsync(new Velopack.UpdateInfo(asset, false)));
            Assert.Equal(2, proxy.Requests.Count);
            Assert.False(File.Exists(Path.Combine(root, asset.FileName)));
            Assert.Empty(Directory.GetFiles(root, "*.partial"));
        });
    }

    [Fact]
    public async Task RepeatedTruncationFailsAfterThreeAttempts()
    {
        using var proxy = new DownloadProxy((_, stream, token) => Respond(stream, 200, Payload[..10000], Payload.Length, token));
        await WithDownload(proxy, async (downloader, file) =>
        {
            await Assert.ThrowsAnyAsync<IOException>(() => downloader.DownloadFile(PackageUrl, file, _ => { }, null, 0.1, default));
            Assert.Equal(3, proxy.Requests.Count);
        });
    }

    [Fact]
    public async Task RepeatedInterruptionsResumeOnlyWithMatchingStrongValidatorAndRange()
    {
        using var proxy = new DownloadProxy((attempt, headers, stream, token) =>
        {
            var offset = (attempt - 1) * 10000;
            if (attempt > 1)
            {
                Assert.Contains($"Range: bytes={offset}-", headers);
                Assert.Contains("If-Range: \"fixture-v1\"", headers);
            }
            return Respond(stream, attempt == 1 ? 200 : 206,
                attempt < 3 ? Payload[offset..(offset + 10000)] : Payload[offset..], Payload.Length - offset, token,
                "ETag: \"fixture-v1\"\r\n" + (attempt == 1 ? "" : $"Content-Range: bytes {offset}-{Payload.Length - 1}/{Payload.Length}\r\n"));
        });
        await WithDownload(proxy, async (downloader, file) =>
        {
            await downloader.DownloadFile(PackageUrl, file, _ => { }, null, 0.1, default);
            Assert.Equal(Payload, await File.ReadAllBytesAsync(file));
            Assert.Equal(3, proxy.Requests.Count);
        });
    }

    [Fact]
    public async Task IgnoredRangeRestartsRatherThanAppendingAFullResponse()
    {
        using var proxy = new DownloadProxy((attempt, headers, stream, token) =>
        {
            if (attempt == 2) Assert.Contains("Range: bytes=10000-", headers);
            return Respond(stream, 200, attempt == 1 ? Payload[..10000] : Payload, Payload.Length, token, "ETag: \"fixture-v1\"\r\n");
        });
        await WithDownload(proxy, async (downloader, file) =>
        {
            await downloader.DownloadFile(PackageUrl, file, _ => { }, null, 0.1, default);
            Assert.Equal(Payload, await File.ReadAllBytesAsync(file));
            Assert.Equal(2, proxy.Requests.Count);
        });
    }

    [Theory]
    [InlineData("offset")]
    [InlineData("etag")]
    [InlineData("total")]
    [InlineData("content-length")]
    public async Task InconsistentResumeMetadataIsRejected(string mismatch)
    {
        using var proxy = new DownloadProxy((attempt, _, stream, token) =>
            Respond(stream, attempt == 1 ? 200 : 206, attempt == 1 ? Payload[..10000] : Payload[10000..],
                attempt == 1 ? Payload.Length : Payload.Length - 10000 - (mismatch == "content-length" ? 1 : 0), token,
                $"ETag: \"{(attempt == 1 || mismatch != "etag" ? "fixture-v1" : "changed")}\"\r\n" +
                (attempt == 1 ? "" : $"Content-Range: bytes {(mismatch == "offset" ? 9999 : 10000)}-{Payload.Length - 1}/{Payload.Length + (mismatch == "total" ? 1 : 0)}\r\n")));
        await WithDownload(proxy, async (downloader, file) =>
        {
            await Assert.ThrowsAsync<InvalidDataException>(() => downloader.DownloadFile(PackageUrl, file, _ => { }, null, 0.1, default));
            Assert.Equal(2, proxy.Requests.Count);
        });
    }

    [Fact]
    public async Task WeakValidatorCannotAuthorizeAppending()
    {
        using var proxy = new DownloadProxy((attempt, headers, stream, token) =>
        {
            Assert.DoesNotContain("Range:", headers);
            return Respond(stream, 200, attempt == 1 ? Payload[..10000] : Payload, Payload.Length, token, "ETag: W/\"weak\"\r\n");
        });
        await WithDownload(proxy, async (downloader, file) =>
        {
            await downloader.DownloadFile(PackageUrl, file, _ => { }, null, 0.1, default);
            Assert.Equal(Payload, await File.ReadAllBytesAsync(file));
            Assert.Equal(2, proxy.Requests.Count);
        });
    }

    [Fact]
    public async Task RetryDoesNotResetTheTotalDeadline()
    {
        using var proxy = new DownloadProxy(async (attempt, stream, token) =>
        {
            await Task.Delay(attempt == 1 ? 200 : 600, token);
            await Respond(stream, 200, attempt == 1 ? Payload[..10000] : Payload, Payload.Length, token);
        });
        await WithDownload(proxy, async (downloader, file) =>
        {
            var download = downloader.DownloadFile(PackageUrl, file, _ => { }, null, 0.015, default);
            await Assert.ThrowsAsync<TimeoutException>(() => download.WaitAsync(TimeSpan.FromSeconds(3)));
            Assert.True(download.IsCompleted);
            Assert.Equal(2, proxy.Requests.Count);
        });
    }

    [Theory]
    [InlineData(401)]
    [InlineData(403)]
    [InlineData(404)]
    [InlineData(407)]
    [InlineData(429)]
    public async Task HttpErrorsAreNotRetried(int status)
    {
        using var proxy = new DownloadProxy((_, stream, token) => Respond(stream, status, [], 0, token));
        await WithDownload(proxy, async (downloader, file) =>
        {
            var error = await Assert.ThrowsAsync<HttpRequestException>(() => downloader.DownloadFile(PackageUrl, file, _ => { }, null, 0.1, default));
            Assert.Equal((HttpStatusCode)status, error.StatusCode);
            Assert.Single(proxy.Requests);
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StalledBodyHonorsDeadlineAndCallerCancellationWithoutRetry(bool callerCancels)
    {
        using var proxy = new DownloadProxy(async (_, stream, token) =>
        {
            await Respond(stream, 200, Payload[..10000], Payload.Length, token);
            await Task.Delay(Timeout.Infinite, token);
        });
        await WithDownload(proxy, async (downloader, file) =>
        {
            using var cancel = new CancellationTokenSource();
            if (callerCancels) cancel.CancelAfter(TimeSpan.FromMilliseconds(300));
            // The external guard makes the old, unbounded body read fail without hanging the suite.
            var download = downloader.DownloadFile(PackageUrl, file, _ => { }, null,
                callerCancels ? 0.1 : 0.005, cancel.Token);
            try
            {
                if (callerCancels)
                    await Assert.ThrowsAnyAsync<OperationCanceledException>(() => download.WaitAsync(TimeSpan.FromSeconds(3)));
                else
                    await Assert.ThrowsAsync<TimeoutException>(() => download.WaitAsync(TimeSpan.FromSeconds(3)));
                Assert.True(download.IsCompleted, "The downloader must enforce its own deadline.");
                Assert.Single(proxy.Requests);
            }
            finally
            {
                cancel.Cancel();
                try { await download; } catch (OperationCanceledException) { } catch (TimeoutException) { }
            }
        });
    }

    private static async Task WithDownload(DownloadProxy proxy, Func<UpdateDownloader, string, Task> action)
    {
        var root = Directory.CreateTempSubdirectory("update-transport-");
        try
        {
            NetworkProxyService.Apply(new() { Enabled = true, Address = proxy.Address });
            await action(new UpdateDownloader(), Path.Combine(root.FullName, "package.partial"));
        }
        finally { NetworkProxyService.Apply(new()); root.Delete(true); }
    }

    private static async Task Respond(NetworkStream stream, int status, byte[] body, int length, CancellationToken token, string extraHeaders = "")
    {
        var header = Encoding.ASCII.GetBytes($"HTTP/1.1 {status} Fixture\r\nContent-Length: {length}\r\n{extraHeaders}Connection: close\r\n\r\n");
        await stream.WriteAsync(header, token);
        await stream.WriteAsync(body, token);
        await stream.FlushAsync(token);
    }

    private sealed class DownloadProxy : IDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stop = new();
        private readonly Task _server;
        public ConcurrentQueue<string> Requests { get; } = new();
        public ConcurrentQueue<string> Headers { get; } = new();
        public string Address { get; }

        public DownloadProxy(Func<int, NetworkStream, CancellationToken, Task> respond)
            : this((attempt, _, stream, token) => respond(attempt, stream, token)) { }

        public DownloadProxy(Func<int, string, NetworkStream, CancellationToken, Task> respond)
        {
            _listener.Start();
            Address = $"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}";
            _server = Task.Run(async () =>
            {
                try
                {
                    while (!_stop.IsCancellationRequested)
                    {
                        using var socket = await _listener.AcceptTcpClientAsync(_stop.Token);
                        using var stream = socket.GetStream();
                        using var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);
                        var request = await reader.ReadLineAsync(_stop.Token);
                        var headers = new List<string>();
                        string? line;
                        while (!string.IsNullOrEmpty(line = await reader.ReadLineAsync(_stop.Token))) headers.Add(line);
                        Headers.Enqueue(string.Join("\n", headers));
                        Requests.Enqueue(request!);
                        await respond(Requests.Count, string.Join("\n", headers), stream, _stop.Token);
                    }
                }
                catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
            });
        }

        public void Dispose()
        {
            _stop.Cancel(); _listener.Stop();
            _server.GetAwaiter().GetResult(); _stop.Dispose();
        }
    }
}
