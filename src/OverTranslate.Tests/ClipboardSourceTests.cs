using System.Net;
using System.Text.Json;
using OverTranslate.Services.ClipboardHistory;
using OverTranslate.Services.Lan;
using Xunit;

namespace OverTranslate.Tests;

public sealed class ClipboardSourceTests
{
    [Fact]
    public void Older_clipboard_headers_without_origin_remain_valid()
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new
        {
            Kind = "text", Id = Guid.NewGuid().ToString("N"), Length = 1, Hash = LanProtocol.Hash([1])
        });
        var header = JsonSerializer.Deserialize<LanHeader>(bytes)!;
        Assert.Equal("", header.Origin);
        LanProtocol.ValidatePayload(header);
    }

    [Theory]
    [InlineData("fixture-PC", "来自 fixture-PC")]
    [InlineData("received", "局域网")]
    [InlineData("", "局域网")]
    [InlineData("bad\nname", "局域网")]
    public void Clipboard_marker_describes_device_without_treating_legacy_markers_as_names(string marker, string expected)
        => Assert.Equal(expected, LanClipboardSync.DescribeOrigin(marker));

    [Fact]
    public void Source_is_saved_searchable_and_updated_to_the_latest_copy_without_duplicate_records()
    {
        var root = Path.Combine(Path.GetTempPath(), "clipboard-origin-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new ClipboardHistoryStore(root);
            var time = DateTimeOffset.UtcNow;
            store.Add(new() { Text = "same fixture", Source = "来自 fixture-A", CopiedAt = time });
            store.Add(new() { Text = "same fixture", Source = "来自 fixture-B", CopiedAt = time.AddSeconds(1) });
            var record = Assert.Single(new ClipboardHistoryStore(root).Read());
            Assert.Equal("来自 fixture-B", record.SourceLabel); Assert.True(record.Matches("全部", "fixture-B"));
            Assert.Equal("来源：fixture.exe", (record with { Source = "fixture.exe" }).SourceLabel);
            Assert.Equal("", (record with { Source = "" }).SourceLabel);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Theory]
    [InlineData("来自 fixture-A", "MouseWithoutBorders.exe", 0, "来自 fixture-A")]
    [InlineData("来自 fixture-A", "MouseWithoutBorders.exe", 2000, "来自 fixture-A")]
    [InlineData("MouseWithoutBorders.exe", "来自 fixture-A", 0, "来自 fixture-A")]
    [InlineData("MouseWithoutBorders.exe", "来自 fixture-A", -2000, "来自 fixture-A")]
    [InlineData("来自 fixture-A", "powertoys.mousewithoutborders.EXE", 500, "来自 fixture-A")]
    [InlineData("来自 fixture-A", "MouseWithoutBorders.exe", 2001, "MouseWithoutBorders.exe")]
    [InlineData("MouseWithoutBorders.exe", "来自 fixture-A", -2001, "MouseWithoutBorders.exe")]
    [InlineData("来自 fixture-A", "notepad.exe", 1, "notepad.exe")]
    [InlineData("来自 fixture-A", "来自 fixture-B", 1, "来自 fixture-B")]
    [InlineData("来自 fixture-A", "notepad.exe", -1, "来自 fixture-A")]
    [InlineData("来自 fixture-A", "", 1, "来自 fixture-A")]
    [InlineData("来自 fixture-A", "FakeMouseWithoutBorders.exe", 1, "FakeMouseWithoutBorders.exe")]
    public void Duplicate_sources_prefer_a_device_only_for_nearby_relay_copies(
        string firstSource, string nextSource, int milliseconds, string expectedSource)
    {
        var root = Path.Combine(Path.GetTempPath(), "clipboard-source-merge-" + Guid.NewGuid().ToString("N"));
        try
        {
            foreach (var withImage in new[] { false, true })
            {
                var folder = Path.Combine(root, withImage ? "image" : "text");
                var store = new ClipboardHistoryStore(folder);
                var time = DateTimeOffset.UtcNow;
                byte[]? png = withImage ? [1, 2, 3] : null;
                var first = store.Add(new() { Text = withImage ? "" : "same fixture", Source = firstSource, CopiedAt = time }, png);
                store.SetPinned(first.Id, true);
                var nextTime = time.AddMilliseconds(milliseconds);
                store.Add(first with { Source = nextSource, CopiedAt = nextTime }, png);

                var record = Assert.Single(new ClipboardHistoryStore(folder).Read());
                Assert.Equal(expectedSource, record.Source);
                Assert.Equal(first.Id, record.Id);
                Assert.Equal(nextTime > time ? nextTime : time, record.CopiedAt);
                Assert.Equal(nextTime < time ? nextTime : time, record.CreatedAt);
                Assert.True(record.IsPinned);
                Assert.Equal(withImage, record.HasImage);
                if (withImage) Assert.Equal(png, store.ReadImage(record.Id));
            }
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Theory]
    [InlineData("MouseWithoutBorders.exe")]
    [InlineData("PowerToys.MouseWithoutBorders.exe")]
    [InlineData("mousewithoutborders.EXE")]
    public void Relay_source_label_reports_unknown_device_without_changing_the_saved_source(string source)
    {
        var record = new ClipboardRecord { Source = source };
        Assert.Equal("跨设备 · Mouse Without Borders（电脑名未知）", record.SourceLabel);
        Assert.Equal(source, record.Source);
        Assert.True(record.Matches("全部", source));
    }

    [Theory]
    [InlineData("bad\nname")]
    [InlineData(" ")]
    [InlineData(null)]
    public void Invalid_network_origin_is_rejected(string? origin)
        => Assert.Throws<InvalidDataException>(() => LanProtocol.ValidatePayload(new("text", Guid.NewGuid().ToString("N"),
            Length: 1, Hash: LanProtocol.Hash([1]), Origin: origin!)));

    [Fact]
    public async Task Relay_preserves_the_original_device_for_text_and_images()
    {
        await using var ab = new LanNode("fixture-hub", () => "");
        await using var ac = new LanNode("fixture-hub", () => "");
        await using var b = new LanNode("fixture-origin", () => "");
        await using var c = new LanNode("fixture-target", () => "");
        using var relay = new LanClipboardRelay(); relay.Add(ab); relay.Add(ac);
        ab.ConfirmPairAsync = ac.ConfirmPairAsync = (_, _) => Task.FromResult(true);
        ab.ApplyClipboardAsync = (_, _) => Task.CompletedTask;
        await ab.ListenAsync(IPAddress.Loopback, 0); await ac.ListenAsync(IPAddress.Loopback, 0);
        await b.ConnectAsync(IPAddress.Loopback, ab.Port, ab.PairCode);
        await c.ConnectAsync(IPAddress.Loopback, ac.Port, ac.PairCode);
        foreach (var kind in new[] { "text", "image" })
        {
            var received = new TaskCompletionSource<LanClipboardPayload>(TaskCreationOptions.RunContinuationsAsynchronously);
            c.ApplyClipboardAsync = (payload, _) => { received.TrySetResult(payload); return Task.CompletedTask; };
            Assert.True(await b.SendClipboardAsync(new(kind, [1, 2])));
            var payload = await received.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal("fixture-origin", payload.Origin); Assert.Equal(kind, payload.Kind);
        }
    }
}
