using System.Diagnostics;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using OverTranslate.Services.Library;
using OverTranslate.Views.Library;
using OverTranslate.Views.Shell;
using OverTranslate.Views.Settings;
using Xunit;

namespace OverTranslate.Tests;

public sealed partial class ScreenTranslateShellTests
{
    private static void ReloadLibrary(LibraryWindow window)
    {
        var reload = window.ReloadViewsAsync();
        PumpUntil(() => reload.IsCompleted);
        reload.GetAwaiter().GetResult();
    }

    private static void VerifyLibraryReadPerformance()
    {
        var directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "library-performance-" + Guid.NewGuid());
        var store = new LibraryStore(System.IO.Path.Combine(directory, "library.json"));
        store.Update(s => s.Prompts = Enumerable.Range(0, 2000).Select(i => new LibraryPrompt
        { Title = "fixture " + i, Content = "body " + i + new string('x', 2048), Tags = ["fixture"] }).ToList());
        var window = new LibraryWindow(store, copy: _ => { })
        { Left = -10000, Top = -10000, ShowActivated = false };
        try
        {
            var search = (TextBox)window.FindName("PromptSearch");
            var gate = typeof(LibraryStore).GetField("_gate", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(store)!;
            using var entered = new ManualResetEventSlim();
            var writer = Task.Run(() => { lock (gate) { entered.Set(); Thread.Sleep(600); } });
            entered.Wait();
            var watch = Stopwatch.StartNew();
            search.Text = "fixture 1234";
            watch.Stop();
            writer.GetAwaiter().GetResult();
            Assert.True(watch.ElapsedMilliseconds < 150, $"资料库搜索等待保存锁，阻塞 UI {watch.ElapsedMilliseconds} ms");
            var list = (ListBox)window.FindName("PromptList");
            PumpUntil(() => list.Items.Count == 1);
            Assert.Equal("fixture 1234", Assert.IsType<LibraryPrompt>(list.Items[0]).Title);
            search.Text = "fixture 123";
            search.Text = "fixture 1999";
            PumpUntil(() => list.Items.Count == 1 && ((LibraryPrompt)list.Items[0]).Title == "fixture 1999");
            System.IO.Directory.CreateDirectory("artifacts");
            System.IO.File.WriteAllText("artifacts/frontend-performance.txt", $"Synthetic prompts=2000; searchWhileStoreLocked={watch.ElapsedMilliseconds}ms\n");

            using var releaseReload = new ManualResetEventSlim();
            entered.Reset();
            writer = Task.Run(() => { lock (gate) { entered.Set(); releaseReload.Wait(TimeSpan.FromSeconds(3)); } });
            entered.Wait();
            try
            {
                watch.Restart();
                var reload = window.ReloadViewsAsync();
                for (var i = 0; i < 20; i++) window.ReloadViewsAsync();
                search.Text = "fixture 1777";
                ((TabControl)window.FindName("Tabs")).SelectedIndex = 1;
                ((TabControl)window.FindName("Tabs")).SelectedIndex = 0;
                ((TabControl)window.FindName("Tabs")).SelectedIndex = 1;
                watch.Stop();
                Assert.True(watch.ElapsedMilliseconds < 150, $"后台刷新或标签保存阻塞 UI {watch.ElapsedMilliseconds} ms");
                releaseReload.Set();
                PumpUntil(() => reload.IsCompleted); reload.GetAwaiter().GetResult();
                Assert.Equal("fixture 1777", Assert.IsType<LibraryPrompt>(Assert.Single(list.Items.Cast<object>())).Title);
            }
            finally { releaseReload.Set(); writer.GetAwaiter().GetResult(); }
            var selected = list.SelectedItem;
            ReloadLibrary(window);
            Assert.Same(selected, list.SelectedItem);
            store.Update(s => s.Prompts.Single(p => p.Title == "fixture 1777").Description = "external fixture change");
            ReloadLibrary(window);
            Assert.Equal("external fixture change", Assert.IsType<LibraryPrompt>(list.SelectedItem).Description);
            window.CloseForTests();
            Assert.Equal(1, new LibraryStore(System.IO.Path.Combine(directory, "library.json")).Read().SelectedTab);
        }
        finally
        {
            window.CloseForTests();
            if (System.IO.Directory.Exists(directory)) System.IO.Directory.Delete(directory, true);
        }
    }

