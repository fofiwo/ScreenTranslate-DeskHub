using System.Reflection;
using System.Windows;
using OverTranslate.Services;
using OverTranslate.Services.Realtime;
using Xunit;

namespace OverTranslate.Tests;

public class AdversarialRegressionTests
{
    [Fact]
    public void RepeatedPartialResultsDoNotClearAndRedisplayTheSameFailure()
    {
        using var ocr = new OcrService();
        var session = new RealtimeTranslationSession(ocr, new TranslationService());
        var notices = new List<string?>();
        session.FailureChanged += (_, _) => notices.Add(session.CurrentFailure);
        var pumpType = typeof(RealtimeTranslationSession).GetNestedType("RegionTranslationPump", BindingFlags.NonPublic)!;
        var pump = Activator.CreateInstance(pumpType, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            null, [session, new RealtimeRegion(0, new System.Drawing.Rectangle(0, 0, 200, 100)), "EN", "ZH-HANS", CancellationToken.None], null)!;
        var publish = pumpType.GetMethod("Publish")!;
        var report = pumpType.GetMethod("Report")!;
        List<TranslatedBlock> partial = [new("source", "source", Rect.Empty) { Untranslated = true }];
        const string failure = "1 行翻译失败，将重试。请检查选定的翻译服务。";
        for (int pass = 1; pass <= 2; pass++)
        {
            // 复现 Post 在同一未翻译结果重试时的真实发布／报错顺序。
            publish.Invoke(pump, [(long)pass, partial, null]);
            report.Invoke(pump, [RealtimeFailureStage.Translate, failure]);
        }
        Assert.Equal(new[] { failure }, notices);
        publish.Invoke(pump, [3L, new List<TranslatedBlock> { new("source", "译文", Rect.Empty) }, null]);
        Assert.Null(session.CurrentFailure);
        Assert.Equal(2, notices.Count);
    }
}
