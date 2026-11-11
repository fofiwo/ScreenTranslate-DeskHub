using System.Net;
using OverTranslate.Services.Lan;
using Xunit;

namespace OverTranslate.Tests;

public sealed class LanFileCancellationTests
{
    [Fact]
    public async Task Legacy_peer_keeps_original_payload_and_disconnects_safely_on_cancel()
    {
        var root = Path.Combine(Path.GetTempPath(), "lan-legacy-cancel-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        await using var host = new LanNode("fixture-A", () => root);
        host.ConfirmPairAsync = (_, _) => Task.FromResult(true);
        var connected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        host.Changed += (_, _) => { if (host.IsConnected) connected.TrySetResult(); };
        try
        {
            await host.ListenAsync(IPAddress.Loopback, 0);
            using var socket = new System.Net.Sockets.TcpClient();
            await socket.ConnectAsync(IPAddress.Loopback, host.Port);
            using var ssl = new System.Net.Security.SslStream(socket.GetStream(), false, (_, _, _, _) => true);
            await ssl.AuthenticateAsClientAsync("localhost");
            var challenge = await LanProtocol.ReadHeaderAsync(ssl, default);
            var proof = Convert.ToHexString(System.Security.Cryptography.HMACSHA256.HashData(host.ExportListenIdentity().Secret, System.Text.Encoding.UTF8.GetBytes(challenge.Id)));
            // 旧版握手没有 FileCancellation 字段。
            var hello = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(new { Kind = "hello", Name = "legacy-fixture", Hash = proof, FileOffers = true, Version = 3 });
            var prefix = new byte[4]; System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(prefix, hello.Length);
            await ssl.WriteAsync(prefix); await ssl.WriteAsync(hello);
            Assert.True((await LanProtocol.ReadHeaderAsync(ssl, default)).Accepted);
            using var multiplex = new LanMultiplexStream(ssl);
            await connected.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var bytes = new byte[] { 7, 8, 9 };
            await LanProtocol.WriteHeaderAsync(multiplex.Files, new("file", Guid.NewGuid().ToString("N"), "legacy.bin", bytes.Length, LanProtocol.Hash(bytes)), default);
            await multiplex.Files.WriteAsync(bytes);
            Assert.True((await LanProtocol.ReadHeaderAsync(multiplex.Files, default).WaitAsync(TimeSpan.FromSeconds(5))).Accepted);
            Assert.Equal(bytes, await File.ReadAllBytesAsync(Path.Combine(root, "legacy.bin")));
            var source = Path.Combine(root, "source.bin"); await File.WriteAllBytesAsync(source, bytes);
            var sending = host.SendFileAsync(source);
            var header = await LanProtocol.ReadHeaderAsync(multiplex.Files, default).WaitAsync(TimeSpan.FromSeconds(5));
            var received = new byte[header.Length]; await multiplex.Files.ReadExactlyAsync(received);
            Assert.Equal(bytes, received);
            await LanProtocol.WriteHeaderAsync(multiplex.Files, new("ack", header.Id, Accepted: true), default);
            await sending.WaitAsync(TimeSpan.FromSeconds(5));
            using var cancellation = new CancellationTokenSource();
            var offer = host.SendBundleAsync([source], cancellation.Token);
            Assert.Equal("file-offer", (await LanProtocol.ReadHeaderAsync(multiplex.Files, default).WaitAsync(TimeSpan.FromSeconds(5))).Kind);
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => offer.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.False(host.IsConnected);
        }
        finally { await host.StopAsync(); Directory.Delete(root, true); }
    }

    [Fact]
    public async Task Cancelling_payload_keeps_reverse_batch_clipboard_and_next_batch()
    {
        var root = Path.Combine(Path.GetTempPath(), "lan-cancel-payload-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var a = Path.Combine(root, "a"); var b = Path.Combine(root, "b");
        await using var host = new LanNode("fixture-A", () => a);
        await using var client = new LanNode("fixture-B", () => b);
        host.ConfirmPairAsync = (_, _) => Task.FromResult(true);
        host.ApplyClipboardAsync = (_, _) => Task.CompletedTask;
        var forwardOffered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var reverseOffered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var answer = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        host.ConfirmFilesAsync = async (_, token) => { forwardOffered.TrySetResult(); await answer.Task.WaitAsync(token); return a; };
        client.ConfirmFilesAsync = async (_, token) => { reverseOffered.TrySetResult(); await answer.Task.WaitAsync(token); return b; };
        try
        {
            var large = Path.Combine(root, "large.bin");
            var bytes = new byte[64 * 1024 * 1024]; new Random(321).NextBytes(bytes);
            await File.WriteAllBytesAsync(large, bytes);
            var small = Path.Combine(root, "small.txt"); await File.WriteAllTextAsync(small, "reverse fixture");
            await host.ListenAsync(IPAddress.Loopback, 0);
            await client.ConnectAsync(IPAddress.Loopback, host.Port, host.PairCode);
            var version = client.ConnectionVersion;
            var reverse = host.SendBundleAsync([large]);
            using var cancellation = new CancellationTokenSource();
            long cancelledAt = 0;
            client.Progress += (_, progress) =>
            {
                if (progress.Phase != "发送中" || progress.Completed == progress.Total || cancelledAt != 0) return;
                Assert.False(reverse.IsCompleted, "The opposite transfer must still be active when cancelling.");
                cancelledAt = progress.Completed; cancellation.Cancel();
            };
            var sending = client.SendBundleAsync([large], cancellation.Token);
            await Task.WhenAll(forwardOffered.Task, reverseOffered.Task).WaitAsync(TimeSpan.FromSeconds(15));
            answer.SetResult();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => sending.WaitAsync(TimeSpan.FromSeconds(30)));
            Assert.True(cancelledAt > 0, "Cancellation must happen before the payload finishes.");
            Assert.True(await reverse.WaitAsync(TimeSpan.FromSeconds(10)));
            Assert.True(host.IsConnected && client.IsConnected); Assert.Equal(version, client.ConnectionVersion);
            Assert.Empty(Directory.GetFileSystemEntries(a));
            Assert.Equal(System.Security.Cryptography.SHA256.HashData(bytes), System.Security.Cryptography.SHA256.HashData(await File.ReadAllBytesAsync(Assert.Single(Directory.GetFiles(b, "large.bin", SearchOption.AllDirectories)))));
            Assert.True(await client.SendClipboardAsync(new("text", [1])).WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.True(await client.SendBundleAsync([small]).WaitAsync(TimeSpan.FromSeconds(10)));
        }
        finally { await host.StopAsync(); await client.StopAsync(); Directory.Delete(root, true); }
    }

    [Theory]
    [InlineData(0)] [InlineData(-2)] [InlineData(65537)] [InlineData(3)]
    public async Task Invalid_chunk_lengths_are_rejected(int count)
    {
        var prefix = new byte[4]; System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(prefix, count);
        using var source = new MemoryStream(prefix);
        using var chunks = new LanFileChunks(source, 2);
        await Assert.ThrowsAsync<InvalidDataException>(async () => await chunks.ReadAsync(new byte[2]));
    }

    [Fact]
    public async Task Cancelling_confirmation_preserves_session_and_next_batch()
    {
        var root = Path.Combine(Path.GetTempPath(), "lan-cancel-isolation-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        await using var host = new LanNode("fixture-A", () => Path.Combine(root, "a"));
        await using var client = new LanNode("fixture-B", () => Path.Combine(root, "b"));
        host.ConfirmPairAsync = (_, _) => Task.FromResult(true);
        var offered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var reverseOffered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var reverseAnswer = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken reverseToken = default;
        client.ConfirmFilesAsync = (_, token) => { reverseToken = token; reverseOffered.TrySetResult(); return reverseAnswer.Task.WaitAsync(token); };
        host.ConfirmFilesAsync = async (_, token) => { offered.TrySetResult(); await Task.Delay(Timeout.Infinite, token); return null; };
        try
        {
            await host.ListenAsync(IPAddress.Loopback, 0);
            await client.ConnectAsync(IPAddress.Loopback, host.Port, host.PairCode);
            var version = client.ConnectionVersion;
            var source = Path.Combine(root, "fixture.txt");
            await File.WriteAllTextAsync(source, "fixture");
            var reverse = host.SendBundleAsync([source]);
            await reverseOffered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            using var cancellation = new CancellationTokenSource();
            var sending = client.SendBundleAsync([source], cancellation.Token);
            await offered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => sending.WaitAsync(TimeSpan.FromSeconds(10)));
            Assert.True(host.IsConnected && client.IsConnected, "Cancelling a pending offer must preserve both directions of the paired session.");
            Assert.Equal(version, client.ConnectionVersion);
            Assert.False(Directory.Exists(Path.Combine(root, "a")));
            Assert.False(reverse.IsCompleted); Assert.False(reverseToken.IsCancellationRequested);
            reverseAnswer.SetResult(Path.Combine(root, "b"));
            Assert.True(await reverse.WaitAsync(TimeSpan.FromSeconds(10)));
            host.ConfirmFilesAsync = (_, _) => Task.FromResult<string?>(Path.Combine(root, "a"));
            Assert.True(await client.SendBundleAsync([source]).WaitAsync(TimeSpan.FromSeconds(10)));
            Assert.Single(Directory.GetFiles(Path.Combine(root, "a"), "fixture.txt", SearchOption.AllDirectories));
        }
        finally { await host.StopAsync(); await client.StopAsync(); Directory.Delete(root, true); }
    }
}
