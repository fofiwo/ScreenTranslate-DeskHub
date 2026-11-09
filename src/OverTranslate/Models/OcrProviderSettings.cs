using System.Text.Json.Serialization;

namespace OverTranslate.Models;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum OcrProviderKind { Local, Http, Baidu }

public sealed class OcrProviderSettings
{
    public OcrProviderKind Provider { get; set; } = OcrProviderKind.Local;
    public string Endpoint { get; set; } = "http://127.0.0.1:8080/ocr";
    public int TimeoutSeconds { get; set; } = 15;
    public int MinimumIntervalMs { get; set; } = 1500;
    public string BaiduApiKeyProtected { get; set; } = "";
    public string BaiduSecretKeyProtected { get; set; } = "";
}
