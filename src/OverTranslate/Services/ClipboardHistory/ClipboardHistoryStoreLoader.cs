namespace OverTranslate.Services.ClipboardHistory;

internal sealed class ClipboardHistoryStoreLoader(Func<ClipboardHistoryStore> open)
{
    private readonly object _gate = new();
    private Task<ClipboardHistoryStore>? _pending;

    internal Task<ClipboardHistoryStore> OpenAsync()
    {
        lock (_gate)
        {
            // 暂时的目录故障不能缓存到退出；进行中和成功的任务仍共用同一实例。
            if (_pending is null || _pending.IsFaulted || _pending.IsCanceled) _pending = Task.Run(open);
            return _pending;
        }
    }
}
