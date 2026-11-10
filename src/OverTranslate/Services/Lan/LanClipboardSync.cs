using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Clipboard = System.Windows.Clipboard;
using DataObject = System.Windows.DataObject;
using DataFormats = System.Windows.DataFormats;

namespace OverTranslate.Services.Lan;

internal sealed class ClipboardLoopGuard
{
    private string? _last;
    private long _connection;
    private void UseConnection(long connection)
    {
        if (_connection == connection) return;
        _connection = connection; Reset();
    }
    public bool ShouldSend(LanClipboardPayload payload, long connection = 0)
    {
        UseConnection(connection);
        var fingerprint = payload.Kind + LanProtocol.Hash(payload.Bytes);
        if (fingerprint == _last) return false;
        _last = fingerprint;
        return true;
    }
    public void Received(LanClipboardPayload payload, long connection = 0)
    { UseConnection(connection); _last = payload.Kind + LanProtocol.Hash(payload.Bytes); }
    public void Reset() => _last = null;
}

internal sealed class LanClipboardSync : IDisposable
{
    internal const string OriginFormat = "ScreenTranslate.LanOrigin.v1";
    internal static string DescribeOrigin(object? value) => value is string { Length: > 0 and <= 80 } device
        && device != "received" && !string.IsNullOrWhiteSpace(device) && !device.Any(char.IsControl) ? "来自 " + device : "局域网";
    private readonly Dispatcher _dispatcher;
    private readonly LanNode _node;
    private readonly ClipboardLoopGuard _loop = new();
    private readonly DispatcherTimer _timer;
    private HwndSource? _source;
    private bool _sending;
    private bool _dirty;
    private bool _disposed;
    private bool _enabled = true;
    private CancellationTokenSource? _publishRequest;
    public bool Enabled
    {
        get => _enabled;
        set
        {
            _enabled = value;
            if (!value) { _dirty = false; _timer.Stop(); _publishRequest?.Cancel(); }
            else _loop.Reset();
        }
    }
    public event EventHandler<string>? Notice;

    public LanClipboardSync(Dispatcher dispatcher, LanNode node)
    {
        _dispatcher = dispatcher;
        _node = node;
        _timer = new DispatcherTimer(TimeSpan.FromMilliseconds(120), DispatcherPriority.Background,
            async (_, _) => { _timer!.Stop(); await PublishAsync(); }, dispatcher);
        _timer.Stop();
        _source = new HwndSource(new HwndSourceParameters("ScreenTranslate LAN clipboard")
        { ParentWindow = new IntPtr(-3), Width = 0, Height = 0, WindowStyle = 0 });
        _source.AddHook(WndProc);
        if (!AddClipboardFormatListener(_source.Handle))
        {
            _source.Dispose(); _source = null;
            throw new InvalidOperationException("无法监听系统剪贴板，请重新打开局域网功能。");
        }
    }

