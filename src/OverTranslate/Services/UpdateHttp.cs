using OverTranslate.Models;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
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
        using var destination = File.Open(targetFile, FileMode.Create);
        long? totalLength = null;
        EntityTagHeaderValue? validator = null;
        try
        {
            for (var attempt = 0; ; attempt++)
            {
                using var client = CreateHttpClient(headers, timeout);
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                var offset = destination.Position;
                var resume = offset > 0 && totalLength.HasValue && validator is not null;
                if (resume)
                {
                    request.Headers.Range = new RangeHeaderValue(offset, null);
                    request.Headers.IfRange = new RangeConditionHeaderValue(validator!);
                }
                if (attempt > 0)
                {
                    var retry = new UriBuilder(url);
                    retry.Query = retry.Query.TrimStart('?') + (retry.Query.Length > 1 ? "&" : "") + $"download={Guid.NewGuid():N}";
                    request.RequestUri = retry.Uri;
                    request.Headers.CacheControl = new CacheControlHeaderValue { NoCache = true, NoStore = true };
                    request.Headers.Pragma.ParseAdd("no-cache");
                }

                using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token).ConfigureAwait(false);
                response.EnsureSuccessStatusCode();
                if (response.StatusCode == HttpStatusCode.PartialContent)
                {
                    var range = response.Content.Headers.ContentRange;
                    // 不信任代理返回的任意片段；只接受同一强 ETag、精确起点及原总长度的余下内容。
                    if (!resume || range is null || range.Unit != "bytes" || range.From != offset
                        || range.Length != totalLength || range.To != totalLength - 1
                        || !Equals(response.Headers.ETag, validator)
                        || response.Content.Headers.ContentLength is { } length && length != totalLength - offset)
                        throw new InvalidDataException("更新包续传响应不一致，已拒绝拼接，请重试。");
                }
                else if (response.StatusCode == HttpStatusCode.OK)
                {
                    // 服务器忽略 Range 或 If-Range 校验未通过时返回完整包，必须从零写入。
                    destination.Position = 0;
                    destination.SetLength(0);
                    totalLength = response.Content.Headers.ContentLength;
                    validator = response.Headers.ETag is { IsWeak: false } etag ? etag : null;
                }
                else throw new InvalidDataException("更新包响应不是完整文件或有效续传片段。");

                using var download = await response.Content.ReadAsStreamAsync(deadline.Token).ConfigureAwait(false);
                var buffer = new byte[81920];
                var interrupted = false;
                var lastProgress = -1;
                while (true)
                {
                    int count;
                    try { count = await download.ReadAsync(buffer, deadline.Token).ConfigureAwait(false); }
                    catch (IOException) when (attempt < 2 && !deadline.IsCancellationRequested)
                    { interrupted = true; break; }
                    if (count == 0) break;
                    // 磁盘写入错误不进入网络重试；没有强校验标记时，下次会丢弃残包重新下载。
                    await destination.WriteAsync(buffer.AsMemory(0, count), deadline.Token).ConfigureAwait(false);
                    if (totalLength is > 0)
                    {
                        var current = (int)Math.Min(99, destination.Position * 100d / totalLength.Value);
                        if (current - lastProgress >= 3) { progress(current); lastProgress = current; }
                    }
                }
                if (!interrupted)
                {
                    if (totalLength is { } expected && destination.Position != expected)
                        throw new IOException("更新包正文长度不完整。");
                    progress(100);
                    return;
                }
                // 最多三次请求，共享总期限及原代理策略；HTTP 状态、取消、超时均不重试。
                await Task.Delay(TimeSpan.FromMilliseconds(250), deadline.Token).ConfigureAwait(false);
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
