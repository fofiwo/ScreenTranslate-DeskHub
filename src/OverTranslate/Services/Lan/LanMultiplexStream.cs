using System.Buffers.Binary;
using System.IO;
using System.Threading.Channels;

namespace OverTranslate.Services.Lan;

// 在已认证的 TLS 连接内分离三条流。每通道仅允许一个未消费分块，慢文件不会堵住剪贴板读循环。
internal sealed class LanMultiplexStream : IDisposable
{
    internal const int MaxChunk = 16 * 1024;
    private readonly Stream _transport;
    private readonly CancellationTokenSource _stop = new();
    private readonly SemaphoreSlim _write = new(1, 1);
    private readonly Lane[] _lanes;
    public Stream Clipboard => _lanes[0];
    public Stream Files => _lanes[1];
    public Stream Library => _lanes[2];
    public Task Completion { get; }

    public LanMultiplexStream(Stream transport)
    {
        _transport = transport;
        _lanes = [new(this, 0), new(this, 1), new(this, 2)];
        Completion = ReadFramesAsync();
    }

    private async Task ReadFramesAsync()
    {
        Exception? failure = null;
        try
        {
            var header = new byte[5];
            while (true)
            {
                await _transport.ReadExactlyAsync(header, _stop.Token).ConfigureAwait(false);
                var channel = header[0];
                var length = BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(1));
                if (channel > 2 || length < 0 || length > MaxChunk) throw new InvalidDataException("同步分块无效。");
                var lane = _lanes[channel];
                if (length == 0)
                {
                    if (!lane.AcceptReceipt()) throw new InvalidDataException("同步分块确认无效。");
                    lane.Receipt.Release();
                    continue;
                }
                if (!lane.AcceptIncoming()) throw new InvalidDataException("同步分块超出接收额度。");
                var bytes = new byte[length];
                await _transport.ReadExactlyAsync(bytes, _stop.Token).ConfigureAwait(false);
                if (!lane.Queue.Writer.TryWrite(bytes)) throw new InvalidDataException("同步分块队列不可用。");
            }
        }
        catch (Exception ex) { failure = ex; }
        finally
        {
            _stop.Cancel();
            foreach (var lane in _lanes) lane.Queue.Writer.TryComplete(failure);
        }
    }

    private async Task FrameAsync(byte channel, ReadOnlyMemory<byte> bytes, CancellationToken token)
    {
        await _write.WaitAsync(token).ConfigureAwait(false);
        try
        {
            var header = new byte[5]; header[0] = channel;
            BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(1), bytes.Length);
            await _transport.WriteAsync(header, token).ConfigureAwait(false);
            if (!bytes.IsEmpty) await _transport.WriteAsync(bytes, token).ConfigureAwait(false);
            await _transport.FlushAsync(token).ConfigureAwait(false);
        }
        finally { _write.Release(); }
    }

    public void Dispose() => _stop.Cancel();

    private sealed class Lane(LanMultiplexStream owner, byte channel) : Stream
    {
        internal readonly Channel<byte[]> Queue = Channel.CreateBounded<byte[]>(1);
        internal readonly SemaphoreSlim Receipt = new(1, 1);
        internal int AwaitingReceipt;
        internal int Incoming;
        internal bool AcceptReceipt() => Interlocked.Exchange(ref AwaitingReceipt, 0) == 1;
        internal bool AcceptIncoming() => Interlocked.CompareExchange(ref Incoming, 1, 0) == 0;
        private byte[]? _current;
        private int _offset;

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default)
        {
            if (buffer.IsEmpty) return 0;
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, owner._stop.Token);
            _current ??= await Queue.Reader.ReadAsync(linked.Token).ConfigureAwait(false);
            var count = Math.Min(buffer.Length, _current.Length - _offset);
            _current.AsMemory(_offset, count).CopyTo(buffer); _offset += count;
            if (_offset == _current.Length)
            {
                _current = null; _offset = 0;
                Interlocked.Exchange(ref Incoming, 0);
                await owner.FrameAsync(channel, ReadOnlyMemory<byte>.Empty, linked.Token).ConfigureAwait(false);
            }
            return count;
        }

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken token = default)
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, owner._stop.Token);
            while (!buffer.IsEmpty)
            {
                var count = Math.Min(buffer.Length, MaxChunk);
                await Receipt.WaitAsync(linked.Token).ConfigureAwait(false);
                Interlocked.Exchange(ref AwaitingReceipt, 1);
                await owner.FrameAsync(channel, buffer[..count], linked.Token).ConfigureAwait(false);
                buffer = buffer[count..];
            }
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken token) => ReadAsync(buffer.AsMemory(offset, count), token).AsTask();
        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken token) => WriteAsync(buffer.AsMemory(offset, count), token).AsTask();
        public override bool CanRead => true;
        public override bool CanWrite => true;
        public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override Task FlushAsync(CancellationToken token) => Task.CompletedTask;
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
}
