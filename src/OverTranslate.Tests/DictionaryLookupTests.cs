using System.Net.Http;
using System.Xml.Linq;
using OverTranslate.Models;
using OverTranslate.Services;
using Xunit;

namespace OverTranslate.Tests;

public class DictionaryLookupTests
{
    [Theory]
    [InlineData(TranslationProvider.Google, "EN-US", "Google:EN-US:False,Microsoft:EN-US:False,Bing:EN-US:False")]
    [InlineData(TranslationProvider.Google2, "EN-US", "Google:EN-US:False,Microsoft:EN-US:False")]
    [InlineData(TranslationProvider.GoogleChrome, "EN-US", "Google:EN-US:False,Microsoft:EN-US:False")]
    [InlineData(TranslationProvider.Microsoft, "EN-US", "Microsoft:EN-US:False,Google:EN-US:False,Bing:EN-US:False")]
    [InlineData(TranslationProvider.Bing, "EN-US", "Bing:EN-US:False,Google:EN-US:False,Microsoft:EN-US:False")]
    [InlineData(TranslationProvider.DeepL, "EN-US", "Google:EN-US:False,Microsoft:EN-US:False")]
    [InlineData(TranslationProvider.Youdao, "EN-US", "Youdao:EN-US:False,Microsoft:EN-US:False,Google:EN-US:False")]
    [InlineData(TranslationProvider.TranSmart, "EN-US", "Youdao:EN-US:False,Microsoft:EN-US:False,Google:EN-US:False")]
    [InlineData(TranslationProvider.Yandex, "EN-US", "Google:EN-US:False,Microsoft:EN-US:False")]
    [InlineData(TranslationProvider.Google, "ZH-HANT", "Google:ZH-HANT:False,Microsoft:ZH-HANS:True,Bing:ZH-HANS:True")]
    [InlineData(TranslationProvider.Google2, "ZH-HANT", "Google:ZH-HANT:False,Microsoft:ZH-HANS:True")]
    [InlineData(TranslationProvider.GoogleChrome, "ZH-HANT", "Google:ZH-HANT:False,Microsoft:ZH-HANS:True")]
    [InlineData(TranslationProvider.Microsoft, "ZH-HANT", "Microsoft:ZH-HANS:True,Google:ZH-HANT:False,Bing:ZH-HANS:True")]
    [InlineData(TranslationProvider.Bing, "ZH-HANT", "Bing:ZH-HANS:True,Google:ZH-HANT:False,Microsoft:ZH-HANS:True")]
    [InlineData(TranslationProvider.DeepL, "ZH-HANT", "Google:ZH-HANT:False,Microsoft:ZH-HANS:True")]
    [InlineData(TranslationProvider.Youdao, "ZH-HANT", "Youdao:ZH-HANT:False,Microsoft:ZH-HANS:True,Google:ZH-HANT:False")]
    [InlineData(TranslationProvider.TranSmart, "ZH-HANT", "Youdao:ZH-HANT:False,Microsoft:ZH-HANS:True,Google:ZH-HANT:False")]
    [InlineData(TranslationProvider.Yandex, "ZH-HANT", "Google:ZH-HANT:False,Microsoft:ZH-HANS:True")]
    [InlineData(TranslationProvider.OpenAI, "ZH-HANT", "")]
    public void Dictionary_fallback_plan_matches_the_selected_provider_and_target(
        TranslationProvider provider, string targetLanguage, string expected)
    {
        var actual = string.Join(",", DictionaryLookupPlan.Build(provider, "EN-US", targetLanguage)
            .Select(step => $"{step.Provider}:{step.TargetLanguage}:{step.ConvertToTraditional}"));

        Assert.Equal(expected, actual);
    }

    [Theory]
    [InlineData(TranslationProvider.Google)]
    [InlineData(TranslationProvider.Google2)]
    [InlineData(TranslationProvider.GoogleChrome)]
    [InlineData(TranslationProvider.Microsoft)]
    [InlineData(TranslationProvider.Bing)]
    [InlineData(TranslationProvider.DeepL)]
    [InlineData(TranslationProvider.Youdao)]
    [InlineData(TranslationProvider.TranSmart)]
    [InlineData(TranslationProvider.Yandex)]
    public void Traditional_Chinese_source_is_simplified_only_for_Microsoft_and_Bing(
        TranslationProvider provider)
    {
        var steps = DictionaryLookupPlan.Build(provider, "ZH-HANT", "EN-US");

        Assert.All(steps, step =>
        {
            var requiresSimplifiedSource =
                step.Provider is TranslationProvider.Microsoft or TranslationProvider.Bing;
            Assert.Equal(requiresSimplifiedSource ? "ZH-HANS" : "ZH-HANT", step.SourceLanguage);
            Assert.Equal(requiresSimplifiedSource, step.ConvertSourceToSimplified);
            Assert.Equal(requiresSimplifiedSource, step.ConvertToTraditional);
        });
    }

    [Fact]
    public void Traditional_Chinese_source_and_target_conversions_are_independent()
    {
        var steps = DictionaryLookupPlan.Build(
            TranslationProvider.Microsoft, "ZH-HANT", "ZH-HANT");

        Assert.True(steps[0].ConvertSourceToSimplified);
        Assert.True(steps[0].ConvertToTraditional);
        Assert.False(steps[1].ConvertSourceToSimplified);
        Assert.False(steps[1].ConvertToTraditional);
    }

    [Fact]
    public void Traditional_dictionary_query_is_converted_with_Taiwan_phrases()
    {
        Assert.Equal("软件", DictionarySimplifiedChineseConverter.Convert("軟體"));
    }

