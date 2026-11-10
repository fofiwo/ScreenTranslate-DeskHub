using System.Security.Cryptography;
using System.Text.Json;
using OverTranslate.Services.Library;

namespace OverTranslate.Services.Lan;

// 复用已确认的设备配对；资料库有独立通道，不与剪贴板及文件相互排队。
internal sealed class LanLibrarySync : IAsyncDisposable
{
    private readonly LanNode _node;
    private readonly Lazy<LibraryStore> _store;
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _loop;
    private readonly TimeSpan _interval;
    public event Action<string>? StatusChanged;
    public event Action? Changed;

    public LanLibrarySync(LanNode node, Func<LibraryStore> store, TimeSpan? interval = null)
    {
        // 文件暂时被占用时不缓存构造异常；下一次发送或接收可以重新打开。
        _node = node; _store = new(store, LazyThreadSafetyMode.PublicationOnly); _interval = interval ?? TimeSpan.FromSeconds(3);
        node.ApplyLibraryAsync = ApplyAsync;
        _loop = Task.Run(RunAsync);
    }

    private Task ApplyAsync(byte[] bytes, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!_node.LibraryEnabled) throw new InvalidOperationException("资料库同步已暂停。");
        try
        {
            var snapshot = JsonSerializer.Deserialize<LibrarySyncSnapshot>(bytes)
                ?? throw new InvalidOperationException("资料库内容无效。");
            if (_store.Value.MergeSync(snapshot)) Changed?.Invoke();
            StatusChanged?.Invoke("已接收并保存对方的提示词库、模型库。");
        }
        catch (Exception)
        {
            StatusChanged?.Invoke("资料库接收未完成，请检查资料格式和目录权限；对方会重试。");
            throw;
        }
        return Task.CompletedTask;
    }

    private async Task RunAsync()
    {
        long connection = -1;
        string? sent = null;
        while (!_stop.IsCancellationRequested)
        {
            try
            {
                if (_node.IsConnected && _node.LibraryEnabled)
                {
                    var version = _node.ConnectionVersion;
                    if (version != connection) { connection = version; sent = null; }
                    var bytes = JsonSerializer.SerializeToUtf8Bytes(_store.Value.ExportSync());
                    try
                    {
                        var hash = LanProtocol.Hash(bytes);
                        if (hash != sent && _node.ConnectionVersion == version && _node.IsConnected && _node.LibraryEnabled)
                        {
                            StatusChanged?.Invoke("正在同步提示词库和模型库…");
                            if (await _node.SendLibraryAsync(bytes, _stop.Token).ConfigureAwait(false))
                            { sent = hash; StatusChanged?.Invoke("提示词库和模型库已同步；后续修改会自动同步。"); }
                            else StatusChanged?.Invoke("对方未能接收或保存资料库，请检查对方同步开关、资料和目录权限；将自动重试。");
                        }
                    }
                    finally { CryptographicOperations.ZeroMemory(bytes); }
                }
                else sent = null;
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                _node.ReportPreparationFailure(SyncKind.Library, ex);
                StatusChanged?.Invoke("资料库同步未完成，请检查连接与资料大小；已有资料保留，将自动重试。");
            }
            try { await Task.Delay(_interval, _stop.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
        }
    }

    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        await _loop.ConfigureAwait(false);
        _node.ApplyLibraryAsync = null;
        _stop.Dispose();
    }
}
