using System.Text;
using System.Text.RegularExpressions;
using System.Windows;

namespace OverTranslate.Services;

internal static class OcrTextFormatter
{
    internal static string Format(IReadOnlyList<OcrTextBlock> blocks, bool preserveFormatting)
    {
        var nonempty = blocks.Where(b => !string.IsNullOrWhiteSpace(b.Text)).ToList();
        if (nonempty.Count == 0) return "";
        var text = nonempty.Count == 1 || nonempty.Any(b => b.Bounds.IsEmpty || b.Bounds.Height <= 0)
            ? string.Join("\n", nonempty.Select(b => Normalize(b.Text)))
            : Arrange(nonempty);
        return preserveFormatting ? text.Trim('\n') : Cleanup(text);
    }

    private static string Normalize(string text) => text.Replace("\r\n", "\n").Replace('\r', '\n');

    private static string Arrange(List<OcrTextBlock> blocks)
    {
        var lines = blocks.SelectMany(block =>
        {
            var parts = Normalize(block.Text).Split('\n');
            return parts.Select((text, i) => (Text: text, Bounds: block.SourceLineBounds?.Count == parts.Length
                ? block.SourceLineBounds[i]
                : new Rect(block.Bounds.X, block.Bounds.Y + i * block.Bounds.Height / parts.Length,
                    block.Bounds.Width, block.Bounds.Height / parts.Length)));
        }).OrderBy(line => line.Bounds.Top).ThenBy(line => line.Bounds.Left).ToList();
        var height = lines.Select(l => l.Bounds.Height).Order().ElementAt(lines.Count / 2);
        var glyph = lines.Where(l => l.Text.Trim().Length > 0)
            .Select(l => l.Bounds.Width / l.Text.Trim().Length).Where(w => w > 0).Order().ToArray();
        var space = glyph.Length == 0 ? 10 : Math.Max(1, Math.Min(height / 2, glyph[glyph.Length / 2]));
        var left = lines.Min(l => l.Bounds.Left);
        var rows = new List<List<(string Text, Rect Bounds)>>();
        foreach (var line in lines)
        {
            var row = rows.LastOrDefault();
            if (row is null || Math.Abs(line.Bounds.Top - row[0].Bounds.Top) > height * .45)
                rows.Add([line]);
            else row.Add(line);
        }
        var result = new StringBuilder();
        double? previousBottom = null;
        foreach (var row in rows)
        {
            row.Sort((a, b) => a.Bounds.Left.CompareTo(b.Bounds.Left));
            if (previousBottom is not null)
            {
                result.Append('\n');
                if (row[0].Bounds.Top - previousBottom > height * .8) result.Append('\n');
            }
            result.Append(' ', Math.Clamp((int)Math.Round((row[0].Bounds.Left - left) / space), 0, 80));
            result.AppendJoin('\t', row.Select(l => l.Text));
            previousBottom = row.Max(l => l.Bounds.Bottom);
        }
        return result.ToString();
    }

    private static readonly Regex ListStart = new(@"^(?:[-*•●▪‣]|\d+[.)、]|[（(]?\d+[）)]|[一二三四五六七八九十]+[、.])\s*", RegexOptions.Compiled);
    private static string Cleanup(string text)
    {
        var result = new StringBuilder();
        bool paragraph = false, previousTable = false;
        foreach (var raw in Normalize(text).Split('\n'))
        {
            if (string.IsNullOrWhiteSpace(raw)) { paragraph = result.Length > 0; continue; }
            bool list = ListStart.IsMatch(raw.TrimStart());
            bool table = raw.Contains('\t') && (!list || raw.TrimStart(' ', '\t').Contains('\t'));
            list &= !table;
            var line = table
                ? string.Join('\t', raw.Split('\t').Select(cell => Regex.Replace(cell.Trim(), @"[^\S\n]+", " ")))
                : Regex.Replace(raw.Trim(), @"[^\S\n]+", " ");
            // 列表层级属于文本结构；仅清理条目正文的多余空白。
            if (list) line = raw[..(raw.Length - raw.TrimStart().Length)] + line;
            if (result.Length > 0)
            {
                if (paragraph) result.Append("\n\n");
                else if (table || previousTable || list) result.Append('\n');
                else if (NeedsSpace(result[^1], line[0])) result.Append(' ');
            }
            result.Append(line);
            paragraph = false;
            previousTable = table;
        }
        return result.ToString();
    }

    private static bool NeedsSpace(char previous, char next)
    {
        if (",.;:!?，。；：！？、)]}）】》〉」』".Contains(next) ||
            "([{（【《〈「『".Contains(previous)) return false;
        return (!IsCjk(previous) && !IsCjk(next)) ||
            (char.IsLetterOrDigit(previous) && !IsCjk(previous)) ||
            (char.IsLetterOrDigit(next) && !IsCjk(next));
    }

    private static bool IsCjk(char c) => c is >= '\u2e80' and <= '\u9fff' or >= '\uac00' and <= '\ud7af' or >= '\uf900' and <= '\ufaff';
}
