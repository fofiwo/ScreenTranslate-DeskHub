using System.Collections.Specialized;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using System.Windows.Documents;
using OverTranslate.Services.Lan;
using Clipboard = System.Windows.Clipboard;
using DataFormats = System.Windows.DataFormats;
using DataObject = System.Windows.DataObject;
using IDataObject = System.Windows.IDataObject;

namespace OverTranslate.Services.ClipboardHistory;

internal sealed record CapturedClipboard(ClipboardRecord Record, byte[]? Png);

internal static class ClipboardContent
{
    internal const string HistoryOrigin = "ScreenTranslate.HistoryItem.v1";
    public static CapturedClipboard? Capture(IDataObject data, string source, DateTimeOffset? copiedAt = null)
    {
        var captured = CaptureCore(data, source, copiedAt, out var image);
        return captured is null || image is null ? captured : captured with { Png = EncodeImage(image) };
    }

    internal static async Task<CapturedClipboard?> CaptureAsync(IDataObject data, string source, DateTimeOffset? copiedAt = null)
    {
        // 只在调用线程读取 COM 剪贴板；冻结后的图像可以在后台压缩。
        var captured = CaptureCore(data, source, copiedAt, out var image);
        if (captured is null || image is null) return captured;
        var frozen = (BitmapSource)image.GetAsFrozen();
        return captured with { Png = await Task.Run(() => EncodeImage(frozen)) };
    }

