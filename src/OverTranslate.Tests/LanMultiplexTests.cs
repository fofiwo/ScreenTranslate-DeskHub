using System.Buffers.Binary;
using OverTranslate.Services.Lan;
using Xunit;

namespace OverTranslate.Tests;

public sealed class LanMultiplexTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnconsumedFileOrLibraryChunkDoesNotBlockClipboardInEitherDirection(bool library)
    {
        using var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        using var client = new System.Net.Sockets.TcpClient();
        var connecting = client.ConnectAsync(System.Net.IPAddress.Loopback, ((System.Net.IPEndPoint)listener.LocalEndpoint).Port);
        using var server = await listener.AcceptTcpClientAsync(); await connecting;
        using var left = new LanMultiplexStream(client.GetStream());
        using var right = new LanMultiplexStream(server.GetStream());
        var sending = library ? left.Library : left.Files;
        var receiving = library ? right.Library : right.Files;
        await sending.WriteAsync(new byte[LanMultiplexStream.MaxChunk]);
        var blocked = sending.WriteAsync(new byte[LanMultiplexStream.MaxChunk]).AsTask();
        try
        {
            await left.Clipboard.WriteAsync(new byte[] { 17 });
            var received = new byte[1];
            await right.Clipboard.ReadExactlyAsync(received).AsTask().WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Equal(17, received[0]);
            await right.Clipboard.WriteAsync(new byte[] { 23 });
            await left.Clipboard.ReadExactlyAsync(received).AsTask().WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Equal(23, received[0]);
            Assert.False(blocked.IsCompleted);
            await receiving.ReadExactlyAsync(new byte[LanMultiplexStream.MaxChunk]);
            await blocked.WaitAsync(TimeSpan.FromSeconds(2));
            await receiving.ReadExactlyAsync(new byte[LanMultiplexStream.MaxChunk]);
        }
        finally
        {
            left.Dispose(); right.Dispose();
            try { await blocked; } catch (OperationCanceledException) { }
        }
    }

    [Theory]
    [InlineData(3, 1)]
    [InlineData(0, -1)]
    [InlineData(1, 16385)]
    [InlineData(0, 0)]
    public async Task InvalidChannelLengthOrUnsolicitedReceiptClosesBothLanes(byte channel, int length)
    {
        var bytes = new byte[5]; bytes[0] = channel;
        BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(1), length);
        using var source = new MemoryStream(bytes);
        using var multiplex = new LanMultiplexStream(source);
        await multiplex.Completion.WaitAsync(TimeSpan.FromSeconds(2));
        await Assert.ThrowsAnyAsync<Exception>(async () => await multiplex.Clipboard.ReadAsync(new byte[1]));
        await Assert.ThrowsAnyAsync<Exception>(async () => await multiplex.Files.ReadAsync(new byte[1]));
        await Assert.ThrowsAnyAsync<Exception>(async () => await multiplex.Library.ReadAsync(new byte[1]));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task OldProtocolIsRejectedBeforePairingOrPayload(int version)
    {
        using var stream = new MemoryStream();
        await LanProtocol.WriteHeaderAsync(stream, new("challenge", Version: version), default);
        stream.Position = 0;
        await Assert.ThrowsAsync<InvalidDataException>(() => LanProtocol.ReadHeaderAsync(stream, default));
    }
}
