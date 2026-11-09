using System.Runtime.InteropServices;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using OverTranslate.Services.Library;
using OverTranslate.Views.Library;
using Xunit;

namespace OverTranslate.Tests;

public sealed partial class ScreenTranslateShellTests
{
    private static void VerifyLibraryCopyRetries(string directory)
    {
        var store = new LibraryStore(System.IO.Path.Combine(directory, "copy-retry", "library.json"));
        store.SavePrompt(new() { Title = "连续复制验收", Content = "仅用于测试的完整正文\n中文 😀" });
        var attempts = 0;
        var copied = new List<string>();
        var window = new LibraryWindow(store, copy: value =>
        {
            if (++attempts <= 2) throw new COMException("Clipboard is temporarily locked", unchecked((int)0x800401D0));
            copied.Add(value);
        }) { Left = -10000, Top = -10000, ShowActivated = false, WindowStartupLocation = WindowStartupLocation.Manual };
        try
        {
            window.Show(); window.UpdateLayout();
            var button = (System.Windows.Controls.Button)window.FindName("PromptCopyButton");
            button.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
            // 同一条仍在重试时连点，只保留当前复制任务。
            for (var i = 0; i < 12; i++) button.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
            PumpUntil(() => copied.Count == 1);
            PumpUntil(() => !window.IsCopyPending);
            Assert.Equal(3, attempts);
            Assert.Equal("仅用于测试的完整正文\n中文 😀", copied.Single());
            PumpUntil(() => store.Read().Prompts.Single().CopyCount == 1);
            Assert.Equal("已复制", button.Content);
        }
        finally { window.CloseForTests(); }
        VerifyCopyCancellation(directory);
        VerifyCopyWhileStoreIsLocked(directory);
        VerifyCopyHistoryFailure(directory);
        VerifyRealClipboard(directory);
    }

    private static void VerifyCopyCancellation(string directory)
    {
        var store = new LibraryStore(System.IO.Path.Combine(directory, "copy-cancel", "library.json"));
        store.SavePrompt(new() { Title = "A", Content = "fixture-A" });
        store.SavePrompt(new() { Title = "B", Content = "fixture-B" });
        var busy = true;
        var copied = new List<string>();
        var attempts = 0;
        var window = new LibraryWindow(store, copy: value =>
        {
            attempts++;
            if (busy) throw new COMException("busy", unchecked((int)0x800401D0));
            copied.Add(value);
        }) { Left = -10000, Top = -10000, ShowActivated = false, WindowStartupLocation = WindowStartupLocation.Manual };
        try
        {
            window.Show(); window.UpdateLayout();
            var button = (System.Windows.Controls.Button)window.FindName("PromptCopyButton");
            var list = (ListBox)window.FindName("PromptList");
            list.SelectedItem = list.Items.Cast<LibraryPrompt>().Single(p => p.Title == "A");
            window.UpdateLayout();
            button.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
            list.SelectedItem = list.Items.Cast<LibraryPrompt>().Single(p => p.Title == "B");
            window.UpdateLayout();
            busy = false;
            button.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
            PumpUntil(() => !window.IsCopyPending);
            Assert.Equal(["fixture-B"], copied);
            Assert.Contains("B", ((TextBlock)window.FindName("PromptCopyFeedback")).Text);
            Assert.Equal(0, store.Read().Prompts.Single(p => p.Title == "A").CopyCount);
            busy = true;
            button.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
            ((TabControl)window.FindName("Tabs")).SelectedIndex = 1;
            PumpUntil(() => !window.IsCopyPending);
            Assert.Single(copied);
            Assert.False(((System.Windows.Controls.Primitives.Popup)window.FindName("CopyToast")).IsOpen);
            ((TabControl)window.FindName("Tabs")).SelectedIndex = 0;
            var before = attempts;
            button.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
            PumpUntil(() => !window.IsCopyPending);
            Assert.Equal(8, attempts - before);
            Assert.Equal("重试复制", button.Content);
            Assert.Equal(1, store.Read().Prompts.Single(p => p.Title == "B").CopyCount);
            Assert.Single(copied);
        }
        finally { window.CloseForTests(); }
    }

