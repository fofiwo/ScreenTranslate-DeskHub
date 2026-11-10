using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using OverTranslate.Services.Library;
using Button = System.Windows.Controls.Button;

namespace OverTranslate.Views.Controls;

internal partial class AppMessageWindow : Window
{
    internal MessageBoxResult Result { get; private set; }

    internal AppMessageWindow(string message, string caption, MessageBoxButton buttons,
        MessageBoxImage image, MessageBoxResult defaultResult, Action<string>? copy = null)
    {
        InitializeComponent();
        Title = MessageTitle.Text = caption;
        MessageText.Text = message;
        SeverityText.Text = image switch
        {
            MessageBoxImage.Error or MessageBoxImage.Warning => "!",
            MessageBoxImage.Question => "?", MessageBoxImage.Information => "i", _ => ""
        };
        if (image == MessageBoxImage.None) SeverityText.Visibility = Visibility.Collapsed;
        MessageBoxResult[] choices = buttons switch
        {
            MessageBoxButton.OK => [MessageBoxResult.OK],
            MessageBoxButton.OKCancel => [MessageBoxResult.OK, MessageBoxResult.Cancel],
            MessageBoxButton.YesNo => [MessageBoxResult.Yes, MessageBoxResult.No],
            MessageBoxButton.YesNoCancel => [MessageBoxResult.Yes, MessageBoxResult.No, MessageBoxResult.Cancel],
            _ => throw new ArgumentOutOfRangeException(nameof(buttons))
        };
        // 关闭或 Esc 始终走取消分支，不能隐式允许删除、配对或忘记设备。
        Result = buttons == MessageBoxButton.YesNo ? MessageBoxResult.No : choices[^1];
        if (defaultResult == MessageBoxResult.None) defaultResult = choices[0];
        if (!choices.Contains(defaultResult)) throw new ArgumentException("默认按钮不属于当前对话框。", nameof(defaultResult));
        foreach (var choice in choices)
        {
            var button = new Button
            {
                Content = choice switch { MessageBoxResult.OK => "确定", MessageBoxResult.Cancel => "取消", MessageBoxResult.Yes => "是", _ => "否" },
                Tag = choice, IsDefault = choice == defaultResult, IsCancel = choice == Result,
                Style = (Style)FindResource(choice is MessageBoxResult.Yes or MessageBoxResult.OK ? "ProductButton" : "ProductSecondaryButton"),
                MinWidth = 80, Margin = new Thickness(8, 0, 0, 8)
            };
            button.Click += (_, _) => { Result = choice; Close(); };
            Actions.Children.Add(button);
        }
        copy ??= text => LibraryClipboard.SetText(text, new WindowInteropHelper(this).Handle);
        CommandBindings.Add(new CommandBinding(ApplicationCommands.Copy, async (_, e) =>
        {
            e.Handled = true;
            try
            {
                await LibraryClipboard.WriteAsync($"{caption}\r\n\r\n{message}\r\n\r\n" +
                    string.Join("    ", Actions.Children.OfType<Button>().Select(b => b.Content)), copy, CancellationToken.None);
            }
            catch (Exception ex) when (ex is System.Runtime.InteropServices.ExternalException or System.ComponentModel.Win32Exception)
            { NLog.LogManager.GetCurrentClassLogger().Warn("消息弹窗复制失败：{0}", ex.GetType().Name); }
        }));
        Loaded += (_, _) => Actions.Children.OfType<Button>().First(b => b.IsDefault).Focus();
    }

    internal static MessageBoxResult ShowMessage(string message, string caption = "ScreenTranslate",
        MessageBoxButton buttons = MessageBoxButton.OK, MessageBoxImage image = MessageBoxImage.None,
        MessageBoxResult defaultResult = MessageBoxResult.None)
        => ShowMessage(null, message, caption, buttons, image, defaultResult);

    internal static MessageBoxResult ShowMessage(Window? owner, string message, string caption = "ScreenTranslate",
        MessageBoxButton buttons = MessageBoxButton.OK, MessageBoxImage image = MessageBoxImage.None,
        MessageBoxResult defaultResult = MessageBoxResult.None)
    {
        var dialog = new AppMessageWindow(message, caption, buttons, image, defaultResult);
        if (owner is not null && new WindowInteropHelper(owner).Handle != IntPtr.Zero)
        { dialog.Owner = owner; dialog.WindowStartupLocation = WindowStartupLocation.CenterOwner; }
        dialog.ShowDialog();
        return dialog.Result;
    }
}
