using System.Net;
using System.Text.Json;
using OverTranslate.Services.Lan;
using OverTranslate.Services.Library;
using Xunit;

namespace OverTranslate.Tests;

public sealed class UnifiedLanSyncTests
{
    [Fact]
    public async Task InvalidLibraryDoesNotDropClipboardAndLaterValidSnapshotCanApply()
    {
        var root = Path.Combine(Path.GetTempPath(), "unified-invalid-" + Guid.NewGuid().ToString("N"));
        var source = new LibraryStore(Path.Combine(root, "a.json"));
        var target = new LibraryStore(Path.Combine(root, "b.json"));
        source.SavePrompt(new() { Content = "valid fixture" });
        await using var host = new LanNode("A", () => root) { LibraryEnabled = true };
        await using var client = new LanNode("B", () => root) { LibraryEnabled = true };
        await using var receiver = new LanLibrarySync(host, () => target);
        host.ConfirmPairAsync = (_, _) => Task.FromResult(true);
        var copies = 0;
        host.ApplyClipboardAsync = (_, _) => { copies++; return Task.CompletedTask; };
        try
        {
            await host.ListenAsync(IPAddress.Loopback, 0);
            await client.ConnectAsync(IPAddress.Loopback, host.Port, host.PairCode);
            Assert.False(await client.SendLibraryAsync("{bad"u8.ToArray()));
            Assert.Empty(target.Read().Prompts);
            Assert.True(await client.SendClipboardAsync(new("text", "fixture"u8.ToArray())));
            Assert.Equal(1, copies);
            Assert.True(await client.SendLibraryAsync(JsonSerializer.SerializeToUtf8Bytes(source.ExportSync())));
            Assert.Single(target.Read().Prompts);
        }
        finally { await client.StopAsync(); await host.StopAsync(); Directory.Delete(root, true); }
    }

    [Fact]
    public async Task TransientStoreOpenFailureCanRecoverWithoutRestart()
    {
        var root = Path.Combine(Path.GetTempPath(), "unified-retry-" + Guid.NewGuid().ToString("N"));
        var a = new LibraryStore(Path.Combine(root, "a.json"));
        var b = new LibraryStore(Path.Combine(root, "b.json"));
        a.SavePrompt(new() { Content = "retry fixture" });
        var attempts = 0;
        await using var host = new LanNode("A", () => root) { LibraryEnabled = true };
        await using var client = new LanNode("B", () => root) { LibraryEnabled = true };
        await using var left = new LanLibrarySync(host, () =>
        {
            if (Interlocked.Increment(ref attempts) == 1) throw new IOException("fixture temporary file lock");
            return a;
        }, TimeSpan.FromMilliseconds(30));
        await using var right = new LanLibrarySync(client, () => b, TimeSpan.FromMilliseconds(30));
        host.ConfirmPairAsync = (_, _) => Task.FromResult(true);
        try
        {
            await host.ListenAsync(IPAddress.Loopback, 0);
            await client.ConnectAsync(IPAddress.Loopback, host.Port, host.PairCode);
            await Until(() => b.Read().Prompts.Count == 1);
            Assert.True(attempts >= 2);
        }
        finally { await client.StopAsync(); await host.StopAsync(); Directory.Delete(root, true); }
    }

    [Fact]
    public async Task PausingLibraryRejectsQueuedSendWithoutDroppingConnection()
    {
        var root = Path.GetTempPath();
        await using var host = new LanNode("A", () => root) { LibraryEnabled = true };
        await using var client = new LanNode("B", () => root) { LibraryEnabled = true };
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var received = 0;
        host.ApplyLibraryAsync = async (_, token) =>
        { Interlocked.Increment(ref received); entered.TrySetResult(); await release.Task.WaitAsync(token); };
        host.ConfirmPairAsync = (_, _) => Task.FromResult(true);
        try
        {
            await host.ListenAsync(IPAddress.Loopback, 0);
            await client.ConnectAsync(IPAddress.Loopback, host.Port, host.PairCode);
            var first = client.SendLibraryAsync([1]);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var queued = client.SendLibraryAsync([2]);
            Assert.False(queued.IsCompleted);
            client.LibraryEnabled = false;
            release.TrySetResult();
            Assert.True(await first);
            await Assert.ThrowsAsync<InvalidOperationException>(() => queued);
            Assert.Equal(1, received);
            Assert.True(client.IsConnected);
            client.LibraryEnabled = true;
            Assert.True(await client.SendLibraryAsync([3]));
        }
        finally { release.TrySetResult(); await client.StopAsync(); await host.StopAsync(); }
    }