    private static void VerifyCopyWhileStoreIsLocked(string directory)
    {
        var path = System.IO.Path.Combine(directory, "copy-store-lock", "library.json");
        var store = new LibraryStore(path);
        store.SavePrompt(new() { Title = "Slow disk", Content = "fixture-disk-content" });
        var window = new LibraryWindow(store, copy: _ => { })
        { Left = -10000, Top = -10000, ShowActivated = false, WindowStartupLocation = WindowStartupLocation.Manual };
        using var locked = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var name = "Local\\ScreenTranslate.Library." + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(System.IO.Path.GetFullPath(path).ToUpperInvariant())));
        var holder = Task.Run(() =>
        {
            using var mutex = new Mutex(false, name);
            mutex.WaitOne();
            try { locked.Set(); release.Wait(TimeSpan.FromSeconds(4)); }
            finally { mutex.ReleaseMutex(); }
        });
        try
        {
            window.Show(); window.UpdateLayout();
            PumpUntil(() => locked.IsSet);
            var button = (System.Windows.Controls.Button)window.FindName("PromptCopyButton");
            var timer = Stopwatch.StartNew();
            button.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
            Assert.True(timer.ElapsedMilliseconds < 150, "Click must return before the store lock is released.");
            Assert.Equal("已复制", button.Content);
            Assert.True(window.IsCopyPending);
            for (var i = 0; i < 12; i++)
                button.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
            var heartbeat = false;
            window.Dispatcher.BeginInvoke(() => heartbeat = true);
            PumpUntil(() => heartbeat);
            release.Set();
            PumpUntil(() => !window.IsCopyPending);
            Assert.Equal(13, store.Read().Prompts.Single().CopyCount);
        }
        finally { release.Set(); holder.GetAwaiter().GetResult(); window.CloseForTests(); }
    }

    private static void VerifyCopyHistoryFailure(string directory)
    {
        var path = System.IO.Path.Combine(directory, "copy-history-failure", "library.json");
        var store = new LibraryStore(path);
        store.SavePrompt(new() { Title = "History failure", Content = "fixture copied despite failed history" });
        var copied = new List<string>();
        var window = new LibraryWindow(store, copy: copied.Add)
        { Left = -10000, Top = -10000, ShowActivated = false, WindowStartupLocation = WindowStartupLocation.Manual };
        try
        {
            window.Show(); window.UpdateLayout();
            System.IO.File.WriteAllText(path, "malformed fixture");
            ((System.Windows.Controls.Button)window.FindName("PromptCopyButton")).RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
            PumpUntil(() => !window.IsCopyPending);
            Assert.Equal(["fixture copied despite failed history"], copied);
            Assert.Contains("已复制，但未能保存复制次数", ((TextBlock)window.FindName("Status")).Text);
            Assert.Equal("malformed fixture", System.IO.File.ReadAllText(path));
            Assert.Equal(0, ((LibraryPrompt)((ListBox)window.FindName("PromptList")).SelectedItem).CopyCount);
        }
        finally { window.CloseForTests(); }
    }

    private static void VerifyRealClipboard(string directory)
    {
        var store = new LibraryStore(System.IO.Path.Combine(directory, "copy-native", "library.json"));
        const string text = "ScreenTranslate clipboard fixture\n中文 😀\t完整末行";
        store.SavePrompt(new() { Title = "Native clipboard", Content = text });
        var window = new LibraryWindow(store)
        { Left = -10000, Top = -10000, ShowActivated = false, WindowStartupLocation = WindowStartupLocation.Manual };
        try
        {
            window.Show(); window.UpdateLayout();
            var owner = new System.Windows.Interop.WindowInteropHelper(window).Handle;
            var timings = new List<double>();
            for (var i = 0; i < 40; i++)
            {
                var timer = Stopwatch.StartNew();
                var write = LibraryClipboard.WriteAsync(text, value => LibraryClipboard.SetText(value, owner), CancellationToken.None);
                PumpUntil(() => write.IsCompleted);
                write.GetAwaiter().GetResult();
                timings.Add(timer.Elapsed.TotalMilliseconds);
                AssertFixtureClipboard(text);
            }
            using (var clipboardLock = new ExternalClipboardLock())
            {
                var button = (System.Windows.Controls.Button)window.FindName("PromptCopyButton");
                var timer = Stopwatch.StartNew();
                button.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
                Assert.True(timer.ElapsedMilliseconds < 150, "A locked OS clipboard must not block the UI thread.");
                Assert.NotEqual("已复制", button.Content);
                // 由界面线程解除独立进程的占用，验证消息泵仍能执行。
                window.Dispatcher.BeginInvoke(() => clipboardLock.Release());
                PumpUntil(() => !window.IsCopyPending);
                Assert.Equal("已复制", button.Content);
                AssertFixtureClipboard(text);
                Assert.Equal(1, store.Read().Prompts.Single().CopyCount);
            }
            var clickTimings = new List<double>();
            var repeatButton = (System.Windows.Controls.Button)window.FindName("PromptCopyButton");
            for (var i = 0; i < 40; i++)
            {
                var timer = Stopwatch.StartNew();
                repeatButton.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
                clickTimings.Add(timer.Elapsed.TotalMilliseconds);
                PumpUntil(() => !window.IsCopyPending);
                Assert.Equal("已复制", repeatButton.Content);
                AssertFixtureClipboard(text);
            }
            Assert.Equal(41, store.Read().Prompts.Single().CopyCount);
            var output = System.IO.Path.GetFullPath(System.IO.Path.Combine("artifacts", "library-copy-responsive"));
            System.IO.Directory.CreateDirectory(output);
            System.IO.File.WriteAllText(System.IO.Path.Combine(output, "native-metrics.json"), System.Text.Json.JsonSerializer.Serialize(
                new { repeats = timings.Count, averageMs = timings.Average(), maximumMs = timings.Max(), exactTextChecks = 80,
                    repeatedUiClicks = clickTimings.Count, clickAverageMs = clickTimings.Average(), clickMaximumMs = clickTimings.Max(),
                    savedCopyCount = 41, lockedClipboardUiResponsive = true },
                new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
        }
        finally { window.CloseForTests(); }
    }

    private static void AssertFixtureClipboard(string expected)
    {
        string? actual = null;
        PumpUntil(() =>
        {
            if (actual is not null) return true;
            try { actual = System.Windows.Clipboard.GetText(); return true; }
            catch (ExternalException ex) when (ex.ErrorCode == unchecked((int)0x800401D0)) { return false; }
        });
        Assert.Equal(expected, actual);
    }

    private sealed class ExternalClipboardLock : IDisposable
    {
        private readonly EventWaitHandle _ready;
        private readonly EventWaitHandle _release;
        private readonly Process _process;
        public ExternalClipboardLock()
        {
            var id = Guid.NewGuid().ToString("N");
            var readyName = "Local\\ClipboardReady-" + id;
            var releaseName = "Local\\ClipboardRelease-" + id;
            _ready = new EventWaitHandle(false, EventResetMode.ManualReset, readyName);
            _release = new EventWaitHandle(false, EventResetMode.ManualReset, releaseName);
            var script = $$"""
                Add-Type 'using System; using System.Runtime.InteropServices; public class ClipboardFixture { [DllImport("user32.dll")] public static extern bool OpenClipboard(IntPtr owner); [DllImport("user32.dll")] public static extern bool CloseClipboard(); }'
                $ready = [Threading.EventWaitHandle]::OpenExisting('{{readyName}}')
                $release = [Threading.EventWaitHandle]::OpenExisting('{{releaseName}}')
                if (-not [ClipboardFixture]::OpenClipboard([IntPtr]::Zero)) { exit 2 }
                try { $ready.Set() | Out-Null; $release.WaitOne(6000) | Out-Null }
                finally { [ClipboardFixture]::CloseClipboard() | Out-Null; $ready.Dispose(); $release.Dispose() }
                """;
            _process = Process.Start(new ProcessStartInfo("powershell.exe")
            {
                Arguments = "-NoProfile -NonInteractive -EncodedCommand " + Convert.ToBase64String(System.Text.Encoding.Unicode.GetBytes(script)),
                UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden
            })!;
            if (!_ready.WaitOne(TimeSpan.FromSeconds(8)))
            {
                Dispose();
                throw new InvalidOperationException("External clipboard fixture did not acquire the lock.");
            }
        }
        public void Release() => _release.Set();
        public void Dispose()
        {
            _release.Set();
            _process.WaitForExit(8000);
            _process.Dispose(); _ready.Dispose(); _release.Dispose();
        }
    }
}
