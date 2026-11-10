using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using OverTranslate.Services.ClipboardHistory;
using OverTranslate.Services;
using OverTranslate.Models;
using OverTranslate.Views.ClipboardHistory;
using Xunit;

namespace OverTranslate.Tests;

public sealed partial class ScreenTranslateShellTests
{
    private static void VerifyClipboardClickPaste()
    {
        var folder = Path.Combine(Path.GetTempPath(), "clipboard-interaction-" + Guid.NewGuid().ToString("N"));
        var store = new ClipboardHistoryStore(folder);
        store.Add(new() { Text = "自动回填回归测试", Source = "fixture.exe" });
        var copies = 0;
        var pastes = 0;
        var pasteResult = true;
        var saveResult = true;
        var settings = new AppSettings();
        var settingsPath = Path.Combine(folder, "fixture-settings.json");
        var window = new ClipboardHistoryWindow(store, _ => { copies++; return Task.CompletedTask; },
            () => { pastes++; return Task.FromResult(pasteResult); }, settings: settings,
            saveSettings: () => saveResult && SettingsService.TrySave(settingsPath, settings))
        { Left = -10000, Top = -10000, ShowActivated = false };
        window.SetWindowPinned(true);
        try
        {
            window.ShowPanel(); PumpUntil(() => !window.IsRefreshing); window.UpdateLayout();
            var query = (System.Windows.Controls.TextBox)window.FindName("SearchBox");
            // 完整回归中前序窗口可能改变焦点；发送 Enter 前建立真实键盘操作的前提。
            Keyboard.Focus(query);
            Assert.True(query.IsKeyboardFocusWithin);
            query.RaiseEvent(new System.Windows.Input.KeyEventArgs(Keyboard.PrimaryDevice,
                PresentationSource.FromVisual(window), 0, Key.Enter)
                { RoutedEvent = Keyboard.PreviewKeyDownEvent });
            PumpUntil(() => !window.IsBusy);
            Assert.Equal(1, copies);
            Assert.Equal(1, pastes);
            Assert.False(window.IsVisible);

            // 鼠标点击正文与 Enter 使用同一动作，不需要先右键选择“粘贴”。
            window.ShowPanel(); PumpUntil(() => !window.IsRefreshing); window.UpdateLayout();
            var list = (ListBox)window.FindName("HistoryList");
            var item = (ListBoxItem)list.ItemContainerGenerator.ContainerFromIndex(0);
            var body = Visuals<TextBlock>(item).First(t => t.Text == "自动回填回归测试");
            body.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left)
                { RoutedEvent = Mouse.PreviewMouseUpEvent });
            PumpUntil(() => !window.IsBusy);
            Assert.Equal(2, pastes); Assert.False(window.IsVisible);

            window.ShowPanel(); PumpUntil(() => !window.IsRefreshing);
            var autoPaste = (MenuItem)window.FindName("AutoPasteMenuItem");
            autoPaste.IsChecked = false; autoPaste.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            Assert.False(SettingsService.Parse(File.ReadAllText(settingsPath)).ClipboardAutoPaste);
            var action = window.ActivateSelectedAsync(); PumpUntil(() => action.IsCompleted); action.GetAwaiter().GetResult();
            Assert.Equal(3, copies); Assert.Equal(2, pastes); Assert.False(window.IsVisible);

            window.ShowPanel(); PumpUntil(() => !window.IsRefreshing);
            saveResult = false;
            autoPaste.IsChecked = true; autoPaste.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            Assert.False(settings.ClipboardAutoPaste); Assert.False(autoPaste.IsChecked);
            Assert.Contains("保存失败", ((TextBlock)window.FindName("StatusText")).Text);
            saveResult = true;
            autoPaste.IsChecked = true; autoPaste.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            var recording = (MenuItem)window.FindName("RecordHistoryMenuItem");
            recording.IsChecked = false; recording.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            Assert.False(SettingsService.Parse(File.ReadAllText(settingsPath)).ClipboardHistoryEnabled);
            Assert.NotEmpty(store.Read());
            VerifyPausedClipboardCapture(store, settings);

