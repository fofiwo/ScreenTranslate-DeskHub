namespace OverTranslate.Services;

internal static class OcrExtraction
{
    // 最后的时效检查和剪贴板写入在调用方的 UI 线程连续执行，过期任务不能覆盖新剪贴板。
    internal static async Task<bool> CopyAsync(
        Func<CancellationToken, Task<List<OcrTextBlock>>> recognize,
        bool preserveFormatting, Func<bool> stillCurrent, Action<string> writeClipboard,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var blocks = await recognize(cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        if (!stillCurrent()) return false;
        var text = OcrTextFormatter.Format(blocks, preserveFormatting);
        if (string.IsNullOrWhiteSpace(text)) return false;
        cancellationToken.ThrowIfCancellationRequested();
        if (!stillCurrent()) return false;
        writeClipboard(text);
        return true;
    }
}
