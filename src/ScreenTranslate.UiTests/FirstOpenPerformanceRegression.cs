using System.Diagnostics;
using System.Reflection;
using System.Windows;
using System.Windows.Interop;
using OverTranslate.Services;
using OverTranslate.Services.ClipboardHistory;
using OverTranslate.Services.Lan;
using OverTranslate.Services.Library;
using OverTranslate.Views.Capture;
using OverTranslate.Views.ClipboardHistory;
using OverTranslate.Views.Library;
using OverTranslate.Views.Shell;
using Xunit;

namespace OverTranslate.Tests;

public sealed partial class ScreenTranslateShellTests
{
    private static void VerifyFirstOpenPerformance()
    {
        var directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "first-open-" + Guid.NewGuid());
        var library = new LibraryStore(System.IO.Path.Combine(directory, "library.json"));
        var clipboard = new ClipboardHistoryStore(System.IO.Path.Combine(directory, "clipboard"));
        var activity = new SyncActivityStore(System.IO.Path.Combine(directory, "activity.json"));
        var timings = new List<string>();
        var regressions = new List<string>();
        try
        {
            Check("shell", () => new ShellWindow(() => new LibraryState(), () => { }));
            Check("clipboard", () => new ClipboardHistoryWindow(clipboard, _ => Task.CompletedTask,
                settings: new(), saveSettings: () => true, syncActivities: activity));
            Check("library", () => new LibraryWindow(library, copy: _ => { }, initialState: new()));
            Check("capture", () => new ScreenCaptureWindow(new System.Drawing.Bitmap(32, 32), new(0, 0, 32, 32)));
            Check("screenshot", () => new ScreenCaptureWindow(new System.Drawing.Bitmap(32, 32), new(0, 0, 32, 32), snippingTools: true));
            VerifyPreparationLifetime(library, clipboard);
            // 默认工厂也只访问测试输出目录，不使用日用资料或启动剪贴板监听。
            Assert.Equal(System.IO.Path.Combine(AppContext.BaseDirectory, "data"), AppDataPaths.Root);
            var windowsBefore = System.Windows.Application.Current.Windows.Count;
            Complete(ShortcutUiWarmup.RunAsync());
            Assert.Equal(windowsBefore + 2, System.Windows.Application.Current.Windows.Count);
            foreach (var type in new[] { typeof(ClipboardHistoryWindow), typeof(LibraryWindow) })
            {
                var window = Current(type);
                Assert.NotNull(window);
                Assert.False(window.IsVisible);
                Assert.Equal(IntPtr.Zero, new WindowInteropHelper(window).Handle);
                var timer = (System.Windows.Threading.DispatcherTimer)type.GetField(
                    type == typeof(LibraryWindow) ? "_rankTimer" : "_timeTimer", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
                Assert.False(timer.IsEnabled);
            }
            System.IO.Directory.CreateDirectory("artifacts");
            System.IO.File.WriteAllLines($"artifacts/first-open-{Environment.GetEnvironmentVariable("SCREEN_TRANSLATE_FIRST_OPEN_KIND") ?? "all"}.txt", timings);
            Assert.True(regressions.Count == 0, string.Join("\n", regressions));
        }
        finally
        {
            LibraryWindow.Shutdown();
            ClipboardHistoryWindow.Shutdown();
            if (System.IO.Directory.Exists(directory)) System.IO.Directory.Delete(directory, true);
        }

        void Check(string name, Func<Window> create)
        {
            if (Environment.GetEnvironmentVariable("SCREEN_TRANSLATE_FIRST_OPEN_KIND") is { } kind && kind != name) return;
            Window? prepared = null;
            if (Environment.GetEnvironmentVariable("SCREEN_TRANSLATE_FIRST_OPEN_COLD") != "1")
            {
                var prepareWatch = Stopwatch.StartNew();
                switch (name)
                {
                    case "clipboard":
                        Complete(ClipboardHistoryWindow.PrepareForShortcutAsync(Task.FromResult(clipboard)));
                        prepared = Current(typeof(ClipboardHistoryWindow));
                        Complete(ClipboardHistoryWindow.PrepareForShortcutAsync(Task.FromResult(clipboard)));
                        Assert.Same(prepared, Current(typeof(ClipboardHistoryWindow)));
                        break;
                    case "library":
                        Complete(LibraryWindow.PrepareForShortcutAsync(() => library));
                        prepared = Current(typeof(LibraryWindow));
                        Complete(LibraryWindow.PrepareForShortcutAsync(() => library));
                        Assert.Same(prepared, Current(typeof(LibraryWindow)));
                        break;
                    case "shell":
                        // 主界面在生产 OnStartup 中已打开，计时从该初始化完成之后开始。
                        var startupShell = create();
                        try { ShortcutUiWarmup.PrepareLayout(startupShell); }
                        finally { startupShell.Close(); }
                        break;
                    default:
                        var windows = System.Windows.Application.Current.Windows.Count;
                        ShortcutUiWarmup.PrepareCapture();
                        Assert.Equal(windows, System.Windows.Application.Current.Windows.Count);
                        break;
                }
                timings.Add($"{name}: idlePreparation={prepareWatch.Elapsed.TotalMilliseconds:F1}ms");
                if (name is "clipboard" or "library")
                {
                    Assert.NotNull(prepared);
                    Assert.False(prepared.IsVisible);
                    Assert.Equal(IntPtr.Zero, new WindowInteropHelper(prepared).Handle);
                }
            }
            var elapsed = new List<double>();
            for (var i = 0; i < 2; i++)
            {
                var watch = Stopwatch.StartNew();
                var window = i == 0 && prepared is not null ? prepared : create();
                var constructed = watch.Elapsed.TotalMilliseconds;
                try
                {
                    // 使用虚构内容离屏布局，不截取桌面、不激活窗口或注册全局热键。
                    var root = (FrameworkElement)window.Content;
                    root.Measure(new Size(1100, 680));
                    root.Arrange(new Rect(0, 0, 1100, 680));
                    root.UpdateLayout();
                    watch.Stop();
                    elapsed.Add(watch.Elapsed.TotalMilliseconds);
                    timings.Add($"{name}[{i}]: construct={constructed:F1}ms; layoutReady={watch.Elapsed.TotalMilliseconds:F1}ms");
                }
                finally
                {
                    if (window is LibraryWindow libraryWindow)
                    {
                        if (ReferenceEquals(window, Current(typeof(LibraryWindow)))) LibraryWindow.Shutdown();
                        else libraryWindow.CloseForTests();
                    }
                    else if (window is ClipboardHistoryWindow && ReferenceEquals(window, Current(typeof(ClipboardHistoryWindow))))
                        ClipboardHistoryWindow.Shutdown();
                    else window.Close();
                }
            }
            if (elapsed[0] > Math.Max(150, elapsed[1] * 4))
                regressions.Add($"{name}: first={elapsed[0]:F1}ms; repeat={elapsed[1]:F1}ms");
        }
    }

