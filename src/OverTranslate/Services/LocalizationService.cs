using System.Globalization;
using System.Windows;
// UseWindowsForms puts System.Windows.Forms and System.Drawing in the implicit usings, so these
// names collide
using Application = System.Windows.Application;
using FontFamily = System.Windows.Media.FontFamily;

namespace OverTranslate.Services;

/// <param name="Display">
/// The language's name written in itself, and the one string in the app that is never translated.
/// </param>
/// <remarks>
/// Deliberately not a resource key. Someone who has landed in a language they cannot read has to be
/// able to find their way back out, and "繁體中文" is legible from an English interface in a way
/// that "Traditional Chinese" is not from a Chinese one.
/// </remarks>
/// <param name="NameKey">
/// Resource key for the language's name in the interface's current language, the same S.Lang.* key
/// the translation pickers use. Only searched, never shown — see <see cref="SearchText"/>.
/// </param>
/// <param name="English">Its English name, also only searched.</param>
public record UiLanguageOption(string Code, string Display, string NameKey, string English)
    : OverTranslate.Models.ISearchableItem
{
    /// <summary>
    /// The code, the name written in itself, its name in the current interface language and its
    /// English name.
    /// </summary>
    /// <remarks>
    /// The list shows each language only in itself, which is the one spelling a searcher is least
    /// likely to type: someone in a Chinese interface looking for Japanese types 日文, not 日本語, and
    /// an English one types "japanese".
    /// </remarks>
    public string SearchText => $"{Code} {Display} {LocalizationService.Get(NameKey)} {English}";
}

/// <summary>ScreenTranslate 的界面固定为简体中文；OCR 与翻译语言独立配置。</summary>
public static class LocalizationService
{
    // 语言代码仍供翻译提示词与文档路径使用，不代表提供界面切换。
    public const string TraditionalChinese = "zh-Hant";
    public const string SimplifiedChinese = "zh-Hans";
    public const string English = "en";
    public const string Japanese = "ja";
    public const string Korean = "ko";
    public const string Russian = "ru";

    public static readonly List<UiLanguageOption> Options =
    [
        new(SimplifiedChinese, "简体中文", "S.Lang.SimplifiedChinese", "Simplified Chinese"),
    ];

    public static bool IsSupported(string? language) =>
        string.Equals(language, SimplifiedChinese, StringComparison.OrdinalIgnoreCase);
    public static string Current => SimplifiedChinese;
    public static string ResolveSystemDefault() => SimplifiedChinese;
    public static event EventHandler? LanguageChanged;

    private static Uri DictionaryUri => new(
        $"pack://application:,,,/{typeof(LocalizationService).Assembly.GetName().Name};component/Resources/Strings.zh-Hans.xaml",
        UriKind.Absolute);

    public static void Apply(string language)
    {
        if (Application.Current is not { } app) return;
        var dictionaries = app.Resources.MergedDictionaries;
        var old = dictionaries.FirstOrDefault(d =>
            d.Source?.OriginalString.Contains("Strings.", StringComparison.OrdinalIgnoreCase) == true);
        if (old is not null) dictionaries.Remove(old);
        dictionaries.Add(new ResourceDictionary { Source = DictionaryUri });
        app.Resources["AppFont"] = new FontFamily("Segoe UI Variable Text, Segoe UI, Microsoft YaHei UI, Sans-Serif");
        LanguageChanged?.Invoke(null, EventArgs.Empty);
    }

    private static ResourceDictionary? _fallback;
    private static readonly object FallbackLock = new();
    private static ResourceDictionary? Fallback
    {
        get
        {
            lock (FallbackLock)
            {
                if (_fallback is not null) return _fallback;
                try
                {
                    _ = System.IO.Packaging.PackUriHelper.UriSchemePack;
                    _fallback = new ResourceDictionary { Source = DictionaryUri };
                }
                catch { /* 资源不可用时返回键名，避免错误提示自身引发异常。 */ }
                return _fallback;
            }
        }
    }

    public static string Get(string key)
    {
        if (Application.Current?.TryFindResource(key) is string fromApp) return fromApp;
        if (Fallback?[key] is string fromFallback) return fromFallback;
        return key;
    }

    public static string Format(string key, params object?[] args) =>
        string.Format(CultureInfo.CurrentCulture, Get(key), args);

    public static void BindLocalizedItems(
        System.Windows.Controls.ItemsControl picker, System.Collections.IEnumerable items)
    {
        picker.ItemsSource = null;
        picker.ItemsSource = items;
    }
}
