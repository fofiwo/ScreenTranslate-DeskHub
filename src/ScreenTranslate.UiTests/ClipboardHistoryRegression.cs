using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using OverTranslate.Services;
using OverTranslate.Services.ClipboardHistory;
using OverTranslate.Views.ClipboardHistory;
using Windows.ApplicationModel.DataTransfer;
using Xunit;
using Button = System.Windows.Controls.Button;

namespace OverTranslate.Tests;

public sealed partial class ScreenTranslateShellTests
{
    [Fact]
    public void FileCardsDistinguishFoldersFilesAndText()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            var folder = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "file-card-" + Guid.NewGuid());
            System.IO.Directory.CreateDirectory(folder);
            try
            {
                var store = new ClipboardHistoryStore(folder);
                var file = System.IO.Path.Combine(folder, "sample.txt");
                System.IO.File.WriteAllText(file, "fixture");
                foreach (var (path, kind) in new[] { (folder, "文件夹"), (file, "TXT 文件"), (file + ".missing", "文件 / 文件夹 · 路径不可用") })
                {
                    var view = new ClipboardRecordView(new ClipboardRecord { Files = [path] }, store);
                    _ = view.Thumbnail; PumpUntil(() => !view.IsThumbnailLoading);
                    Assert.True(view.HasFiles); Assert.Equal(kind, view.FileKind);
                    Assert.Equal(path, view.FilePaths);
                    if (path == folder) Assert.Equal("\uE8B7", view.FileGlyph);
                }
                Assert.False(new ClipboardRecordView(new ClipboardRecord { Text = "text" }, store).HasFiles);
            }
            catch (Exception ex) { failure = ex; }
            finally { System.IO.Directory.Delete(folder, true); }
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); thread.Join();
        if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }

    private sealed class FixtureClipboardHistory : ISystemClipboardHistory
    {
        public Func<CancellationToken, Task<SystemClipboardSnapshot>> Read = _ =>
            Task.FromResult(new SystemClipboardSnapshot(ClipboardHistoryItemsResultStatus.Success, []));
        public SetHistoryItemAsContentStatus CopyStatus = SetHistoryItemAsContentStatus.Success;
        public SystemClipboardEntry? Copied;
        public Task<SystemClipboardSnapshot> ReadAsync(CancellationToken token) => Read(token);
        public SetHistoryItemAsContentStatus Restore(SystemClipboardEntry entry) { Copied = entry; return CopyStatus; }
    }

    private static void VerifyClipboardWindowPinToggle()
    {
        var folder = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "clipboard-pin-" + Guid.NewGuid().ToString("N"));
        var window = new ClipboardHistoryWindow(new ClipboardHistoryStore(folder), _ => Task.CompletedTask)
        { Left = -10000, Top = -10000, ShowActivated = false };
        try
        {
            window.Show(); window.UpdateLayout();
            VerifyWindowPinToggle(window);
        }
        finally
        {
            window.Close();
            if (System.IO.Directory.Exists(folder)) System.IO.Directory.Delete(folder, true);
        }
    }

    private static void VerifyWindowPinToggle(ClipboardHistoryWindow window)
    {
        var button = (Button)window.FindName("WindowPinButton");
        var unpinnedIcon = button.Content;
        for (var i = 0; i < 3; i++)
        {
            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.True(window.WindowPinned);
            Assert.True(window.Topmost);
            Assert.Equal("取消固定窗口：点击外部自动收起", button.ToolTip);
            Assert.Equal("取消固定窗口", System.Windows.Automation.AutomationProperties.GetName(button));
            var pinnedIcon = button.Content;
            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.False(window.WindowPinned);
            Assert.False(window.Topmost);
            Assert.Equal("固定窗口：失焦后保持显示", button.ToolTip);
            Assert.Equal("固定窗口", System.Windows.Automation.AutomationProperties.GetName(button));
            Assert.True(window.IsVisible);
            Assert.Equal(unpinnedIcon, button.Content);
            Assert.NotEqual(unpinnedIcon, pinnedIcon);
        }
        var other = new Window { Width = 160, Height = 80, Left = -10000, Top = -10000,
            WindowStartupLocation = WindowStartupLocation.Manual, ShowInTaskbar = false };
        try
        {
            window.Activate(); PumpUntil(() => window.IsActive);
            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            other.Show(); other.Activate(); PumpUntil(() => !window.IsActive);
            System.Windows.Threading.Dispatcher.CurrentDispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            Assert.True(window.IsVisible);
            window.Activate(); PumpUntil(() => window.IsActive);
            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.False(window.WindowPinned);
            other.Activate(); PumpUntil(() => !window.IsVisible);
        }
        finally { other.Close(); }
        window.Show(); window.UpdateLayout();
    }

    private static void VerifySystemClipboardHistory()
    {
        VerifyClipboardPerformance();
        VerifyClipboardRecopyPosition();
        var folder = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "clipboard-popup-" + Guid.NewGuid().ToString("N"));
        var store = new ClipboardHistoryStore(folder);
        VerifyClipboardCaptureDrain(store);
        var copied = new List<ClipboardRecord>();
        var failCopy = false;
        var pasteCalls = 0;
        var pasteResult = false;
        var importReads = 0;
        var fake = new FixtureClipboardHistory();
        fake.Read = _ => { importReads++; return Task.FromResult(new SystemClipboardSnapshot(ClipboardHistoryItemsResultStatus.Success, [])); };
        var window = new ClipboardHistoryWindow(store, record =>
        {
            if (failCopy) throw new InvalidOperationException("fixture clipboard busy");
            copied.Add(record); return Task.CompletedTask;
        }, () => { pasteCalls++; return Task.FromResult(pasteResult); }, fake)
        { Left = -10000, Top = -10000, ShowActivated = false };
        System.Windows.Input.InputMethod.SetIsInputMethodEnabled(window, false);
        try
        {
            window.Show(); window.UpdateLayout();
            VerifyWindowPinToggle(window);
            var list = (ListBox)window.FindName("HistoryList");
            var status = (TextBlock)window.FindName("StatusText");
            var query = (System.Windows.Controls.TextBox)window.FindName("SearchBox");
            var filter = (ListBox)window.FindName("TypeFilter");
            Assert.Empty(list.Items); Assert.Equal(0, importReads);
            Assert.Equal(7, filter.Items.Count);
            var bitmap = BitmapSource.Create(1, 1, 96, 96, PixelFormats.Bgra32, null, new byte[] { 0, 0, 255, 255 }, 4);
            bitmap.Freeze();
            byte[] png;
            using (var stream = new System.IO.MemoryStream())
            {
                var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); encoder.Save(stream); png = stream.ToArray();
            }
            Assert.True(ClipboardContent.DecodeImage(png, true).IsFrozen);
            var imageFile = System.IO.Path.Combine(folder, "thumbnail.png");
            System.IO.File.WriteAllBytes(imageFile, png);
            var fileView = new ClipboardRecordView(new ClipboardRecord { Files = [imageFile] }, store);
            _ = fileView.Thumbnail; PumpUntil(() => !fileView.IsThumbnailLoading); Assert.NotNull(fileView.Thumbnail);
            Assert.NotNull(fileView.LoadImage(false));
            Assert.Equal("PNG 文件", fileView.FileKind);
            var folderView = new ClipboardRecordView(new ClipboardRecord { Files = [folder] }, store);
            _ = folderView.Thumbnail; PumpUntil(() => !folderView.IsThumbnailLoading);
            Assert.True(folderView.HasFiles);
            Assert.Equal("文件夹", folderView.FileKind);
            Assert.Equal("\uE8B7", folderView.FileGlyph);
            Assert.Null(folderView.Thumbnail);
            Assert.False(new ClipboardRecordView(new ClipboardRecord { Text = "普通文本" }, store).HasFiles);
            using (var unlocked = new System.IO.FileStream(imageFile, System.IO.FileMode.Open, System.IO.FileAccess.ReadWrite, System.IO.FileShare.None))
                Assert.True(unlocked.Length > 0);
            var missingView = new ClipboardRecordView(new ClipboardRecord { Files = [imageFile + ".png"] }, store);
            _ = missingView.Thumbnail; PumpUntil(() => !missingView.IsThumbnailLoading); Assert.Null(missingView.Thumbnail);
            Assert.Contains("thumbnail.png.png", missingView.Preview);
            Assert.Contains("路径不可用", missingView.FileKind);
            using (var stream = new System.IO.MemoryStream())
            {
                var encoder = new TiffBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); encoder.Frames.Add(BitmapFrame.Create(bitmap)); encoder.Save(stream);
                Assert.ThrowsAny<Exception>(() => ClipboardContent.DecodeImage(stream.ToArray(), false));
            }
            Assert.ThrowsAny<Exception>(() => ClipboardContent.DecodeImage([1, 2, 3], false));
            var text = store.Add(new() { Text = "这是用于界面验证的剪贴板记录。\n第二行完整内容", Source = "fixture.exe", CopiedAt = DateTimeOffset.UtcNow.AddHours(-2) });
            var picture = store.Add(new(), png);
            var preparedImage = ClipboardContent.BuildDataAsync(picture, store);
            PumpUntil(() => preparedImage.IsCompleted);
            Assert.True(preparedImage.GetAwaiter().GetResult().GetImage().IsFrozen);
            var link = store.Add(new() { Text = "https://example.com/clipboard", Source = "browser.exe" });
            store.Add(new() { Text = "const value = 42;\nconsole.log(value);" });
            store.Add(new() { Text = "保留格式的文字", Html = "<b>保留格式的文字</b>" });
            var file = store.Add(new() { Files = [@"C:\fixture\report.xlsx"] });
            window.RefreshViews(); PumpUntil(() => !window.IsRefreshing);
            Assert.Equal(6, list.Items.Count);
            query.Text = "第二行"; PumpUntil(() => !window.IsRefreshing); Assert.Single(list.Items); Assert.Equal(text.Id, window.SelectedRecord!.Id);
            query.Text = "not found"; PumpUntil(() => !window.IsRefreshing); Assert.Empty(list.Items);
            query.Clear(); filter.SelectedIndex = 2; PumpUntil(() => !window.IsRefreshing); Assert.Single(list.Items); Assert.Equal(picture.Id, window.SelectedRecord!.Id);
            window.UpdateLayout();
            PumpUntil(() => Visuals<Image>(list).Any(i => i.Source is not null));
            var thumbnail = Visuals<Image>(list).First(i => i.Source is not null);
            Assert.Equal(65, thumbnail.MaxHeight);
            var imageTip = Assert.IsType<System.Windows.Controls.ToolTip>(thumbnail.ToolTip);
            imageTip.PlacementTarget = thumbnail;
            imageTip.IsOpen = true;
            PumpUntil(() => ((Image)imageTip.Content).Source is not null);
            Assert.NotNull(((Image)imageTip.Content).Source);
            imageTip.IsOpen = false;
            PumpUntil(() => ((Image)imageTip.Content).Source is null);
            Assert.Empty(copied);
            Assert.True(window.IsVisible);
            window.ShowPreview();
            PumpUntil(() => ((Image)window.FindName("PreviewImage")).Source is not null);
            Assert.NotNull(((Image)window.FindName("PreviewImage")).Source);
            ((Border)window.FindName("PreviewPanel")).Visibility = Visibility.Collapsed;
            filter.SelectedIndex = 3; PumpUntil(() => !window.IsRefreshing); Assert.Single(list.Items); Assert.Equal(link.Id, window.SelectedRecord!.Id);
            filter.SelectedIndex = 4; PumpUntil(() => !window.IsRefreshing); Assert.Single(list.Items); Assert.Equal(file.Id, window.SelectedRecord!.Id);
            Assert.Throws<System.IO.IOException>(() => ClipboardContent.BuildData(file, store));
            filter.SelectedIndex = 0; PumpUntil(() => !window.IsRefreshing); query.Text = "第二行"; PumpUntil(() => !window.IsRefreshing);
            var pin = window.TogglePinAsync(); PumpUntil(() => pin.IsCompleted); pin.GetAwaiter().GetResult();
            Assert.True(new ClipboardHistoryStore(folder).Read().Single(i => i.Id == text.Id).IsPinned);
            Assert.Empty(copied); Assert.True(window.IsVisible);
            query.Clear(); window.RefreshViews(); PumpUntil(() => !window.IsRefreshing); Assert.Equal(text.Id, ((ClipboardRecordView)list.Items[0]).Record.Id);
            window.UpdateLayout();
            var pinControl = Visuals<Button>(list).First(b => Equals(b.Tag, "pin"));
            pinControl.RaiseEvent(new System.Windows.Input.KeyEventArgs(System.Windows.Input.Keyboard.PrimaryDevice,
                System.Windows.PresentationSource.FromVisual(window), 0, System.Windows.Input.Key.Enter)
                { RoutedEvent = System.Windows.Input.Keyboard.PreviewKeyDownEvent });
            Assert.Empty(copied); Assert.True(window.IsVisible);
            var selectedBeforeNavigation = list.SelectedIndex;
            query.RaiseEvent(new System.Windows.Input.KeyEventArgs(System.Windows.Input.Keyboard.PrimaryDevice,
                System.Windows.PresentationSource.FromVisual(window), 0, System.Windows.Input.Key.Down)
                { RoutedEvent = System.Windows.Input.Keyboard.PreviewKeyDownEvent });
            Assert.Equal(selectedBeforeNavigation + 1, list.SelectedIndex); Assert.Empty(copied);
            query.RaiseEvent(new System.Windows.Input.KeyEventArgs(System.Windows.Input.Keyboard.PrimaryDevice,
                System.Windows.PresentationSource.FromVisual(window), 0, System.Windows.Input.Key.Up)
                { RoutedEvent = System.Windows.Input.Keyboard.PreviewKeyDownEvent });
            Assert.Equal(selectedBeforeNavigation, list.SelectedIndex);
            Render(window, "clipboard-popup-default", 560, 700);
            window.Width = 460; window.Height = 400; window.UpdateLayout();
            AssertInside(window, query); AssertInside(window, list); AssertInside(window, (Button)window.FindName("WindowPinButton"));
            Render(window, "clipboard-popup-minimum", 460, 400);
            window.Width = 560; window.Height = 700; window.UpdateLayout();
            query.Text = "第二行"; PumpUntil(() => !window.IsRefreshing);
            window.SetWindowPinned(true); Assert.True(window.Topmost);
            var copy = window.CopySelectedAsync(); PumpUntil(() => copy.IsCompleted); copy.GetAwaiter().GetResult();
            Assert.Single(copied); Assert.False(window.IsVisible); Assert.Equal(0, pasteCalls);
            Assert.True(store.Read().Single(r => r.Id == text.Id).CopiedAt > text.CopiedAt);
            window.Show(); window.RefreshViews(); PumpUntil(() => !window.IsRefreshing);
            failCopy = true;
            copy = window.CopySelectedAsync(); PumpUntil(() => copy.IsCompleted);
            Assert.True(window.IsVisible); Assert.Contains("失败", status.Text); Assert.Single(copied);
            var paste = window.PasteSelectedAsync(); PumpUntil(() => paste.IsCompleted); Assert.Equal(0, pasteCalls);
            failCopy = false;
            paste = window.PasteSelectedAsync(); PumpUntil(() => paste.IsCompleted);
            Assert.Equal(1, pasteCalls); Assert.True(window.IsVisible); Assert.Contains("无法自动粘贴", status.Text);
            pasteResult = true;
            paste = window.PasteSelectedAsync(); PumpUntil(() => paste.IsCompleted);
            Assert.Equal(2, pasteCalls); Assert.False(window.IsVisible);
            window.Show(); window.RefreshViews(); PumpUntil(() => !window.IsRefreshing);
            var delete = window.DeleteSelectedAsync(); PumpUntil(() => delete.IsCompleted); delete.GetAwaiter().GetResult();
            Assert.DoesNotContain(new ClipboardHistoryStore(folder).Read(), i => i.Id == text.Id);
            Assert.True(window.IsVisible);
            var imported = new SystemClipboardEntry("system-fixture", DateTimeOffset.Now.AddDays(-1), "imported fixture", null, false, false);
            fake.Read = _ => { importReads++; return Task.FromResult(new SystemClipboardSnapshot(ClipboardHistoryItemsResultStatus.Success, [imported])); };
            var import = window.ImportSystemHistoryAsync(); PumpUntil(() => import.IsCompleted); import.GetAwaiter().GetResult();
            Assert.Equal(1, importReads); Assert.Contains(store.Read(), i => i.Text == "imported fixture");
            Assert.Contains("Windows 原记录未改动", status.Text);
            fake.Read = _ => Task.FromResult(new SystemClipboardSnapshot(ClipboardHistoryItemsResultStatus.ClipboardHistoryDisabled, []));
            import = window.ImportSystemHistoryAsync(); PumpUntil(() => import.IsCompleted);
            Assert.Contains("独立历史仍会记录", status.Text); Assert.NotEmpty(store.Read());
            var bulkPinned = store.Add(new() { Text = "bulk pinned" });
            store.SetPinned(bulkPinned.Id, true);
            store.Add(new() { Text = "bulk hidden" });
            query.Text = "no matching bulk records";
            ((Button)window.FindName("ClearUnpinnedButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            PumpUntil(() => !window.IsBusy);
            Assert.All(store.Read(), r => Assert.True(r.IsPinned));
            Assert.Contains(store.Read(), r => r.Id == bulkPinned.Id);
            Assert.Contains("置顶内容已保留", status.Text);
            query.Clear();
            // 原生消息只发送给 fixture HWND，不向外部应用注入按键或写系统剪贴板。
            window.SetWindowPinned(false);
            using (var hotkey = new GlobalHotkey(GlobalHotkey.ClipboardHistoryId))
            {
                var hwnd = new System.Windows.Interop.WindowInteropHelper(window).Handle;
                hotkey.Register(hwnd, 7, 0x87);
                hotkey.HotkeyPressed += (_, _) => window.ToggleVisibility();
                SendMessage(hwnd, 0x0312, (IntPtr)GlobalHotkey.ClipboardHistoryId, IntPtr.Zero);
                Assert.False(window.IsVisible);
                SendMessage(hwnd, 0x0312, (IntPtr)GlobalHotkey.ClipboardHistoryId, IntPtr.Zero);
                Assert.True(window.IsVisible); Assert.Empty(query.Text);
            }
            var onDeactivated = typeof(Window).GetMethod("OnDeactivated", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
            window.SetWindowPinned(true);
            onDeactivated.Invoke(window, [EventArgs.Empty]);
            System.Windows.Threading.Dispatcher.CurrentDispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            Assert.True(window.IsVisible);
            window.SetWindowPinned(false);
            // Escape 为真实路由事件；不发送桌面物理键。
            window.RaiseEvent(new System.Windows.Input.KeyEventArgs(System.Windows.Input.Keyboard.PrimaryDevice,
                System.Windows.PresentationSource.FromVisual(window), 0, System.Windows.Input.Key.Escape)
                { RoutedEvent = System.Windows.Input.Keyboard.PreviewKeyDownEvent });
            Assert.False(window.IsVisible);
            window.Show(); window.UpdateLayout();
            window.Close(); Assert.Empty(list.Items);
            var stillSaved = new ClipboardHistoryStore(folder).Read(); Assert.NotEmpty(stillSaved);
            var data = new System.Windows.DataObject(); data.SetText("received content");
            data.SetData(OverTranslate.Services.Lan.LanClipboardSync.OriginFormat, "received");
            var received = ClipboardContent.Capture(data, "");
            Assert.NotNull(received); Assert.Equal("局域网", received.Record.Source);
            var enriched = new System.Windows.DataObject(); enriched.SetText("formatted fixture");
            enriched.SetData(System.Windows.DataFormats.Html, "<b>formatted fixture</b>");
            enriched.SetData(System.Windows.DataFormats.Rtf, @"{\rtf1 formatted fixture}");
            var capturedRich = ClipboardContent.Capture(enriched, "fixture.exe")!;
            var richRecord = store.Add(capturedRich.Record, capturedRich.Png);
            var restoredRich = ClipboardContent.BuildData(richRecord, store);
            Assert.Equal(capturedRich.Record.Html, restoredRich.GetData(System.Windows.DataFormats.Html));
            Assert.Equal(capturedRich.Record.Rtf, restoredRich.GetData(System.Windows.DataFormats.Rtf));
            Assert.Null(ClipboardContent.Capture(restoredRich, ""));
            data.SetData(ClipboardContent.HistoryOrigin, "fixture"); Assert.Null(ClipboardContent.Capture(data, ""));
            var excluded = new System.Windows.DataObject(); excluded.SetText("excluded fixture");
            excluded.SetData("ExcludeClipboardContentFromMonitorProcessing", 1); Assert.Null(ClipboardContent.Capture(excluded, ""));
            var privateData = new System.Windows.DataObject(); privateData.SetText("private fixture");
            privateData.SetData("CanIncludeInClipboardHistory", 0); Assert.Null(ClipboardContent.Capture(privateData, ""));
            using var privateStream = new System.IO.MemoryStream(new byte[1024 * 1024]);
            privateStream.Position = 123;
            privateData.SetData("CanIncludeInClipboardHistory", privateStream);
            Assert.Null(ClipboardContent.Capture(privateData, "")); Assert.Equal(123, privateStream.Position);
            var onlyRtf = new System.Windows.DataObject();
            onlyRtf.SetData(System.Windows.DataFormats.Rtf, @"{\rtf1\ansi search \b needle\b0  in rich text}");
            var rtfCapture = ClipboardContent.Capture(onlyRtf, "fixture.exe")!;
            Assert.Contains("search needle", rtfCapture.Record.Text);
            var rtfRecord = store.Add(rtfCapture.Record);
            Assert.True(rtfRecord.Matches("富文本", "needle"));
            Assert.Equal(onlyRtf.GetData(System.Windows.DataFormats.Rtf), ClipboardContent.BuildData(rtfRecord, store).GetData(System.Windows.DataFormats.Rtf));
        }
        finally
        {
            if (window.IsVisible) window.Close();
            if (System.IO.Directory.Exists(folder)) System.IO.Directory.Delete(folder, true);
        }
        VerifyLanTransferSettings();
        VerifyClipboardMenuDismissal();
    }

    private static void VerifyLanTransferSettings()
    {
        var root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "lan-ui-" + Guid.NewGuid().ToString("N"));
        var path = System.IO.Path.Combine(root, "preferences.json");
        IReadOnlyList<System.Net.IPAddress> addresses = [];
        System.Windows.DataObject? copied = null;
        var failCopy = false;
        var window = new LanTransferWindow(path, readAddresses: () => addresses, writeClipboard: data =>
        { if (failCopy) throw new System.Runtime.InteropServices.ExternalException(); copied = data; })
        { Left = -10000, Top = -10000, ShowActivated = false, WindowStartupLocation = WindowStartupLocation.Manual };
        try
        {
            window.Show(); window.Width = window.MinWidth; window.Height = window.MinHeight; window.UpdateLayout();
            var localAddresses = (ComboBox)window.FindName("LocalAddressBox");
            Assert.Empty(localAddresses.Items);
            addresses = [System.Net.IPAddress.Parse("192.168.1.20")];
            window.RefreshLocalAddresses();
            Assert.Equal(addresses[0], localAddresses.SelectedItem);
            addresses = [System.Net.IPAddress.Parse("192.168.2.30")];
            window.RefreshLocalAddresses();
            Assert.Equal(addresses[0], localAddresses.SelectedItem);
            Assert.True(((FrameworkElement)window.FindName("DropArea")).AllowDrop);
            Assert.False(((Button)window.FindName("ChooseFilesButton")).IsEnabled);
            var sending = window.SendFilesAsync(["fixture.bin"]); PumpUntil(() => sending.IsCompleted);
            Assert.Contains("先配对", ((TextBlock)window.FindName("TransferStatus")).Text);
            ((Expander)window.FindName("FileTransferSection")).IsExpanded = true;
            window.UpdateLayout();
            var folder = System.IO.Path.Combine(root, "接收目录");
            ((System.Windows.Controls.TextBox)window.FindName("ReceiveFolderBox")).Text = folder;
            Visuals<Button>(window).Single(b => Equals(b.Content, "保存")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Equal(folder, OverTranslate.Services.Lan.LanPreferences.Load(path).ReceiveFolder);
            ((CheckBox)window.FindName("AutoSyncBox")).IsChecked = false;
            Assert.False(OverTranslate.Services.Lan.LanPreferences.Load(path).AutoSync);
            Assert.Contains("已暂停", ((TextBlock)window.FindName("ClipboardStatus")).Text);
            var connectionStatus = ((TextBlock)window.FindName("ConnectionStatus")).Text;
            window.ShowClipboardStatus("剪贴板同步失败，测试提示");
            PumpUntil(() => ((TextBlock)window.FindName("ClipboardStatus")).Text.Contains("同步失败"));
            Assert.Equal(connectionStatus, ((TextBlock)window.FindName("ConnectionStatus")).Text);
            Assert.False(OverTranslate.Services.Lan.LanPreferences.Load(path).AutoSyncLibrary);
            window.EnableUnifiedSync();
            var unified = OverTranslate.Services.Lan.LanPreferences.Load(path);
            Assert.True(unified.AutoSync); Assert.True(unified.AutoSyncLibrary);
            Assert.True(((CheckBox)window.FindName("LibrarySyncBox")).IsChecked);
            ((CheckBox)window.FindName("LibrarySyncBox")).IsChecked = false;
            Assert.False(OverTranslate.Services.Lan.LanPreferences.Load(path).AutoSyncLibrary);
            var copyButton = (Button)window.FindName("CopyPairingButton");
            Assert.False(copyButton.IsEnabled);
            addresses = [System.Net.IPAddress.Loopback]; window.RefreshLocalAddresses();
            ((Button)window.FindName("ListenButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            PumpUntil(() => copyButton.IsEnabled);
            var info = (System.Windows.Controls.TextBox)window.FindName("PairingInfo");
            Assert.Contains("对方地址：127.0.0.1:", info.Text);
            Assert.Contains("提示词与模型库：暂停", info.Text);
            Assert.Equal(5, info.Text.Split('\n').Length);
            var originalInfo = info.Text;
            addresses = [System.Net.IPAddress.Parse("192.168.2.30")]; window.RefreshLocalAddresses();
            ((CheckBox)window.FindName("LibrarySyncBox")).IsChecked = true;
            Assert.Contains("对方地址：127.0.0.1:", info.Text);
            Assert.Contains("开启（含 API Key）", info.Text);
            Assert.NotEqual(originalInfo, info.Text);
            copyButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.NotNull(copied); Assert.Equal(info.Text, copied.GetText());
            Assert.True(copied.GetDataPresent("ExcludeClipboardContentFromMonitorProcessing"));
            Assert.True(copied.GetDataPresent("CanUploadToCloudClipboard"));
            Assert.Null(ClipboardContent.Capture(copied, "fixture"));
            failCopy = true;
            copyButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Contains("复制失败", ((TextBlock)window.FindName("PairingCopyStatus")).Text);
            Assert.True(copyButton.IsEnabled);
            Visuals<Button>(window).Single(b => Equals(b.Content, "断开连接")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            PumpUntil(() => !copyButton.IsEnabled && ((TextBlock)window.FindName("ConnectionStatus")).Text.Contains("已断开"));
            Assert.DoesNotContain("完整配对码：", info.Text);
            ((Expander)window.FindName("FileTransferSection")).IsExpanded = false;
            // 视觉夹具只展示虚构配对信息，不保存实际监听期间的配对码。
            info.Text = LanTransferWindow.BuildPairingInfo("我的电脑", "192.168.1.20:52000", "0123456789ABCDEF-0123456789ABCDEF", unified);
            copyButton.IsEnabled = true;
            ((TextBlock)window.FindName("ConnectionStatus")).Text = "等待另一台电脑连接";
            ((TextBlock)window.FindName("PairingCopyStatus")).Text = "仅分享给自己的可信电脑";
            window.Width = 880; window.Height = 790; window.UpdateLayout();
            Render(window, "sync-pairing-desktop", 880, 790);
            window.Width = 760; window.Height = 650; window.UpdateLayout();
            Render(window, "sync-pairing-minimum", 760, 650);
            ApplyTheme(System.Windows.Application.Current, "Dark");
            Render(window, "sync-pairing-dark", 760, 650);
            ApplyTheme(System.Windows.Application.Current, "Light");

            window.Close(); Assert.False(window.IsVisible);
        }
        finally
        {
            var closing = window.ShutdownAsync(); PumpUntil(() => closing.IsCompleted);
            if (System.IO.Directory.Exists(root)) System.IO.Directory.Delete(root, true);
        }
    }

    private static void VerifyClipboardCaptureDrain(ClipboardHistoryStore store)
    {
        var ready = new TaskCompletionSource<ClipboardHistoryStore>(TaskCreationOptions.RunContinuationsAsynchronously);
        var reads = 0; var text = "capture-A";
        using var runtime = new ClipboardHistoryRuntime(System.Windows.Threading.Dispatcher.CurrentDispatcher, ready.Task,
            () => { reads++; return new(new() { Text = text }, null); }, () => 1, listen: false);
        runtime.TrackCapture();
        Assert.Equal(1, reads); // 初始化尚未完成，也必须先取不可变快照。
        text = "capture-B"; runtime.TrackCapture(); Assert.Equal(2, reads);
        text = "capture-C"; runtime.NotifyClipboardChanged();
        var stopping = runtime.StopAndDrainAsync();
        Assert.Equal(3, reads); Assert.False(stopping.IsCompleted);
        ready.SetResult(store);
        PumpUntil(() => stopping.IsCompleted); stopping.GetAwaiter().GetResult();
        Assert.Equal(3, store.Read().Count);
        foreach (var item in store.Read()) store.Delete(item.Id);
    }

    private static void VerifyClipboardMenuDismissal()
    {
        var folder = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "clipboard-menu-" + Guid.NewGuid().ToString("N"));
        foreach (var name in new[] { "MenuButton", "HistoryList" })
        {
            var window = new ClipboardHistoryWindow(new ClipboardHistoryStore(folder), _ => Task.CompletedTask)
            { Left = -10000, Top = -10000, ShowActivated = false };
            var other = new Window { Width = 160, Height = 80, Left = -10000, Top = -10000,
                WindowStartupLocation = WindowStartupLocation.Manual, ShowInTaskbar = false };
            try
            {
                window.Show(); window.Activate(); window.UpdateLayout(); PumpUntil(() => window.IsActive);
                var menu = ((FrameworkElement)window.FindName(name)).ContextMenu;
                menu.PlacementTarget = (UIElement)window.FindName(name); menu.IsOpen = true;
                Assert.True(window.IsVisible);
                other.Show(); other.Activate();
                PumpUntil(() => !window.IsActive && !window.IsVisible);
                Assert.False(window.IsVisible);
            }
            finally { other.Close(); window.Close(); }
        }
    }
}