    [Fact]
    public void Simplified_dictionary_results_are_converted_without_exposing_the_conversion()
    {
        var source = new DictionaryLookupData(
            "cost", "Microsoft", "软件", null,
            [new DictionaryLookupGroupData("noun", [
                new DictionaryEntryData("多个翻译", null, ["这个翻译"])
            ])]);

        var result = DictionaryTraditionalChineseConverter.Convert(source);

        Assert.Equal("Microsoft", result.Service);
        Assert.Equal("軟件", result.Headword);
        Assert.Equal("多個翻譯", result.Groups[0].Entries[0].Text);
        Assert.Equal("這個翻譯", result.Groups[0].Entries[0].BackTranslations[0]);
    }

    [Fact]
    public void Simplified_dictionary_results_preserve_the_original_wording()
    {
        var source = new DictionaryLookupData(
            "software", "Microsoft", "软件", null, []);

        var result = DictionaryTraditionalChineseConverter.Convert(source);

        Assert.Equal("軟件", result.Headword);
    }

    [Theory]
    [InlineData("軟體", "软件", true)]
    [InlineData("software", "API headword", false)]
    [InlineData("軟件", "软件", false)]
    public void Dictionary_heading_uses_the_original_input_for_every_language_direction(
        string originalText, string apiHeadword, bool convertToTraditional)
    {
        var source = new DictionaryLookupData(
            "API source", "Microsoft", apiHeadword, null, []);

        var result = TranslationService.PrepareDictionaryResult(
            source, originalText, convertToTraditional);

        Assert.Equal(originalText, result.Headword);
    }

    [Fact]
    public void Dictionary_results_expose_only_groups_with_a_part_of_speech()
    {
        var unlabelled = new DictionaryLookupGroupData(null, [
            new DictionaryEntryData("價錢為", null, [])
        ]);
        var noun = new DictionaryLookupGroupData("noun", [
            new DictionaryEntryData("成本", null, [])
        ]);
        var result = new DictionaryLookupData("cost", "Google Web", "cost", null, [unlabelled, noun]);

        Assert.Equal([noun], result.DisplayGroups);
        Assert.True(result.HasContent);
        Assert.False((result with { Groups = [unlabelled] }).HasContent);
    }

    [Fact]
    public void Dictionary_view_renders_only_concise_provider_details()
    {
        var path = Path.Combine(
            StringsParityTests.ProjectDirectory(),
            "Views", "Controls", "DictionaryResultView.xaml");

        var document = XDocument.Load(path);
        var bindings = document
            .Descendants()
            .Attributes()
            .Select(attribute => attribute.Value)
            .ToList();

        Assert.Contains("{Binding BackTranslationsText}", bindings);
        Assert.DoesNotContain("{Binding Examples}", bindings);
        Assert.DoesNotContain("{Binding DefinitionsText}", bindings);
        Assert.DoesNotContain("{Binding SynonymsText}", bindings);
        Assert.Empty(document.Descendants("{http://schemas.microsoft.com/winfx/2006/xaml/presentation}ProgressBar"));
    }

    [Fact]
    public async Task Dictionary_lookup_falls_back_when_the_selected_provider_rejects_the_language_pair()
    {
        var expected = new DictionaryLookupData(
            "cost", "Google Web", "cost", null,
            [new DictionaryLookupGroupData("noun", [
                new DictionaryEntryData("成本", null, [])
            ])]);
        var attempts = 0;

        var result = await DictionaryLookupFallback.TryAsync([
            _ =>
            {
                attempts++;
                return Task.FromException<DictionaryLookupData?>(
                    new HttpRequestException("The API returned status code 400."));
            },
            _ =>
            {
                attempts++;
                return Task.FromResult<DictionaryLookupData?>(expected);
            }
        ]);

        Assert.Same(expected, result);
        Assert.Equal(2, attempts);
    }

    [Theory]
    [InlineData("charge")]
    [InlineData("credit card")]
    [InlineData("look forward to")]
    [InlineData("state-of-the-art")]
    [InlineData("don't")]
    [InlineData("New　York")]
    [InlineData("銀行")]
    [InlineData("飛ぶ")]
    public void Words_and_short_phrases_are_dictionary_candidates(string text)
    {
        Assert.True(DictionaryLookupEligibility.IsEligible(text));
    }

    [Theory]
    [InlineData("")]
    [InlineData("one two three four five")]
    [InlineData("one two three four five six seven")]
    [InlineData("first line\nsecond line")]
    [InlineData("cost.")]
    [InlineData("hello, world")]
    [InlineData("這是一段沒有空白而且超過十六個中文字的完整句子")]
    [InlineData("This deliberately long input exceeds the dictionary candidate character limit by a lot.")]
    public void Empty_or_sentence_like_text_skips_the_extra_request(string text)
    {
        Assert.False(DictionaryLookupEligibility.IsEligible(text));
    }

    // Google Web, Bing, Microsoft and Youdao are the engines with a dictionary; RPC, Chrome, DeepL,
    // OpenAI, TranSmart and Yandex have none, so whichever of those is chosen, the plan must send the lookup elsewhere.
    [Theory]
    [InlineData("EN-US")]
    [InlineData("ZH-HANT")]
    public void Lookups_only_go_to_engines_that_have_a_dictionary(string target)
    {
        TranslationProvider[] withDictionary = [TranslationProvider.Google, TranslationProvider.Bing, TranslationProvider.Microsoft, TranslationProvider.Youdao];

        foreach (var chosen in Enum.GetValues<TranslationProvider>())
            Assert.All(DictionaryLookupPlan.Build(chosen, "EN", target),
                step => Assert.Contains(step.Provider, withDictionary));
    }

}