    [Fact]
    public async Task OnePairingMergesBothLibrariesAndPropagatesLaterChanges()
    {
        var root = Path.Combine(Path.GetTempPath(), "unified-sync-" + Guid.NewGuid().ToString("N"));
        var a = new LibraryStore(Path.Combine(root, "a.json"));
        var b = new LibraryStore(Path.Combine(root, "b.json"));
        // 两端初始快照都跨多个传输分块，覆盖同时发送和确认的死锁边界。
        var original = a.SavePrompt(new() { Content = "fixture A" + new string('a', 40000) });
        b.SavePrompt(new() { Content = "fixture B" + new string('b', 40000) });
        a.SaveConnection(new() { Platform = "fixture", Endpoint = "https://example.com/v1", Model = "fixture-model" }, newKey: "fixture-not-a-real-key");
        await using var host = new LanNode("A", () => root) { LibraryEnabled = true };
        await using var client = new LanNode("B", () => root) { LibraryEnabled = true };
        await using var left = new LanLibrarySync(host, () => a, TimeSpan.FromMilliseconds(100));
        await using var right = new LanLibrarySync(client, () => b, TimeSpan.FromMilliseconds(100));
        host.ConfirmPairAsync = (_, _) => Task.FromResult(true);
        try
        {
            await host.ListenAsync(IPAddress.Loopback, 0);
            await client.ConnectAsync(IPAddress.Loopback, host.Port, host.PairCode);
            await Until(() => a.Read().Prompts.Count == 2 && b.Read().Prompts.Count == 2 && b.Read().Connections.Count == 1);
            original.Content = "updated fixture A"; a.SavePrompt(original);
            await Until(() => b.Read().Prompts.Any(p => p.Content == original.Content));
            b.Update(s => s.Prompts.RemoveAll(p => p.Id == original.Id));
            await Until(() => a.Read().Prompts.All(p => p.Id != original.Id));
            Assert.Equal("fixture-model", Assert.Single(b.Read().Connections).Model);
        }
        finally { await client.StopAsync(); await host.StopAsync(); Directory.Delete(root, true); }
    }

    [Fact]
    public async Task DisabledReceiverDoesNotWriteLibraryAndCanResumeOnSamePairing()
    {
        var root = Path.Combine(Path.GetTempPath(), "unified-paused-" + Guid.NewGuid().ToString("N"));
        var a = new LibraryStore(Path.Combine(root, "a.json"));
        var b = new LibraryStore(Path.Combine(root, "b.json"));
        a.SavePrompt(new() { Content = "fixture" });
        await using var host = new LanNode("A", () => root);
        await using var client = new LanNode("B", () => root) { LibraryEnabled = true };
        await using var receiver = new LanLibrarySync(host, () => b);
        host.ConfirmPairAsync = (_, _) => Task.FromResult(true);
        try
        {
            await host.ListenAsync(IPAddress.Loopback, 0);
            await client.ConnectAsync(IPAddress.Loopback, host.Port, host.PairCode);
            var payload = JsonSerializer.SerializeToUtf8Bytes(a.ExportSync());
            Assert.False(await client.SendLibraryAsync(payload)); Assert.Empty(b.Read().Prompts);
            host.LibraryEnabled = true;
            Assert.True(await client.SendLibraryAsync(payload)); Assert.Single(b.Read().Prompts);
        }
        finally { await client.StopAsync(); await host.StopAsync(); Directory.Delete(root, true); }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void FirstRunChoiceIsRememberedWithoutStoringUserContent(bool enabled)
    {
        var root = Path.Combine(Path.GetTempPath(), "sync-choice-" + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(root, "sync-onboarding.json");
        try
        {
            Assert.True(SyncSetupPrompt.NeedsPrompt(path));
            SyncSetupPrompt.SaveChoice(path, enabled);
            Assert.False(SyncSetupPrompt.NeedsPrompt(path));
            using var json = JsonDocument.Parse(File.ReadAllText(path));
            Assert.Equal(enabled, json.RootElement.GetProperty("EnableSync").GetBoolean());
            Assert.Equal(2, json.RootElement.EnumerateObject().Count());
        }
        finally { Directory.Delete(root, true); }
    }

    private static async Task Until(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!condition()) await Task.Delay(50, timeout.Token);
    }
}