    private IntPtr WndProc(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message == 0x031D && !_disposed && Enabled && _node.IsConnected)
        { _dirty = true; _timer.Stop(); _timer.Start(); }
        return IntPtr.Zero;
    }

    private async Task PublishAsync()
    {
        if (_sending || _disposed) return;
        _sending = true;
        using var request = new CancellationTokenSource();
        _publishRequest = request;
        try
        {
            while (_dirty && !_disposed && Enabled && _node.IsConnected)
            {
                _dirty = false;
                LanClipboardPayload? payload = null;
                var sequence = GetClipboardSequenceNumber();
                for (var attempt = 0; attempt < 4; attempt++)
                {
                    try { payload = await CaptureAsync(); break; }
                    catch (ExternalException) when (attempt < 3) { await Task.Delay(60); }
                }
                if (sequence != GetClipboardSequenceNumber()) { _dirty = true; continue; }
                if (payload is null || !_loop.ShouldSend(payload, _node.ConnectionVersion)) continue;
                payload = payload with { Sequence = sequence };
                if (!Enabled || request.IsCancellationRequested) return;
                Notice?.Invoke(this, payload.Kind == "text" ? "正在同步新复制的文本…" : "正在同步新复制的图片…");
                if (await _node.SendClipboardAsync(payload, request.Token))
                    Notice?.Invoke(this, payload.Kind == "text" ? "新复制的文本已同步。" : "新复制的图片已同步。");
                else { _loop.Reset(); Notice?.Invoke(this, "对方已暂停自动同步，内容未写入对方剪贴板。"); }
            }
        }
        catch (OperationCanceledException) when (request.IsCancellationRequested) { _loop.Reset(); }
        catch (Exception ex)
        {
            _loop.Reset();
            if (!_disposed) _node.ReportPreparationFailure(SyncKind.Clipboard, ex);
            if (!_disposed) Notice?.Invoke(this, "剪贴板同步失败，请检查连接；本机复制不受影响。");
        }
        finally { _sending = false; _publishRequest = null; }
    }

    private static async Task<LanClipboardPayload?> CaptureAsync()
    {
        var data = Clipboard.GetDataObject();
        if (data is null || data.GetDataPresent(OriginFormat)
            || data.GetDataPresent("ExcludeClipboardContentFromMonitorProcessing")
            || data.GetDataPresent(DataFormats.FileDrop)) return null;
        if (data.GetDataPresent(DataFormats.Bitmap))
        {
            var image = Clipboard.GetImage();
            if (image is null || (long)image.PixelWidth * image.PixelHeight > 32 * 1024 * 1024)
                throw new InvalidDataException("图片过大。");
            var frozen = (BitmapSource)image.GetAsFrozen();
            var bytes = await Task.Run(() => ClipboardHistory.ClipboardContent.EncodeImage(frozen));
            if (bytes.Length > LanProtocol.MaxClipboard) throw new InvalidDataException("图片超过同步大小上限。");
            return new("image", bytes);
        }
        if (!data.GetDataPresent(DataFormats.UnicodeText)) return null;
        var text = data.GetData(DataFormats.UnicodeText) as string;
        if (string.IsNullOrEmpty(text)) return null;
        if (Encoding.UTF8.GetByteCount(text) > LanProtocol.MaxClipboard) throw new InvalidDataException("文本过大。");
        return new("text", Encoding.UTF8.GetBytes(text));
    }

    public async Task ApplyAsync(LanClipboardPayload payload, CancellationToken token)
    {
        if (_disposed || !Enabled) throw new InvalidOperationException("自动同步已暂停。");
        var image = payload.Kind == "image" ? await Task.Run(() => DecodeImage(payload.Bytes), token).ConfigureAwait(false) : null;
        await _dispatcher.InvokeAsync(async () =>
        {
            var data = new DataObject();
            data.SetData(OriginFormat, payload.Origin.Length == 0 ? _node.PeerName : payload.Origin);
            if (payload.Kind == "text") data.SetText(new UTF8Encoding(false, true).GetString(payload.Bytes));
            else if (payload.Kind == "image")
            {
                data.SetImage(image!);
            }
            else throw new InvalidDataException("不支持的剪贴板内容。");
            for (var attempt = 0; ; attempt++)
            {
                token.ThrowIfCancellationRequested();
                if (_disposed || !Enabled) throw new InvalidOperationException("自动同步已暂停。");
                try { Clipboard.SetDataObject(data, true); _loop.Received(payload, _node.ConnectionVersion); return; }
                catch (ExternalException) when (attempt < 5) { await Task.Delay(60, token); }
            }
        }, DispatcherPriority.Normal, token).Task.Unwrap().ConfigureAwait(false);
    }

    internal static BitmapSource DecodeImage(byte[] bytes)
    {
        if (bytes.Length > LanProtocol.MaxClipboard) throw new InvalidDataException("对方图片过大。");
        using var stream = new MemoryStream(bytes, false);
        // 协议只发送单帧 PNG。不能用通用解码器只检查首帧后加载所有 TIFF/GIF 帧。
        var metadata = new PngBitmapDecoder(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.None);
        if (metadata.Frames.Count != 1
            || (long)metadata.Frames[0].PixelWidth * metadata.Frames[0].PixelHeight > 32 * 1024 * 1024)
            throw new InvalidDataException("对方图片过大或格式无效。");
        stream.Position = 0;
        var image = new PngBitmapDecoder(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad).Frames[0];
        image.Freeze();
        return image;
    }

    public void Dispose()
    {
        _disposed = true; _dirty = false; _timer.Stop(); _publishRequest?.Cancel();
        if (_source is null) return;
        RemoveClipboardFormatListener(_source.Handle);
        _source.RemoveHook(WndProc); _source.Dispose(); _source = null;
    }

    [DllImport("user32.dll", SetLastError = true)] private static extern bool AddClipboardFormatListener(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool RemoveClipboardFormatListener(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern uint GetClipboardSequenceNumber();
}
