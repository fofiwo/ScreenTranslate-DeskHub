using System.Net;
using OverTranslate.Services.Lan;
using Xunit;

namespace OverTranslate.Tests;

public sealed class LanClipboardPriorityTests
{
    [Fact]
    public async Task ClipboardBothWaysCompletesWhileFileReceiverIsBlocked()
    {
        var root = Path.Combine(Path.GetTempPath(), "lan-priority-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        await using var host = new LanNode("fixture-A", () => Path.Combine(root, "received"));
        await using var client = new LanNode("fixture-B", () => root);
        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var atHost = new List<string>(); var atClient = new List<string>();
        host.ConfirmPairAsync = (_, _) => Task.FromResult(true);
        host.ApplyClipboardAsync = (p, _) => { atHost.Add(p.Kind); return Task.CompletedTask; };
        client.ApplyClipboardAsync = (p, _) => { atClient.Add(p.Kind); return Task.CompletedTask; };
        host.Progress += (_, _) => { entered.TrySetResult(); release.Wait(TimeSpan.FromSeconds(10)); };
        Task? sending = null;
        try
        {
            var path = Path.Combine(root, "fixture.bin");
            await File.WriteAllBytesAsync(path, new byte[1024 * 1024]);
            await host.ListenAsync(IPAddress.Loopback, 0);
            await client.ConnectAsync(IPAddress.Loopback, host.Port, host.PairCode);
            sending = client.SendFileAsync(path);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await Task.WhenAll(client.SendClipboardAsync(new("text", [1])),
                host.SendClipboardAsync(new("image", [2]))).WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Equal(new[] { "text" }, atHost);
            Assert.Equal(new[] { "image" }, atClient);
            Assert.False(sending.IsCompleted);
            release.Set();
            await sending.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(new byte[1024 * 1024], await File.ReadAllBytesAsync(Path.Combine(root, "received", "fixture.bin")));
        }
        finally
        {
            release.Set();
            await client.StopAsync(); await host.StopAsync();
            if (sending is not null) { try { await sending; } catch (Exception) { } }
            Directory.Delete(root, true);
        }
    }
}
