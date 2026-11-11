using System.Buffers.Binary;
using System.IO;

namespace OverTranslate.Services.Lan;

internal sealed class LanFileCancelledException : IOException;

// 协商支持后，取消标记只出现在完整分块之间，不会截断消息或污染下一批文件。
internal sealed class LanFileChunks(Stream source, long length) : Stream
{
    internal const int MaxChunk = 65536;
    private long _remaining = length;
    private int _chunkRemaining;

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default)
    {
        if (buffer.IsEmpty || _remaining == 0) return 0;
        if (_chunkRemaining == 0)
        {
            var prefix = new byte[4];
            await source.ReadExactlyAsync(prefix, token).ConfigureAwait(false);
            _chunkRemaining = BinaryPrimitives.ReadInt32BigEndian(prefix);
            if (_chunkRemaining == -1) throw new LanFileCancelledException();
            if (_chunkRemaining <= 0 || _chunkRemaining > MaxChunk || _chunkRemaining > _remaining)
                throw new InvalidDataException("文件分块长度无效。");
        }
        var count = await source.ReadAsync(buffer[..Math.Min(buffer.Length, _chunkRemaining)], token).ConfigureAwait(false);
        if (count == 0) throw new EndOfStreamException();
        _remaining -= count; _chunkRemaining -= count;
        return count;
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken token) => ReadAsync(buffer.AsMemory(offset, count), token).AsTask();
    public override bool CanRead => true;
    public override bool CanWrite => false;
    public override bool CanSeek => false;
    public override long Length { get; } = length;
    public override long Position { get => Length - _remaining; set => throw new NotSupportedException(); }
    public override void Flush() { }
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
}
