using System.Text.Json;
using System.Windows;
using OverTranslate.Models;
using OverTranslate.Services;
using Xunit;

namespace OverTranslate.Tests;

public class OcrExtractionTests
{
    [Fact]
    public void SaveFailureIsReportedAndKeepsTheLastSettingsFile()
    {
        var directory = Path.Combine(Path.GetTempPath(), "ScreenTranslate-SaveFailure-" + Guid.NewGuid());
        var path = Path.Combine(directory, "settings.json");
        try
        {
            SettingsService.Save(path, new AppSettings { OcrPreserveFormatting = true });
            // 临时写入路径被目录占用，稳定复现写入失败，不操作个人设置。
            Directory.CreateDirectory(path + ".tmp");
            Assert.False(SettingsService.TrySave(path, new AppSettings { OcrPreserveFormatting = false }));
            Assert.True(SettingsService.Parse(File.ReadAllText(path)).OcrPreserveFormatting);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task OldOcrFinishingAfterNewCopyCannotOverwriteIt()
    {
        var delayed = new TaskCompletionSource<List<OcrTextBlock>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var generation = 1;
        string clipboard = "original clipboard";
        var old = OcrExtraction.CopyAsync(_ => delayed.Task, true, () => generation == 1, value => clipboard = value);
        generation = 2;
        Assert.True(await OcrExtraction.CopyAsync(_ => Task.FromResult(new List<OcrTextBlock> { new("new", Rect.Empty) }),
            true, () => generation == 2, value => clipboard = value));
        delayed.SetResult([new("old", Rect.Empty)]);
        Assert.False(await old);
        Assert.Equal("new", clipboard);
    }

    [Fact]
    public void FileSaveAndNewLoadKeepOcrAndFallbackChoices()
    {
        var directory = Path.Combine(Path.GetTempPath(), "ScreenTranslate-Ocr-" + Guid.NewGuid());
        var path = Path.Combine(directory, "settings.json");
        var settings = new AppSettings
        {
            OcrPreserveFormatting = false, AllowFallbackServices = true,
            OcrHotkeyEnabled = false, OcrHotkeyModifiers = 6,
            OcrHotkeyVirtualKey = 0x46, OcrHotkeyDisplay = "Ctrl+Shift+F",
        };
        try
        {
            SettingsService.Save(path, settings);
            var restored = SettingsService.Load(path, Path.Combine(directory, "absent.json"),
                settings.LastRunVersion, _ => throw new InvalidOperationException("Unexpected migration"));
            Assert.False(restored.OcrPreserveFormatting);
            Assert.True(restored.AllowFallbackServices);
            Assert.False(restored.OcrHotkeyEnabled);
            Assert.Equal(6u, restored.OcrHotkeyModifiers);
            Assert.Equal(0x46u, restored.OcrHotkeyVirtualKey);
            Assert.Equal("Ctrl+Shift+F", restored.OcrHotkeyDisplay);
            Assert.False(File.Exists(path + ".tmp"));
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [Fact]
    public void PreservedTextKeepsIndentationListsAndTableCells()
    {
        var blocks = new List<OcrTextBlock>
        {
            new("  标题\n\n    - 第一项\n    - 第二项\n姓名\t数量\n甲\t2", Rect.Empty)
        };
        Assert.Equal(blocks[0].Text, OcrTextFormatter.Format(blocks, true));
    }

    [Fact]
    public void GeometryRestoresRowsColumnsIndentationAndParagraphs()
    {
        var blocks = new List<OcrTextBlock>
        {
            new("数量", new Rect(120, 0, 30, 20)),
            new("姓名", new Rect(0, 0, 40, 20)),
            new("甲", new Rect(0, 25, 20, 20)),
            new("2", new Rect(120, 25, 10, 20)),
            new("缩进段落", new Rect(40, 90, 80, 20)),
        };
        Assert.Equal("姓名\t数量\n甲\t2\n\n    缩进段落", OcrTextFormatter.Format(blocks, true));
    }

    [Theory]
    [InlineData("A   wrapped\nline.\n\n\nNext paragraph.", "A wrapped line.\n\nNext paragraph.")]
    [InlineData("这是排版\n断行。\n\n下一段。", "这是排版断行。\n\n下一段。")]
    [InlineData("- first\n  continuation\n- second", "- first continuation\n- second")]
    [InlineData("姓名\t数量\n甲\t2", "姓名\t数量\n甲\t2")]
    [InlineData("调用\nAPI", "调用 API")]
    [InlineData("API\n调用", "API 调用")]
    [InlineData("Hello\n, world!", "Hello, world!")]
    [InlineData("call(\nvalue)", "call(value)")]
    [InlineData("- parent\n  - child\n- next", "- parent\n  - child\n- next")]
    [InlineData("\t- parent\n\t\t- child", "\t- parent\n\t\t- child")]
    [InlineData("- item\t2\nnext\t3", "- item\t2\nnext\t3")]
    [InlineData("café\n菜单", "café 菜单")]
    public void CleanupKeepsWordsParagraphsListsAndTables(string source, string expected) =>
        Assert.Equal(expected, OcrTextFormatter.Format([new(source, Rect.Empty)], false));

    [Fact]
    public async Task EmptyStaleCancelledAndFailedRecognitionNeverWriteClipboard()
    {
        int writes = 0;
        void Write(string _) => writes++;
        Assert.False(await OcrExtraction.CopyAsync(_ => Task.FromResult(new List<OcrTextBlock>()), true, () => true, Write));
        Assert.False(await OcrExtraction.CopyAsync(_ => Task.FromResult(new List<OcrTextBlock> { new("   ", Rect.Empty) }), true, () => true, Write));
        Assert.False(await OcrExtraction.CopyAsync(_ => Task.FromResult(new List<OcrTextBlock> { new("late", Rect.Empty) }), true, () => false, Write));
        using var cancelled = new CancellationTokenSource();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => OcrExtraction.CopyAsync(_ =>
        {
            cancelled.Cancel();
            return Task.FromResult(new List<OcrTextBlock> { new("late", Rect.Empty) });
        }, true, () => true, Write, cancelled.Token));
        await Assert.ThrowsAsync<InvalidOperationException>(() => OcrExtraction.CopyAsync(_ => throw new InvalidOperationException("failure"), true, () => true, Write));
        Assert.Equal(0, writes);
    }

    [Fact]
    public async Task SuccessfulCopyCompletesOnlyAfterClipboardWrite()
    {
        string? clipboard = null;
        Assert.True(await OcrExtraction.CopyAsync(_ => Task.FromResult(new List<OcrTextBlock> { new("  原文\n第二行", Rect.Empty) }), true, () => true, value => clipboard = value));
        Assert.Equal("  原文\n第二行", clipboard);
        await Assert.ThrowsAsync<InvalidOperationException>(() => OcrExtraction.CopyAsync(_ => Task.FromResult(new List<OcrTextBlock> { new("原文", Rect.Empty) }), true, () => true, _ => throw new InvalidOperationException("clipboard locked")));
    }

    [Fact]
    public void DefaultsAndRestartRoundTripPreserveChoicesAndShortcutConflicts()
    {
        var defaults = SettingsService.Parse("{}");
        Assert.True(defaults.OcrPreserveFormatting);
        Assert.False(defaults.AllowFallbackServices);
        defaults.OcrPreserveFormatting = false;
        defaults.AllowFallbackServices = true;
        defaults.OcrHotkeyModifiers = defaults.HotkeyModifiers;
        defaults.OcrHotkeyVirtualKey = defaults.HotkeyVirtualKey;
        var restored = SettingsService.Parse(JsonSerializer.Serialize(defaults));
        Assert.False(restored.OcrPreserveFormatting);
        Assert.True(restored.AllowFallbackServices);
        Assert.Equal(HotkeyAction.Capture, HotkeyBindings.Resolve(restored).Single(b => b.Action == HotkeyAction.OcrExtract).ShadowedBy);
        Assert.Equal(new[] { HotkeyAction.Capture, HotkeyAction.TranslationWindow, HotkeyAction.RealtimePause,
            HotkeyAction.OcrExtract, HotkeyAction.LibraryWindow, HotkeyAction.ClipboardHistory },
            HotkeyBindings.Resolve(restored).Select(binding => binding.Action));
    }
}
