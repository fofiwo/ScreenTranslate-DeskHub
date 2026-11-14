using System.Net;
using OverTranslate.Services.Lan;
using Xunit;

namespace OverTranslate.Tests;

public sealed class SyncActivityContentTests
{
    [Theory]
    [InlineData("text", "剪贴板 · 文本", true)]
    [InlineData("image", "剪贴板 · 图片", true)]
    [InlineData("text", "剪贴板 · 文本", false)]
    [InlineData("image", "剪贴板 · 图片", false)]
    public async Task Clipboard_type_survives_both_directions_rejection_and_restart(string kind, string label, bool accepted)
    {
        var root = Path.Combine(Path.GetTempPath(), "sync-content-" + Guid.NewGuid().ToString("N"));
        var incomingPath = Path.Combine(root, "incoming.json");
        var outgoingPath = Path.Combine(root, "outgoing.json");
        await using var host = new LanNode("fixture-A", () => root) { Activities = new(incomingPath), ClipboardEnabled = accepted };
        await using var client = new LanNode("fixture-B", () => root) { Activities = new(outgoingPath) };
        host.ConfirmPairAsync = (_, _) => Task.FromResult(true);
        // 只验证类型元数据的传递，不读写系统剪贴板或解码图片。
        host.ApplyClipboardAsync = (_, _) => Task.CompletedTask;
        try
        {
            await host.ListenAsync(IPAddress.Loopback, 0);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            await client.ConnectAsync(IPAddress.Loopback, host.Port, host.PairCode, timeout.Token);
            Assert.Equal(accepted, await client.SendClipboardAsync(new(kind, "private fixture body"u8.ToArray()), timeout.Token));
            foreach (var (path, incoming) in new[] { (incomingPath, true), (outgoingPath, false) })
            {
                var activity = Assert.Single(new SyncActivityStore(path).Snapshot().Where(x => x.Kind == SyncKind.Clipboard));
                Assert.Equal(label, activity.KindLabel);
                Assert.Contains(label, activity.Display);
                Assert.Equal(incoming, activity.Incoming);
                Assert.Equal(accepted ? SyncState.Completed : SyncState.Rejected, activity.State);
                Assert.Empty(activity.Name);
                Assert.DoesNotContain("private fixture body", File.ReadAllText(path));
            }
        }
        finally { await client.StopAsync(); await host.StopAsync(); if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public void Old_clipboard_records_do_not_guess_type_from_size()
    {
        var activity = System.Text.Json.JsonSerializer.Deserialize<SyncActivity>(
            """{"Id":"legacy","Kind":0,"Incoming":true,"Peer":"fixture","State":3,"Total":5854}""")!;
        Assert.Equal("剪贴板", activity.KindLabel);
        Assert.Equal("文件", new SyncActivity("files", SyncKind.Files, false, "fixture", SyncState.Completed).KindLabel);
    }
}
