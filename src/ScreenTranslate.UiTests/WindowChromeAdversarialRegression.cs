using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Threading;
using OverTranslate.Views.Controls;
using Xunit;
using Button = System.Windows.Controls.Button;

namespace OverTranslate.Tests;

public sealed partial class ScreenTranslateShellTests
{
    private static void VerifyAppMessageDialogs()
    {
        string? copied = null;
        var copyAttempts = 0;
        var owner = new AppMessageWindow("owner fixture", "fixture", MessageBoxButton.OK, MessageBoxImage.None, MessageBoxResult.None,
            text =>
            {
                if (++copyAttempts == 1) throw new COMException("synthetic clipboard busy", unchecked((int)0x800401D0));
                copied = text;
            })
        { Left = -10000, Top = -10000, WindowStartupLocation = WindowStartupLocation.Manual, ShowActivated = false };
        owner.Show();
        try
        {
            var copyTarget = Visuals<Button>(owner).Single(b => b.IsDefault);
            Assert.True(System.Windows.Input.ApplicationCommands.Copy.CanExecute(null, copyTarget),
                "消息弹窗必须保留系统消息框的 Ctrl+C 复制能力。");
            System.Windows.Input.ApplicationCommands.Copy.Execute(null, copyTarget);
            PumpUntil(() => copied is not null);
            Assert.Equal(2, copyAttempts);
            Assert.Equal("fixture\r\n\r\nowner fixture\r\n\r\n确定", copied);
            Assert.True(owner.IsVisible);
            foreach (var (buttons, selected, key, expected) in new[] {
                (MessageBoxButton.YesNo, MessageBoxResult.No, 13, MessageBoxResult.No),
                (MessageBoxButton.YesNo, MessageBoxResult.Yes, 13, MessageBoxResult.Yes),
                (MessageBoxButton.OKCancel, MessageBoxResult.OK, 27, MessageBoxResult.Cancel),
                (MessageBoxButton.YesNoCancel, MessageBoxResult.Yes, 27, MessageBoxResult.Cancel),
                (MessageBoxButton.OK, MessageBoxResult.OK, 13, MessageBoxResult.OK) })
                RunPrompt(buttons, selected, expected, dialog =>
                {
                    dialog.Activate();
                    PumpUntil(() => dialog.IsActive);
                    var defaultButton = Visuals<Button>(dialog).Single(b => b.IsDefault);
                    Assert.True(defaultButton.Focus() && defaultButton.IsKeyboardFocused,
                        $"原生按键测试必须先取得焦点：{buttons}, default={selected}, active={dialog.IsActive}。");
                    var handle = new WindowInteropHelper(dialog).Handle;
                    var keyData = 1 | ((key == 13 ? 0x1c : 0x01) << 16);
                    Assert.True(ChromePostMessage(handle, 0x0100, new IntPtr(key), new IntPtr(keyData)));
                    Assert.True(ChromePostMessage(handle, 0x0101, new IntPtr(key), new IntPtr(unchecked((int)(0xc0000000u | (uint)keyData)))));
                });
            RunPrompt(MessageBoxButton.YesNo, MessageBoxResult.Yes, MessageBoxResult.No, dialog =>
                Visuals<Button>(dialog).Single(b => b.Name == "CloseButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent)));
            RunPrompt(MessageBoxButton.YesNo, MessageBoxResult.No, MessageBoxResult.Yes, dialog =>
                Visuals<Button>(dialog).Single(b => Equals(b.Tag, MessageBoxResult.Yes)).RaiseEvent(new RoutedEventArgs(Button.ClickEvent)));
            RunPrompt(MessageBoxButton.OK, MessageBoxResult.OK, MessageBoxResult.OK, dialog => dialog.Close(), owned: false);

            void RunPrompt(MessageBoxButton buttons, MessageBoxResult selected, MessageBoxResult expected, Action<AppMessageWindow> action, bool owned = true)
            {
                Exception? failure = null;
                var timedOut = false;
                var timeout = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
                timeout.Tick += (_, _) =>
                {
                    timedOut = true; timeout.Stop();
                    foreach (var pending in System.Windows.Application.Current.Windows.OfType<AppMessageWindow>().Where(w => w != owner).ToArray()) pending.Close();
                };
                owner.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, () =>
                {
                    var dialog = System.Windows.Application.Current.Windows.OfType<AppMessageWindow>().Single(w => w != owner);
                    try
                    {
                        dialog.UpdateLayout();
                        VerifyWindowChromeInput(dialog);
                        Assert.Same(owned ? owner : null, dialog.Owner);
                        Assert.False(ChromeIsWindowEnabled(new WindowInteropHelper(owner).Handle));
                        Assert.Equal(selected, Visuals<Button>(dialog).Single(b => b.IsDefault).Tag);
                        AssertInside((FrameworkElement)dialog.Content, Visuals<Button>(dialog).First(b => b.Tag is MessageBoxResult));
                        if (buttons == MessageBoxButton.YesNo && selected == MessageBoxResult.No)
                            Render(dialog, "message-confirm-no-default", (int)dialog.ActualWidth, (int)dialog.ActualHeight);
                        action(dialog);
                    }
                    catch (Exception ex) { failure = ex; dialog.Close(); }
                });
                timeout.Start();
                MessageBoxResult result;
                try { result = AppMessageWindow.ShowMessage(owned ? owner : null, new string('长', 8000), "确认操作（测试）", buttons, MessageBoxImage.Question, selected); }
                finally { timeout.Stop(); }
                if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
                Assert.False(timedOut, $"对话框未响应键盘消息或关闭动作：{buttons}, default={selected}, expected={expected}, owned={owned}。");
                Assert.Equal(expected, result);
                Assert.True(ChromeIsWindowEnabled(new WindowInteropHelper(owner).Handle));
            }
        }
        finally { owner.Close(); }
        VerifyDeleteConfirmations();
    }

    private static void VerifyDeleteConfirmations()
    {
        var directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "message-consumers-" + Guid.NewGuid());
        var store = new OverTranslate.Services.Library.LibraryStore(System.IO.Path.Combine(directory, "library.json"));
        var prompt = store.SavePrompt(new() { Title = "fixture", Content = "synthetic text" });
        store.Update(state => state.Connections.Add(new() { Platform = "fixture", Model = "fixture-model" }));
        var model = store.Read().Connections.Single();
        var window = new OverTranslate.Views.Library.LibraryWindow(store, copy: _ => { })
        { Left = -10000, Top = -10000, ShowActivated = false, WindowStartupLocation = WindowStartupLocation.Manual };
        try
        {
            window.Show();
            foreach (var (method, item, exists) in new (string, object, Func<bool>)[] {
                ("DeletePrompt_Click", prompt, () => store.Read().Prompts.Any(p => p.Id == prompt.Id)),
                ("DeleteModel_Click", model, () => store.Read().Connections.Any(c => c.Id == model.Id)) })
            foreach (var approve in new[] { false, true })
            {
                Exception? failure = null;
                var responded = false;
                var timeout = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
                timeout.Tick += (_, _) =>
                {
                    failure = new TimeoutException("删除确认未完成。");
                    foreach (var dialog in System.Windows.Application.Current.Windows.OfType<AppMessageWindow>().ToArray()) dialog.Close();
                };
                var response = window.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, () =>
                {
                    var dialog = System.Windows.Application.Current.Windows.OfType<AppMessageWindow>().Single();
                    try
                    {
                        Assert.Same(window, dialog.Owner); AssertNoNativeCaption(dialog);
                        responded = true;
                        if (approve) Visuals<Button>(dialog).Single(b => Equals(b.Tag, MessageBoxResult.Yes)).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                        else dialog.Close();
                    }
                    catch (Exception ex) { failure = ex; dialog.Close(); }
                });
                timeout.Start();
                try
                {
                    typeof(OverTranslate.Views.Library.LibraryWindow).GetMethod(method, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                        .Invoke(window, [new Button { Tag = item }, new RoutedEventArgs()]);
                }
                finally { timeout.Stop(); response.Abort(); }
                if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
                PumpUntil(() => !Assert.IsType<bool>(Field(window, "_changingData")));
                Assert.True(responded); Assert.Equal(!approve, exists());
            }
        }
        finally { window.CloseForTests(); System.IO.Directory.Delete(directory, true); }
    }

