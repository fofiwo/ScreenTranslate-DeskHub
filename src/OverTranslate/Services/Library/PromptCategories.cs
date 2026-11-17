using System.Text.RegularExpressions;

namespace OverTranslate.Services.Library;

internal sealed record PromptCategory(string Name, string Description);
internal sealed record PromptClassification(string Id, string Category, string CategoryDescription, List<string> Tags, string Reason,
    string Title = "", string Description = "");

internal static class PromptCategories
{
    internal const int MaxBatchCharacters = 60000;
    internal static string Normalize(string value) => Regex.Replace(value.Trim(), @"\s+", " ");
    internal static bool ValidName(string? name) => name is not null && name.Length is >= 2 and <= 20
        && !name.Any(char.IsControl) && name == Normalize(name)
        && name is not ("全部" or "待分类" or "未分类" or "其他" or "其它" or "通用" or "效率" or "优化");
    internal static bool ValidFields(LibraryPrompt p) => p.Category is not null && p.SuggestedCategory is not null
        && (p.Category.Length == 0 || ValidName(p.Category)) && (p.SuggestedCategory.Length == 0 || ValidName(p.SuggestedCategory))
        && p.CategoryDescription is { Length: <= 160 } && p.SuggestedCategoryDescription is { Length: <= 160 }
        && p.CategoryNote is { Length: <= 300 };
    internal static List<PromptCategory> Catalog(IEnumerable<LibraryPrompt> prompts) => prompts.Where(p => p.Category.Length > 0)
        .GroupBy(p => p.Category, StringComparer.OrdinalIgnoreCase)
        .Select(g => new PromptCategory(g.Key, g.Select(p => p.CategoryDescription).FirstOrDefault(d => d.Length > 0) ?? ""))
        .OrderBy(c => c.Name, StringComparer.CurrentCulture).ToList();
}

internal sealed partial class LibraryStore
{
    public void ApplyClassification(LibraryPrompt expected, PromptClassification result, bool approveNewCategory = false)
    {
        if (result.Id != expected.Id || result.Tags is null || result.Tags.Count > 12 || result.Tags.Any(t => string.IsNullOrWhiteSpace(t) || t.Length > 24)
            || result.CategoryDescription.Length > 160 || result.Reason.Length > 300
            || (result.Category.Length > 0 && !PromptCategories.ValidName(result.Category)))
            throw new InvalidOperationException("AI 分类结果格式无效，未更改提示词。");
        Update(s =>
        {
            var current = ClassificationTarget(s, expected);
            if (!current.CategoryManual)
            {
                var category = PromptCategories.Catalog(s.Prompts).FirstOrDefault(c => c.Name.Equals(result.Category, StringComparison.OrdinalIgnoreCase));
                if (result.Category.Length == 0)
                {
                    current.Category = current.CategoryDescription = current.SuggestedCategory = current.SuggestedCategoryDescription = "";
                    current.CategoryNote = result.Reason.Length > 0 ? result.Reason : "用途不明确，请补充具体任务和预期结果。";
                }
                else if (category is not null || approveNewCategory)
                {
                    current.Category = category?.Name ?? result.Category;
                    current.CategoryDescription = category?.Description ?? result.CategoryDescription;
                    current.SuggestedCategory = current.SuggestedCategoryDescription = current.CategoryNote = "";
                }
                else
                {
                    current.SuggestedCategory = result.Category;
                    current.SuggestedCategoryDescription = result.CategoryDescription;
                    current.CategoryNote = result.Reason;
                }
            }
            if (result.Title.Length > 0) current.Title = result.Title;
            if (result.Description.Length > 0) current.Description = result.Description;
            current.Tags = result.Tags;
            current.UpdatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        });
    }

    private static LibraryPrompt ClassificationTarget(LibraryState state, LibraryPrompt expected)
    {
        var current = state.Prompts.FirstOrDefault(p => p.Id == expected.Id);
        if (current is null || !SamePromptContent(current, expected) || current.UpdatedAt != expected.UpdatedAt)
            throw new InvalidOperationException("提示词已更改或删除，本次分类未写入。请核对最新内容后重试。");
        return current;
    }

    public void SetPromptCategory(LibraryPrompt expected, string name, bool approveSuggestion = false)
    {
        name = PromptCategories.Normalize(name);
        if (approveSuggestion && !PromptCategories.ValidName(name))
            throw new InvalidOperationException("请输入 2–20 字的具体任务名称；不能使用其他、通用等含糊名称。");
        Update(s =>
        {
            var current = ClassificationTarget(s, expected);
            var category = PromptCategories.Catalog(s.Prompts).FirstOrDefault(c => c.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (name.Length > 0 && category is null && (!approveSuggestion || !PromptCategories.ValidName(name) || current.SuggestedCategory.Length == 0))
                throw new InvalidOperationException("请选择已有分类，或确认 AI 建议的新分类。");
            current.Category = category?.Name ?? name;
            current.CategoryDescription = name.Length == 0 ? "" : category?.Description ?? current.SuggestedCategoryDescription;
            current.CategoryManual = name.Length > 0;
            current.SuggestedCategory = current.SuggestedCategoryDescription = current.CategoryNote = "";
            current.UpdatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        });
    }

    public void SaveClassificationFailure(LibraryPrompt expected, string message) => Update(s =>
    {
        var current = ClassificationTarget(s, expected);
        current.CategoryNote = message[..Math.Min(message.Length, 300)];
        current.UpdatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    });
}
