using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using OverTranslate.Models;
using Button = System.Windows.Controls.Button;
using Panel = System.Windows.Controls.Panel;
using Color = System.Windows.Media.Color;
using Orientation = System.Windows.Controls.Orientation;

namespace OverTranslate.Views.Capture;

public partial class ScreenCaptureWindow
{
    private FrameworkElement? _snipOptions;
    private Border? _snipWelcome;
    private Border? _snipSizeBadge;
    private Border? _snipColorPreview;
    private Border? _snipColorHud;
    private TextBlock? _snipColorLabel;
    private readonly Dictionary<Color, ToggleButton> _snipColors = [];
    private readonly List<Button> _snipWriteActions = [];
    private Button? _snipCopy;
    private Button? _snipPin;

    private void InitializeSnipChrome()
    {
        Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/ScreenTranslate;component/Views/Capture/SnipStyles.xaml", UriKind.Relative) });
        var content = new StackPanel();
        var tools = new WrapPanel();
        foreach (var (tool, label) in new[] {
            (SnipTool.Select, "选区"), (SnipTool.Rectangle, "矩形"), (SnipTool.Arrow, "箭头"),
            (SnipTool.Pen, "画笔"), (SnipTool.Text, "文字"), (SnipTool.Mosaic, "马赛克"), (SnipTool.Picker, "取色") })
        {
            var button = new ToggleButton { Style = (Style)Resources["SnipTool"], Tag = tool,
                ToolTip = label + (tool == SnipTool.Picker ? "（移动查看颜色，C 复制）" : "") };
            var icon = tool switch { SnipTool.Select => "crop", SnipTool.Rectangle => "square", SnipTool.Arrow => "move-up-right",
                SnipTool.Pen => "pen-line", SnipTool.Text => "type", SnipTool.Mosaic => "grid-2x2", _ => "pipette" };
            button.Content = SnipIcons.Create(icon, button);
            System.Windows.Automation.AutomationProperties.SetName(button, label);
            button.Click += (_, _) => SetSnipTool(tool);
            tools.Children.Add(button); _snipTools[tool] = button;
        }
        tools.Children.Add(SnipDivider());
        _snipUndo = AddSnipButton(tools, "撤销（Ctrl+Z）", "undo-2", () => { CommitSnipText(); _snipDocument.Undo(); RefreshSnipDrawing(); });
        _snipRedo = AddSnipButton(tools, "重做（Ctrl+Y）", "redo-2", () => { CommitSnipText(); _snipDocument.Redo(); RefreshSnipDrawing(); });
        tools.Children.Add(SnipDivider());
        _snipPin = AddSnipButton(tools, "贴图（Ctrl+P）", "pin", PinSnip);
        var copy = _snipCopy = AddSnipButton(tools, "复制截图（Enter / Ctrl+C）", "copy", () => _ = CopySnipAsync());
        copy.Style = (Style)Resources["SnipCopy"];
        var copyContent = new StackPanel { Orientation = Orientation.Horizontal };
        copyContent.Children.Add(SnipIcons.Create("copy", copy, 16));
        copyContent.Children.Add(new TextBlock { Text = "复制", Margin = new Thickness(7, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center });
        copy.Content = copyContent;
        AddSnipButton(tools, "取消（Esc）", "x", Close, keepEnabledWhileWriting: true);
        content.Children.Add(tools);

        var options = new WrapPanel { Margin = new Thickness(0, 6, 0, 0), HorizontalAlignment = System.Windows.HorizontalAlignment.Center };
        options.Children.Add(new TextBlock { Text = "颜色", Style = (Style)Resources["SnipHint"], VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) });
        foreach (var color in new[] { Colors.Red, Colors.Orange, Colors.DodgerBlue, Colors.LimeGreen, Colors.White, Colors.Black })
        {
            var button = new ToggleButton { Style = (Style)Resources["SnipOption"], IsChecked = color == _snipColor,
                ToolTip = "标注颜色 " + SnipDocument.Hex(color),
                Content = new System.Windows.Shapes.Ellipse { Width = 16, Height = 16, Fill = new SolidColorBrush(color),
                    Stroke = new SolidColorBrush(Color.FromArgb(35, 0, 0, 0)), StrokeThickness = 1 } };
            System.Windows.Automation.AutomationProperties.SetName(button, (string)button.ToolTip);
            button.Click += (_, _) => ApplySnipColor(color);
            options.Children.Add(button); _snipColors[color] = button;
        }
        options.Children.Add(SnipDivider(20));
        options.Children.Add(new TextBlock { Text = "粗细", Style = (Style)Resources["SnipHint"], VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) });
        var weights = new List<ToggleButton>();
        foreach (var weight in new[] { 2, 3, 5, 8 })
        {
            var button = new ToggleButton { Style = (Style)Resources["SnipOption"], Tag = weight, IsChecked = weight == 3,
                ToolTip = $"{weight} 像素", Content = new System.Windows.Shapes.Ellipse { Width = weight + 2, Height = weight + 2,
                    Fill = new SolidColorBrush(Color.FromRgb(52, 64, 84)) } };
            System.Windows.Automation.AutomationProperties.SetName(button, $"标注粗细 {weight} 像素");
            button.Click += (_, _) => { _snipThickness = weight; foreach (var option in weights) option.IsChecked = Equals(option.Tag, weight); };
            weights.Add(button); options.Children.Add(button);
        }
        _snipOptions = options; content.Children.Add(options);
        var statusRow = new DockPanel { Margin = new Thickness(7, 8, 7, 2) };
        _snipStatus = new TextBlock { Style = (Style)Resources["SnipHint"], TextAlignment = TextAlignment.Center };
        statusRow.Children.Add(_snipStatus); content.Children.Add(statusRow);
        _snipToolbar = SnipFloatingSurface(content); _snipCanvas!.Children.Add(_snipToolbar);
        _snipSize = new TextBlock { Foreground = System.Windows.Media.Brushes.White, FontFamily = new System.Windows.Media.FontFamily("Cascadia Mono, Consolas"), FontSize = 11 };
        _snipSizeBadge = new Border { Background = new SolidColorBrush(Color.FromArgb(235, 25, 37, 53)), CornerRadius = new CornerRadius(5), Padding = new Thickness(8, 4, 8, 4), Child = _snipSize, IsHitTestVisible = false, Visibility = Visibility.Collapsed };
        _snipCanvas.Children.Add(_snipSizeBadge);
        var welcome = new StackPanel { Orientation = Orientation.Horizontal };
        var message = new StackPanel { Margin = new Thickness(10, 2, 26, 2), VerticalAlignment = VerticalAlignment.Center };
        message.Children.Add(new TextBlock { Text = "拖动选择截图区域", FontSize = 14, FontWeight = FontWeights.Medium, Foreground = new SolidColorBrush(Color.FromRgb(25, 37, 53)) });
        message.Children.Add(new TextBlock { Text = "C 复制色值   ·   Shift 切换 HEX/RGB   ·   Esc 取消", Style = (Style)Resources["SnipHint"], Margin = new Thickness(0, 5, 0, 0) });
        welcome.Children.Add(message);
        AddSnipButton(welcome, "取色（移动查看颜色，C 复制）", "pipette", () => SetSnipTool(SnipTool.Picker));
        AddSnipButton(welcome, "取消（Esc）", "x", Close, keepEnabledWhileWriting: true);
        _snipWelcome = SnipFloatingSurface(welcome); _snipCanvas.Children.Add(_snipWelcome);
        var colorRow = new StackPanel { Orientation = Orientation.Horizontal };
        _snipColorPreview = new Border { Width = 18, Height = 18, CornerRadius = new CornerRadius(3), Margin = new Thickness(0, 0, 8, 0),
            VerticalAlignment = VerticalAlignment.Center,
            BorderBrush = new SolidColorBrush(Color.FromArgb(120, 128, 128, 128)), BorderThickness = new Thickness(1) };
        _snipColorLabel = new TextBlock { FontSize = 16, FontWeight = FontWeights.Medium, VerticalAlignment = VerticalAlignment.Center,
            FontFamily = new System.Windows.Media.FontFamily("Cascadia Mono, Consolas") };
        colorRow.Children.Add(_snipColorPreview); colorRow.Children.Add(_snipColorLabel);
        _snipColorHud = new Border { Child = colorRow, Opacity = 0.86, ClipToBounds = true,
            IsHitTestVisible = false, Visibility = Visibility.Collapsed };
        _snipCanvas.Children.Add(_snipColorHud);
    }

    private Button AddSnipButton(Panel panel, string text, string icon, Action action, bool keepEnabledWhileWriting = false)
    {
        var button = new Button { Style = (Style)Resources["SnipAction"], ToolTip = text };
        button.Content = SnipIcons.Create(icon, button);
        System.Windows.Automation.AutomationProperties.SetName(button, text);
        button.Click += (_, _) => action(); panel.Children.Add(button);
        if (!keepEnabledWhileWriting) _snipWriteActions.Add(button);
        return button;
    }

    private static Border SnipDivider(double height = 22) => new() { Width = 1, Height = height, Background = new SolidColorBrush(Color.FromRgb(228, 233, 240)), Margin = new Thickness(6, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center };

    private static Border SnipFloatingSurface(UIElement content) => new() { Background = System.Windows.Media.Brushes.White,
        BorderBrush = new SolidColorBrush(Color.FromRgb(220, 226, 234)), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(12),
        Padding = new Thickness(6), Child = content, SnapsToDevicePixels = true };

    private void ApplySnipColor(Color color)
    {
        _snipColor = color;
        foreach (var (candidate, button) in _snipColors) button.IsChecked = candidate == color;
    }
}
