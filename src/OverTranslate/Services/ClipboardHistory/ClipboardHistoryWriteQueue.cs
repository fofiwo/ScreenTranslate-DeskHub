namespace OverTranslate.Services.ClipboardHistory;

// 队列只接收已捕获快照；磁盘延迟不能推迟读取下一条剪贴板内容。
internal sealed class ClipboardHistoryWriteQueue(Func<CapturedClipboard, Task> save, Action<Exception?> report)
{
    private readonly object _gate = new();
    private Task _tail = Task.CompletedTask;

    public void Enqueue(CapturedClipboard captured)
    {
        lock (_gate) _tail = SaveAfterAsync(_tail, captured);
    }

    public Task DrainAsync() { lock (_gate) return _tail; }

    private async Task SaveAfterAsync(Task previous, CapturedClipboard captured)
    {
        await previous.ConfigureAwait(false);
        try { await save(captured).ConfigureAwait(false); report(null); }
        catch (Exception ex) { report(ex); }
    }
}
