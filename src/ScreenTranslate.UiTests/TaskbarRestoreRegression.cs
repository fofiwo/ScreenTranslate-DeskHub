using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Threading;
using OverTranslate.Models;
using OverTranslate.Services;
using OverTranslate.Services.Library;
using OverTranslate.Views.Shell;
using Xunit;

namespace OverTranslate.Tests;

public sealed partial class ScreenTranslateShellTests
{
    private static void VerifyTaskbarRestorePerformance()
    {
        var settings = SettingsService.Instance.Current;
        var original = (settings.TargetLanguage, settings.Provider, settings.HiddenProviders);
        var shell = new ShellWindow(() => new LibraryState(), () => { })
        { Left = 80, Top = 80, WindowStartupLocation = WindowStartupLocation.Manual };
        try
        {
            shell.Navigate(ShellPage.Home);
            shell.Show(); shell.Activate();
            PumpUntil(() => shell.IsActive);
            shell.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
            var home = Assert.IsType<HomePage>(((Border)shell.FindName("ContentHost")).Child);
            var pickers = new[] { "SourceBox", "TargetBox", "ProviderBox" }
                .Select(name => (ComboBox)home.FindName(name)).ToArray();
            var selections = pickers.Select(p => p.SelectedValue).ToArray();
            var resets = 0;
            foreach (var picker in pickers) picker.SelectionChanged += (_, _) => resets++;
            var handle = new WindowInteropHelper(shell).Handle;
            var timings = new List<double>();
            var allocated = GC.GetAllocatedBytesForCurrentThread();
            for (var i = 0; i < 5; i++)
            {
                // 与任务栏一致的原生系统命令，覆盖 WPF 的激活和布局处理。
                ChromeSendMessage(handle, 0x0112, new IntPtr(0xF020), IntPtr.Zero);
                Assert.True(shell.WindowState == WindowState.Minimized,
                    $"Native minimize state={shell.WindowState}; active={shell.IsActive}");
                shell.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
                var watch = Stopwatch.StartNew();
                ChromeSendMessage(handle, 0x0112, new IntPtr(0xF120), IntPtr.Zero);
                shell.Activate();
                PumpUntil(() => shell.WindowState == WindowState.Normal && shell.IsActive);
                shell.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
                watch.Stop(); timings.Add(watch.Elapsed.TotalMilliseconds);
                Assert.Same(shell, ShellWindow.Current);
                Assert.Equal(handle, new WindowInteropHelper(shell).Handle);
                Assert.Equal(selections, pickers.Select(p => p.SelectedValue).ToArray());
            }
            System.IO.Directory.CreateDirectory("artifacts");
            System.IO.File.AppendAllText("artifacts/taskbar-performance.txt",
                $"restoreMs=[{string.Join(",", timings.Select(ms => ms.ToString("F1")))}]; pickerSelectionEvents={resets}; allocatedBytes={GC.GetAllocatedBytesForCurrentThread() - allocated}\n");
            Assert.True(resets == 0, $"设置未变化时，5 次任务栏还原导致 {resets} 次下拉框选中项清空/重设，触发多余布局。");

            // 保留容器不能导致外部设置变化失效，尤其是隐藏但仍选中的服务。
            var targets = pickers[1].ItemsSource;
            settings.TargetLanguage = LanguageData.TargetLanguages.First(l => l.Code != original.TargetLanguage).Code;
            settings.HiddenProviders = [settings.Provider.ToString()];
            shell.RefreshHotkeyHints();
            Assert.Equal(settings.TargetLanguage, pickers[1].SelectedValue);
            Assert.Same(targets, pickers[1].ItemsSource);
            Assert.True(Assert.IsType<ProviderItem>(pickers[2].SelectedItem).IsHiddenSelection);
            var hidden = settings.Provider;
            settings.Provider = LanguageData.Providers.First(p => p.Provider != hidden).Provider;
            shell.RefreshHotkeyHints();
            Assert.Equal(settings.Provider, pickers[2].SelectedValue);
            Assert.DoesNotContain(pickers[2].Items.OfType<ProviderItem>(), p => p.Provider == hidden);

            shell.WindowState = WindowState.Maximized;
            ChromeSendMessage(handle, 0x0112, new IntPtr(0xF020), IntPtr.Zero);
            Assert.Equal(WindowState.Minimized, shell.WindowState);
            ChromeSendMessage(handle, 0x0112, new IntPtr(0xF120), IntPtr.Zero);
            Assert.Equal(WindowState.Maximized, shell.WindowState);
            AssertNoNativeCaption(shell);
            shell.Close();
            Assert.Null(ShellWindow.Current);
        }
        finally
        {
            (settings.TargetLanguage, settings.Provider, settings.HiddenProviders) = original;
            shell.Close();
        }
    }
}
