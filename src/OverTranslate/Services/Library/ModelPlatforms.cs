using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace OverTranslate.Services.Library;

internal sealed class ModelPlatform
{
    public string Name { get; set; } = "";
    public string Endpoint { get; set; } = "";
    public string Hint { get; set; } = "";
    public Dictionary<string, string>? EndpointsByType { get; set; }
    public Dictionary<string, string>? HintsByType { get; set; }
}

internal static class ModelPlatforms
{
    public static readonly Dictionary<string, string> Types = new()
    { ["chat"] = "对话", ["image"] = "生图", ["multimodal"] = "多模态", ["embedding"] = "嵌入", ["audio"] = "语音", ["other"] = "其他" };
    public static string TypeLabel(string type) => Types.GetValueOrDefault(type, "其他");
    public static readonly IReadOnlyList<ModelPlatform> Presets = Load();
    private static List<ModelPlatform> Load()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("OverTranslate.Services.Library.model-platforms.json")!;
        return JsonSerializer.Deserialize<List<ModelPlatform>>(stream, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
    }
    public static ModelPlatform? Find(string platform) => Presets.FirstOrDefault(p => p.Name.Equals(platform.Trim(), StringComparison.OrdinalIgnoreCase));
    public static string Endpoint(string platform, string type, string current = "")
    {
        if (platform == "Cloudflare Workers AI")
        {
            var match = Regex.Match(current, @"^(https://api\.cloudflare\.com/client/v4/accounts/[^/?#]+/ai)(?:/v1)?/?$", RegexOptions.IgnoreCase);
            return match.Success ? match.Groups[1].Value + (type is "chat" or "embedding" ? "/v1" : "") : "";
        }
        var preset = Find(platform);
        return preset?.EndpointsByType?.GetValueOrDefault(type) ?? preset?.Endpoint ?? "";
    }
    public static string Hint(string platform, string type)
    {
        var preset = Find(platform);
        return preset is null ? "自定义平台：填写 API 基础地址，固定模型 ID 可留空。" : preset.Hint + " " + preset.HintsByType?.GetValueOrDefault(type);
    }
    public static bool SameOrigin(string left, string right) => Uri.TryCreate(left, UriKind.Absolute, out var l) && Uri.TryCreate(right, UriKind.Absolute, out var r)
        && l.Scheme == r.Scheme && l.Host == r.Host && l.Port == r.Port;
}