    internal static byte[] EncodeImage(BitmapSource image)
    {
        using var stream = new MemoryStream();
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image)); encoder.Save(stream);
        return stream.ToArray();
    }

    private static CapturedClipboard? CaptureCore(IDataObject data, string source, DateTimeOffset? copiedAt, out BitmapSource? image)
    {
        image = null;
        if (data.GetDataPresent(HistoryOrigin) || data.GetDataPresent("ExcludeClipboardContentFromMonitorProcessing")) return null;
        if (data.GetDataPresent("CanIncludeInClipboardHistory") && IsExcluded(data.GetData("CanIncludeInClipboardHistory"))) return null;
        var text = data.GetDataPresent(DataFormats.UnicodeText) ? data.GetData(DataFormats.UnicodeText) as string ?? "" : "";
        var html = data.GetDataPresent(DataFormats.Html) ? data.GetData(DataFormats.Html) as string ?? "" : "";
        var rtf = data.GetDataPresent(DataFormats.Rtf) ? data.GetData(DataFormats.Rtf) as string ?? "" : "";
        if (text.Length == 0 && rtf.Length > 0) text = RtfText(rtf);
        var files = data.GetDataPresent(DataFormats.FileDrop) ? data.GetData(DataFormats.FileDrop) as string[] ?? [] : [];
        if (data.GetDataPresent(DataFormats.Bitmap) && data.GetData(DataFormats.Bitmap) is BitmapSource bitmap)
        {
            if ((long)bitmap.PixelWidth * bitmap.PixelHeight > 32 * 1024 * 1024) throw new InvalidDataException("图片超过 3200 万像素，未记录。其他历史不受影响。");
            image = bitmap;
        }
        if (text.Length == 0 && html.Length == 0 && rtf.Length == 0 && files.Length == 0 && image is null) return null;
        return new(new ClipboardRecord { Text = text, Html = html, Rtf = rtf, Files = files,
            Source = data.GetDataPresent(LanClipboardSync.OriginFormat) ? "局域网" : source,
            CopiedAt = copiedAt ?? DateTimeOffset.UtcNow }, null);
    }

    internal static string RtfText(string rtf)
    {
        try
        {
            var document = new FlowDocument(); var range = new TextRange(document.ContentStart, document.ContentEnd);
            using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(rtf));
            range.Load(stream, DataFormats.Rtf); return range.Text.TrimEnd('\r', '\n');
        }
        catch (Exception) { return ""; } // 无法解析时仍保留原 RTF，避免丢弃可重新复制的内容。
    }

    private static bool IsExcluded(object? value) => value switch
    {
        int n => n == 0, uint n => n == 0, bool b => !b, byte[] bytes => bytes.Length >= 4 && BitConverter.ToInt32(bytes) == 0,
        MemoryStream stream => ExcludedStream(stream), _ => false
    };

    private static bool ExcludedStream(MemoryStream stream)
    {
        var position = stream.Position;
        try
        {
            stream.Position = 0; Span<byte> bytes = stackalloc byte[4];
            return stream.Read(bytes) == 4 && BitConverter.ToInt32(bytes) == 0;
        }
        finally { stream.Position = position; }
    }

    public static DataObject BuildData(ClipboardRecord record, ClipboardHistoryStore store)
    {
        return CreateData(record, PrepareImage(record, store));
    }

    private static BitmapSource? PrepareImage(ClipboardRecord record, ClipboardHistoryStore store)
    {
        if (record.Files.Any(f => !File.Exists(f) && !Directory.Exists(f))) throw new IOException("原文件已移动或删除，无法复制这条文件记录。");
        return record.HasImage ? DecodeImage(store.ReadImage(record.Id), false) : null;
    }

    private static DataObject CreateData(ClipboardRecord record, BitmapSource? image)
    {
        var data = new DataObject();
        if (record.Files.Length > 0)
        {
            var files = new StringCollection(); files.AddRange(record.Files); data.SetFileDropList(files);
        }
        if (record.Text.Length > 0) data.SetText(record.Text, System.Windows.TextDataFormat.UnicodeText);
        if (record.Html.Length > 0) data.SetData(DataFormats.Html, record.Html);
        if (record.Rtf.Length > 0) data.SetData(DataFormats.Rtf, record.Rtf);
        if (image is not null) data.SetImage(image);
        data.SetData(HistoryOrigin, record.Id);
        return data;
    }

    public static BitmapSource DecodeImage(byte[] bytes, bool thumbnail)
    {
        using var stream = new MemoryStream(bytes);
        var metadata = new PngBitmapDecoder(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.None);
        if (metadata.Frames.Count != 1 || (long)metadata.Frames[0].PixelWidth * metadata.Frames[0].PixelHeight > 32 * 1024 * 1024)
            throw new InvalidDataException("图片附件格式或尺寸无效。");
        var width = metadata.Frames[0].PixelWidth; var height = metadata.Frames[0].PixelHeight;
        stream.Position = 0;
        var image = new BitmapImage(); image.BeginInit(); image.CacheOption = BitmapCacheOption.OnLoad;
        if (thumbnail)
        {
            if (width >= height) image.DecodePixelWidth = Math.Min(360, width);
            else image.DecodePixelHeight = Math.Min(180, height);
        }
        image.StreamSource = stream; image.EndInit(); image.Freeze(); return image;
    }

    internal static async Task<DataObject> BuildDataAsync(ClipboardRecord record, ClipboardHistoryStore store)
    {
        var image = await Task.Run(() => PrepareImage(record, store));
        return CreateData(record, image);
    }

    public static async Task CopyAsync(ClipboardRecord record, ClipboardHistoryStore store)
    {
        var data = await BuildDataAsync(record, store);
        for (var attempt = 0; ; attempt++)
        {
            try { Clipboard.SetDataObject(data, true); return; }
            catch (ExternalException) when (attempt < 5) { await Task.Delay(40 * (attempt + 1)); }
        }
    }
}

internal sealed class ClipboardHistoryRuntime : IDisposable
{
    private static ClipboardHistoryRuntime? _current;
    private static Task<ClipboardHistoryStore>? _store;
    private readonly HwndSource _source;
    private readonly DispatcherTimer _timer;
    private readonly ClipboardHistoryWriteQueue _writes;
    private readonly HashSet<Task> _captures = [];
    private readonly Func<Task<CapturedClipboard?>> _capture;
    private readonly Func<uint> _sequence;
    private bool _capturePending;
    private bool _stopping;
    private bool _disposed;
    public static string? LastError { get; private set; }
    public static Task<ClipboardHistoryStore> StoreAsync => _store ??= Task.Run(() => new ClipboardHistoryStore(ClipboardHistoryStore.DefaultFolder));

    public static void Start(Dispatcher dispatcher)
    {
        if (_current is not null) return;
        try { _current = new(dispatcher, StoreAsync); }
        catch (Exception) { LastError = "剪贴板后台监听未启动，请重启应用后重试。已有历史仍可使用。"; }
    }