    private static void VerifyWindowChromeInput(Window window)
    {
        AssertNoNativeCaption(window);
        var header = Visuals<OverTranslate.Views.Controls.ToolWindowHeader>(window).Single(c => c.IsVisible);
        Assert.Equal(2, ChromeHit(window, header.PointToScreen(new Point(180, 20))));
        foreach (var button in Visuals<Button>(header).Where(b => b.IsVisible))
            Assert.Equal(1, ChromeHit(window, button.PointToScreen(new Point(button.ActualWidth / 2, button.ActualHeight / 2))));
        if (window.ResizeMode is ResizeMode.CanResize or ResizeMode.CanResizeWithGrip)
            Assert.Equal(10, ChromeHit(window, window.PointToScreen(new Point(2, window.ActualHeight / 2))));
        else
            Assert.Equal(1, ChromeHit(window, window.PointToScreen(new Point(2, window.ActualHeight / 2))));
    }

    private static int ChromeHit(Window window, Point screen)
    {
        var coordinates = ((int)screen.X & 0xffff) | (((int)screen.Y & 0xffff) << 16);
        return (int)ChromeSendMessage(new WindowInteropHelper(window).Handle, 0x0084, IntPtr.Zero, new IntPtr(coordinates));
    }

    [DllImport("user32.dll", EntryPoint = "SendMessageW")]
    private static extern IntPtr ChromeSendMessage(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", EntryPoint = "PostMessageW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ChromePostMessage(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", EntryPoint = "IsWindowEnabled")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ChromeIsWindowEnabled(IntPtr hwnd);
}
