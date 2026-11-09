using System.Buffers.Binary;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;

namespace OverTranslate.Services.Lan;

internal sealed record LanClipboardPayload(string Kind, byte[] Bytes);
internal sealed record LanPairRequest(string Name, string Address);
internal sealed record LanProgress(string Name, long Completed, long Total, string Phase);
internal sealed record LanHeader(string Kind, string Id = "", string Name = "", long Length = 0,
    string Hash = "", bool Accepted = false, int Version = 3);

internal static class LanProtocol
{
    internal const int MaxHeader = 4096;
    internal const int MaxClipboard = 32 * 1024 * 1024;
    internal const long MaxFile = 64L * 1024 * 1024 * 1024;

    public static async Task WriteHeaderAsync(Stream stream, LanHeader header, CancellationToken token)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(header);
        if (bytes.Length > MaxHeader) throw new InvalidDataException("消息头过大。");
        var frame = new byte[4 + bytes.Length];
        BinaryPrimitives.WriteInt32BigEndian(frame, bytes.Length);
        bytes.CopyTo(frame, 4);
        await stream.WriteAsync(frame, token).ConfigureAwait(false);
    }

    public static async Task<LanHeader> ReadHeaderAsync(Stream stream, CancellationToken token)
    {
        var prefix = new byte[4];
        await stream.ReadExactlyAsync(prefix, token).ConfigureAwait(false);
        var length = BinaryPrimitives.ReadInt32BigEndian(prefix);
        if (length is <= 0 or > MaxHeader) throw new InvalidDataException("消息头长度无效。");
        var bytes = new byte[length];
        await stream.ReadExactlyAsync(bytes, token).ConfigureAwait(false);
        var header = JsonSerializer.Deserialize<LanHeader>(bytes) ?? throw new InvalidDataException("消息头无效。");
        if (header.Version != 3) throw new InvalidDataException("对方的传输版本不兼容，请将两台电脑更新到相同新版。");
        return header;
    }

    public static void ValidatePayload(LanHeader header)
    {
        if (!Guid.TryParseExact(header.Id, "N", out _) || header.Hash.Length != 64
            || !header.Hash.All(Uri.IsHexDigit) || header.Length < 0)
            throw new InvalidDataException("传输信息无效。");
        if (header.Kind == "file")
        {
            if (header.Length > MaxFile) throw new InvalidDataException("文件超过 64 GB 上限。");
            LanFiles.ValidateName(header.Name);
        }
        else if (header.Kind == "library")
        {
            if (header.Length > 8 * 1024 * 1024 - 28) throw new InvalidDataException("资料库超过同步大小上限。");
        }
        else if (header.Kind is "text" or "image")
        {
            if (header.Length > MaxClipboard) throw new InvalidDataException("剪贴板内容超过 32 MB 上限。");
        }
        else throw new InvalidDataException("不支持的传输类型。");
    }

    public static string Hash(ReadOnlySpan<byte> bytes) => Convert.ToHexString(SHA256.HashData(bytes));
}