    internal ClipboardHistoryRuntime(Dispatcher dispatcher, Task<ClipboardHistoryStore> ready,
        Func<CapturedClipboard?>? capture = null, Func<uint>? sequence = null, bool listen = true,
        Func<Task<CapturedClipboard?>>? captureAsync = null)
    {
        _capture = captureAsync ?? (capture is not null ? () => Task.FromResult(capture())
            : () => Clipboard.GetDataObject() is { } data ? ClipboardContent.CaptureAsync(data, ReadSource()) : Task.FromResult<CapturedClipboard?>(null));
        _sequence = sequence ?? GetClipboardSequenceNumber;
        _writes = new(async captured =>
        {
            var store = await ready.ConfigureAwait(false);
            await Task.Run(() => store.Add(captured.Record, captured.Png)).ConfigureAwait(false);
        }, error => LastError = error is null ? null : "有一条剪贴板内容未能保存，请检查数据目录可写及磁盘空间；已有记录不会自动清理。");
        _timer = new DispatcherTimer(TimeSpan.FromMilliseconds(80), DispatcherPriority.Background, OnTick, dispatcher);
        _timer.Stop();
        _source = new HwndSource(new HwndSourceParameters("ScreenTranslate clipboard history")
        { ParentWindow = new IntPtr(-3), Width = 0, Height = 0, WindowStyle = 0 });
        _source.AddHook(WndProc);
        if (listen && !AddClipboardFormatListener(_source.Handle)) { _source.Dispose(); throw new InvalidOperationException(); }
    }

    private IntPtr WndProc(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message == 0x031D) NotifyClipboardChanged();
        return IntPtr.Zero;
    }

    internal void NotifyClipboardChanged()
    { if (!_disposed && !_stopping) { _timer.Stop(); _timer.Start(); } }

    private void OnTick(object? sender, EventArgs e)
    {
        _timer.Stop();
        if (!_stopping && !_disposed) TrackCapture();
    }

    internal void TrackCapture()
    {
        // 压缩期间合并通知，只再取一次最新剪贴板，避免快速复制大图积累后台任务。
        if (_captures.Count > 0) { _capturePending = true; return; }
        var task = CaptureAsync(); _captures.Add(task); _ = ObserveCaptureAsync(task);
    }
    private async Task ObserveCaptureAsync(Task task) { await task; _captures.Remove(task); }

    private async Task CaptureAsync()
    {
        do { _capturePending = false; await CaptureOnceAsync(); }
        while (_capturePending && !_disposed);
    }

    private async Task CaptureOnceAsync()
    {
        try
        {
            if (_disposed) return;
            CapturedClipboard? captured = null;
            var sequence = _sequence();
            for (var attempt = 0; ; attempt++)
            {
                try
                {
                    captured = await _capture();
                    break;
                }
                catch (ExternalException) when (attempt < 4) { await Task.Delay(40); }
                if (_disposed || sequence != _sequence()) return;
            }
            if (_disposed || sequence != _sequence() || captured is null) return;
            _writes.Enqueue(captured);
        }
        catch (Exception) { LastError = "有一条剪贴板内容未能保存，请检查数据目录可写及磁盘空间；已有记录不会自动清理。"; }
    }

    private static string ReadSource()
    {
        try
        {
            GetWindowThreadProcessId(GetClipboardOwner(), out var processId);
            if (processId == 0 || processId == Environment.ProcessId) return "";
            using var process = Process.GetProcessById((int)processId);
            return process.ProcessName + ".exe";
        }
        catch (Exception) { return ""; }
    }

    public static async Task StopAsync()
    {
        var current = _current; _current = null;
        if (current is null) return;
        await current.StopAndDrainAsync();
    }

    internal async Task StopAndDrainAsync()
    {
        if (_disposed) return;
        _stopping = true;
        var captureLast = _timer.IsEnabled;
        _timer.Stop(); RemoveClipboardFormatListener(_source.Handle);
        if (captureLast) TrackCapture();
        await Task.WhenAll(_captures.ToArray());
        await _writes.DrainAsync();
        Dispose();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true; _timer.Stop(); RemoveClipboardFormatListener(_source.Handle); _source.RemoveHook(WndProc); _source.Dispose();
    }
    [DllImport("user32.dll", SetLastError = true)] private static extern bool AddClipboardFormatListener(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool RemoveClipboardFormatListener(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern uint GetClipboardSequenceNumber();
    [DllImport("user32.dll")] private static extern IntPtr GetClipboardOwner();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);
}