    private static void VerifyLibraryColdOpenPerformance()
    {
        var directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "library-cold-open-" + Guid.NewGuid());
        var store = new LibraryStore(System.IO.Path.Combine(directory, "library.json"));
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var opens = 0;
        LibraryStore CreateStore()
        {
            Interlocked.Increment(ref opens); entered.Set();
            release.Wait(TimeSpan.FromSeconds(3));
            return store;
        }
        var current = typeof(LibraryWindow).GetField("_current", BindingFlags.Static | BindingFlags.NonPublic)!;
        var first = LibraryWindow.OpenAsync(true, CreateStore);
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(2)));
            var watch = Stopwatch.StartNew();
            LibraryWindow.Toggle();
            var latest = LibraryWindow.OpenAsync(true, CreateStore);
            watch.Stop();
            Assert.True(watch.ElapsedMilliseconds < 150, "冷启动重复唤醒不应等待磁盘读取");
            Assert.Equal(1, Volatile.Read(ref opens));
            release.Set();
            PumpUntil(() => first.IsCompleted && latest.IsCompleted);
            first.GetAwaiter().GetResult(); latest.GetAwaiter().GetResult();
            var window = Assert.IsType<LibraryWindow>(current.GetValue(null));
            Assert.True(window.IsVisible);
            LibraryWindow.Shutdown();
            Assert.Null(current.GetValue(null));

            entered.Reset(); release.Reset();
            var cancelled = LibraryWindow.OpenAsync(true, CreateStore);
            Assert.True(entered.Wait(TimeSpan.FromSeconds(2)));
            LibraryWindow.Shutdown(); release.Set();
            PumpUntil(() => cancelled.IsCompleted); cancelled.GetAwaiter().GetResult();
            Assert.Null(current.GetValue(null));
            var failed = LibraryWindow.OpenAsync(true, () => throw new System.IO.IOException("fixture"));
            PumpUntil(() => failed.IsCompleted);
            Assert.Throws<System.IO.IOException>(() => failed.GetAwaiter().GetResult());
            var recovery = LibraryWindow.OpenAsync(true, () => store);
            PumpUntil(() => recovery.IsCompleted); recovery.GetAwaiter().GetResult();
            Assert.True(Assert.IsType<LibraryWindow>(current.GetValue(null)).IsVisible);
            System.IO.File.AppendAllText("artifacts/frontend-performance.txt", $"coldOpenRepeat={watch.ElapsedMilliseconds}ms; sharedColdReads=1; cancelAndRecovery=PASS\n");
        }
        finally
        {
            release.Set(); LibraryWindow.Shutdown();
            PumpUntil(() => first.IsCompleted);
            if (System.IO.Directory.Exists(directory)) System.IO.Directory.Delete(directory, true);
        }
    }

    private static void VerifyShellReadPerformance()
    {
        using var release = new ManualResetEventSlim();
        using var entered = new ManualResetEventSlim();
        var reads = 0;
        var failRead = 0;
        var watch = Stopwatch.StartNew();
        var shell = new ShellWindow(() =>
        {
            Interlocked.Increment(ref reads); entered.Set();
            release.Wait(TimeSpan.FromMilliseconds(600));
            if (Volatile.Read(ref failRead) != 0) throw new InvalidOperationException("fixture writer owns mutex");
            return new LibraryState { Prompts = [new()] };
        }, () => { });
        watch.Stop();
        try
        {
            Assert.True(watch.ElapsedMilliseconds < 500, $"主窗口构造被资料读取阻塞 {watch.ElapsedMilliseconds} ms");
            Assert.True(entered.Wait(TimeSpan.FromSeconds(2)));
            for (var i = 0; i < 20; i++) shell.RefreshLibrarySummary();
            Assert.Equal(1, Volatile.Read(ref reads));
            release.Set();
            var summary = (TextBlock)shell.FindName("LibrarySummaryText");
            PumpUntil(() => summary.Text == "提示词 1 · 连接 0");
            System.IO.File.AppendAllText("artifacts/frontend-performance.txt", $"shellConstructionWithBlockedRead={watch.ElapsedMilliseconds}ms; burstSummaryReads={reads}\n");
            Volatile.Write(ref failRead, 1);
            shell.RefreshLibrarySummary();
            PumpUntil(() => summary.Text == "资料暂不可用，请打开检查");
            Volatile.Write(ref failRead, 0);
            shell.RefreshLibrarySummary();
            PumpUntil(() => summary.Text == "提示词 1 · 连接 0");
        }
        finally { release.Set(); shell.Close(); }
    }

    private static void VerifyAiReadPerformance()
    {
        var directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ai-load-performance-" + Guid.NewGuid());
        var store = new LibraryStore(System.IO.Path.Combine(directory, "library.json"));
        using var release = new ManualResetEventSlim();
        var reads = 0;
        var watch = Stopwatch.StartNew();
        var page = new AiOrganizingSettingsPage(() =>
        {
            Interlocked.Increment(ref reads);
            release.Wait(TimeSpan.FromMilliseconds(600));
            return store;
        });
        watch.Stop();
        try
        {
            Assert.True(watch.ElapsedMilliseconds < 300, $"进入 AI 设置被读盘阻塞 {watch.ElapsedMilliseconds} ms");
            for (var i = 0; i < 20; i++) page.Reload();
            release.Set();
            PumpUntil(() => ((System.Windows.Controls.Button)page.FindName("SaveButton")).IsEnabled);
            Assert.Equal(1, Volatile.Read(ref reads));
            var model = (TextBox)page.FindName("AiModel");
            model.Text = "unsaved fixture";
            page.Reload();
            Assert.Equal("unsaved fixture", model.Text);
            Assert.Equal(1, Volatile.Read(ref reads));
            System.IO.File.AppendAllText("artifacts/frontend-performance.txt", $"aiSettingsConstructionWithBlockedRead={watch.ElapsedMilliseconds}ms; coalescedReads={reads}; unsavedInput=PASS\n");
        }
        finally { release.Set(); if (System.IO.Directory.Exists(directory)) System.IO.Directory.Delete(directory, true); }
    }
}
