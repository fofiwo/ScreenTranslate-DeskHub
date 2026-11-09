using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using OverTranslate.Models;

namespace OverTranslate.Services.Ocr;

internal sealed class HttpOcrProvider : IDisposable
{
    private readonly HttpClient _client;
    private readonly SemaphoreSlim _slot = new(1, 1);
    private DateTimeOffset _nextRead;
    private string _token = "";
    private string _credentialFingerprint = "";
    private DateTimeOffset _tokenExpiry;

    internal HttpOcrProvider(HttpMessageHandler? handler = null)
    {
        _client = handler is null
            ? new HttpClient(new HttpClientHandler { AllowAutoRedirect = false })
            : new HttpClient(handler);
        _client.Timeout = Timeout.InfiniteTimeSpan;
    }

    internal static Uri ValidateEndpoint(string endpoint)
    {
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) || !string.IsNullOrEmpty(uri.UserInfo)
            || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment)
            || (uri.Scheme != "https" && !(uri.Scheme == "http" && uri.IsLoopback)))
            throw new InvalidOperationException("OCR 地址仅允许 HTTPS 或本机回环 HTTP；认证信息不能放在地址中。");
        return uri;
    }

    internal async Task<List<OcrTextBlock>?> ReadAsync(Bitmap bitmap, string language,
        OcrProviderSettings options, bool skipBusy, CancellationToken cancellationToken)
    {
        if (skipBusy ? !await _slot.WaitAsync(0, cancellationToken) : !await TakeAsync(cancellationToken)) return null;
        try
        {
            // 云端/HTTP 连续识别单独限频，不占用 ONNX 推理槽，不积压帧。
            if (skipBusy && DateTimeOffset.UtcNow < _nextRead) return null;
            _nextRead = DateTimeOffset.UtcNow.AddMilliseconds(Math.Clamp(options.MinimumIntervalMs, 500, 60000));
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(options.TimeoutSeconds, 2, 120)));
            var token = timeout.Token;
            using var stream = new MemoryStream();
            bitmap.Save(stream, ImageFormat.Png);
            if (stream.Length > 8 * 1024 * 1024) throw new InvalidOperationException("OCR 图片超过 8MB，请缩小框选范围。");
            var image = Convert.ToBase64String(stream.ToArray());
            var baidu = options.Provider == OcrProviderKind.Baidu;
            using var request = new HttpRequestMessage(HttpMethod.Post, baidu
                ? new Uri("https://aip.baidubce.com/rest/2.0/ocr/v1/general?access_token=" + Uri.EscapeDataString(await GetTokenAsync(options, token)))
                : ValidateEndpoint(options.Endpoint));
            request.Content = baidu
                ? new FormUrlEncodedContent(new Dictionary<string, string> { ["image"] = image, ["language_type"] = BaiduLanguage(language), ["probability"] = "true" })
                : new StringContent(JsonSerializer.Serialize(new { image, language, width = bitmap.Width, height = bitmap.Height }), System.Text.Encoding.UTF8, "application/json");
            var json = await SendAsync(request, token);
            cancellationToken.ThrowIfCancellationRequested();
            return OcrResponseParser.Parse(json, bitmap.Width, bitmap.Height, baidu);
        }
        catch (HttpRequestException) { throw new InvalidOperationException("OCR 网络连接失败，请检查地址、代理或服务状态。"); }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        { throw new TimeoutException("OCR 请求超时，请检查服务或缩小框选范围。"); }
        finally { _slot.Release(); }
    }

    private async Task<bool> TakeAsync(CancellationToken token) { await _slot.WaitAsync(token); return true; }

    private async Task<string> GetTokenAsync(OcrProviderSettings options, CancellationToken token)
    {
        var fingerprint = options.BaiduApiKeyProtected + ":" + options.BaiduSecretKeyProtected;
        if (_token.Length > 0 && fingerprint == _credentialFingerprint && DateTimeOffset.UtcNow < _tokenExpiry) return _token;
        var apiKey = LocalSecretStore.Unprotect(options.BaiduApiKeyProtected);
        var secret = LocalSecretStore.Unprotect(options.BaiduSecretKeyProtected);
        if (apiKey.Length == 0 || secret.Length == 0) throw new InvalidOperationException("请先在 OCR 设置中保存百度 API Key 和 Secret Key。");
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://aip.baidubce.com/oauth/2.0/token")
        { Content = new FormUrlEncodedContent(new Dictionary<string, string> { ["grant_type"] = "client_credentials", ["client_id"] = apiKey, ["client_secret"] = secret }) };
        using var doc = JsonDocument.Parse(await SendAsync(request, token));
        if (!doc.RootElement.TryGetProperty("access_token", out var accessToken) || string.IsNullOrEmpty(accessToken.GetString()))
            throw new InvalidOperationException("百度 OCR 认证失败，请检查凭据及 OCR 权限。");
        _token = accessToken.GetString()!;
        _tokenExpiry = DateTimeOffset.UtcNow.AddSeconds(doc.RootElement.TryGetProperty("expires_in", out var expires) ? Math.Max(0, expires.GetInt32() - 60) : 300);
        _credentialFingerprint = fingerprint;
        return _token;
    }

    private async Task<string> SendAsync(HttpRequestMessage request, CancellationToken token)
    {
        using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException($"OCR 服务请求失败（HTTP {(int)response.StatusCode}）。");
        await using var stream = await response.Content.ReadAsStreamAsync(token);
        using var buffer = new MemoryStream();
        var bytes = new byte[8192];
        int count;
        while ((count = await stream.ReadAsync(bytes, token)) > 0)
        {
            if (buffer.Length + count > 4 * 1024 * 1024) throw new InvalidOperationException("OCR 服务返回内容超过 4MB。");
            buffer.Write(bytes, 0, count);
        }
        return System.Text.Encoding.UTF8.GetString(buffer.ToArray());
    }

    private static string BaiduLanguage(string language) => language.ToUpperInvariant() switch
    { "EN" => "ENG", "JA" => "JAP", "KO" => "KOR", "FR" => "FRE", "DE" => "GER", "ES" => "SPA", "RU" => "RUS", _ => "CHN_ENG" };

    public void Dispose() => _client.Dispose();
}