    private static Window? Current(Type type) => (Window?)type.GetField("_current", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null);

    private static void Complete(Task task) { PumpUntil(() => task.IsCompleted); task.GetAwaiter().GetResult(); }

    private static void VerifyPreparationLifetime(LibraryStore library, ClipboardHistoryStore clipboard)
    {
        var delayedClipboard = new TaskCompletionSource<ClipboardHistoryStore>();
        var preparingClipboard = ClipboardHistoryWindow.PrepareForShortcutAsync(delayedClipboard.Task);
        ClipboardHistoryWindow.Shutdown();
        delayedClipboard.SetResult(clipboard);
        Complete(preparingClipboard);
        Assert.Null(Current(typeof(ClipboardHistoryWindow)));

        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var preparingLibrary = LibraryWindow.PrepareForShortcutAsync(() =>
        {
            entered.Set(); release.Wait(TimeSpan.FromSeconds(3)); return library;
        });
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(2)));
            LibraryWindow.Shutdown();
        }
        finally { release.Set(); }
        Complete(preparingLibrary);
        Assert.Null(Current(typeof(LibraryWindow)));

        entered.Reset(); release.Reset();
        var reads = 0;
        preparingLibrary = LibraryWindow.PrepareForShortcutAsync(() =>
        {
            Interlocked.Increment(ref reads); entered.Set(); release.Wait(TimeSpan.FromSeconds(3)); return library;
        });
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(2)));
            var opening = LibraryWindow.OpenAsync(true, () => throw new InvalidOperationException("duplicate read"));
            release.Set();
            Complete(Task.WhenAll(preparingLibrary, opening));
            Assert.Equal(1, reads);
            Assert.True(Current(typeof(LibraryWindow))!.IsVisible);
        }
        finally { release.Set(); LibraryWindow.Shutdown(); }

        var failed = LibraryWindow.PrepareForShortcutAsync(() => throw new System.IO.IOException("fixture"));
        PumpUntil(() => failed.IsCompleted);
        Assert.Throws<System.IO.IOException>(() => failed.GetAwaiter().GetResult());
        Complete(LibraryWindow.PrepareForShortcutAsync(() => library));
        Assert.NotNull(Current(typeof(LibraryWindow)));
        LibraryWindow.Shutdown();
    }
}
