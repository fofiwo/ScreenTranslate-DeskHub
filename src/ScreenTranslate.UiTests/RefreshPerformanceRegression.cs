using System.Collections.Specialized;
using System.Diagnostics;
using System.IO;
using System.Windows.Controls;
using OverTranslate.Models;
using OverTranslate.Services;
using OverTranslate.Services.Library;
using OverTranslate.Views.Library;
using OverTranslate.Views.Realtime;
using Xunit;

namespace OverTranslate.Tests;

public sealed partial class ScreenTranslateShellTests
{
    private static void VerifyRealtimeReloadPerformance()
    {
        var page = new RealtimePage();
        try
        {
            page.Reload();
            var pickers = new[] { "SrcLangBox", "TgtLangBox", "ProviderBox", "ScreenBox" }
                .Select(name => (ComboBox)page.FindName(name)).ToArray();
            var selected = pickers.Select(p => p.SelectedValue).ToArray();
            var changes = 0;
            foreach (var picker in pickers) picker.SelectionChanged += (_, _) => changes++;
            var written = File.GetLastWriteTimeUtc(SettingsService.FilePath);
            var allocated = GC.GetAllocatedBytesForCurrentThread();
            var watch = Stopwatch.StartNew();
            for (var i = 0; i < 10; i++) page.Reload();
            watch.Stop();
            var rewroteSettings = written != File.GetLastWriteTimeUtc(SettingsService.FilePath);
            Directory.CreateDirectory("artifacts");
            File.AppendAllText("artifacts/refresh-performance.txt",
                $"realtimeReloads=10; elapsedMs={watch.Elapsed.TotalMilliseconds:F1}; selectionEvents={changes}; rewroteSettings={rewroteSettings}; allocatedBytes={GC.GetAllocatedBytesForCurrentThread() - allocated}\n");
            Assert.Equal(selected, pickers.Select(p => p.SelectedValue).ToArray());
            Assert.True(changes == 0 && !rewroteSettings,
                $"无设置变化的实时页刷新：选中项事件 {changes} 次，重写设置文件={rewroteSettings}。");

            var settings = SettingsService.Instance.Current;
            var hidden = settings.HiddenProviders;
            try
            {
                settings.HiddenProviders = [settings.Realtime.Provider.ToString()];
                page.Reload();
                Assert.True(Assert.IsType<ProviderItem>(pickers[2].SelectedItem).IsHiddenSelection);
                Assert.Equal(written, File.GetLastWriteTimeUtc(SettingsService.FilePath));
            }
            finally { settings.HiddenProviders = hidden; }

            var previousTarget = settings.Realtime.TargetLanguage;
            try
            {
                var next = LanguageData.TargetLanguages.First(l => l.Code != previousTarget).Code;
                pickers[1].SelectedValue = next;
                Assert.Equal(next, settings.Realtime.TargetLanguage);
                Assert.NotEqual(written, File.GetLastWriteTimeUtc(SettingsService.FilePath));
            }
            finally { pickers[1].SelectedValue = previousTarget; }
        }
        finally { page.Teardown(); }
    }

    private static void VerifyIndependentLibrarySearchPerformance()
    {
        var folder = Path.Combine(Path.GetTempPath(), "library-search-audit-" + Guid.NewGuid());
        var store = new LibraryStore(Path.Combine(folder, "library.json"));
        var body = new string('x', 20000);
        var state = new LibraryState
        {
            Prompts = Enumerable.Range(0, 2000).Select(i => new LibraryPrompt
                { Id = "prompt-" + i, Title = "prompt " + i, Content = body, Tags = ["tag"] }).ToList(),
            Connections = [new() { Id = "model-1", Platform = "fixture-one" }, new() { Id = "model-2", Platform = "fixture-two" }]
        };
        var window = new LibraryWindow(store, copy: _ => { }, initialState: state);
        try
        {
            var prompts = (ListBox)window.FindName("PromptList");
            var models = (ListBox)window.FindName("ModelList");
            var source = models.ItemsSource;
            var promptEvents = 0;
            ((INotifyCollectionChanged)prompts.ItemsSource).CollectionChanged += (_, _) => promptEvents++;
            var allocated = GC.GetAllocatedBytesForCurrentThread();
            var watch = Stopwatch.StartNew();
            ((TextBox)window.FindName("ModelSearch")).Text = "fixture-two";
            watch.Stop();
            var bytes = GC.GetAllocatedBytesForCurrentThread() - allocated;
            Assert.Equal("model-2", Assert.IsType<LibraryConnection>(Assert.Single(models.Items.Cast<object>())).Id);
            File.AppendAllText("artifacts/refresh-performance.txt",
                $"modelSearchWith2000LongPromptsMs={watch.Elapsed.TotalMilliseconds:F1}; unrelatedPromptEvents={promptEvents}; allocatedBytes={bytes}\n");
            Assert.True(promptEvents == 0, $"搜索模型触发了 {promptEvents} 次无关提示词列表变更。");
            var modelSource = models.ItemsSource;
            ((TextBox)window.FindName("PromptSearch")).Text = "prompt 1999";
            Assert.Equal("prompt-1999", Assert.IsType<LibraryPrompt>(Assert.Single(prompts.Items.Cast<object>())).Id);
            Assert.Same(modelSource, models.ItemsSource);
            Assert.NotSame(source, modelSource);
        }
        finally
        {
            window.CloseForTests();
            if (Directory.Exists(folder)) Directory.Delete(folder, true);
        }
    }
}
