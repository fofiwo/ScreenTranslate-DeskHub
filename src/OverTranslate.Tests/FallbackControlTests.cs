using System.Windows;
using OverTranslate.Models;
using OverTranslate.Services;
using OverTranslate.Services.Providers;
using OverTranslate.Translation;
using Xunit;

namespace OverTranslate.Tests;

public class FallbackControlTests
{
    [Fact]
    public void UsageComesFromThisResultAndExcludesUntranslatedLines()
    {
        var result = new[] {
            new TranslatedBlock("a", "译文", Rect.Empty) { ActualService = "实际备用" },
            new TranslatedBlock("b", "b", Rect.Empty) { ActualService = "未翻译", Untranslated = true },
        };
        var usage = TranslationService.DescribeUsage(result, TranslationProvider.Microsoft);
        Assert.Equal("实际备用", usage.Summary);
        Assert.True(usage.FallbackUsed);
        Assert.Equal("未翻译", TranslationService.DescribeUsage(result.Skip(1), TranslationProvider.Microsoft).Summary);
    }

    private sealed class Engine(string name, bool fails) : ITextTranslator
    {
        public string Name => name;
        public int Calls;
        public IReadOnlyList<IReadOnlyList<int>> Plan(IReadOnlyList<string> texts) => [Enumerable.Range(0, texts.Count).ToArray()];
        public Task<IReadOnlyList<TextTranslation>> TranslateAsync(IReadOnlyList<string> texts, string targetLanguage,
            string? sourceLanguage = null, CancellationToken cancellationToken = default)
        {
            Calls++;
            if (fails) throw new InvalidOperationException("selected service failed");
            return Task.FromResult<IReadOnlyList<TextTranslation>>(texts.Select(t => new TextTranslation("译文", "EN")).ToList());
        }
    }

    [Fact]
    public async Task DisabledNeverCallsBackup_EnabledUsesAndNamesExistingBackup()
    {
        var primary = new Engine("primary", true);
        var backup = new Engine("backup", false);
        bool allowed = false;
        var translation = new TranslationService((_, resilient) => resilient
            ? new ResilientProvider([primary, backup], TimeSpan.FromMilliseconds(1), TimeSpan.FromSeconds(1))
            : new EngineProvider(primary), () => allowed);
        List<OcrTextBlock> blocks = [new("original", new Rect(0, 0, 100, 20))];
        await Assert.ThrowsAsync<InvalidOperationException>(() => translation.TranslateAsync(blocks, "EN", "ZH-HANS", "", engine: TranslationProvider.Microsoft));
        Assert.Equal(0, backup.Calls);
        allowed = true;
        var result = await translation.TranslateAsync(blocks, "EN", "ZH-HANS", "", engine: TranslationProvider.Microsoft);
        Assert.Equal("backup", Assert.Single(result.Blocks).ActualService);
        Assert.True(backup.Calls > 0);
        Assert.True(translation.LastEngineUsage!.FallbackUsed);
    }

    [Theory]
    [InlineData(TranslationProvider.OpenAI)]
    [InlineData(TranslationProvider.DeepL)]
    public async Task OpenAiAndDeepLNeverExpandToFallback(TranslationProvider chosen)
    {
        bool? usedFallback = null;
        var service = new TranslationService((_, fallback) => { usedFallback = fallback; return new EngineProvider(new Engine("single", false)); }, () => true);
        await service.TranslateAsync([new("original", Rect.Empty)], "EN", "ZH-HANS", "", engine: chosen);
        Assert.False(usedFallback);
    }
}
