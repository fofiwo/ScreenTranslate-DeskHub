using OverTranslate.Models;
using System.IO;
using System.Net.Http;
using Velopack.Sources;

namespace OverTranslate.Services;

internal static class UpdateHttp
{
    internal static HttpClientHandler CreateHandler() => NetworkProxyService.CreateHandler(NetworkFeature.Updates);
}

internal sealed class UpdateDownloader : HttpClientFileDownloader
{
    protected override HttpClientHandler CreateHttpClientHandler() => UpdateHttp.CreateHandler();

    public override async Task DownloadFile(string url, string targetFile, Action<int> progress,
        IDictionary<string, string>? headers, double timeout, CancellationToken cancelToken = default)
    {
        // Velopack 的 ResponseHeadersRead 只让 HttpClient 超时覆盖响应头；正文也必须受同一总期限约束。
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancelToken);
        deadline.CancelAfter(TimeSpan.FromMinutes(timeout));
        try
        {
            for (var attempt = 0; ; attempt++)
            {
                try
                {
                    await base.DownloadFile(url, targetFile, progress, headers, timeout, deadline.Token).ConfigureAwait(false);
                    return;
                }
                catch (IOException) when (attempt == 0 && !deadline.IsCancellationRequested) { }
                catch (HttpRequestException ex) when (attempt == 0 && !deadline.IsCancellationRequested
                    && ex.HttpRequestError == HttpRequestError.ResponseEnded) { }

                // 仅重新下载一次，继续使用同一代理策略、版本路径和 Velopack 哈希校验；不拼接残包。
                // 独立 query 避免复用已缓存的下载重定向。HTTP 状态错误、取消和超时不会重试。
                await Task.Delay(TimeSpan.FromMilliseconds(250), deadline.Token).ConfigureAwait(false);
                var retry = new UriBuilder(url);
                retry.Query = retry.Query.TrimStart('?') + (retry.Query.Length > 1 ? "&" : "") + $"download={Guid.NewGuid():N}";
                url = retry.Uri.AbsoluteUri;
                headers = new Dictionary<string, string>(headers ?? new Dictionary<string, string>())
                {
                    ["Cache-Control"] = "no-cache, no-store", ["Pragma"] = "no-cache",
                };
            }
        }
        catch (OperationCanceledException ex) when (!cancelToken.IsCancellationRequested)
        {
            // 更新窗口把 OperationCanceledException 视为用户取消；超时必须显示失败及重试入口。
            throw new TimeoutException("更新包下载超时，请检查网络后重试。", ex);
        }
    }

    // GitHub 的路径区分大小写；失败后小写重试会掩盖原错误并把超时时间翻倍。
    protected override Task<T> TryDownloadThenLowercase<T>(Func<string, Task<T>> downloadFunc, string url) => downloadFunc(url);
}
