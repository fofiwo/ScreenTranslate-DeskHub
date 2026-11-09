using System.Net;
using System.Net.Http;
using OverTranslate.Models;

namespace OverTranslate.Services;

internal static class NetworkProxyService
{
    private sealed record Snapshot(bool Enabled, Uri? Address, NetworkFeature Features);
    private static Snapshot _current = new(false, null, NetworkFeature.All);

    internal static readonly (NetworkFeature Feature, string Label)[] Features =
    [
        (NetworkFeature.Updates, "应用更新：检查版本、下载安装包、更新说明"),
        (NetworkFeature.Translation, "翻译：文本、截图、持续翻译、替换翻译（含 OpenAI / DeepL）"),
        (NetworkFeature.Dictionary, "词典查询：取词、查词与词典释义"),
        (NetworkFeature.Speech, "语音朗读：在线语音与音频下载"),
        (NetworkFeature.OnlineOcr, "在线 OCR：百度、OpenAI 及自定义远程识别接口"),
        (NetworkFeature.ModelDownloads, "模型下载：OCR 语言包、漫画模型与运行组件"),
        (NetworkFeature.Library, "资料库联网：模型列表、连接检查、AI 整理"),
        (NetworkFeature.Diagnostics, "诊断上传：仅在主动提交诊断时请求"),
    ];

    internal static bool TryParseAddress(string address, out Uri? proxy)
    {
        proxy = null;
        var text = address.Trim();
        if (text.Any(char.IsControl) || text.Contains('\\')
            || !Uri.TryCreate(text, UriKind.Absolute, out var uri)
            || uri.Scheme is not ("http" or "https" or "socks5") || uri.Host.Length == 0
            || uri.Port is <= 0 or > 65535 || uri.UserInfo.Length != 0 || uri.Query.Length != 0
            || uri.Fragment.Length != 0 || uri.AbsolutePath is not ("" or "/")) return false;
        proxy = uri;
        return true;
    }

    internal static void Apply(NetworkProxySettings settings)
    {
        TryParseAddress(settings.Address, out var address);
        Volatile.Write(ref _current, new(settings.Enabled, address, settings.Features));
    }

    internal static IWebProxy For(NetworkFeature feature) => new FeatureProxy(feature, HttpClient.DefaultProxy);

    internal static HttpClientHandler CreateHandler(NetworkFeature feature, bool allowAutoRedirect = true) => new()
    {
        Proxy = For(feature), AutomaticDecompression = DecompressionMethods.All, AllowAutoRedirect = allowAutoRedirect,
    };

    internal sealed class FeatureProxy(NetworkFeature feature, IWebProxy system) : IWebProxy, ICredentials
    {
        private static readonly Uri InvalidProxy = new("invalid-proxy://settings");
        public ICredentials? Credentials
        {
            get => this;
            set { }
        }
        NetworkCredential? ICredentials.GetCredential(Uri uri, string authType)
        {
            if (UsesCustom(Volatile.Read(ref _current)) || system.IsBypassed(uri)) return null;
            var expected = system.GetProxy(uri);
            // 切换设置时，旧自定义代理的延迟认证挑战也不能得到系统代理凭据。
            return expected is not null && string.Equals(expected.GetLeftPart(UriPartial.Authority),
                uri.GetLeftPart(UriPartial.Authority), StringComparison.OrdinalIgnoreCase)
                ? system.Credentials?.GetCredential(uri, authType) : null;
        }
        private bool UsesCustom(Snapshot current) => current.Enabled && (current.Features & feature) != 0;
        public Uri? GetProxy(Uri destination)
        {
            // 本机模型 / OCR 不绕到外部代理。局域网同步使用独立 TCP，不受此设置影响。
            if (destination.IsLoopback) return destination;
            var current = Volatile.Read(ref _current);
            if (!UsesCustom(current)) return system.IsBypassed(destination) ? destination : system.GetProxy(destination);
            // .NET 会吞掉 IWebProxy 抛出的异常并改为直连；返回不支持的代理协议才能阻断请求。
            return current.Address ?? InvalidProxy;
        }
        public bool IsBypassed(Uri host) => GetProxy(host) is not { } proxy || proxy == host;
    }
}
