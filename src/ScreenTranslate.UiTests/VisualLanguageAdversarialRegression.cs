using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using OverTranslate.Views.Controls;
using Xunit;

namespace OverTranslate.Tests;

public sealed partial class ScreenTranslateShellTests
{
    private static void VerifyVisualLanguageAdversarial()
    {
        var errors = new List<string>();
        try
        {
            foreach (var theme in new[] { "Light", "Dark" })
            {
                ApplyTheme(System.Windows.Application.Current, theme);
                foreach (var caption in new[] { "确认操作", new string('长', 160) })
                foreach (var scale in new[] { 1.0, 2.0 })
                {
                    var dialog = new AppMessageWindow(new string('文', 4000) + "正文结尾", caption,
                        MessageBoxButton.YesNoCancel, MessageBoxImage.Question, MessageBoxResult.No, _ => { })
                    { Left = -10000, Top = -10000, WindowStartupLocation = WindowStartupLocation.Manual, ShowActivated = false };
                    try
                    {
                        dialog.Show(); dialog.UpdateLayout();
                        var root = (FrameworkElement)dialog.Content;
                        Render(dialog, $"visual-audit-message-{theme}-{caption.Length}-{scale}", (int)root.ActualWidth, (int)root.ActualHeight, scale);
                        var message = (TextBlock)dialog.FindName("MessageText");
                        var scroll = Visuals<ScrollViewer>(root).Single(s => Visuals<TextBlock>(s).Contains(message));
                        var actions = (WrapPanel)dialog.FindName("Actions");
                        var scrollBounds = scroll.TransformToAncestor(root).TransformBounds(new Rect(scroll.RenderSize));
                        var actionBounds = actions.TransformToAncestor(root).TransformBounds(new Rect(actions.RenderSize));
                        if (scrollBounds.Bottom > actionBounds.Top + 1)
                            errors.Add($"{theme}, title={caption.Length}, DPI={scale}: body viewport covers the action area ({scrollBounds.Bottom:0} > {actionBounds.Top:0}).");
                        scroll.ScrollToEnd(); dialog.UpdateLayout();
                        var viewport = Visuals<ScrollContentPresenter>(scroll).First();
                        var end = message.TransformToAncestor(viewport).Transform(new Point(0, message.ActualHeight));
                        if (end.Y > viewport.ActualHeight + 1)
                            errors.Add($"{theme}, title={caption.Length}, DPI={scale}: end of the message cannot be reached by scrolling.");
                        foreach (var button in actions.Children.OfType<Button>()) AssertInside(root, button);
                        Assert.Equal(MessageBoxResult.Cancel, dialog.Result);
                        Assert.Equal(MessageBoxResult.No, (MessageBoxResult)actions.Children.OfType<Button>().Single(b => b.IsDefault).Tag);
                    }
                    finally { dialog.Close(); }
                }
            }
        }
        finally { ApplyTheme(System.Windows.Application.Current, "Light"); }
        Assert.True(errors.Count == 0, string.Join(Environment.NewLine, errors));
    }

}
