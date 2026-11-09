using System.Drawing;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using OverTranslate.Models;
using OverTranslate.Services;
using OverTranslate.Services.Ocr;
using OverTranslate.Services.Realtime;
using Xunit;

namespace OverTranslate.Tests;

public class ScreenTranslateProviderTests
{
    private const string Answer = """{"width":320,"height":120,"blocks":[{"text":"Hello","x":20,"y":30,"width":100,"height":24}]}""";
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => send(request, token);
    }

    [Fact]
    public async Task SendsDocumentedImageContract_AndMapsResponseToCapturePixels()
    {
        using var provider = new HttpOcrProvider(new Handler(async (request, token) =>
        {
            Assert.Equal("127.0.0.1", request.RequestUri!.Host);
            using var doc = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token));
            Assert.Equal("EN", doc.RootElement.GetProperty("language").GetString());
            Assert.Equal(640, doc.RootElement.GetProperty("width").GetInt32());
            var bytes = Convert.FromBase64String(doc.RootElement.GetProperty("image").GetString()!);
            Assert.Equal(new byte[] { 137, 80, 78, 71 }, bytes.Take(4));
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Answer) };
        }));
        using var bitmap = new Bitmap(640, 240);
        var result = await provider.ReadAsync(bitmap, "EN", new OcrProviderSettings { Provider = OcrProviderKind.Http }, false, default);
        Assert.Equal(new System.Windows.Rect(40, 60, 200, 48), Assert.Single(result!).Bounds);
    }

    [Fact]
    public async Task BusyAndRateLimitedLiveFramesAreSkipped_InsteadOfQueued()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var count = 0;
        using var provider = new HttpOcrProvider(new Handler(async (_, token) =>
        {
            Interlocked.Increment(ref count); entered.SetResult(); await release.Task.WaitAsync(token);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Answer) };
        }));
        using var bitmap = new Bitmap(320, 120);
        var options = new OcrProviderSettings { Provider = OcrProviderKind.Http, MinimumIntervalMs = 60000 };
        var first = provider.ReadAsync(bitmap, "EN", options, true, default);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Null(await provider.ReadAsync(bitmap, "EN", options, true, default));
        release.SetResult();
        Assert.Single((await first)!);
        Assert.Null(await provider.ReadAsync(bitmap, "EN", options, true, default));
        Assert.Equal(1, count);
    }

    [Fact]
    public async Task CancellationStopsNetworkWork_AndReleasesTheSlot()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var count = 0;
        using var provider = new HttpOcrProvider(new Handler(async (_, token) =>
        {
            if (Interlocked.Increment(ref count) == 1) { entered.SetResult(); await Task.Delay(Timeout.Infinite, token); }
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Answer) };
        }));
        using var bitmap = new Bitmap(320, 120);
        using var cancellation = new CancellationTokenSource();
        var options = new OcrProviderSettings { Provider = OcrProviderKind.Http };
        var pending = provider.ReadAsync(bitmap, "EN", options, false, cancellation.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await pending);
        Assert.Single((await provider.ReadAsync(bitmap, "EN", options, false, default))!);
    }

    [Fact]
    public void ProfilesKeepWindowRelativePlacement_AcrossNegativeOriginAndResize()
    {
        var original = new Rectangle(-1800, 100, 1200, 800);
        var blocks = new[] { new RealtimeBlockPlacement(new Rectangle(-1680, 180, 240, 80), RealtimeBlockMode.Panel),
            new RealtimeBlockPlacement(new Rectangle(-1080, 580, 360, 160)) };
        var profile = WindowRegionGeometry.Normalize(original, blocks);
        var restored = WindowRegionGeometry.Restore(new Rectangle(100, 200, 1800, 1200), profile);
        Assert.Equal(new Rectangle(280, 320, 360, 120), restored[0].Bounds);
        Assert.Equal(new Rectangle(1180, 920, 540, 240), restored[1].Bounds);
        Assert.Equal(RealtimeBlockMode.Panel, restored[0].Mode);
        Assert.Empty(WindowRegionGeometry.Restore(original, [new ProfileRegion { X = double.NaN, Width = 1, Height = 1 }]));
    }

    [Fact]
    public void ProfilesRequireExecutableAndWindowTitle_AndRoundTripSettings()
    {
        var profile = new ApplicationProfile { ExecutablePath = @"C:\Apps\reader.exe", WindowTitle = "Document A",
            OcrProvider = OcrProviderKind.Http, Regions = [new ProfileRegion { X = .1, Y = .1, Width = .5, Height = .2 }] };
        var loaded = SettingsService.Parse(SettingsService.Serialize(new AppSettings { ApplicationProfiles = [profile], Ocr = new OcrProviderSettings { Provider = OcrProviderKind.Http } }));
        Assert.Equal(OcrProviderKind.Http, loaded.Ocr.Provider);
        Assert.Equal(.5, Assert.Single(Assert.Single(loaded.ApplicationProfiles).Regions).Width);
        Assert.False(loaded.ApplicationProfiles[0].AutoAttach);
    }

    [Fact]
    public async Task BuiltInModelReadsARealBitmap()
    {
        using var bitmap = new Bitmap(800, 180);
        using (var graphics = Graphics.FromImage(bitmap))
        using (var font = new Font("Arial", 36))
        { graphics.Clear(Color.White); graphics.DrawString("Screen translation test", font, Brushes.Black, 20, 40); }
        using var service = new OcrService();
        var result = await service.RecognizeAsync(bitmap, "EN");
        Assert.Contains("translation", string.Join(" ", result.Select(block => block.Text)), StringComparison.OrdinalIgnoreCase);
        Assert.All(result, block => Assert.True(block.Bounds.X >= 0 && block.Bounds.Y >= 0 && block.Bounds.Right <= bitmap.Width && block.Bounds.Bottom <= bitmap.Height));
    }

    [Fact]
    public async Task IndependentOcrReadsRealLinesAndPreservesTheirBreak()
    {
        using var bitmap = new Bitmap(800, 220);
        using (var graphics = Graphics.FromImage(bitmap))
        using (var font = new Font("Arial", 30))
        {
            graphics.Clear(Color.White);
            graphics.DrawString("First original line", font, Brushes.Black, 20, 20);
            graphics.DrawString("Second original line", font, Brushes.Black, 20, 110);
        }
        using var service = new OcrService();
        var lines = await service.RecognizeTextAsync(bitmap, "EN");
        var text = OcrTextFormatter.Format(lines, true);
        Assert.Contains("First", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Second", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains('\n', text);
    }
}
