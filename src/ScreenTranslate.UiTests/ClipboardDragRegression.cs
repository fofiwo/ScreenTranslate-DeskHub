using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using OverTranslate.Models;
using OverTranslate.Services;
using OverTranslate.Services.ClipboardHistory;
using OverTranslate.Views.ClipboardHistory;
using Xunit;

namespace OverTranslate.Tests;

public sealed partial class ScreenTranslateShellTests
{
    private static void VerifyClipboardDragOut()
    {
        var folder = Path.Combine(Path.GetTempPath(), "clipboard-drag-test-" + Guid.NewGuid().ToString("N"));
        var store = new ClipboardHistoryStore(Path.Combine(folder, "history"));
        var text = store.Add(new() { Text = "拖出完整文字\n第二行", Html = "<b>拖出完整文字</b>", Rtf = @"{\rtf1 drag text}" });
        var bitmap = BitmapSource.Create(1, 1, 96, 96, PixelFormats.Bgra32, null, new byte[] { 0, 0, 255, 255 }, 4);
        var png = ClipboardContent.EncodeImage(bitmap);
        var picture = store.Add(new(), png);
        var filePath = Path.Combine(folder, "source.txt"); File.WriteAllText(filePath, "保留原文件");
        var files = store.Add(new() { Files = [filePath, folder] });
        var copies = 0;
        var saved = 0;
        store.Changed += (_, _) => Interlocked.Increment(ref saved);
        var drags = 0;
        var native = Environment.GetEnvironmentVariable("SCREEN_TRANSLATE_CLIPBOARD_DRAG_NATIVE") == "1";
        var result = DragDropEffects.Copy;
        IDataObject? dragged = null;
        var downs = 0;
        Func<IDataObject, DragDropEffects, DragDropEffects>? nativeDrag = null;
        var window = new ClipboardHistoryWindow(store, _ => { copies++; return Task.CompletedTask; },
            settings: new AppSettings { ClipboardAutoPaste = false }, saveSettings: () => true,
            syncActivities: new OverTranslate.Services.Lan.SyncActivityStore(Path.Combine(folder, "activity.json")),
            drag: (data, effects) => { Assert.Equal(DragDropEffects.Copy, effects); drags++; dragged = data; return nativeDrag?.Invoke(data, effects) ?? result; })
        { Left = -10000, Top = -10000, Width = 560, Height = 700 };
        InputMethod.SetIsInputMethodEnabled((TextBox)window.FindName("SearchBox"), false);
        window.PreviewMouseDown += (_, _) => downs++;
        Assert.True(HoverGetCursorPos(out var cursor));
        try
        {
            var exportFolder = Path.Combine(folder, "exports");
            DataObject Prepare(ClipboardRecord record)
            {
                var task = ClipboardContent.BuildDragDataAsync(record, store, exportFolder);
                PumpUntil(() => task.IsCompleted); return task.GetAwaiter().GetResult();
            }
            var data = Prepare(text);
            Assert.Equal(text.Text, data.GetText()); Assert.Equal(text.Html, data.GetData(DataFormats.Html));
            Assert.Equal(text.Rtf, data.GetData(DataFormats.Rtf)); Assert.False(data.ContainsFileDropList());
            data = Prepare(picture);
            Assert.True(data.ContainsImage()); Assert.True(data.GetImage().IsFrozen);
            var exported = Assert.Single(data.GetFileDropList().Cast<string>());
            Assert.Equal(png, File.ReadAllBytes(exported)); Assert.Equal(png, store.ReadImage(picture.Id));
            Assert.NotEqual(Path.Combine(store.Folder, picture.Id + ".png"), exported);
            Assert.Equal(files.Files, Prepare(files).GetFileDropList().Cast<string>());
            var missing = ClipboardContent.BuildDragDataAsync(new() { Files = [filePath + ".missing"] }, store, exportFolder);
            PumpUntil(() => missing.IsCompleted); Assert.Throws<IOException>(() => missing.GetAwaiter().GetResult());

            if (native) { window.Left = 80; window.Top = 80; }
            window.SetWindowPinned(true); window.ShowPanel();
            var handle = new System.Windows.Interop.WindowInteropHelper(window).Handle;
            if (native) NativePasteShowWindow(handle, 5);
            PumpUntil(() => !window.IsRefreshing && (!native || window.IsActive)); window.UpdateLayout();
            var list = (ListBox)window.FindName("HistoryList");
            FrameworkElement Select(ClipboardRecord record)
            {
                list.SelectedItem = list.Items.Cast<ClipboardRecordView>().Single(v => v.Record.Id == record.Id);
                list.ScrollIntoView(list.SelectedItem); window.UpdateLayout();
                var item = (ListBoxItem)list.ItemContainerGenerator.ContainerFromItem(list.SelectedItem);
                if (record.HasImage)
                {
                    PumpUntil(() => Visuals<Image>(item).Any(i => i.Source is not null));
                    return Visuals<Image>(item).First(i => i.Source is not null);
                }
                return Visuals<TextBlock>(item).First(t => t.Text == ((ClipboardRecordView)list.SelectedItem).Preview);
            }
            void Press(FrameworkElement element)
            {
                Assert.Equal(handle, NativePasteGetForeground());
                var point = element.PointToScreen(new Point(15, element.ActualHeight / 2));
                Assert.True(HoverSetCursorPos((int)point.X, (int)point.Y));
                PumpUntil(() => element.IsMouseOver);
                // 仅向当前前台的合成测试窗口发送鼠标按键。
                DragMouseButton(0x0002);
                try { PumpUntil(() => Mouse.LeftButton == MouseButtonState.Pressed); }
                catch (Exception ex) { throw new InvalidOperationException($"Mouse down: visible={window.IsVisible}, active={window.IsActive}, over={element.IsMouseOver}, foreground={NativePasteGetForeground() == handle}, capture={Mouse.Captured?.GetType().Name}, downs={downs}, async={DragGetAsyncKeyState(1)}, key={DragGetKeyState(1)}", ex); }
                window.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.Background);
            }
            void Move(FrameworkElement element, double distance)
            {
                var point = element.PointToScreen(new Point(15 + distance, element.ActualHeight / 2));
                Assert.True(HoverSetCursorPos((int)point.X, (int)point.Y));
            }
            void Release()
            {
                DragMouseButton(0x0004);
                PumpUntil(() => Mouse.LeftButton == MouseButtonState.Released && !window.IsBusy);
                window.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.Background);
            }

