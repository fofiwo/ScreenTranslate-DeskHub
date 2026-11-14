using System.Net;
using System.Runtime.InteropServices;
using OverTranslate.Services.Lan;
using Xunit;

namespace OverTranslate.Tests;

public sealed class LanClipboardRecoveryTests
{
    [Fact]
    public async Task Failed_apply_keeps_connection_and_same_message_can_retry_without_false_success()
    {
        await using var host = new LanNode("fixture-host", () => "");
        await using var client = new LanNode("fixture-client", () => "");
        using var relay = new LanClipboardRelay(); relay.Add(host);
        host.ConfirmPairAsync = (_, _) => Task.FromResult(true);
        var calls = 0;
        host.ApplyClipboardAsync = (_, _) => ++calls == 1
            ? Task.FromException(new ExternalException("fixture clipboard busy")) : Task.CompletedTask;
        await host.ListenAsync(IPAddress.Loopback, 0);
        await client.ConnectAsync(IPAddress.Loopback, host.Port, host.PairCode);
        var id = Guid.NewGuid().ToString("N");
        Assert.False(await client.SendClipboardAsync(new("image", [1]), id).WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.True(host.IsConnected); Assert.True(client.IsConnected);
        Assert.True(await client.SendClipboardAsync(new("image", [1]), id));
        Assert.Equal(2, calls);
        Assert.True(await client.SendClipboardAsync(new("image", [1]), id));
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task Concurrent_routes_wait_for_successful_apply_before_acknowledging_duplicate()
    {
        await using var ab = new LanNode("fixture-hub", () => "");
        await using var ac = new LanNode("fixture-hub", () => "");
        await using var b = new LanNode("fixture-B", () => "");
        await using var c = new LanNode("fixture-C", () => "");
        using var relay = new LanClipboardRelay(); relay.Add(ab); relay.Add(ac);
        ab.ConfirmPairAsync = ac.ConfirmPairAsync = (_, _) => Task.FromResult(true);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        ab.ApplyClipboardAsync = ac.ApplyClipboardAsync = async (_, token) =>
        { Interlocked.Increment(ref calls); entered.TrySetResult(); await release.Task.WaitAsync(token); };
        await ab.ListenAsync(IPAddress.Loopback, 0); await ac.ListenAsync(IPAddress.Loopback, 0);
        await b.ConnectAsync(IPAddress.Loopback, ab.Port, ab.PairCode);
        await c.ConnectAsync(IPAddress.Loopback, ac.Port, ac.PairCode);
        var id = Guid.NewGuid().ToString("N");
        var first = b.SendClipboardAsync(new("image", [1]), id);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var second = c.SendClipboardAsync(new("image", [1]), id);
        try
        {
            Assert.NotSame(second, await Task.WhenAny(second, Task.Delay(200)));
            release.TrySetResult();
            Assert.All(await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(5)), Assert.True);
            Assert.Equal(1, calls);
        }
        finally { release.TrySetResult(); }
    }

    [Fact]
    public void Recopying_same_image_is_a_new_event_but_retrying_one_event_is_not()
    {
        var guard = new ClipboardLoopGuard();
        var first = new LanClipboardPayload("image", [1], Sequence: 10);
        Assert.True(guard.ShouldSend(first, 1));
        Assert.False(guard.ShouldSend(first, 1));
        Assert.True(guard.ShouldSend(first with { Sequence = 11 }, 1));
    }

    [Fact]
    public async Task Stale_copy_waiting_behind_an_active_send_is_cancelled_without_disconnecting()
    {
        await using var host = new LanNode("fixture-host", () => "");
        await using var client = new LanNode("fixture-client", () => "");
        host.ConfirmPairAsync = (_, _) => Task.FromResult(true);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var received = new List<byte>();
        host.ApplyClipboardAsync = async (p, token) =>
        { received.Add(p.Bytes[0]); if (p.Bytes[0] == 1) { entered.TrySetResult(); await release.Task.WaitAsync(token); } };
        await host.ListenAsync(IPAddress.Loopback, 0);
        await client.ConnectAsync(IPAddress.Loopback, host.Port, host.PairCode);
        var first = client.SendClipboardAsync(new("image", [1]));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var current = true;
        var queued = client.SendClipboardAsync(new("image", [2]), canSend: () => current);
        current = false; release.TrySetResult();
        Assert.True(await first);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => queued);
        Assert.True(await client.SendClipboardAsync(new("image", [3])));
        Assert.Equal(new byte[] { 1, 3 }, received);
    }

    [Fact]
    public async Task Disconnect_cancels_in_progress_apply_so_original_listener_can_accept_reconnection()
    {
        await using var host = new LanNode("fixture-host", () => "");
        await using var client = new LanNode("fixture-client", () => "");
        host.ConfirmPairAsync = (_, _) => Task.FromResult(true);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        host.ApplyClipboardAsync = async (_, token) =>
        { entered.TrySetResult(); await Task.Delay(Timeout.InfiniteTimeSpan, token); };
        await host.ListenAsync(IPAddress.Loopback, 0);
        await client.ConnectAsync(IPAddress.Loopback, host.Port, host.PairCode);
        var first = client.SendClipboardAsync(new("image", [1]));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await client.StopAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
        host.ApplyClipboardAsync = (_, _) => Task.CompletedTask;
        await client.ConnectAsync(IPAddress.Loopback, host.Port, host.PairCode).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(await client.SendClipboardAsync(new("image", [2])));
    }

