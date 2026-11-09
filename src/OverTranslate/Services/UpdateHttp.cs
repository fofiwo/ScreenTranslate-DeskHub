using OverTranslate.Models;
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

    // GitHub 的路径区分大小写；失败后小写重试会掩盖原错误并把超时时间翻倍。
    protected override Task<T> TryDownloadThenLowercase<T>(Func<string, Task<T>> downloadFunc, string url) => downloadFunc(url);
}
