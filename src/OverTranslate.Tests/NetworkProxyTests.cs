using System.Net;
using System.Net.Sockets;
using System.Text;
using OverTranslate.Models;
using OverTranslate.Services;
using Xunit;

namespace OverTranslate.Tests;

[CollectionDefinition("Network proxy", DisableParallelization = true)]
public sealed class NetworkProxyCollection;

[Collection("Network proxy")]
public sealed class NetworkProxyTests
{
    [Theory]
    [InlineData("http://127.0.0.1:10808")]
    [InlineData("https://proxy.example:443")]
    [InlineData("socks5://[::1]:1080")]
    public void AcceptsSupportedProxyAddresses(string address) => Assert.True(NetworkProxyService.TryParseAddress(address, out _));

    [Theory]
    [InlineData("")]
    [InlineData("127.0.0.1:10808")]
    [InlineData("ftp://proxy.example:21")]
    [InlineData("http://user:password@proxy.example:80")]
    [InlineData("http://proxy.example:80/path")]
    [InlineData("http://proxy.example:80?token=fixture")]
    [InlineData("http://proxy.example:80#fragment")]
    [InlineData("http://proxy.example:0")]
    [InlineData("http://proxy.example:80\r\nHeader: value")]
    public void RejectsMalformedOrCredentialBearingAddresses(string address) => Assert.False(NetworkProxyService.TryParseAddress(address, out _));

    [Fact]
    public void SettingsRoundTripAndOldSettingsKeepProxyOff()
    {
        Assert.False(SettingsService.Parse("{}").NetworkProxy.Enabled);
        var model = new AppSettings { NetworkProxy = new() { Enabled = true, Address = "http://127.0.0.1:10808",
            Features = NetworkFeature.Updates | NetworkFeature.OnlineOcr } };
        var parsed = SettingsService.Parse(SettingsService.Serialize(model));
        Assert.True(parsed.NetworkProxy.Enabled);
        Assert.Equal(model.NetworkProxy.Address, parsed.NetworkProxy.Address);
        Assert.Equal(model.NetworkProxy.Features, parsed.NetworkProxy.Features);
        Assert.Equal(NetworkFeature.All, NetworkProxyService.Features.Aggregate((NetworkFeature)0, (flags, item) => flags | item.Feature));
    }

    [Fact]
    public void CachedCredentialsNeverReachACustomProxyEvenAfterItIsDisabled()
    {
        var systemUri = new Uri("http://system.example:8080");
        var customUri = new Uri("http://custom.example:8080");
        var system = new WebProxy(systemUri) { Credentials = new NetworkCredential("fixture-user", "fixture-password") };
        var router = new NetworkProxyService.FeatureProxy(NetworkFeature.Updates, system);
        var captured = router.Credentials!;
        try
        {
            NetworkProxyService.Apply(new());
            Assert.NotNull(captured.GetCredential(systemUri, "Basic"));
            NetworkProxyService.Apply(new() { Enabled = true, Address = customUri.AbsoluteUri });
            Assert.Null(captured.GetCredential(customUri, "Basic"));
            NetworkProxyService.Apply(new());
            Assert.Null(captured.GetCredential(customUri, "Basic"));
            Assert.NotNull(captured.GetCredential(systemUri, "Basic"));
        }
        finally { NetworkProxyService.Apply(new()); }
    }

    [Fact]
    public void InvalidEnabledProxyBlocksSelectedRequestsAndLocalhostAlwaysBypasses()
    {
        var system = new WebProxy("http://127.0.0.1:19991");
        var router = new NetworkProxyService.FeatureProxy(NetworkFeature.Updates, system);
        try
        {
            NetworkProxyService.Apply(new() { Enabled = true, Address = "invalid", Features = NetworkFeature.Updates });
            Assert.False(router.IsBypassed(new Uri("https://example.invalid")));
            var local = new Uri("http://localhost:8080/ocr");
            Assert.True(router.IsBypassed(local));
            Assert.Equal(local, router.GetProxy(local));
            var ipv6 = new Uri("http://[::1]:8080/v1");
            Assert.True(router.IsBypassed(ipv6));
        }
        finally { NetworkProxyService.Apply(new()); }
    }

