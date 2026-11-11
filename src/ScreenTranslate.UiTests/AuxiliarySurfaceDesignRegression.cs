using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shell;
using OverTranslate.Services.Lan;
using OverTranslate.Views.ClipboardHistory;
using Xunit;
using Button = System.Windows.Controls.Button;

namespace OverTranslate.Tests;

public sealed partial class ScreenTranslateShellTests
{
    private static void VerifyAuxiliarySurfaceDesign()
    {
        VerifyAppMessageDialogs();
        var directory = Path.Combine(Path.GetTempPath(), "dialog-design-" + Guid.NewGuid());
        Directory.CreateDirectory(directory);
        var journal = new SyncActivityStore(Path.Combine(directory, "activity.json"));
        journal.Update(new("file", SyncKind.Files, false, "测试电脑", SyncState.Failed,
            Name: new string('长', 600), Sources: [@"C:\fixture\example.txt"]));
        var deviceRows = new List<LanDeviceRow>
        {
            new("fixture", "测试电脑 · " + new string('长', 80), "192.0.2.1:12345", "未连接",
                "剪贴板：开启 · 资料库：关闭", "上次连接失败", true)
        };
        Window[] windows = [
            new FileSendWindow("测试电脑", true, [@"C:\fixture\" + string.Concat(Enumerable.Repeat(@"很长的目录\", 60)) + "example.txt"]),
            new SyncActivityWindow(journal),
            new LanTransferWindow(Path.Combine(directory, "preferences.json"), readAddresses: () => []),
            new DeviceSyncWindow(readRows: () => deviceRows, activities: journal)
        ];
        try
        {
            foreach (var window in windows)
            {
                window.WindowStartupLocation = WindowStartupLocation.Manual;
                window.Left = window.Top = -10000;
                window.ShowActivated = false;
                window.Show(); window.UpdateLayout();
                VerifyWindowChromeInput(window);
                Assert.Equal(WindowStyle.None, window.WindowStyle);
                Assert.Equal(40, WindowChrome.GetWindowChrome(window).CaptionHeight);
                ApplyTheme(System.Windows.Application.Current, "Light");
                var normal = (FrameworkElement)window.Content;
                Render(window, $"dialog-{window.GetType().Name}-normal", (int)Math.Ceiling(normal.ActualWidth), (int)Math.Ceiling(normal.ActualHeight));
                foreach (var theme in new[] { "Light", "Dark" })
                {
                    ApplyTheme(System.Windows.Application.Current, theme);
                    if (window.ResizeMode != ResizeMode.NoResize)
                    { window.Width = window.MinWidth; window.Height = window.MinHeight; }
                    window.UpdateLayout();
                    var root = (FrameworkElement)window.Content;
                    var buttons = window is LanTransferWindow
                        ? Visuals<Button>(Visuals<UserControl>(root).Single(c => c.GetType().Name == "ToolWindowHeader"))
                        : Visuals<Button>(root);
                    foreach (var button in buttons.Where(b => b.IsVisible))
                    {
                        AssertInside(root, button);
                        Assert.NotNull(button.FocusVisualStyle);
                    }
                    Render(window, $"dialog-{window.GetType().Name}-{theme.ToLowerInvariant()}-minimum",
                        (int)Math.Ceiling(root.ActualWidth), (int)Math.Ceiling(root.ActualHeight));
                }
            }
            VerifyAuxiliaryKeyboardAndOverflow(windows);
            var sender = (FileSendWindow)windows[0];
            Assert.True(((Button)sender.FindName("SendButton")).IsDefault);
            Assert.Contains(Visuals<Button>((FrameworkElement)sender.Content), b => b.IsCancel);
            Assert.Same(sender.FindResource("ModernComboBox"), ((ComboBox)sender.FindName("RecipientBox")).Style);
            Assert.True(((ListBox)sender.FindName("SourceList")).ActualHeight >= 60);
            var transfer = (LanTransferWindow)windows[2];
            var sync = (CheckBox)transfer.FindName("AutoSyncBox");
            Assert.Same(transfer.FindResource("ModernCheckBox"), sync.Style);
            Assert.NotNull(sync.FocusVisualStyle);
            sync.IsEnabled = false;
            Assert.Equal(0.45, sync.Opacity);
            sync.IsEnabled = true;
            var history = (SyncActivityWindow)windows[1];
            var list = (ListBox)history.FindName("ActivityList");
            list.SelectedIndex = 0; list.UpdateLayout();
            var item = Assert.IsType<ListBoxItem>(list.ItemContainerGenerator.ContainerFromIndex(0));
            Assert.Same(history.FindResource("ProductListItem"), item.Style);
            Assert.Equal(((SolidColorBrush)history.FindResource("AppListSelectedBg")).Color,
                ((SolidColorBrush)Visuals<Border>(item).First().Background).Color);
            var devices = (DeviceSyncWindow)windows[3];
            var deviceList = (ListBox)devices.FindName("DeviceList");
            deviceList.SelectedIndex = 0; deviceList.UpdateLayout();
            Assert.True(((Button)devices.FindName("SettingsButton")).IsEnabled);
            Assert.Same(devices.FindResource("ProductListItem"), ((ListBoxItem)deviceList.ItemContainerGenerator.ContainerFromIndex(0)).Style);
            deviceRows.Clear();
            journal.Update(new("clipboard", SyncKind.Clipboard, false, "测试电脑", SyncState.Completed));
            devices.UpdateLayout();
            Assert.Equal(Visibility.Visible, ((TextBlock)devices.FindName("EmptyHint")).Visibility);
            Assert.False(((Button)devices.FindName("SettingsButton")).IsEnabled);
            var warning = (TextBlock)devices.FindName("WarningText");
            Assert.Equal(Visibility.Collapsed, warning.Visibility);
            warning.Text = "测试：设备信息读取失败，请检查权限。";
            Assert.Equal(Visibility.Visible, warning.Visibility);
            warning.Text = "";
            var cancelled = false;
            sender.Closing += (_, args) => { args.Cancel = true; cancelled = true; };
            var close = Visuals<Button>((FrameworkElement)sender.Content).Single(b => b.Name == "CloseButton");
            close.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            PumpUntil(() => cancelled);
            Assert.True(sender.IsVisible);
            // 仅本次点击拦截关闭；清理阶段仍要释放测试窗口。
            sender.Closing += (_, args) => args.Cancel = false;
        }
        finally
        {
            foreach (var window in windows)
            {
                if (window is LanTransferWindow transfer)
                {
                    var shutdown = transfer.ShutdownAsync();
                    PumpUntil(() => shutdown.IsCompleted);
                    shutdown.GetAwaiter().GetResult();
                }
                else window.Close();
            }
            ApplyTheme(System.Windows.Application.Current, "Light");
            Directory.Delete(directory, true);
        }
    }

    private static void VerifyAuxiliaryKeyboardAndOverflow(Window[] windows)
    {
        var errors = new List<string>();
        var transfer = (LanTransferWindow)windows[2];
        ((Expander)transfer.FindName("FileTransferSection")).IsExpanded = true;
        ((ListBox)transfer.FindName("TransferLog")).ItemsSource = new[] { string.Concat(Enumerable.Repeat("很长的传输结果与目录\\", 80)) + "最后一行" };
        transfer.UpdateLayout();
        var storePath = Path.Combine(Path.GetTempPath(), "dialog-chrome-" + Guid.NewGuid(), "library.json");
        var library = new OverTranslate.Views.Library.LibraryWindow(new OverTranslate.Services.Library.LibraryStore(storePath), copy: _ => { });
        try
        {
            typeof(OverTranslate.Views.Library.LibraryWindow)
                .GetMethod("SetQuickAccess", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                .Invoke(library, [true]);
            foreach (var window in windows)
                if (WindowChrome.GetWindowChrome(window).CaptionHeight != 40)
                    errors.Add($"{window.GetType().Name}: opening quick-access library must not change another window's draggable caption height.");
        }
        finally
        {
            library.CloseForTests();
            foreach (var window in windows) WindowChrome.GetWindowChrome(window).CaptionHeight = 40;
            var storeDirectory = Path.GetDirectoryName(storePath)!;
            if (Directory.Exists(storeDirectory)) Directory.Delete(storeDirectory, true);
        }
        foreach (var window in windows)
        {
            var root = (FrameworkElement)window.Content;
            var header = Visuals<UserControl>(root).Single(c => c.GetType().Name == "ToolWindowHeader");
            foreach (var button in Visuals<Button>(header).Where(b => b.IsVisible))
                if (button.IsTabStop) errors.Add($"{window.GetType().Name}.{button.Name}: custom caption buttons must preserve the native tab order of the task controls.");
            var list = window switch
            {
                FileSendWindow => (ListBox)window.FindName("SourceList"),
                SyncActivityWindow => (ListBox)window.FindName("ActivityList"),
                DeviceSyncWindow => (ListBox)window.FindName("DeviceList"),
                LanTransferWindow => (ListBox)window.FindName("TransferLog"),
                _ => null
            };
            if (list is null) continue;
            var scroller = Visuals<ScrollViewer>(list).First();
            scroller.ScrollToBottom(); list.UpdateLayout();
            var viewport = Visuals<ScrollContentPresenter>(scroller).First();
            var item = (ListBoxItem)list.ItemContainerGenerator.ContainerFromIndex(list.Items.Count - 1);
            var lastText = Visuals<TextBlock>(item).Last(t => t.Text.Length > 0);
            var bottom = lastText.TranslatePoint(new Point(0, lastText.ActualHeight), viewport).Y;
            if (bottom > viewport.ActualHeight + 1)
                errors.Add($"{window.GetType().Name}.{list.Name}: the end of an oversized item stays clipped after scrolling to the bottom (bottom {bottom:0.##}, viewport {viewport.ActualHeight:0.##}, offset {scroller.VerticalOffset:0.##}, extent {scroller.ExtentHeight:0.##}).");
            if (window is not LanTransferWindow)
                Render(window, $"dialog-overflow-{window.GetType().Name}", (int)Math.Ceiling(root.ActualWidth), (int)Math.Ceiling(root.ActualHeight));
            if (window is FileSendWindow)
            {
                var originalItems = list.ItemsSource;
                try
                {
                    list.ItemsSource = Enumerable.Range(0, 1000).Select(i => $"C:\\fixture\\{i}.txt").ToArray();
                    list.UpdateLayout();
                    var realized = Enumerable.Range(0, list.Items.Count).Count(i => list.ItemContainerGenerator.ContainerFromIndex(i) is not null);
                    if (realized >= 100) errors.Add($"{list.Name}: scrolling must retain virtualization (realized {realized} of {list.Items.Count} rows).");
                }
                finally { list.ItemsSource = originalItems; list.UpdateLayout(); }
            }
        }
        Assert.True(errors.Count == 0, string.Join(Environment.NewLine, errors));
    }
}
