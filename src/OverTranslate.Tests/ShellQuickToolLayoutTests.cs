using System.Globalization;
using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Xml.Linq;
using Xunit;

namespace OverTranslate.Tests;

public class ShellQuickToolLayoutTests
{
    private static readonly XNamespace P = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
    private static readonly XNamespace X = "http://schemas.microsoft.com/winfx/2006/xaml";

    [Theory]
    [InlineData("ScreenshotBtn", "ScreenshotHotkeyText", "Ctrl+Alt+Q")]
    [InlineData("PinImageBtn", "PinImageHotkeyText", "Ctrl+Alt+I")]
    [InlineData("OcrExtractBtn", "OcrHotkeyText", "Ctrl+Alt+O")]
    public void Quick_tool_text_fits_the_real_rail_at_minimum_window_size(
        string buttonName, string hotkeyName, string defaultHotkey) => OnSta(() =>
    {
        foreach (var theme in new[] { "Light", "Dark" })
        {
            var root = LoadRail(theme);
            Layout(root);
            var button = (Button)root.FindName(buttonName);
            var hotkey = (TextBlock)root.FindName(hotkeyName);
            var label = Descendants<TextBlock>(button).Single(t => t != hotkey);
            var originalLabel = label.Text;
            // 设置页直接使用 WPF Key 名称；这个较长组合也可正常录入。
            var longHotkey = Services.GlobalHotkey.ModifiersToString(7) + "+" + System.Windows.Input.Key.OemOpenBrackets;
            foreach (var text in new[] { defaultHotkey, longHotkey, "" })
            foreach (var name in new[] { originalLabel, "这是用于验证名称过长时仍能完整显示快捷键的测试名称" })
            {
                label.Text = name;
                hotkey.Text = text;
                Layout(root);
                var frame = (Border)button.Template.FindName("Root", button);
                AssertInside(root, button);
                AssertInside(frame, label);
                Assert.Equal(TextTrimming.CharacterEllipsis, label.TextTrimming);
                if (text.Length == 0)
                {
                    Assert.Equal(Visibility.Collapsed, hotkey.Visibility);
                    continue;
                }

                Assert.Equal(Visibility.Visible, hotkey.Visibility);
                Assert.Equal(TextTrimming.CharacterEllipsis, hotkey.TextTrimming);
                Assert.Equal(text, hotkey.ToolTip);
                AssertInside(frame, hotkey);
                var labelBounds = label.TransformToAncestor(frame).TransformBounds(new Rect(label.RenderSize));
                var keyBounds = hotkey.TransformToAncestor(frame).TransformBounds(new Rect(hotkey.RenderSize));
                Assert.True(label.ActualWidth >= 45, "The action name must retain readable space.");
                Assert.True(labelBounds.Right <= keyBounds.Left + 0.5, "The name and shortcut must not overlap.");
                if (text == longHotkey && name == originalLabel) SaveEvidence(root, $"{buttonName}-{theme}");
            }
        }
    });

    private static Grid LoadRail(string theme)
    {
        _ = System.IO.Packaging.PackUriHelper.UriSchemePack;
        var document = XDocument.Load(Path.Combine(StringsParityTests.ProjectDirectory(), "Views", "Shell", "ShellWindow.xaml"));
        var window = document.Root!;
        var rail = document.Descendants().Single(e => (string?)e.Attribute(X + "Name") == "RailPanel");
        var border = new XElement(rail.Parent!);
        // 仅加载实际侧栏的控件和样式，不启动应用、注册快捷键或读取用户设置。
        foreach (var attribute in border.DescendantsAndSelf().Attributes()
                     .Where(a => a.Name.LocalName is "Click" or "Checked").ToList()) attribute.Remove();
        var column = document.Descendants().Single(e => (string?)e.Attribute(X + "Name") == "SidebarColumn");
        var titleHeight = document.Descendants(P + "RowDefinition").First().Attribute("Height")!.Value;
        var resources = new XElement(P + "ResourceDictionary",
            new XElement(P + "ResourceDictionary.MergedDictionaries",
                new[] { $"Themes/{theme}Theme.xaml", "Themes/SharedStyles.xaml", "Resources/Strings.zh-Hans.xaml", "Themes/ProductStyles.xaml" }
                    .Select(path => new XElement(P + "ResourceDictionary", new XAttribute("Source", $"pack://application:,,,/ScreenTranslate;component/{path}")))),
            window.Element(P + "Window.Resources")!.Elements().Select(e => new XElement(e)));
        var markup = new XElement(P + "Grid", new XAttribute(XNamespace.Xmlns + "x", X.NamespaceName),
            new XAttribute("Width", window.Attribute("MinWidth")!.Value),
            new XAttribute("Height", double.Parse(window.Attribute("MinHeight")!.Value, CultureInfo.InvariantCulture)
                - double.Parse(titleHeight, CultureInfo.InvariantCulture)),
            new XAttribute("Background", window.Attribute("Background")!.Value),
            new[] { "Foreground", "FontFamily", "FontSize" }
                .Select(name => new XAttribute("TextElement." + name, window.Attribute(name)!.Value)),
            new XElement(P + "Grid.Resources", resources),
            new XElement(P + "Grid.ColumnDefinitions", new XElement(column), new XElement(P + "ColumnDefinition")), border);
        return (Grid)XamlReader.Parse(markup.ToString());
    }

    private static void Layout(FrameworkElement root)
    {
        root.Measure(new Size(root.Width, root.Height));
        root.Arrange(new Rect(0, 0, root.Width, root.Height));
        root.UpdateLayout();
    }

    private static void AssertInside(FrameworkElement parent, FrameworkElement child)
    {
        var bounds = child.TransformToAncestor(parent).TransformBounds(new Rect(child.RenderSize));
        Assert.True(bounds.Left >= -0.5 && bounds.Top >= -0.5 && bounds.Right <= parent.ActualWidth + 0.5
            && bounds.Bottom <= parent.ActualHeight + 0.5,
            $"{child.Name} bounds {bounds} must fit inside {parent.GetType().Name} {parent.RenderSize}.");
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T found) yield return found;
            foreach (var descendant in Descendants<T>(child)) yield return descendant;
        }
    }

    private static void SaveEvidence(FrameworkElement root, string name)
    {
        var directory = Environment.GetEnvironmentVariable("QUICK_TOOL_EVIDENCE_DIR");
        if (string.IsNullOrEmpty(directory)) return;
        Directory.CreateDirectory(directory);
        var bitmap = new RenderTargetBitmap((int)root.Width, (int)root.Height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(root);
        var png = new PngBitmapEncoder();
        png.Frames.Add(BitmapFrame.Create(bitmap));
        using var output = File.Create(Path.Combine(directory, name + ".png"));
        png.Save(output);
    }

    private static void OnSta(Action action)
    {
        Exception? error = null;
        var thread = new Thread(() => { try { action(); } catch (Exception ex) { error = ex; } }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(20)), "Quick tool layout test timed out.");
        if (error is not null) ExceptionDispatchInfo.Capture(error).Throw();
    }
}