            var body = Select(text);
            if (!native)
            {
                body.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left) { RoutedEvent = Mouse.PreviewMouseDownEvent });
                body.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, 0) { RoutedEvent = Mouse.PreviewMouseMoveEvent });
                body.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left) { RoutedEvent = Mouse.PreviewMouseUpEvent });
                // IsBusy 只覆盖剪贴板操作；等待复制时间落盘后才能删除夹具目录。
                PumpUntil(() => copies == 1 && !window.IsBusy && Volatile.Read(ref saved) == 1);
                Assert.Equal(0, drags); Assert.False(window.IsVisible);
                return;
            }
            Press(body); Move(body, 1);
            window.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.Background);
            Assert.Equal(0, drags); Release();
            PumpUntil(() => copies == 1 && Volatile.Read(ref saved) == 1); Assert.False(window.IsVisible);

            window.ShowPanel(); PumpUntil(() => !window.IsRefreshing && window.IsActive); window.UpdateLayout();
            body = Select(text); Press(body); Move(body, 24); PumpUntil(() => drags == 1); Release();
            Assert.Equal(text.Text, dragged!.GetData(DataFormats.UnicodeText));
            Assert.Equal(1, copies); Assert.True(window.IsVisible);

            // 取消拖拽后的松手不会退回为点击粘贴。
            result = DragDropEffects.None;
            body = Select(text); Press(body); Move(body, 24); PumpUntil(() => drags == 2); Release();
            Assert.Equal(1, copies); Assert.Contains("取消", ((TextBlock)window.FindName("StatusText")).Text);

            // 大图准备期间先松手：不能在之后的一次按住鼠标时迟到启动旧拖拽。
            var gate = typeof(ClipboardHistoryStore).GetField("_gate", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(store)!;
            using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
            var holding = Task.Run(() => { lock (gate) { entered.Set(); release.Wait(TimeSpan.FromSeconds(10)); } });
            Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
            try
            {
                body = Select(picture); Press(body); Move(body, 24); PumpUntil(() => window.IsBusy);
                DragMouseButton(0x0004);
                PumpUntil(() => Mouse.LeftButton == MouseButtonState.Released);
            }
            finally { release.Set(); holding.GetAwaiter().GetResult(); }
            PumpUntil(() => !window.IsBusy);
            Assert.Equal(2, drags); Assert.Equal(1, copies);
            Assert.Equal(png, store.ReadImage(picture.Id)); Assert.True(File.Exists(filePath));

            // 真正经过 OLE DoDragDrop，目标仅为本测试创建的输入框和文件接收区。
            var input = new TextBox { AllowDrop = true, AcceptsReturn = true, Height = 80, Margin = new Thickness(12) };
            var fileTarget = new Border { AllowDrop = true, Height = 100, Margin = new Thickness(12), Background = Brushes.White };
            var panel = new StackPanel(); panel.Children.Add(input); panel.Children.Add(fileTarget);
            var target = new Window { Content = panel, Left = 720, Top = 80, Width = 360, Height = 300, ShowActivated = false, ShowInTaskbar = false };
            var received = Path.Combine(folder, "received.png");
            fileTarget.DragOver += (_, e) => { e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None; e.Handled = true; };
            fileTarget.Drop += (_, e) =>
            {
                var paths = Assert.IsType<string[]>(e.Data.GetData(DataFormats.FileDrop));
                File.Copy(Assert.Single(paths), received); e.Effects = DragDropEffects.Copy; e.Handled = true;
            };
            try
            {
                target.Show(); NativePasteShowWindow(new System.Windows.Interop.WindowInteropHelper(target).Handle, 5); target.UpdateLayout();
                FrameworkElement destination = input;
                nativeDrag = (payload, effects) =>
                {
                    window.SetWindowPinned(false);
                    var point = destination.PointToScreen(new Point(destination.ActualWidth / 2, destination.ActualHeight / 2));
                    var step = 0;
                    var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
                    timer.Tick += (_, _) =>
                    {
                        if (step++ == 0) HoverSetCursorPos((int)point.X, (int)point.Y);
                        else { timer.Stop(); DragMouseButton(0x0004); }
                    };
                    timer.Start();
                    try { return System.Windows.DragDrop.DoDragDrop(list, payload, effects); }
                    finally { timer.Stop(); }
                };
                window.ShowPanel(); PumpUntil(() => !window.IsRefreshing && window.IsActive);
                body = Select(text); Press(body); Move(body, 24);
                PumpUntil(() => drags == 3 && !window.IsBusy);
                Assert.Equal(text.Text, input.Text);
                Assert.False(window.IsVisible); Assert.Equal(1, copies);

                window.ShowPanel(); PumpUntil(() => !window.IsRefreshing && window.IsActive);
                destination = fileTarget;
                body = Select(picture); Press(body); Move(body, 24);
                PumpUntil(() => drags == 4 && !window.IsBusy);
                Assert.Equal(png, File.ReadAllBytes(received)); Assert.Equal(png, store.ReadImage(picture.Id));
                Assert.False(window.IsVisible); Assert.Equal(1, copies);
            }
            finally { target.Close(); }
        }
        finally
        {
            if (native) DragMouseButton(0x0004);
            window.Close(); HoverSetCursorPos(cursor.X, cursor.Y); Directory.Delete(folder, true);
        }
    }

    private static void DragMouseButton(uint flags)
    {
        var input = new KeyboardInput.INPUT { type = 0,
            u = new KeyboardInput.INPUTUNION { mi = new KeyboardInput.MOUSEINPUT { dwFlags = flags, dwExtraInfo = KeyboardInput.InjectedMarker } } };
        Assert.Equal(1u, DragSendInput(1, [input], Marshal.SizeOf<KeyboardInput.INPUT>()));
    }

    [DllImport("user32.dll", EntryPoint = "SendInput", SetLastError = true)]
    private static extern uint DragSendInput(uint count, KeyboardInput.INPUT[] inputs, int size);
    [DllImport("user32.dll", EntryPoint = "GetAsyncKeyState")]
    private static extern short DragGetAsyncKeyState(int key);
    [DllImport("user32.dll", EntryPoint = "GetKeyState")]
    private static extern short DragGetKeyState(int key);
}