            pasteResult = false;
            action = window.ActivateSelectedAsync(); PumpUntil(() => action.IsCompleted); action.GetAwaiter().GetResult();
            Assert.True(window.IsVisible); Assert.Equal(3, pastes);
            Assert.Contains("无法自动粘贴", ((TextBlock)window.FindName("StatusText")).Text);
            window.Width = 560; window.Height = 700; window.UpdateLayout();
            Render(window, "clipboard-interaction-light", 560, 700);
            window.Width = 460; window.Height = 400; window.UpdateLayout();
            Render(window, "clipboard-interaction-minimum", 460, 400);
            VerifyClipboardMenuVisual(window, "clipboard-settings-light");
            ApplyTheme(System.Windows.Application.Current, "Dark");
            Render(window, "clipboard-interaction-dark", 460, 400);
            VerifyClipboardMenuVisual(window, "clipboard-settings-dark");
            ApplyTheme(System.Windows.Application.Current, "Light");
        }
        finally { window.Close(); Directory.Delete(folder, true); }
        VerifyClipboardFirstWake(click: false);
        VerifyClipboardFirstWake(click: true);
        VerifyClipboardFirstWake(click: false, cancel: true);
    }

    private static void VerifyClipboardFirstWake(bool click, bool cancel = false)
    {
        var folder = Path.Combine(Path.GetTempPath(), "clipboard-first-wake-" + Guid.NewGuid().ToString("N"));
        var store = new ClipboardHistoryStore(folder);
        store.Add(new() { Text = "首次唤醒测试" });
        var copies = 0;
        var pastes = 0;
        var saved = 0;
        store.Changed += (_, _) => Interlocked.Exchange(ref saved, 1);
        var window = new ClipboardHistoryWindow(store, _ => { copies++; return Task.CompletedTask; },
            () => { pastes++; return Task.FromResult(true); }, settings: new AppSettings(), saveSettings: () => true,
            syncActivities: new OverTranslate.Services.Lan.SyncActivityStore(Path.Combine(folder, "activity.json")))
        { Left = -10000, Top = -10000 };
        // 为真实 Open 路径注入临时存储，避免读取个人剪贴板历史。
        var current = typeof(ClipboardHistoryWindow).GetField("_current", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!;
        var loader = typeof(ClipboardHistoryRuntime).GetField("StoreLoader", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!.GetValue(null)!;
        var pending = typeof(ClipboardHistoryStoreLoader).GetField("_pending", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        var previousStore = pending.GetValue(loader);
        Assert.Null(current.GetValue(null));
        current.SetValue(null, window);
        pending.SetValue(loader, Task.FromResult(store));
        var query = (TextBox)window.FindName("SearchBox");
        var list = (ListBox)window.FindName("HistoryList");
        var shown = false;
        window.ContentRendered += (_, _) =>
        {
            shown = true;
            window.Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Input, () =>
            {
                if (click)
                {
                    list.UpdateLayout();
                    if (list.ItemContainerGenerator.ContainerFromIndex(0) is not ListBoxItem item) return;
                    var body = Visuals<TextBlock>(item).First(t => t.Text == "首次唤醒测试");
                    body.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left)
                        { RoutedEvent = Mouse.PreviewMouseUpEvent });
                }
                else query.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(window), 0, Key.Enter)
                    { RoutedEvent = Keyboard.PreviewKeyDownEvent });
            });
        };
        try
        {
            ClipboardHistoryWindow.Open();
            if (cancel)
            {
                Assert.False(window.IsVisible);
                ClipboardHistoryWindow.Toggle();
                PumpUntil(() => !window.IsRefreshing);
                Assert.False(window.IsVisible);
                Assert.Equal(0, copies);
                ClipboardHistoryWindow.Open();
            }
            PumpUntil(() => shown && !window.IsRefreshing && !window.IsBusy);
            Assert.Equal(1, copies);
            Assert.Equal(1, pastes);
            Assert.False(window.IsVisible);
            PumpUntil(() => Volatile.Read(ref saved) == 1);
        }
        finally
        {
            window.Close();
            pending.SetValue(loader, previousStore); Directory.Delete(folder, true);
        }
    }

    private static void VerifyPausedClipboardCapture(ClipboardHistoryStore store, AppSettings settings)
    {
        var reads = 0;
        using var runtime = new ClipboardHistoryRuntime(System.Windows.Threading.Dispatcher.CurrentDispatcher,
            Task.FromResult(store), capture: () => { reads++; return new(new ClipboardRecord { Text = "录制测试" }, null); },
            sequence: () => 1, listen: false, recordingEnabled: () => settings.ClipboardHistoryEnabled);
        runtime.TrackCapture(); Assert.Equal(0, reads);
        settings.ClipboardHistoryEnabled = true;
        runtime.TrackCapture(); Assert.Equal(1, reads);
        var stop = runtime.StopAndDrainAsync(); PumpUntil(() => stop.IsCompleted); stop.GetAwaiter().GetResult();
        Assert.Contains(store.Read(), r => r.Text == "录制测试");
    }

    private static void VerifyClipboardMenuVisual(ClipboardHistoryWindow window, string name)
    {
        var button = (System.Windows.Controls.Button)window.FindName("MenuButton");
        button.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
        var menu = button.ContextMenu;
        menu.UpdateLayout();
        var settle = System.Diagnostics.Stopwatch.StartNew();
        PumpUntil(() => settle.ElapsedMilliseconds >= 250);
        Assert.True(menu.IsOpen); Assert.True(window.IsVisible);
        Assert.True(menu.ActualWidth >= 292 && menu.ActualWidth < 440);
        var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap((int)menu.ActualWidth, (int)menu.ActualHeight,
            96, 96, System.Windows.Media.PixelFormats.Pbgra32);
        bitmap.Render(menu);
        var directory = Path.Combine(AppContext.BaseDirectory, "ui-evidence"); Directory.CreateDirectory(directory);
        using var output = File.Create(Path.Combine(directory, name + ".png"));
        var png = new System.Windows.Media.Imaging.PngBitmapEncoder();
        png.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap)); png.Save(output);
        menu.IsOpen = false;
    }
}