    [Fact]
    public async Task Clipboard_confirmation_stalls_time_out_even_while_library_traffic_is_active()
    {
        await using var host = new LanNode("fixture-host", () => "") { LibraryEnabled = true };
        await using var client = new LanNode("fixture-client", () => "") { LibraryEnabled = true };
        host.ConfirmPairAsync = (_, _) => Task.FromResult(true);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        host.ApplyClipboardAsync = async (_, token) =>
        { entered.TrySetResult(); await Task.Delay(Timeout.InfiniteTimeSpan, token); };
        client.ApplyLibraryAsync = (_, _) => Task.CompletedTask;
        await host.ListenAsync(IPAddress.Loopback, 0);
        await client.ConnectAsync(IPAddress.Loopback, host.Port, host.PairCode);
        var sending = client.SendClipboardAsync(new("image", [1]));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var exchanges = 0;
        async Task Traffic()
        {
            while (host.IsConnected && !sending.IsCompleted)
            {
                try { await host.SendLibraryAsync([1]); exchanges++; await Task.Delay(100); }
                catch (Exception) when (!host.IsConnected || sending.IsCompleted) { break; }
            }
        }
        var traffic = Traffic();
        try
        {
            await Assert.ThrowsAsync<TimeoutException>(() => sending.WaitAsync(TimeSpan.FromSeconds(8)));
            Assert.False(client.IsConnected);
            Assert.True(exchanges >= 2);
        }
        finally { await client.StopAsync(); await host.StopAsync(); await traffic; }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Clipboard_payload_times_out_only_when_its_own_read_progress_stalls(bool progressing)
    {
        await using var host = new LanNode("fixture-host", () => "");
        host.ConfirmPairAsync = (_, _) => Task.FromResult(true);
        var connected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        host.Changed += (_, _) => { if (host.IsConnected) connected.TrySetResult(); };
        await host.ListenAsync(IPAddress.Loopback, 0);
        using var socket = new System.Net.Sockets.TcpClient();
        await socket.ConnectAsync(IPAddress.Loopback, host.Port);
        using var ssl = new System.Net.Security.SslStream(socket.GetStream(), false, (_, _, _, _) => true);
        await ssl.AuthenticateAsClientAsync("localhost");
        var challenge = await LanProtocol.ReadHeaderAsync(ssl, default);
        var proof = Convert.ToHexString(System.Security.Cryptography.HMACSHA256.HashData(
            host.ExportListenIdentity().Secret, System.Text.Encoding.UTF8.GetBytes(challenge.Id)));
        await LanProtocol.WriteHeaderAsync(ssl, new("hello", Name: "fixture-stalled", Hash: proof), default);
        Assert.True((await LanProtocol.ReadHeaderAsync(ssl, default)).Accepted);
        using var multiplex = new LanMultiplexStream(ssl);
        await connected.Task.WaitAsync(TimeSpan.FromSeconds(5));
        // 不消费剪贴板通道，消息头占用一个额度后，正文等待对方读取。
        var bytes = new byte[128 * 1024];
        var sending = host.SendClipboardAsync(new("image", bytes));
        if (progressing)
        {
            var header = await LanProtocol.ReadHeaderAsync(multiplex.Clipboard, default);
            var received = new byte[bytes.Length];
            for (var offset = 0; offset < received.Length; offset += LanMultiplexStream.MaxChunk)
            {
                await Task.Delay(500);
                await multiplex.Clipboard.ReadExactlyAsync(received.AsMemory(offset, LanMultiplexStream.MaxChunk));
            }
            await LanProtocol.WriteHeaderAsync(multiplex.Clipboard, new("ack", header.Id, Accepted: true), default);
            Assert.True(await sending); Assert.True(host.IsConnected); Assert.Equal(bytes, received);
        }
        else
        {
            await Assert.ThrowsAsync<TimeoutException>(() => sending.WaitAsync(TimeSpan.FromSeconds(6)));
            Assert.False(host.IsConnected);
        }
    }

    [Fact]
    public void Socket_uses_native_keepalive_with_short_idle_and_retry_intervals()
    {
        using var socket = new System.Net.Sockets.TcpClient();
        LanNode.ConfigureSocket(socket);
        Assert.True(socket.NoDelay);
        Assert.Equal(1, (int)socket.Client.GetSocketOption(System.Net.Sockets.SocketOptionLevel.Socket, System.Net.Sockets.SocketOptionName.KeepAlive)!);
        foreach (var (option, expected) in new[]
        {
            (System.Net.Sockets.SocketOptionName.TcpKeepAliveTime, 5),
            (System.Net.Sockets.SocketOptionName.TcpKeepAliveInterval, 1),
            (System.Net.Sockets.SocketOptionName.TcpKeepAliveRetryCount, 3)
        }) Assert.Equal(expected, (int)socket.Client.GetSocketOption(System.Net.Sockets.SocketOptionLevel.Tcp, option)!);
    }

    [Fact]
    public async Task Automatic_reconnect_attempt_is_cancelled_and_next_attempt_can_recover()
    {
        var connected = false; var attempts = 0; CancellationToken firstToken = default;
        var recovered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var notices = new List<string>();
        await using var reconnect = new LanReconnectLoop(() => connected, async token =>
        {
            if (++attempts == 1) { firstToken = token; await Task.Delay(Timeout.InfiniteTimeSpan, token); }
            else { connected = true; recovered.TrySetResult(); }
        }, notices.Add, (_, token) => Task.Delay(1, token));
        reconnect.Start(); await recovered.Task.WaitAsync(TimeSpan.FromSeconds(8));
        Assert.True(firstToken.IsCancellationRequested); Assert.Equal(2, attempts);
        Assert.Contains(notices, x => x.Contains("连接超时"));
    }
}
