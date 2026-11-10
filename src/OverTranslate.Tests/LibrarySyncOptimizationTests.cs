using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using OverTranslate.Services.Lan;
using OverTranslate.Services.Library;
using Xunit;

namespace OverTranslate.Tests;

public sealed class LibrarySyncOptimizationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "library-delta-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void Cache_rejects_a_corrupt_external_file_and_recovers_after_it_is_restored()
    {
        var path = Path.Combine(_root, "library.json");
        var store = new LibraryStore(path);
        store.SavePrompt(new() { Content = "preserved fixture" });
        store.ExportSyncChanges(null, out var known);
        var valid = File.ReadAllBytes(path);
        File.WriteAllText(path, "{invalid fixture");
        Assert.Throws<JsonException>(() => store.ExportSyncChanges(known, out _));
        File.WriteAllBytes(path, valid);
        Assert.Empty(store.ExportSyncChanges(known, out _).Items);
        Assert.Equal("preserved fixture", Assert.Single(store.Read().Prompts).Content);
    }

    [Fact]
    public void Idle_export_does_not_read_content_and_external_edits_and_local_preferences_are_handled()
    {
        var path = Path.Combine(_root, "library.json");
        var store = new LibraryStore(path);
        var prompt = store.SavePrompt(new() { Content = "fixture" });
        store.ExportSyncChanges(null, out var known);
        Assert.Empty(store.ExportSyncChanges(known, out known).Items);
        using (new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            Assert.Empty(store.ExportSyncChanges(known, out _).Items);
        store.RememberCopiedPrompt(prompt.Id);
        Assert.Empty(store.ExportSyncChanges(known, out _).Items);
        var external = new LibraryStore(path);
        prompt.Content = "external fixture edit"; external.SavePrompt(prompt);
        Assert.Equal("external fixture edit", Assert.Single(store.ExportSyncChanges(known, out _).Items).Prompt!.Content);
    }

    [Fact]
    public void Delta_exchange_converges_after_concurrent_edits_and_offline_deletion()
    {
        var stores = Enumerable.Range(0, 3).Select(i => new LibraryStore(Path.Combine(_root, i + ".json"))).ToArray();
        var prompt = stores[0].SavePrompt(new() { Content = "initial fixture" });
        var observed = new Dictionary<(int, int), IReadOnlyDictionary<string, string>>();
        void Exchange()
        {
            for (var round = 0; round < 4; round++)
                for (var from = 0; from < stores.Length; from++)
                    for (var to = 0; to < stores.Length; to++)
                    {
                        if (from == to) continue;
                        var delta = stores[from].ExportSyncChanges(observed.GetValueOrDefault((from, to)), out var versions);
                        if (delta.Items.Count > 0) stores[to].MergeSync(delta);
                        observed[(from, to)] = versions;
                    }
        }
        Exchange();
        for (var i = 0; i < stores.Length; i++)
        {
            var edited = stores[i].Read().Prompts.Single(); edited.Content = "concurrent fixture " + i;
            stores[i].SavePrompt(edited);
        }
        Exchange();
        Assert.All(stores, store => Assert.Equal(3, store.Read().Prompts.Count));
        var stale = stores[2].ExportSync();
        stores[0].Update(s => s.Prompts.RemoveAll(p => p.Id == prompt.Id));
        Exchange(); stores[1].MergeSync(stale); Exchange();
        var expected = JsonSerializer.Serialize(stores[0].ExportSync());
        Assert.All(stores, store =>
        {
            Assert.Equal(expected, JsonSerializer.Serialize(store.ExportSync()));
            Assert.DoesNotContain(store.Read().Prompts, p => p.Id == prompt.Id);
        });
    }

    [Fact]
    public async Task Rejected_snapshot_is_retried_before_advancing_the_peer_versions()
    {
        var store = new LibraryStore(Path.Combine(_root, "retry.json"));
        store.SavePrompt(new() { Content = "retry fixture" });
        await using var host = new LanNode("fixture-host", () => _root) { LibraryEnabled = true };
        await using var client = new LanNode("fixture-client", () => _root) { LibraryEnabled = true };
        var received = new ConcurrentQueue<LibrarySyncSnapshot>();
        host.ConfirmPairAsync = (_, _) => Task.FromResult(true);
        var attempts = 0;
        host.ApplyLibraryAsync = (bytes, _) =>
        {
            received.Enqueue(JsonSerializer.Deserialize<LibrarySyncSnapshot>(bytes)!);
            if (Interlocked.Increment(ref attempts) == 1) throw new IOException("fixture save failure");
            return Task.CompletedTask;
        };
        await using var sync = new LanLibrarySync(client, () => store, TimeSpan.FromMilliseconds(30));
        await host.ListenAsync(IPAddress.Loopback, 0);
        await client.ConnectAsync(IPAddress.Loopback, host.Port, host.PairCode);
        var first = await Next(received); var retry = await Next(received);
        Assert.Equal(JsonSerializer.Serialize(first), JsonSerializer.Serialize(retry));
        Assert.Single(retry.Items);
    }

    [Fact]
    public async Task Connected_sync_sends_only_changed_records_and_reconnect_sends_full_state()
    {
        var store = new LibraryStore(Path.Combine(_root, "library.json"));
        var first = store.SavePrompt(new() { Content = "first fixture" });
        var second = store.SavePrompt(new() { Content = "second fixture" });
        await using var host = new LanNode("fixture-host", () => _root) { LibraryEnabled = true };
        await using var client = new LanNode("fixture-client", () => _root) { LibraryEnabled = true };
        var received = new ConcurrentQueue<LibrarySyncSnapshot>();
        host.ConfirmPairAsync = (_, _) => Task.FromResult(true);
        host.ApplyLibraryAsync = (bytes, _) =>
        {
            received.Enqueue(JsonSerializer.Deserialize<LibrarySyncSnapshot>(bytes)!);
            return Task.CompletedTask;
        };
        await using var sync = new LanLibrarySync(client, () => store, TimeSpan.FromMilliseconds(30));
        await host.ListenAsync(IPAddress.Loopback, 0);
        await client.ConnectAsync(IPAddress.Loopback, host.Port, host.PairCode);
        Assert.Equal(2, (await Next(received)).Items.Count);
        first.Content = "edited fixture"; store.SavePrompt(first);
        var edited = Assert.Single((await Next(received)).Items);
        Assert.Equal("p:" + first.Id, edited.Id);
        Assert.Equal("edited fixture", edited.Prompt!.Content);
        store.Update(s => s.Prompts.RemoveAll(p => p.Id == second.Id));
        var deleted = Assert.Single((await Next(received)).Items);
        Assert.Equal("p:" + second.Id, deleted.Id); Assert.True(deleted.Version.Deleted);
        await client.StopAsync();
        await client.ConnectAsync(IPAddress.Loopback, host.Port, host.PairCode);
        Assert.Equal(2, (await Next(received)).Items.Count);
    }

    private static async Task<LibrarySyncSnapshot> Next(ConcurrentQueue<LibrarySyncSnapshot> received)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (true)
        {
            if (received.TryDequeue(out var snapshot)) return snapshot;
            await Task.Delay(15, timeout.Token);
        }
    }

    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
}
