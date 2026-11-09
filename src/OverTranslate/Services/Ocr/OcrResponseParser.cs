using System.Text.Json;
using System.Windows;

namespace OverTranslate.Services.Ocr;

internal static class OcrResponseParser
{
    internal static List<OcrTextBlock> Parse(string json, int width, int height, bool baidu)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.TryGetProperty("error_code", out _))
            throw new InvalidOperationException("OCR 服务返回错误，请检查服务配置、配额和权限。");
        var sx = 1.0;
        var sy = 1.0;
        if (!baidu)
        {
            if (!root.TryGetProperty("width", out var w) || !root.TryGetProperty("height", out var h)
                || !w.TryGetDouble(out var rw) || !h.TryGetDouble(out var rh)
                || !double.IsFinite(rw) || !double.IsFinite(rh) || rw <= 0 || rh <= 0)
                throw new InvalidOperationException("HTTP OCR 必须返回有效的 width、height 和 blocks。");
            sx = width / rw;
            sy = height / rh;
        }
        if (!root.TryGetProperty(baidu ? "words_result" : "blocks", out var items)
            || items.ValueKind != JsonValueKind.Array || items.GetArrayLength() > 4096)
            throw new InvalidOperationException("OCR 返回缺少文字块或文字块过多。");
        var result = new List<OcrTextBlock>();
        foreach (var item in items.EnumerateArray())
        {
            var text = item.GetProperty(baidu ? "words" : "text").GetString();
            if (string.IsNullOrWhiteSpace(text)) continue;
            if (text.Length > 16384) throw new InvalidOperationException("OCR 文字块过长。");
            var box = baidu ? item.GetProperty("location") : item;
            var x = box.GetProperty(baidu ? "left" : "x").GetDouble() * sx;
            var y = box.GetProperty(baidu ? "top" : "y").GetDouble() * sy;
            var bw = box.GetProperty("width").GetDouble() * sx;
            var bh = box.GetProperty("height").GetDouble() * sy;
            if (!double.IsFinite(x) || !double.IsFinite(y) || !double.IsFinite(bw) || !double.IsFinite(bh)
                || bw <= 0 || bh <= 0)
                throw new InvalidOperationException("OCR 坐标无效。");
            var bounds = Rect.Intersect(new Rect(x, y, bw, bh), new Rect(0, 0, width, height));
            if (bounds.IsEmpty || bounds.Width <= 0 || bounds.Height <= 0) continue;
            result.Add(new OcrTextBlock(text, bounds, LayoutBounds: bounds));
        }
        return result;
    }
}
