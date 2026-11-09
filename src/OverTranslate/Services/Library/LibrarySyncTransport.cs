using System.Buffers.Binary;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace OverTranslate.Services.Library;

internal static class LibrarySyncTransport
{
    internal const int MaxFrameBytes = 8 * 1024 * 1024;
    private static readonly byte[] Protocol = Encoding.ASCII.GetBytes("ScreenTranslate-LAN-v2");

    public static string GenerateCode() => Convert.ToHexString(RandomNumberGenerator.GetBytes(32));

    public static byte[] ParseCode(string code)
    {
        try
        {
            var bytes = Convert.FromHexString(code.Trim());
            if (bytes.Length == 32) return bytes;
        }
        catch (FormatException) { }
        throw new InvalidOperationException("配对码须为由本软件生成的 64 位代码，请在另一台电脑输入相同代码。");
    }

    public static bool IsLan(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        if (IPAddress.IsLoopback(address)) return true;
        var b = address.GetAddressBytes();
        return b.Length == 4 && (b[0] == 10 || b[0] == 172 && b[1] is >= 16 and <= 31
            || b[0] == 192 && b[1] == 168 || b[0] == 169 && b[1] == 254);
    }

    internal static byte[] Seal(byte[] data, byte[] key, byte[] context)
    {
        if (data.Length > MaxFrameBytes - 28) throw new InvalidDataException("资料超过单次同步的 8 MB 上限。");
        var result = new byte[data.Length + 28];
        RandomNumberGenerator.Fill(result.AsSpan(0, 12));
        using var aes = new AesGcm(key, 16);
        aes.Encrypt(result.AsSpan(0, 12), data, result.AsSpan(28), result.AsSpan(12, 16), context);
        return result;
    }

    internal static byte[] Open(byte[] frame, byte[] key, byte[] context)
    {
        if (frame.Length is < 28 or > MaxFrameBytes) throw new InvalidDataException("同步数据长度无效。");
        var result = new byte[frame.Length - 28];
        using var aes = new AesGcm(key, 16);
        aes.Decrypt(frame.AsSpan(0, 12), frame.AsSpan(28), frame.AsSpan(12, 16), result, context);
        return result;
    }

    private static byte[] Context(byte[] challenge, byte direction, byte[]? request = null) =>
        Protocol.Concat(new[] { direction }).Concat(challenge).Concat(request?.Take(12) ?? []).ToArray();

    private static async Task WriteFrame(Stream stream, byte[] frame, CancellationToken token)
    {
        if (frame.Length > MaxFrameBytes) throw new InvalidDataException("资料超过单次同步的 8 MB 上限。");
        var header = new byte[4]; BinaryPrimitives.WriteInt32BigEndian(header, frame.Length);
        await stream.WriteAsync(header, token); await stream.WriteAsync(frame, token);
    }

    private static async Task<byte[]> ReadFrame(Stream stream, CancellationToken token)
    {
        var header = new byte[4]; await stream.ReadExactlyAsync(header, token);
        var length = BinaryPrimitives.ReadInt32BigEndian(header);
        if (length is < 28 or > MaxFrameBytes) throw new InvalidDataException("同步数据长度无效。");
        var frame = new byte[length]; await stream.ReadExactlyAsync(frame, token); return frame;
    }

    private static LibrarySyncSnapshot Decode(byte[] frame, byte[] key, byte[] context)
    {
        var plain = Open(frame, key, context);
        try
        {
            var snapshot = JsonSerializer.Deserialize<LibrarySyncSnapshot>(plain)
                ?? throw new InvalidDataException("同步资料为空。");
            LibrarySyncMerge.Validate(snapshot); return snapshot;
        }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }

    private static byte[] Encode(LibrarySyncSnapshot snapshot, byte[] key, byte[] context)
    {
        var plain = JsonSerializer.SerializeToUtf8Bytes(snapshot);
        try { return Seal(plain, key, context); }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }

    public static async Task<bool> ExchangeAsync(LibraryStore store, IPEndPoint peer, byte[] key, CancellationToken token)
    {
        if (!IsLan(peer.Address)) throw new InvalidOperationException("仅支持局域网 IPv4 地址。");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(12)); token = deadline.Token;
        using var client = new TcpClient(AddressFamily.InterNetwork);
        await client.ConnectAsync(peer.Address, peer.Port, token);
        using var stream = client.GetStream();
        var challenge = new byte[32]; await stream.ReadExactlyAsync(challenge, token);
        var request = Encode(store.ExportSync(), key, Context(challenge, 1));
        await WriteFrame(stream, request, token);
        var response = await ReadFrame(stream, token);
        return store.MergeSync(Decode(response, key, Context(challenge, 2, request)));
    }

    public static async Task<bool> ServeAsync(LibraryStore store, TcpClient client, byte[] key, CancellationToken token)
    {
        if (client.Client.RemoteEndPoint is not IPEndPoint remote || !IsLan(remote.Address))
            throw new InvalidDataException("拒绝非局域网同步连接。");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(12)); token = deadline.Token;
        using var stream = client.GetStream();
        // 每个连接独立的服务器挑战、请求随机 nonce 和方向标记共同绑定响应，拒绝跨会话重放。
        var challenge = RandomNumberGenerator.GetBytes(32); await stream.WriteAsync(challenge, token);
        var request = await ReadFrame(stream, token);
        var incoming = Decode(request, key, Context(challenge, 1));
        var changed = store.MergeSync(incoming);
        var response = Encode(store.ExportSync(), key, Context(challenge, 2, request));
        await WriteFrame(stream, response, token); return changed;
    }
}