    [Theory]
    [InlineData(NetworkFeature.Updates)]
    [InlineData(NetworkFeature.Translation)]
    [InlineData(NetworkFeature.Dictionary)]
    [InlineData(NetworkFeature.Speech)]
    [InlineData(NetworkFeature.OnlineOcr)]
    [InlineData(NetworkFeature.ModelDownloads)]
    [InlineData(NetworkFeature.Library)]
    [InlineData(NetworkFeature.Diagnostics)]
    public async Task ExistingClientRoutesOnlySelectedFeaturesAndSwitchesWithoutRestart(NetworkFeature feature)
    {
        using var system = new TinyProxy("system");
        using var custom = new TinyProxy("custom");
        using var changed = new TinyProxy("changed");
        using var client = new HttpClient(new HttpClientHandler { Proxy = new NetworkProxyService.FeatureProxy(feature, new WebProxy(system.Address)) })
            { Timeout = TimeSpan.FromSeconds(5) };
        const string destination = "http://external.invalid/proxy-test";
        try
        {
            NetworkProxyService.Apply(new() { Enabled = true, Address = custom.Address, Features = NetworkFeature.All & ~feature });
            Assert.Equal("system", await client.GetStringAsync(destination));
            NetworkProxyService.Apply(new() { Enabled = true, Address = custom.Address, Features = feature });
            Assert.Equal("custom", await client.GetStringAsync(destination));
            NetworkProxyService.Apply(new() { Enabled = true, Address = changed.Address, Features = feature });
            Assert.Equal("changed", await client.GetStringAsync(destination));
            NetworkProxyService.Apply(new() { Enabled = false, Address = custom.Address, Features = feature });
            Assert.Equal("system", await client.GetStringAsync(destination));
            NetworkProxyService.Apply(new() { Enabled = true, Address = "http://127.0.0.1:1", Features = feature });
            await Assert.ThrowsAsync<HttpRequestException>(() => client.GetStringAsync(destination));
            Assert.Equal(2, system.Requests);
            NetworkProxyService.Apply(new() { Enabled = true, Address = "invalid", Features = feature });
            await Assert.ThrowsAsync<NotSupportedException>(() => client.GetStringAsync(destination));
            Assert.Equal(2, system.Requests);
        }
        finally { NetworkProxyService.Apply(new()); }
    }

    [Theory]
    [InlineData(408, "HTTP 408")]
    [InlineData(429, "HTTP 429")]
    [InlineData(503, "HTTP 503")]
    public void UpdateErrorsExposeStatusWithoutSensitiveDetails(int status, string expected)
    {
        var message = UpdateService.DescribeCheckError(new HttpRequestException("sensitive fixture URL", null, (HttpStatusCode)status));
        Assert.Contains(expected, message); Assert.DoesNotContain("fixture", message);
        Assert.Contains("超时", UpdateService.DescribeCheckError(new TaskCanceledException("private")));
        Assert.Contains("连接", UpdateService.DescribeCheckError(new HttpRequestException("private")));
    }

    private sealed class TinyProxy : IDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stop = new();
        private readonly Task _server;
        private int _requests;
        internal int Requests => Volatile.Read(ref _requests);
        internal string Address { get; }
        internal TinyProxy(string body)
        {
            _listener.Start(); Address = $"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}";
            _server = Serve(body);
        }
        private async Task Serve(string body)
        {
            try
            {
                while (!_stop.IsCancellationRequested)
                {
                    using var connection = await _listener.AcceptTcpClientAsync(_stop.Token);
                    await using var stream = connection.GetStream();
                    using var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);
                    string? line;
                    do { line = await reader.ReadLineAsync(_stop.Token); } while (!string.IsNullOrEmpty(line));
                    Interlocked.Increment(ref _requests);
                    var response = Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n{body}");
                    await stream.WriteAsync(response, _stop.Token);
                }
            }
            catch (OperationCanceledException) { }
        }
        public void Dispose()
        {
            _stop.Cancel(); _listener.Stop();
            _server.GetAwaiter().GetResult(); _stop.Dispose();
        }
    }
}
