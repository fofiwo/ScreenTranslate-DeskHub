using System.Net;
using System.Text;
using OverTranslate.Services.Lan;
using OverTranslate.Services.Library;
using Xunit;

namespace OverTranslate.Tests;

public sealed class LanReconnectTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "lan-reconnect-" + Guid.NewGuid().ToString("N"));
    public LanReconnectTests() => Directory.CreateDirectory(_root);
    public void Dispose() => Directory.Delete(_root, true);

    [Fact]
    public async Task Trusted_identity_and_encrypted_profiles_survive_both_node_restarts()
    {
        var path = Path.Combine(_root, "trusted.dat");
        var profiles = new LanTrustedDevices(path);
        var id = Guid.NewGuid().ToString("N");
        await using (var host = new LanNode("fixture-A", () => _root))
        await using (var client = new LanNode("fixture-B", () => _root))
        {
            host.ConfirmPairAsync = (_, _) => Task.FromResult(true);
            await host.ListenAsync(IPAddress.Loopback, 0);
            await client.ConnectAsync(IPAddress.Loopback, host.Port, host.PairCode);
            profiles.Save([new(id, "fixture-B", true, "127.0.0.1", host.Port, host.PairCode, host.ExportListenIdentity(), true, false, true)]);
            var encrypted = Encoding.UTF8.GetString(await File.ReadAllBytesAsync(path));
            Assert.DoesNotContain("fixture-B", encrypted);
            Assert.DoesNotContain(host.PairCode, encrypted);
        }
        var restored = new LanTrustedDevices(path).Load().Single();
        await using var restartedHost = new LanNode("fixture-A", () => _root);
        await using var restartedClient = new LanNode("fixture-B", () => _root);
        restartedHost.ConfirmPairAsync = (peer, _) => Task.FromResult(peer.Name == restored.Name);
        restartedHost.ApplyClipboardAsync = (_, _) => Task.CompletedTask;
        await restartedHost.ListenAsync(IPAddress.Loopback, restored.Port, restored.Identity);
        await restartedClient.ConnectAsync(IPAddress.Loopback, restored.Port, restored.PairCode);
        Assert.True(await restartedClient.SendClipboardAsync(new("text", [1])));
        Assert.NotEmpty(restartedHost.ExportListenIdentity().Certificate);
    }

    [Fact]
    public void Corrupt_trust_file_is_preserved_and_cannot_be_silently_replaced()
    {
        var path = Path.Combine(_root, "trusted.dat");
        var bytes = new byte[] { 1, 2, 3 };
        File.WriteAllBytes(path, bytes);
        var profiles = new LanTrustedDevices(path);
        Assert.Empty(profiles.Load());
        Assert.Contains("自动重连已暂停", profiles.Error);
        Assert.Throws<InvalidDataException>(() => profiles.Save([]));
        Assert.Equal(bytes, File.ReadAllBytes(path));
    }

    [Fact]
    public async Task Reconnect_backs_off_recovers_and_cancels_pending_work()
    {
        var attempts = 0; var connected = false;
        var delays = new List<double>();
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var retry = new LanReconnectLoop(() => connected, _ =>
        {
            if (++attempts < 3) throw new IOException("synthetic failure");
            connected = true; return Task.CompletedTask;
        }, _ => { }, (duration, token) =>
        {
            delays.Add(duration.TotalSeconds);
            if (connected) { completed.TrySetResult(); return Task.Delay(Timeout.InfiniteTimeSpan, token); }
            return Task.CompletedTask;
        });
        retry.Start();
        await completed.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(3, attempts);
        Assert.Equal(new double[] { 2, 4, 2 }, delays);
        Assert.Equal(30, LanReconnectLoop.Backoff(100).TotalSeconds);
    }

    [Fact]
    public async Task Clipboard_relay_reaches_third_computer_once_and_honors_paused_link()
    {
        // A 是中间电脑，分别连 B、C；两个节点共享同一台电脑的中继。
        await using var ab = new LanNode("fixture-A", () => _root);
        await using var b = new LanNode("fixture-B", () => _root);
        await using var ac = new LanNode("fixture-A", () => _root);
        await using var c = new LanNode("fixture-C", () => _root);
        using var relay = new LanClipboardRelay();
        relay.Add(ab); relay.Add(ac);
        ab.ConfirmPairAsync = ac.ConfirmPairAsync = (_, _) => Task.FromResult(true);
        var atA = 0; var atC = 0;
        var arrived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        ab.ApplyClipboardAsync = (_, _) => { Interlocked.Increment(ref atA); return Task.CompletedTask; };
        ac.ApplyClipboardAsync = (_, _) => { Interlocked.Increment(ref atA); return Task.CompletedTask; };
        c.ApplyClipboardAsync = (_, _) => { Interlocked.Increment(ref atC); arrived.TrySetResult(); return Task.CompletedTask; };
        await ab.ListenAsync(IPAddress.Loopback, 0); await ac.ListenAsync(IPAddress.Loopback, 0);
        await b.ConnectAsync(IPAddress.Loopback, ab.Port, ab.PairCode);
        await c.ConnectAsync(IPAddress.Loopback, ac.Port, ac.PairCode);
        var message = Guid.NewGuid().ToString("N");
        Assert.True(await b.SendClipboardAsync(new("text", [1]), message));
        await arrived.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(await b.SendClipboardAsync(new("text", [1]), message));
        Assert.Equal(1, atA); Assert.Equal(1, atC);
        ac.ClipboardEnabled = false;
        await b.SendClipboardAsync(new("text", [2]));
        Assert.Equal(2, atA); Assert.Equal(1, atC);
    }

    [Fact]
    public async Task Triangle_connections_apply_one_copy_once_and_never_echo_to_origin()
    {
        var aJournal = new SyncActivityStore(Path.Combine(_root, "a-journal.json"));
        var cJournal = new SyncActivityStore(Path.Combine(_root, "c-journal.json"));
        await using var ab = new LanNode("A", () => _root) { Activities = aJournal, ActivityDeviceId = "ab" };
        await using var ac = new LanNode("A", () => _root) { Activities = aJournal, ActivityDeviceId = "ac" };
        await using var ba = new LanNode("B", () => _root);
        await using var bc = new LanNode("B", () => _root);
        await using var ca = new LanNode("C", () => _root) { Activities = cJournal, ActivityDeviceId = "ca" };
        await using var cb = new LanNode("C", () => _root) { Activities = cJournal, ActivityDeviceId = "cb" };
        using var aRelay = new LanClipboardRelay(); using var bRelay = new LanClipboardRelay(); using var cRelay = new LanClipboardRelay();
        aRelay.Add(ab); aRelay.Add(ac); bRelay.Add(ba); bRelay.Add(bc); cRelay.Add(ca); cRelay.Add(cb);
        ab.ConfirmPairAsync = ac.ConfirmPairAsync = bc.ConfirmPairAsync = (_, _) => Task.FromResult(true);
        var atA = 0; var atB = 0; var atC = 0;
        ab.ApplyClipboardAsync = ac.ApplyClipboardAsync = (_, _) => { Interlocked.Increment(ref atA); return Task.CompletedTask; };
        ba.ApplyClipboardAsync = bc.ApplyClipboardAsync = (_, _) => { Interlocked.Increment(ref atB); return Task.CompletedTask; };
        ca.ApplyClipboardAsync = cb.ApplyClipboardAsync = (_, _) => { Interlocked.Increment(ref atC); return Task.CompletedTask; };
        await ab.ListenAsync(IPAddress.Loopback, 0); await ac.ListenAsync(IPAddress.Loopback, 0); await bc.ListenAsync(IPAddress.Loopback, 0);
        await ba.ConnectAsync(IPAddress.Loopback, ab.Port, ab.PairCode);
        await ca.ConnectAsync(IPAddress.Loopback, ac.Port, ac.PairCode);
        await cb.ConnectAsync(IPAddress.Loopback, bc.Port, bc.PairCode);
        var payload = new LanClipboardPayload("text", [1], Sequence: 42);
        await Task.WhenAll(ba.SendClipboardAsync(payload), bc.SendClipboardAsync(payload));
        await Until(() => aJournal.Snapshot().Any(x => !x.Incoming && x.DeviceId == "ac" && x.State == SyncState.Completed)
            && cJournal.Snapshot().Any(x => !x.Incoming && x.DeviceId == "ca" && x.State == SyncState.Completed));
        Assert.Equal(1, atA); Assert.Equal(1, atC); Assert.Equal(0, atB);
    }

    [Fact]
    public async Task Three_libraries_converge_via_hub_and_paused_device_keeps_its_data()
    {
        var a = new LibraryStore(Path.Combine(_root, "a.json"));
        var b = new LibraryStore(Path.Combine(_root, "b.json"));
        var c = new LibraryStore(Path.Combine(_root, "c.json"));
        a.SavePrompt(new() { Content = "fixture A" }); b.SavePrompt(new() { Content = "fixture B" }); c.SavePrompt(new() { Content = "fixture C" });
        await using var ab = new LanNode("A", () => _root) { LibraryEnabled = true };
        await using var ac = new LanNode("A", () => _root) { LibraryEnabled = false };
        await using var ba = new LanNode("B", () => _root) { LibraryEnabled = true };
        await using var ca = new LanNode("C", () => _root) { LibraryEnabled = true };
        ab.ConfirmPairAsync = ac.ConfirmPairAsync = (_, _) => Task.FromResult(true);
        await using var syncAb = new LanLibrarySync(ab, () => a, TimeSpan.FromMilliseconds(40));
        await using var syncAc = new LanLibrarySync(ac, () => new LibraryStore(Path.Combine(_root, "a.json")), TimeSpan.FromMilliseconds(40));
        await using var syncBa = new LanLibrarySync(ba, () => b, TimeSpan.FromMilliseconds(40));
        await using var syncCa = new LanLibrarySync(ca, () => c, TimeSpan.FromMilliseconds(40));
        await ab.ListenAsync(IPAddress.Loopback, 0); await ac.ListenAsync(IPAddress.Loopback, 0);
        await ba.ConnectAsync(IPAddress.Loopback, ab.Port, ab.PairCode); await ca.ConnectAsync(IPAddress.Loopback, ac.Port, ac.PairCode);
        await Until(() => a.Read().Prompts.Count == 2 && b.Read().Prompts.Count == 2);
        Assert.Single(c.Read().Prompts);
        ac.LibraryEnabled = true;
        await Until(() => a.Read().Prompts.Count == 3 && b.Read().Prompts.Count == 3 && c.Read().Prompts.Count == 3);
        Assert.All(new[] { a, b, c }, store => Assert.Equal(3, store.Read().Prompts.Select(p => p.Content).Distinct().Count()));
    }
    private static async Task Until(Func<bool> ready)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!ready()) await Task.Delay(20, deadline.Token);
    }
}
